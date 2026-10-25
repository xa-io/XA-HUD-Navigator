using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace XAHudNavigator.Services;

public sealed unsafe class ZoneInstanceSnapshotService : IDisposable
{
    private readonly object gate = new();
    private readonly ZoneInstanceFreshnessTracker freshness = new();
    private Hook<UIModule.Delegates.HandlePacket>? uiModuleHandlePacketHook;
    private ZoneInstanceSnapshot snapshot;
    private bool disposed;

    public ZoneInstanceSnapshotService()
    {
        snapshot = new ZoneInstanceSnapshot(
            HookActive: false,
            HasCapturedPacket: false,
            HookUnavailableReason: string.Empty,
            CapturedAtUtc: DateTime.MinValue,
            DalamudClientStateInstance: Plugin.ClientState.Instance,
            ServerId: 0,
            TerritoryTypeId: 0,
            PacketInstance: 0,
            ContentFinderConditionId: 0,
            TransitionTerritoryFilterKey: 0,
            PopRangeId: 0,
            WeatherId: 0,
            Flags: ZoneInitFlags.None);

        ObserveContext();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Logout += OnLogout;
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;

        try
        {
            var vtable = UIModule.StaticVirtualTablePointer;
            if (vtable == null)
            {
                SetHookUnavailable("UIModule.StaticVirtualTablePointer was null.");
                return;
            }

            var handlePacketAddress = (nint)vtable->HandlePacket;
            if (handlePacketAddress == nint.Zero)
            {
                SetHookUnavailable("UIModule.HandlePacket vtable entry was unavailable.");
                return;
            }

            uiModuleHandlePacketHook = Plugin.HookProvider.HookFromAddress<UIModule.Delegates.HandlePacket>(
                handlePacketAddress,
                UIModuleHandlePacketDetour);
            uiModuleHandlePacketHook.Enable();
            lock (gate)
            {
                snapshot = snapshot with
                {
                    HookActive = true,
                    HookUnavailableReason = string.Empty
                };
            }
        }
        catch (Exception ex)
        {
            SetHookUnavailable($"Failed to install UIModule.HandlePacket hook: {ex.GetType().Name}: {ex.Message}");
            Plugin.Log.Warning(ex, "[XAHudNavigator] Failed to install the zone-init packet hook.");
        }
    }

    public ZoneInstanceSnapshot GetSnapshot()
    {
        lock (gate)
        {
            ObserveContext();
            return freshness.Apply(snapshot) with
            {
                HookActive = uiModuleHandlePacketHook != null && string.IsNullOrWhiteSpace(snapshot.HookUnavailableReason),
                DalamudClientStateInstance = Plugin.ClientState.Instance
            };
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Logout -= OnLogout;
        Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        lock (gate)
        {
            freshness.Invalidate();
            snapshot = snapshot with
            {
                HookActive = false,
                HookUnavailableReason = "Disposed."
            };
        }

        try
        {
            uiModuleHandlePacketHook?.Dispose();
        }
        finally
        {
            uiModuleHandlePacketHook = null;
        }
    }

    private void UIModuleHandlePacketDetour(UIModule* thisPtr, UIModulePacketType type, uint uintParam, void* packet)
    {
        uiModuleHandlePacketHook!.Original(thisPtr, type, uintParam, packet);

        try
        {
            CaptureZoneInitPacket(type, packet);
        }
        catch (Exception ex)
        {
            // Only our observation is contained; Original remains outside this boundary.
            DiagnosticLog.VerboseThrottled("ZoneInstanceSnapshotService.CaptureZoneInitPacket", ex);
        }
    }

    private void CaptureZoneInitPacket(UIModulePacketType type, void* packet)
    {
        if (disposed || type != UIModulePacketType.ZoneInit || packet == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)packet, (nuint)sizeof(ZoneInitPacket)))
            return;

        var zoneInitPacket = (ZoneInitPacket*)packet;
        lock (gate)
        {
            ObserveContext();
            snapshot = new ZoneInstanceSnapshot(
                HookActive: true,
                HasCapturedPacket: true,
                HookUnavailableReason: string.Empty,
                CapturedAtUtc: DateTime.UtcNow,
                DalamudClientStateInstance: Plugin.ClientState.Instance,
                ServerId: zoneInitPacket->ServerId,
                TerritoryTypeId: zoneInitPacket->TerritoryTypeId,
                PacketInstance: zoneInitPacket->Instance,
                ContentFinderConditionId: zoneInitPacket->ContentFinderConditionId,
                TransitionTerritoryFilterKey: zoneInitPacket->TransitionTerritoryFilterKey,
                PopRangeId: zoneInitPacket->PopRangeId,
                WeatherId: zoneInitPacket->WeatherId,
                Flags: zoneInitPacket->Flags)
            {
                CaptureGeneration = freshness.Generation
            };
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        lock (gate)
        {
            if (!disposed)
                ObserveContext();
        }
    }

    private void OnLogout(int type, int code)
    {
        lock (gate)
            freshness.Observe(false, 0, false);
    }

    private void OnTerritoryChanged(uint territory)
    {
        lock (gate)
            ObserveContext(territory);
    }

    private void ObserveContext(uint? territory = null)
    {
        var conditions = Conditions.Instance();
        var readable = conditions != null
            && ClientStructRuntimeInspector.IsReadableRange((nint)conditions, (nuint)sizeof(Conditions));
        var transitioning = !readable || conditions->BetweenAreas || conditions->BetweenAreas51;
        freshness.Observe(Plugin.ClientState.IsLoggedIn, territory ?? Plugin.ClientState.TerritoryType, transitioning);
    }

    internal static bool IsReplayCurrent(ZoneInstanceSnapshot capture, ZoneInitPacket replay)
        => capture.IsCurrentContext
            && replay.TerritoryTypeId == capture.TerritoryTypeId
            && replay.ServerId == capture.ServerId
            && replay.Instance == capture.PacketInstance
            && replay.ContentFinderConditionId == capture.ContentFinderConditionId
            && replay.TransitionTerritoryFilterKey == capture.TransitionTerritoryFilterKey
            && replay.PopRangeId == capture.PopRangeId
            && replay.Flags == capture.Flags;

    private void SetHookUnavailable(string reason)
    {
        lock (gate)
        {
            snapshot = snapshot with
            {
                HookActive = false,
                HookUnavailableReason = reason
            };
        }

        Plugin.Log.Warning($"[XAHudNavigator] Zone-init packet hook unavailable: {reason}");
    }
}

public readonly record struct ZoneInstanceSnapshot(
    bool HookActive,
    bool HasCapturedPacket,
    string HookUnavailableReason,
    DateTime CapturedAtUtc,
    uint DalamudClientStateInstance,
    ushort ServerId,
    ushort TerritoryTypeId,
    ushort PacketInstance,
    ushort ContentFinderConditionId,
    uint TransitionTerritoryFilterKey,
    uint PopRangeId,
    byte WeatherId,
    ZoneInitFlags Flags)
{
    public long CaptureGeneration { get; init; }
    public long CurrentGeneration { get; init; }
    public uint CurrentTerritoryTypeId { get; init; }
    public bool IsLoggedIn { get; init; }
    public bool IsTransitioning { get; init; }
    public bool IsCurrentContext => HasCapturedPacket && CaptureGeneration > 0
        && CaptureGeneration == CurrentGeneration && IsLoggedIn && !IsTransitioning
        && TerritoryTypeId != 0 && TerritoryTypeId == CurrentTerritoryTypeId;
    public string Freshness => !HasCapturedPacket ? "No packet captured"
        : !IsLoggedIn ? "Historical: logged out"
        : IsTransitioning ? "Historical: zone transition in progress or condition state unavailable"
        : CaptureGeneration == 0 || CaptureGeneration != CurrentGeneration ? "Historical: captured before the current session or zone transition"
        : TerritoryTypeId == 0 || TerritoryTypeId != CurrentTerritoryTypeId ? "Historical: territory does not match the current zone"
        : "Current session and territory";
    public bool PacketSaysInstancedArea => (Flags & ZoneInitFlags.IsInstancedArea) != 0;
}

// Kept independent of the game and hook so transition/relogin behavior is directly testable.
internal sealed class ZoneInstanceFreshnessTracker
{
    private bool observed;
    private bool loggedIn;
    private bool transitioning;
    private uint territory;
    internal long Generation { get; private set; } = 1;

    internal void Observe(bool isLoggedIn, uint territoryType, bool isTransitioning)
    {
        // A transition can deliver ZoneInit before ClientState publishes the destination.
        // Its leading edge already retired the old packet; changing territory while that
        // transition settles must not retire the new destination packet a second time.
        if (observed && ((loggedIn && !isLoggedIn)
            || (territory != territoryType && !transitioning)
            || (!transitioning && isTransitioning)))
            Invalidate();
        observed = true;
        loggedIn = isLoggedIn;
        territory = territoryType;
        transitioning = isTransitioning;
    }

    internal void Invalidate() => Generation++;

    internal ZoneInstanceSnapshot Apply(ZoneInstanceSnapshot capture) => capture with
    {
        CurrentGeneration = Generation,
        CurrentTerritoryTypeId = territory,
        IsLoggedIn = loggedIn,
        IsTransitioning = transitioning,
    };
}
