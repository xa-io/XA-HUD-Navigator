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
    public List<AddonDebugEntry> Entries { get; set; } = new();
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
            var addon = AtkStage.Instance()->RaptureAtkUnitManager->GetAddonByName(name);
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
            var stage = AtkStage.Instance();
            if (stage == null) return null;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null) return null;

            var addon = unitManager->GetAddonByName(name);
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
            };

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

        if (!string.IsNullOrEmpty(entry.Text))
            parts.Add($"Text=\"{entry.Text}\"");

        parts.Add($"Addr=0x{entry.Address:X}");
        return string.Join(" | ", parts);
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
                if (textNode->NodeText.StringPtr != null)
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

    private static string GetNodeTypeDisplay(NodeType type, ushort typeVal)
    {
        if (typeVal >= 1000)
            return ComponentTypeNames.TryGetValue(typeVal, out var ct) ? ct : $"Component({typeVal})";

        return type.ToString();
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
                    if (textNode->NodeText.StringPtr != null)
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
