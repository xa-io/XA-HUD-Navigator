using Dalamud.Configuration;
using System;

namespace XAHudNavigator;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 1;
    internal const float DefaultRefreshInterval = 0.5f;
    internal const float MinimumRefreshInterval = 0.1f;
    internal const float MaximumRefreshInterval = 10f;

    public int Version { get; set; } = CurrentVersion;

    // HUD overlay toggle
    public bool OverlayEnabled { get; set; }

    public bool ShowSheetsTab { get; set; } = true;
    public bool ShowDebugTab { get; set; }

    // Overlay drawing options
    public bool OverlayShowBoundingBoxes { get; set; } = true;
    public bool OverlayShowNodeIds { get; set; } = true;
    public bool OverlayShowAddonNames { get; set; } = true;
    public bool OverlayShowInteractiveOnly { get; set; }

    private float refreshInterval = DefaultRefreshInterval;

    // Auto-refresh interval (seconds). Hand-edited values are bounded so a zero,
    // negative, NaN, or infinite value cannot turn the native scan into a hot loop.
    public float RefreshInterval
    {
        get => NormalizeRefreshInterval(refreshInterval);
        set => refreshInterval = value;
    }

    internal static Configuration LoadOrDefault(
        Func<object?> load,
        Action<Configuration> save,
        Action<Exception>? reportFailure = null)
    {
        Configuration configuration;
        try
        {
            configuration = load() as Configuration ?? new Configuration();
        }
        catch (Exception ex)
        {
            reportFailure?.Invoke(ex);
            return new Configuration();
        }

        if (!configuration.MigrateAndNormalize())
            return configuration;

        try
        {
            save(configuration);
        }
        catch (Exception ex)
        {
            // A transient persistence failure must not discard settings that were
            // loaded and successfully normalized in memory.
            reportFailure?.Invoke(ex);
        }

        return configuration;
    }

    internal bool MigrateAndNormalize()
    {
        var changed = false;
        if (Version < CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        var normalizedRefreshInterval = NormalizeRefreshInterval(refreshInterval);
        if (!refreshInterval.Equals(normalizedRefreshInterval))
        {
            refreshInterval = normalizedRefreshInterval;
            changed = true;
        }

        return changed;
    }

    internal static float NormalizeRefreshInterval(float value)
        => !float.IsFinite(value)
            ? DefaultRefreshInterval
            : Math.Clamp(value, MinimumRefreshInterval, MaximumRefreshInterval);

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
