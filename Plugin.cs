using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using KeyboundTyper.Services;
using KeyboundTyper.Windows;

namespace KeyboundTyper;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/kbtyper";
    private const string CommandAlias = "/kbt";
    private const string CommandDigitas = "/digitas";
    private const string ToggleCommand = "/kbttoggle";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    public static Configuration Configuration { get; set; } = null!;
    public WindowSystem WindowSystem { get; } = new("DigitasAutomatizados");

    public KeyboundScanner Scanner { get; }
    public AutoTyperService AutoTyper { get; }
    public MainWindow MainWindow { get; }

    public Plugin()
    {
        try { KeyboardSimulator.TimeBeginPeriod(1); } catch { }

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Scanner = new KeyboundScanner();
        AutoTyper = new AutoTyperService(Scanner, Configuration);
        MainWindow = new MainWindow(AutoTyper, Configuration);

        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Abre ou fecha a janela do Digitas Automatizados."
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Atalho para o Digitas Automatizados."
        });

        CommandManager.AddHandler(CommandDigitas, new CommandInfo(OnCommand)
        {
            HelpMessage = "Abre ou fecha o Digitas Automatizados."
        });

        CommandManager.AddHandler(ToggleCommand, new CommandInfo(OnToggleCommand)
        {
            HelpMessage = "Alterna Ligar/Desligar o Auto-Typer."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += DrawConfigUI;

        Framework.Update += OnFrameworkUpdate;

        Log.Information("Keybound Typer inicializado com sucesso!");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            ToggleAutoTyper();
            return;
        }

        MainWindow.Toggle();
    }

    private void OnToggleCommand(string command, string args)
    {
        ToggleAutoTyper();
    }

    private void ToggleAutoTyper()
    {
        Configuration.Enabled = !Configuration.Enabled;
        Configuration.Save();
        var status = Configuration.Enabled ? "ATIVADO" : "DESATIVADO";
        ChatGui.Print($"[Keybound Typer] Auto-Typer {status}!");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        AutoTyper.Update();
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
    }

    private void DrawConfigUI()
    {
        MainWindow.IsOpen = true;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= DrawConfigUI;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
        CommandManager.RemoveHandler(CommandDigitas);
        CommandManager.RemoveHandler(ToggleCommand);

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        AutoTyper.Dispose();

        try { KeyboardSimulator.TimeEndPeriod(1); } catch { }

        Log.Information("Digitas Automatizados finalizado.");
    }
}
