using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KeyboundTyper.Services;

/// <summary>Searches every place the game could store an on-screen word and writes a report.</summary>
public static class DiagnosticService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nint dwLength);

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_WRITECOPY = 0x08;

    public static async Task<string> RunAsync(string word, KeyboundScanner scanner)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Diagnóstico para '{word}' ({DateTime.Now:HH:mm:ss}) ===");

        // 1) Parts that must run on the game thread
        var uiPart = await Plugin.Framework.RunOnFrameworkThread(() => UiSearch(word, scanner));
        sb.Append(uiPart);

        // 2) Raw memory scan (background)
        var mem = await Task.Run(() => MemorySearch(word));
        sb.Append(mem);

        var report = sb.ToString();
        try
        {
            var path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "diagnostico.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            report += $"\nRelatório salvo em: {path}\n";
        }
        catch (Exception ex)
        {
            report += $"\n(Falha ao salvar arquivo: {ex.Message})\n";
        }

        Plugin.Log.Information(report);
        return report;
    }

    private static unsafe string UiSearch(string word, KeyboundScanner scanner)
    {
        var sb = new StringBuilder();

        // Nodes
        var nodes = scanner.FindText(word);
        sb.AppendLine($"[Nós de texto] {nodes.Count} ocorrência(s)");
        foreach (var n in nodes.Take(10)) sb.AppendLine($"   addon={n.AddonName} node={n.NodeId} '{n.Text}'");

        var stage = AtkStage.Instance();
        if (stage == null) { sb.AppendLine("AtkStage nulo."); return sb.ToString(); }

        // KTG-ish addons overview + AtkValues
        sb.AppendLine("[Addons KTG/TurnBreak carregados]");
        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var valueHits = 0;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null) continue;
            var name = unit->NameString;
            var interesting = name.StartsWith("KTG", StringComparison.OrdinalIgnoreCase) ||
                              name.StartsWith("TurnBreak", StringComparison.OrdinalIgnoreCase);
            if (interesting)
                sb.AppendLine($"   {name} visible={unit->IsVisible} nodes={unit->UldManager.NodeListCount} values={unit->AtkValuesCount} addr=0x{(nint)unit:X}");

            // search AtkValues of ALL addons for the word
            for (var v = 0; v < unit->AtkValuesCount; v++)
            {
                try
                {
                    var val = &unit->AtkValues[v];
                    var s = val->GetValueAsString();
                    if (!string.IsNullOrEmpty(s) && s.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        valueHits++;
                        sb.AppendLine($"   [AtkValue] addon={name} index={v} type={val->Type} '{s}'");
                    }
                }
                catch { }
            }
        }
        sb.AppendLine($"[AtkValues] {valueHits} ocorrência(s)");

        // StringArrayData
        var holder = stage->AtkArrayDataHolder;
        var arrHits = 0;
        if (holder != null)
        {
            for (var a = 0; a < holder->StringArrayCount; a++)
            {
                var arr = holder->StringArrays[a];
                if (arr == null) continue;
                for (var j = 0; j < arr->AtkArrayData.Size; j++)
                {
                    var p = arr->StringArray[j];
                    if (!p.HasValue) continue;
                    string s;
                    try { s = p.ToString() ?? ""; } catch { continue; }
                    if (s.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        arrHits++;
                        sb.AppendLine($"   [StringArray] array={a} index={j} '{s}'");
                    }
                }
            }
        }
        sb.AppendLine($"[StringArrayData] {arrHits} ocorrência(s)");
        return sb.ToString();
    }

    private static unsafe string MemorySearch(string word)
    {
        var sb = new StringBuilder();
        var utf8 = Encoding.UTF8.GetBytes(word);
        var utf16 = Encoding.Unicode.GetBytes(word);

        var modules = new List<(nint Base, long Size, string Name)>();
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                modules.Add((m.BaseAddress, m.ModuleMemorySize, m.ModuleName));
        }
        catch { }

        string Where(nint addr)
        {
            foreach (var m in modules)
                if (addr >= m.Base && addr < m.Base + (nint)m.Size) return $"{m.Name}+0x{addr - m.Base:X}";
            return "heap";
        }

        var hits8 = new List<string>();
        var hits16 = new List<string>();
        var total8 = 0;
        var total16 = 0;

        nint address = 0;
        var mbiSize = (nint)sizeof(MEMORY_BASIC_INFORMATION);
        while (VirtualQuery(address, out var mbi, mbiSize) != 0)
        {
            var next = mbi.BaseAddress + mbi.RegionSize;
            if (next <= address) break;
            address = next;

            if (mbi.State != MEM_COMMIT) continue;
            if ((mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) != 0) continue;
            if ((mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY)) == 0) continue;
            if (mbi.RegionSize > 0x40000000) continue;

            var span = new ReadOnlySpan<byte>((void*)mbi.BaseAddress, (int)mbi.RegionSize);
            Scan(span, utf8, mbi.BaseAddress, hits8, ref total8, Where, sb: null);
            Scan(span, utf16, mbi.BaseAddress, hits16, ref total16, Where, sb: null);
        }

        sb.AppendLine($"[Memória UTF-8]  {total8} ocorrência(s)");
        foreach (var h in hits8.Take(25)) sb.AppendLine("   " + h);
        sb.AppendLine($"[Memória UTF-16] {total16} ocorrência(s)");
        foreach (var h in hits16.Take(25)) sb.AppendLine("   " + h);
        return sb.ToString();
    }

    private static unsafe void Scan(ReadOnlySpan<byte> span, byte[] pattern, nint baseAddr, List<string> hits, ref int total, Func<nint, string> where, StringBuilder? sb)
    {
        var offset = 0;
        while (offset < span.Length)
        {
            var idx = span[offset..].IndexOf(pattern);
            if (idx < 0) break;
            var pos = offset + idx;
            total++;
            if (hits.Count < 60)
            {
                var addr = baseAddr + pos;
                var from = Math.Max(0, pos - 16);
                var before = Convert.ToHexString(span.Slice(from, pos - from));
                hits.Add($"0x{addr:X} [{where(addr)}] antes={before}");
            }
            offset = pos + pattern.Length;
        }
    }
}
