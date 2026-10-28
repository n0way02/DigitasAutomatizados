using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;

namespace KeyboundTyper.Services;

public sealed class AutoTyperService : IDisposable
{
    private const string UseWordActionSignature = "48 89 5C 24 18 56 57 41 56 48 83 EC 40 48 8B 1D ?? ?? ?? ?? 4C 8B F2 48 8B F1";
    private const uint CursedGazeActionId = 49529;
    private const uint SensoryJammerActionId = 49532;
    private const uint SensoryJammerAltActionId = 49702;
    private const int ForbiddenCastWindowMs = 500;

    private static nint useWordActionAddress;
    private static bool scanAttempted;

    private readonly KeyboundScanner scanner;
    public KeyboundScanner Scanner => scanner;
    private readonly Configuration config;

    private CancellationTokenSource? cts;
    private bool isTyping = false;
    private string lastTypedWord = string.Empty;
    private DateTime lastTypedTime = DateTime.MinValue;
    private DateTime lastKeepAliveTime = DateTime.MinValue;
    private bool wasGameplayUiVisible = false;
    private DateTime waveStartTime = DateTime.MinValue;

    // Saucy state tracking
    private uint pendingEntityId;
    private string pendingWord = string.Empty;
    private string pendingAlternate = string.Empty;
    private long pendingReadyAt;
    private long forbiddenCastBlockUntil;
    private long lastInactivityResetAt;

    public string CurrentStatus { get; private set; } = "Inativo";
    public string? LastDetectedWord { get; private set; }
    public string? ActiveAddonName { get; private set; }
    public bool IsCurrentlyLetterlocked { get; private set; }
    public bool IsCurrentlyGazeHolding { get; private set; }
    public bool IsCurrentlyForbiddenCast => Environment.TickCount64 < forbiddenCastBlockUntil;
    public long ForbiddenCastRemainingMs => Math.Max(0, forbiddenCastBlockUntil - Environment.TickCount64);
    public string? CurrentGazeSpell { get; private set; }
    public string? ActiveForbiddenCastName { get; private set; }
    public List<string> CurrentCastNames { get; private set; } = [];
    public int TotalWordsTyped { get; private set; } = 0;
    public List<DetectedTextNode> LastScannedNodes { get; private set; } = [];
    public List<string> History { get; } = [];

    public static bool ScanFailed => scanAttempted && useWordActionAddress == 0;

    private static string Sanitize(string word)
    {
        var chars = word.Where(c => !char.IsControl(c)).ToArray();
        return new string(chars).Trim();
    }

    public AutoTyperService(KeyboundScanner scanner, Configuration config)
    {
        this.scanner = scanner;
        this.config = config;
    }

    public static unsafe nint GetDirector()
    {
        var framework = EventFramework.Instance();
        var director = framework == null ? null : framework->GetInstanceContentDirector();
        return director != null && *((byte*)director + 0xDCE) == 23 ? (nint)director : 0;
    }

    public void Update()
    {
        if (!config.Enabled)
        {
            CurrentStatus = "Desativado";
            return;
        }

        // Anti-AFK: Periodic key pulse and native inactivity reset
        var now = Environment.TickCount64;
        if (config.AntiAfk && (DateTime.UtcNow - lastKeepAliveTime).TotalSeconds >= Math.Max(5, config.AntiAfkIntervalSec))
        {
            lastKeepAliveTime = DateTime.UtcNow;
            KeyboardSimulator.SendKeepAlive();
            ResetNativeInactivityTimer(now);
        }

        if (config.UseSaucyEngine)
        {
            TickSaucyGameplay();
        }
        else
        {
            TickSimulatedGameplay();
        }
    }

    private unsafe void ResetNativeInactivityTimer(long now)
    {
        try
        {
            if (lastInactivityResetAt == 0 || now - lastInactivityResetAt >= 20000)
            {
                lastInactivityResetAt = now;
                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var hud = agentModule->GetAgentByInternalId((AgentId)397);
                    var input = agentModule->GetAgentByInternalId((AgentId)398);
                    if (input != null && hud != null && *(nint*)((byte*)input + 0x28) == (nint)hud)
                    {
                        *(ulong*)((byte*)input + 0x50) = 0;
                    }
                }
            }
        }
        catch { }
    }

    private unsafe void TickSaucyGameplay()
    {
        var director = GetDirector();
        if (director == 0 || *((byte*)director + 0x1FC0) == 0)
        {
            pendingEntityId = 0;
            forbiddenCastBlockUntil = 0;
            lastInactivityResetAt = 0;
            CurrentStatus = "Aguardando início do minigame na arena...";
            return;
        }

        if (!scanAttempted)
        {
            scanAttempted = true;
            if (Plugin.SigScanner.TryScanText(UseWordActionSignature, out var address))
            {
                useWordActionAddress = address;
                Plugin.Log.Information($"[AutoTyper] UseWordAction signature encontrada em 0x{address:X}");
            }
            else
            {
                Plugin.Log.Warning("[AutoTyper] UseWordAction signature não foi encontrada. Mudando para modo teclado.");
            }
        }

        if (useWordActionAddress == 0)
        {
            CurrentStatus = "Signature não encontrada - usando simulação de teclado";
            TickSimulatedGameplay();
            return;
        }

        var agentModule = AgentModule.Instance();
        var hud = agentModule == null ? null : agentModule->GetAgentByInternalId((AgentId)397);
        if (hud == null)
        {
            pendingEntityId = 0;
            CurrentStatus = "Aguardando HUD do jogo (Agent 397)...";
            return;
        }

        var now = Environment.TickCount64;
        ResetNativeInactivityTimer(now);

        var words = (StdMap<uint, KeyboundScanner.HudTarget>*)((byte*)hud + 0x28);
        if (*(nint*)words == 0 || words->Count == 0)
        {
            pendingEntityId = 0;
            CurrentStatus = "Nenhum alvo ou palavra na lista do jogo...";
            return;
        }

        var bestPriority = -1;
        var bestLength = int.MaxValue;
        var bestDistance = float.MaxValue;
        uint bestEntityId = 0;
        GameObject* bestObject = null;
        var bestWord = string.Empty;
        var bestAlternate = string.Empty;
        GameObject* pendingObject = null;
        var pendingPriority = -1;

        var player = Plugin.ObjectTable.Length > 0 ? Plugin.ObjectTable[0] : null;
        var objectManager = GameObjectManager.Instance();
        if (objectManager == null)
        {
            pendingEntityId = 0;
            return;
        }

        foreach (var entityId in words->Keys)
        {
            var gameObject = objectManager->Objects.GetObjectByEntityId(entityId);
            if (gameObject == null ||
                gameObject->ObjectKind != ObjectKind.BattleNpc ||
                !words->TryGetValuePointer(in entityId, out var entry))
            {
                continue;
            }

            // Forbidden Cast Check (Cursed Gaze / Sensory Jammer)
            var character = gameObject->GetAsCharacter();
            var cast = character == null ? null : character->GetCastInfo();
            if (cast != null && cast->IsCasting &&
                (cast->ActionId is CursedGazeActionId or SensoryJammerActionId or SensoryJammerAltActionId))
            {
                var remainingMs = (cast->TotalCastTime - cast->CurrentCastTime) * 1000f;
                if (remainingMs <= ForbiddenCastWindowMs)
                {
                    forbiddenCastBlockUntil = Math.Max(forbiddenCastBlockUntil, now + (long)Math.Max(remainingMs, 0) + ForbiddenCastWindowMs);
                    ActiveForbiddenCastName = cast->ActionId == CursedGazeActionId ? "Cursed Gaze (Olhar Maldito)" : "Sensory Jammer (Bloqueio Sensorial)";
                }
            }

            if (gameObject->IsDead() && entry->BubbleType != 2)
            {
                continue;
            }

            var word = entry->Word.ToString();
            var alternate = entry->AlternateWord.ToString();
            var length = word.Length == 0 ? alternate.Length : alternate.Length == 0 ? word.Length : Math.Min(word.Length, alternate.Length);
            if (length == 0 || entry->BubbleType == 3)
            {
                continue;
            }

            var priority = BubblePriority(entry);
            if (entityId == pendingEntityId && word == pendingWord && alternate == pendingAlternate)
            {
                pendingObject = gameObject;
                pendingPriority = priority;
            }

            var distance = player == null
                ? float.MaxValue
                : Vector3.DistanceSquared(player.Position, gameObject->Position);

            if (priority < bestPriority ||
                (priority == bestPriority &&
                (distance > bestDistance || (distance == bestDistance && length >= bestLength))))
            {
                continue;
            }

            bestPriority = priority;
            bestLength = length;
            bestDistance = distance;
            bestEntityId = entityId;
            bestObject = gameObject;
            bestWord = word;
            bestAlternate = alternate;
        }

        if (pendingObject != null && pendingPriority == bestPriority)
        {
            bestEntityId = pendingEntityId;
            bestObject = pendingObject;
            bestWord = pendingWord;
            bestAlternate = pendingAlternate;
        }

        if (bestObject == null)
        {
            pendingEntityId = 0;
            CurrentStatus = "Procurando próximo alvo...";
            return;
        }

        LastDetectedWord = bestWord;

        // If target changed, re-calculate human typing delay according to slider settings
        if (pendingEntityId != bestEntityId || pendingWord != bestWord || pendingAlternate != bestAlternate)
        {
            pendingEntityId = bestEntityId;
            pendingWord = bestWord;
            pendingAlternate = bestAlternate;
            var min = Math.Clamp(config.MinCharDelayMs, 20, 1000);
            var max = Math.Clamp(config.MaxCharDelayMs, min, 1000);
            var charDelay = Random.Shared.Next(min, max + 1);
            pendingReadyAt = now + bestLength * charDelay;
            CurrentStatus = $"Mirando '{bestWord}' ({bestLength} letras @ {charDelay}ms/letra)...";
            return;
        }

        // Check forbidden cast block
        if (config.RespectForbiddenCasts && now < forbiddenCastBlockUntil)
        {
            var rem = forbiddenCastBlockUntil - now;
            CurrentStatus = $"PAUSADO: Cast proibido em andamento ({ActiveForbiddenCastName})! Aguardando {rem}ms...";
            return;
        }

        // Check typing duration elapsed
        if (now < pendingReadyAt)
        {
            var rem = pendingReadyAt - now;
            CurrentStatus = $"Digitando (Motor Nativo): '{bestWord}' ({rem}ms restantes)...";
            return;
        }

        if (!words->TryGetValuePointer(in bestEntityId, out var currentEntry) ||
            (bestObject->IsDead() && currentEntry->BubbleType != 2))
        {
            pendingEntityId = 0;
            return;
        }

        var useWordAction = (delegate* unmanaged<nint, GameObject*, byte, byte>)useWordActionAddress;
        if (useWordAction(director, bestObject, 1) != 0)
        {
            TotalWordsTyped++;
            History.Insert(0, $"{DateTime.Now:HH:mm:ss}  {bestWord} (100% Preciso)");
            if (History.Count > 15) History.RemoveAt(History.Count - 1);
            CurrentStatus = $"Palavra concluída com sucesso: {bestWord} (Total: {TotalWordsTyped})";
            pendingEntityId = 0;
        }
        else
        {
            pendingReadyAt = now + 250;
        }
    }

    private unsafe int BubblePriority(KeyboundScanner.HudTarget* entry)
    {
        if (config.PrioritizeBarrier && entry->BubbleType == 2)
            return 2; // Shield Barrier is top priority
        if (entry->BubbleType == 2 || entry->BubbleType == 4 || entry->CastProgress > 0)
            return 1;
        return 0;
    }

    private void TickSimulatedGameplay()
    {
        if (config.OnlyInKeyboundDuty && !scanner.IsInKeyboundDuty)
        {
            CurrentStatus = "Aguardando entrada na arena Keybound Brawler";
            return;
        }

        var now = Environment.TickCount64;

        // Check forbidden casts
        CheckForbiddenCastsSimulated(now);
        if (config.RespectForbiddenCasts && now < forbiddenCastBlockUntil)
        {
            if (isTyping) cts?.Cancel();
            CurrentStatus = $"PAUSADO: Cast proibido em andamento ({ActiveForbiddenCastName})! Bloqueado por mais {forbiddenCastBlockUntil - now}ms";
            return;
        }

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

    private unsafe void CheckForbiddenCastsSimulated(long now)
    {
        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj is not Dalamud.Game.ClientState.Objects.Types.IBattleNpc bnpc) continue;
                var ptr = (GameObject*)bnpc.Address;
                if (ptr == null || ptr->ObjectKind != ObjectKind.BattleNpc) continue;

                var character = ptr->GetAsCharacter();
                var cast = character == null ? null : character->GetCastInfo();
                if (cast != null && cast->IsCasting &&
                    (cast->ActionId is CursedGazeActionId or SensoryJammerActionId or SensoryJammerAltActionId))
                {
                    var remainingMs = (cast->TotalCastTime - cast->CurrentCastTime) * 1000f;
                    if (remainingMs <= ForbiddenCastWindowMs)
                    {
                        forbiddenCastBlockUntil = Math.Max(forbiddenCastBlockUntil, now + (long)Math.Max(remainingMs, 0) + ForbiddenCastWindowMs);
                        ActiveForbiddenCastName = cast->ActionId == CursedGazeActionId ? "Cursed Gaze (Olhar Maldito)" : "Sensory Jammer (Bloqueio Sensorial)";
                    }
                }
            }
        }
        catch { }
    }

    private async Task TypeWordAsync(string word)
    {
        isTyping = true;
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var token = cts.Token;

        try
        {
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
