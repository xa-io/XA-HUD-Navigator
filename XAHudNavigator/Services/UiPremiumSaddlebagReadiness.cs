using System;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XAHudNavigator.Services;

/// <summary>Pins copied premium ownership and sorter identity across one selected-addon capture.</summary>
internal static unsafe class UiPremiumSaddlebagReadiness
{
    private const int PremiumSlots = 70;

    internal static Func<bool> CreateGuard(nint addonAddress)
        => CreateGuard(addonAddress, () => ((nint)PlayerState.Instance(), (nint)ItemOrderModule.Instance()));

    internal static Func<bool> CreateGuard(nint addonAddress, Func<(nint Player, nint Order)> acquire)
    {
        PremiumCaptureIdentity? initial = null;
        return () =>
        {
            try
            {
                var addresses = acquire();
                if (!TryRead(addonAddress, addresses.Player, addresses.Order, out var identity))
                    return false;
                if (initial.HasValue)
                    return initial.Value == identity;
                initial = identity;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLog.VerboseThrottled("UiPremiumSaddlebagReadiness.Capture", ex);
                return false;
            }
        };
    }

    private static bool TryRead(nint addonAddress, nint playerAddress, nint orderAddress, out PremiumCaptureIdentity identity)
    {
        identity = default;
        if (!NativeSheetReader.TryCopy(addonAddress, out AddonInventoryBuddy addon)
            || !addon.AtkUnitBase.IsReady || !addon.AtkUnitBase.IsVisible || addon.TabIndex != 1
            || !NativeSheetReader.TryCopy(playerAddress, out PlayerState player)
            || !player.IsLoaded || !player.HasPremiumSaddlebag || player.ContentId == 0
            || !NativeSheetReader.TryCopy(orderAddress, out ItemOrderModule order)
            || order.CharacterContentId != player.ContentId
            || !NativeSheetReader.TryCopy((nint)order.PremiumSaddleBagSorter, out ItemOrderModuleSorter sorter))
            return false;

        var first = (nint)sorter.Items.First;
        var last = (nint)sorter.Items.Last;
        var end = (nint)sorter.Items.End;
        if (!ClientStructRuntimeInspector.TryValidateVectorHeader(first, last, end, IntPtr.Size,
                out var count, out var capacity, out _)
            || count != PremiumSlots || capacity > 4096)
            return false;

        // Only verify the bounded pointer-cell storage. Entries are not followed or evaluated.
        Span<byte> cells = stackalloc byte[PremiumSlots * sizeof(nint)];
        if (!NativeSheetReader.CopyBytes(first, cells)
            || !NativeSheetReader.TryCopy(addonAddress, out AddonInventoryBuddy addonAfter)
            || addonAfter.AtkUnitBase.Id != addon.AtkUnitBase.Id || !addonAfter.AtkUnitBase.IsReady
            || !addonAfter.AtkUnitBase.IsVisible || addonAfter.TabIndex != 1
            || !NativeSheetReader.TryCopy(playerAddress, out PlayerState playerAfter)
            || !playerAfter.IsLoaded || !playerAfter.HasPremiumSaddlebag || playerAfter.ContentId != player.ContentId
            || !NativeSheetReader.TryCopy(orderAddress, out ItemOrderModule orderAfter)
            || orderAfter.CharacterContentId != order.CharacterContentId
            || orderAfter.PremiumSaddleBagSorter != order.PremiumSaddleBagSorter
            || !NativeSheetReader.TryCopy((nint)order.PremiumSaddleBagSorter, out ItemOrderModuleSorter sorterAfter)
            || (nint)sorterAfter.Items.First != first || (nint)sorterAfter.Items.Last != last || (nint)sorterAfter.Items.End != end)
            return false;

        identity = new(player.ContentId, playerAddress, orderAddress, (nint)order.PremiumSaddleBagSorter,
            first, last, end, addon.AtkUnitBase.Id);
        return true;
    }

    private readonly record struct PremiumCaptureIdentity(ulong Owner, nint Player, nint Order, nint Sorter,
        nint First, nint Last, nint End, ushort AddonId);
}
