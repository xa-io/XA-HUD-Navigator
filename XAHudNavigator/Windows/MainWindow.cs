using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using XAHudNavigator.Services;

namespace XAHudNavigator.Windows;

/// <summary>
/// Main window for XA HUD Navigator v0.0.0.2
/// Layout: Tabs [Addons | Sheets] with 3-column addon view:
///   Left: addon list (all addons, visible highlighted green, focused = bright green)
///   Center: node table
///   Right: node detail panel (fixed, top-right — not buried at bottom)
/// Click any line to copy. Click-track mode to discover interactions.
/// </summary>
public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string PluginVersion = BuildInfo.Version;

    // Cached addon scan results
    private List<AddonInfo> cachedAddons = new();
    private DateTime lastRefresh = DateTime.MinValue;
    private string searchFilter = string.Empty;
    private string? selectedAddonName;
    private int selectedNodeIndex = -1;

    // Logging mode — detects in-game addon appear/change events
    private bool clickTrackMode;
    private readonly List<string> clickLog = new();
    private HashSet<string> previousVisibleAddons = new();
    private bool clickTrackFrameworkHooked;
    private bool switchToLoggingTab;
    private bool switchToDebugTab;
    private bool switchToSheetsTab;
    private string debugFilter = string.Empty;
    private bool debugMeaningfulOnly = true;
    private bool debugInteractiveOnly;
    private bool debugTextOnly;
    private bool debugVisibleOnly;
    private AddonDebugSnapshot? cachedDebugSnapshot;
    private string? cachedDebugAddonName;

    // Hover state — which node row is hovered in the table (for overlay pulsation)
    private int hoveredNodeIndex = -1;

    // Sheets tab
    private readonly ExdSchemaService exdSchemaService = new();
    private readonly ClientStructsSheetService clientStructsSheetService = new();
    private string sheetSearchFilter = string.Empty;
    private string? selectedSheetName;
    private List<string>? cachedSheetNames;
    private uint sheetPreviewRowId;
    private int sheetPreviewStartIndex;
    private string sheetPreviewText = string.Empty;
    private string sheetSchemaStatus = "EXDSchema not loaded yet.";
    private bool preferSchemaFormatting = true;
    private bool includeSchemaOffsets = true;
    private bool includeSchemaComments = true;
    private int sheetPreviewRowCount = 20;
    private string sheetPreviewMessage = string.Empty;
    private List<SheetPreviewColumn> sheetPreviewColumns = new();
    private List<SheetPreviewRow> sheetPreviewRows = new();
    private int selectedSheetPreviewRow = -1;
    private int selectedSheetPreviewColumn = -1;
    private string clientStructsSheetSearchFilter = string.Empty;
    private string? selectedClientStructsSheetName;
    private ClientStructsSheetSnapshot? clientStructsSheetSnapshot;
    private DateTime clientStructsSheetLastRefresh = DateTime.MinValue;
    private bool clientStructsAutoRefresh = true;
    private InventoryType clientStructsInventoryType = InventoryType.Inventory1;
    private int clientStructsPreviewStartIndex;
    private int clientStructsPreviewRowCount = 20;
    private int selectedClientStructsPreviewRow = -1;
    private int selectedClientStructsPreviewColumn = -1;
    private static readonly InventoryType[] ClientStructsInventoryTypes = Enum.GetValues<InventoryType>();

    // Copied feedback
    private string copiedFeedback = string.Empty;
    private DateTime copiedExpiry = DateTime.MinValue;

    public MainWindow(Plugin plugin)
        : base("XA HUD Navigator##XAHudNavigator", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(800, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
    }

    public void Dispose()
    {
        if (clickTrackFrameworkHooked)
        {
            Plugin.Framework.Update -= OnClickTrackTick;
            clickTrackFrameworkHooked = false;
        }
    }

    private void CopyAndNotify(string text, string label = "")
    {
        ImGui.SetClipboardText(text);
        copiedFeedback = string.IsNullOrEmpty(label) ? $"Copied: {(text.Length > 60 ? text[..60] + "..." : text)}" : $"Copied: {label}";
        copiedExpiry = DateTime.UtcNow.AddSeconds(3);
    }

    public override void Draw()
    {
        // Auto-refresh
        var now = DateTime.UtcNow;
        if ((float)(now - lastRefresh).TotalSeconds >= plugin.Configuration.RefreshInterval)
        {
            cachedAddons = AddonInspector.ScanAllAddons();
            lastRefresh = now;
        }

        // ── Top bar ──
        DrawTopBar();

        // ── Tabs ──
        if (ImGui.BeginTabBar("MainTabs"))
        {
            if (ImGui.BeginTabItem("Addons"))
            {
                DrawAddonsTab();
                ImGui.EndTabItem();
            }
            if (plugin.Configuration.ShowSheetsTab)
            {
                var flags = switchToSheetsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (ImGui.BeginTabItem("Sheets", flags))
                {
                    DrawSheetsTab();
                    ImGui.EndTabItem();
                }
                switchToSheetsTab = false;
            }
            if (clickTrackMode)
            {
                var flags = switchToLoggingTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (ImGui.BeginTabItem("Logging", flags))
                {
                    DrawLoggingTab();
                    ImGui.EndTabItem();
                }
                switchToLoggingTab = false;
            }
            if (plugin.Configuration.ShowDebugTab)
            {
                var flags = switchToDebugTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                if (ImGui.BeginTabItem("Debug", flags))
                {
                    DrawDebugTab();
                    ImGui.EndTabItem();
                }
                switchToDebugTab = false;
            }
            ImGui.EndTabBar();
        }

        // ── Status bar ──
        ImGui.Separator();
        DrawStatusBar();
    }

    // ═══════════════════════════════════════════════════
    //  Top Bar
    // ═══════════════════════════════════════════════════
    private void DrawTopBar()
    {
        var overlayEnabled = plugin.Configuration.OverlayEnabled;
        if (ImGui.Checkbox("Overlay", ref overlayEnabled))
        {
            plugin.Configuration.OverlayEnabled = overlayEnabled;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw outlines around the selected addon's nodes on the game screen.");

        ImGui.SameLine();
        if (ImGui.Checkbox("Logging", ref clickTrackMode))
        {
            if (clickTrackMode)
            {
                clickLog.Add($"[{DateTime.Now:HH:mm:ss}] Logging enabled — interact with game UI to log events");
                // Snapshot current visible addons and hook Framework for change detection
                previousVisibleAddons = new HashSet<string>(cachedAddons.Where(a => a.IsVisible).Select(a => a.Name));
                if (!clickTrackFrameworkHooked)
                {
                    Plugin.Framework.Update += OnClickTrackTick;
                    clickTrackFrameworkHooked = true;
                }
                // Auto-switch to Logging tab
                switchToLoggingTab = true;
            }
            else
            {
                if (clickTrackFrameworkHooked)
                {
                    Plugin.Framework.Update -= OnClickTrackTick;
                    clickTrackFrameworkHooked = false;
                }
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enable to detect when game UI addons appear, disappear, or gain focus.\nOpens the Logging tab to view events.");

        ImGui.SameLine();
        var showDebugTab = plugin.Configuration.ShowDebugTab;
        if (ImGui.Checkbox("Debugging", ref showDebugTab))
        {
            plugin.Configuration.ShowDebugTab = showDebugTab;
            plugin.Configuration.Save();
            if (showDebugTab)
            {
                switchToDebugTab = true;
                RefreshDebugSnapshot();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the Debug tab with recursive addon dumps, text extraction, and copy helpers for the selected addon.");

        ImGui.SameLine();
        var showSheetsTab = plugin.Configuration.ShowSheetsTab;
        if (ImGui.Checkbox("Sheets", ref showSheetsTab))
        {
            plugin.Configuration.ShowSheetsTab = showSheetsTab;
            plugin.Configuration.Save();
            if (showSheetsTab)
                switchToSheetsTab = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the Sheets tab for Lumina Excel browsing plus ClientStructs runtime sheet inspection.");

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
        {
            cachedAddons = AddonInspector.ScanAllAddons();
            lastRefresh = DateTime.UtcNow;
            cachedDebugSnapshot = null;
            cachedDebugAddonName = null;
        }

        ImGui.SameLine();
        var visCount = cachedAddons.Count(a => a.IsVisible);
        ImGui.TextDisabled($"({cachedAddons.Count} loaded, {visCount} visible)");

        // Copied feedback
        if (!string.IsNullOrEmpty(copiedFeedback) && DateTime.UtcNow < copiedExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), copiedFeedback);
        }

        // Overlay settings collapsible
        if (plugin.Configuration.OverlayEnabled)
        {
            var showBoxes = plugin.Configuration.OverlayShowBoundingBoxes;
            var showIds = plugin.Configuration.OverlayShowNodeIds;
            var showNames = plugin.Configuration.OverlayShowAddonNames;
            var interactiveOnly = plugin.Configuration.OverlayShowInteractiveOnly;

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            if (ImGui.Checkbox("Boxes", ref showBoxes)) { plugin.Configuration.OverlayShowBoundingBoxes = showBoxes; plugin.Configuration.Save(); }
            ImGui.SameLine();
            if (ImGui.Checkbox("IDs", ref showIds)) { plugin.Configuration.OverlayShowNodeIds = showIds; plugin.Configuration.Save(); }
            ImGui.SameLine();
            if (ImGui.Checkbox("Names", ref showNames)) { plugin.Configuration.OverlayShowAddonNames = showNames; plugin.Configuration.Save(); }
            ImGui.SameLine();
            if (ImGui.Checkbox("Interactive Only", ref interactiveOnly)) { plugin.Configuration.OverlayShowInteractiveOnly = interactiveOnly; plugin.Configuration.Save(); }
        }

        ImGui.Separator();
    }

    // ═══════════════════════════════════════════════════
    //  Addons Tab — 3 column layout
    // ═══════════════════════════════════════════════════
    private void DrawAddonsTab()
    {
        var avail = ImGui.GetContentRegionAvail();
        var leftW = 200f;
        var rightW = 260f;
        var centerW = avail.X - leftW - rightW - 12;
        if (centerW < 200) centerW = 200;
        var panelH = avail.Y - 30;

        // ── Left: Addon list ──
        using (var child = ImRaii.Child("AddonList", new Vector2(leftW, panelH), true))
        {
            if (child.Success) DrawAddonList();
        }

        ImGui.SameLine();

        // ── Center: Node table ──
        using (var child = ImRaii.Child("NodeTable", new Vector2(centerW, panelH), true))
        {
            if (child.Success) DrawNodeTable();
        }

        ImGui.SameLine();

        // ── Right: Node detail (top-right, always visible) ──
        using (var child = ImRaii.Child("NodeDetail", new Vector2(rightW, panelH), true))
        {
            if (child.Success) DrawNodeDetailPanel();
        }
    }

    private void DrawAddonList()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##Search", "Search...", ref searchFilter, 128);
        ImGui.Separator();

        var filtered = cachedAddons
            .Where(a => string.IsNullOrEmpty(searchFilter)
                || a.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.IsFocused)
            .ThenByDescending(a => a.IsVisible)
            .ThenBy(a => a.Name)
            .ToList();

        foreach (var addon in filtered)
        {
            var label = $"{addon.Name} [{addon.NodeCount}]";

            // Color: focused = bright green, visible = white, hidden = grey
            Vector4 color;
            if (addon.IsFocused)
                color = new Vector4(0.2f, 1f, 0.2f, 1f);
            else if (addon.IsVisible)
                color = new Vector4(1f, 1f, 1f, 1f);
            else
                color = new Vector4(0.45f, 0.45f, 0.45f, 1f);

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var isSelected = selectedAddonName == addon.Name;
            if (ImGui.Selectable(label, isSelected))
            {
                selectedAddonName = addon.Name;
                selectedNodeIndex = -1;
                cachedDebugSnapshot = null;
                cachedDebugAddonName = null;
            }
            ImGui.PopStyleColor();

            // Right-click to copy name
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                CopyAndNotify(addon.Name, addon.Name);

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text($"{addon.Name}");
                ImGui.Text($"Pos: ({addon.Position.X:F0}, {addon.Position.Y:F0})  Size: {addon.Size.X:F0}x{addon.Size.Y:F0}");
                ImGui.Text($"Visible: {addon.IsVisible}  Ready: {addon.IsReady}  Scale: {addon.Scale:F2}");
                ImGui.Text($"Nodes: {addon.NodeCount}  Addr: 0x{addon.Address:X}");
                if (addon.IsVisible)
                {
                    var inter = addon.Nodes.Count(n => n.IsInteractive);
                    if (inter > 0) ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"Interactive: {inter}");
                }
                ImGui.TextDisabled("Right-click to copy name");
                ImGui.EndTooltip();
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  Center: Node Table
    // ═══════════════════════════════════════════════════
    private void DrawNodeTable()
    {
        if (string.IsNullOrEmpty(selectedAddonName))
        {
            ImGui.TextDisabled("Select an addon from the left panel.");
            return;
        }

        // Deep scan the selected addon for fresh data
        var addon = cachedAddons.FirstOrDefault(a => a.Name == selectedAddonName);
        if (addon == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"'{selectedAddonName}' not found.");
            return;
        }

        // If not visible, try deep scan to get nodes
        if (addon.Nodes.Count == 0 && addon.NodeCount > 0)
        {
            var deep = AddonInspector.ScanSingleAddon(selectedAddonName);
            if (deep != null) addon = deep;
        }

        // Header
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), addon.Name);
        ImGui.SameLine();
        if (addon.IsVisible)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Visible");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Hidden");
        ImGui.SameLine();
        ImGui.TextDisabled($"| Addr: 0x{addon.Address:X} | Scale: {addon.Scale:F2} | Pos: ({addon.Position.X:F0},{addon.Position.Y:F0}) | Size: {addon.Size.X:F0}x{addon.Size.Y:F0}");

        // Copy buttons
        if (ImGui.SmallButton("Copy Name"))
            CopyAndNotify(addon.Name, addon.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Addr"))
            CopyAndNotify($"0x{addon.Address:X}", "Address");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy All Nodes"))
        {
            var lines = new List<string> { $"Addon: {addon.Name} ({addon.NodeCount} nodes) Addr: 0x{addon.Address:X}" };
            foreach (var n in addon.Nodes)
            {
                var parts = $"  [{n.Index}] NodeId={n.NodeId} Type={n.TypeDisplay} (raw:{n.TypeRaw}) Vis={n.IsVisible} Pos=({n.Position.X:F0},{n.Position.Y:F0}) Size=({n.Size.X:F0}x{n.Size.Y:F0})";
                if (!string.IsNullOrEmpty(n.Text)) parts += $" Text=\"{n.Text}\"";
                if (n.HasEvents) parts += $" event:{n.EventParam}";
                if (n.IsInteractive) parts += " [INTERACTIVE]";
                parts += $" Addr=0x{n.Address:X}";
                lines.Add(parts);
            }
            CopyAndNotify(string.Join("\n", lines), "All Nodes");
        }

        ImGui.Spacing();

        // Reset hover state each frame — will be set if a row is hovered
        hoveredNodeIndex = -1;

        // Node table
        if (ImGui.BeginTable("Nodes", 8,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableSetupColumn("NodeId", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Vis", ImGuiTableColumnFlags.WidthFixed, 22);
            ImGui.TableSetupColumn("Pos", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Text / Info", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Act", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableHeadersRow();

            foreach (var node in addon.Nodes)
            {
                ImGui.TableNextRow();

                // # — click row to select + copy
                ImGui.TableNextColumn();
                var isNodeSel = selectedNodeIndex == node.Index;
                if (ImGui.Selectable($"{node.Index}##n{node.Index}", isNodeSel, ImGuiSelectableFlags.SpanAllColumns))
                {
                    selectedNodeIndex = node.Index;
                    var copyLine = $"[{node.Index}] NodeId={node.NodeId} Type={node.TypeDisplay} (raw:{node.TypeRaw})";
                    if (!string.IsNullOrEmpty(node.Text)) copyLine += $" \"{node.Text}\"";
                    if (node.IsInteractive) copyLine += " [INTERACTIVE]";
                    CopyAndNotify(copyLine, $"Node [{node.Index}]");
                }
                // Track hover for overlay pulsation
                if (ImGui.IsItemHovered())
                    hoveredNodeIndex = node.Index;

                // NodeId
                ImGui.TableNextColumn();
                ImGui.Text($"{node.NodeId}");

                // Type
                ImGui.TableNextColumn();
                if (node.IsComponentNode)
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f), node.ComponentType);
                else
                    ImGui.Text(node.Type.ToString());

                // Vis
                ImGui.TableNextColumn();
                if (node.IsVisible)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Y");
                else
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "N");

                // Pos
                ImGui.TableNextColumn();
                ImGui.Text($"{node.Position.X:F0},{node.Position.Y:F0}");

                // Size
                ImGui.TableNextColumn();
                ImGui.Text($"{node.Size.X:F0}x{node.Size.Y:F0}");

                // Text / Info
                ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(node.Text))
                {
                    var t = node.Text.Length > 35 ? node.Text[..35] + "..." : node.Text;
                    ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), t);
                    if (ImGui.IsItemHovered() && node.Text.Length > 35)
                        ImGui.SetTooltip(node.Text);
                }
                else if (node.HasEvents)
                {
                    ImGui.TextDisabled($"event:{node.EventParam}");
                }

                // Interactive
                ImGui.TableNextColumn();
                if (node.IsInteractive)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "YES");
            }

            ImGui.EndTable();
        }

    }

    // ═══════════════════════════════════════════════════
    //  Right: Node Detail Panel (top-right, fixed)
    // ═══════════════════════════════════════════════════
    private void DrawNodeDetailPanel()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "Node Detail");
        ImGui.Separator();

        if (string.IsNullOrEmpty(selectedAddonName) || selectedNodeIndex < 0)
        {
            ImGui.TextDisabled("Click a node row to inspect.");
            return;
        }

        var addon = cachedAddons.FirstOrDefault(a => a.Name == selectedAddonName);
        if (addon == null || selectedNodeIndex >= addon.Nodes.Count)
        {
            ImGui.TextDisabled("Node not available.");
            return;
        }

        var n = addon.Nodes[selectedNodeIndex];

        // ── Node Detail Section (~20 lines height) ──
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        using (var detailChild = ImRaii.Child("NodeDetailInfo", new Vector2(-1, lineH * 20), false))
        {
            if (detailChild.Success)
            {
                // Each line is clickable to copy
                DrawCopyLine($"Addon: {addon.Name}");
                DrawCopyLine($"Node Index: [{n.Index}]");
                DrawCopyLine($"NodeId: {n.NodeId}");
                DrawCopyLine($"Type: {n.TypeDisplay} (raw: {n.TypeRaw})");
                if (n.IsComponentNode)
                    DrawCopyLine($"Component: {n.ComponentType}");
                DrawCopyLine($"Visible: {n.IsVisible}");
                DrawCopyLine($"Position: ({n.Position.X:F1}, {n.Position.Y:F1})");
                DrawCopyLine($"Screen: ({n.ScreenPosition.X:F1}, {n.ScreenPosition.Y:F1})");
                DrawCopyLine($"Size: {n.Size.X:F1} x {n.Size.Y:F1}");
                DrawCopyLine($"HasEvents: {n.HasEvents}");
                if (n.HasEvents)
                    DrawCopyLine($"EventParam: {n.EventParam}");
                DrawCopyLine($"Interactive: {n.IsInteractive}");
                DrawCopyLine($"Address: 0x{n.Address:X}");

                if (!string.IsNullOrEmpty(n.Text))
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), "Text:");
                    ImGui.TextWrapped(n.Text);
                    if (ImGui.IsItemClicked())
                        CopyAndNotify(n.Text, "Text");
                }
            }
        }

        // ── Copy All button ──
        if (ImGui.SmallButton("Copy All Details"))
        {
            var details = new List<string>
            {
                $"Addon: {addon.Name}",
                $"Node Index: [{n.Index}]",
                $"NodeId: {n.NodeId}",
                $"Type: {n.TypeDisplay} (raw: {n.TypeRaw})",
            };
            if (n.IsComponentNode)
                details.Add($"Component: {n.ComponentType}");
            details.Add($"Visible: {n.IsVisible}");
            details.Add($"Position: ({n.Position.X:F1}, {n.Position.Y:F1})");
            details.Add($"Screen: ({n.ScreenPosition.X:F1}, {n.ScreenPosition.Y:F1})");
            details.Add($"Size: {n.Size.X:F1} x {n.Size.Y:F1}");
            details.Add($"HasEvents: {n.HasEvents}");
            if (n.HasEvents)
                details.Add($"EventParam: {n.EventParam}");
            details.Add($"Interactive: {n.IsInteractive}");
            details.Add($"Address: 0x{n.Address:X}");
            if (!string.IsNullOrEmpty(n.Text))
                details.Add($"Text: {n.Text}");
            CopyAndNotify(string.Join("\n", details), "All Details");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Code Snippets:");
        var snippetVisible = $"AddonHelper.IsAddonVisible(\"{addon.Name}\")";
        var snippetReady = $"AddonHelper.IsAddonReady(\"{addon.Name}\")";
        var snippetClick = $"AddonHelper.ClickAddonButton(\"{addon.Name}\", {n.Index})";
        var snippetCallback = $"AddonHelper.FireCallback(\"{addon.Name}\", {n.EventParam})";

        if (ImGui.SmallButton("IsVisible"))
            CopyAndNotify(snippetVisible, "IsVisible snippet");
        ImGui.SameLine();
        if (ImGui.SmallButton("IsReady"))
            CopyAndNotify(snippetReady, "IsReady snippet");

        if (n.IsInteractive)
        {
            if (ImGui.SmallButton("ClickButton"))
                CopyAndNotify(snippetClick, "ClickButton snippet");
            ImGui.SameLine();
            if (n.HasEvents && ImGui.SmallButton("FireCallback"))
                CopyAndNotify(snippetCallback, "FireCallback snippet");
        }

    }

    // ═══════════════════════════════════════════════════
    //  Logging Tab — full-width addon event log
    // ═══════════════════════════════════════════════════
    private void DrawLoggingTab()
    {
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Addon Event Log");
        ImGui.SameLine();
        ImGui.TextDisabled($"({clickLog.Count} entries)");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Log"))
        {
            if (clickLog.Count > 0)
                CopyAndNotify(string.Join("\n", clickLog), "Log");
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear Log"))
            clickLog.Clear();
        ImGui.Separator();

        using (var logChild = ImRaii.Child("LoggingContent", new Vector2(-1, -1), true))
        {
            if (logChild.Success)
            {
                if (clickLog.Count == 0)
                {
                    ImGui.TextDisabled("Interact with game UI to log events here.");
                    ImGui.TextDisabled("Addon appear/disappear events are detected automatically.");
                }
                else
                {
                    for (int i = clickLog.Count - 1; i >= 0; i--)
                    {
                        if (ImGui.Selectable(clickLog[i]))
                            CopyAndNotify(clickLog[i], "Log entry");
                    }
                }
            }
        }
    }

    private void DrawCopyLine(string text)
    {
        ImGui.Text(text);
        if (ImGui.IsItemClicked())
            CopyAndNotify(text);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click to copy");
    }

    // ═══════════════════════════════════════════════════
    //  Debug Tab — recursive addon dump
    // ═══════════════════════════════════════════════════
    private void DrawDebugTab()
    {
        if (string.IsNullOrEmpty(selectedAddonName))
        {
            ImGui.TextDisabled("Select an addon in the Addons tab to open the debug workspace.");
            ImGui.TextDisabled("Use Logging to catch addon timing and open/close events.");
            return;
        }

        var snapshot = GetDebugSnapshot();
        if (snapshot == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Unable to scan '{selectedAddonName}' for debug data.");
            if (ImGui.SmallButton("Retry Snapshot"))
                RefreshDebugSnapshot();
            return;
        }

        var filteredEntries = GetFilteredDebugEntries(snapshot);

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Debug Workspace");
        ImGui.SameLine();
        ImGui.TextDisabled($"{snapshot.Name} ({filteredEntries.Count} / {snapshot.Entries.Count})");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh Snapshot"))
            RefreshDebugSnapshot();
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Full Dump"))
            CopyAndNotify(BuildDebugReport(snapshot, snapshot.Entries), "Full debug dump");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Filtered Dump"))
            CopyAndNotify(BuildDebugReport(snapshot, filteredEntries), "Filtered debug dump");

        ImGui.TextDisabled("Recursive paths mirror the component-path style used by XA addon text readers.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##DebugFilter", "Filter path, text, node id, event...", ref debugFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Meaningful Only", ref debugMeaningfulOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Text Only", ref debugTextOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Interactive Only", ref debugInteractiveOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Visible Only", ref debugVisibleOnly);

        var avail = ImGui.GetContentRegionAvail();
        var leftW = 440f;

        using (var child = ImRaii.Child("DebugEntryList", new Vector2(leftW, avail.Y), true))
        {
            if (child.Success)
            {
                if (filteredEntries.Count == 0)
                {
                    ImGui.TextDisabled("No debug entries match the current filters.");
                }
                else if (ImGui.BeginTable("DebugEntries", 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
                    | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("Path", ImGuiTableColumnFlags.WidthFixed, 130);
                    ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 110);
                    ImGui.TableSetupColumn("Text / Event", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthFixed, 70);
                    ImGui.TableHeadersRow();

                    for (var i = 0; i < filteredEntries.Count; i++)
                    {
                        var entry = filteredEntries[i];
                        var formatted = AddonInspector.FormatDebugEntry(entry);

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        if (ImGui.Selectable($"{entry.Path}##dbg{i}"))
                            CopyAndNotify(formatted, $"{snapshot.Name} {entry.Path}");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(formatted);

                        ImGui.TableNextColumn();
                        ImGui.Text(entry.TypeName);

                        ImGui.TableNextColumn();
                        var detail = !string.IsNullOrEmpty(entry.Text)
                            ? entry.Text
                            : entry.HasEvents ? $"event:{entry.EventParam}" : $"NodeId:{entry.NodeId}";
                        if (detail.Length > 48)
                        {
                            ImGui.Text(detail[..48] + "...");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(detail);
                        }
                        else
                        {
                            ImGui.Text(detail);
                        }

                        ImGui.TableNextColumn();
                        var flags = new List<string>();
                        if (entry.IsVisible) flags.Add("V");
                        if (entry.IsInteractive) flags.Add("I");
                        if (entry.HasEvents) flags.Add("E");
                        ImGui.Text(flags.Count > 0 ? string.Join(" ", flags) : "-");
                    }

                    ImGui.EndTable();
                }
            }
        }

        ImGui.SameLine();

        using (var child = ImRaii.Child("DebugDetail", new Vector2(0, avail.Y), true))
        {
            if (child.Success)
            {
                DrawCopyLine($"Addon: {snapshot.Name}");
                DrawCopyLine($"Visible: {snapshot.IsVisible}");
                DrawCopyLine($"Ready: {snapshot.IsReady}");
                DrawCopyLine($"Position: ({snapshot.Position.X:F1}, {snapshot.Position.Y:F1})");
                DrawCopyLine($"Size: {snapshot.Size.X:F1} x {snapshot.Size.Y:F1}");
                DrawCopyLine($"Scale: {snapshot.Scale:F2}");
                DrawCopyLine($"Top-level nodes: {snapshot.NodeCount}");
                DrawCopyLine($"Recursive entries: {snapshot.Entries.Count}");
                DrawCopyLine($"Address: 0x{snapshot.Address:X}");

                if (selectedNodeIndex >= 0)
                {
                    var selectedPathPrefix = $"[{selectedNodeIndex}]";
                    var selectedBranch = snapshot.Entries
                        .Where(e => e.Path == selectedPathPrefix
                            || e.Path.StartsWith($"{selectedPathPrefix}→", StringComparison.Ordinal)
                            || e.Path.StartsWith($"{selectedPathPrefix}[", StringComparison.Ordinal))
                        .ToList();

                    if (selectedBranch.Count > 0)
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"Selected Node Branch [{selectedNodeIndex}]");
                        if (ImGui.SmallButton("Copy Selected Branch"))
                            CopyAndNotify(BuildDebugReport(snapshot, selectedBranch), "Selected branch");
                        ImGui.SameLine();
                        ImGui.TextDisabled($"({selectedBranch.Count} entries)");
                        ImGui.Separator();

                        var previewCount = Math.Min(selectedBranch.Count, 16);
                        for (var i = 0; i < previewCount; i++)
                        {
                            var line = AddonInspector.FormatDebugEntry(selectedBranch[i]);
                            if (ImGui.Selectable($"{line}##branch{i}"))
                                CopyAndNotify(line, $"Branch {i + 1}");
                        }

                        if (selectedBranch.Count > previewCount)
                            ImGui.TextDisabled($"+ {selectedBranch.Count - previewCount} more entries in the copied branch dump");
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Workflow:");
                ImGui.TextWrapped("1. Enable Logging to catch addon timing and open/close events.");
                ImGui.TextWrapped("2. Select the addon in the Addons tab.");
                ImGui.TextWrapped("3. Use this tab to copy recursive paths, text nodes, event params, and focused branches before moving anything into XA Database or XA Slave.");
            }
        }
    }

    private void RefreshDebugSnapshot()
    {
        if (string.IsNullOrEmpty(selectedAddonName))
        {
            cachedDebugSnapshot = null;
            cachedDebugAddonName = null;
            return;
        }

        cachedDebugSnapshot = AddonInspector.ScanDebugAddon(selectedAddonName);
        cachedDebugAddonName = selectedAddonName;
    }

    private AddonDebugSnapshot? GetDebugSnapshot()
    {
        if (string.IsNullOrEmpty(selectedAddonName))
            return null;

        if (cachedDebugSnapshot == null || cachedDebugAddonName != selectedAddonName)
            RefreshDebugSnapshot();

        return cachedDebugSnapshot;
    }

    private List<AddonDebugEntry> GetFilteredDebugEntries(AddonDebugSnapshot snapshot)
    {
        return snapshot.Entries
            .Where(entry => !debugMeaningfulOnly
                || entry.TypeRaw >= 1000
                || entry.HasEvents
                || entry.IsInteractive
                || !string.IsNullOrEmpty(entry.Text))
            .Where(entry => !debugTextOnly || !string.IsNullOrEmpty(entry.Text))
            .Where(entry => !debugInteractiveOnly || entry.IsInteractive)
            .Where(entry => !debugVisibleOnly || entry.IsVisible)
            .Where(entry => string.IsNullOrEmpty(debugFilter) || MatchesDebugEntryFilter(entry, debugFilter))
            .ToList();
    }

    private string BuildDebugReport(AddonDebugSnapshot snapshot, List<AddonDebugEntry> entries)
    {
        var lines = new List<string>
        {
            $"Addon: {snapshot.Name}",
            $"Visible: {snapshot.IsVisible}  Ready: {snapshot.IsReady}  Scale: {snapshot.Scale:F2}",
            $"Pos: ({snapshot.Position.X:F1}, {snapshot.Position.Y:F1})  Size: {snapshot.Size.X:F1} x {snapshot.Size.Y:F1}",
            $"Top-level nodes: {snapshot.NodeCount}  Recursive entries: {entries.Count}  Addr: 0x{snapshot.Address:X}"
        };

        foreach (var entry in entries)
            lines.Add(AddonInspector.FormatDebugEntry(entry));

        return string.Join("\n", lines);
    }

    private static bool MatchesDebugEntryFilter(AddonDebugEntry entry, string filter)
    {
        return entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.NodeId.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.TypeRaw.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (entry.HasEvents && entry.EventParam.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(entry.Text) && entry.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════
    //  Sheets Tab — Common sheets with auto-preview + All Sheets
    // ═══════════════════════════════════════════════════

    // Curated common sheets that plugin developers actually use
    private static readonly (string Name, string Category, string Description, uint SampleRow)[] CommonSheets =
    {
        ("Aetheryte", "Locations", "Teleport destinations", 2),
        ("TerritoryType", "Locations", "Zones and territories", 132),
        ("PlaceName", "Locations", "Zone/place display names", 28),
        ("Map", "Locations", "Map data and scale factors", 4),
        ("World", "Locations", "Game servers/worlds", 34),
        ("WorldDCGroupType", "Locations", "Data centers", 1),
        ("Item", "Items", "All game items", 1),
        ("ItemUICategory", "Items", "Item categories (Weapon, Armor, etc.)", 1),
        ("ClassJob", "Jobs", "Jobs and classes", 1),
        ("ClassJobCategory", "Jobs", "Job restriction groups", 1),
        ("Action", "Actions", "All combat/craft actions", 3),
        ("Status", "Actions", "Status effects (buffs/debuffs)", 1),
        ("Mount", "Collections", "All mounts", 1),
        ("Companion", "Collections", "All minions", 1),
        ("Orchestrion", "Collections", "Orchestrion rolls", 1),
        ("TripleTriadCard", "Collections", "Triple Triad cards", 1),
        ("Achievement", "Collections", "All achievements", 1),
        ("Title", "Collections", "Character titles", 1),
        ("Quest", "Quests", "All quests", 65536),
        ("ContentFinderCondition", "Content", "Duty Finder duties", 1),
        ("ENpcResident", "NPCs", "NPC names", 1),
        ("BNpcName", "NPCs", "Battle NPC/enemy names", 1),
        ("Recipe", "Crafting", "Crafting recipes", 1),
        ("GatheringItem", "Crafting", "Gathering node items", 1),
        ("Race", "Character", "Playable races", 1),
        ("Tribe", "Character", "Playable clans/tribes", 1),
        ("GrandCompany", "Character", "Grand Companies", 1),
        ("Weather", "World", "Weather types", 1),
        ("Emote", "Social", "Emotes", 1),
        ("Fate", "Content", "FATEs", 1),
    };

    private void DrawSheetsTab()
    {
        if (cachedSheetNames == null)
        {
            try
            {
                cachedSheetNames = Plugin.DataManager.Excel.SheetNames
                    .OrderBy(n => n)
                    .ToList();
            }
            catch
            {
                cachedSheetNames = new List<string>();
            }
        }

        ImGui.TextDisabled("Combined Lumina + ClientStructs browsing workspace for verifying static game data and live runtime state before wiring changes into production collectors.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("SheetsTabs"))
        {
            if (ImGui.BeginTabItem("Lumina Sheets"))
            {
                DrawLuminaSheetsWorkspace();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("CS.Sheets"))
            {
                DrawClientStructsSheetsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawLuminaSheetsWorkspace()
    {
        if (ImGui.BeginTabBar("LuminaSheetsTabs"))
        {
            if (ImGui.BeginTabItem("Common Sheets"))
            {
                DrawCommonSheetsView();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"All Sheets ({cachedSheetNames?.Count ?? 0})"))
            {
                DrawAllSheetsView();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawCommonSheetsView()
    {
        var avail = ImGui.GetContentRegionAvail();
        var leftW = 280f;

        // Left: categorized common sheet list
        using (var child = ImRaii.Child("CommonList", new Vector2(leftW, avail.Y), true))
        {
            if (child.Success)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Common Game Data Sheets");
                ImGui.TextDisabled("Click to preview, right-click to copy name");
                ImGui.Separator();

                string? lastCat = null;
                foreach (var (name, cat, desc, sampleRow) in CommonSheets)
                {
                    if (lastCat != cat)
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), cat);
                        lastCat = cat;
                    }

                    var isSel = selectedSheetName == name;
                    if (ImGui.Selectable($"  {name}", isSel))
                    {
                        selectedSheetName = name;
                        sheetPreviewStartIndex = 0;
                        sheetPreviewRowId = sampleRow;
                        sheetSchemaStatus = "EXDSchema pending...";
                        selectedSheetPreviewRow = -1;
                        selectedSheetPreviewColumn = -1;
                        ReadSheetRow(name, sampleRow, true);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        CopyAndNotify(name, name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{desc}\nSample row: {sampleRow}");
                }
            }
        }

        ImGui.SameLine();

        // Right: preview
        using (var child = ImRaii.Child("CommonPreview", new Vector2(0, avail.Y), true))
        {
            if (child.Success)
                DrawSheetPreview();
        }
    }

    private void DrawAllSheetsView()
    {
        var avail = ImGui.GetContentRegionAvail();
        var leftW = 250f;

        using (var child = ImRaii.Child("AllSheetList", new Vector2(leftW, avail.Y), true))
        {
            if (child.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##SheetSearch", "Search all sheets...", ref sheetSearchFilter, 128);
                if (ImGui.SmallButton("Housing")) sheetSearchFilter = "Housing";
                ImGui.SameLine();
                if (ImGui.SmallButton("Territory")) sheetSearchFilter = "Territory";
                ImGui.SameLine();
                if (ImGui.SmallButton("Place")) sheetSearchFilter = "Place";
                ImGui.SameLine();
                if (ImGui.SmallButton("World")) sheetSearchFilter = "World";
                ImGui.SameLine();
                if (ImGui.SmallButton("Company")) sheetSearchFilter = "Company";
                ImGui.SameLine();
                if (ImGui.SmallButton("Submarine")) sheetSearchFilter = "Submarine";
                ImGui.SameLine();
                if (ImGui.SmallButton("Airship")) sheetSearchFilter = "Airship";
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear")) sheetSearchFilter = string.Empty;
                ImGui.Separator();

                var filtered = cachedSheetNames!
                    .Where(s => string.IsNullOrEmpty(sheetSearchFilter)
                        || s.Contains(sheetSearchFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                ImGui.TextDisabled($"{filtered.Count} / {cachedSheetNames!.Count}");

                foreach (var name in filtered)
                {
                    var isSel = selectedSheetName == name;
                    if (ImGui.Selectable(name, isSel))
                    {
                        selectedSheetName = name;
                        sheetPreviewStartIndex = 0;
                        sheetPreviewRowId = 0;
                        sheetSchemaStatus = "EXDSchema pending...";
                        selectedSheetPreviewRow = -1;
                        selectedSheetPreviewColumn = -1;
                        ReadSheetRow(name, 0, true);
                    }
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        CopyAndNotify(name, name);
                }
            }
        }

        ImGui.SameLine();

        using (var child = ImRaii.Child("AllSheetPreview", new Vector2(0, avail.Y), true))
        {
            if (child.Success)
                DrawSheetPreview();
        }
    }

    private void DrawSheetPreview()
    {
        if (string.IsNullOrEmpty(selectedSheetName))
        {
            ImGui.TextDisabled("Select a sheet to preview.");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), selectedSheetName);
        ImGui.TextDisabled("Uses Lumina raw sheet access with a readable multi-row grid preview.");

        if (ImGui.SmallButton("Copy Name"))
            CopyAndNotify(selectedSheetName, selectedSheetName);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy RawSheet"))
            CopyAndNotify($"var sheet = Plugin.DataManager.GameData.GetExcelSheet<RawRow>(name: \"{selectedSheetName}\");", "Raw sheet snippet");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy TryGetRow"))
            CopyAndNotify($"var sheet = Plugin.DataManager.GameData.GetExcelSheet<RawRow>(name: \"{selectedSheetName}\");\nif (sheet != null && sheet.TryGetRow(rowId, out var row))\n{{\n    // inspect raw columns here\n}}", "TryGetRow snippet");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Visible Rows") && !string.IsNullOrWhiteSpace(sheetPreviewText))
            CopyAndNotify(sheetPreviewText, "Visible sheet rows");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh EXDSchema"))
        {
            exdSchemaService.Invalidate(selectedSheetName);
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        }

        ImGui.Spacing();
        if (ImGui.Checkbox("Schema Formatting", ref preferSchemaFormatting) && sheetPreviewRows.Count > 0)
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        ImGui.SameLine();
        if (ImGui.Checkbox("Offsets", ref includeSchemaOffsets) && sheetPreviewRows.Count > 0)
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        ImGui.SameLine();
        if (ImGui.Checkbox("Comments", ref includeSchemaComments) && sheetPreviewRows.Count > 0)
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        ImGui.TextDisabled(sheetSchemaStatus);
        ImGui.Separator();

        ImGui.SetNextItemWidth(100);
        var rowId = (int)sheetPreviewRowId;
        if (ImGui.InputInt("Row ID", ref rowId))
            sheetPreviewRowId = (uint)Math.Max(0, rowId);
        ImGui.SameLine();
        if (ImGui.SmallButton("Jump Row"))
            ReadSheetRow(selectedSheetName, sheetPreviewRowId, true);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        var startIndex = sheetPreviewStartIndex;
        if (ImGui.InputInt("Start Index", ref startIndex))
            sheetPreviewStartIndex = Math.Max(0, startIndex);
        ImGui.SameLine();
        if (ImGui.SmallButton("Load Window"))
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);

        ImGui.SetNextItemWidth(70);
        var rowsToShow = sheetPreviewRowCount;
        if (ImGui.InputInt("Rows", ref rowsToShow))
        {
            sheetPreviewRowCount = Math.Clamp(rowsToShow, 5, 100);
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        }

        if (ImGui.SmallButton("Prev"))
        {
            sheetPreviewStartIndex = Math.Max(0, sheetPreviewStartIndex - sheetPreviewRowCount);
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Next"))
        {
            sheetPreviewStartIndex += sheetPreviewRowCount;
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Top"))
        {
            sheetPreviewStartIndex = 0;
            ReadSheetRow(selectedSheetName, sheetPreviewRowId);
        }

        if (!string.IsNullOrWhiteSpace(sheetPreviewMessage))
            ImGui.TextDisabled(sheetPreviewMessage);

        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var detailWidth = Math.Clamp(avail.X * 0.32f, 280f, 360f);
        var gridWidth = Math.Max(320f, avail.X - detailWidth - 10f);

        using (var gridChild = ImRaii.Child("SheetGrid", new Vector2(gridWidth, avail.Y), true))
        {
            if (gridChild.Success)
                DrawSheetPreviewGrid();
        }

        ImGui.SameLine();

        using (var detailChild = ImRaii.Child("SheetSelection", new Vector2(0, avail.Y), true))
        {
            if (detailChild.Success)
                DrawSheetPreviewSelection();
        }
    }

    private void DrawSheetPreviewGrid()
    {
        if (sheetPreviewColumns.Count == 0 || sheetPreviewRows.Count == 0)
        {
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(sheetPreviewMessage) ? "No sheet rows loaded." : sheetPreviewMessage);
            return;
        }

        if (!ImGui.BeginTable("SheetPreviewGrid", sheetPreviewColumns.Count + 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn("Idx", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("RowId", ImGuiTableColumnFlags.WidthFixed, 72f);
        foreach (var column in sheetPreviewColumns)
            ImGui.TableSetupColumn(column.Header, ImGuiTableColumnFlags.WidthFixed, column.Width);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("Idx");
        ImGui.TableNextColumn();
        ImGui.TableHeader("RowId");
        foreach (var column in sheetPreviewColumns)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader(column.Header);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(column.Tooltip))
                ImGui.SetTooltip(column.Tooltip);
        }

        for (var rowListIndex = 0; rowListIndex < sheetPreviewRows.Count; rowListIndex++)
        {
            var row = sheetPreviewRows[rowListIndex];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.RowIndex}##sheetRowIndex{rowListIndex}", selectedSheetPreviewRow == rowListIndex))
                SelectSheetPreviewCell(rowListIndex, selectedSheetPreviewColumn >= 0 ? selectedSheetPreviewColumn : 0);

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.RowId}##sheetRowId{rowListIndex}", selectedSheetPreviewRow == rowListIndex))
                SelectSheetPreviewCell(rowListIndex, selectedSheetPreviewColumn >= 0 ? selectedSheetPreviewColumn : 0);

            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                var cell = row.Cells[columnIndex];
                var column = sheetPreviewColumns[columnIndex];
                var display = TruncateSheetCellText(cell.DisplayText, 42);

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{display}##sheetCell{rowListIndex}_{columnIndex}", selectedSheetPreviewRow == rowListIndex && selectedSheetPreviewColumn == columnIndex))
                    SelectSheetPreviewCell(rowListIndex, columnIndex);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(BuildSheetCellTooltip(row, column, cell));
            }
        }

        ImGui.EndTable();
    }

    private void DrawSheetPreviewSelection()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Selection");
        ImGui.Separator();

        if (selectedSheetPreviewRow < 0
            || selectedSheetPreviewRow >= sheetPreviewRows.Count
            || selectedSheetPreviewColumn < 0
            || selectedSheetPreviewColumn >= sheetPreviewColumns.Count)
        {
            ImGui.TextDisabled("Select a grid cell to inspect the selected row and field details.");
            return;
        }

        var row = sheetPreviewRows[selectedSheetPreviewRow];
        var column = sheetPreviewColumns[selectedSheetPreviewColumn];
        var cell = row.Cells[selectedSheetPreviewColumn];

        DrawCopyLine($"Sheet: {selectedSheetName}");
        DrawCopyLine($"Row Index: {row.RowIndex}");
        DrawCopyLine($"Row ID: {row.RowId}");
        DrawCopyLine($"Column: {column.Header} [{column.ColumnIndex}]");
        DrawCopyLine($"Type: {column.Descriptor}");

        if (ImGui.SmallButton("Copy Cell"))
            CopyAndNotify(BuildSelectedSheetCellCopyText(), $"{column.Header} [{row.RowId}]");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Row"))
            CopyAndNotify(BuildSheetRowCopyText(row), $"Row {row.RowId}");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), "Display Value");
        ImGui.TextWrapped(cell.DisplayText);
        if (ImGui.IsItemClicked())
            CopyAndNotify(cell.DisplayText, "Display value");

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Raw Value");
            ImGui.TextWrapped(cell.RawText);
            if (ImGui.IsItemClicked())
                CopyAndNotify(cell.RawText, "Raw value");
        }

        if (!string.IsNullOrWhiteSpace(column.Tooltip))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Field Details");
            ImGui.TextWrapped(column.Tooltip);
            if (ImGui.IsItemClicked())
                CopyAndNotify(column.Tooltip, "Field details");
        }
    }

    private void SelectSheetPreviewCell(int rowListIndex, int columnIndex)
    {
        if (rowListIndex < 0 || rowListIndex >= sheetPreviewRows.Count)
            return;

        selectedSheetPreviewRow = rowListIndex;
        selectedSheetPreviewColumn = sheetPreviewColumns.Count == 0
            ? -1
            : Math.Clamp(columnIndex, 0, sheetPreviewColumns.Count - 1);

        if (selectedSheetPreviewRow >= 0)
            sheetPreviewRowId = sheetPreviewRows[selectedSheetPreviewRow].RowId;
    }

    private string BuildSelectedSheetCellCopyText()
    {
        if (selectedSheetPreviewRow < 0
            || selectedSheetPreviewRow >= sheetPreviewRows.Count
            || selectedSheetPreviewColumn < 0
            || selectedSheetPreviewColumn >= sheetPreviewColumns.Count)
            return string.Empty;

        var row = sheetPreviewRows[selectedSheetPreviewRow];
        var column = sheetPreviewColumns[selectedSheetPreviewColumn];
        var cell = row.Cells[selectedSheetPreviewColumn];
        var lines = new List<string>
        {
            $"Sheet: {selectedSheetName}",
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}",
            $"Column: {column.Header} [{column.ColumnIndex}]",
            $"Type: {column.Descriptor}",
            $"Display: {cell.DisplayText}"
        };

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
            lines.Add($"Raw: {cell.RawText}");
        if (!string.IsNullOrWhiteSpace(column.Tooltip))
            lines.Add($"Field Details: {column.Tooltip}");

        return string.Join("\n", lines);
    }

    private string BuildSheetRowCopyText(SheetPreviewRow row)
    {
        var lines = new List<string>
        {
            $"Sheet: {selectedSheetName}",
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}"
        };

        for (var columnIndex = 0; columnIndex < sheetPreviewColumns.Count && columnIndex < row.Cells.Count; columnIndex++)
        {
            var column = sheetPreviewColumns[columnIndex];
            lines.Add($"{column.Header} [{column.ColumnIndex}] ({column.Descriptor}): {row.Cells[columnIndex].DisplayText}");
        }

        return string.Join("\n", lines);
    }

    private string BuildVisibleRowsCopyText()
    {
        if (sheetPreviewColumns.Count == 0 || sheetPreviewRows.Count == 0)
            return string.Empty;

        var lines = new List<string>();
        var header = new List<string> { "RowIndex", "RowId" };
        header.AddRange(sheetPreviewColumns.Select(c => c.Header));
        lines.Add(string.Join("\t", header));

        foreach (var row in sheetPreviewRows)
        {
            var values = new List<string>
            {
                row.RowIndex.ToString(),
                row.RowId.ToString()
            };
            values.AddRange(row.Cells.Select(cell => cell.DisplayText.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
            lines.Add(string.Join("\t", values));
        }

        return string.Join("\n", lines);
    }

    private void DrawClientStructsSheetsTab()
    {
        if (ImGui.BeginTabBar("ClientStructsSheetsTabs"))
        {
            if (ImGui.BeginTabItem("Common Views"))
            {
                DrawClientStructsCommonView();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"All Views ({clientStructsSheetService.Definitions.Count})"))
            {
                DrawClientStructsAllView();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawClientStructsCommonView()
    {
        var avail = ImGui.GetContentRegionAvail();
        var leftW = 300f;

        using (var child = ImRaii.Child("ClientStructsCommonList", new Vector2(leftW, avail.Y), true))
        {
            if (child.Success)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "ClientStructs Runtime Views");
                ImGui.TextDisabled("Click to inspect, right-click to copy name");
                ImGui.Separator();

                string? lastCategory = null;
                foreach (var definition in clientStructsSheetService.Definitions)
                {
                    if (!string.Equals(lastCategory, definition.Category, StringComparison.Ordinal))
                    {
                        ImGui.Spacing();
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), definition.Category);
                        lastCategory = definition.Category;
                    }

                    var isSelected = string.Equals(selectedClientStructsSheetName, definition.Name, StringComparison.Ordinal);
                    if (ImGui.Selectable($"  {definition.Name}", isSelected))
                        SelectClientStructsSheet(definition.Name);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        CopyAndNotify(definition.Name, definition.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(definition.Description);
                }
            }
        }

        ImGui.SameLine();

        using (var child = ImRaii.Child("ClientStructsCommonPreview", new Vector2(0, avail.Y), true))
        {
            if (child.Success)
                DrawClientStructsSheetPreview();
        }
    }

    private void DrawClientStructsAllView()
    {
        var avail = ImGui.GetContentRegionAvail();
        var leftW = 270f;

        using (var child = ImRaii.Child("ClientStructsAllList", new Vector2(leftW, avail.Y), true))
        {
            if (child.Success)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##ClientStructsSheetSearch", "Search ClientStructs views...", ref clientStructsSheetSearchFilter, 128);
                if (ImGui.SmallButton("Quest")) clientStructsSheetSearchFilter = "Quest";
                ImGui.SameLine();
                if (ImGui.SmallButton("Inventory")) clientStructsSheetSearchFilter = "Inventory";
                ImGui.SameLine();
                if (ImGui.SmallButton("Retainer")) clientStructsSheetSearchFilter = "Retainer";
                ImGui.SameLine();
                if (ImGui.SmallButton("Housing")) clientStructsSheetSearchFilter = "Housing";
                ImGui.SameLine();
                if (ImGui.SmallButton("Info")) clientStructsSheetSearchFilter = "Info";
                ImGui.SameLine();
                if (ImGui.SmallButton("Target")) clientStructsSheetSearchFilter = "Target";
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear")) clientStructsSheetSearchFilter = string.Empty;
                ImGui.Separator();

                var filtered = clientStructsSheetService.Definitions
                    .Where(definition => string.IsNullOrEmpty(clientStructsSheetSearchFilter)
                        || definition.Name.Contains(clientStructsSheetSearchFilter, StringComparison.OrdinalIgnoreCase)
                        || definition.Category.Contains(clientStructsSheetSearchFilter, StringComparison.OrdinalIgnoreCase)
                        || definition.Description.Contains(clientStructsSheetSearchFilter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                ImGui.TextDisabled($"{filtered.Count} / {clientStructsSheetService.Definitions.Count}");

                foreach (var definition in filtered)
                {
                    var isSelected = string.Equals(selectedClientStructsSheetName, definition.Name, StringComparison.Ordinal);
                    if (ImGui.Selectable(definition.Name, isSelected))
                        SelectClientStructsSheet(definition.Name);
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        CopyAndNotify(definition.Name, definition.Name);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"{definition.Category}\n{definition.Description}");
                }
            }
        }

        ImGui.SameLine();

        using (var child = ImRaii.Child("ClientStructsAllPreview", new Vector2(0, avail.Y), true))
        {
            if (child.Success)
                DrawClientStructsSheetPreview();
        }
    }

    private void SelectClientStructsSheet(string name)
    {
        selectedClientStructsSheetName = name;
        clientStructsPreviewStartIndex = 0;
        selectedClientStructsPreviewRow = -1;
        selectedClientStructsPreviewColumn = -1;
        RefreshSelectedClientStructsSheet(true);
    }

    private void RefreshSelectedClientStructsSheet(bool resetSelection = false)
    {
        if (string.IsNullOrWhiteSpace(selectedClientStructsSheetName))
            return;

        clientStructsSheetSnapshot = clientStructsSheetService.ReadSheet(selectedClientStructsSheetName, new ClientStructsSheetRequest
        {
            StartIndex = clientStructsPreviewStartIndex,
            RowCount = clientStructsPreviewRowCount,
            InventoryType = clientStructsInventoryType
        });

        clientStructsSheetLastRefresh = DateTime.UtcNow;
        clientStructsPreviewStartIndex = clientStructsSheetSnapshot.StartIndex;

        if (clientStructsSheetSnapshot.Rows.Count == 0 || clientStructsSheetSnapshot.Columns.Count == 0)
        {
            selectedClientStructsPreviewRow = -1;
            selectedClientStructsPreviewColumn = -1;
            return;
        }

        if (resetSelection || selectedClientStructsPreviewRow < 0 || selectedClientStructsPreviewRow >= clientStructsSheetSnapshot.Rows.Count)
            selectedClientStructsPreviewRow = 0;
        else
            selectedClientStructsPreviewRow = Math.Clamp(selectedClientStructsPreviewRow, 0, clientStructsSheetSnapshot.Rows.Count - 1);

        if (resetSelection || selectedClientStructsPreviewColumn < 0 || selectedClientStructsPreviewColumn >= clientStructsSheetSnapshot.Columns.Count)
            selectedClientStructsPreviewColumn = 0;
        else
            selectedClientStructsPreviewColumn = Math.Clamp(selectedClientStructsPreviewColumn, 0, clientStructsSheetSnapshot.Columns.Count - 1);
    }

    private void DrawClientStructsSheetPreview()
    {
        if (string.IsNullOrWhiteSpace(selectedClientStructsSheetName))
        {
            ImGui.TextDisabled("Select a ClientStructs runtime view to preview.");
            return;
        }

        if (clientStructsAutoRefresh && (clientStructsSheetSnapshot == null || (DateTime.UtcNow - clientStructsSheetLastRefresh).TotalSeconds >= plugin.Configuration.RefreshInterval))
            RefreshSelectedClientStructsSheet();

        clientStructsSheetSnapshot ??= clientStructsSheetService.ReadSheet(selectedClientStructsSheetName, new ClientStructsSheetRequest
        {
            StartIndex = clientStructsPreviewStartIndex,
            RowCount = clientStructsPreviewRowCount,
            InventoryType = clientStructsInventoryType
        });

        var snapshot = clientStructsSheetSnapshot;
        if (snapshot == null)
        {
            ImGui.TextDisabled("ClientStructs snapshot unavailable.");
            return;
        }

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), snapshot.Definition.Name);
        ImGui.TextDisabled(snapshot.Definition.Description);

        if (ImGui.SmallButton("Copy Name"))
            CopyAndNotify(snapshot.Definition.Name, snapshot.Definition.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Access Snippet") && !string.IsNullOrWhiteSpace(snapshot.Definition.AccessSnippet))
            CopyAndNotify(snapshot.Definition.AccessSnippet, $"{snapshot.Definition.Name} snippet");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Visible Rows") && !string.IsNullOrWhiteSpace(snapshot.VisibleRowsCopyText))
            CopyAndNotify(snapshot.VisibleRowsCopyText, "Visible runtime rows");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            RefreshSelectedClientStructsSheet();

        ImGui.Spacing();
        if (ImGui.Checkbox("Auto Refresh", ref clientStructsAutoRefresh) && clientStructsAutoRefresh)
            RefreshSelectedClientStructsSheet();

        if (snapshot.Definition.SupportsInventoryType)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220f);
            if (ImGui.BeginCombo("InventoryType", clientStructsInventoryType.ToString()))
            {
                foreach (var inventoryType in ClientStructsInventoryTypes)
                {
                    var selected = inventoryType == clientStructsInventoryType;
                    if (ImGui.Selectable(inventoryType.ToString(), selected))
                    {
                        clientStructsInventoryType = inventoryType;
                        clientStructsPreviewStartIndex = 0;
                        RefreshSelectedClientStructsSheet(true);
                    }

                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.TextDisabled(snapshot.Status);
        ImGui.Separator();

        if (snapshot.Definition.SupportsWindowing)
        {
            ImGui.SetNextItemWidth(110f);
            var startIndex = clientStructsPreviewStartIndex;
            if (ImGui.InputInt("Start Index", ref startIndex))
                clientStructsPreviewStartIndex = Math.Max(0, startIndex);
            ImGui.SameLine();
            if (ImGui.SmallButton("Load Window"))
                RefreshSelectedClientStructsSheet();

            ImGui.SetNextItemWidth(70f);
            var rowsToShow = clientStructsPreviewRowCount;
            if (ImGui.InputInt("Rows", ref rowsToShow))
            {
                clientStructsPreviewRowCount = Math.Clamp(rowsToShow, 5, 100);
                RefreshSelectedClientStructsSheet();
            }

            if (ImGui.SmallButton("Prev"))
            {
                clientStructsPreviewStartIndex = Math.Max(0, clientStructsPreviewStartIndex - clientStructsPreviewRowCount);
                RefreshSelectedClientStructsSheet();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Next"))
            {
                clientStructsPreviewStartIndex += clientStructsPreviewRowCount;
                RefreshSelectedClientStructsSheet();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Top"))
            {
                clientStructsPreviewStartIndex = 0;
                RefreshSelectedClientStructsSheet();
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Message))
            ImGui.TextDisabled(snapshot.Message);

        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var detailWidth = Math.Clamp(avail.X * 0.32f, 280f, 360f);
        var gridWidth = Math.Max(320f, avail.X - detailWidth - 10f);

        using (var gridChild = ImRaii.Child("ClientStructsSheetGridChild", new Vector2(gridWidth, avail.Y), true))
        {
            if (gridChild.Success)
                DrawClientStructsSheetGrid();
        }

        ImGui.SameLine();

        using (var detailChild = ImRaii.Child("ClientStructsSheetSelection", new Vector2(0, avail.Y), true))
        {
            if (detailChild.Success)
                DrawClientStructsSheetSelection();
        }
    }

    private void DrawClientStructsSheetGrid()
    {
        var snapshot = clientStructsSheetSnapshot;
        if (snapshot == null || snapshot.Columns.Count == 0 || snapshot.Rows.Count == 0)
        {
            ImGui.TextDisabled(snapshot == null || string.IsNullOrWhiteSpace(snapshot?.Message) ? "No runtime rows loaded." : snapshot.Message);
            return;
        }

        if (!ImGui.BeginTable("ClientStructsSheetGrid", snapshot.Columns.Count + 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn("Idx", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableSetupColumn("RowId", ImGuiTableColumnFlags.WidthFixed, 72f);
        foreach (var column in snapshot.Columns)
            ImGui.TableSetupColumn(column.Header, ImGuiTableColumnFlags.WidthFixed, column.Width);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("Idx");
        ImGui.TableNextColumn();
        ImGui.TableHeader("RowId");
        foreach (var column in snapshot.Columns)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader(column.Header);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(column.Tooltip))
                ImGui.SetTooltip(column.Tooltip);
        }

        for (var rowListIndex = 0; rowListIndex < snapshot.Rows.Count; rowListIndex++)
        {
            var row = snapshot.Rows[rowListIndex];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.RowIndex}##clientStructsRowIndex{rowListIndex}", selectedClientStructsPreviewRow == rowListIndex))
                SelectClientStructsSheetCell(rowListIndex, selectedClientStructsPreviewColumn >= 0 ? selectedClientStructsPreviewColumn : 0);

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.RowId}##clientStructsRowId{rowListIndex}", selectedClientStructsPreviewRow == rowListIndex))
                SelectClientStructsSheetCell(rowListIndex, selectedClientStructsPreviewColumn >= 0 ? selectedClientStructsPreviewColumn : 0);

            for (var columnIndex = 0; columnIndex < row.Cells.Count; columnIndex++)
            {
                var cell = row.Cells[columnIndex];
                var column = snapshot.Columns[columnIndex];
                var display = TruncateSheetCellText(cell.DisplayText, 42);

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{display}##clientStructsCell{rowListIndex}_{columnIndex}", selectedClientStructsPreviewRow == rowListIndex && selectedClientStructsPreviewColumn == columnIndex))
                    SelectClientStructsSheetCell(rowListIndex, columnIndex);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(BuildClientStructsCellTooltip(row, column, cell));
            }
        }

        ImGui.EndTable();
    }

    private void DrawClientStructsSheetSelection()
    {
        var snapshot = clientStructsSheetSnapshot;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Selection");
        ImGui.Separator();

        if (snapshot == null
            || selectedClientStructsPreviewRow < 0
            || selectedClientStructsPreviewRow >= snapshot.Rows.Count
            || selectedClientStructsPreviewColumn < 0
            || selectedClientStructsPreviewColumn >= snapshot.Columns.Count)
        {
            ImGui.TextDisabled("Select a grid cell to inspect the selected runtime field details.");
            return;
        }

        var row = snapshot.Rows[selectedClientStructsPreviewRow];
        var column = snapshot.Columns[selectedClientStructsPreviewColumn];
        var cell = row.Cells[selectedClientStructsPreviewColumn];

        DrawCopyLine($"Sheet: {snapshot.Definition.Name}");
        DrawCopyLine($"Row Index: {row.RowIndex}");
        DrawCopyLine($"Row ID: {row.RowId}");
        DrawCopyLine($"Column: {column.Header} [{column.ColumnIndex}]");
        DrawCopyLine($"Type: {column.Descriptor}");

        if (ImGui.SmallButton("Copy Cell"))
            CopyAndNotify(BuildSelectedClientStructsCellCopyText(), $"{column.Header} [{row.RowId}]");
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy Row"))
            CopyAndNotify(BuildClientStructsRowCopyText(row), $"Row {row.RowId}");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.9f, 0.6f, 1f), "Display Value");
        ImGui.TextWrapped(cell.DisplayText);
        if (ImGui.IsItemClicked())
            CopyAndNotify(cell.DisplayText, "Display value");

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Raw Value");
            ImGui.TextWrapped(cell.RawText);
            if (ImGui.IsItemClicked())
                CopyAndNotify(cell.RawText, "Raw value");
        }

        if (!string.IsNullOrWhiteSpace(column.Tooltip))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Field Details");
            ImGui.TextWrapped(column.Tooltip);
            if (ImGui.IsItemClicked())
                CopyAndNotify(column.Tooltip, "Field details");
        }
    }

    private void SelectClientStructsSheetCell(int rowListIndex, int columnIndex)
    {
        var snapshot = clientStructsSheetSnapshot;
        if (snapshot == null || rowListIndex < 0 || rowListIndex >= snapshot.Rows.Count)
            return;

        selectedClientStructsPreviewRow = rowListIndex;
        selectedClientStructsPreviewColumn = snapshot.Columns.Count == 0
            ? -1
            : Math.Clamp(columnIndex, 0, snapshot.Columns.Count - 1);
    }

    private string BuildSelectedClientStructsCellCopyText()
    {
        var snapshot = clientStructsSheetSnapshot;
        if (snapshot == null
            || selectedClientStructsPreviewRow < 0
            || selectedClientStructsPreviewRow >= snapshot.Rows.Count
            || selectedClientStructsPreviewColumn < 0
            || selectedClientStructsPreviewColumn >= snapshot.Columns.Count)
            return string.Empty;

        var row = snapshot.Rows[selectedClientStructsPreviewRow];
        var column = snapshot.Columns[selectedClientStructsPreviewColumn];
        var cell = row.Cells[selectedClientStructsPreviewColumn];
        var lines = new List<string>
        {
            $"Sheet: {snapshot.Definition.Name}",
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}",
            $"Column: {column.Header} [{column.ColumnIndex}]",
            $"Type: {column.Descriptor}",
            $"Display: {cell.DisplayText}"
        };

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
            lines.Add($"Raw: {cell.RawText}");
        if (!string.IsNullOrWhiteSpace(column.Tooltip))
            lines.Add($"Field Details: {column.Tooltip}");

        return string.Join("\n", lines);
    }

    private string BuildClientStructsRowCopyText(ClientStructsSheetRow row)
    {
        var snapshot = clientStructsSheetSnapshot;
        if (snapshot == null)
            return string.Empty;

        var lines = new List<string>
        {
            $"Sheet: {snapshot.Definition.Name}",
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}"
        };

        for (var columnIndex = 0; columnIndex < snapshot.Columns.Count && columnIndex < row.Cells.Count; columnIndex++)
        {
            var column = snapshot.Columns[columnIndex];
            lines.Add($"{column.Header} [{column.ColumnIndex}] ({column.Descriptor}): {row.Cells[columnIndex].DisplayText}");
        }

        return string.Join("\n", lines);
    }

    private static string BuildClientStructsCellTooltip(ClientStructsSheetRow row, ClientStructsSheetColumn column, ClientStructsSheetCell cell)
    {
        var lines = new List<string>
        {
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}",
            $"Column: {column.Header} [{column.ColumnIndex}]",
            $"Type: {column.Descriptor}",
            $"Display: {cell.DisplayText}"
        };

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
            lines.Add($"Raw: {cell.RawText}");
        if (!string.IsNullOrWhiteSpace(column.Tooltip))
            lines.Add(column.Tooltip);

        return string.Join("\n", lines);
    }

    private static string BuildSheetCellTooltip(SheetPreviewRow row, SheetPreviewColumn column, SheetPreviewCell cell)
    {
        var lines = new List<string>
        {
            $"Row Index: {row.RowIndex}",
            $"Row ID: {row.RowId}",
            $"Column: {column.Header} [{column.ColumnIndex}]",
            $"Type: {column.Descriptor}",
            $"Display: {cell.DisplayText}"
        };

        if (!string.Equals(cell.RawText, cell.DisplayText, StringComparison.Ordinal))
            lines.Add($"Raw: {cell.RawText}");
        if (!string.IsNullOrWhiteSpace(column.Tooltip))
            lines.Add(column.Tooltip);

        return string.Join("\n", lines);
    }

    private static string TruncateSheetCellText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "<empty>";
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    private static bool TryFindSheetRowIndex(ExcelSheet<RawRow> rawSheet, uint rowId, out int rowIndex)
    {
        for (var i = 0; i < rawSheet.Count; i++)
        {
            if (rawSheet.GetRowAt(i).RowId == rowId)
            {
                rowIndex = i;
                return true;
            }
        }

        rowIndex = -1;
        return false;
    }

    private void ReadSheetRow(string sheetName, uint rowId)
        => ReadSheetRow(sheetName, rowId, false);

    private void ReadSheetRow(string sheetName, uint rowId, bool centerOnSelection)
    {
        try
        {
            var rawSheet = Plugin.DataManager.GameData.GetExcelSheet<RawRow>(name: sheetName);
            if (rawSheet == null)
            {
                sheetPreviewColumns = new List<SheetPreviewColumn>();
                sheetPreviewRows = new List<SheetPreviewRow>();
                sheetPreviewText = string.Empty;
                sheetPreviewMessage = $"Sheet {sheetName} is not available.";
                return;
            }

            ExdSchemaSheetInfo? schema = null;
            if (preferSchemaFormatting)
                schema = exdSchemaService.TryGetSheetInfo(sheetName, out sheetSchemaStatus);
            else
                sheetSchemaStatus = "EXDSchema formatting disabled.";

            var totalRows = rawSheet.Count;
            if (totalRows <= 0)
            {
                sheetPreviewColumns = new List<SheetPreviewColumn>();
                sheetPreviewRows = new List<SheetPreviewRow>();
                sheetPreviewText = string.Empty;
                sheetPreviewMessage = $"Sheet {sheetName} has no rows.";
                return;
            }

            if (centerOnSelection && TryFindSheetRowIndex(rawSheet, rowId, out var targetRowIndex))
                sheetPreviewStartIndex = Math.Max(0, targetRowIndex - (sheetPreviewRowCount / 2));

            sheetPreviewRowCount = Math.Clamp(sheetPreviewRowCount, 5, 100);
            sheetPreviewStartIndex = Math.Clamp(sheetPreviewStartIndex, 0, Math.Max(0, totalRows - sheetPreviewRowCount));

            var cols = rawSheet.Columns;
            sheetPreviewColumns = new List<SheetPreviewColumn>(cols.Count);
            for (var columnIndex = 0; columnIndex < cols.Count; columnIndex++)
            {
                var header = $"Col[{columnIndex}]";
                var descriptor = cols[columnIndex].Type.ToString();
                var tooltipLines = new List<string> { $"Col[{columnIndex}]", descriptor };

                if (schema != null && columnIndex < schema.FlattenedFields.Count)
                {
                    var field = schema.FlattenedFields[columnIndex];
                    header = field.Path;
                    descriptor = BuildSchemaDescriptor(field, cols[columnIndex].Type);
                    tooltipLines = new List<string> { $"{field.Path} [{columnIndex}]", descriptor };

                    if (includeSchemaComments && !string.IsNullOrWhiteSpace(field.Comment))
                        tooltipLines.Add(field.Comment);

                    var linkInfo = BuildSchemaLinkInfo(field);
                    if (!string.IsNullOrEmpty(linkInfo))
                        tooltipLines.Add(linkInfo);
                }

                sheetPreviewColumns.Add(new SheetPreviewColumn
                {
                    ColumnIndex = columnIndex,
                    Header = header,
                    Descriptor = descriptor,
                    Tooltip = string.Join("\n", tooltipLines),
                    Width = Math.Clamp(header.Length * 7f + 40f, 110f, 260f)
                });
            }

            sheetPreviewRows = new List<SheetPreviewRow>();
            var rowsToLoad = Math.Min(sheetPreviewRowCount, Math.Max(0, totalRows - sheetPreviewStartIndex));
            for (var offset = 0; offset < rowsToLoad; offset++)
            {
                var rowIndex = sheetPreviewStartIndex + offset;
                var rawRow = rawSheet.GetRowAt(rowIndex);
                var previewRow = new SheetPreviewRow
                {
                    RowIndex = rowIndex,
                    RowId = rawRow.RowId,
                    Cells = new List<SheetPreviewCell>(cols.Count)
                };

                for (var columnIndex = 0; columnIndex < cols.Count; columnIndex++)
                {
                    string displayValue;
                    object? rawValue;
                    try
                    {
                        (displayValue, rawValue) = ReadColumnValue(rawRow, columnIndex, cols[columnIndex].Type);
                    }
                    catch
                    {
                        displayValue = "<error>";
                        rawValue = null;
                    }

                    var formattedValue = displayValue;
                    if (schema != null && columnIndex < schema.FlattenedFields.Count)
                        formattedValue = FormatSchemaValue(schema.FlattenedFields[columnIndex], rawValue, displayValue);

                    previewRow.Cells.Add(new SheetPreviewCell
                    {
                        ColumnIndex = columnIndex,
                        DisplayText = formattedValue,
                        RawText = rawValue?.ToString() ?? displayValue
                    });
                }

                sheetPreviewRows.Add(previewRow);
            }

            var selectedRowIndex = sheetPreviewRows.FindIndex(r => r.RowId == rowId);
            if (selectedRowIndex >= 0)
                selectedSheetPreviewRow = selectedRowIndex;
            else if (sheetPreviewRows.Count > 0)
                selectedSheetPreviewRow = Math.Clamp(selectedSheetPreviewRow, 0, sheetPreviewRows.Count - 1);
            else
                selectedSheetPreviewRow = -1;

            if (sheetPreviewColumns.Count > 0)
                selectedSheetPreviewColumn = Math.Clamp(selectedSheetPreviewColumn, 0, sheetPreviewColumns.Count - 1);
            else
                selectedSheetPreviewColumn = -1;

            if (selectedSheetPreviewRow >= 0 && selectedSheetPreviewRow < sheetPreviewRows.Count)
                sheetPreviewRowId = sheetPreviewRows[selectedSheetPreviewRow].RowId;

            sheetPreviewText = BuildVisibleRowsCopyText();
            sheetPreviewMessage = $"Rows {sheetPreviewStartIndex + 1}-{sheetPreviewStartIndex + sheetPreviewRows.Count} of {totalRows} • Columns: {sheetPreviewColumns.Count}";
            if (schema != null && schema.FlattenedFields.Count != cols.Count)
                sheetPreviewMessage += $" • Schema fields: {schema.FlattenedFields.Count}";
        }
        catch (Exception ex)
        {
            sheetPreviewColumns = new List<SheetPreviewColumn>();
            sheetPreviewRows = new List<SheetPreviewRow>();
            sheetPreviewText = string.Empty;
            sheetPreviewMessage = string.Empty;
            sheetSchemaStatus = $"Sheet read failed: {ex.Message}";
        }
    }

    private static (string DisplayText, object? RawValue) ReadColumnValue(RawRow rawRow, int columnIndex, Lumina.Data.Structs.Excel.ExcelColumnDataType type)
    {
        return type switch
        {
            Lumina.Data.Structs.Excel.ExcelColumnDataType.String => (rawRow.ReadStringColumn(columnIndex).ToString(), rawRow.ReadStringColumn(columnIndex).ToString()),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Int32 => (rawRow.ReadInt32Column(columnIndex).ToString(), rawRow.ReadInt32Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.UInt32 => (rawRow.ReadUInt32Column(columnIndex).ToString(), rawRow.ReadUInt32Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Int16 => (rawRow.ReadInt16Column(columnIndex).ToString(), rawRow.ReadInt16Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.UInt16 => (rawRow.ReadUInt16Column(columnIndex).ToString(), rawRow.ReadUInt16Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Int8 => (rawRow.ReadInt8Column(columnIndex).ToString(), rawRow.ReadInt8Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.UInt8 => (rawRow.ReadUInt8Column(columnIndex).ToString(), rawRow.ReadUInt8Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Int64 => (rawRow.ReadInt64Column(columnIndex).ToString(), rawRow.ReadInt64Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.UInt64 => (rawRow.ReadUInt64Column(columnIndex).ToString(), rawRow.ReadUInt64Column(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Bool => (rawRow.ReadBoolColumn(columnIndex).ToString(), rawRow.ReadBoolColumn(columnIndex)),
            Lumina.Data.Structs.Excel.ExcelColumnDataType.Float32 => (rawRow.ReadFloat32Column(columnIndex).ToString("F4"), rawRow.ReadFloat32Column(columnIndex)),
            _ => ($"({type})", null)
        };
    }

    private string BuildSchemaDescriptor(ExdSchemaFlatField field, Lumina.Data.Structs.Excel.ExcelColumnDataType fallbackType)
    {
        var parts = new List<string> { field.Type };
        if (!string.IsNullOrEmpty(field.ColumnType))
            parts.Add(field.ColumnType);
        else
            parts.Add(fallbackType.ToString());
        if (includeSchemaOffsets && field.Offset.HasValue)
            parts.Add($"ofs {field.Offset.Value}");
        return string.Join(", ", parts);
    }

    private static string BuildSchemaLinkInfo(ExdSchemaFlatField field)
    {
        if (field.Condition?.Cases != null && field.Condition.Cases.Count > 0)
        {
            var cases = string.Join("; ", field.Condition.Cases.Select(kvp => $"{kvp.Key} => {string.Join(" / ", kvp.Value)}"));
            return $"Conditional link via {field.Condition.Switch}: {cases}";
        }

        if (field.Targets.Count > 0)
            return $"Links: {string.Join(" / ", field.Targets)}";

        return string.Empty;
    }

    private static string FormatSchemaValue(ExdSchemaFlatField field, object? rawValue, string fallback)
    {
        if (rawValue == null)
            return fallback;

        if (string.Equals(field.Type, "icon", StringComparison.OrdinalIgnoreCase) && TryGetUnsignedValue(rawValue, out var iconValue))
        {
            var iconFolder = (iconValue / 1000UL) * 1000UL;
            return $"{fallback} => ui/icon/{iconFolder:000000}/{iconValue:000000}_hr1.tex";
        }

        if (string.Equals(field.Type, "color", StringComparison.OrdinalIgnoreCase) && TryGetUnsignedValue(rawValue, out var colorValue))
            return $"{fallback} => #{(colorValue & 0xFFFFFFUL):X6}";

        if (string.Equals(field.Type, "modelId", StringComparison.OrdinalIgnoreCase) && TryGetUnsignedValue(rawValue, out var modelValue))
        {
            if (rawValue is ulong or long)
            {
                var skeletonId = (modelValue >> 48) & 0xFFFFUL;
                var modelId = (modelValue >> 32) & 0xFFFFUL;
                var variantId = (modelValue >> 16) & 0xFFFFUL;
                var stainId = modelValue & 0xFFFFUL;
                return $"{fallback} => skeleton:{skeletonId} model:{modelId} variant:{variantId} stain:{stainId}";
            }

            var packedModelId = modelValue & 0xFFFFUL;
            var packedVariantId = (modelValue >> 16) & 0xFFUL;
            var packedStain = (modelValue >> 24) & 0xFFUL;
            return $"{fallback} => model:{packedModelId} variant:{packedVariantId} stain:{packedStain}";
        }

        return fallback;
    }

    private static bool TryGetUnsignedValue(object rawValue, out ulong value)
    {
        switch (rawValue)
        {
            case byte v:
                value = v;
                return true;
            case sbyte v:
                value = unchecked((ulong)v);
                return true;
            case ushort v:
                value = v;
                return true;
            case short v:
                value = unchecked((ulong)v);
                return true;
            case uint v:
                value = v;
                return true;
            case int v:
                value = unchecked((ulong)v);
                return true;
            case ulong v:
                value = v;
                return true;
            case long v:
                value = unchecked((ulong)v);
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private sealed class SheetPreviewColumn
    {
        public int ColumnIndex { get; set; }
        public string Header { get; set; } = string.Empty;
        public string Descriptor { get; set; } = string.Empty;
        public string Tooltip { get; set; } = string.Empty;
        public float Width { get; set; }
    }

    private sealed class SheetPreviewCell
    {
        public int ColumnIndex { get; set; }
        public string DisplayText { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
    }

    private sealed class SheetPreviewRow
    {
        public int RowIndex { get; set; }
        public uint RowId { get; set; }
        public List<SheetPreviewCell> Cells { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════
    //  Status Bar
    // ═══════════════════════════════════════════════════
    private void DrawStatusBar()
    {
        ImGui.TextDisabled($"XA HUD Navigator v{PluginVersion}");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("/xahud to toggle");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        if (plugin.Configuration.OverlayEnabled)
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Overlay: ON");
        else
            ImGui.TextDisabled("Overlay: OFF");
        if (clickTrackMode)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Logging: ON");
        }
    }

    /// <summary>Called by the overlay when it detects a game-side click during track mode.</summary>
    public void LogClick(string addonName, int nodeIndex, string nodeType)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {addonName} → [{nodeIndex}] {nodeType}";
        clickLog.Add(entry);
        if (clickLog.Count > 200) clickLog.RemoveAt(0);
    }

    public bool IsClickTrackMode => clickTrackMode;
    public string? SelectedAddonName => selectedAddonName;
    public int HoveredNodeIndex => hoveredNodeIndex;

    /// <summary>
    /// Framework.Update callback — polls visible addons and detects appear/disappear/focus changes.
    /// Fires once per frame while click tracking is enabled.
    /// </summary>
    private void OnClickTrackTick(IFramework fw)
    {
        if (!clickTrackMode) return;

        try
        {
            var currentAddons = AddonInspector.ScanAllAddons();
            var currentVisible = new HashSet<string>();
            string? currentFocused = null;

            foreach (var a in currentAddons)
            {
                if (a.IsVisible)
                    currentVisible.Add(a.Name);
                if (a.IsFocused)
                    currentFocused = a.Name;
            }

            // Detect newly appeared addons
            foreach (var name in currentVisible)
            {
                if (!previousVisibleAddons.Contains(name))
                {
                    var addonInfo = currentAddons.FirstOrDefault(a => a.Name == name);
                    var nodeCount = addonInfo?.NodeCount ?? 0;
                    clickLog.Add($"[{DateTime.Now:HH:mm:ss}] APPEARED: {name} [{nodeCount} nodes]");
                    if (clickLog.Count > 200) clickLog.RemoveAt(0);
                }
            }

            // Detect disappeared addons
            foreach (var name in previousVisibleAddons)
            {
                if (!currentVisible.Contains(name))
                {
                    clickLog.Add($"[{DateTime.Now:HH:mm:ss}] CLOSED: {name}");
                    if (clickLog.Count > 200) clickLog.RemoveAt(0);
                }
            }

            previousVisibleAddons = currentVisible;
        }
        catch { /* ignore scan errors during tracking */ }
    }

}
