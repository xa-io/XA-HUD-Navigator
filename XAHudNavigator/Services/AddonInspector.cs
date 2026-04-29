using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

/// <summary>
/// Data model for a single addon's inspection snapshot.
/// </summary>
public class AddonInfo
{
    public string Name { get; set; } = "";
    public bool IsVisible { get; set; }
    public bool IsReady { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
    public float Scale { get; set; } = 1f;
    public int NodeCount { get; set; }
    public int WidgetCount { get; set; }
    public nint Address { get; set; }
    public List<NodeInfo> Nodes { get; set; } = new();
    public bool IsFocused { get; set; }
}

/// <summary>
/// Data model for a single AtkResNode within an addon.
/// </summary>
public class NodeInfo
{
    public int Index { get; set; }
    public uint NodeId { get; set; }
    public NodeType Type { get; set; }
    public ushort TypeRaw { get; set; }
    public bool IsVisible { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 ScreenPosition { get; set; }
    public Vector2 Size { get; set; }
    public bool HasEvents { get; set; }
    public uint EventParam { get; set; }
    public string Text { get; set; } = "";
    public bool IsInteractive { get; set; }
    public nint Address { get; set; }

    // Component sub-type info
    public bool IsComponentNode { get; set; }
    public string ComponentType { get; set; } = "";

    /// <summary>Formatted type string for display.</summary>
    public string TypeDisplay => IsComponentNode ? ComponentType : Type.ToString();
}

public class AddonDebugEntry
{
    public string Path { get; set; } = "";
    public uint NodeId { get; set; }
    public string TypeName { get; set; } = "";
    public ushort TypeRaw { get; set; }
    public bool IsVisible { get; set; }
    public bool IsInteractive { get; set; }
    public bool HasEvents { get; set; }
    public uint EventParam { get; set; }
    public string Text { get; set; } = "";
    public nint Address { get; set; }
    public ushort NodeFlagsRaw { get; set; }
    public string NodeFlagsText { get; set; } = "";
    public uint DrawFlags { get; set; }
    public ushort ChildCount { get; set; }
    public ushort Priority { get; set; }
    public float Depth { get; set; }
    public nint ParentAddress { get; set; }
    public nint PrevSiblingAddress { get; set; }
    public nint NextSiblingAddress { get; set; }
    public nint ChildAddress { get; set; }
    public nint ComponentAddress { get; set; }
}

public class NodeRuntimeReport
{
    public string AddonName { get; set; } = "";
    public int NodeIndex { get; set; }
    public List<string> SummaryLines { get; set; } = new();
    public List<string> ComponentChildLines { get; set; } = new();
    public List<string> TreeListLines { get; set; } = new();
    public List<string> TreeListRendererLines { get; set; } = new();
}

public class AddonDebugSnapshot
{
    public string Name { get; set; } = "";
    public bool IsVisible { get; set; }
    public bool IsReady { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
    public float Scale { get; set; } = 1f;
    public int NodeCount { get; set; }
    public nint Address { get; set; }
    public nint AgentAddress { get; set; }
    public int AtkValueCount { get; set; }
    public List<AddonAtkValueInfo> AtkValues { get; set; } = new();
    public string SelectedAddonLookupSource { get; set; } = "";
    public List<AddonLookupInstanceInfo> GameGuiAddonInstances { get; set; } = new();
    public AddonLookupInstanceInfo? RaptureAddonInstance { get; set; }
    public ResolvedNativeStructSnapshot? BaseAddonStruct { get; set; }
    public ResolvedNativeStructSnapshot? AddonStruct { get; set; }
    public ResolvedNativeStructSnapshot? AgentStruct { get; set; }
    public List<AddonDebugEntry> Entries { get; set; } = new();
}

public class AddonAtkValueInfo
{
    public int Index { get; set; }
    public string TypeName { get; set; } = "";
    public ushort TypeRaw { get; set; }
    public string Value { get; set; } = "";
    public nint Address { get; set; }
}

public class AddonLookupInstanceInfo
{
    public string Source { get; set; } = "";
    public int Index { get; set; }
    public nint Address { get; set; }
    public bool IsVisible { get; set; }
    public bool IsReady { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
    public float Scale { get; set; } = 1f;
    public int NodeCount { get; set; }
    public bool MatchesSelectedSnapshot { get; set; }
}

/// <summary>
/// Enumerates all loaded AtkUnitBase addons and their node trees.
/// Produces read-only snapshot data for UI display and overlay drawing.
/// </summary>
public static class AddonInspector
{
    private static readonly Dictionary<ushort, string> ComponentTypeNames = new()
    {
        { 1000, "Custom" }, { 1001, "Button" }, { 1002, "Window" },
        { 1003, "CheckBox" }, { 1004, "RadioButton" }, { 1005, "Slider" },
        { 1006, "TextInput" }, { 1007, "NumericInput" }, { 1008, "List" },
        { 1009, "DropDown" }, { 1010, "Tab" }, { 1011, "TreeList" },
        { 1012, "ScrollBar" }, { 1013, "ListItemRenderer" }, { 1014, "Icon" },
        { 1015, "IconText" }, { 1016, "DragDrop" }, { 1017, "GuildLeveCard" },
        { 1018, "TextNineGrid" }, { 1019, "JournalCanvas" }, { 1020, "Multipurpose" },
        { 1021, "Map" }, { 1022, "Preview" }, { 1023, "HoldButton" },
    };

    private static readonly HashSet<ushort> InteractiveComponentTypes = new()
    {
        1001, 1003, 1004, 1005, 1006, 1007, 1009, 1010, 1011, 1016, 1023,
    };

    /// <summary>
    /// Scans all loaded addons in the AtkUnitManager and returns inspection data.
    /// Always returns ALL addons (visible and hidden) for the full list.
    /// </summary>
    public static unsafe List<AddonInfo> ScanAllAddons()
    {
        var results = new List<AddonInfo>();

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null) return results;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null) return results;

            // Get the focused addon for highlighting
            var focusedAddon = unitManager->GetAddonByName("") == null ? null : (AtkUnitBase*)null;
            try { focusedAddon = unitManager->AtkUnitManager.FocusedUnitsList.Entries[0].Value; } catch { }

            var allLoadedList = &unitManager->AtkUnitManager.AllLoadedUnitsList;
            if (allLoadedList == null) return results;

            for (var i = 0; i < allLoadedList->Count; i++)
            {
                AtkUnitBase* addon;
                try { addon = allLoadedList->Entries[i].Value; }
                catch { continue; }
                if (addon == null) continue;

                string name;
                try
                {
                    var nameSpan = addon->Name;
                    var nullIdx = nameSpan.IndexOf((byte)0);
                    name = nullIdx > 0
                        ? Encoding.UTF8.GetString(nameSpan.Slice(0, nullIdx))
                        : $"<unnamed_{i}>";
                }
                catch { name = $"<error_{i}>"; }

                var info = new AddonInfo
                {
                    Name = name,
                    IsVisible = addon->IsVisible,
                    IsReady = addon->IsReady,
                    Position = new Vector2(addon->X, addon->Y),
                    Size = new Vector2(addon->RootNode != null ? addon->RootNode->Width : 0,
                                       addon->RootNode != null ? addon->RootNode->Height : 0),
                    Scale = addon->Scale,
                    NodeCount = addon->UldManager.NodeListCount,
                    Address = (nint)addon,
                    IsFocused = focusedAddon != null && addon == focusedAddon,
                };

                // Only scan nodes for visible addons (performance)
                if (addon->IsVisible)
                    info.Nodes = ScanNodes(addon);

                results.Add(info);
            }
        }
        catch { }

        return results;
    }

    /// <summary>
    /// Scans nodes for a specific addon by name (deep scan for selected addon).
    /// </summary>
    public static unsafe AddonInfo? ScanSingleAddon(string name)
    {
        try
        {
            var addon = TryGetAddonByName(name);
            if (addon == null) return null;

            var info = new AddonInfo
            {
                Name = name,
                IsVisible = addon->IsVisible,
                IsReady = addon->IsReady,
                Position = new Vector2(addon->X, addon->Y),
                Size = new Vector2(addon->RootNode != null ? addon->RootNode->Width : 0,
                                   addon->RootNode != null ? addon->RootNode->Height : 0),
                Scale = addon->Scale,
                NodeCount = addon->UldManager.NodeListCount,
                Address = (nint)addon,
            };
            info.Nodes = ScanNodes(addon);
            return info;
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds which addon and node is at the given screen position.
    /// Used for mouse-hover inspection.
    /// </summary>
    public static unsafe (string? AddonName, int NodeIndex) HitTest(Vector2 screenPos, List<AddonInfo> addons)
    {
        // Iterate in reverse (top-most addon first)
        for (int a = addons.Count - 1; a >= 0; a--)
        {
            var addon = addons[a];
            if (!addon.IsVisible) continue;
            if (addon.Size.X <= 0 || addon.Size.Y <= 0) continue;

            var addonMin = addon.Position;
            var addonMax = addon.Position + addon.Size;

            if (screenPos.X >= addonMin.X && screenPos.X <= addonMax.X &&
                screenPos.Y >= addonMin.Y && screenPos.Y <= addonMax.Y)
            {
                // Check nodes in reverse (top-most node first)
                for (int n = 0; n < addon.Nodes.Count; n++)
                {
                    var node = addon.Nodes[n];
                    if (!node.IsVisible || node.Size.X <= 0 || node.Size.Y <= 0) continue;

                    var nMin = node.ScreenPosition;
                    var nMax = nMin + node.Size;
                    if (screenPos.X >= nMin.X && screenPos.X <= nMax.X &&
                        screenPos.Y >= nMin.Y && screenPos.Y <= nMax.Y)
                    {
                        return (addon.Name, n);
                    }
                }
                return (addon.Name, -1);
            }
        }
        return (null, -1);
    }

    public static unsafe AddonDebugSnapshot? ScanDebugAddon(string name)
    {
        try
        {
            var addon = TryGetAddonByName(name);
            if (addon == null) return null;

            var snapshot = new AddonDebugSnapshot
            {
                Name = name,
                IsVisible = addon->IsVisible,
                IsReady = addon->IsReady,
                Position = new Vector2(addon->X, addon->Y),
                Size = new Vector2(addon->RootNode != null ? addon->RootNode->Width : 0,
                                   addon->RootNode != null ? addon->RootNode->Height : 0),
                Scale = addon->Scale,
                NodeCount = addon->UldManager.NodeListCount,
                Address = (nint)addon,
                AgentAddress = TryFindAgentAddress(name),
                AtkValueCount = addon->AtkValuesCount,
            };

            snapshot.GameGuiAddonInstances = ScanGameGuiAddonInstances(name);
            snapshot.RaptureAddonInstance = ScanRaptureAddonInstance(name);
            MarkSelectedAddonLookupMatches(snapshot);
            snapshot.SelectedAddonLookupSource = ResolveSelectedAddonLookupSource(snapshot);
            snapshot.AtkValues = ScanAtkValues(addon);
            snapshot.BaseAddonStruct = ClientStructRuntimeInspector.ResolveBaseAddonStruct(snapshot.Address);
            snapshot.AddonStruct = ClientStructRuntimeInspector.ResolveAddonStruct(name, snapshot.Address);
            snapshot.AgentStruct = ClientStructRuntimeInspector.ResolveAgentStruct(snapshot.AgentAddress);

            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node != null)
                    CollectDebugEntries(node, $"[{i}]", snapshot.Entries);
            }

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatDebugEntry(AddonDebugEntry entry)
    {
        var parts = new List<string>
        {
            $"{entry.Path} #{entry.NodeId} {entry.TypeName} (raw:{entry.TypeRaw})",
            entry.IsVisible ? "Visible" : "Hidden"
        };

        if (entry.HasEvents)
            parts.Add($"event:{entry.EventParam}");

        if (entry.IsInteractive)
            parts.Add("INTERACTIVE");

        if (entry.NodeFlagsRaw != 0)
            parts.Add($"flags:0x{entry.NodeFlagsRaw:X4}");

        if (entry.DrawFlags != 0)
            parts.Add($"draw:0x{entry.DrawFlags:X}");

        if (!string.IsNullOrEmpty(entry.Text))
            parts.Add($"Text=\"{entry.Text}\"");

        if (entry.ComponentAddress != 0)
            parts.Add($"Comp=0x{entry.ComponentAddress:X}");

        parts.Add($"Addr=0x{entry.Address:X}");
        return string.Join(" | ", parts);
    }

    public static string FormatAtkValueEntry(AddonAtkValueInfo entry)
        => $"[{entry.Index}] {entry.TypeName} (raw:{entry.TypeRaw}) | {entry.Value} | Addr=0x{entry.Address:X}";

    public static string FormatAddonLookupEntry(AddonLookupInstanceInfo entry)
    {
        var label = entry.Source.Equals("GameGui", StringComparison.OrdinalIgnoreCase)
            ? $"GameGui[{entry.Index}]"
            : entry.Source;
        var selectedSuffix = entry.MatchesSelectedSnapshot ? " | SELECTED" : string.Empty;
        return $"{label} | Visible={entry.IsVisible} Ready={entry.IsReady} Nodes={entry.NodeCount} " +
               $"Pos=({entry.Position.X:F1}, {entry.Position.Y:F1}) Size={entry.Size.X:F1}x{entry.Size.Y:F1} Scale={entry.Scale:F2} " +
               $"Addr=0x{entry.Address:X}{selectedSuffix}";
    }

    public static unsafe NodeRuntimeReport? BuildNodeRuntimeReport(string addonName, int nodeIndex)
    {
        var addon = TryGetAddonByName(addonName);
        if (addon == null || !addon->IsVisible || nodeIndex < 0 || nodeIndex >= addon->UldManager.NodeListCount)
            return null;

        var node = addon->UldManager.NodeList[nodeIndex];
        if (node == null)
            return null;

        var report = new NodeRuntimeReport
        {
            AddonName = addonName,
            NodeIndex = nodeIndex,
        };

        AppendEventLines(report.SummaryLines, "Node", node);

        var typeVal = (ushort)node->Type;
        if (typeVal < 1000)
            return report;

        var componentNode = (AtkComponentNode*)node;
        if (componentNode->Component == null)
        {
            report.SummaryLines.Add("Component Address: 0x0");
            return report;
        }

        var component = componentNode->Component;
        report.SummaryLines.Add($"Component Address: 0x{(nint)component:X}");
        report.SummaryLines.Add($"Component Type: {GetNodeTypeDisplay(node->Type, typeVal)}");
        report.SummaryLines.Add($"Owner Node: {FormatPointer((nint)component->OwnerNode)}");
        report.SummaryLines.Add($"Component Child Count: {component->UldManager.NodeListCount}");

        AtkResNode* focusNode = null;
        try
        {
            focusNode = component->GetFocusNode();
        }
        catch
        {
        }

        report.SummaryLines.Add($"Focus Node: {FormatPointer((nint)focusNode)}");
        if (focusNode != null)
            AppendEventLines(report.SummaryLines, "Focus Node", focusNode);

        AppendComponentChildLines(report.ComponentChildLines, component);

        if (typeVal == 1011)
            AppendTreeListLines(report, (AtkComponentTreeList*)component);

        return report;
    }

    public static string BuildNodeRuntimeReportText(NodeRuntimeReport report)
    {
        var lines = new List<string>
        {
            $"Addon: {report.AddonName}",
            $"Node Index: [{report.NodeIndex}]",
        };

        AppendMultilineLines(lines, "Selected Node Runtime", report.SummaryLines);
        AppendMultilineLines(lines, "Component Children", report.ComponentChildLines);
        AppendMultilineLines(lines, "TreeList Runtime", report.TreeListLines);
        AppendMultilineLines(lines, "TreeList Visible Renderers", report.TreeListRendererLines);
        return string.Join("\n", lines);
    }

    private static unsafe void CollectDebugEntries(AtkResNode* node, string path, List<AddonDebugEntry> results, int depth = 0)
    {
        if (node == null || depth > 5) return;

        var typeVal = (ushort)node->Type;
        var displayPath = node->Type == NodeType.Counter ? $"{path}[ctr]" : path;
        var entry = new AddonDebugEntry
        {
            Path = displayPath,
            NodeId = node->NodeId,
            TypeName = GetNodeTypeDisplay(node->Type, typeVal),
            TypeRaw = typeVal,
            IsVisible = node->IsVisible(),
            IsInteractive = InteractiveComponentTypes.Contains(typeVal),
            NodeFlagsRaw = (ushort)node->NodeFlags,
            NodeFlagsText = node->NodeFlags.ToString(),
            DrawFlags = node->DrawFlags,
            ChildCount = node->ChildCount,
            Priority = node->Priority,
            Depth = node->Depth,
            ParentAddress = (nint)node->ParentNode,
            PrevSiblingAddress = (nint)node->PrevSiblingNode,
            NextSiblingAddress = (nint)node->NextSiblingNode,
            ChildAddress = (nint)node->ChildNode,
            Address = (nint)node,
        };

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt != null)
            {
                entry.HasEvents = true;
                entry.EventParam = evt->Param;
                entry.IsInteractive = true;
            }
        }
        catch { }

        try
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                if (textNode->NodeText.StringPtr.HasValue)
                    entry.Text = textNode->NodeText.ToString() ?? "";
            }
            else if (node->Type == NodeType.Counter)
            {
                var counterNode = (AtkCounterNode*)node;
                entry.Text = counterNode->NodeText.ToString() ?? "";
            }
        }
        catch { }

        results.Add(entry);

        if (typeVal >= 1000)
        {
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component != null)
            {
                entry.ComponentAddress = (nint)compNode->Component;
                var childCount = compNode->Component->UldManager.NodeListCount;
                for (var i = 0; i < childCount; i++)
                {
                    var child = compNode->Component->UldManager.NodeList[i];
                    if (child != null)
                        CollectDebugEntries(child, $"{path}→[{i}]", results, depth + 1);
                }
            }
        }
    }

    private static unsafe void AppendComponentChildLines(List<string> lines, AtkComponentBase* component)
    {
        if (component == null)
            return;

        var childCount = component->UldManager.NodeListCount;
        for (var i = 0; i < childCount; i++)
        {
            var child = component->UldManager.NodeList[i];
            if (child == null)
                continue;

            var childType = (ushort)child->Type;
            var line = $"[{i}] #{child->NodeId} {GetNodeTypeDisplay(child->Type, childType)} | Visible={child->IsVisible()}";

            try
            {
                var evt = child->AtkEventManager.Event;
                if (evt != null)
                    line += $" | event:{evt->Param} @ 0x{(nint)evt:X}";
            }
            catch
            {
            }

            var text = TryGetNodeText(child);
            if (!string.IsNullOrWhiteSpace(text))
                line += $" | Text=\"{TrimForLine(text)}\"";

            line += $" | Addr=0x{(nint)child:X}";
            lines.Add(line);
        }
    }

    private static unsafe void AppendTreeListLines(NodeRuntimeReport report, AtkComponentTreeList* treeList)
    {
        if (treeList == null)
            return;

        var itemCount = treeList->GetItemCount();
        report.TreeListLines.Add($"Item Count: {itemCount}");
        report.TreeListLines.Add($"List Length: {treeList->ListLength}");
        report.TreeListLines.Add($"Selected Item Index: {treeList->SelectedItemIndex}");
        report.TreeListLines.Add($"Held Item Index: {treeList->HeldItemIndex}");
        report.TreeListLines.Add($"Hovered Item Index: {treeList->HoveredItemIndex}");
        report.TreeListLines.Add($"Hovered Item Index 2: {treeList->HoveredItemIndex2}");
        report.TreeListLines.Add($"Hovered Item Index 3: {treeList->HoveredItemIndex3}");
        report.TreeListLines.Add($"First Visible Item Index: {treeList->FirstVisibleItemIndex}");
        report.TreeListLines.Add($"Pending First Visible Item Index: {treeList->PendingFirstVisibleItemIndex}");
        report.TreeListLines.Add($"Visible Row Count: {treeList->VisibleRowCount}");
        report.TreeListLines.Add($"Num Visible Items: {treeList->NumVisibleItems}");
        report.TreeListLines.Add($"Num Visible Rows: {treeList->NumVisibleRows}");
        report.TreeListLines.Add($"Num Visible Columns: {treeList->NumVisibleColumns}");
        report.TreeListLines.Add($"Scroll Offset: {treeList->ScrollOffset}");
        report.TreeListLines.Add($"Scroll Max Index: {treeList->ScrollMaxIndex}");
        report.TreeListLines.Add($"Row Step: ({treeList->RowStepX}, {treeList->RowStepY})");
        report.TreeListLines.Add($"Column Count: {treeList->ColumnCount}");
        report.TreeListLines.Add($"Column Step: ({treeList->ColumnStepX}, {treeList->ColumnStepY})");
        report.TreeListLines.Add($"List Size: {treeList->ListWidth} x {treeList->ListHeight}");
        report.TreeListLines.Add($"Item Size: {treeList->ItemWidth} x {treeList->ItemHeight}");
        report.TreeListLines.Add($"ScrollBar Component: {FormatPointer((nint)treeList->ScrollBarComponent)}");
        report.TreeListLines.Add($"Collision Node: {FormatPointer((nint)treeList->CollisionNode)}");
        report.TreeListLines.Add($"Hovered Item Collision Node: {FormatPointer((nint)treeList->HoveredItemCollisionNode)}");
        report.TreeListLines.Add($"Item Renderer List: {FormatPointer((nint)treeList->ItemRendererList)}");
        report.TreeListLines.Add($"Allocated Renderer Length: {treeList->AllocatedItemRendererListLength}");
        report.TreeListLines.Add($"First Renderer: {FormatPointer((nint)treeList->FirstAtkComponentListItemRenderer)}");
        report.TreeListLines.Add($"Wrap: {treeList->Wrap}");
        report.TreeListLines.Add($"Vertical Scroll: {treeList->IsVerticalScroll}");
        report.TreeListLines.Add($"ScrollBar Enabled: {treeList->IsScrollBarEnabled}");
        report.TreeListLines.Add($"ScrollBar Visible: {treeList->IsScrollBarVisible}");
        report.TreeListLines.Add($"Scroll Snapping: {treeList->IsScrollSnappingEnabled}");
        report.TreeListLines.Add($"Item Interaction Enabled: {treeList->IsItemInteractionEnabled}");
        report.TreeListLines.Add($"Item Click Enabled: {treeList->IsItemClickEnabled}");
        report.TreeListLines.Add($"Input Top/Bottom Enabled: {treeList->IsInputTopBottomEnabled}");
        report.TreeListLines.Add($"Input Menu Option Enabled: {treeList->IsInputMenuOptionEnabled}");
        report.TreeListLines.Add($"Update Pending: {treeList->IsUpdatePending}");
        report.TreeListLines.Add($"Scroll Refresh Pending: {treeList->IsScrollRefreshPending}");

        var firstVisible = Math.Max(0, (int)treeList->FirstVisibleItemIndex);
        var visibleItems = Math.Max(0, (int)treeList->NumVisibleItems);
        if (visibleItems <= 0)
            visibleItems = Math.Max(0, (int)treeList->VisibleRowCount);
        if (visibleItems <= 0)
            visibleItems = Math.Min(itemCount, 6);

        var lastExclusive = Math.Min(itemCount, firstVisible + Math.Max(visibleItems, 1));
        for (var i = firstVisible; i < lastExclusive && report.TreeListRendererLines.Count < 10; i++)
        {
            AtkComponentListItemRenderer* renderer;
            try
            {
                renderer = treeList->GetItemRenderer(i);
            }
            catch
            {
                continue;
            }

            if (renderer == null)
                continue;

            var componentNode = renderer->GetComponentNode();
            var activeNode = renderer->GetActiveNode();
            var text = renderer->ButtonTextNode != null ? renderer->ButtonTextNode->NodeText.ToString() ?? string.Empty : string.Empty;
            var line = $"Row {i} | Renderer={FormatPointer((nint)renderer)} | Owner={FormatPointer((nint)renderer->OwnerNode)} " +
                       $"| ComponentNode={FormatPointer((nint)componentNode)} | ActiveNode={FormatPointer((nint)activeNode)}";

            if (renderer->ButtonTextNode != null)
                line += $" | TextNode={FormatPointer((nint)renderer->ButtonTextNode)}";
            if (!string.IsNullOrWhiteSpace(text))
                line += $" | Text=\"{TrimForLine(text)}\"";
            if (renderer->ButtonBGNode != null)
                line += $" | BGNode={FormatPointer((nint)renderer->ButtonBGNode)}";

            line += $" | IsActive={renderer->IsActive}";
            report.TreeListRendererLines.Add(line);
        }
    }

    private static unsafe void AppendEventLines(List<string> lines, string label, AtkResNode* node)
    {
        if (node == null)
            return;

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt == null)
            {
                lines.Add($"{label} Event: <none>");
                return;
            }

            lines.Add($"{label} Event: 0x{(nint)evt:X}");
            lines.Add($"{label} Event Param: {evt->Param}");
        }
        catch
        {
            lines.Add($"{label} Event: <error>");
        }
    }

    private static unsafe string TryGetNodeText(AtkResNode* node)
    {
        if (node == null)
            return string.Empty;

        try
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                return textNode->NodeText.StringPtr.HasValue ? textNode->NodeText.ToString() ?? string.Empty : string.Empty;
            }

            if (node->Type == NodeType.Counter)
            {
                var counterNode = (AtkCounterNode*)node;
                return counterNode->NodeText.ToString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string TrimForLine(string text, int maxLength = 72)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "...";
    }

    private static string FormatPointer(nint address)
        => address == 0 ? "0x0" : $"0x{address:X}";

    private static void AppendMultilineLines(List<string> target, string header, List<string> lines)
    {
        if (lines.Count == 0)
            return;

        target.Add(string.Empty);
        target.Add(header);
        target.AddRange(lines);
    }

    private static string GetNodeTypeDisplay(NodeType type, ushort typeVal)
    {
        if (typeVal >= 1000)
            return ComponentTypeNames.TryGetValue(typeVal, out var ct) ? ct : $"Component({typeVal})";

        return type.ToString();
    }

    private static nint TryFindAgentAddress(string addonName)
    {
        try
        {
            var agent = Plugin.GameGui.FindAgentInterface(addonName);
            return agent.IsNull ? 0 : agent.Address;
        }
        catch
        {
            return 0;
        }
    }

    private static void MarkSelectedAddonLookupMatches(AddonDebugSnapshot snapshot)
    {
        foreach (var instance in snapshot.GameGuiAddonInstances)
            instance.MatchesSelectedSnapshot = instance.Address == snapshot.Address;

        if (snapshot.RaptureAddonInstance != null)
            snapshot.RaptureAddonInstance.MatchesSelectedSnapshot = snapshot.RaptureAddonInstance.Address == snapshot.Address;
    }

    private static string ResolveSelectedAddonLookupSource(AddonDebugSnapshot snapshot)
    {
        foreach (var instance in snapshot.GameGuiAddonInstances)
        {
            if (instance.Address == snapshot.Address)
                return $"GameGui[{instance.Index}]";
        }

        if (snapshot.RaptureAddonInstance != null && snapshot.RaptureAddonInstance.Address == snapshot.Address)
            return "RaptureAtkUnitManager";

        return "Unknown";
    }

    private static unsafe List<AddonLookupInstanceInfo> ScanGameGuiAddonInstances(string addonName)
    {
        var results = new List<AddonLookupInstanceInfo>();

        for (var index = 1; index <= 10; index++)
        {
            try
            {
                var addonHandle = Plugin.GameGui.GetAddonByName(addonName, index);
                if (addonHandle.IsNull)
                    continue;

                var addon = (AtkUnitBase*)addonHandle.Address;
                if (addon == null)
                    continue;

                results.Add(CreateAddonLookupInstanceInfo(addon, "GameGui", index));
            }
            catch
            {
            }
        }

        return results;
    }

    private static unsafe AddonLookupInstanceInfo? ScanRaptureAddonInstance(string addonName)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
                return null;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null)
                return null;

            var addon = unitManager->GetAddonByName(addonName);
            return addon == null ? null : CreateAddonLookupInstanceInfo(addon, "RaptureAtkUnitManager", 0);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe AddonLookupInstanceInfo CreateAddonLookupInstanceInfo(AtkUnitBase* addon, string source, int index)
    {
        return new AddonLookupInstanceInfo
        {
            Source = source,
            Index = index,
            Address = (nint)addon,
            IsVisible = addon->IsVisible,
            IsReady = addon->IsReady,
            Position = new Vector2(addon->X, addon->Y),
            Size = new Vector2(addon->RootNode != null ? addon->RootNode->Width : 0,
                               addon->RootNode != null ? addon->RootNode->Height : 0),
            Scale = addon->Scale,
            NodeCount = addon->UldManager.NodeListCount,
        };
    }

    private static unsafe AtkUnitBase* TryGetAddonByName(string addonName)
    {
        try
        {
            var visibleAddon = Plugin.GameGui.GetAddonByName(addonName, 1);
            if (!visibleAddon.IsNull)
                return (AtkUnitBase*)visibleAddon.Address;
        }
        catch
        {
        }

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
                return null;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null)
                return null;

            return unitManager->GetAddonByName(addonName);
        }
        catch
        {
            return null;
        }
    }

    private static unsafe List<AddonAtkValueInfo> ScanAtkValues(AtkUnitBase* addon)
    {
        var results = new List<AddonAtkValueInfo>();
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 0)
            return results;

        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = &addon->AtkValues[i];
            var typeRaw = (ushort)value->Type;
            results.Add(new AddonAtkValueInfo
            {
                Index = i,
                TypeName = typeRaw == 0 ? "Not Set" : value->Type.ToString(),
                TypeRaw = typeRaw,
                Value = FormatAtkValueDisplay(value),
                Address = (nint)value,
            });
        }

        return results;
    }

    private static unsafe string FormatAtkValueDisplay(AtkValue* value)
    {
        if (value == null)
            return "null";

        try
        {
            return value->Type switch
            {
                0 => "(not set)",
                AtkValueType.Int => value->Int.ToString(),
                AtkValueType.UInt => value->UInt.ToString(),
                AtkValueType.Bool => (value->Byte != 0).ToString(),
                AtkValueType.Pointer => $"0x{(nint)value->Pointer:X}",
                AtkValueType.ManagedString or AtkValueType.String or AtkValueType.String8
                    => value->String.Value == null ? "null" : value->String.ToString() ?? string.Empty,
                _ => $"Unhandled type {value->Type}"
            };
        }
        catch (Exception ex)
        {
            return $"<error: {ex.Message}>";
        }
    }

    private static unsafe List<NodeInfo> ScanNodes(AtkUnitBase* addon)
    {
        var nodes = new List<NodeInfo>();
        if (addon == null) return nodes;

        var addonPos = new Vector2(addon->X, addon->Y);
        var count = addon->UldManager.NodeListCount;

        for (var i = 0; i < count; i++)
        {
            AtkResNode* node;
            try { node = addon->UldManager.NodeList[i]; }
            catch { continue; }
            if (node == null) continue;

            var typeVal = (ushort)node->Type;
            var nodeInfo = new NodeInfo
            {
                Index = i,
                NodeId = node->NodeId,
                Type = node->Type,
                TypeRaw = typeVal,
                IsVisible = node->IsVisible(),
                Position = new Vector2(node->X, node->Y),
                ScreenPosition = addonPos + new Vector2(node->ScreenX - addon->X, node->ScreenY - addon->Y),
                Size = new Vector2(node->Width, node->Height),
                Address = (nint)node,
            };

            // Check for events (interactive)
            try
            {
                var evt = node->AtkEventManager.Event;
                if (evt != null)
                {
                    nodeInfo.HasEvents = true;
                    nodeInfo.EventParam = evt->Param;
                    nodeInfo.IsInteractive = true;
                }
            }
            catch { }

            // Text nodes
            try
            {
                if (node->Type == NodeType.Text)
                {
                    var textNode = (AtkTextNode*)node;
                    if (textNode->NodeText.StringPtr.HasValue)
                        nodeInfo.Text = textNode->NodeText.ToString() ?? "";
                }
            }
            catch { }

            // Component node detection
            if (typeVal >= 1000)
            {
                nodeInfo.IsComponentNode = true;
                nodeInfo.ComponentType = ComponentTypeNames.TryGetValue(typeVal, out var ct) ? ct : $"Component({typeVal})";
                if (InteractiveComponentTypes.Contains(typeVal))
                    nodeInfo.IsInteractive = true;
            }

            nodes.Add(nodeInfo);
        }
        return nodes;
    }
}
