using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

/// <summary>Copies all physical crystal slots without inventing a crystal identity from the slot number.</summary>
internal static unsafe class UiCrystalInventoryReader
{
    private const int CrystalSlots = 18;

    public static UiMenuSnapshot? Capture(string name, AtkUnitBase* addon)
    {
        if (name != "InventoryCrystalGrid")
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
        if (name != "InventoryCrystalGrid")
            return Unavailable(name, addon, "This addon has no verified crystal inventory source.");
        try
        {
            if (!TryReadContext((nint)addon, getContext, out var context, out var issue))
                return Unavailable(name, addon, issue);
            var address = getContainer(context.Manager, InventoryType.Crystals);
            if (!NativeSheetReader.TryCopy(address, out InventoryContainer header)
                || !header.IsLoaded || header.Type != InventoryType.Crystals
                || header.Size != CrystalSlots || header.Items == null)
                return Unavailable(name, addon, "The 18-slot crystal container is unreadable, unloaded or has an unsupported layout; no rows were published.");

            var entries = new List<UiMenuEntry>(CrystalSlots);
            var identified = 0;
            var zero = 0;
            var unidentified = 0;
            var proxies = 0;
            for (var slot = 0; slot < CrystalSlots; slot++)
            {
                var offset = (nuint)(slot * sizeof(InventoryItem));
                if ((nuint)header.Items > nuint.MaxValue - offset)
                    return Unavailable(name, addon, "Crystal slot address overflowed; all rows were discarded.");
                var itemAddress = (nint)((nuint)header.Items + offset);
                if (!NativeSheetReader.TryCopy(itemAddress, out InventoryItem item)
                    || item.Container != InventoryType.Crystals || item.Slot != slot)
                    return Unavailable(name, addon, $"Crystal slot {slot} is unreadable or has a mismatched physical identity; all rows were discarded.");
                if (!item.IsSymbolic && (item.Quantity < 0 || item.ItemId == 0 && item.Quantity != 0
                    || item.ItemId != 0 && (item.ItemId < 2 || item.ItemId > 19)))
                    return Unavailable(name, addon, $"Crystal slot {slot} has an unsupported item ID or quantity; all rows were discarded.");

                var entry = UiInventoryReader.CreateEntry(item, 0, address, itemAddress);
                var fields = new List<UiMenuField>(entry.Fields)
                {
                    new("Physical crystal slot", slot.ToString(), "InventoryType.Crystals storage order; no UI order inferred"),
                    new("Gear attributes", "Not applicable to crystals; raw values are retained separately.", "Crystal inventory interpretation"),
                };
                string label;
                string detail;
                if (item.IsSymbolic)
                {
                    proxies++;
                    label = entry.Label;
                    detail = entry.Detail;
                    fields.Add(new("Crystal identity", "Unavailable: symbolic link was not followed.", "InventoryItem.IsSymbolic"));
                }
                else if (item.ItemId == 0)
                {
                    unidentified++;
                    zero++;
                    label = "(unidentified crystal slot)";
                    detail = "Zero quantity; native item ID is absent";
                    fields.Add(new("Crystal identity", "Unavailable: raw item ID is zero; no identity was inferred from its slot.", "InventoryItem.ItemId"));
                }
                else
                {
                    identified++;
                    if (item.Quantity == 0) zero++;
                    label = string.Empty;
                    detail = "Crystal inventory item";
                    fields.Add(new("Crystal identity", "Native item ID; a zero quantity still identifies this crystal.", "InventoryItem.ItemId"));
                    fields.Add(new("Crystal tier", item.ItemId < 8 ? "Shard" : item.ItemId < 14 ? "Crystal" : "Cluster", "HUD Navigator interpretation of raw ItemId 2–19"));
                    fields.Add(new("Crystal element", Element(item.ItemId), "HUD Navigator interpretation of raw ItemId 2–19"));
                }
                entries.Add(entry with
                {
                    Label = label, Detail = detail,
                    Inventory = entry.Inventory! with { Condition = null, SpiritbondOrCollectability = null },
                    Fields = fields.ToArray(),
                });
            }

            if (getContainer(context.Manager, InventoryType.Crystals) != address
                || !NativeSheetReader.TryCopy(address, out InventoryContainer after)
                || after.Type != header.Type || after.Size != header.Size || after.IsLoaded != header.IsLoaded
                || after.Items != header.Items || after.VirtualTable != header.VirtualTable)
                return Unavailable(name, addon, "The crystal container changed during capture; all rows were discarded.");
            if (!TryReadContext((nint)addon, getContext, out var finalContext, out _) || context != finalContext)
                return Unavailable(name, addon, "The crystal addon, current player or inventory manager changed during capture; all rows were discarded.");

            return new(name, (ulong)(nuint)addon, "Inventory", "Crystal inventory — all 18 physical slots", entries.ToArray())
            {
                CapturedAtUtc = DateTime.UtcNow, IsVisible = true, IsDecoded = true,
                IsPartial = unidentified != 0 || proxies != 0,
                Status = $"18 physical crystal slots: {identified} identified, {zero} zero-quantity, {unidentified} unidentified, {proxies} unresolved proxies. Zero-quantity slots are included; identities come from native item IDs, not slot numbers.",
                Fields = [new("Available slots", CrystalSlots.ToString(), "Copied InventoryType.Crystals physical slots"),
                    new("Identified crystal slots", identified.ToString(), "Non-symbolic native item IDs 2–19"),
                    new("Zero-quantity slots", zero.ToString(), "Non-symbolic native Quantity=0, including unidentified slots"),
                    new("Unidentified crystal slots", unidentified.ToString(), "Native ItemId=0 and Quantity=0; identity not inferred"),
                    new("Unresolved proxy slots", proxies.ToString(), "Symbolic links were not followed"),
                    new("Order", "Physical container order; visual grid order is not inferred.", "InventoryContainer.Items[0–17]")],
            };
        }
        catch (Exception ex)
        {
            return Unavailable(name, addon, $"Crystal capture is unavailable: {ex.GetType().Name}; no rows were published.");
        }
    }

    private static bool TryReadContext(nint addonAddress, Func<(nint Player, nint Manager)> getContext,
        out ContextIdentity identity, out string issue)
    {
        identity = default;
        issue = "The crystal addon is unreadable, hidden or not ready; no inventory slots were inspected.";
        if (!NativeSheetReader.TryCopy(addonAddress, out AtkUnitBase addon) || !addon.IsVisible || !addon.IsReady)
            return false;
        var context = getContext();
        issue = "The current player or inventory manager is unavailable; no crystal slots were inspected.";
        if (!NativeSheetReader.TryCopy(context.Player, out PlayerState player) || !player.IsLoaded || player.ContentId == 0
            || !NativeSheetReader.TryCopy(context.Manager, out InventoryManager _))
            return false;
        identity = new(context.Player, player.ContentId, context.Manager, addon.Id, addon.ParentId);
        issue = string.Empty;
        return true;
    }

    private static nint AcquireContainer(nint managerAddress, InventoryType type)
    {
        var manager = InventoryManager.Instance();
        return (nint)manager == managerAddress && NativeSheetReader.TryCopy(managerAddress, out InventoryManager _)
            ? (nint)manager->GetInventoryContainer(type) : 0;
    }

    private static string Element(uint itemId) => ((itemId - 2) % 6) switch
    { 0 => "Fire", 1 => "Ice", 2 => "Wind", 3 => "Earth", 4 => "Lightning", _ => "Water" };

    private readonly record struct ContextIdentity(nint Player, ulong Owner, nint Manager, ushort AddonId, ushort ParentId);
    private static UiMenuSnapshot Unavailable(string name, AtkUnitBase* addon, string issue)
        => new(name, (ulong)(nuint)addon, "Inventory", "Crystal inventory — unavailable", Array.Empty<UiMenuEntry>())
        { CapturedAtUtc = DateTime.UtcNow, IsVisible = false, IsDecoded = false, IsPartial = true, Status = issue };
}
