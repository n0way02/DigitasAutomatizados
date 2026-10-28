using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace KeyboundTyper.Services;

public record DetectedTextNode(string AddonName, uint NodeId, string Text, bool IsVisible);

public sealed class KeyboundScanner
{
    // Addons whose name starts with one of these are auto-detected as part of the minigame.
    private static readonly string[] AutoPrefixes = ["KTG", "TurnBreak", "Keybound"];

    // Known non-word KTG UI texts (effects/banners).
    private static readonly HashSet<string> IgnoredPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "READY", "GO", "GO!", "START", "FINISH", "TIME UP", "CLEAR", "FAILED", "VICTORY",
        "WAVE", "WAVE 1", "WAVE 2", "WAVE 3", "FINAL WAVE",
        "SCORE", "BONUS", "CHAIN", "COMBO", "MGC", "PTS", "POINTS",
        "HP", "BASE HP", "BARRIER", "DEFENSE",
        "TYPING FEVER!", "TYPING FEVER", "FEVER", "PERFECT", "GREAT", "GOOD", "MISS",
        "AVALANCHE", "KEYBOUND BRAWLER",
        "OK", "CANCEL", "YES", "NO", "CLOSE", "EXIT"
    };

    public bool IsInKeyboundDuty
    {
        get
        {
            try
            {
                var territoryType = Plugin.ClientState.TerritoryType;
                if (territoryType == 0) return false;

                var row = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryType);
                if (row == null) return false;

                // TerritoryIntendedUse 65 is KeyboundBrawler
                return row.Value.TerritoryIntendedUse.RowId == 65;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool MatchesAuto(string name, string? custom)
    {
        foreach (var p in AutoPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;

        if (!string.IsNullOrWhiteSpace(custom))
            foreach (var part in custom.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (name.Equals(part, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static unsafe List<Pointer> LoadedUnits()
    {
        var result = new List<Pointer>();
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return result;

        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit != null) result.Add(new Pointer((nint)unit));
        }

        return result;
    }

    private readonly record struct Pointer(nint Address);

    public unsafe List<DetectedTextNode> ScanAllNodes(string? customAddon = null)
    {
        var result = new List<DetectedTextNode>();
        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            if (!unit->IsVisible) continue;
            var name = unit->NameString;
            if (!MatchesAuto(name, customAddon)) continue;
            if (!IsTargetWordAddon(name)) continue;

            ExtractAll(name, unit, result, includeHidden: false);
        }

        return result;
    }

    /// <summary>Discovery: every visible addon that has any text.</summary>
    public unsafe List<(string Name, List<string> Texts)> ListVisibleAddons()
    {
        var output = new List<(string, List<string>)>();
        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            if (!unit->IsVisible) continue;

            var nodes = new List<DetectedTextNode>();
            ExtractAll(unit->NameString, unit, nodes, includeHidden: false);
            if (nodes.Count == 0) continue;
            output.Add((unit->NameString, nodes.Select(n => n.Text).Distinct().Take(40).ToList()));
        }

        return output;
    }

    /// <summary>Discovery: all loaded addons matching a name filter, including hidden nodes.</summary>
    public unsafe List<(string Name, bool Visible, int NodeCount, List<string> Texts)> ListAllAddons(string filter)
    {
        var output = new List<(string, bool, int, List<string>)>();
        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            var name = unit->NameString;
            if (!string.IsNullOrWhiteSpace(filter) && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            var nodes = new List<DetectedTextNode>();
            ExtractAll(name, unit, nodes, includeHidden: true);
            output.Add((name, unit->IsVisible, unit->UldManager.NodeListCount, nodes.Select(n => n.Text).Distinct().Take(40).ToList()));
        }

        return output;
    }

    /// <summary>Finds a given on-screen word in ANY loaded addon (visible or not).</summary>
    public unsafe List<DetectedTextNode> FindText(string needle)
    {
        var output = new List<DetectedTextNode>();
        if (string.IsNullOrWhiteSpace(needle)) return output;

        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            var nodes = new List<DetectedTextNode>();
            ExtractAll(unit->NameString, unit, nodes, includeHidden: true);
            output.AddRange(nodes.Where(n => n.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return output;
    }

    /// <summary>Learn mode: names of addons that contain a word (used to auto-pick the right addon).</summary>
    public IEnumerable<string> FindAddonsContaining(string needle) =>
        FindText(needle).Select(n => n.AddonName).Distinct();

    /// <summary>True only while the typing HUD (KTGInput + KTGEWord) is on screen, i.e. during an actual wave.</summary>
    public unsafe bool IsGameplayUiVisible()
    {
        var input = false;
        var word = false;
        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            if (!unit->IsVisible) continue;
            var name = unit->NameString;
            if (name.Equals("KTGInput", StringComparison.OrdinalIgnoreCase)) input = true;
            else if (name.Equals("KTGEWord", StringComparison.OrdinalIgnoreCase)) word = true;
        }

        return input && word;
    }

    /// <summary>Reads all non-empty entries of a game StringArrayData (where KTGEWord/KTGInput get their text).</summary>
    public unsafe List<(int Index, string Text)> ReadStringArray(int arrayIndex)
    {
        var output = new List<(int, string)>();
        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkArrayDataHolder == null) return output;

        var holder = stage->AtkArrayDataHolder;
        if (arrayIndex < 0 || arrayIndex >= holder->StringArrayCount) return output;

        var arr = holder->StringArrays[arrayIndex];
        if (arr == null) return output;

        for (var j = 0; j < arr->AtkArrayData.Size; j++)
        {
            var p = arr->StringArray[j];
            if (!p.HasValue) continue;
            string s;
            try { s = p.ToString() ?? string.Empty; } catch { continue; }
            if (!string.IsNullOrWhiteSpace(s)) output.Add((j, s));
        }

        return output;
    }

    /// <summary>Live dump of AtkValues for every KTG* addon (to learn the data layout).</summary>
    public unsafe List<string> DumpKtgValues()
    {
        var lines = new List<string>();
        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            var name = unit->NameString;
            if (!name.StartsWith("KTG", StringComparison.OrdinalIgnoreCase)) continue;

            for (var v = 0; v < unit->AtkValuesCount; v++)
            {
                var val = &unit->AtkValues[v];
                string text;
                try { text = val->GetValueAsString(); } catch { text = "?"; }
                lines.Add($"{name}[{v}] {val->Type} = {text}");
            }
        }

        return lines;
    }

    public static bool IsTargetWordAddon(string name)
    {
        if (name.StartsWith("KTGEnemyCastBar", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("KTGInput", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("KTGIGaugeHp", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("KTGPlayerCastBar", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    // Known cast/spell names from Keybound Brawler that must NEVER be typed
    public static readonly HashSet<string> KnownCastNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tankbuster", "Big Bomber", "Hellfire", "Megiddo", "Shadow Flare", "Germinate",
        "Bad Breath", "Cursed Gaze", "Stop Press", "Letterlock", "Expansion", "Expansion 100%",
        "Explosion", "Self-Destruct", "??? Needles", "1000 Needles", "10,000 Needles", "100,000 Needles",
        "Needles", "Needle", "Bio", "Miasma", "Aero", "Aeroga", "Tornado", "Quake", "Fluid Strike",
        "Laser", "Pulse", "Thermal Cutter", "Discharger", "Diffractive Laser", "100 Tonze Swing",
        "Gigaflare", "Teraflare", "Megaflare", "Tail Drive", "Tail Screw", "Burst", "Burr Burrow",
    };

    /// <summary>Reads all active enemy cast bars to get cast names and detect dangerous Gaze attacks.</summary>
    public unsafe (HashSet<string> ActiveCastNames, bool IsGazeHolding, string? GazeSpellName) ReadEnemyCastState()
    {
        var castNames = new HashSet<string>(KnownCastNames, StringComparer.OrdinalIgnoreCase);
        var isGazeHolding = false;
        string? gazeSpell = null;

        foreach (var p in LoadedUnits())
        {
            var unit = (AtkUnitBase*)p.Address;
            if (!unit->IsVisible) continue;
            var name = unit->NameString;
            if (!name.StartsWith("KTGEnemyCastBar", StringComparison.OrdinalIgnoreCase)) continue;

            // Extract all text nodes inside this cast bar unit (including components)
            var castNodes = new List<DetectedTextNode>();
            ExtractAll(name, unit, castNodes, includeHidden: false);

            var interrupted = false;
            foreach (var node in castNodes)
            {
                var text = CleanWord(node.Text);
                if (text.Contains("Interrupted", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Interrompido", StringComparison.OrdinalIgnoreCase))
                {
                    interrupted = true;
                    break;
                }
            }

            foreach (var node in castNodes)
            {
                var text = CleanWord(node.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.Contains("Interrupted", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Interrompido", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Regex.IsMatch(text, @"^\d+$")) continue; // hit count

                castNames.Add(text);

                if (!interrupted && IsGazeSpell(text))
                {
                    var node6 = unit->GetNodeById(6);
                    var progress = node6 != null ? (node6->Width / 128.0f) : 0.9f;
                    if (progress >= 0.80f)
                    {
                        isGazeHolding = true;
                        gazeSpell = text;
                    }
                }
            }
        }

        return (castNames, isGazeHolding, gazeSpell);
    }

    public static bool IsCastName(string word, HashSet<string> castNames)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;
        if (IsBarrierPhrase(word)) return false; // Barrier phrases should never be filtered

        // Universal cast indicators and known cast keywords
        if (word.Contains("?") ||
            word.Contains("Needles", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Needle", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Gaze", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Breath", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Bomber", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Tankbuster", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Hellfire", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Megiddo", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Shadow Flare", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Explosion", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Destruct", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Laser", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Tornado", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("Tonze", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (castNames.Count > 0)
        {
            if (castNames.Contains(word)) return true;

            foreach (var cast in castNames)
            {
                if (word.Equals(cast, StringComparison.OrdinalIgnoreCase)) return true;
                // If the cast name is contained in the word
                if (cast.Length >= 4 && (word.Contains(cast, StringComparison.OrdinalIgnoreCase) || cast.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsGazeSpell(string spellName)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return false;
        return spellName.Contains("Gaze", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Olhar", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Regard", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Blick", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("視線", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Petrifaction", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Evil Eye", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Stop", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Burst", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("??? Needles", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Burr Burrow", StringComparison.OrdinalIgnoreCase)
            || spellName.Contains("Letterlock", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsBarrierPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // In Keybound Brawler, barrier phrases are long quotes or contain key terms:
        // "Listen to the Cries of the Planet", "Let's make it a night to remember", "A knight lives for..."
        if (text.Contains("Planet", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Barrier", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Shield", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Escudo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Barreira", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cries", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Night", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Remember", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Knight", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Barrier phrases are quotes longer than typical mob names (mobs are usually 3-12 letters)
        if (text.Length >= 26)
        {
            return true;
        }

        return false;
    }

    public static int GetTargetRank(string text, int candidateCount, bool prioritizeBarrier, HashSet<string> activeCastNames)
    {
        // Rank 2: Barrier (highest priority, protects the group from wipe)
        if (prioritizeBarrier && IsBarrierPhrase(text))
        {
            return 2;
        }

        // Rank -1: Boss (deprioritize boss if there are small mobs or barriers present)
        // Boss names: "Cave of the Gi", "Airbuster", "Urban Planning Division", "Sweeper", "Scorpion", or long strings >= 12 chars
        bool isBoss = text.Length >= 12 ||
                      text.Contains("Airbuster", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("Gi", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("Planning", StringComparison.OrdinalIgnoreCase);

        if (isBoss && candidateCount > 1)
        {
            return -1;
        }

        // Rank 0: Regular adds / small enemies
        return 0;
    }

    public (string? PrimaryWord, bool IsLetterlocked, bool IsGazeHolding, string? GazeSpellName, string? ActiveAddon, List<DetectedTextNode> AllNodes, List<string> ActiveCasts) GetCurrentTarget(string? customAddon = null)
    {
        var nodes = ScanAllNodes(customAddon);
        var (castNameSet, isGazeHolding, gazeSpell) = ReadEnemyCastState();
        var castList = castNameSet.ToList();

        // Primary source: the game's string array that feeds the KTG addons.
        var arrayIndex = Plugin.Configuration.WordStringArrayIndex;
        var arrayWords = new List<string>();
        foreach (var (idx, text) in ReadStringArray(arrayIndex))
        {
            var clean = CleanWord(text);
            nodes.Add(new DetectedTextNode($"StringArray#{arrayIndex}", (uint)idx, clean, true));
            if (IsValidWord(clean))
            {
                if (Plugin.Configuration.FilterCastNames && IsCastName(clean, castNameSet))
                {
                    Plugin.Log.Debug($"[KeyboundScanner] StringArray ignorando cast: '{clean}'");
                    continue;
                }
                arrayWords.Add(clean);
            }
        }

        if (arrayWords.Count > 0 && IsInKeyboundDuty)
        {
            var chosen = arrayWords
                .OrderByDescending(w => GetTargetRank(w, arrayWords.Count, Plugin.Configuration.PrioritizeBarrier, castNameSet))
                .ThenBy(w => Plugin.Configuration.PrioritizeShortWords ? w.Length : 0)
                .ThenBy(w => w)
                .First();
            return (chosen, false, isGazeHolding, gazeSpell, $"StringArray#{arrayIndex}", nodes, castList);
        }

        if (nodes.Count == 0)
        {
            return (null, false, isGazeHolding, gazeSpell, null, nodes, castList);
        }

        var isLetterlocked = false;
        string? activeAddon = null;

        foreach (var node in nodes)
        {
            activeAddon ??= node.AddonName;
            var text = node.Text.Trim();

            if (text.Contains("Letterlock", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Locked", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Bloqueado", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Trava", StringComparison.OrdinalIgnoreCase))
            {
                isLetterlocked = true;
            }
        }

        if (isLetterlocked)
        {
            return (null, true, isGazeHolding, gazeSpell, activeAddon, nodes, castList);
        }

        var candidates = new List<string>();
        foreach (var node in nodes)
        {
            // Ignore nodes from cast bars or input box
            if (!IsTargetWordAddon(node.AddonName)) continue;

            var clean = CleanWord(node.Text);
            if (IsValidWord(clean))
            {
                // Never type active enemy cast names
                if (Plugin.Configuration.FilterCastNames && IsCastName(clean, castNameSet))
                {
                    Plugin.Log.Debug($"[KeyboundScanner] Nodes ignorando cast: '{clean}'");
                    continue;
                }

                candidates.Add(clean);
            }
        }

        var primary = candidates
            .OrderByDescending(c => GetTargetRank(c, candidates.Count, Plugin.Configuration.PrioritizeBarrier, castNameSet))
            .ThenBy(c => Plugin.Configuration.PrioritizeShortWords ? c.Length : 0)
            .ThenBy(c => c)
            .FirstOrDefault();

        return (primary, false, isGazeHolding, gazeSpell, activeAddon, nodes, castList);
    }

    /// <summary>Collects text from both the flat node list and the full node tree (dynamic nodes are only in the tree).</summary>
    private static unsafe void ExtractAll(string addonName, AtkUnitBase* unit, List<DetectedTextNode> output, bool includeHidden)
    {
        var seen = new HashSet<(uint, string)>();
        var tmp = new List<DetectedTextNode>();

        ExtractFlat(addonName, &unit->UldManager, tmp, includeHidden, 0);
        WalkTree(addonName, unit->RootNode, tmp, includeHidden, 0);

        foreach (var n in tmp)
            if (seen.Add((n.NodeId, n.Text))) output.Add(n);
    }

    private static unsafe void ExtractFlat(string addonName, AtkUldManager* uld, List<DetectedTextNode> output, bool includeHidden, int depth)
    {
        if (uld == null || uld->NodeList == null || depth > 8) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || (!includeHidden && !node->IsVisible())) continue;
            HandleNode(addonName, node, output, includeHidden, depth);
        }
    }

    private static unsafe void WalkTree(string addonName, AtkResNode* first, List<DetectedTextNode> output, bool includeHidden, int depth)
    {
        if (first == null || depth > 14) return;

        var guard = 0;
        for (var node = first; node != null && guard < 2000; node = node->PrevSiblingNode, guard++)
        {
            if (!includeHidden && !node->IsVisible()) continue;

            HandleNode(addonName, node, output, includeHidden, depth);

            if (node->ChildNode != null)
                WalkTree(addonName, node->ChildNode, output, includeHidden, depth + 1);
        }
    }

    private static unsafe void HandleNode(string addonName, AtkResNode* node, List<DetectedTextNode> output, bool includeHidden, int depth)
    {
        if (node->Type == NodeType.Text)
        {
            var raw = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(raw))
                output.Add(new DetectedTextNode(addonName, node->NodeId, raw.Trim(), node->IsVisible()));
        }
        else if (node->Type >= NodeType.Component)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp != null)
            {
                ExtractFlat(addonName, &comp->UldManager, output, includeHidden, depth + 1);
                WalkTree(addonName, comp->UldManager.RootNode, output, includeHidden, depth + 1);
            }
        }
    }

    private static string CleanWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = Regex.Replace(text, "<.*?>", string.Empty);
        return cleaned.Trim();
    }

    private static bool IsValidWord(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length < 2 || text.Length > 40) return false;
        if (Regex.IsMatch(text, @"^[\d\s\/\%\:\,\.\+\-]+$")) return false;
        if (IgnoredPhrases.Contains(text)) return false;
        if (!text.Any(char.IsLetter)) return false;

        // Question marks are NEVER in valid typing words (they indicate hidden casts like '??? Needles' or system text)
        if (text.Contains("?")) return false;

        // Universal exclusion of known cast keywords
        if (text.Contains("Needles", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Needle", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Gaze", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Tankbuster", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Bad Breath", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Big Bomber", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Self-Destruct", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
