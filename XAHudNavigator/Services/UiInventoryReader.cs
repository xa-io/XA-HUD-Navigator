using System;
using System.Collections.Generic;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

/// <summary>Copies verified inventory containers during the selected UI Explorer framework capture.</summary>
internal static unsafe class UiInventoryReader
{
    internal const int SlotsPerBag = 35;
    internal const int MaximumSlots = SlotsPerBag * 4;
    private const int MaximumContainerSlots = 50;
    private static readonly InventoryProfile MainInventory = new("Main inventory — all four bags",
        "All four main bags, including every confirmed empty slot; other inventory types are not included.",
        [new(InventoryType.Inventory1, 35), new(InventoryType.Inventory2, 35), new(InventoryType.Inventory3, 35), new(InventoryType.Inventory4, 35)]);
    private static readonly InventoryProfile Saddlebag = new("Chocobo saddlebag — both normal pages",
        "Both normal saddlebag pages, including every confirmed empty slot.",
        [new(InventoryType.SaddleBag1, 35), new(InventoryType.SaddleBag2, 35)]);
    private static readonly InventoryProfile PremiumSaddlebag = new("Chocobo saddlebag — both premium pages",
        "Both premium saddlebag pages, including every confirmed empty slot; current character ownership and sorter readiness verified.",
        [new(InventoryType.PremiumSaddleBag1, 35, false), new(InventoryType.PremiumSaddleBag2, 35, false)]);
    private static readonly InventoryProfile Armoury = new("Armoury chest — all twelve categories",
        "All twelve active armoury categories, including every confirmed empty slot. The retired waist category is excluded.",
        [new(InventoryType.ArmoryMainHand, 50), new(InventoryType.ArmoryOffHand, 35),
            new(InventoryType.ArmoryHead, 35), new(InventoryType.ArmoryBody, 35), new(InventoryType.ArmoryHands, 35),
            new(InventoryType.ArmoryLegs, 35), new(InventoryType.ArmoryFeets, 35), new(InventoryType.ArmoryEar, 35),
            new(InventoryType.ArmoryNeck, 35), new(InventoryType.ArmoryWrist, 35),
            new(InventoryType.ArmoryRings, 50), new(InventoryType.ArmorySoulCrystal, 26)]);

    public static UiMenuSnapshot? Capture(string addonName, AtkUnitBase* addon)
    {
        if (!Supports(addonName))
            return null;
        if (!NativeSheetReader.TryCopy((nint)addon, out AtkUnitBase owner) || !owner.IsVisible)
            return Unavailable(addonName, addon, "The inventory addon is unavailable, unreadable or hidden.");
        var premiumReady = UiPremiumSaddlebagReadiness.CreateGuard((nint)addon);
        if (!TryGetProfile(addonName, addon, premiumReady, out _, out var profileProblem))
            return Unavailable(addonName, addon, profileProblem);
        try
        {
            var manager = InventoryManager.Instance();
            if (!NativeSheetReader.TryCopy((nint)manager, out InventoryManager _))
                return Unavailable(addonName, addon, "The inventory manager is unavailable or unreadable.");
            var managerAddress = (nint)manager;
            return CaptureContainers(addonName, addon,
                type => (nint)((InventoryManager*)managerAddress)->GetInventoryContainer(type), premiumReady);
        }
        catch (Exception ex)
        {
            return Unavailable(addonName, addon, $"Inventory acquisition is unavailable: {ex.GetType().Name}.");
        }
    }

    internal static UiMenuSnapshot CaptureContainers(string addonName, AtkUnitBase* addon, Func<InventoryType, nint> getContainer,
        Func<bool>? premiumReady = null)
    {
        if (!Supports(addonName))
            return Unavailable(addonName, addon, "This addon has no verified inventory mapping.");
        if (!NativeSheetReader.TryCopy((nint)addon, out AtkUnitBase owner) || !owner.IsVisible)
            return Unavailable(addonName, addon, "The inventory addon is unavailable, unreadable or hidden.");
        if (!TryGetProfile(addonName, addon, premiumReady, out var profile, out var profileProblem))
            return Unavailable(addonName, addon, profileProblem);

        var entries = new List<UiMenuEntry>(profile.TotalSlots);
        var fields = new List<UiMenuField>();
        var premiumHeaders = new List<ContainerStamp>(2);
        var occupied = 0;
        var free = 0;
        var proxies = 0;
        var unavailable = 0;
        var entryOffset = 0;
        foreach (var container in profile.Containers)
        {
            var type = container.Type;
            var offset = entryOffset;
            entryOffset += container.Slots;
            if (!TryCaptureContainer(container, offset, getContainer, out var rows, out var stamp, out var problem))
            {
                unavailable++;
                fields.Add(new(type.ToString(), problem, "InventoryManager.GetInventoryContainer"));
                continue;
            }
            if (profile == PremiumSaddlebag)
                premiumHeaders.Add(stamp);
            entries.AddRange(rows);
            foreach (var row in rows)
            {
                if (!row.ItemId.HasValue) proxies++;
                else if (row.Inventory!.IsEmpty) free++;
                else occupied++;
            }
            fields.Add(new(type.ToString(), $"{container.Slots} slots captured; native slot indices 0–{container.Slots - 1}.", "InventoryContainer"));
        }

        foreach (var stamp in premiumHeaders)
        {
            if (IsContainerUnchanged(stamp, getContainer))
                continue;
            unavailable++;
            fields.Add(new(stamp.Header.Type.ToString(),
                "Container storage changed or became unreadable after its page was captured; paired premium rows were withheld.",
                "Final paired premium container recheck"));
        }
        if (addonName == "InventoryBuddy" && (!TryGetProfile(addonName, addon, premiumReady, out var afterProfile, out _)
            || afterProfile != profile))
            return Unavailable(addonName, addon, "The saddlebag tab or readiness changed during capture; all rows were discarded.");
        var withheldPremium = profile == PremiumSaddlebag && unavailable != 0;
        if (withheldPremium)
        {
            entries.Clear();
            occupied = free = proxies = 0;
            fields.Add(new("Premium capture", "Paired premium capture withheld all rows because both premium pages must validate.", "PremiumSaddleBag1 and PremiumSaddleBag2"));
        }
        fields.Add(new("Scope", profile.Scope, "Verified inventory container profile"));
        fields.Add(Field("Available slots", entries.Count, "Copied and validated inventory slots"));
        fields.Add(Field("Occupied slots", occupied, "Non-symbolic slots with an item and positive quantity"));
        fields.Add(Field("Free slots", free, "Non-symbolic slots with ItemId=0 and Quantity=0"));
        fields.Add(Field("Unresolved proxy slots", proxies, "Symbolic slots; linked item data was not followed"));
        fields.Add(Field(profile == MainInventory ? "Unavailable bags" : "Unavailable containers", unavailable, "Missing or rejected containers; no empty rows fabricated"));
        return new(addonName, (ulong)(nuint)addon, "Inventory", profile.Title, entries.ToArray())
        {
            CapturedAtUtc = DateTime.UtcNow,
            IsVisible = true,
            IsDecoded = entries.Count != 0,
            IsPartial = unavailable != 0 || proxies != 0,
            Fields = fields.ToArray(),
            Status = $"{entries.Count}/{profile.TotalSlots} slots available: {occupied} occupied, {free} free, {proxies} unresolved proxies."
                + (unavailable == 0 ? string.Empty : $" {unavailable} container(s) unavailable; see container diagnostics.")
                + (withheldPremium ? " Paired premium capture withheld all rows." : string.Empty)
                + " All containers in this inventory scope are included; slot numbers are native zero-based indices in physical storage order, which may differ from UI sorting.",
        };
    }

    private static bool TryCaptureContainer(ContainerProfile container, int entryOffset, Func<InventoryType, nint> getContainer,
        out List<UiMenuEntry> rows, out ContainerStamp stamp, out string problem)
    {
        var type = container.Type;
        rows = new(container.Slots);
        stamp = default;
        problem = string.Empty;
        try
        {
            var address = getContainer(type);
            if (!NativeSheetReader.TryCopy(address, out InventoryContainer header))
                return Fail("Container is missing or unreadable.", out problem);
            if (container.RequireLoaded && !header.IsLoaded)
                return Fail("Container is not loaded; no slots were inferred.", out problem);
            if (header.Type != type)
                return Fail($"Container type mismatch: expected {type}, received {header.Type}.", out problem);
            // Each profile records its verified capacity. A changed layout requires review, not a guessed range.
            if (container.Slots < 1 || container.Slots > MaximumContainerSlots || header.Size != container.Slots || header.Items == null)
                return Fail($"Expected a verified {container.Slots}-slot container with item storage; size={header.Size}, items=0x{(nuint)header.Items:X}.", out problem);

            for (var slot = 0; slot < header.Size; slot++)
            {
                if (!TrySlotAddress((nint)header.Items, slot, out var itemAddress)
                    || !NativeSheetReader.TryCopy(itemAddress, out InventoryItem item))
                    return Fail($"Slot {slot} is unreadable; this bag's rows were discarded.", out problem);
                if (item.Container != type || item.Slot != slot)
                    return Fail($"Slot {slot} identity mismatch: {item.Container}, slot {item.Slot}; this bag's rows were discarded.", out problem);
                if (!item.IsSymbolic && (item.Quantity < 0 || (item.ItemId == 0) != (item.Quantity == 0)))
                    return Fail($"Slot {slot} has inconsistent item ID and quantity; this bag's rows were discarded.", out problem);
                rows.Add(CreateEntry(item, entryOffset, address, itemAddress));
            }

            if (getContainer(type) != address || !NativeSheetReader.TryCopy(address, out InventoryContainer after)
                || after.Type != header.Type || after.Size != header.Size || after.IsLoaded != header.IsLoaded
                || after.Items != header.Items || after.VirtualTable != header.VirtualTable)
                return Fail("Container storage changed or became unreadable during capture; this bag's rows were discarded.", out problem);
            stamp = new(address, header);
            return true;
        }
        catch (Exception ex)
        {
            return Fail($"Container acquisition failed: {ex.GetType().Name}; no rows from this bag were published.", out problem);
        }
    }

    private static bool IsContainerUnchanged(ContainerStamp stamp, Func<InventoryType, nint> getContainer)
    {
        try
        {
            var before = stamp.Header;
            return getContainer(before.Type) == stamp.Address
                && NativeSheetReader.TryCopy(stamp.Address, out InventoryContainer after)
                && after.Type == before.Type && after.Size == before.Size && after.IsLoaded == before.IsLoaded
                && after.Items == before.Items && after.VirtualTable == before.VirtualTable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static UiMenuEntry CreateEntry(InventoryItem item, int entryOffset, nint containerAddress, nint itemAddress)
    {
        var symbolic = item.IsSymbolic;
        var empty = !symbolic && item.ItemId == 0;
        var source = $"InventoryItem @ 0x{itemAddress:X}";
        var flags = $"{item.Flags} (0x{(byte)item.Flags:X2})";
        var fields = new List<UiMenuField>
        {
            new("Container", $"{item.Container} ({(int)item.Container})", source + ".Container"),
            Field("Slot", item.Slot, source + ".Slot"),
            new("Symbolic proxy", symbolic.ToString(), source + ".IsSymbolic"),
            Field("Raw item ID / link union", item.ItemId, source + ".ItemId (only an item ID when IsSymbolic=false)"),
            Field("Raw quantity", item.Quantity, source + ".Quantity"),
            new("Flags", flags, source + ".Flags"),
            Field("Condition (raw)", item.Condition, source + ".Condition"),
            Field("Spiritbond / collectability (raw)", item.SpiritbondOrCollectability, source + ".SpiritbondOrCollectability"),
            Field("Glamour ID", item.GlamourId, source + ".GlamourId"),
            Field("Crafter content ID", item.CrafterContentId, source + ".CrafterContentId"),
            Field("Event ID", item.EventId.Id, source + ".EventId.Id"),
            new("Container address", $"0x{containerAddress:X}", "InventoryContainer"),
            new("Slot address", $"0x{itemAddress:X}", "InventoryItem"),
            new("Raw virtual table", $"0x{(nuint)item.VirtualTable:X}", source + ".VirtualTable (not invoked)"),
        };
        for (var index = 0; index < item.Materia.Length; index++)
        {
            fields.Add(Field($"Materia {index} ID", item.Materia[index], source + $".Materia[{index}]"));
            fields.Add(Field($"Materia {index} grade", item.MateriaGrades[index], source + $".MateriaGrades[{index}]"));
        }
        for (var index = 0; index < item.Stains.Length; index++)
            fields.Add(Field($"Stain {index}", item.Stains[index], source + $".Stains[{index}]"));
        if (symbolic)
        {
            fields.Add(Field("Linked inventory type", item.LinkedInventoryType, source + ".LinkedInventoryType"));
            fields.Add(Field("Linked item slot", item.LinkedItemSlot, source + ".LinkedItemSlot"));
            fields.Add(new("Proxy status", "Linked item was not followed; raw proxy fields are not resolved item attributes.", source));
        }
        return new(entryOffset + item.Slot, symbolic ? "(unresolved proxy)" : empty ? "(empty)" : string.Empty,
            symbolic ? "Symbolic slot; linked item unavailable" : empty ? "Empty inventory slot" : "Inventory item")
        {
            ItemId = symbolic ? null : item.ItemId,
            Quantity = symbolic ? null : (uint)item.Quantity,
            Inventory = new(item.Container.ToString(), item.Slot, empty, symbolic ? "Unresolved proxy" : flags,
                symbolic || empty ? null : item.Condition, symbolic || empty ? null : item.SpiritbondOrCollectability),
            Fields = fields.ToArray(),
        };
    }

    private static bool TrySlotAddress(nint items, int slot, out nint address)
    {
        address = 0;
        if (slot < 0 || slot >= MaximumContainerSlots)
            return false;
        var offset = (nuint)(slot * sizeof(InventoryItem));
        if (items == 0 || (nuint)items > nuint.MaxValue - offset)
            return false;
        address = (nint)((nuint)items + offset);
        return address != 0;
    }

    private static bool Supports(string name) => name is "Inventory" or "InventoryLarge" or "InventoryExpansion"
        or "ArmouryBoard" or "InventoryBuddy" or "InventoryBuddy2";
    private static bool TryGetProfile(string name, AtkUnitBase* addon, Func<bool>? premiumReady,
        out InventoryProfile profile, out string problem)
    {
        profile = MainInventory;
        problem = string.Empty;
        if (name is "Inventory" or "InventoryLarge" or "InventoryExpansion")
            return true;
        if (name == "ArmouryBoard")
        {
            profile = Armoury;
            return true;
        }
        if (name == "InventoryBuddy")
        {
            if (!NativeSheetReader.TryCopy((nint)addon, out AddonInventoryBuddy buddy)
                || !buddy.AtkUnitBase.IsVisible || !buddy.AtkUnitBase.IsReady)
                return Fail("Saddlebag addon data is unreadable, hidden or not ready; no inventory slots were inspected.", out problem);
            if (buddy.TabIndex == 1)
            {
                try
                {
                    if (premiumReady == null || !premiumReady())
                        return Fail("Premium saddlebag is selected, but current character ownership and sorter readiness are not verified; premium containers were not inspected.", out problem);
                }
                catch (Exception ex)
                {
                    return Fail($"Premium saddlebag readiness is unavailable: {ex.GetType().Name}; premium containers were not inspected.", out problem);
                }
                profile = PremiumSaddlebag;
                return true;
            }
            if (buddy.TabIndex != 0)
                return Fail($"Saddlebag tab {buddy.TabIndex} is unrecognized; no inventory slots were inspected.", out problem);
            profile = Saddlebag;
            return true;
        }
        return Fail("This addon has no verified container mapping or current premium availability gate; no inventory slots were inspected.", out problem);
    }

    private sealed record ContainerProfile(InventoryType Type, int Slots, bool RequireLoaded = true);
    private readonly record struct ContainerStamp(nint Address, InventoryContainer Header);
    private sealed record InventoryProfile(string Title, string Scope, ContainerProfile[] Containers)
    {
        internal int TotalSlots
        {
            get
            {
                var total = 0;
                foreach (var container in Containers) total += container.Slots;
                return total;
            }
        }
    }
    private static bool Fail(string message, out string problem) { problem = message; return false; }
    private static UiMenuField Field(string name, object value, string source)
        => new(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, source);
    private static UiMenuSnapshot Unavailable(string name, AtkUnitBase* addon, string message)
        => new(name, (ulong)(nuint)addon, "Inventory", name is "InventoryBuddy" or "InventoryBuddy2" ? "Chocobo saddlebag"
            : name == "ArmouryBoard" ? Armoury.Title : MainInventory.Title, Array.Empty<UiMenuEntry>())
        { CapturedAtUtc = DateTime.UtcNow, IsDecoded = false, IsPartial = true, IsVisible = false, Status = message };
}
