using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace KeyboundTyper.Services;

public enum AutoQueueState
{
    Disabled,
    WaitingOutside,
    TargetingNpc,
    WaitingRegisterAddon,
    WaitingDutyPop,
    EnteringDuty,
    InDuty,
    FinishingDuty,
    TransitioningBack
}

public sealed class AutoQueueService : IDisposable
{
    private readonly Configuration config;
    private readonly KeyboundScanner scanner;
    private readonly AutoTyperService autoTyper;

    private readonly Stopwatch stateTimer = new();
    private readonly Stopwatch throttleTimer = new();

    public AutoQueueState CurrentState { get; private set; } = AutoQueueState.Disabled;
    public string StatusText { get; private set; } = "Inativo";

    public AutoQueueService(Configuration config, KeyboundScanner scanner, AutoTyperService autoTyper)
    {
        this.config = config;
        this.scanner = scanner;
        this.autoTyper = autoTyper;
        stateTimer.Start();
        throttleTimer.Start();
    }

    public void StartLoop()
    {
        config.AutoLoopEnabled = true;
        if (!config.InfiniteLoops && config.RemainingLoops <= 0)
        {
            config.RemainingLoops = Math.Max(1, config.LoopCount);
        }
        config.Save();

        // Garante que o Auto-Typer principal também esteja ativo
        config.Enabled = true;
        config.Save();

        SetState(AutoQueueState.WaitingOutside, "Iniciando ciclo de auto-loop...");
        Plugin.ChatGui.Print("[Digitas Automatizados] Auto-Loop ATIVADO!");
    }

    public void StopLoop()
    {
        config.AutoLoopEnabled = false;
        config.Save();
        SetState(AutoQueueState.Disabled, "Auto-Loop desativado pelo usuário.");
        Plugin.ChatGui.Print("[Digitas Automatizados] Auto-Loop DESATIVADO.");
    }

    public void ToggleLoop()
    {
        if (config.AutoLoopEnabled)
            StopLoop();
        else
            StartLoop();
    }

    private void SetState(AutoQueueState newState, string status)
    {
        CurrentState = newState;
        StatusText = status;
        stateTimer.Restart();
    }

    public unsafe void Update()
    {
        if (!config.AutoLoopEnabled)
        {
            if (CurrentState != AutoQueueState.Disabled)
            {
                SetState(AutoQueueState.Disabled, "Inativo");
            }
            return;
        }

        // Throttle updates to avoid spamming every frame (~100ms)
        if (throttleTimer.ElapsedMilliseconds < 100) return;
        throttleTimer.Restart();

        // Condições globais
        var isBetweenAreas = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        var isBoundByDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
        var isInKeybound = scanner.IsInKeyboundDuty;

        // Se estiver em transição de tela / tela preta de loading
        if (isBetweenAreas)
        {
            if (CurrentState != AutoQueueState.TransitioningBack)
            {
                SetState(AutoQueueState.TransitioningBack, "Carregando área (Loading)...");
            }
            return;
        }

        switch (CurrentState)
        {
            case AutoQueueState.WaitingOutside:
                // Se já estiver na duty por algum motivo
                if (isBoundByDuty || isInKeybound)
                {
                    SetState(AutoQueueState.InDuty, "Na arena Keybound Brawler.");
                    return;
                }

                // Aguarda 2.5s após loading para o personagem e NPCs carregarem
                if (stateTimer.ElapsedMilliseconds < 2500)
                {
                    StatusText = $"Estabilizando no Gold Saucer ({2500 - stateTimer.ElapsedMilliseconds}ms)...";
                    return;
                }

                SetState(AutoQueueState.TargetingNpc, "Procurando NPC Keybound Registrar...");
                break;

            case AutoQueueState.TargetingNpc:
                if (isBoundByDuty || isInKeybound)
                {
                    SetState(AutoQueueState.InDuty, "Na arena Keybound Brawler.");
                    return;
                }

                // Encontra o NPC Keybound Registrar
                var registrarNpc = FindKeyboundRegistrar();
                if (registrarNpc == null)
                {
                    StatusText = "Keybound Registrar não encontrado por perto. Aproxime-se do NPC.";
                    return;
                }

                // Foca o NPC e interage
                Plugin.TargetManager.Target = registrarNpc;
                AddonHelper.InteractWithGameObject(registrarNpc.Address);
                SetState(AutoQueueState.WaitingRegisterAddon, "Interagindo com Keybound Registrar...");
                break;

            case AutoQueueState.WaitingRegisterAddon:
                // Se o popup de entrada rápida já abriu direto (ContentsFinderConfirm)
                if (TryHandleContentsFinderConfirm())
                {
                    SetState(AutoQueueState.EnteringDuty, "Confirmando entrada na partida (Commence)...");
                    return;
                }

                // Tenta achar a janela de registro ("Keybound Brawler" com botão "Register")
                var regAddon = FindRegisterAddon();
                if (regAddon != null)
                {
                    // Dispara o callback para o botão Register
                    AddonHelper.FireCallback(regAddon, 0);
                    SetState(AutoQueueState.WaitingDutyPop, "Registro enviado! Aguardando fila...");
                    return;
                }

                // Se demorar mais de 6 segundos sem resposta, tenta interagir com o NPC de novo
                if (stateTimer.ElapsedMilliseconds > 6000)
                {
                    SetState(AutoQueueState.TargetingNpc, "Tempo esgotado. Tentando interagir novamente...");
                }
                break;

            case AutoQueueState.WaitingDutyPop:
                // Aguarda o popup Duty Ready (ContentsFinderConfirm)
                if (TryHandleContentsFinderConfirm())
                {
                    SetState(AutoQueueState.EnteringDuty, "Partida encontrada! Clicando em Commence...");
                    return;
                }

                // Se já entrou direto na duty
                if (isBoundByDuty || isInKeybound)
                {
                    SetState(AutoQueueState.InDuty, "Entrou na partida!");
                    return;
                }

                // Se passar muito tempo (ex: 20s) e não tiver na fila nem aparecer confirmação, reavalia
                if (stateTimer.ElapsedMilliseconds > 20000 && !Plugin.Condition[ConditionFlag.WaitingForDutyFinder])
                {
                    SetState(AutoQueueState.WaitingOutside, "Reiniciando busca de fila...");
                }
                break;

            case AutoQueueState.EnteringDuty:
                if (isBoundByDuty || isInKeybound)
                {
                    SetState(AutoQueueState.InDuty, "Na partida do Keybound Brawler!");
                }
                break;

            case AutoQueueState.InDuty:
                if (!isBoundByDuty && !isInKeybound)
                {
                    // O jogador já saiu da partida
                    OnDutyEnded();
                    return;
                }

                StatusText = "Jogando Keybound Brawler (Auto-Typer Ativo)...";

                // Tenta sair mais rápido se o diálogo de Exit / Leave já estiver disponível
                TryHandleExitDialog();
                break;

            case AutoQueueState.FinishingDuty:
                if (!isBoundByDuty && !isInKeybound)
                {
                    OnDutyEnded();
                }
                else
                {
                    TryHandleExitDialog();
                }
                break;

            case AutoQueueState.TransitioningBack:
                // Aguarda a transição de loading terminar
                if (!isBetweenAreas && !isBoundByDuty && !isInKeybound)
                {
                    OnBackToLobby();
                }
                break;
        }
    }

    private void OnDutyEnded()
    {
        SetState(AutoQueueState.TransitioningBack, "Partida concluída. Retornando ao Gold Saucer...");
    }

    private void OnBackToLobby()
    {
        config.CompletedLoops++;

        if (!config.InfiniteLoops)
        {
            config.RemainingLoops--;
            if (config.RemainingLoops <= 0)
            {
                config.AutoLoopEnabled = false;
                config.Save();
                SetState(AutoQueueState.Disabled, "Todos os loops foram concluídos!");
                Plugin.ChatGui.Print($"[Digitas Automatizados] Concluído! Total de partidas nesta sessão: {config.CompletedLoops}.");
                return;
            }
        }

        config.Save();
        var remainingStr = config.InfiniteLoops ? "Infinito" : $"{config.RemainingLoops} restantes";
        SetState(AutoQueueState.WaitingOutside, $"Partida finalizada ({config.CompletedLoops} concluídas). Próxima rodada ({remainingStr})...");
    }

    private unsafe bool TryHandleContentsFinderConfirm()
    {
        if (!config.AutoAcceptQueue) return false;

        var ptr = Plugin.GameGui.GetAddonByName("ContentsFinderConfirm");
        var addon = (AtkUnitBase*)ptr.Address;
        if (addon != null && addon->IsVisible)
        {
            AddonHelper.FireCallback(addon, 0);
            return true;
        }
        return false;
    }

    private unsafe bool TryHandleExitDialog()
    {
        if (!config.AutoExitDuty) return false;

        // Se abrir a confirmação padrão de saída "SelectYesno"
        var selectYesnoPtr = Plugin.GameGui.GetAddonByName("SelectYesno");
        var selectYesno = (AtkUnitBase*)selectYesnoPtr.Address;
        if (selectYesno != null && selectYesno->IsVisible)
        {
            AddonHelper.FireCallback(selectYesno, 0); // 0 = Yes / Confirm
            return true;
        }

        // Procura outros diálogos com botão Exit
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return false;

        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible) continue;
            var name = unit->NameString;

            if (name.Contains("Result", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TurnBreak", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Exit", StringComparison.OrdinalIgnoreCase))
            {
                // Dispara confirmação para fechar janela de resultados / sair
                AddonHelper.FireCallback(unit, 0);
            }
        }

        return false;
    }

    private unsafe AtkUnitBase* FindRegisterAddon()
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return null;

        ref var list = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit == null || !unit->IsVisible) continue;
            var name = unit->NameString;

            // Addon do minigame ou com botão Register
            if (name.Contains("Keybound", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("GoldSaucer", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("TurnBreak", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ContentsFinder", StringComparison.OrdinalIgnoreCase))
            {
                if (!name.Equals("ContentsFinderConfirm", StringComparison.OrdinalIgnoreCase))
                {
                    return unit;
                }
            }
        }

        return null;
    }

    private IGameObject? FindKeyboundRegistrar()
    {
        var player = Plugin.ObjectTable.Length > 0 ? Plugin.ObjectTable[0] : null;
        if (player == null) return null;

        IGameObject? bestMatch = null;
        var minDistance = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null) continue;
            var name = obj.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (name.Equals("Keybound Registrar", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Keybound Registrar", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Registrar", StringComparison.OrdinalIgnoreCase))
            {
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist < minDistance && dist < 18.0f) // Dentro de 18 yalms
                {
                    minDistance = dist;
                    bestMatch = obj;
                }
            }
        }

        return bestMatch;
    }

    public void Dispose()
    {
        StopLoop();
    }
}
