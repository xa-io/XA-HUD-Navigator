using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Dalamud.Plugin.Services;

namespace XAHudNavigator.Services;

internal static class UiMenuQuery
{
    public static uint ItemSheetId(uint rawId) => rawId is > 1_000_000 and < 2_000_000 ? rawId - 1_000_000 : rawId;

    public static bool Matches(UiMenuEntry entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var text = string.Join(" ", entry.Label, entry.Detail, entry.Cost,
            entry.ItemId?.ToString(CultureInfo.InvariantCulture), entry.Index.ToString(CultureInfo.InvariantCulture),
            entry.Inventory?.Container, entry.Inventory?.Slot.ToString(CultureInfo.InvariantCulture), entry.Inventory?.Flags);
        return text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public static string Export(UiMenuFrame frame) => JsonSerializer.Serialize(new
    {
        FormatVersion = 1, PluginVersion = BuildInfo.Version, frame.Sequence, frame.CapturedAtUtc,
        frame.Context, frame.IsCurrent, frame.Status, frame.Menus, frame.Selection, frame.Detail,
    }, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>Opt-in Framework capture. Drawing and exports never dereference retained native pointers.</summary>
internal sealed class UiMenuSnapshotService : IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<DateTime> utcNow;
    private readonly Func<WorldContext?> readContext;
    private readonly Func<DateTime, WorldContext, UiMenuKey?, bool, UiMenuFrame> capture;
    private readonly Action unsubscribe;
    private readonly object gate = new();
    private UiMenuFrame current = UiMenuFrame.Empty;
    private UiMenuKey? selection;
    private bool nativeDetail;
    private int running;
    private long generation;
    private int refresh;
    private long sequence;
    private DateTime lastCapture;
    private bool disposed;

    public UiMenuFrame Current => Volatile.Read(ref current);
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public UiMenuSnapshotService(Configuration configuration)
        : this(configuration, () => DateTime.UtcNow, ReadContext, UiMenuCapture.Capture, Subscribe) { }

    internal UiMenuSnapshotService(Configuration configuration, Func<DateTime> utcNow,
        Func<WorldContext?> readContext, Func<DateTime, WorldContext, UiMenuKey?, bool, UiMenuFrame> capture,
        Func<Action, Action> subscribe)
    {
        this.configuration = configuration;
        this.utcNow = utcNow;
        this.readContext = readContext;
        this.capture = capture;
        unsubscribe = subscribe(OnUpdate);
    }

    private static Action Subscribe(Action update)
    {
        void Tick(IFramework framework) => update();
        Plugin.Framework.Update += Tick;
        return () => Plugin.Framework.Update -= Tick;
    }

    private static WorldContext? ReadContext()
    {
        if (!Plugin.ClientState.IsLoggedIn) return null;
        var local = Plugin.ObjectTable.LocalPlayer;
        return local == null ? null : new(Plugin.ClientState.TerritoryType, Plugin.ClientState.MapId, local.GameObjectId);
    }

    public void Start()
    {
        lock (gate)
        {
            if (disposed || Interlocked.Exchange(ref running, 1) != 0) return;
            Interlocked.Increment(ref generation);
            Interlocked.Exchange(ref refresh, 1);
        }
    }

    public void Stop()
    {
        Interlocked.Exchange(ref running, 0);
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            Interlocked.Exchange(ref running, 0);
            Interlocked.Exchange(ref refresh, 0);
            Volatile.Write(ref current, Current with { IsCurrent = false, Status = "Capture stopped. Retained menu data is historical." });
        }
    }

    public void RequestRefresh()
    {
        if (IsRunning) Interlocked.Exchange(ref refresh, 1);
    }

    public void Select(UiMenuKey? key, bool includeNativeDetail)
    {
        lock (gate)
        {
            if (disposed) return;
            if (selection != key)
            {
                selection = key;
                Volatile.Write(ref current, Current with { Selection = key, Detail = null, IsCurrent = false,
                    Status = IsRunning ? "Menu selected; waiting for capture." : "Capture stopped. Press Start capture to read this menu." });
            }
            nativeDetail = includeNativeDetail;
            RequestRefresh();
        }
    }

    private void OnUpdate()
    {
        lock (gate)
        {
            // Keep this before both the clock and context read: a stopped explorer performs no scanning.
            if (disposed || !IsRunning) return;
            var startedGeneration = Interlocked.Read(ref generation);
            try
            {
                var context = readContext();
                if (!IsRunning || startedGeneration != Interlocked.Read(ref generation)) return;
                if (!context.HasValue || context.Value != Current.Context)
                {
                    selection = null;
                    lastCapture = DateTime.MinValue;
                    Volatile.Write(ref current, UiMenuFrame.Empty with { Sequence = ++sequence, Context = context.GetValueOrDefault(),
                        Status = context.HasValue ? "World context changed; waiting for capture." : "No local player; waiting for a logged-in world." });
                    if (!context.HasValue) return;
                }
                var now = utcNow();
                var elapsed = (now - lastCapture).TotalSeconds;
                var forced = Interlocked.Exchange(ref refresh, 0) != 0;
                if (!forced && elapsed >= 0 && elapsed < configuration.RefreshInterval) return;
                lastCapture = now;
                var frame = capture(now, context.Value, selection, nativeDetail) with { Sequence = ++sequence };
                if (!IsRunning || startedGeneration != Interlocked.Read(ref generation)) return;
                if (readContext() != context)
                {
                    selection = null;
                    lastCapture = DateTime.MinValue;
                    Volatile.Write(ref current, UiMenuFrame.Empty with { Sequence = ++sequence, Status = "World changed during capture; discarded menu data." });
                    return;
                }
                if (!IsRunning || startedGeneration != Interlocked.Read(ref generation)) return;
                selection = frame.Selection;
                Volatile.Write(ref current, frame);
            }
            catch (Exception ex)
            {
                if (IsRunning && startedGeneration == Interlocked.Read(ref generation))
                    Volatile.Write(ref current, Current with { IsCurrent = false, Status = $"Capture failed ({ex.GetType().Name}); retained menu data is historical." });
                DiagnosticLog.VerboseThrottled("UiMenuSnapshotService.Capture", ex);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref running, 0);
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Interlocked.Exchange(ref running, 0);
            Interlocked.Exchange(ref refresh, 0);
            Volatile.Write(ref current, Current with { IsCurrent = false, Status = "Capture disposed. Retained menu data is historical." });
            unsubscribe();
        }
    }
}
