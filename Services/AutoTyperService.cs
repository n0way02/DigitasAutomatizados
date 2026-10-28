using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KeyboundTyper.Services;

public sealed class AutoTyperService : IDisposable
{
    private readonly KeyboundScanner scanner;
    public KeyboundScanner Scanner => scanner;
    private readonly Configuration config;

    private CancellationTokenSource? cts;
    private bool isTyping = false;
    private string lastTypedWord = string.Empty;
    private DateTime lastTypedTime = DateTime.MinValue;
    private DateTime lastActivityTime = DateTime.MinValue;
    private DateTime lastKeepAliveTime = DateTime.MinValue;
    private bool wasGameplayUiVisible = false;
    private DateTime waveStartTime = DateTime.MinValue;

    public string CurrentStatus { get; private set; } = "Inativo";
    public string? LastDetectedWord { get; private set; }
    public string? ActiveAddonName { get; private set; }
    public bool IsCurrentlyLetterlocked { get; private set; }
    public bool IsCurrentlyGazeHolding { get; private set; }
    public string? CurrentGazeSpell { get; private set; }
    public List<string> CurrentCastNames { get; private set; } = [];
    public int TotalWordsTyped { get; private set; } = 0;
    public List<DetectedTextNode> LastScannedNodes { get; private set; } = [];
    public List<string> History { get; } = [];

    private static string Sanitize(string word)
    {
        // Never send control characters (Esc = 0x1B quits the instance, Backspace deletes text).
        var chars = word.Where(c => !char.IsControl(c)).ToArray();
        return new string(chars).Trim();
    }

    public AutoTyperService(KeyboundScanner scanner, Configuration config)
    {
        this.scanner = scanner;
        this.config = config;
    }

    public void Update()
    {
        if (!config.Enabled)
        {
            CurrentStatus = "Desativado";
            return;
        }

        if (config.OnlyInKeyboundDuty && !scanner.IsInKeyboundDuty)
        {
            CurrentStatus = "Aguardando entrada na arena Keybound Brawler";
            return;
        }

        // Anti-AFK: Send safe harmless key (F15) periodically to prevent expulsion for inactivity
        if (config.AntiAfk && (DateTime.UtcNow - lastKeepAliveTime).TotalSeconds >= Math.Max(5, config.AntiAfkIntervalSec))
        {
            lastKeepAliveTime = DateTime.UtcNow;
            KeyboardSimulator.SendKeepAlive();

            // Native engine inactivity timer reset (Saucy: Agent 398 offset 0x50)
            try
            {
                unsafe
                {
                    var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
                    if (agentModule != null)
                    {
                        var hud = agentModule->GetAgentByInternalId((FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId)397);
                        var input = agentModule->GetAgentByInternalId((FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId)398);
                        if (input != null && hud != null && *(nint*)((byte*)input + 0x28) == (nint)hud)
                        {
                            *(ulong*)((byte*)input + 0x50) = 0;
                        }
                    }
                }
            }
            catch { }
        }

        // SAFETY: only type while the wave HUD is up. Enter/keys on result or exit screens can quit the instance.
        var uiVisible = scanner.IsGameplayUiVisible();
        if (!uiVisible)
        {
            wasGameplayUiVisible = false;
            if (isTyping) cts?.Cancel();
            CurrentStatus = "Aguardando wave (HUD KTGInput/KTGEWord não visível)";
            return;
        }

        if (!wasGameplayUiVisible)
        {
            wasGameplayUiVisible = true;
            waveStartTime = DateTime.UtcNow;
        }

        // Wait 350ms after the wave HUD appears so the game finishes opening and focusing the text box
        if ((DateTime.UtcNow - waveStartTime).TotalMilliseconds < 350)
        {
            CurrentStatus = "Aguardando foco do campo de texto da wave...";
            return;
        }

        var (primaryWord, isLetterlocked, isGazeHolding, gazeSpell, activeAddon, allNodes, activeCasts) = scanner.GetCurrentTarget(config.CustomAddonName);
        LastScannedNodes = allNodes;
        ActiveAddonName = activeAddon;
        IsCurrentlyLetterlocked = isLetterlocked;
        IsCurrentlyGazeHolding = isGazeHolding;
        CurrentGazeSpell = gazeSpell;
        CurrentCastNames = activeCasts;
        LastDetectedWord = primaryWord;

        if (isLetterlocked && config.RespectLetterlock)
        {
            if (isTyping) cts?.Cancel();
            CurrentStatus = "PAUSADO: Letterlock detectado! Aguardando liberação...";
            return;
        }

        if (isGazeHolding && config.RespectGaze)
        {
            if (isTyping) cts?.Cancel();
            CurrentStatus = $"PAUSADO: Ataque de Olhar / Gaze ({gazeSpell ?? "Cursed Gaze"}) quase cheio! Aguardando...";
            return;
        }

        if (string.IsNullOrWhiteSpace(primaryWord))
        {
            CurrentStatus = activeAddon != null ? $"Addon {activeAddon} ativo, procurando próximo monstro..." : "Aguardando início da wave / Addon";
            return;
        }

        // Avoid re-typing the exact same word too quickly (wait for monster to die or text to clear)
        if (primaryWord == lastTypedWord && (DateTime.UtcNow - lastTypedTime).TotalMilliseconds < Math.Max(50, config.SameWordRetryDelayMs))
        {
            CurrentStatus = $"Aguardando morte do alvo: {primaryWord}";
            return;
        }

        if (!isTyping)
        {
            var safe = Sanitize(primaryWord);
            if (string.IsNullOrEmpty(safe) || safe.Length > 40) return;
            _ = TypeWordAsync(safe);
        }
    }

    private async Task TypeWordAsync(string word)
    {
        isTyping = true;
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
            // Reaction delay before picking and typing (0ms = Instant reaction!)
            if (config.ReactionDelayMs > 0)
            {
                CurrentStatus = $"Mirando: {word}...";
                await Task.Delay(config.ReactionDelayMs, token);
            }

            CurrentStatus = $"Digitando: {word}";
            lastTypedWord = word;
            lastTypedTime = DateTime.UtcNow;
            History.Insert(0, $"{DateTime.Now:HH:mm:ss}  {word}");
            if (History.Count > 15) History.RemoveAt(History.Count - 1);
            Plugin.Log.Information($"[KeyboundTyper] digitando '{word}'");

            await KeyboardSimulator.TypeStringAsync(
                word,
                config.MinCharDelayMs,
                config.MaxCharDelayMs,
                config.AutoPressEnter,
                config.DelayBeforeEnterMs,
                config.DelayPostEnterMs,
                config.InitialDelayMs,
                config.InputMethod,
                token
            );

            TotalWordsTyped++;
            CurrentStatus = $"Palavra enviada: {word} (Total: {TotalWordsTyped})";

            if (config.AntiAfk)
            {
                KeyboardSimulator.SendKeepAlive();
            }

            if (config.DelayAfterWordMs > 0)
            {
                await Task.Delay(config.DelayAfterWordMs, token);
            }
        }
        catch (TaskCanceledException)
        {
            // Ignored
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Erro ao digitar palavra no Keybound Brawler.");
            CurrentStatus = $"Erro: {ex.Message}";
        }
        finally
        {
            isTyping = false;
        }
    }

    public async Task TestTypeAsync(string testText)
    {
        if (isTyping) return;
        isTyping = true;
        try
        {
            await KeyboardSimulator.TypeStringAsync(
                testText,
                config.MinCharDelayMs,
                config.MaxCharDelayMs,
                config.AutoPressEnter,
                config.DelayBeforeEnterMs,
                config.DelayPostEnterMs,
                config.InitialDelayMs,
                config.InputMethod,
                CancellationToken.None
            );
        }
        finally
        {
            isTyping = false;
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
