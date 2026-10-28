using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    private string testWord = "SEPHIROTH";
    private string nodeFilter = string.Empty;

    public MainWindow(AutoTyperService autoTyper, AutoQueueService autoQueue, Configuration config)
        : base("Digitas Automatizados###DigitasAutomatizadosMain", ImGuiWindowFlags.NoCollapse)
    {
        this.autoTyper = autoTyper;
        this.autoQueue = autoQueue;
        this.config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Size = new Vector2(560, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private string T(string pt, string en) => config.Language == "en" ? en : pt;

    public override void Draw()
    {
        DrawHeader();
        DrawStatusBar();

        ImGui.Spacing();
        if (ImGui.BeginTabBar("###DigitasTabs"))
        {
            if (ImGui.BeginTabItem($"{T("Controle Principal", "Main Control")}###TabControl"))
            {
                DrawControlTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"{T("🔄 Auto-Loop (Farm)", "🔄 Auto-Loop (Farm)")}###TabAutoLoop"))
            {
                DrawAutoLoopTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"{T("Entrada & Segurança", "Input & Safety")}###TabAdvanced"))
            {
                DrawAdvancedTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"{T("Inspetor de UI (Debug)", "UI Inspector (Debug)")}###TabInspector"))
            {
                DrawInspectorTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        // Top action bar: Title badge on left, Language Switch & Ko-Fi Donate on right
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), "⌨ Digitas Automatizados");
        ImGui.SameLine();
        ImGui.TextDisabled("v1.1");
        ImGui.EndGroup();

        ImGui.SameLine();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var donateBtnWidth = 135f;
        var langBtnWidth = 65f;
        var spacing = 8f;
        var offset = availWidth - (donateBtnWidth + langBtnWidth + spacing);
        if (offset > 0)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        }

        // Language Switch Button
        var isEn = config.Language == "en";
        if (ImGui.Button(isEn ? "🇧🇷 PT" : "🇺🇸 EN", new Vector2(langBtnWidth, 24)))
        {
            config.Language = isEn ? "pt" : "en";
            config.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(T("Trocar idioma para Português", "Switch language to English"));
        }

        ImGui.SameLine();

        // Ko-Fi Donation Button
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1.0f, 0.37f, 0.35f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.48f, 0.45f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.25f, 0.23f, 1.0f));
        if (ImGui.Button(T("☕ Apoiar no Ko-fi", "☕ Buy me a Coffee"), new Vector2(donateBtnWidth, 24)))
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://ko-fi.com/n0way02") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Falha ao abrir o link do Ko-fi.");
            }
        }
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("https://ko-fi.com/n0way02\n" + T("Clique para abrir a página de doação no navegador!", "Click to open the donation page in your browser!"));
        }

        ImGui.Spacing();
    }

    private void DrawStatusBar()
    {
        var height = config.AutoLoopEnabled ? 66f : 48f;
        ImGui.BeginChild("###StatusBanner", new Vector2(-1, height), true);

        if (config.Enabled)
        {
            if (autoTyper.IsCurrentlyLetterlocked)
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
                ImGui.TextUnformatted(T("● [ATIVO] AUTO-TYPER PRONTO", "● [ACTIVE] AUTO-TYPER READY"));
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
            ImGui.TextColored(new Vector4(0.2f, 0.85f, 1.0f, 1.0f), $"🔄 {T("AUTO-LOOP", "AUTO-LOOP")}: {autoQueue.StatusText}  |  {T("Partidas", "Runs")}: {config.CompletedLoops} ({remainingStr})");
        }

        ImGui.EndChild();
    }

    private static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.35f, 0.85f, 1.0f, 1.0f), text);
        ImGui.Separator();
    }

    private void DrawControlTab()
    {
        ImGui.Spacing();

        // Big Main Toggle Button
        var isEnabled = config.Enabled;
        if (isEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.32f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button(T("⏹ DESLIGAR AUTO-TYPER", "⏹ STOP AUTO-TYPER"), new Vector2(-1, 46)))
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
            if (ImGui.Button(T("▶ LIGAR AUTO-TYPER", "▶ START AUTO-TYPER"), new Vector2(-1, 46)))
            {
                config.Enabled = true;
                config.Save();
            }
            ImGui.PopStyleColor(3);
        }

        // Auto-Loop Quick Toggle Button
        ImGui.Spacing();
        if (config.AutoLoopEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.32f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button(T("⏹ PARAR AUTO-LOOP (FARM COMPLETO)", "⏹ STOP AUTO-LOOP (FULL FARM)"), new Vector2(-1, 34)))
            {
                autoQueue.StopLoop();
            }
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.85f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.65f, 0.95f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.45f, 0.75f, 1.0f));
            if (ImGui.Button(T("🔄 INICIAR AUTO-LOOP COMPLETO (FARM)", "🔄 START AUTO-LOOP FULL FARM"), new Vector2(-1, 34)))
            {
                autoQueue.StartLoop();
            }
            ImGui.PopStyleColor(3);
        }

        SectionHeader(T("🎯 Alvo Atual Detectado", "🎯 Detected Target"));

        ImGui.BeginChild("###DetectedWordCard", new Vector2(-1, 74), true);
        if (!string.IsNullOrWhiteSpace(autoTyper.LastDetectedWord))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.92f, 0.3f, 1.0f), $"{T("ALVO", "TARGET")}: {autoTyper.LastDetectedWord}");
            ImGui.TextDisabled($"{T("Tamanho", "Length")}: {autoTyper.LastDetectedWord.Length} {T("letras", "chars")}  |  {T("Palavras digitadas nesta sessão", "Words typed this session")}: {autoTyper.TotalWordsTyped}");
        }
        else
        {
            ImGui.TextDisabled(T("Nenhum monstro ou frase detectada no momento...", "No target or enemy detected at the moment..."));
        }

        if (config.FilterCastNames)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.82f, 1.0f, 1.0f), $"{T("Filtro de Casts", "Cast Filter")}: {T("ATIVO", "ACTIVE")} ({autoTyper.CurrentCastNames.Count} {T("magias bloqueadas", "spells blocked")})");
        }
        ImGui.EndChild();

        SectionHeader(T("⚡ Presets de Velocidade", "⚡ Speed Presets"));

        var btnWidth = (ImGui.GetContentRegionAvail().X - 18f) / 4f;

        if (ImGui.Button($"{T("Humano Seguro", "Safe Human")}\n(80 WPM)", new Vector2(btnWidth, 42)))
        {
            config.InitialDelayMs = 100;
            config.MinCharDelayMs = 65;
            config.MaxCharDelayMs = 114;
            config.DelayBeforeEnterMs = 100;
            config.DelayPostEnterMs = 120;
            config.DelayAfterWordMs = 50;
            config.ReactionDelayMs = 48;
            config.SameWordRetryDelayMs = 400;
            config.PrioritizeShortWords = false;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Rápido", "Fast")}\n(140 WPM)", new Vector2(btnWidth, 42)))
        {
            config.InitialDelayMs = 75;
            config.MinCharDelayMs = 25;
            config.MaxCharDelayMs = 45;
            config.DelayBeforeEnterMs = 30;
            config.DelayPostEnterMs = 110;
            config.DelayAfterWordMs = 30;
            config.ReactionDelayMs = 0;
            config.SameWordRetryDelayMs = 250;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Pro", "Pro")}\n(200 WPM)", new Vector2(btnWidth, 42)))
        {
            config.InitialDelayMs = 60;
            config.MinCharDelayMs = 12;
            config.MaxCharDelayMs = 22;
            config.DelayBeforeEnterMs = 20;
            config.DelayPostEnterMs = 95;
            config.DelayAfterWordMs = 15;
            config.ReactionDelayMs = 0;
            config.SameWordRetryDelayMs = 150;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{T("Turbo", "Turbo")}\n({T("Instantâneo", "Instant")})", new Vector2(btnWidth, 42)))
        {
            config.InitialDelayMs = 50;
            config.MinCharDelayMs = 4;
            config.MaxCharDelayMs = 8;
            config.DelayBeforeEnterMs = 10;
            config.DelayPostEnterMs = 85;
            config.DelayAfterWordMs = 0;
            config.ReactionDelayMs = 0;
            config.SameWordRetryDelayMs = 100;
            config.Save();
        }

        // Collapsible Advanced Timing Sliders (Hidden by default for a clean, sleek UI)
        ImGui.Spacing();
        if (ImGui.CollapsingHeader(T("⏱️ Ajustes Finos de Timing & Delays (Opcional)", "⏱️ Advanced Timing & Delays (Optional)")))
        {
            ImGui.Indent();

            var reactionDelay = config.ReactionDelayMs;
            if (ImGui.SliderInt(T("Delay de Escolha / Reação (ms) [0 = Instantâneo]", "Reaction / Target Choice Delay (ms) [0 = Instant]"), ref reactionDelay, 0, 300))
            {
                config.ReactionDelayMs = reactionDelay;
                config.Save();
            }

            var initDelay = config.InitialDelayMs;
            if (ImGui.SliderInt(T("Delay Inicial antes da 1ª Letra (ms)", "Initial Delay before 1st Character (ms)"), ref initDelay, 30, 250))
            {
                config.InitialDelayMs = initDelay;
                config.Save();
            }

            var minDelay = config.MinCharDelayMs;
            if (ImGui.SliderInt(T("Delay Mínimo por Letra (ms)", "Min Delay per Character (ms)"), ref minDelay, 1, 150))
            {
                config.MinCharDelayMs = minDelay;
                if (config.MaxCharDelayMs < minDelay) config.MaxCharDelayMs = minDelay;
                config.Save();
            }

            var maxDelay = config.MaxCharDelayMs;
            if (ImGui.SliderInt(T("Delay Máximo por Letra (ms)", "Max Delay per Character (ms)"), ref maxDelay, 1, 200))
            {
                config.MaxCharDelayMs = Math.Max(config.MinCharDelayMs, maxDelay);
                config.Save();
            }

            var enterDelay = config.DelayBeforeEnterMs;
            if (ImGui.SliderInt(T("Delay Antes do ENTER (ms)", "Delay Before ENTER (ms)"), ref enterDelay, 0, 200))
            {
                config.DelayBeforeEnterMs = enterDelay;
                config.Save();
            }

            var postEnterDelay = config.DelayPostEnterMs;
            if (ImGui.SliderInt(T("Delay pós-ENTER / Limpeza da Caixa (ms)", "Post-ENTER / Box Clear Delay (ms)"), ref postEnterDelay, 20, 250))
            {
                config.DelayPostEnterMs = postEnterDelay;
                config.Save();
            }

            var wordDelay = config.DelayAfterWordMs;
            if (ImGui.SliderInt(T("Delay Entre Palavras (ms)", "Delay Between Words (ms)"), ref wordDelay, 0, 300))
            {
                config.DelayAfterWordMs = wordDelay;
                config.Save();
            }

            var sameWordDelay = config.SameWordRetryDelayMs;
            if (ImGui.SliderInt(T("Espera para Repetir Mesma Palavra (ms)", "Same Word Retry Delay (ms)"), ref sameWordDelay, 50, 1000))
            {
                config.SameWordRetryDelayMs = sameWordDelay;
                config.Save();
            }

            ImGui.Unindent();
        }

        SectionHeader(T("🛡️ Prioridade de Alvos & Segurança", "🛡️ Target Priority & Safety"));

        var prioritizeBarrier = config.PrioritizeBarrier;
        if (ImGui.Checkbox(T("Priorizar Barreira de Proteção (Escudo da equipe antes de qualquer inimigo)", "Prioritize Shield Barrier (Protect party before any enemy)"), ref prioritizeBarrier))
        {
            config.PrioritizeBarrier = prioritizeBarrier;
            config.Save();
        }

        var filterCastNames = config.FilterCastNames;
        if (ImGui.Checkbox(T("Filtrar Nomes de Casts (Nunca digitar nomes de magias como Tankbuster, Big Bomber, etc.)", "Filter Cast Names (Never type enemy spell casts like Tankbuster, etc.)"), ref filterCastNames))
        {
            config.FilterCastNames = filterCastNames;
            config.Save();
        }

        var prioritizeShort = config.PrioritizeShortWords;
        if (ImGui.Checkbox(T("Priorizar palavras menores (Mobs pequenos antes do Boss)", "Prioritize shorter words (Small adds before Boss)"), ref prioritizeShort))
        {
            config.PrioritizeShortWords = prioritizeShort;
            config.Save();
        }

        var respectGaze = config.RespectGaze;
        if (ImGui.Checkbox(T("Pausar em Ataques de Olhar / Gaze (Evita Letterlock automático em casts cheios)", "Pause on Gaze / Eye Attacks (Prevents automatic Letterlock)"), ref respectGaze))
        {
            config.RespectGaze = respectGaze;
            config.Save();
        }

        var letterlock = config.RespectLetterlock;
        if (ImGui.Checkbox(T("Respeitar Letterlock (Pausar imediatamente quando bloqueado)", "Respect Letterlock (Immediately pause while locked)"), ref letterlock))
        {
            config.RespectLetterlock = letterlock;
            config.Save();
        }

        var autoEnter = config.AutoPressEnter;
        if (ImGui.Checkbox(T("Pressionar ENTER automaticamente após terminar a palavra", "Auto-press ENTER after typing word"), ref autoEnter))
        {
            config.AutoPressEnter = autoEnter;
            config.Save();
        }

        var onlyDuty = config.OnlyInKeyboundDuty;
        if (ImGui.Checkbox(T("Ativar apenas dentro da instância do Keybound Brawler", "Only activate inside Keybound Brawler duty"), ref onlyDuty))
        {
            config.OnlyInKeyboundDuty = onlyDuty;
            config.Save();
        }

        if (ImGui.CollapsingHeader(T("📜 Histórico de Palavras Enviadas", "📜 Typed Words History")))
        {
            foreach (var h in autoTyper.History) ImGui.BulletText(h);
        }
    }

    private void DrawAdvancedTab()
    {
        SectionHeader(T("Método de Simulação de Teclado", "Keyboard Simulation Method"));

        var method = config.InputMethod;
        if (ImGui.RadioButton(T("SendInput Direto (Hardware Real - Recomendado / Zero AFK Kick)", "Direct SendInput (Hardware Scan Code - Recommended / Zero AFK Kick)"), method == 1))
        {
            config.InputMethod = 1;
            config.Save();
        }
        if (ImGui.RadioButton(T("Modo PostMessage (Mensagem de Janela)", "PostMessage Mode (Window Message)"), method == 0))
        {
            config.InputMethod = 0;
            config.Save();
        }
        ImGui.TextDisabled(T("• SendInput Direto: simula hardware real com scan codes e timer multimídia de 1ms. Previne 100% de expulsão por inatividade e não come letras.",
            "• Direct SendInput: simulates real hardware scan codes with 1ms multimedia timer. 100% prevents AFK kick and never drops characters."));

        SectionHeader(T("Prevenção de Inatividade (Anti-AFK)", "Inactivity Prevention (Anti-AFK)"));
        var antiAfk = config.AntiAfk;
        if (ImGui.Checkbox(T("Ativar pulso Anti-AFK periódico (invisível, zera timer de inatividade)", "Enable periodic Anti-AFK pulse (invisible, resets inactivity timer)"), ref antiAfk))
        {
            config.AntiAfk = antiAfk;
            config.Save();
        }
        ImGui.TextDisabled(T("Envia um micro-pulso de tecla a cada 15s para manter o servidor do FFXIV ciente da atividade de hardware.",
            "Sends a tiny key pulse every 15s to keep the FFXIV server aware of hardware activity."));

        SectionHeader(T("Teste de Digitação no Jogo", "In-Game Typing Test"));

        ImGui.InputText(T("Texto de Teste", "Test Text"), ref testWord, 64);
        if (ImGui.Button(T("Testar Digitação", "Test Typing")))
        {
            _ = autoTyper.TestTypeAsync(testWord);
        }

        SectionHeader(T("Configuração de Addon", "Addon Configuration"));

        var addonName = config.CustomAddonName;
        if (ImGui.InputText(T("Nome do Addon Primário", "Primary Addon Name"), ref addonName, 64))
        {
            config.CustomAddonName = addonName;
            config.Save();
        }

        SectionHeader(T("Comandos de Chat", "Chat Commands"));
        ImGui.BulletText("/digitas  |  /kbtyper  |  /kbt  : " + T("Abre ou fecha esta janela", "Open or close this window"));
        ImGui.BulletText("/kbttoggle  |  /kbt toggle  : " + T("Alterna Ligar/Desligar (ideal para macro)", "Toggle On/Off (great for macros)"));
    }

    private List<(string Name, List<string> Texts)> discovered = [];
    private string findWord = "";
    private string diagReport = "";
    private bool diagRunning;

    private void DrawDiagnostic()
    {
        SectionHeader(T("Diagnóstico completo", "Full Diagnostic"));
        ImGui.TextWrapped(T("Digite uma palavra que está na tela AGORA (ex: phantom) e clique. Procura em nós, valores, arrays e memória.",
            "Type a word that is currently visible on screen (e.g. phantom) and click. Searches nodes, values, arrays, and memory."));
        ImGui.InputText($"{T("Palavra", "Word")}###DiagWord", ref findWord, 64);
        ImGui.BeginDisabled(diagRunning || string.IsNullOrWhiteSpace(findWord));
        if (ImGui.Button(diagRunning ? T("Rodando...", "Running...") : T("Rodar diagnóstico", "Run diagnostic")))
        {
            diagRunning = true;
            var w = findWord.Trim();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { diagReport = await DiagnosticService.RunAsync(w, autoTyper.Scanner); }
                catch (Exception ex) { diagReport = "Erro: " + ex; }
                finally { diagRunning = false; }
            });
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button(T("Copiar relatório", "Copy report"))) ImGui.SetClipboardText(diagReport);
        ImGui.InputTextMultiline("###DiagReport", ref diagReport, 20000, new Vector2(-1, 200), ImGuiInputTextFlags.ReadOnly);
    }

    private List<DetectedTextNode> foundNodes = [];
    private bool findSearched;

    private void DrawFindWord()
    {
        SectionHeader(T("Achar addon pela palavra da tela", "Find addon by on-screen word"));
        ImGui.InputText($"{T("Palavra", "Word")}###FindWordInput", ref findWord, 64);
        if (ImGui.Button(T("Procurar em todos os addons", "Search in all addons")))
        {
            foundNodes = autoTyper.Scanner.FindText(findWord);
            findSearched = true;
        }

        if (findSearched && foundNodes.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f), T("Não encontrada em nenhum nó de texto da UI.", "Not found in any UI text node."));

        foreach (var n in foundNodes.Take(8))
        {
            ImGui.TextColored(new Vector4(0.3f, 1f, 0.4f, 1f), $"{n.AddonName}  (node {n.NodeId})  '{n.Text}'");
            ImGui.SameLine();
            if (ImGui.Button($"{T("Usar", "Use")}###find_{n.AddonName}_{n.NodeId}"))
            {
                config.CustomAddonName = n.AddonName;
                config.Save();
            }
        }
    }

    private List<(string Name, bool Visible, int NodeCount, List<string> Texts)> allAddons = [];
    private string addonFilter = "KTG";

    private void DrawAllAddonsDiscovery()
    {
        SectionHeader(T("Todos os addons carregados", "All Loaded Addons"));
        ImGui.InputText($"{T("Filtro de nome", "Name filter")}###AddonFilter", ref addonFilter, 64);
        if (ImGui.Button(T("Listar addons", "List addons")))
        {
            allAddons = autoTyper.Scanner.ListAllAddons(addonFilter);
        }

        ImGui.BeginChild("###AllAddons", new Vector2(-1, 160), true);
        foreach (var (name, vis, count, texts) in allAddons)
        {
            if (ImGui.Button($"{T("Usar", "Use")}###all_{name}"))
            {
                config.CustomAddonName = name;
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(vis ? new Vector4(0.3f, 1f, 0.4f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f), $"{name}  [vis={vis}, nodes={count}]");
            if (texts.Count > 0) ImGui.TextDisabled(string.Join(" | ", texts.Take(8)));
            ImGui.Separator();
        }
        ImGui.EndChild();
    }

    private void DrawDiscovery()
    {
        SectionHeader(T("Descobrir Addons Visíveis", "Discover Visible Addons"));
        if (ImGui.Button(T("Listar todos os addons visíveis", "List all visible addons")))
        {
            discovered = autoTyper.Scanner.ListVisibleAddons();
        }

        ImGui.BeginChild("###DiscoveredAddons", new Vector2(-1, 140), true);
        foreach (var (name, texts) in discovered)
        {
            if (ImGui.Button($"{T("Usar", "Use")}###use_{name}"))
            {
                config.CustomAddonName = name;
                config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), name);
            if (texts.Count > 0) ImGui.TextDisabled(string.Join(" | ", texts.Take(8)));
            ImGui.Separator();
        }
        ImGui.EndChild();
    }

    private void DrawLiveMonitor()
    {
        SectionHeader(T("Monitor ao vivo", "Live Monitor"));
        var idx = config.WordStringArrayIndex;
        if (ImGui.InputInt(T("Índice do StringArray das palavras", "Target Word StringArray Index"), ref idx))
        {
            config.WordStringArrayIndex = Math.Max(0, idx);
            config.Save();
        }

        ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), $"StringArray #{config.WordStringArrayIndex}:");
        foreach (var (i, text) in autoTyper.Scanner.ReadStringArray(config.WordStringArrayIndex))
            ImGui.BulletText($"[{i}] {text}");

        if (ImGui.CollapsingHeader(T("Valores (AtkValues) dos addons KTG*", "Values (AtkValues) of KTG* addons")))
        {
            foreach (var line in autoTyper.Scanner.DumpKtgValues())
                ImGui.TextUnformatted(line);
        }
    }

    private void DrawInspectorTab()
    {
        DrawLiveMonitor();
        DrawDiagnostic();
        DrawFindWord();
        DrawDiscovery();
        DrawAllAddonsDiscovery();
        ImGui.Spacing();
        ImGui.TextUnformatted(T("Lista em tempo real de nós de texto encontrados nos Addons do evento:", "Real-time list of text nodes in event addons:"));
        ImGui.InputTextWithHint("###NodeFilter", T("Filtrar nós por texto...", "Filter nodes by text..."), ref nodeFilter, 64);

        ImGui.Spacing();
        if (ImGui.BeginTable("###NodesTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1, 260)))
        {
            ImGui.TableSetupColumn(T("Addon", "Addon"), ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn(T("ID", "ID"), ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn(T("Texto", "Text"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var nodes = autoTyper.LastScannedNodes;
            foreach (var node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(nodeFilter) &&
                    !node.Text.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !node.AddonName.Contains(nodeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.AddonName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.NodeId.ToString());

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(node.Text);
            }

            ImGui.EndTable();
        }
    }

    private void DrawAutoLoopTab()
    {
        ImGui.Spacing();

        // Big Main Auto-Loop Toggle Button
        var isLooping = config.AutoLoopEnabled;
        if (isLooping)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.85f, 0.22f, 0.22f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.32f, 0.32f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button(T("⏹ PARAR AUTO-LOOP", "⏹ STOP AUTO-LOOP"), new Vector2(-1, 46)))
            {
                autoQueue.StopLoop();
            }
            ImGui.PopStyleColor(3);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.85f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.65f, 0.95f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.45f, 0.75f, 1.0f));
            if (ImGui.Button(T("🔄 INICIAR AUTO-LOOP (FARM COMPLETO)", "🔄 START AUTO-LOOP (FULL FARM)"), new Vector2(-1, 46)))
            {
                autoQueue.StartLoop();
            }
            ImGui.PopStyleColor(3);
        }

        SectionHeader(T("📊 Status do Ciclo em Tempo Real", "📊 Live Cycle Status"));

        ImGui.BeginChild("###AutoLoopStatusCard", new Vector2(-1, 84), true);
        ImGui.TextColored(new Vector4(0.35f, 0.9f, 1.0f, 1.0f), $"{T("Estado Atual", "Current State")}: {autoQueue.StatusText}");
        ImGui.Spacing();

        var remainingStr = config.InfiniteLoops ? T("Infinito", "Infinite") : $"{config.RemainingLoops} {T("restantes", "remaining")}";
        ImGui.Text($"{T("Partidas concluídas nesta sessão", "Completed runs this session")}: ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.45f, 1.0f), config.CompletedLoops.ToString());
        ImGui.SameLine();
        ImGui.TextDisabled($"|  {T("Meta", "Target")}: {remainingStr}");

        ImGui.EndChild();

        SectionHeader(T("⚙️ Configurações de Repetição & Loops", "⚙️ Repeat & Loop Settings"));

        var infinite = config.InfiniteLoops;
        if (ImGui.Checkbox(T("Modo Infinito (Repetir sem parar até pausar manualmente)", "Infinite Mode (Repeat continuously until stopped)"), ref infinite))
        {
            config.InfiniteLoops = infinite;
            config.Save();
        }

        if (!config.InfiniteLoops)
        {
            ImGui.Spacing();
            var loops = config.RemainingLoops;
            ImGui.Text(T("Quantidade de Loops / Partidas restantes:", "Remaining Loops / Runs:"));
            ImGui.SameLine();

            if (ImGui.Button("-5###Sub5Loops")) { config.RemainingLoops = Math.Max(1, config.RemainingLoops - 5); config.Save(); }
            ImGui.SameLine();
            if (ImGui.Button("-1###Sub1Loop")) { config.RemainingLoops = Math.Max(1, config.RemainingLoops - 1); config.Save(); }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("###LoopCountInput", ref loops))
            {
                config.RemainingLoops = Math.Max(1, loops);
                config.LoopCount = config.RemainingLoops;
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("+1###Add1Loop")) { config.RemainingLoops += 1; config.Save(); }
            ImGui.SameLine();
            if (ImGui.Button("+5###Add5Loops")) { config.RemainingLoops += 5; config.Save(); }
        }

        ImGui.Spacing();
        if (ImGui.Button(T("Zerar Contador de Partidas Concluídas", "Reset Completed Runs Counter")))
        {
            config.CompletedLoops = 0;
            config.Save();
        }

        SectionHeader(T("🤖 Automações do Ciclo", "🤖 Cycle Automations"));

        var autoReg = config.AutoRegister;
        if (ImGui.Checkbox(T("Interagir com Keybound Registrar e Registrar automaticamente", "Interact with Keybound Registrar & Register automatically"), ref autoReg))
        {
            config.AutoRegister = autoReg;
            config.Save();
        }

        var autoAccept = config.AutoAcceptQueue;
        if (ImGui.Checkbox(T("Aceitar confirmação de entrada (Commence) na fila automaticamente", "Auto-accept Duty Ready popup (Commence)"), ref autoAccept))
        {
            config.AutoAcceptQueue = autoAccept;
            config.Save();
        }

        var autoExit = config.AutoExitDuty;
        if (ImGui.Checkbox(T("Sair da arena instantaneamente ao término (evita esperar 50s)", "Leave arena instantly upon completion (skips 50s wait)"), ref autoExit))
        {
            config.AutoExitDuty = autoExit;
            config.Save();
        }

        SectionHeader(T("💡 Como Usar", "💡 How to Use"));
        ImGui.TextWrapped(T(
            "1. Posicione seu personagem próximo ao NPC 'Keybound Registrar' no Gold Saucer (X: 5.8, Y: 6.0).\n" +
            "2. Clique em 'INICIAR AUTO-LOOP'.\n" +
            "3. O plugin interagirá com o NPC, colocará na fila, confirmará a entrada, jogará a partida digitando com prioridades inteligentes e sairá da arena sozinho ao terminar.\n" +
            "4. Ao sair da arena, seu personagem já reaparece em frente ao NPC e o ciclo reinicia automaticamente!",
            "1. Stand near the 'Keybound Registrar' NPC in the Gold Saucer (X: 5.8, Y: 6.0).\n" +
            "2. Click 'START AUTO-LOOP'.\n" +
            "3. The plugin will interact with the NPC, queue up, accept Duty Ready, type with smart priorities, and exit the arena upon completion.\n" +
            "4. When teleported back in front of the NPC, the cycle repeats itself automatically!"
        ));
    }

    public void Dispose() { }
}
