using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Plugin.Services;

namespace XAHudNavigator.Services;

public sealed class AddonSnapshotService : IDisposable
{
    private readonly Configuration configuration;
    private readonly AddonSnapshotState state = new();
    private readonly Action unsubscribe;
    private readonly Func<DateTime> utcNow;
    private readonly Func<List<AddonInfo>> scanAllAddons;
    private readonly Func<string, AddonInfo?> scanSingleAddon;
    private readonly Func<string, int, NodeRuntimeReport?> buildNodeRuntimeReport;
    private int disposeState;

    public AddonSnapshotService(Configuration configuration)
        : this(
            configuration,
            SubscribeFramework,
            () => DateTime.UtcNow,
            AddonInspector.CaptureAllAddons,
            AddonInspector.ScanSingleAddon,
            AddonInspector.BuildNodeRuntimeReport)
    {
    }

    internal AddonSnapshotService(
        Configuration configuration,
        Func<Action, Action> subscribe,
        Func<DateTime> utcNow,
        Func<List<AddonInfo>> scanAllAddons,
        Func<string, AddonInfo?> scanSingleAddon,
        Func<string, int, NodeRuntimeReport?> buildNodeRuntimeReport)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(scanAllAddons);
        ArgumentNullException.ThrowIfNull(scanSingleAddon);
        ArgumentNullException.ThrowIfNull(buildNodeRuntimeReport);

        this.configuration = configuration;
        this.utcNow = utcNow;
        this.scanAllAddons = scanAllAddons;
        this.scanSingleAddon = scanSingleAddon;
        this.buildNodeRuntimeReport = buildNodeRuntimeReport;
        unsubscribe = subscribe(OnFrameworkUpdate)
                      ?? throw new InvalidOperationException("Framework subscription did not provide an unsubscribe action.");
    }

    public AddonFrameSnapshot Current => state.Current;
    public int HoveredNodeIndex => state.HoveredNodeIndex;

    public void SetHoveredNodeIndex(int nodeIndex)
        => state.SetHoveredNodeIndex(nodeIndex);

    public void SetSelection(string? addonName, int nodeIndex)
        => state.SetSelection(addonName, nodeIndex);

    public void RequestRefresh()
        => state.RequestRefresh();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0)
            return;

        unsubscribe();
    }

    private void OnFrameworkUpdate()
    {
        if (Volatile.Read(ref disposeState) != 0)
            return;

        var request = state.TryBeginRefresh(utcNow(), configuration.RefreshInterval);
        if (request == null)
            return;

        try
        {
            var addons = scanAllAddons();
            AddonInfo? selectedAddon = null;
            NodeRuntimeReport? selectedNodeRuntime = null;
            if (!string.IsNullOrWhiteSpace(request.SelectedAddonName))
            {
                selectedAddon = scanSingleAddon(request.SelectedAddonName);
                if (selectedAddon != null)
                {
                    var existingIndex = addons.FindIndex(addon => string.Equals(
                        addon.Name,
                        request.SelectedAddonName,
                        StringComparison.Ordinal));
                    if (existingIndex >= 0)
                    {
                        selectedAddon.IsFocused = addons[existingIndex].IsFocused;
                        addons[existingIndex] = selectedAddon;
                    }
                    else
                    {
                        addons.Add(selectedAddon);
                    }

                    if (request.SelectedNodeIndex >= 0)
                    {
                        selectedNodeRuntime = buildNodeRuntimeReport(
                            request.SelectedAddonName,
                            request.SelectedNodeIndex);
                    }
                }
            }

            state.Publish(
                request,
                addons.AsReadOnly(),
                selectedAddon,
                selectedNodeRuntime);
        }
        catch (Exception ex)
        {
            state.Fail(request, $"{ex.GetType().Name}: {ex.Message}");
            DiagnosticLog.VerboseThrottled("AddonSnapshotService.Update", ex);
        }
    }

    private static Action SubscribeFramework(Action callback)
    {
        void Handler(IFramework _) => callback();

        Plugin.Framework.Update += Handler;
        return () => Plugin.Framework.Update -= Handler;
    }
}

internal sealed class AddonSnapshotState
{
    private readonly object gate = new();
    private AddonFrameSnapshot current = AddonFrameSnapshot.Empty;
    private DateTime lastAttemptUtc = DateTime.MinValue;
    private string? selectedAddonName;
    private int selectedNodeIndex = -1;
    private int hoveredNodeIndex = -1;
    private long frameNumber;
    private long refreshGeneration = 1;
    private bool forceRefresh = true;
    private bool refreshInProgress;

    internal AddonFrameSnapshot Current => Volatile.Read(ref current);
    internal int HoveredNodeIndex => Volatile.Read(ref hoveredNodeIndex);

    internal void SetHoveredNodeIndex(int nodeIndex)
        => Volatile.Write(ref hoveredNodeIndex, nodeIndex);

    internal void SetSelection(string? addonName, int nodeIndex)
    {
        lock (gate)
        {
            if (string.Equals(selectedAddonName, addonName, StringComparison.Ordinal)
                && selectedNodeIndex == nodeIndex)
            {
                return;
            }

            selectedAddonName = addonName;
            selectedNodeIndex = nodeIndex;
            refreshGeneration++;
            forceRefresh = true;
        }
    }

    internal void RequestRefresh()
    {
        lock (gate)
        {
            refreshGeneration++;
            forceRefresh = true;
        }
    }

    internal AddonRefreshRequest? TryBeginRefresh(DateTime nowUtc, float refreshIntervalSeconds)
    {
        var normalizedInterval = TimeSpan.FromSeconds(
            Configuration.NormalizeRefreshInterval(refreshIntervalSeconds));
        lock (gate)
        {
            if (refreshInProgress)
                return null;

            if (!forceRefresh
                && nowUtc >= lastAttemptUtc
                && nowUtc - lastAttemptUtc < normalizedInterval)
                return null;

            refreshInProgress = true;
            lastAttemptUtc = nowUtc;
            return new AddonRefreshRequest(
                refreshGeneration,
                nowUtc,
                selectedAddonName,
                selectedNodeIndex);
        }
    }

    internal void Publish(
        AddonRefreshRequest request,
        IReadOnlyList<AddonInfo> addons,
        AddonInfo? selectedAddon,
        NodeRuntimeReport? selectedNodeRuntime)
    {
        lock (gate)
        {
            if (refreshGeneration == request.RefreshGeneration)
            {
                var next = new AddonFrameSnapshot(
                    ++frameNumber,
                    request.CapturedAtUtc,
                    addons,
                    selectedAddon,
                    selectedNodeRuntime)
                {
                    CaptureStatus = AddonSnapshotCaptureStatus.Captured,
                    LastAttemptUtc = request.CapturedAtUtc,
                };
                Volatile.Write(ref current, next);
            }

            FinishRefresh(request);
        }
    }

    internal void Fail(AddonRefreshRequest request, string error = "The addon snapshot could not be captured.")
    {
        lock (gate)
        {
            if (refreshGeneration == request.RefreshGeneration)
            {
                // Retain the last successful values for inspection without presenting them as a new capture.
                Volatile.Write(ref current, current with
                {
                    CaptureStatus = AddonSnapshotCaptureStatus.Failed,
                    LastAttemptUtc = request.CapturedAtUtc,
                    Error = error,
                });
                Volatile.Write(ref hoveredNodeIndex, -1);
            }
            FinishRefresh(request);
        }
    }

    private void FinishRefresh(AddonRefreshRequest request)
    {
        refreshInProgress = false;
        forceRefresh = refreshGeneration != request.RefreshGeneration;
    }
}

internal sealed record AddonRefreshRequest(
    long RefreshGeneration,
    DateTime CapturedAtUtc,
    string? SelectedAddonName,
    int SelectedNodeIndex);

public sealed record AddonFrameSnapshot(
    long FrameNumber,
    DateTime CapturedAtUtc,
    IReadOnlyList<AddonInfo> Addons,
    AddonInfo? SelectedAddon,
    NodeRuntimeReport? SelectedNodeRuntime)
{
    public AddonSnapshotCaptureStatus CaptureStatus { get; init; }
    public DateTime LastAttemptUtc { get; init; } = DateTime.MinValue;
    public string Error { get; init; } = string.Empty;
    public bool IsCurrent => CaptureStatus == AddonSnapshotCaptureStatus.Captured;

    public static AddonFrameSnapshot Empty { get; } = new(
        0,
        DateTime.MinValue,
        Array.Empty<AddonInfo>(),
        null,
        null);
}

public enum AddonSnapshotCaptureStatus
{
    NotCaptured,
    Captured,
    Failed,
}
