using System;
using System.Collections.Generic;

namespace XAHudNavigator.Services;

internal static class DiagnosticLog
{
    private static readonly DiagnosticThrottle Throttle = new(TimeSpan.FromSeconds(30));

    internal static void VerboseThrottled(string site, Exception exception)
    {
        if (!Throttle.ShouldLogThrottled(site, DateTime.UtcNow))
            return;

        try
        {
            Plugin.Log?.Verbose(exception, $"[XAHudNavigator] Suppressed managed failure at {site}.");
        }
        catch
        {
            // Diagnostics must never turn a contained native-read failure into a new failure.
        }
    }

    internal static void WarningOnce(string site, string message, Exception? exception = null)
    {
        if (!Throttle.ShouldLogOnce(site))
            return;

        try
        {
            if (exception == null)
                Plugin.Log?.Warning($"[XAHudNavigator] {message}");
            else
                Plugin.Log?.Warning(exception, $"[XAHudNavigator] {message}");
        }
        catch
        {
            // Diagnostics must remain fail-safe during startup, shutdown, and tests.
        }
    }
}

internal sealed class DiagnosticThrottle
{
    private readonly Dictionary<string, DateTime> lastLogged = new(StringComparer.Ordinal);
    private readonly HashSet<string> warningsLogged = new(StringComparer.Ordinal);
    private readonly TimeSpan interval;
    private readonly object gate = new();

    internal DiagnosticThrottle(TimeSpan interval)
    {
        if (interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        this.interval = interval;
    }

    internal bool ShouldLogThrottled(string site, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);
        lock (gate)
        {
            if (lastLogged.TryGetValue(site, out var last) && nowUtc - last < interval)
                return false;

            lastLogged[site] = nowUtc;
            return true;
        }
    }

    internal bool ShouldLogOnce(string site)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(site);
        lock (gate)
            return warningsLogged.Add(site);
    }
}
