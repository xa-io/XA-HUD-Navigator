using System;
using System.Collections.Generic;
using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XAHudNavigator.Services;

/// <summary>Copies the currently displayed shop rows without invoking shop callbacks or changing tabs.</summary>
internal static unsafe class UiShopReader
{
    private const int MaximumValues = 4096;

    public static UiMenuSnapshot? Capture(string addonName, AtkUnitBase* addon)
    {
        if (addonName is not ("Shop" or "ShopExchangeCurrency" or "InclusionShop" or
            "GrandCompanyExchange" or "FreeCompanyCreditShop" or "FreeCompanyExchange"))
            return null;
        if (!Readable(addon))
            return Partial(addonName, addon, "The shop addon pointer is unavailable or unreadable.");
        if (!addon->IsVisible)
            return Partial(addonName, addon, "The shop addon is hidden; no current rows were decoded.");

        try
        {
            switch (addonName)
            {
                case "Shop":
                    var proxy = ShopEventHandler.AgentProxy.Instance();
                    return Readable(proxy) && proxy->AddonId == addon->Id
                        ? CaptureGilForAddon(addon, proxy->Handler)
                        : Partial(addonName, addon, "The gil shop handler is unavailable or belongs to another addon.");
                case "ShopExchangeCurrency":
                    return CaptureCurrency(addon, AgentShop.Instance());
                case "InclusionShop":
                    return CaptureInclusion(addon);
                case "GrandCompanyExchange":
                    return CaptureGrandCompany(addon);
                case "FreeCompanyCreditShop":
                    return CaptureFreeCompany(addon);
                default:
                    return Partial(addonName, addon,
                        "Company action decoding is unavailable: ADS uses number array 61, while installed ClientStructs names FreeCompanyExchange as 52. The mapping is unverified; inspect text and raw values.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("UiShopReader.Capture", ex);
            return Partial(addonName, addon, $"Shop decoding is unavailable: {ex.GetType().Name}.");
        }
    }

    internal static UiMenuSnapshot CaptureGilForAddon(AtkUnitBase* addon, ShopEventHandler* handler)
    {
        var shop = (AddonShop*)addon;
        if (!Readable(shop) || !Readable(handler))
            return Partial("Shop", addon, "The shop list or handler is unavailable or unreadable.");
        var list = handler->BuybackTabActive ? shop->BuybackList : shop->BuyList;
        if (!Readable(list) || !Readable(list->OwnerNode))
            return Partial("Shop", addon, "The current buy or buyback list could not be corroborated; sell rows are not decoded.");
        var node = &list->OwnerNode->AtkResNode;
        var seen = new HashSet<nint>();
        while (node != null)
        {
            if (!Readable(node) || seen.Count >= 64 || !seen.Add((nint)node))
                return Partial("Shop", addon, "Shop list visibility has an unreadable, cyclic or excessive parent chain.");
            if ((node->NodeFlags & NodeFlags.Visible) == 0)
                return Partial("Shop", addon, "The buy or buyback list is hidden; sell rows are not decoded.");
            node = node->ParentNode;
        }
        return CaptureGil(addon, handler);
    }

    internal static UiMenuSnapshot CaptureGil(AtkUnitBase* addon, ShopEventHandler* handler)
    {
        const string name = "Shop";
        if (!Readable(addon) || !Readable(handler))
            return Partial(name, addon, "The gil shop handler is unavailable or unreadable.");
        var rows = new List<UiMenuEntry>();
        var fields = new List<UiMenuField>
        {
            Field("Shop ID", handler->Info.EventId.Id, "ShopEventHandler.Info.EventId"),
            Field("Mode", handler->CurrentMode, "ShopEventHandler.CurrentMode"),
            Field("Buyback tab active", handler->BuybackTabActive ? 1 : 0, "ShopEventHandler.BuybackTabActive"),
        };
        // ClientStructs documents 0=none, 1=normal and 2=buyback. The production wrapper also verifies list visibility.
        if (handler->CurrentMode != (handler->BuybackTabActive ? 2 : 1))
            return Partial(name, addon, "Shop mode is inactive, unsupported or inconsistent with its buyback flag.", fields: fields);
        if (handler->BuybackTabActive)
        {
            if (handler->BuybackCount is < 0 or > 10)
                return Partial(name, addon, "The gil shop buyback count is outside its 10-row buffer.");
            for (var row = 0; row < handler->BuybackCount; row++)
            {
                ref var item = ref handler->Buyback[row];
                if (item.ItemId == 0 || item.Price < 0 || item.Quantity < 1)
                    return Partial(name, addon, $"Buyback row {row} contains an invalid item, quantity or price.", rows, fields);
                rows.Add(new UiMenuEntry(row, ItemName(item.ItemId), "Buyback item")
                {
                    ItemId = item.ItemId,
                    Quantity = (uint)item.Quantity,
                    Cost = $"{Number(item.Price)} gil",
                    Fields = new[]
                    {
                        Field("Item ID", item.ItemId, "ShopEventHandler.Buyback.ItemId"),
                        Field("Quantity", item.Quantity, "ShopEventHandler.Buyback.Quantity"),
                        Field("Price", item.Price, "ShopEventHandler.Buyback.Price"),
                        Field("Owned", item.NumOwned, "ShopEventHandler.Buyback.NumOwned"),
                    },
                });
            }
            return Snapshot(name, addon, "Buyback", rows, fields);
        }
        if (handler->ItemsCount is < 0 or > 60 || handler->VisibleItemsCount < 0 ||
            handler->VisibleItemsCount > handler->ItemsCount)
            return Partial(name, addon, "The gil shop item counts are outside their 60-row buffers.");
        var seen = new HashSet<int>();
        var nameIssues = false;
        for (var row = 0; row < handler->VisibleItemsCount; row++)
        {
            var index = handler->VisibleItems[row];
            if (index < 0 || index >= handler->ItemsCount || !seen.Add(index))
                return Partial(name, addon, $"Gil shop row {row} has an invalid or duplicate item index.", rows, fields);
            ref var item = ref handler->Items[index];
            if (item.ItemId == 0 || item.PriceBuy < 0)
                return Partial(name, addon, $"Gil shop row {row} contains an invalid item or price.", rows, fields);
            var namePointer = (nint)item.ItemName.StringPtr.Value;
            var decodeIssue = string.Empty;
            var rawNameHex = string.Empty;
            var itemName = namePointer == 0 ? string.Empty
                : UiMenuReader.ReadText(namePointer, out decodeIssue, out rawNameHex, plainLabel: true);
            if (decodeIssue.Length != 0 || string.IsNullOrWhiteSpace(itemName) || itemName.StartsWith('<'))
                itemName = ItemLabel(item.ItemId);
            var rowFields = new List<UiMenuField>
            {
                Field("Item buffer index", index, "ShopEventHandler.VisibleItems"),
                Field("Item ID", item.ItemId, "ShopEventHandler.Items.ItemId"),
                Field("Price", item.PriceBuy, "ShopEventHandler.Items.PriceBuy"),
                Field("Owned", item.NumOwned, "ShopEventHandler.Items.NumOwned"),
                Field("Stack size", item.StackSize, "ShopEventHandler.Items.StackSize"),
                Field("High quality", item.IsHQ ? 1 : 0, "ShopEventHandler.Items.IsHQ"),
            };
            var nameSource = $"ShopEventHandler.Items[{index}].ItemName @ 0x{namePointer:X}";
            if (namePointer != 0)
                rowFields.Add(new("Raw item name bytes", rawNameHex, nameSource + " (captured SeString hex; terminator excluded)"));
            if (decodeIssue.Length != 0)
            {
                rowFields.Add(new("Item name decoding", decodeIssue, nameSource));
                nameIssues = true;
            }
            rows.Add(new UiMenuEntry(row, itemName, item.IsHQ ? "High quality" : "Normal quality")
            {
                ItemId = item.ItemId,
                Quantity = 1,
                Cost = $"{Number(item.PriceBuy)} gil",
                Fields = rowFields,
            });
        }
        var snapshot = Snapshot(name, addon, "Gil shop", rows, fields);
        return nameIssues ? snapshot with
        {
            IsPartial = true,
            Status = "Current shop rows decoded; some native item names could not be decoded. Readable fallback names are shown; captured bytes and decoding issues are retained in entry fields.",
        } : snapshot;
    }

    internal static UiMenuSnapshot CaptureCurrency(AtkUnitBase* addon, AgentShop* agent)
    {
        const string name = "ShopExchangeCurrency";
        if (!Values(addon, 88, out var values) || !Unsigned(values[4], out var count) || count > 122 ||
            !Unsigned(values[87], out var currencyIcon))
            return Partial(name, addon, "Currency shop header is missing, mistyped or outside its 122-row limit.");
        if (!Values(addon, 1310 + (int)count, out values))
            return Partial(name, addon, "Currency shop rows extend beyond the available AtkValue buffer.");
        var fields = new[] { Field("Currency icon ID", currencyIcon, "AtkValues[87]") };
        var rows = new List<UiMenuEntry>();
        var agentReady = Readable(agent) && Readable(addon) && agent->AddonId == addon->Id &&
            agent->ItemReceiveCount is > 0 and <= 122 && agent->ItemReceive != null &&
            ClientStructRuntimeInspector.IsReadableRange((nint)agent->ItemReceive,
                (nuint)(agent->ItemReceiveCount * sizeof(AgentShop.ShopItem)));
        var ordinaryLayout = agentReady && agent->ItemReceiveCount == count;
        var certificateLayout = agentReady && Unsigned(values[3], out var totalRows) && totalRows == agent->ItemReceiveCount;
        var partial = !agentReady;
        var seen = new HashSet<uint>();
        for (var row = 0; row < count; row++)
        {
            if (!Unsigned(values[1066 + row], out var itemId) || itemId == 0 ||
                !Unsigned(values[456 + row], out var price) ||
                !Unsigned(values[1310 + row], out var index) || index >= 122 || !seen.Add(index))
                return Partial(name, addon, $"Currency shop row {row} has an invalid item, price or row index.", rows, fields);
            uint? quantity = null;
            uint? icon = null;
            var label = ItemName(itemId);
            // ADS maps ordinary shops by visible row and certificate shops by callback index.
            // Without its selected-offer context, publish bundle data only when every feasible mapping agrees.
            if (ordinaryLayout || certificateLayout)
            {
                var ordinary = ordinaryLayout ? agent->ItemReceive[row] : default;
                var certificateInRange = certificateLayout && index < agent->ItemReceiveCount;
                var certificate = certificateInRange ? agent->ItemReceive[index] : default;
                var ordinaryValid = ordinaryLayout && ordinary.ItemId == itemId && ordinary.ItemCount > 0;
                var certificateValid = certificateInRange && certificate.ItemId == itemId && certificate.ItemCount > 0;
                var compatible = (!ordinaryLayout || ordinaryValid) && (!certificateLayout || certificateValid) &&
                    (!ordinaryLayout || !certificateLayout ||
                        (ordinary.ItemCount == certificate.ItemCount && ordinary.ItemIconId == certificate.ItemIconId));
                if (compatible)
                {
                    var item = ordinaryLayout ? ordinary : certificate;
                    quantity = item.ItemCount;
                    icon = item.ItemIconId;
                }
            }
            if (quantity == null)
                partial = true;
            rows.Add(new UiMenuEntry(row, label, "Currency exchange")
            {
                ItemId = itemId,
                IconId = icon,
                Quantity = quantity,
                Cost = $"{Number(price)} currency (icon {Number(currencyIcon)})",
                Fields = new[]
                {
                    Field("Item ID", itemId, $"AtkValues[{1066 + row}]"),
                    Field("Price", price, $"AtkValues[{456 + row}]"),
                    Field("Native row index", index, $"AtkValues[{1310 + row}]"),
                    new UiMenuField("Receive quantity", quantity.HasValue ? Number(quantity.Value) : "Unavailable",
                        "AgentShop.ItemReceive (visible-row / certificate-index interpretations corroborated)"),
                },
            });
        }
        var result = Snapshot(name, addon, "Currency exchange", rows, fields);
        return result with
        {
            IsPartial = partial,
            Status = partial
                ? "Visible item IDs and prices decoded. Receive quantities could not all be corroborated with the current AgentShop rows. Currency icon is shown without guessing its item identity."
                : "Visible rows decoded. Currency icon is shown without guessing its item identity.",
        };
    }

    internal static UiMenuSnapshot CaptureInclusion(AtkUnitBase* addon)
    {
        const string name = "InclusionShop";
        if (!Values(addon, 299, out var values) || !Unsigned(values[298], out var count) || count > 60)
            return Partial(name, addon, "InclusionShop item count is missing, mistyped or outside its 60-row limit.");
        // Installed ClientStructs: first row at value 299, 18 AtkValues per row.
        if (!Values(addon, 299 + (int)count * 18, out values))
            return Partial(name, addon, "InclusionShop rows extend beyond the available AtkValue buffer.");
        var rows = new List<UiMenuEntry>();
        for (var row = 0; row < count; row++)
        {
            var offset = 299 + row * 18;
            if (!Unsigned(values[offset + 1], out var itemId) || itemId == 0 ||
                !Unsigned(values[offset + 2], out var icon) ||
                !Unsigned(values[offset + 3], out var quantity) || quantity == 0 ||
                !Unsigned(values[offset + 5], out var costs) || costs > 3)
                return Partial(name, addon, $"InclusionShop row {row} contains an invalid item, quantity or cost count.", rows);
            var costParts = new List<string>();
            for (var cost = 0; cost < costs; cost++)
            {
                if (!Unsigned(values[offset + 6 + cost], out var costId) || costId == 0 ||
                    !Unsigned(values[offset + 12 + cost], out var amount))
                    return Partial(name, addon, $"InclusionShop row {row}, cost {cost} has an invalid item or amount.", rows);
                costParts.Add($"{Number(amount)} × {ItemLabel(costId)} (#{Number(costId)})");
            }
            rows.Add(new UiMenuEntry(row, ItemName(itemId), "Current page and subpage")
            {
                ItemId = itemId,
                IconId = icon,
                Quantity = quantity,
                Cost = string.Join(" + ", costParts),
                Fields = NumericFields(values, ("Item ID", offset + 1), ("Quantity", offset + 3), ("Owned", offset + 4), ("Maximum amount", offset + 15),
                    ("Flags", offset + 16), ("Native row index", offset + 17)),
            });
        }
        return Snapshot(name, addon, "Inclusion shop", rows,
            NumericFields(values, ("Pinned currency icon", 296), ("Pinned currency count", 297)));
    }

    internal static UiMenuSnapshot CaptureGrandCompany(AtkUnitBase* addon)
    {
        const string name = "GrandCompanyExchange";
        // These ADS/ECommons parallel arrays have 50 entries; reject overlap instead of clamping.
        if (!Values(addon, 2, out var values) || !Unsigned(values[1], out var count) || count > 50)
            return Partial(name, addon, "Grand Company item count is missing, mistyped or outside the 50-row layout.");
        if (!Values(addon, 417 + (int)count, out values))
            return Partial(name, addon, "Grand Company rows extend beyond the available AtkValue buffer.");
        var rows = new List<UiMenuEntry>();
        for (var row = 0; row < count; row++)
        {
            if (!Unsigned(values[317 + row], out var itemId) || itemId == 0 ||
                !Unsigned(values[67 + row], out var price) || !Unsigned(values[417 + row], out var rank))
                return Partial(name, addon, $"Grand Company row {row} has an invalid item, seal price or rank.", rows);
            rows.Add(new UiMenuEntry(row, ItemName(itemId), $"Required rank: {Number(rank)}")
            {
                ItemId = itemId,
                Cost = $"{Number(price)} company seals",
                Fields = new[]
                {
                    Field("Item ID", itemId, $"AtkValues[{317 + row}]"),
                    Field("Price", price, $"AtkValues[{67 + row}]"),
                    Field("Required rank", rank, $"AtkValues[{417 + row}]"),
                },
            });
        }
        return Snapshot(name, addon, "Grand Company exchange", rows);
    }

    internal static UiMenuSnapshot CaptureFreeCompany(AtkUnitBase* addon)
    {
        const string name = "FreeCompanyCreditShop";
        if (!Values(addon, 10, out var values) || !Unsigned(values[9], out var count) || count > 20 ||
            !Unsigned(values[0], out var currentRank) || !Unsigned(values[3], out var credits))
            return Partial(name, addon, "Free Company shop header is missing, mistyped or outside its 20-row limit.");
        if (!Values(addon, 130 + (int)count, out values))
            return Partial(name, addon, "Free Company rows extend beyond the available AtkValue buffer.");
        var fields = new[] { Field("Company rank", currentRank, "AtkValues[0]"), Field("Company credits", credits, "AtkValues[3]") };
        var rows = new List<UiMenuEntry>();
        for (var row = 0; row < count; row++)
        {
            if (!Unsigned(values[30 + row], out var itemId) || itemId == 0 ||
                !Unsigned(values[70 + row], out var rank) || !Unsigned(values[130 + row], out var price) ||
                !Integer(values[110 + row], out var maximum))
                return Partial(name, addon, $"Free Company row {row} has an invalid item, price, rank or maximum quantity.", rows, fields);
            rows.Add(new UiMenuEntry(row, ItemName(itemId), $"Required rank: {Number(rank)}; maximum quantity: {Number(maximum)}")
            {
                ItemId = itemId,
                Cost = $"{Number(price)} company credits",
                Fields = new[]
                {
                    Field("Item ID", itemId, $"AtkValues[{30 + row}]"),
                    Field("Price", price, $"AtkValues[{130 + row}]"),
                    Field("Required rank", rank, $"AtkValues[{70 + row}]"),
                    Field("Maximum quantity", maximum, $"AtkValues[{110 + row}]"),
                },
            });
        }
        return Snapshot(name, addon, "Free Company credit shop", rows, fields);
    }

    private static bool Readable<T>(T* pointer) where T : unmanaged
        => pointer != null && ClientStructRuntimeInspector.IsReadableRange((nint)pointer, (nuint)sizeof(T));

    private static bool Values(AtkUnitBase* addon, int minimum, out AtkValue* values)
    {
        values = null;
        if (!Readable(addon) || addon->AtkValuesCount < minimum || addon->AtkValuesCount > MaximumValues || addon->AtkValues == null ||
            !ClientStructRuntimeInspector.IsReadableRange((nint)addon->AtkValues, (nuint)(addon->AtkValuesCount * sizeof(AtkValue))))
            return false;
        values = addon->AtkValues;
        return true;
    }

    private static bool Integer(AtkValue value, out long number)
    {
        switch (value.Type)
        {
            case AtkValueType.Int: number = value.Int; return true;
            case AtkValueType.UInt: number = value.UInt; return true;
            case AtkValueType.Int64: number = value.Int64; return true;
            case AtkValueType.UInt64 when value.UInt64 <= long.MaxValue: number = (long)value.UInt64; return true;
            default: number = 0; return false;
        }
    }

    private static bool Unsigned(AtkValue value, out uint number)
    {
        if (Integer(value, out var integer) && integer is >= 0 and <= uint.MaxValue)
        {
            number = (uint)integer;
            return true;
        }
        number = 0;
        return false;
    }

    private static UiMenuField[] NumericFields(AtkValue* values, params (string Name, int Index)[] fields)
    {
        var result = new UiMenuField[fields.Length];
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            result[index] = new UiMenuField(field.Name, Integer(values[field.Index], out var number) ? Number(number) : "Unavailable (unexpected value type)",
                $"AtkValues[{field.Index}]");
        }
        return result;
    }

    private static UiMenuField Field(string name, long value, string source) => new(name, Number(value), source);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string ItemName(uint id)
    {
        try
        {
            if (Plugin.DataManager != null && Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(UiMenuQuery.ItemSheetId(id), out var item))
            {
                var label = item.Name.ToString();
                if (!string.IsNullOrWhiteSpace(label))
                    return label;
            }
        }
        catch (Exception ex) { DiagnosticLog.VerboseThrottled("UiShopReader.ItemName", ex); }
        return string.Empty;
    }

    private static string ItemLabel(uint id)
    {
        var name = ItemName(id);
        return name.Length == 0 ? $"Item #{Number(id)}" : name;
    }

    private static UiMenuSnapshot Snapshot(string name, AtkUnitBase* addon, string title, IReadOnlyList<UiMenuEntry> rows,
        IReadOnlyList<UiMenuField>? fields = null)
        => new(name, (ulong)addon, "Shop", title, rows)
        {
            CapturedAtUtc = DateTime.UtcNow,
            IsVisible = Readable(addon) && addon->IsVisible,
            Fields = fields ?? Array.Empty<UiMenuField>(),
            Status = rows.Count == 0 ? "No shop rows are currently populated." : "Current shop rows decoded; no interaction was performed.",
        };

    private static UiMenuSnapshot Partial(string name, AtkUnitBase* addon, string reason,
        IReadOnlyList<UiMenuEntry>? rows = null, IReadOnlyList<UiMenuField>? fields = null)
        => Snapshot(name, addon, name, rows ?? Array.Empty<UiMenuEntry>(), fields) with
        {
            IsDecoded = rows is { Count: > 0 },
            IsPartial = true,
            Status = reason,
        };
}
