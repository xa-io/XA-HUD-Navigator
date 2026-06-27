using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace XAHudNavigator.Services;

public sealed unsafe class ZoneInstanceSnapshotService : IDisposable
{
    private readonly object gate = new();
    private Hook<UIModule.Delegates.HandlePacket>? uiModuleHandlePacketHook;
    private ZoneInstanceSnapshot snapshot;

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
            return snapshot with
            {
                HookActive = uiModuleHandlePacketHook != null && string.IsNullOrWhiteSpace(snapshot.HookUnavailableReason),
                DalamudClientStateInstance = Plugin.ClientState.Instance
            };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
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

        if (type != UIModulePacketType.ZoneInit || packet == null)
            return;

        var zoneInitPacket = (ZoneInitPacket*)packet;
        lock (gate)
        {
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
                Flags: zoneInitPacket->Flags);
        }
    }

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
    public bool PacketSaysInstancedArea => (Flags & ZoneInitFlags.IsInstancedArea) != 0;
}
