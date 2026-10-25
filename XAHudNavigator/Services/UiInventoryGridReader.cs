using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

/// <summary>Maps one expanded inventory grid through copied, current-owner sort order before reading its physical slots.</summary>
internal static unsafe class UiInventoryGridReader
{
    private const int PageSlots = 35;
    private const int InventorySlots = 140;

    public static UiMenuSnapshot? Capture(string name, AtkUnitBase* addon)
    {
        if (GridPage(name) < 0)
            return null;
        return CaptureWithSources(name, addon, AcquireContainer,
            () => ((nint)PlayerState.Instance(), (nint)ItemOrderModule.Instance()));
    }

    internal static UiMenuSnapshot CaptureWithSources(string name, AtkUnitBase* addon,
        Func<InventoryType, nint> getContainer, nint playerAddress, nint orderAddress)
        => CaptureWithSources(name, addon, getContainer, () => (playerAddress, orderAddress));

    internal static UiMenuSnapshot CaptureWithSources(string name, AtkUnitBase* addon,
        Func<InventoryType, nint> getContainer, Func<(nint Player, nint Order)> getContext)
    {
        var page = GridPage(name);
        if (page < 0)
            return Unavailable(name, addon, "This grid name has no verified expanded-inventory page mapping.");
        try
        {
            if (!TryReadMapping((nint)addon, getContext, out var mapping, out var issue))
                return Unavailable(name, addon, issue);
            var entries = new List<UiMenuEntry>(PageSlots);
            var containers = new Dictionary<InventoryType, ContainerStamp>();
            var free = 0;
            var occupied = 0;
            var proxies = 0;
            for (var displaySlot = 0; displaySlot < PageSlots; displaySlot++)
            {
                var mappingIndex = page * PageSlots + displaySlot;
                var mapped = mapping.Entries[mappingIndex];
                var type = (InventoryType)((int)InventoryType.Inventory1 + mapped.Page);
                if (!containers.TryGetValue(type, out var container))
                {
                    var address = getContainer(type);
                    if (!NativeSheetReader.TryCopy(address, out InventoryContainer header)
                        || !header.IsLoaded || header.Type != type || header.Size != PageSlots || header.Items == null)
                        return Unavailable(name, addon, $"Mapped source container {type} is unavailable, unloaded or has an invalid layout; no grid rows were published.");
                    container = new(address, header);
                    containers.Add(type, container);
                }
                var offset = (nuint)(mapped.Slot * sizeof(InventoryItem));
                if ((nuint)container.Header.Items > nuint.MaxValue - offset)
                    return Unavailable(name, addon, "Mapped inventory slot address overflowed; no grid rows were published.");
                var itemAddress = (nint)((nuint)container.Header.Items + offset);
                if (!NativeSheetReader.TryCopy(itemAddress, out InventoryItem item)
                    || item.Container != type || item.Slot != mapped.Slot
                    || !item.IsSymbolic && (item.Quantity < 0 || (item.ItemId == 0) != (item.Quantity == 0)))
                    return Unavailable(name, addon, $"Mapped source slot {type}[{mapped.Slot}] is unreadable or inconsistent; no grid rows were published.");
                var entry = UiInventoryReader.CreateEntry(item, 0, container.Address, itemAddress);
                var fields = new List<UiMenuField>(entry.Fields)
                {
                    new("Display slot", displaySlot.ToString(), $"{name} (zero-based visual position)"),
                    new("Sorter position", mappingIndex.ToString(), "InventorySorter.Items vector position"),
                    new("Mapped source page", mapped.Page.ToString(), "InventorySorter entry.Page"),
                    new("Mapped source slot", mapped.Slot.ToString(), "InventorySorter entry.Slot"),
                    new("Raw sorter entry index", mapped.Index.ToString(), "InventorySorter entry.Index (not used for mapping)"),
                    new("Raw sorter flags", $"0x{mapped.Flags:X2}", "InventorySorter entry.Flags"),
                    new("Sorter entry address", $"0x{mapping.EntryAddresses[mappingIndex]:X}", "Copied InventorySorter pointer cell"),
                };
                entry = entry with { Index = displaySlot, Inventory = entry.Inventory! with { DisplaySlot = displaySlot }, Fields = fields.ToArray() };
                entries.Add(entry);
                if (item.IsSymbolic) proxies++;
                else if (item.ItemId == 0) free++;
                else occupied++;
            }

            foreach (var (type, container) in containers)
            {
                if (getContainer(type) != container.Address
                    || !NativeSheetReader.TryCopy(container.Address, out InventoryContainer after)
                    || after.Type != container.Header.Type || after.Items != container.Header.Items
                    || after.Size != container.Header.Size || after.IsLoaded != container.Header.IsLoaded
                    || after.VirtualTable != container.Header.VirtualTable)
                    return Unavailable(name, addon, $"Source container {type} changed during grid capture; all grid rows were discarded.");
            }
            if (!TryReadMapping((nint)addon, getContext, out var finalMapping, out _)
                || !SameMapping(mapping, finalMapping))
                return Unavailable(name, addon, "Grid, current owner or inventory sort order changed during capture; all grid rows were discarded.");

            return new(name, (ulong)(nuint)addon, "Inventory", $"Inventory grid {page + 1} — displayed slots", entries.ToArray())
            {
                CapturedAtUtc = DateTime.UtcNow, IsVisible = true, IsDecoded = true, IsPartial = proxies != 0,
                Status = $"35 displayed slots: {occupied} occupied, {free} free, {proxies} unresolved proxies. Current-owner sort order maps every display slot to its physical container and slot; empty slots are included.",
                Fields = [new("Visual page", page.ToString(), name + " (zero-based page)"),
                    new("Available slots", "35", "Validated displayed grid slots"),
                    new("Occupied slots", occupied.ToString(), "Mapped non-symbolic items"),
                    new("Free slots", free.ToString(), "Mapped non-symbolic empty slots"),
                    new("Unresolved proxy slots", proxies.ToString(), "Symbolic slots are not followed"),
                    new("Mapping", "140 unique source slots; 35 entries per displayed page.", "Current character InventorySorter")],
            };
        }
        catch (Exception ex)
        {
            return Unavailable(name, addon, $"Grid capture is unavailable: {ex.GetType().Name}; no grid rows were published.");
        }
    }

    private static bool TryReadMapping(nint addonAddress, Func<(nint Player, nint Order)> getContext,
        out MappingSnapshot mapping, out string issue)
    {
        mapping = null!;
        issue = "Grid is unreadable, hidden or not ready; no inventory slots were inspected.";
        if (!NativeSheetReader.TryCopy(addonAddress, out AtkUnitBase addon) || !addon.IsVisible || !addon.IsReady)
            return false;
        var context = getContext();
        issue = "Current inventory sort-order ownership is unavailable or does not match the loaded character.";
        if (!NativeSheetReader.TryCopy(context.Player, out PlayerState player) || !player.IsLoaded || player.ContentId == 0
            || !NativeSheetReader.TryCopy(context.Order, out ItemOrderModule order) || order.CharacterContentId != player.ContentId
            || !NativeSheetReader.TryCopy((nint)order.InventorySorter, out ItemOrderModuleSorter sorter))
            return false;
        issue = "Inventory sort order is incomplete or has an unsupported type, page size or vector layout.";
        var first = (nint)sorter.Items.First;
        var last = (nint)sorter.Items.Last;
        var end = (nint)sorter.Items.End;
        if (sorter.InventoryType != InventoryType.Inventory1 || sorter.ItemsPerPage != PageSlots
            || sorter.SortFunctionIndex != -1 || sorter.PercentComplete != 100
            || !ClientStructRuntimeInspector.TryValidateVectorHeader(first, last, end, sizeof(nint), out var count, out var capacity, out _)
            || count != InventorySlots || capacity > 4096)
            return false;
        var pointers = new nint[InventorySlots];
        issue = "Inventory sorter pointer storage is unreadable.";
        if (!NativeSheetReader.CopyBytes(first, MemoryMarshal.AsBytes(pointers.AsSpan())))
            return false;
        var entries = new ItemOrderModuleSorterItemEntry[InventorySlots];
        var seen = new bool[InventorySlots];
        for (var index = 0; index < InventorySlots; index++)
        {
            issue = $"Inventory sorter entry {index} is unreadable, duplicated or outside the four 35-slot bags.";
            if (!NativeSheetReader.TryCopy(pointers[index], out entries[index]))
                return false;
            var entry = entries[index];
            if (entry.Page >= 4 || entry.Slot >= PageSlots)
                return false;
            var physical = entry.Page * PageSlots + entry.Slot;
            if (seen[physical])
                return false;
            seen[physical] = true;
        }
        mapping = new(new(player.ContentId, context.Player, context.Order, (nint)order.InventorySorter,
            first, last, end, addon.Id, addon.ParentId), pointers, entries);
        issue = string.Empty;
        return true;
    }

    private static bool SameMapping(MappingSnapshot first, MappingSnapshot second)
        => first.Identity == second.Identity && first.EntryAddresses.AsSpan().SequenceEqual(second.EntryAddresses)
            && MemoryMarshal.AsBytes(first.Entries.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(second.Entries.AsSpan()));

    private static nint AcquireContainer(InventoryType type)
    {
        var manager = InventoryManager.Instance();
        return NativeSheetReader.TryCopy((nint)manager, out InventoryManager _)
            ? (nint)manager->GetInventoryContainer(type) : 0;
    }

    private static int GridPage(string name) => name switch
    {
        "InventoryGrid0E" => 0, "InventoryGrid1E" => 1, "InventoryGrid2E" => 2, "InventoryGrid3E" => 3, _ => -1,
    };
    private static UiMenuSnapshot Unavailable(string name, AtkUnitBase* addon, string issue)
        => new(name, (ulong)(nuint)addon, "Inventory", "Inventory grid — mapping unavailable", Array.Empty<UiMenuEntry>())
        { CapturedAtUtc = DateTime.UtcNow, IsDecoded = false, IsPartial = true, Status = issue };
    private readonly record struct ContainerStamp(nint Address, InventoryContainer Header);
    private readonly record struct MappingIdentity(ulong Owner, nint Player, nint Order, nint Sorter,
        nint First, nint Last, nint End, ushort AddonId, ushort ParentId);
    private sealed record MappingSnapshot(MappingIdentity Identity, nint[] EntryAddresses, ItemOrderModuleSorterItemEntry[] Entries);
}
