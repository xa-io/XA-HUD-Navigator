using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace XAHudNavigator.Services;

/// <summary>Read-only native boundary for the UI Explorer, invoked exclusively on Framework.Update.</summary>
internal static unsafe class UiMenuCapture
{
    internal const int MaxAddons = 512;
    internal const int MaxRawValues = 4096;
    internal const int MaxFallbackNodes = 2048;
    internal const int MaxFallbackRows = 256;

    public static UiMenuFrame Capture(DateTime now, WorldContext context, UiMenuKey? selected, bool includeNative)
    {
        var stage = AtkStage.Instance();
        if (!Readable(stage) || !Readable(stage->RaptureAtkUnitManager))
            throw new InvalidOperationException("UI stage or addon manager is unavailable.");
        var list = &stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        var rawCount = (int)list->Count;
        var count = Math.Min(Math.Min(rawCount, list->Entries.Length), MaxAddons);
        var menus = new List<UiMenuSummary>();
        var skipped = Math.Max(0, rawCount - count);
        for (var i = 0; i < count; i++)
        {
            var addon = list->Entries[i].Value;
            if (!Readable(addon)) { skipped++; continue; }
            if (!addon->IsVisible) continue;
            var key = ReadKey(addon);
            if (string.IsNullOrEmpty(key.Name)) { skipped++; continue; }
            menus.Add(new(key, Category(key.Name), addon->UldManager.NodeListCount));
        }
        menus.Sort((a, b) =>
        {
            var priority = (a.Category == "Other UI").CompareTo(b.Category == "Other UI");
            return priority != 0 ? priority : StringComparer.Ordinal.Compare(a.Key.Name, b.Key.Name);
        });
        // Never reuse a pointer after the original identity has disappeared.
        var chosen = selected.HasValue ? menus.FirstOrDefault(m => m.Key == selected.Value)
            : menus.FirstOrDefault(m => m.Category != "Other UI") ?? menus.FirstOrDefault();
        UiMenuSnapshot? detail = null;
        if (chosen != null)
        {
            var addon = (AtkUnitBase*)chosen.Key.Address;
            if (!Readable(addon) || !addon->IsVisible || ReadKey(addon) != chosen.Key)
                throw new InvalidOperationException("Selected menu identity changed.");
            detail = CaptureDetail(chosen.Key.Name, addon, now, includeNative);
            detail = EnrichItems(detail);
            if (!Readable(addon) || !addon->IsVisible || ReadKey(addon) != chosen.Key)
                throw new InvalidOperationException("Selected menu changed during capture.");
        }
        var status = menus.Count == 0 ? "No visible game UI found. Open a shop or menu in game."
            : chosen == null ? "Selected menu closed or changed; choose another menu."
            : "Captured visible menus and the selected menu's details.";
        if (skipped > 0) status += $" Partial discovery: {skipped} unavailable or beyond the addon limit.";
        return new(0, now, context, menus.AsReadOnly(), chosen?.Key, detail, status, true);
    }

    internal static UiMenuSnapshot CaptureDetail(string name, AtkUnitBase* addon, DateTime now, bool includeNative,
        Func<string, nint, UiMenuSnapshot?>? inventoryReader = null,
        Func<string, nint, UiMenuSnapshot?>? characterReader = null)
    {
        if (!Readable(addon))
            return new(name, (ulong)addon, Category(name), name, Array.Empty<UiMenuEntry>())
                { CapturedAtUtc = now, IsPartial = true, IsDecoded = false, Status = "Addon memory is unavailable." };
        var snapshot = addon->IsReady
            ? (characterReader == null ? UiCharacterDetailsReader.Capture(name, addon) : characterReader(name, (nint)addon))
                ?? UiMenuReader.Capture(name, addon) ?? UiShopReader.Capture(name, addon)
                ?? (inventoryReader == null
                    ? UiEquipmentReader.Capture(name, addon) ?? UiCrystalInventoryReader.Capture(name, addon)
                        ?? UiInventoryGridReader.Capture(name, addon) ?? UiInventoryReader.Capture(name, addon)
                    : inventoryReader(name, (nint)addon))
                ?? CaptureFallback(name, addon)
            : CaptureFallback(name, addon) with { Status = "Addon setup is incomplete; semantic decoding is unavailable. Raw diagnostics only." };
        if (!snapshot.IsDecoded && snapshot.Entries.Count == 0)
        {
            var fallback = CaptureFallback(name, addon);
            snapshot = snapshot with { Entries = fallback.Entries, Status = snapshot.Status + " " + fallback.Status };
        }
        var rawCount = addon->AtkValuesCount;
        var validRawRange = rawCount == 0 || rawCount <= MaxRawValues && addon->AtkValues != null
            && ClientStructRuntimeInspector.IsReadableRange((nint)addon->AtkValues, (nuint)rawCount * (nuint)sizeof(AtkValue));
        var raw = validRawRange ? AddonInspector.ScanAtkValues(addon)
            .Select(v => new UiMenuField($"[{v.Index}] {v.TypeName}", v.Value, $"AtkValues[{v.Index}] @ 0x{v.Address:X}"))
            .ToArray() : Array.Empty<UiMenuField>();
        var nativeReport = string.Empty;
        if (includeNative)
        {
            var native = ClientStructRuntimeInspector.ResolveAddonStruct(name, (nint)addon);
            nativeReport = ClientStructRuntimeInspector.BuildStructReport(native.IsResolved ? native
                : ClientStructRuntimeInspector.ResolveBaseAddonStruct((nint)addon));
        }
        return snapshot with { CapturedAtUtc = now, IsVisible = addon->IsVisible, RawValues = raw,
            IsPartial = snapshot.IsPartial || !validRawRange,
            Status = snapshot.Status + (!validRawRange ? $" Raw values unavailable: count {rawCount}, limit {MaxRawValues}, or unreadable range." : string.Empty),
            Fields = snapshot.Fields.Concat(new[]
            {
                new UiMenuField("Addon ID", addon->Id.ToString(CultureInfo.InvariantCulture), "AtkUnitBase.Id"),
                new UiMenuField("Ready", addon->IsReady.ToString(), "AtkUnitBase.IsReady (OnSetup completed)"),
                new UiMenuField("Node count", addon->UldManager.NodeListCount.ToString(CultureInfo.InvariantCulture), "AtkUnitBase.UldManager.NodeListCount"),
                new UiMenuField("AtkValue count", rawCount.ToString(CultureInfo.InvariantCulture), "AtkUnitBase.AtkValuesCount"),
            }).ToArray(), NativeReport = nativeReport };
    }

    internal static UiMenuSnapshot CaptureFallback(string name, AtkUnitBase* addon)
    {
        var entries = new List<UiMenuEntry>();
        var status = "Generic top-level text preview; menu roles, item IDs and actions are not decoded.";
        if (!Readable(addon)) status += " Addon memory is unavailable.";
        else if (addon->UldManager.NodeListCount > MaxFallbackNodes)
            status += $" Node count exceeds the {MaxFallbackNodes}-slot preview limit.";
        else
        {
            var nodes = AddonInspector.ScanNodes(addon);
            foreach (var node in nodes.Where(n => n.IsVisible && !string.IsNullOrWhiteSpace(n.Text)).Take(MaxFallbackRows))
                entries.Add(new(node.Index, node.Text, node.TypeDisplay)
                {
                    NodeId = node.NodeId, NodeIndex = node.Index,
                    Fields = new[] { new UiMenuField("Text", node.Text, $"UldManager.NodeList[{node.Index}], node #{node.NodeId}"),
                        new UiMenuField("Address", $"0x{node.Address:X}", "AtkResNode") },
                });
            if (nodes.Count(n => n.IsVisible && !string.IsNullOrWhiteSpace(n.Text)) > MaxFallbackRows)
                status += $" Text preview capped at {MaxFallbackRows} rows.";
            if (entries.Count == 0) status += " No readable visible top-level text; inspect raw values or native fields.";
        }
        return new(name, (ulong)addon, Category(name), name, entries.AsReadOnly())
            { IsDecoded = false, IsPartial = true, Status = status };
    }

    private static UiMenuSnapshot EnrichItems(UiMenuSnapshot snapshot)
    {
        return EnrichItems(snapshot, id =>
        {
            var item = Plugin.DataManager.GetExcelSheet<Item>()?.GetRowOrDefault(id);
            return item.HasValue ? (item.Value.Name.ToString(), item.Value.Icon) : null;
        });
    }

    internal static UiMenuSnapshot EnrichItems(UiMenuSnapshot snapshot, Func<uint, (string Name, uint Icon)?> lookup)
    {
        var entries = new List<UiMenuEntry>(snapshot.Entries.Count);
        var unavailable = false;
        foreach (var entry in snapshot.Entries)
        {
            if (!entry.ItemId.HasValue || !string.IsNullOrWhiteSpace(entry.Label)) { entries.Add(entry); continue; }
            (string Name, uint Icon)? item = null;
            try { item = lookup(UiMenuQuery.ItemSheetId(entry.ItemId.Value)); }
            catch (Exception ex) { DiagnosticLog.VerboseThrottled("UiMenuCapture.ItemName", ex); }
            if (!item.HasValue || string.IsNullOrWhiteSpace(item.Value.Name)) unavailable = true;
            var label = string.IsNullOrWhiteSpace(item?.Name) ? $"Item #{entry.ItemId.Value}" : item.Value.Name;
            var sourceFields = entry.Fields.ToList();
            sourceFields.Add(new("Item name", label, string.IsNullOrWhiteSpace(item?.Name)
                ? "HUD Navigator fallback from the captured raw item ID; sheet name unavailable"
                : $"Lumina Item.Name, row {UiMenuQuery.ItemSheetId(entry.ItemId.Value)}"));
            if (!entry.IconId.HasValue && item.HasValue)
                sourceFields.Add(new("Item icon", item.Value.Icon.ToString(CultureInfo.InvariantCulture),
                    $"Lumina Item.Icon, row {UiMenuQuery.ItemSheetId(entry.ItemId.Value)}"));
            entries.Add(entry with { Label = label, IconId = entry.IconId ?? item?.Icon, Fields = sourceFields.ToArray() });
        }
        return snapshot with { Entries = entries.AsReadOnly(),
            Status = snapshot.Status + (unavailable ? " Some item names are unavailable; raw item IDs are retained." : string.Empty) };
    }

    internal static string Category(string name) => name switch
    {
        "Shop" or "ShopExchangeCurrency" or "ShopExchangeItem" or "InclusionShop" or "GrandCompanyExchange"
            or "FreeCompanyCreditShop" or "FreeCompanyExchange" => "Shop",
        "SelectString" or "SelectIconString" or "ContextMenu" => "Selection",
        "SelectYesno" => "Confirmation",
        "Talk" => "Dialogue",
        "CharacterStatus" or "CharacterProfile" => "Character",
        "Inventory" or "InventoryLarge" or "InventoryExpansion" or "ArmouryBoard" or "InventoryBuddy" or "InventoryBuddy2"
            or "InventoryGrid0E" or "InventoryGrid1E" or "InventoryGrid2E" or "InventoryGrid3E" or "InventoryCrystalGrid" or "Character" => "Inventory",
        _ => "Other UI",
    };

    internal static UiMenuKey ReadKey(AtkUnitBase* addon)
    {
        var bytes = addon->Name;
        var end = bytes.IndexOf((byte)0);
        return new(Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]), (ulong)addon, addon->Id);
    }

    private static bool Readable<T>(T* pointer) where T : unmanaged
        => pointer != null && ClientStructRuntimeInspector.IsReadableRange((nint)pointer, (nuint)sizeof(T));
}
