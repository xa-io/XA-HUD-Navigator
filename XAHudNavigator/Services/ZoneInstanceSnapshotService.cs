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
            if (UIModule.StaticVirtualTablePointer == null)
            {
                Plugin.Log.Warning("[XAHudNavigator] Could not start zone-init packet hook because UIModule.StaticVirtualTablePointer was null.");
                return;
            }

            uiModuleHandlePacketHook = Plugin.HookProvider.HookFromAddress<UIModule.Delegates.HandlePacket>(
                (nint)UIModule.StaticVirtualTablePointer->HandlePacket,
                UIModuleHandlePacketDetour);
            uiModuleHandlePacketHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XAHudNavigator] Failed to install the zone-init packet hook.");
        }
    }

    public ZoneInstanceSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return snapshot with
            {
                HookActive = uiModuleHandlePacketHook != null,
                DalamudClientStateInstance = Plugin.ClientState.Instance
            };
        }
    }

    public void Dispose()
    {
        uiModuleHandlePacketHook?.Dispose();
        uiModuleHandlePacketHook = null;
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
}

public readonly record struct ZoneInstanceSnapshot(
    bool HookActive,
    bool HasCapturedPacket,
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
