using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using XAHudNavigator.Services;

namespace XAHudNavigator.Windows;

/// <summary>Renders managed menu and inventory snapshots without accessing the game UI.</summary>
internal sealed class UiExplorerView
{
    private readonly UiMenuSnapshotService snapshots;
    private readonly Action<string, uint> openSheet;
    private string menuFilter = string.Empty;
    private string entryFilter = string.Empty;
    private string fieldFilter = string.Empty;
    private string categoryFilter = string.Empty;
    private string inventoryContainerFilter = string.Empty;
    private string message = string.Empty;
    private bool nativeDetail;
    private bool showEmptyInventorySlots = true;
    private int? selectedEntry;
    private UiMenuKey? observedSelection;
    private WorldContext observedContext;
    private static float Scale => ImGuiHelpers.GlobalScale;

    public UiExplorerView(UiMenuSnapshotService snapshots, Action<string, uint> openSheet)
    {
        this.snapshots = snapshots;
        this.openSheet = openSheet;
    }

    public void Draw()
    {
        DrawControls();
        var frame = snapshots.Current;
        Wrapped($"{(frame.IsCurrent ? "Current capture" : "Historical / unavailable")} | Territory {frame.Context.TerritoryId} / Map {frame.Context.MapId}");
        Wrapped(frame.Status);
        if (message.Length > 0) Wrapped(message);

        if (!ImGui.BeginTable("UiExplorerLayout", 2, ImGuiTableFlags.Resizable)) return;
        ImGui.TableSetupColumn("Menus", ImGuiTableColumnFlags.WidthFixed, 240 * Scale);
        ImGui.TableSetupColumn("Menu details", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (ImGui.BeginChild("UiMenuList", new Vector2(0, 0), true)) DrawMenuList(frame);
        ImGui.EndChild();
        ImGui.TableSetColumnIndex(1);
        if (ImGui.BeginChild("UiMenuDetails", new Vector2(0, 0), true)) DrawDetails(snapshots.Current);
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void DrawControls()
    {
        ImGui.BeginDisabled(snapshots.IsRunning);
        if (ImGui.Button("Start capture"))
        {
            showEmptyInventorySlots = true;
            inventoryContainerFilter = string.Empty;
            snapshots.Start();
            snapshots.Select(snapshots.Current.Selection, nativeDetail);
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!snapshots.IsRunning);
        if (ImGui.Button("Stop capture")) snapshots.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Refresh##uiMenus")) snapshots.RequestRefresh();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted(snapshots.IsRunning ? "RUNNING" : "STOPPED");
        ImGui.SameLine();
        if (ImGui.Checkbox("Native inline fields", ref nativeDetail) && snapshots.IsRunning)
            snapshots.Select(snapshots.Current.Selection, nativeDetail);

        var frame = snapshots.Current;
        ImGui.BeginDisabled(frame.CapturedAtUtc == DateTime.MinValue);
        if (ImGui.Button("Copy all JSON##uiMenus")) Copy(UiMenuQuery.Export(frame));
        ImGui.SameLine();
        if (ImGui.Button("Save all JSON##uiMenus")) Save(frame);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled("Exports contain the menu list and selected menu's captured details.");
    }

    private void DrawMenuList(UiMenuFrame frame)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##uiMenuFilter", "Find a menu or address", ref menuFilter, 256);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##uiMenuCategory", categoryFilter.Length == 0 ? "All categories" : categoryFilter))
        {
            if (ImGui.Selectable("All categories", categoryFilter.Length == 0)) categoryFilter = string.Empty;
            var categories = frame.Menus.Select(menu => menu.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            for (var index = 0; index < categories.Length; index++)
            {
                ImGui.PushID(index);
                if (SelectableText(categories[index], categoryFilter == categories[index])) categoryFilter = categories[index];
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }

        var menus = frame.Menus.Where(menu => (categoryFilter.Length == 0 || menu.Category == categoryFilter)
            && (menuFilter.Length == 0 || $"{menu.Key.Name} {menu.Category} 0x{menu.Key.Address:X} {menu.Key.Id}"
                .Contains(menuFilter, StringComparison.OrdinalIgnoreCase))).ToArray();
        ImGui.TextDisabled($"{menus.Length} shown / {frame.Menus.Count} captured");
        if (frame.Menus.Count == 0)
        {
            Wrapped(snapshots.IsRunning ? "No visible menus captured. Open a game menu to inspect it."
                : "Press Start capture, then open a shop or game menu.");
            return;
        }
        if (menus.Length == 0) Wrapped("No menus match these filters.");
        for (var index = 0; index < menus.Length; index++)
        {
            var menu = menus[index];
            ImGui.PushID(index);
            if (SelectableText(menu.Key.Name, frame.Selection == menu.Key) && snapshots.IsRunning)
            {
                selectedEntry = null;
                snapshots.Select(menu.Key, nativeDetail);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"{menu.Category} | ID {menu.Key.Id} | 0x{menu.Key.Address:X} | {menu.NodeCount} nodes");
                if (!snapshots.IsRunning) ImGui.TextUnformatted("Start capture to inspect another menu.");
                ImGui.EndTooltip();
            }
            ImGui.TextDisabled(menu.Category);
            ImGui.PopID();
        }
    }

    private void DrawDetails(UiMenuFrame frame)
    {
        if (observedSelection != frame.Selection || observedContext != frame.Context)
        {
            selectedEntry = null;
            inventoryContainerFilter = string.Empty;
            observedSelection = frame.Selection;
            observedContext = frame.Context;
        }
        if (frame.Detail is not { } detail)
        {
            Wrapped(snapshots.IsRunning ? "Select a menu to capture its entries and raw details."
                : "Capture is stopped. Previously captured details remain available until a new capture replaces them.");
            return;
        }

        ImGui.TextUnformatted(detail.AddonName);
        Wrapped(detail.Title);
        ImGui.TextDisabled($"{detail.Category} | 0x{detail.Address:X} | {detail.Entries.Count} entries");
        Wrapped($"{(detail.IsDecoded ? "Decoded" : "Generic fallback")}{(detail.IsPartial ? " / partial" : string.Empty)}: {detail.Status}");
        if (!detail.IsVisible) ImGui.TextDisabled("This menu was not visible at capture time.");
        if (ImGui.SmallButton("Copy selected JSON##uiMenu"))
            Copy(UiMenuQuery.Export(frame with { Menus = frame.Menus.Where(menu => menu.Key == frame.Selection).ToArray() }));
        ImGui.SameLine();
        ImGui.TextDisabled("Inspection only; selecting a row does not act in the game.");

        var inventory = detail.Category == "Inventory";
        var crystals = inventory && detail.AddonName == "InventoryCrystalGrid";
        var equipment = inventory && detail.AddonName == "Character";
        var characterFields = detail.Category == "Character";
        if (inventory)
        {
            var scope = detail.Fields.FirstOrDefault(field => field.Name == "Scope")?.Value;
            if (!string.IsNullOrWhiteSpace(scope)) Wrapped(scope);
            var grid = detail.Entries.Any(entry => entry.Inventory?.DisplaySlot.HasValue == true);
            Wrapped(equipment
                ? "All active equipped slots belong to the current character. Native indices are zero-based; retired waist storage is available in raw diagnostics."
                : crystals
                ? "Crystal rows use native storage slots with zero-based indices. Named crystals with quantity 0 remain visible when empty slots are hidden."
                : grid
                ? "Captured sorted UI order. Visible slot is the position within this grid; source container/slot identifies actual storage. All indices are zero-based."
                : "Native container/slot order, not current UI sort/page. Slots use native zero-based indices.");
            var containers = detail.Entries.Where(entry => entry.Inventory != null)
                .Select(entry => entry.Inventory!.Container).Distinct(StringComparer.Ordinal).ToArray();
            if (inventoryContainerFilter.Length > 0 && !containers.Contains(inventoryContainerFilter, StringComparer.Ordinal))
                inventoryContainerFilter = string.Empty;
            ImGui.TextUnformatted(crystals || equipment ? "Container" : grid ? "Source container" : "Container / page");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##uiInventoryContainer", inventoryContainerFilter.Length == 0 ? "All containers" : inventoryContainerFilter))
            {
                if (ImGui.Selectable("All containers", inventoryContainerFilter.Length == 0))
                {
                    inventoryContainerFilter = string.Empty;
                    selectedEntry = null;
                }
                for (var index = 0; index < containers.Length; index++)
                {
                    ImGui.PushID(index);
                    if (SelectableText(containers[index], inventoryContainerFilter == containers[index]))
                    {
                        inventoryContainerFilter = containers[index];
                        selectedEntry = null;
                    }
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
            ImGui.Checkbox("Show empty slots##uiInventory", ref showEmptyInventorySlots);
            ImGui.TextDisabled("Filters affect this view; JSON includes all captured slots.");
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##uiEntryFilter", characterFields ? "Find fields or values" : crystals ? "Find crystals, item IDs or native slots"
            : inventory ? "Find items, containers, slots or flags" : "Find text, item IDs or costs", ref entryFilter, 256);
        var entries = detail.Entries.Where(entry => UiMenuQuery.Matches(entry, entryFilter)
            && (!inventory || (showEmptyInventorySlots || entry.Inventory is not { IsEmpty: true })
                && (inventoryContainerFilter.Length == 0 || entry.Inventory?.Container == inventoryContainerFilter))).ToArray();
        ImGui.TextDisabled($"{entries.Length} shown / {detail.Entries.Count} {(inventory ? "slots" : "entries")}; unavailable values are shown as —.");
        if (detail.Entries.Count == 0) Wrapped("No readable entries were captured. Expand the raw details for available data and diagnostics.");
        else if (entries.Length == 0) Wrapped("No entries match the filter.");
        else if (inventory) DrawInventoryEntries(entries, crystals, equipment);
        else if (characterFields) DrawCharacterEntries(entries);
        else DrawEntries(entries);
        var selected = detail.Entries.FirstOrDefault(entry => entry.Index == selectedEntry);
        if (selected != null) DrawEntryDetails(selected);

        ImGui.Separator();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##uiRawFilter", "Filter raw field names, values or sources", ref fieldFilter, 256);
        DrawFields("Menu fields", detail.Fields);
        DrawFields("Raw AtkValues", detail.RawValues);
        if (nativeDetail && ImGui.TreeNode("Native inline fields##uiNativeReport"))
        {
            if (detail.NativeReport.Length == 0)
                Wrapped(snapshots.IsRunning ? "Native details are pending or unavailable; refresh the selected menu."
                    : "Start capture with Native inline fields enabled to collect this report.");
            else
            {
                if (ImGui.SmallButton("Copy native report##uiMenus")) Copy(detail.NativeReport);
                foreach (var line in detail.NativeReport.Split('\n'))
                    if (line.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)) ImGui.TextUnformatted(line.TrimEnd('\r'));
            }
            ImGui.TreePop();
        }
    }

    private unsafe void DrawEntries(IReadOnlyList<UiMenuEntry> entries)
    {
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y * .5f, 110 * Scale, 320 * Scale);
        if (!ImGui.BeginTable("UiMenuEntries", 7,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable |
            ImGuiTableFlags.Reorderable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY, new Vector2(0, height))) return;
        ImGui.TableSetupColumn("Entry", ImGuiTableColumnFlags.WidthFixed, 48 * Scale);
        ImGui.TableSetupColumn("Label", ImGuiTableColumnFlags.WidthFixed, 230 * Scale);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 70 * Scale);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 170 * Scale);
        ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed, 80 * Scale);
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 70 * Scale);
        ImGui.TableSetupColumn("Selected", ImGuiTableColumnFlags.WidthFixed, 70 * Scale);
        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableHeadersRow();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var entry = entries[index];
                ImGui.PushID(index);
                ImGui.TableNextRow();
                Cell(0, entry.Index.ToString(CultureInfo.InvariantCulture));
                if (ImGui.TableSetColumnIndex(1))
                {
                    if (SelectableText(entry.Label.Length == 0 ? "(unnamed entry)" : entry.Label, selectedEntry == entry.Index)) selectedEntry = entry.Index;
                    if (ImGui.IsItemHovered() && entry.Detail.Length > 0)
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(entry.Detail);
                        ImGui.EndTooltip();
                    }
                }
                Cell(2, entry.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "—");
                Cell(3, entry.Cost.Length == 0 ? "—" : entry.Cost);
                Cell(4, entry.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "—");
                Cell(5, entry.Enabled is { } enabled ? enabled ? "Yes" : "No" : "—");
                Cell(6, entry.Selected is { } selected ? selected ? "Yes" : "No" : "—");
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private unsafe void DrawCharacterEntries(IReadOnlyList<UiMenuEntry> entries)
    {
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y * .6f, 110 * Scale, 420 * Scale);
        if (!ImGui.BeginTable("UiCharacterFieldEntries", 4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable |
            ImGuiTableFlags.Reorderable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, height))) return;
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthFixed, 150 * Scale);
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 190 * Scale);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 250 * Scale);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 400 * Scale);
        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableHeadersRow();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var entry = entries[index];
                ImGui.PushID(entry.Index);
                ImGui.TableNextRow();
                Cell(0, entry.Fields.FirstOrDefault(field => field.Name == "Group")?.Value ?? "Character");
                if (ImGui.TableSetColumnIndex(1) && SelectableText(entry.Label, selectedEntry == entry.Index))
                    selectedEntry = entry.Index;
                Cell(2, entry.Detail.Length == 0 ? "—" : entry.Detail);
                var source = entry.Fields.FirstOrDefault(field => field.Name == "Source")?.Value
                    ?? entry.Fields.FirstOrDefault()?.Source;
                Cell(3, string.IsNullOrWhiteSpace(source) ? "—" : source);
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private unsafe void DrawInventoryEntries(IReadOnlyList<UiMenuEntry> entries, bool crystals, bool equipment)
    {
        var grid = !crystals && !equipment && entries.Any(entry => entry.Inventory?.DisplaySlot.HasValue == true);
        var sourceColumn = grid || equipment ? 1 : 0;
        var height = Math.Clamp(ImGui.GetContentRegionAvail().Y * .5f, 110 * Scale, 320 * Scale);
        if (!ImGui.BeginTable(crystals ? "UiInventoryCrystalEntries" : equipment ? "UiEquipmentEntries" : grid ? "UiInventoryGridEntries" : "UiInventoryEntries",
            crystals ? 5 : 8 + sourceColumn,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Hideable |
            ImGuiTableFlags.Reorderable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, height))) return;
        if (grid) ImGui.TableSetupColumn("Visible slot", ImGuiTableColumnFlags.WidthFixed, 90 * Scale);
        else if (equipment) ImGui.TableSetupColumn("Equip slot", ImGuiTableColumnFlags.WidthFixed, 125 * Scale);
        ImGui.TableSetupColumn(grid ? "Source container" : "Container", ImGuiTableColumnFlags.WidthFixed, (grid ? 140 : 120) * Scale);
        ImGui.TableSetupColumn(crystals || equipment ? "Native slot" : grid ? "Source slot" : "Slot", ImGuiTableColumnFlags.WidthFixed, (crystals || equipment || grid ? 85 : 48) * Scale);
        ImGui.TableSetupColumn(crystals ? "Crystal" : "Item", ImGuiTableColumnFlags.WidthFixed, 230 * Scale);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 70 * Scale);
        if (!crystals)
        {
            ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthFixed, 110 * Scale);
            ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthFixed, 120 * Scale);
            ImGui.TableSetupColumn("Spiritbond / collectability (raw)", ImGuiTableColumnFlags.WidthFixed, 230 * Scale);
        }
        ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed, 80 * Scale);
        ImGui.TableSetupScrollFreeze(2 + sourceColumn, 1);
        ImGui.TableHeadersRow();
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var entry = entries[index];
                var slot = entry.Inventory;
                ImGui.PushID(entry.Index);
                ImGui.TableNextRow();
                if (grid) Cell(0, slot?.DisplaySlot?.ToString(CultureInfo.InvariantCulture) ?? "—");
                else if (equipment) Cell(0, entry.Fields.FirstOrDefault(field => field.Name == "Equipment slot")?.Value ?? entry.Detail);
                Cell(sourceColumn, slot?.Container ?? "—");
                Cell(sourceColumn + 1, slot?.Slot.ToString(CultureInfo.InvariantCulture) ?? "—");
                if (ImGui.TableSetColumnIndex(sourceColumn + 2))
                {
                    var label = entry.Label.Length > 0 ? entry.Label : slot is { IsEmpty: true } ? "(empty)" : "(unnamed item)";
                    if (SelectableText(label, selectedEntry == entry.Index)) selectedEntry = entry.Index;
                    if (ImGui.IsItemHovered() && entry.Detail.Length > 0)
                    {
                        ImGui.BeginTooltip();
                        ImGui.TextUnformatted(entry.Detail);
                        ImGui.EndTooltip();
                    }
                }
                Cell(sourceColumn + 3, entry.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "—");
                if (!crystals)
                {
                    Cell(sourceColumn + 4, slot is { Flags.Length: > 0 } ? slot.Flags : "—");
                    Cell(sourceColumn + 5, slot is { IsEmpty: true } ? "—" : slot?.Condition is { } rawCondition
                        ? rawCondition <= 30000
                            ? (rawCondition / 300d).ToString("0.##", CultureInfo.InvariantCulture) + "%"
                            : rawCondition.ToString(CultureInfo.InvariantCulture) + " (raw; out of range)"
                        : "Unavailable");
                    Cell(sourceColumn + 6, slot?.SpiritbondOrCollectability?.ToString(CultureInfo.InvariantCulture) ?? "—");
                }
                Cell(crystals ? 4 : sourceColumn + 7, entry.ItemId?.ToString(CultureInfo.InvariantCulture) ?? "—");
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private void DrawEntryDetails(UiMenuEntry entry)
    {
        ImGui.Separator();
        ImGui.TextUnformatted(entry.Inventory is { } slot
            ? slot.DisplaySlot is { } displaySlot ? $"Visible slot {displaySlot} | Source {slot.Container} / slot {slot.Slot}"
                : $"{slot.Container} / slot {slot.Slot}"
            : $"Entry {entry.Index}");
        Wrapped(entry.Label);
        Wrapped(entry.Detail);
        if (entry.ItemId is > 0)
        {
            var rawId = entry.ItemId.Value;
            var sheetId = UiMenuQuery.ItemSheetId(rawId);
            if (ImGui.SmallButton($"Open Item #{sheetId}")) openSheet("Item", sheetId);
            if (sheetId != rawId) { ImGui.SameLine(); ImGui.TextDisabled($"Raw HQ item ID: {rawId}"); }
        }
        if (entry.IconId.HasValue || entry.NodeId.HasValue || entry.NodeIndex.HasValue)
            ImGui.TextUnformatted($"Icon {entry.IconId?.ToString(CultureInfo.InvariantCulture) ?? "—"} | Node ID {entry.NodeId?.ToString(CultureInfo.InvariantCulture) ?? "—"} | Node index {entry.NodeIndex?.ToString(CultureInfo.InvariantCulture) ?? "—"}");
        DrawFields("Entry fields", entry.Fields);
    }

    private unsafe void DrawFields(string label, IReadOnlyList<UiMenuField> fields)
    {
        if (!ImGui.TreeNode($"{label} ({fields.Count})###{label}")) return;
        var visible = fields.Where(field => field.Name.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)
            || field.Value.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)
            || field.Source.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (visible.Length == 0) ImGui.TextDisabled(fields.Count == 0 ? "No fields captured." : "No fields match the filter.");
        else if (ImGui.BeginTable("UiRawFields", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY, new Vector2(0, Math.Min(260 * Scale, (visible.Length + 2) * ImGui.GetTextLineHeightWithSpacing()))))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 160 * Scale);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 250 * Scale);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 270 * Scale);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(visible.Length);
            while (clipper.Step())
            {
                for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
                {
                    var field = visible[index];
                    ImGui.TableNextRow();
                    Cell(0, field.Name); Cell(1, field.Value); Cell(2, field.Source);
                }
            }
            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }
        ImGui.TreePop();
    }

    private static bool SelectableText(string text, bool selected)
    {
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable("##select", selected, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetTextLineHeight()));
        ImGui.SetCursorPos(start);
        ImGui.TextUnformatted(SingleLine(text));
        return clicked;
    }

    private static void Cell(int column, string text)
    {
        if (!ImGui.TableSetColumnIndex(column)) return;
        ImGui.TextUnformatted(SingleLine(text));
        if (ImGui.IsItemHovered() && (text.Length > 80 || text.Contains('\n') || text.Contains('\r')))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(600 * Scale);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private static string SingleLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static void Wrapped(string text)
    {
        if (text.Length == 0) return;
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private void Copy(string text)
    {
        ImGui.SetClipboardText(text);
        message = "Copied captured menu values.";
    }

    private void Save(UiMenuFrame frame)
    {
        try
        {
            var directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ui-menu-snapshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"menus-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream)) writer.Write(UiMenuQuery.Export(frame));
            message = $"Saved {path}";
        }
        catch (Exception ex) { message = $"Export failed: {ex.Message}"; }
    }
}
