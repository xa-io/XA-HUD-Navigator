using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

/// <summary>Copies the local character's equipped inventory; retired waist storage remains diagnostic only.</summary>
internal static unsafe class UiEquipmentReader
{
    private const int StorageSlots = 14;
    private const int RetiredWaistSlot = 5;
    private static readonly string[] SlotNames =
    ["Main hand", "Off hand", "Head", "Body", "Hands", "Waist (retired)", "Legs", "Feet",
        "Earrings", "Necklace", "Bracelets", "Right ring", "Left ring", "Soul crystal"];

    public static UiMenuSnapshot? Capture(string name, AtkUnitBase* addon)
    {
        if (name != "Character")
            return null;
        return CaptureWithSources(name, addon, AcquireContainer,
            () => ((nint)PlayerState.Instance(), (nint)InventoryManager.Instance()));
    }

    internal static UiMenuSnapshot CaptureWithSources(string name, AtkUnitBase* addon,
        Func<nint, InventoryType, nint> getContainer, nint playerAddress, nint managerAddress)
        => CaptureWithSources(name, addon, getContainer, () => (playerAddress, managerAddress));

    internal static UiMenuSnapshot CaptureWithSources(string name, AtkUnitBase* addon,
        Func<nint, InventoryType, nint> getContainer, Func<(nint Player, nint Manager)> getContext)
    {
        if (name != "Character")
            return Unavailable(name, addon, "This addon has no verified local-character equipment source.");
        try
        {
            if (!TryReadContext((nint)addon, getContext, out var context, out var issue))
                return Unavailable(name, addon, issue);
            // EquippedItemsInventoryType may refer to a retainer; local equipment always uses this exact type.
            var address = getContainer(context.Manager, InventoryType.EquippedItems);
            if (!NativeSheetReader.TryCopy(address, out InventoryContainer header)
                || !header.IsLoaded || header.Type != InventoryType.EquippedItems
                || header.Size != StorageSlots || header.Items == null)
                return Unavailable(name, addon, "Local equipped-item storage is unreadable, unloaded or not the verified 14-slot layout; no equipment rows were published.");

            var entries = new List<UiMenuEntry>(StorageSlots - 1);
            var fields = new List<UiMenuField>();
            var occupied = 0;
            var empty = 0;
            var proxies = 0;
            for (var slot = 0; slot < StorageSlots; slot++)
            {
                var offset = (nuint)(slot * sizeof(InventoryItem));
                if ((nuint)header.Items > nuint.MaxValue - offset)
                    return Unavailable(name, addon, "Equipment slot address overflowed; all rows were discarded.");
                var itemAddress = (nint)((nuint)header.Items + offset);
                if (!NativeSheetReader.TryCopy(itemAddress, out InventoryItem item))
                    return Unavailable(name, addon, $"Equipment storage slot {slot} is unreadable; all rows were discarded.");
                if (slot == RetiredWaistSlot)
                {
                    // Retired storage may retain default or stale identity fields. Do not treat it as active equipment.
                    fields.Add(new("Retired waist storage", "Native storage slot 5 is excluded from active equipment; its copied fields below are diagnostic only.", "HUD Navigator interpretation of retired equipment storage"));
                    foreach (var field in UiInventoryReader.CreateEntry(item, 0, address, itemAddress).Fields)
                        fields.Add(field with { Name = "Retired waist / " + field.Name });
                    continue;
                }
                if (item.Container != InventoryType.EquippedItems || item.Slot != slot
                    || !item.IsSymbolic && (item.Quantity < 0 || (item.ItemId == 0) != (item.Quantity == 0)))
                    return Unavailable(name, addon, $"Equipment slot {slot} has inconsistent identity, item ID or quantity; all rows were discarded.");

                var entry = UiInventoryReader.CreateEntry(item, 0, address, itemAddress);
                var rowFields = new List<UiMenuField>(entry.Fields)
                {
                    new("Equipment slot", SlotNames[slot], "HUD Navigator label for the verified EquippedItems native slot"),
                };
                entries.Add(entry with { Detail = SlotNames[slot] + " — " + entry.Detail, Fields = rowFields.ToArray() });
                if (item.IsSymbolic) proxies++;
                else if (item.ItemId == 0) empty++;
                else occupied++;
            }

            if (getContainer(context.Manager, InventoryType.EquippedItems) != address
                || !NativeSheetReader.TryCopy(address, out InventoryContainer after)
                || after.Type != header.Type || after.Size != header.Size || after.IsLoaded != header.IsLoaded
                || after.Items != header.Items || after.VirtualTable != header.VirtualTable)
                return Unavailable(name, addon, "Local equipped-item storage changed during capture; all rows were discarded.");
            if (!TryReadContext((nint)addon, getContext, out var finalContext, out _) || context != finalContext)
                return Unavailable(name, addon, "The Character addon, current player, class/job, level or inventory manager changed during capture; all rows were discarded.");

            fields.InsertRange(0, new UiMenuField[]
            {
                new("Scope", "All 13 active local-character equipment slots, including empty slots; retired waist storage is diagnostic only.", "InventoryType.EquippedItems (1000)"),
                new("Available slots", entries.Count.ToString(), "Active equipped-item slots; native slot 5 excluded"),
                new("Occupied slots", occupied.ToString(), "Non-symbolic active slots with an item and positive quantity"),
                new("Free slots", empty.ToString(), "Non-symbolic active slots with ItemId=0 and Quantity=0"),
                new("Unresolved proxy slots", proxies.ToString(), "Symbolic links were not followed"),
                new("Current class/job ID", context.ClassJob.ToString(), "Copied PlayerState.CurrentClassJobId; ClassJob sheet row ID"),
                new("Current level", context.Level.ToString(), "Copied PlayerState.CurrentLevel"),
            });
            return new(name, (ulong)(nuint)addon, "Inventory", "Character equipment — all 13 active slots", entries.ToArray())
            {
                CapturedAtUtc = DateTime.UtcNow, IsVisible = true, IsDecoded = true, IsPartial = proxies != 0,
                Fields = fields.ToArray(),
                Status = $"13 active equipment slots: {occupied} occupied, {empty} empty, {proxies} unresolved proxies. Empty off-hand and soul-crystal slots are included. Native slot 5 (retired waist) is retained only in raw menu diagnostics.",
            };
        }
        catch (Exception ex)
        {
            return Unavailable(name, addon, $"Equipment capture is unavailable: {ex.GetType().Name}; no rows were published.");
        }
    }

    private static bool TryReadContext(nint addonAddress, Func<(nint Player, nint Manager)> getContext,
        out ContextIdentity identity, out string issue)
    {
        identity = default;
        issue = "The Character addon is unreadable, hidden or not ready; no equipment slots were inspected.";
        if (!NativeSheetReader.TryCopy(addonAddress, out AtkUnitBase addon) || !addon.IsVisible || !addon.IsReady)
            return false;
        var context = getContext();
        issue = "The current player or inventory manager is unavailable; no equipment slots were inspected.";
        if (!NativeSheetReader.TryCopy(context.Player, out PlayerState player) || !player.IsLoaded || player.ContentId == 0
            || !NativeSheetReader.TryCopy(context.Manager, out InventoryManager _))
            return false;
        identity = new(context.Player, player.ContentId, context.Manager, addon.Id, addon.ParentId,
            player.CurrentClassJobId, player.CurrentLevel);
        issue = string.Empty;
        return true;
    }

    private static nint AcquireContainer(nint managerAddress, InventoryType type)
    {
        var manager = InventoryManager.Instance();
        return (nint)manager == managerAddress && NativeSheetReader.TryCopy(managerAddress, out InventoryManager _)
            ? (nint)manager->GetInventoryContainer(type) : 0;
    }

    private readonly record struct ContextIdentity(nint Player, ulong Owner, nint Manager,
        ushort AddonId, ushort ParentId, byte ClassJob, short Level);
    private static UiMenuSnapshot Unavailable(string name, AtkUnitBase* addon, string issue)
        => new(name, (ulong)(nuint)addon, "Inventory", "Character equipment — unavailable", Array.Empty<UiMenuEntry>())
        { CapturedAtUtc = DateTime.UtcNow, IsVisible = false, IsDecoded = false, IsPartial = true, Status = issue };
}
