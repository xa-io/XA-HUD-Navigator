using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XAHudNavigator.Windows;

namespace XAHudNavigator;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider HookProvider { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/xahud";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("XAHudNavigator");
    private MainWindow MainWindow { get; init; }
    private HudOverlay HudOverlay { get; init; }

    public MainWindow GetMainWindow() => MainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        MainWindow = new MainWindow(this);
        HudOverlay = new HudOverlay(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(HudOverlay);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the XA HUD Navigator window"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information("[XAHudNavigator] Plugin loaded successfully.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        HudOverlay.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();

        if (trimmed == "overlay")
        {
            Configuration.OverlayEnabled = !Configuration.OverlayEnabled;
            Configuration.Save();
            Log.Information($"[XAHudNavigator] Overlay toggled: {Configuration.OverlayEnabled}");
            return;
        }

        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}

internal static class BuildInfo
{
    public const string Version = "0.0.0.4";
}
