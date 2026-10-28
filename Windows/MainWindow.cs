using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using KeyboundTyper.Services;

namespace KeyboundTyper.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly AutoTyperService autoTyper;
    private readonly AutoQueueService autoQueue;
    private readonly Configuration config;

    public MainWindow(AutoTyperService autoTyper, AutoQueueService autoQueue, Configuration config)
        : base("Digitas Automatizados###DigitasAutomatizadosMain", ImGuiWindowFlags.None)
    {
        this.autoTyper = autoTyper;
        this.autoQueue = autoQueue;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 140),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Size = new Vector2(460, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private string T(string pt, string en) => config.Language == "en" ? en : pt;

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);

        try
        {
            DrawHeader();

            if (config.CompactMode)
            {
                DrawCompactView();
            }
            else
            {
                DrawFullView();
            }
        }
        finally
        {
            ImGui.PopStyleVar(4);
        }
    }

    private void DrawHeader()
    {
        // Top action bar
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), "⌨ Digitas Automatizados");
        ImGui.SameLine();
        ImGui.TextDisabled("v1.6.3");
        ImGui.EndGroup();

        ImGui.SameLine();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var donateBtnWidth = 115f;
        var langBtnWidth = 55f;
        var minBtnWidth = 72f;
        var spacing = 6f;
        var offset = availWidth - (donateBtnWidth + langBtnWidth + minBtnWidth + spacing * 2);
        if (offset > 0)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        }

        // Minimize / Compact Mode Toggle Button
        var isCompact = config.CompactMode;
        ImGui.PushStyleColor(ImGuiCol.Button, isCompact ? new Vector4(0.2f, 0.55f, 0.85f, 0.9f) : new Vector4(0.25f, 0.28f, 0.35f, 0.8f));
        if (ImGui.Button(isCompact ? T("🗖 Expandir", "🗖 Expand") : T("🗕 Mini HUD", "🗕 Mini HUD"), new Vector2(minBtnWidth, 24)))
        {
            config.CompactMode = !config.CompactMode;
            config.Save();
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(T("Alternar entre janela completa e Mini HUD compacto flutuante.", "Toggle between full window and floating compact Mini HUD."));
        }

        ImGui.SameLine();

        // Language Switch Button
        var isEn = config.Language == "en";
        if (ImGui.Button(isEn ? "🇧🇷 PT" : "🇺🇸 EN", new Vector2(langBtnWidth, 24)))
        {
            config.Language = isEn ? "pt" : "en";
            config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(T("Trocar idioma", "Switch language"));
        }

        ImGui.SameLine();

        // Ko-Fi Donation Button
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1.0f, 0.35f, 0.38f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.48f, 0.5f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.25f, 0.28f, 1.0f));
        if (ImGui.Button(T("☕ Ko-fi", "☕ Ko-fi"), new Vector2(donateBtnWidth, 24)))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/n0way02") { UseShellExecute = true });
            }
            catch { }
        }
        ImGui.PopStyleColor(3);

        ImGui.Spacing();
    }

    private void DrawCompactView()
    {
        // Minimalist Mini HUD widget
        ImGui.BeginChild("###CompactCard", new Vector2(-1, -1), true);

        // Status text
        if (config.Enabled)
        {
            if (autoTyper.IsCurrentlyForbiddenCast)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.45f, 0.1f, 1.0f), $"● [PAUSADO] CAST PROIBIDO ({autoTyper.ActiveForbiddenCastName ?? "Gaze"})");
            }
            else if (autoTyper.IsCurrentlyLetterlocked)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.35f, 1.0f), "● [PAUSADO] LETTERLOCK");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.45f, 1.0f), "● [ATIVO - SAUCY]");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.65f, 0.65f, 0.65f, 1.0f), "○ [DESATIVADO]");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"| {autoTyper.CurrentStatus}");

        ImGui.Spacing();
        if (!string.IsNullOrWhiteSpace(autoTyper.LastDetectedWord))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.3f, 1.0f), $"{T("Alvo", "Target")}: {autoTyper.LastDetectedWord}");
            ImGui.SameLine();
        }
        ImGui.TextDisabled($"{T("Digitadas", "Typed")}: {autoTyper.TotalWordsTyped} | {T("Partidas", "Runs")}: {config.CompletedLoops}");

        ImGui.Spacing();
        var btnWidth = (ImGui.GetContentRegionAvail().X - 6f) / 2f;

        // Toggle Typer Button
        if (config.Enabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 0.9f));
            if (ImGui.Button(T("⏹ Desligar", "⏹ Stop"), new Vector2(btnWidth, 28)))
            {
                config.Enabled = false;
                config.Save();
            }
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.65f, 0.35f, 0.9f));
            if (ImGui.Button(T("▶ Ligar", "▶ Start"), new Vector2(btnWidth, 28)))
            {
                config.Enabled = true;
                config.Save();
            }
            ImGui.PopStyleColor();
        }

        ImGui.SameLine();

        // Toggle Auto-Loop Button
        if (config.AutoLoopEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 0.9f));
            if (ImGui.Button(T("⏹ Parar Farm", "⏹ Stop Farm"), new Vector2(btnWidth, 28)))
            {
                autoQueue.StopLoop();
            }
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.85f, 0.9f));
            if (ImGui.Button(T("🔄 Auto-Loop", "🔄 Auto-Loop"), new Vector2(btnWidth, 28)))
            {
                autoQueue.StartLoop();
            }
            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }

    private void DrawFullView()
    {
        // 1. Status Banner Card
        DrawStatusBanner();

        ImGui.Spacing();

        // 2. Main Control Buttons (Power & Auto-Loop Farm)
        DrawMainActionButtons();

        ImGui.Spacing();

        // 3. Target Word Display Card
        DrawTargetWordCard();

        ImGui.Spacing();

        // 4. Speed Sliders & Presets
        DrawSpeedSection();

        ImGui.Spacing();

        // 5. Essential Options
        DrawOptionsSection();

        ImGui.Spacing();

        // 6. History
        if (ImGui.CollapsingHeader(T("📜 Últimas Palavras Digitadas", "📜 Typed Words History")))
        {
            ImGui.BeginChild("###HistoryBox", new Vector2(-1, 95), true);
            if (autoTyper.History.Count == 0)
            {
                ImGui.TextDisabled(T("Nenhuma palavra digitada ainda nesta sessão...", "No words typed yet this session..."));
            }
            else
            {
                foreach (var item in autoTyper.History)
                {
                    ImGui.BulletText(item);
                }
            }
            ImGui.EndChild();
        }
    }

    private void DrawStatusBanner()
    {
        var height = config.AutoLoopEnabled ? 56f : 44f;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.12f, 0.16f, 0.9f));
        ImGui.BeginChild("###StatusBanner", new Vector2(-1, height), true);

        if (config.Enabled)
        {
            if (autoTyper.IsCurrentlyForbiddenCast)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.1f, 1.0f));
                ImGui.TextUnformatted(T($"● [PAUSADO] CAST PROIBIDO ({autoTyper.ActiveForbiddenCastName ?? "Gaze"}) [{autoTyper.ForbiddenCastRemainingMs}ms]",
                                       $"● [PAUSED] FORBIDDEN CAST ({autoTyper.ActiveForbiddenCastName ?? "Gaze"}) [{autoTyper.ForbiddenCastRemainingMs}ms]"));
            }
            else if (autoTyper.IsCurrentlyLetterlocked)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.35f, 0.35f, 1.0f));
                ImGui.TextUnformatted(T("● [PAUSADO] LETTERLOCK ATIVO", "● [PAUSED] LETTERLOCK ACTIVE"));
            }
            else if (autoTyper.IsCurrentlyGazeHolding)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.75f, 0.2f, 1.0f));
                ImGui.TextUnformatted(T($"● [PAUSADO] GAZE ATIVO ({autoTyper.CurrentGazeSpell ?? "Cursed Gaze"})", $"● [PAUSED] GAZE ACTIVE ({autoTyper.CurrentGazeSpell ?? "Cursed Gaze"})"));
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.2f, 1.0f, 0.45f, 1.0f));
                ImGui.TextUnformatted(T("● [ATIVO - MOTOR SAUCY]", "● [ACTIVE - SAUCY ENGINE]"));
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.65f, 1.0f));
            ImGui.TextUnformatted(T("○ [DESATIVADO]", "○ [DISABLED]"));
        }
        ImGui.PopStyleColor();

        ImGui.SameLine();
        ImGui.TextDisabled($"|  {autoTyper.CurrentStatus}");

        if (config.AutoLoopEnabled)
        {
            var remainingStr = config.InfiniteLoops ? T("Infinito", "Infinite") : $"{config.RemainingLoops} {T("restantes", "remaining")}";
            ImGui.TextColored(new Vector4(0.2f, 0.85f, 1.0f, 1.0f), $"🔄 {T("FARM", "FARM")}: {autoQueue.StatusText}  |  {T("Partidas", "Runs")}: {config.CompletedLoops} ({remainingStr})");
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawMainActionButtons()
    {
        var btnWidth = (ImGui.GetContentRegionAvail().X - 8f) / 2f;

        // Big Main Toggle Button
        if (config.Enabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.32f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button(T("⏹ DESLIGAR", "⏹ STOP TYPER"), new Vector2(btnWidth, 44)))
            {
                config.Enabled = false;
                config.Save();
            }
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.68f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.78f, 0.40f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.55f, 0.25f, 1.0f));
            if (ImGui.Button(T("▶ LIGAR DIGITAÇÃO", "▶ START TYPER"), new Vector2(btnWidth, 44)))
            {
                config.Enabled = true;
                config.Save();
            }
            ImGui.PopStyleColor(3);
        }

        ImGui.SameLine();

        // Auto-Loop Quick Toggle Button
        if (config.AutoLoopEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.32f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button(T("⏹ PARAR FARM", "⏹ STOP FARM"), new Vector2(btnWidth, 44)))
            {
                autoQueue.StopLoop();
            }
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.52f, 0.82f, 0.9f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.62f, 0.92f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.42f, 0.72f, 1.0f));
            if (ImGui.Button(T("🔄 AUTO-LOOP FARM", "🔄 AUTO-LOOP FARM"), new Vector2(btnWidth, 44)))
            {
                autoQueue.StartLoop();
            }
            ImGui.PopStyleColor(3);
        }
    }

    private void DrawTargetWordCard()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.14f, 0.18f, 0.75f));
        ImGui.BeginChild("###DetectedWordCard", new Vector2(-1, 56), true);

        if (!string.IsNullOrWhiteSpace(autoTyper.LastDetectedWord))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.92f, 0.35f, 1.0f), $"{T("ALVO ATUAL", "TARGET")}: {autoTyper.LastDetectedWord}");
            ImGui.TextDisabled($"{T("Tamanho", "Length")}: {autoTyper.LastDetectedWord.Length} {T("letras", "chars")}  |  {T("Palavras digitadas", "Words typed")}: {autoTyper.TotalWordsTyped}");
        }
        else
        {
            ImGui.TextDisabled(T("Aguardando próximo monstro ou wave...", "Waiting for next monster or wave..."));
            ImGui.TextDisabled($"{T("Palavras digitadas na sessão", "Words typed in session")}: {autoTyper.TotalWordsTyped}");
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void DrawSpeedSection()
    {
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), T("⚡ Velocidade de Digitação (Sliders)", "⚡ Typing Speed (Sliders)"));
        ImGui.Separator();

        ImGui.TextDisabled(T("Mova o slider para diminuir ou aumentar a velocidade (ms por letra):",
                             "Move slider to decrease or increase typing speed (ms per character):"));

        var minCharDelay = config.MinCharDelayMs;
        if (ImGui.SliderInt(T("Velocidade Mínima (ms)###MinDelay", "Min Delay (ms)###MinDelay"), ref minCharDelay, 5, 400))
        {
            config.MinCharDelayMs = minCharDelay;
            if (config.MaxCharDelayMs < minCharDelay) config.MaxCharDelayMs = minCharDelay;
            config.Save();
        }

        var maxCharDelay = config.MaxCharDelayMs;
        if (ImGui.SliderInt(T("Velocidade Máxima (ms)###MaxDelay", "Max Delay (ms)###MaxDelay"), ref maxCharDelay, 5, 400))
        {
            config.MaxCharDelayMs = Math.Max(config.MinCharDelayMs, maxCharDelay);
            config.Save();
        }

        var currentAvg = (config.MinCharDelayMs + config.MaxCharDelayMs) / 2;
        var estWpm = currentAvg > 0 ? (12000 / currentAvg) : 999;
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), $"{T("Ritmo Estimado", "Estimated Speed")}: ~{estWpm} WPM ({config.MinCharDelayMs} - {config.MaxCharDelayMs} ms/letra)");

        ImGui.Spacing();
        var btnWidth = (ImGui.GetContentRegionAvail().X - 18f) / 4f;

        if (ImGui.Button($"{T("Humano", "Human")}\n(80 WPM)", new Vector2(btnWidth, 38)))
        {
            config.MinCharDelayMs = 80;
            config.MaxCharDelayMs = 130;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Rápido", "Fast")}\n(140 WPM)", new Vector2(btnWidth, 38)))
        {
            config.MinCharDelayMs = 40;
            config.MaxCharDelayMs = 70;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Pro", "Pro")}\n(200 WPM)", new Vector2(btnWidth, 38)))
        {
            config.MinCharDelayMs = 18;
            config.MaxCharDelayMs = 30;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Turbo", "Turbo")}\n(Inst.)", new Vector2(btnWidth, 38)))
        {
            config.MinCharDelayMs = 5;
            config.MaxCharDelayMs = 12;
            config.Save();
        }
    }

    private void DrawOptionsSection()
    {
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), T("🛡️ Opções Essenciais", "🛡️ Essential Options"));
        ImGui.Separator();

        var respectForbidden = config.RespectForbiddenCasts;
        if (ImGui.Checkbox(T("Pausar em Casts Proibidos (Cursed Gaze & Sensory Jammer)", "Pause on Forbidden Casts (Cursed Gaze & Sensory Jammer)"), ref respectForbidden))
        {
            config.RespectForbiddenCasts = respectForbidden;
            config.Save();
        }

        var prioritizeBarrier = config.PrioritizeBarrier;
        if (ImGui.Checkbox(T("Priorizar Barreira de Proteção (Escudo da equipe)", "Prioritize Shield Barrier (Party shield first)"), ref prioritizeBarrier))
        {
            config.PrioritizeBarrier = prioritizeBarrier;
            config.Save();
        }

        var infinite = config.InfiniteLoops;
        if (ImGui.Checkbox(T("Auto-Loop Infinito (Farma continuamente sem parar)", "Infinite Auto-Loop (Farm non-stop)"), ref infinite))
        {
            config.InfiniteLoops = infinite;
            config.Save();
        }

        if (!config.InfiniteLoops)
        {
            ImGui.SameLine();
            var loops = config.RemainingLoops;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt(T("Partidas restantes", "Remaining runs"), ref loops))
            {
                config.RemainingLoops = Math.Max(1, loops);
                config.LoopCount = config.RemainingLoops;
                config.Save();
            }
        }
    }

    public void Dispose()
    {
    }
}
