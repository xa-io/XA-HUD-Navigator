using Dalamud.Configuration;
using System;

namespace XAHudNavigator;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // HUD overlay toggle
    public bool OverlayEnabled { get; set; } = false;

    // Show only visible addons in the list
    public bool ShowOnlyVisible { get; set; } = true;

    public bool ShowSheetsTab { get; set; } = true;
    public bool ShowDebugTab { get; set; } = false;

    // Overlay drawing options
    public bool OverlayShowBoundingBoxes { get; set; } = true;
    public bool OverlayShowNodeIds { get; set; } = true;
    public bool OverlayShowAddonNames { get; set; } = true;
    public bool OverlayShowInteractiveOnly { get; set; } = false;

    // Overlay colors (RGBA 0-1)
    public float OverlayAlpha { get; set; } = 0.35f;

    // Auto-refresh interval (seconds)
    public float RefreshInterval { get; set; } = 0.5f;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
