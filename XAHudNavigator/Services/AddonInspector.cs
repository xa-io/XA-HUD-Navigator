using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
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
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public List<string> Diagnostics { get; set; } = new();
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
    private const int MaxNativeNodeSlots = 16384;
    private const int MaxNativeAtkValueSlots = 16384;
    private const int MaxDebugEntries = 16384;
    private const int MaxDebugDepth = 5;
    private const int MaxInspectionDiagnostics = 64;
    internal const int MaxNativeStringBytes = 4096;
    internal const int MaxNodeEvents = 32;
    internal const int MaxNodeNativeFields = 256;
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

    internal static bool IsPlausibleNativeCount(int count, int maximum)
    {
        return maximum >= 0 && count >= 0 && count <= maximum;
    }

    internal static int ClampInlineNativeCount(int count, int capacity)
    {
        return count <= 0 || capacity <= 0 ? 0 : Math.Min(count, capacity);
    }

    internal static unsafe bool TrySnapshotPointerArray(
        nint pointerArrayAddress,
        int count,
        int maximum,
        out nint[] pointers)
    {
        pointers = Array.Empty<nint>();
        if (!IsPlausibleNativeCount(count, maximum))
            return false;
        if (count == 0)
            return true;
        if (pointerArrayAddress == 0
            || (nuint)count > nuint.MaxValue / (nuint)IntPtr.Size)
            return false;

        var byteLength = (nuint)count * (nuint)IntPtr.Size;
        if (!ClientStructRuntimeInspector.IsReadableRange(pointerArrayAddress, byteLength))
            return false;

        var source = (nint*)pointerArrayAddress;
        var snapshot = new nint[count];
        for (var i = 0; i < count; i++)
            snapshot[i] = source[i];

        pointers = snapshot;
        return true;
    }

    /// <summary>
    /// Scans all loaded addons in the AtkUnitManager and returns inspection data.
    /// Always returns ALL addons (visible and hidden) for the full list.
    /// </summary>
    public static unsafe List<AddonInfo> ScanAllAddons()
        => ScanAllAddonsCore(false);

    internal static unsafe List<AddonInfo> CaptureAllAddons()
        => ScanAllAddonsCore(true);

    private static unsafe List<AddonInfo> ScanAllAddonsCore(bool reportFailure)
    {
        var results = new List<AddonInfo>();

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null) return results;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null) return results;

            // Slot zero retains stale data when Count is zero, so never read it unconditionally.
            AtkUnitBase* focusedAddon = null;
            try
            {
                var focusedList = &unitManager->AtkUnitManager.FocusedUnitsList;
                var focusedRawCount = (int)focusedList->Count;
                var focusedCount = ClampInlineNativeCount(focusedRawCount, focusedList->Entries.Length);
                if (focusedRawCount > focusedCount)
                {
                    DiagnosticLog.WarningOnce(
                        "ScanAllAddons.FocusedCount",
                        $"Clamped focused-addon count ({focusedRawCount}) to inline capacity ({focusedList->Entries.Length}).");
                }

                if (focusedCount > 0)
                    focusedAddon = focusedList->Entries[0].Value;
            }
            catch (Exception ex)
            {
                DiagnosticLog.VerboseThrottled("ScanAllAddons.FocusedUnitsList", ex);
                focusedAddon = null;
            }

            var allLoadedList = &unitManager->AtkUnitManager.AllLoadedUnitsList;
            var loadedRawCount = (int)allLoadedList->Count;
            var loadedCount = ClampInlineNativeCount(loadedRawCount, allLoadedList->Entries.Length);
            if (loadedRawCount > loadedCount)
            {
                DiagnosticLog.WarningOnce(
                    "ScanAllAddons.InvalidCount",
                    $"Clamped loaded-addon count ({loadedRawCount}) to inline capacity ({allLoadedList->Entries.Length}).");
            }

            for (var i = 0; i < loadedCount; i++)
            {
                AtkUnitBase* addon;
                try { addon = allLoadedList->Entries[i].Value; }
                catch (Exception ex) { DiagnosticLog.VerboseThrottled("ScanAllAddons.AllLoadedUnitsList", ex); continue; }
                if (addon == null) continue;
                if (!ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase)))
                {
                    DiagnosticLog.WarningOnce(
                        "ScanAllAddons.UnreadableAddon",
                        "Skipped an unreadable loaded-addon pointer.");
                    continue;
                }

                string name;
                try
                {
                    var nameSpan = addon->Name;
                    var nullIdx = nameSpan.IndexOf((byte)0);
                    name = nullIdx > 0
                        ? Encoding.UTF8.GetString(nameSpan.Slice(0, nullIdx))
                        : $"<unnamed_{i}>";
                }
                catch (Exception ex) { DiagnosticLog.VerboseThrottled("ScanAllAddons.Name", ex); name = $"<error_{i}>"; }

                var info = new AddonInfo
                {
                    Name = name,
                    IsVisible = addon->IsVisible,
                    IsReady = addon->IsReady,
                    Position = new Vector2(addon->X, addon->Y),
                    Size = ComputeSafeAddonSize(addon),
                    Scale = addon->Scale,
                    NodeCount = addon->UldManager.NodeListCount,
                    Address = (nint)addon,
                    IsFocused = focusedAddon != null && addon == focusedAddon,
                };

                results.Add(info);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ScanAllAddons", ex);
            if (reportFailure)
                throw;
        }

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
            if (addon == null
                || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase)))
                return null;

            var info = new AddonInfo
            {
                Name = name,
                IsVisible = addon->IsVisible,
                IsReady = addon->IsReady,
                Position = new Vector2(addon->X, addon->Y),
                Size = ComputeSafeAddonSize(addon),
                Scale = addon->Scale,
                NodeCount = addon->UldManager.NodeListCount,
                Address = (nint)addon,
            };
            info.Nodes = ScanNodes(addon);
            return info;
        }
        catch (Exception ex) { DiagnosticLog.VerboseThrottled("ScanSingleAddon", ex); return null; }
    }

    public static unsafe AddonDebugSnapshot? ScanDebugAddon(string name)
    {
        try
        {
            var addon = TryGetAddonByName(name);
            if (addon == null
                || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase)))
                return null;

            var snapshot = new AddonDebugSnapshot
            {
                Name = name,
                IsVisible = addon->IsVisible,
                IsReady = addon->IsReady,
                Position = new Vector2(addon->X, addon->Y),
                Size = ComputeSafeAddonSize(addon),
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

            var nodeCount = (int)addon->UldManager.NodeListCount;
            if (!TrySnapshotPointerArray(
                    (nint)addon->UldManager.NodeList,
                    nodeCount,
                    MaxNativeNodeSlots,
                    out var nodeSlots))
            {
                DiagnosticLog.WarningOnce(
                    "ScanDebugAddon.InvalidNodeList",
                    $"Skipped an invalid or unreadable debug-node list ({nodeCount} slots).");
            }
            else
            {
                var traversal = new DebugTraversalContext(snapshot.Entries, snapshot.Diagnostics);
                for (var i = 0; i < nodeSlots.Length && !traversal.IsExhausted; i++)
                {
                    var node = (AtkResNode*)nodeSlots[i];
                    CollectDebugEntriesCore(node, $"[{i}]", traversal, 0);
                }
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ScanDebugAddon", ex);
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

    public static unsafe List<AddonAtkValueInfo> ScanAddonAtkValues(string addonName)
    {
        try
        {
            var addon = TryGetAddonByName(addonName);
            return addon == null ? new List<AddonAtkValueInfo>() : ScanAtkValues(addon);
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ScanAddonAtkValues", ex);
            return new List<AddonAtkValueInfo>();
        }
    }

    public static string FormatAddonLookupEntry(AddonLookupInstanceInfo entry)
    {
        var label = entry.Source.Equals("GameGui", StringComparison.OrdinalIgnoreCase)
            ? $"GameGui[{entry.Index}]"
            : entry.Source;
        var selectedSuffix = entry.MatchesSelectedSnapshot ? " | SELECTED" : string.Empty;
        return FormattableString.Invariant(
            $"{label} | Visible={entry.IsVisible} Ready={entry.IsReady} Nodes={entry.NodeCount} Pos=({entry.Position.X:F1}, {entry.Position.Y:F1}) Size={entry.Size.X:F1}x{entry.Size.Y:F1} Scale={entry.Scale:F2} Addr=0x{entry.Address:X}{selectedSuffix}");
    }

    public static NodeRuntimeReport? BuildNodeRuntimeReport(string addonName, int nodeIndex)
    {
        try
        {
            return BuildNodeRuntimeReportCore(addonName, nodeIndex);
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("BuildNodeRuntimeReport", ex);
            return null;
        }
    }

    private static unsafe NodeRuntimeReport? BuildNodeRuntimeReportCore(string addonName, int nodeIndex)
    {
        var addon = TryGetAddonByName(addonName);
        if (addon == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase))
            || !addon->IsVisible
            || addon->UldManager.NodeList == null
            || nodeIndex < 0)
            return null;

        var nodeCount = (int)addon->UldManager.NodeListCount;
        if (!IsPlausibleNativeCount(nodeCount, MaxNativeNodeSlots)
            || nodeIndex >= nodeCount)
            return null;

        if (!TrySnapshotPointerArray(
                (nint)addon->UldManager.NodeList,
                nodeCount,
                MaxNativeNodeSlots,
                out var nodeSlots))
            return null;

        var node = (AtkResNode*)nodeSlots[nodeIndex];
        if (node == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)node, (nuint)sizeof(AtkResNode)))
            return null;

        var report = new NodeRuntimeReport
        {
            AddonName = addonName,
            NodeIndex = nodeIndex,
        };

        AppendNodeNativeLines(report.SummaryLines, node);
        AppendEventLines(report.SummaryLines, "Node", node);

        var typeVal = (ushort)node->Type;
        if (typeVal < 1000)
            return report;

        var componentNode = (AtkComponentNode*)node;
        if (!IsReadable(componentNode))
        {
            report.SummaryLines.Add("Component Node: <unreadable>");
            return report;
        }
        if (componentNode->Component == null)
        {
            report.SummaryLines.Add("Component Address: 0x0");
            return report;
        }

        var component = componentNode->Component;
        if (!ClientStructRuntimeInspector.IsReadableRange((nint)component, (nuint)sizeof(AtkComponentBase)))
        {
            report.SummaryLines.Add("Component Address: <unreadable>");
            return report;
        }

        report.SummaryLines.Add($"Component Address: 0x{(nint)component:X}");
        report.SummaryLines.Add($"Component Type: {GetNodeTypeDisplay(node->Type, typeVal)}");
        report.SummaryLines.Add($"Owner Node: {FormatPointer((nint)component->OwnerNode)}");
        report.SummaryLines.Add($"Component Child Count: {component->UldManager.NodeListCount}");

        AtkResNode* focusNode = null;
        try
        {
            focusNode = component->GetFocusNode();
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("BuildNodeRuntimeReport.GetFocusNode", ex);
        }

        report.SummaryLines.Add($"Focus Node: {FormatPointer((nint)focusNode)}");
        if (focusNode != null)
            AppendEventLines(report.SummaryLines, "Focus Node", focusNode);

        AppendComponentChildLines(report.ComponentChildLines, component);

        if (typeVal == 1011)
            AppendTreeListLines(report, (AtkComponentTreeList*)component);

        return report;
    }

    internal static unsafe void AppendNodeNativeLines(List<string> lines, AtkResNode* node)
    {
        if (!IsReadable(node))
        {
            lines.Add("Native Node: <unreadable>");
            return;
        }

        var (type, size) = node->Type switch
        {
            NodeType.Image => (typeof(AtkImageNode), sizeof(AtkImageNode)),
            NodeType.NineGrid => (typeof(AtkNineGridNode), sizeof(AtkNineGridNode)),
            NodeType.Text => (typeof(AtkTextNode), sizeof(AtkTextNode)),
            NodeType.Counter => (typeof(AtkCounterNode), sizeof(AtkCounterNode)),
            _ when (ushort)node->Type >= 1000 => (typeof(AtkComponentNode), sizeof(AtkComponentNode)),
            _ => (typeof(AtkResNode), sizeof(AtkResNode)),
        };

        if (!ClientStructRuntimeInspector.IsReadableRange((nint)node, (nuint)size))
        {
            lines.Add($"Native Node ({type.Name}): <unreadable derived fields>");
            return;
        }

        try
        {
            var snapshot = ClientStructRuntimeInspector.SnapshotStruct(
                "Native Node", $"NodeId {node->NodeId}", (nint)node, type,
                $"Concrete node layout selected from NodeType {(ushort)node->Type}.");
            lines.Add($"Native Node: {snapshot.TypeName} | Addr=0x{snapshot.Address:X} | Size=0x{snapshot.Size:X}");
            lines.Add(snapshot.ResolutionInfo);
            var count = Math.Min(snapshot.Fields.Count, MaxNodeNativeFields);
            for (var index = 0; index < count; index++)
                lines.Add(ClientStructRuntimeInspector.FormatFieldEntry(snapshot.Fields[index]));
            if (snapshot.IsTruncated || snapshot.Fields.Count > count)
                lines.Add($"Native Node: field preview truncated after {count} rows.");

            if (node->Type == NodeType.Image)
            {
                var image = (AtkImageNode*)node;
                AppendImageResourceLines(lines, image->PartsList, image->PartId);
            }
            else if (node->Type == NodeType.NineGrid)
            {
                var nineGrid = (AtkNineGridNode*)node;
                AppendImageResourceLines(lines, nineGrid->PartsList, nineGrid->PartId);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Native Node ({type.Name}): <inspection unavailable>");
            DiagnosticLog.VerboseThrottled("AppendNodeNativeLines", ex);
        }
    }

    internal static unsafe void AppendImageResourceLines(List<string> lines, AtkUldPartsList* partsList, uint partId)
    {
        if (partsList == null)
        {
            lines.Add("Image Parts: <none>");
            return;
        }
        if (!IsReadable(partsList))
        {
            lines.Add("Image Parts: <unreadable>");
            return;
        }

        var partCount = partsList->PartCount;
        lines.Add($"Image Parts: ListId={partsList->Id} | Count={partCount} | SelectedPart={partId} | Addr=0x{(nint)partsList:X}");
        if (partCount > MaxNativeNodeSlots || partId >= partCount)
        {
            lines.Add("Image Part: <invalid count or selected index>");
            return;
        }
        if (!ClientStructRuntimeInspector.TryAddAddress((nint)partsList->Parts, checked((int)partId * sizeof(AtkUldPart)), out var partAddress)
            || !IsReadable((AtkUldPart*)partAddress))
        {
            lines.Add("Image Part: <unreadable>");
            return;
        }

        var part = (AtkUldPart*)partAddress;
        lines.Add($"Image Part: U={part->U} V={part->V} Width={part->Width} Height={part->Height} | Addr=0x{partAddress:X}");
        var asset = part->UldAsset;
        if (!IsReadable(asset))
        {
            lines.Add(asset == null ? "Image Asset: <none>" : "Image Asset: <unreadable>");
            return;
        }

        var texture = &asset->AtkTexture;
        lines.Add($"Image Asset: Id={asset->Id} | TextureType={texture->TextureType} | Addr=0x{(nint)asset:X}");
        if (texture->TextureType != TextureType.Resource)
        {
            lines.Add("Image Resource: unavailable for this texture type.");
            return;
        }
        var resource = texture->Resource;
        if (!IsReadable(resource))
        {
            lines.Add(resource == null ? "Image Resource: <none>" : "Image Resource: <unreadable>");
            return;
        }

        lines.Add($"Image Resource: IconId={resource->IconId} | PathHash=0x{resource->TexPathHash:X8} | Addr=0x{(nint)resource:X}");
        lines.Add($"Image Resource Handles: File=0x{(nint)resource->TexFileResourceHandle:X} | KernelTexture=0x{(nint)resource->KernelTextureObject:X}");
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
        => CollectDebugEntriesCore(node, path, new DebugTraversalContext(results, new List<string>()), depth);

    internal static unsafe void CollectDebugNodeSnapshot(AtkResNode* node, List<AddonDebugEntry> results, List<string> diagnostics)
        => CollectDebugEntriesCore(node, "[0]", new DebugTraversalContext(results, diagnostics), 0);

    private static unsafe void CollectDebugEntriesCore(AtkResNode* node, string path, DebugTraversalContext context, int depth)
    {
        if (node == null)
            return;
        if (!context.TakeVisit())
            return;
        if (depth > MaxDebugDepth || context.Entries.Count >= MaxDebugEntries)
        {
            context.AddDiagnostic($"{path}: traversal stopped at the depth or entry limit.");
            return;
        }
        if (!IsReadable(node))
        {
            context.AddDiagnostic($"{path}: unreadable node {FormatPointer((nint)node)}.");
            return;
        }
        if (!context.Visited.Add((nint)node))
        {
            context.AddDiagnostic($"{path}: repeated node {FormatPointer((nint)node)} skipped (shared reference or cycle).");
            return;
        }

        var typeVal = (ushort)node->Type;
        var displayPath = node->Type == NodeType.Counter ? $"{path}[ctr]" : path;
        var entry = new AddonDebugEntry
        {
            Path = displayPath,
            NodeId = node->NodeId,
            TypeName = GetNodeTypeDisplay(node->Type, typeVal),
            TypeRaw = typeVal,
            IsVisible = node->NodeFlags.HasFlag(NodeFlags.Visible),
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

        if (TryReadFirstEvent(node, out var eventParam, out _, out var eventError))
        {
            entry.HasEvents = true;
            entry.EventParam = eventParam;
            entry.IsInteractive = true;
        }
        else if (!string.IsNullOrEmpty(eventError))
            context.AddDiagnostic($"{path}: {eventError}");

        entry.Text = TryGetNodeText(node);
        context.Entries.Add(entry);

        if (typeVal >= 1000)
        {
            var compNode = (AtkComponentNode*)node;
            if (!IsReadable(compNode))
            {
                context.AddDiagnostic($"{path}: unreadable component-node fields.");
                return;
            }
            if (compNode->Component != null
                && ClientStructRuntimeInspector.IsReadableRange(
                    (nint)compNode->Component,
                    (nuint)sizeof(AtkComponentBase)))
            {
                entry.ComponentAddress = (nint)compNode->Component;
                var childCount = (int)compNode->Component->UldManager.NodeListCount;
                if (!IsPlausibleNativeCount(childCount, MaxNativeNodeSlots))
                {
                    context.AddDiagnostic($"{path}: invalid component-node count ({childCount}).");
                    return;
                }
                var inspectedChildCount = Math.Min(childCount, context.RemainingVisits);
                if (inspectedChildCount < childCount)
                    context.AddDiagnostic($"{path}: child list truncated to the remaining traversal budget ({inspectedChildCount}/{childCount}).");
                if (!TrySnapshotPointerArray(
                        (nint)compNode->Component->UldManager.NodeList,
                        inspectedChildCount,
                        MaxNativeNodeSlots,
                        out var childSlots))
                {
                    DiagnosticLog.WarningOnce(
                        "CollectDebugEntries.InvalidChildList",
                        $"Skipped an invalid or unreadable recursive component-node list ({childCount} slots).");
                    context.AddDiagnostic($"{path}: invalid or unreadable component-node list ({childCount} slots).");
                    return;
                }

                for (var i = 0; i < childSlots.Length && !context.IsExhausted; i++)
                {
                    var child = (AtkResNode*)childSlots[i];
                    if (child != null)
                        CollectDebugEntriesCore(child, $"{path}→[{i}]", context, depth + 1);
                }
            }
            else if (compNode->Component != null)
                context.AddDiagnostic($"{path}: unreadable component {FormatPointer((nint)compNode->Component)}.");
        }
    }

    private sealed class DebugTraversalContext(List<AddonDebugEntry> entries, List<string> diagnostics)
    {
        internal List<AddonDebugEntry> Entries { get; } = entries;
        internal HashSet<nint> Visited { get; } = new();
        internal int RemainingVisits { get; private set; } = MaxDebugEntries;
        internal bool IsExhausted => RemainingVisits <= 0;

        internal bool TakeVisit()
        {
            if (IsExhausted)
                return false;
            RemainingVisits--;
            if (IsExhausted)
                AddDiagnostic("The node traversal limit was reached; remaining references were omitted.");
            return true;
        }

        internal void AddDiagnostic(string message)
        {
            if (diagnostics.Count < MaxInspectionDiagnostics)
                diagnostics.Add(message);
        }
    }

    private static unsafe void AppendComponentChildLines(List<string> lines, AtkComponentBase* component)
    {
        if (component == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)component, (nuint)sizeof(AtkComponentBase)))
            return;

        var childCount = (int)component->UldManager.NodeListCount;
        if (!TrySnapshotPointerArray(
                (nint)component->UldManager.NodeList,
                childCount,
                MaxNativeNodeSlots,
                out var childSlots))
        {
            DiagnosticLog.WarningOnce(
                "AppendComponentChildLines.InvalidList",
                $"Skipped an invalid or unreadable component-node list ({childCount} slots).");
            return;
        }

        for (var i = 0; i < childSlots.Length; i++)
        {
            var child = (AtkResNode*)childSlots[i];
            if (child == null
                || !ClientStructRuntimeInspector.IsReadableRange((nint)child, (nuint)sizeof(AtkResNode)))
                continue;

            var childType = (ushort)child->Type;
            var line = $"[{i}] #{child->NodeId} {GetNodeTypeDisplay(child->Type, childType)} | Visible={child->NodeFlags.HasFlag(NodeFlags.Visible)}";

            if (TryReadFirstEvent(child, out var eventParam, out var eventAddress, out var eventError))
            {
                line += $" | event:{eventParam} @ 0x{eventAddress:X}";
            }
            else if (!string.IsNullOrEmpty(eventError))
                line += $" | Event: {eventError}";

            var text = TryGetNodeText(child);
            if (!string.IsNullOrWhiteSpace(text))
                line += $" | Text=\"{TrimForLine(text)}\"";

            line += $" | Addr=0x{(nint)child:X}";
            lines.Add(line);
        }
    }

    private static unsafe void AppendTreeListLines(NodeRuntimeReport report, AtkComponentTreeList* treeList)
    {
        if (treeList == null
            || !ClientStructRuntimeInspector.IsReadableRange(
                (nint)treeList,
                (nuint)sizeof(AtkComponentTreeList)))
            return;

        var itemCount = treeList->GetItemCount();
        report.TreeListLines.Add($"Item Count: {itemCount}");
        if (itemCount < 0)
        {
            report.TreeListLines.Add("Renderer inspection skipped: invalid item count.");
            return;
        }
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

        var rowsToInspect = Math.Min(Math.Max(visibleItems, 1), 128);
        var lastExclusive = (int)Math.Min(itemCount, (long)firstVisible + rowsToInspect);
        for (var i = firstVisible; i < lastExclusive && report.TreeListRendererLines.Count < 10; i++)
        {
            AtkComponentListItemRenderer* renderer;
            try
            {
                renderer = treeList->GetItemRenderer(i);
            }
            catch (Exception ex)
            {
                DiagnosticLog.VerboseThrottled("AppendTreeListLines.GetItemRenderer", ex);
                continue;
            }

            AppendTreeListRendererLine(report.TreeListRendererLines, i, renderer);
        }
    }

    internal static unsafe void AppendTreeListRendererLine(List<string> lines, int index, AtkComponentListItemRenderer* renderer)
    {
        if (!IsReadable(renderer))
        {
            if (renderer != null)
                lines.Add($"Row {index} | Renderer={FormatPointer((nint)renderer)} | <unreadable>");
            return;
        }

        var componentNode = renderer->GetComponentNode();
        var activeNode = renderer->GetActiveNode();
        var text = TryGetNodeText((AtkResNode*)renderer->ButtonTextNode);
        var line = $"Row {index} | Renderer={FormatPointer((nint)renderer)} | Owner={FormatPointer((nint)renderer->OwnerNode)} " +
                   $"| ComponentNode={FormatPointer((nint)componentNode)} | ActiveNode={FormatPointer((nint)activeNode)}";

        if (renderer->ButtonTextNode != null)
            line += $" | TextNode={FormatPointer((nint)renderer->ButtonTextNode)}";
        if (!string.IsNullOrWhiteSpace(text))
            line += $" | Text=\"{TrimForLine(text)}\"";
        if (renderer->ButtonBGNode != null)
            line += $" | BGNode={FormatPointer((nint)renderer->ButtonBGNode)}";

        line += $" | IsActive={renderer->IsActive}";
        lines.Add(line);
    }

    internal static unsafe void AppendEventLines(List<string> lines, string label, AtkResNode* node)
    {
        if (node == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)node, (nuint)sizeof(AtkResNode)))
            return;

        try
        {
            var evt = node->AtkEventManager.Event;
            if (evt == null)
            {
                lines.Add($"{label} Event: <none>");
                return;
            }

            var visited = new HashSet<nint>();
            for (var index = 0; evt != null; index++)
            {
                if (index >= MaxNodeEvents)
                {
                    lines.Add($"{label} Events: truncated after {MaxNodeEvents} entries.");
                    break;
                }
                if (!visited.Add((nint)evt))
                {
                    lines.Add($"{label} Events: cycle at {FormatPointer((nint)evt)}.");
                    break;
                }
                if (!IsReadable(evt))
                {
                    lines.Add($"{label} Event: {FormatPointer((nint)evt)} <unreadable>");
                    break;
                }
                var eventLabel = index == 0 ? label : $"{label} [{index}]";
                lines.Add($"{eventLabel} Event: 0x{(nint)evt:X}");
                lines.Add($"{eventLabel} Event Param: {evt->Param}");
                lines.Add($"{eventLabel} Listener: {FormatPointer((nint)evt->Listener)} | Target: {FormatPointer((nint)evt->Target)}");
                evt = evt->NextEvent;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("AppendEventLines", ex);
            lines.Add($"{label} Event: <error>");
        }
    }

    internal static unsafe string TryGetNodeText(AtkResNode* node)
    {
        if (node == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)node, (nuint)sizeof(AtkResNode)))
            return string.Empty;

        try
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                return IsReadable(textNode)
                    ? ReadNativeString((nint)textNode->NodeText.StringPtr.Value)
                    : "<unreadable text node>";
            }

            if (node->Type == NodeType.Counter)
            {
                var counterNode = (AtkCounterNode*)node;
                return IsReadable(counterNode)
                    ? ReadNativeString((nint)counterNode->NodeText.StringPtr.Value)
                    : "<unreadable counter node>";
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("TryGetNodeText", ex);
        }

        return string.Empty;
    }

    private static unsafe bool IsReadable<T>(T* value) where T : unmanaged
        => value != null && ClientStructRuntimeInspector.IsReadableRange((nint)value, (nuint)sizeof(T));

    internal static unsafe bool TryReadFirstEvent(AtkResNode* node, out uint param, out nint address, out string error)
    {
        param = 0;
        address = 0;
        error = string.Empty;
        if (!IsReadable(node))
        {
            error = "unreadable event owner";
            return false;
        }
        var evt = node->AtkEventManager.Event;
        if (evt == null)
            return false;
        address = (nint)evt;
        if (!IsReadable(evt))
        {
            error = $"unreadable event {FormatPointer(address)}";
            return false;
        }
        param = evt->Param;
        return true;
    }

    internal static string ReadNativeString(nint address, bool wide = false)
    {
        if (address == 0)
            return string.Empty;

        // Validate each page before reading it, allowing a terminator before an unreadable next page.
        var stride = wide ? 2 : 1;
        var bytes = new byte[MaxNativeStringBytes];
        var length = 0;
        try
        {
            while (length < bytes.Length)
            {
                if (!ClientStructRuntimeInspector.TryAddAddress(address, length, out var current))
                    return "<unreadable>";
                var pageRemaining = Environment.SystemPageSize - (int)((nuint)current % (nuint)Environment.SystemPageSize);
                var chunkBytes = Math.Min(bytes.Length - length, pageRemaining);
                // A misaligned UTF-16 code unit can straddle a page boundary.
                if (chunkBytes < stride)
                    chunkBytes = stride;
                chunkBytes -= chunkBytes % stride;
                if (!ClientStructRuntimeInspector.IsReadableRange(current, (nuint)chunkBytes))
                    return "<unreadable>";

                var chunkEnd = Math.Min(bytes.Length, length + chunkBytes);
                while (length < chunkEnd)
                {
                    var first = Marshal.ReadByte(address, length);
                    var second = wide ? Marshal.ReadByte(address, length + 1) : (byte)0;
                    if (first == 0 && second == 0)
                        return (wide ? Encoding.Unicode : Encoding.UTF8).GetString(bytes, 0, length);
                    bytes[length++] = first;
                    if (wide)
                        bytes[length++] = second;
                }
            }
            return (wide ? Encoding.Unicode : Encoding.UTF8).GetString(bytes) + "… <truncated>";
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("AddonInspector.ReadNativeString", ex);
            return "<unreadable>";
        }
    }

    private static string TrimForLine(string text, int maxLength = 72)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return TextDisplay.Truncate(trimmed, maxLength, string.Empty);
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
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("TryFindAgentAddress", ex);
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
                if (addonHandle.IsNull || addonHandle.Address == nint.Zero)
                    continue;

                var addon = (AtkUnitBase*)addonHandle.Address;
                if (addon == null)
                    continue;

                var instance = CreateAddonLookupInstanceInfo(addon, "GameGui", index);
                if (instance != null)
                    results.Add(instance);
            }
            catch (Exception ex)
            {
                DiagnosticLog.VerboseThrottled("ScanGameGuiAddonInstances", ex);
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
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ScanRaptureAddonInstance", ex);
            return null;
        }
    }

    private static unsafe AddonLookupInstanceInfo? CreateAddonLookupInstanceInfo(AtkUnitBase* addon, string source, int index)
    {
        if (addon == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase)))
            return null;

        return new AddonLookupInstanceInfo
        {
            Source = source,
            Index = index,
            Address = (nint)addon,
            IsVisible = addon->IsVisible,
            IsReady = addon->IsReady,
            Position = new Vector2(addon->X, addon->Y),
            Size = ComputeSafeAddonSize(addon),
            Scale = addon->Scale,
            NodeCount = addon->UldManager.NodeListCount,
        };
    }

    private static unsafe AtkUnitBase* TryGetAddonByName(string addonName)
    {
        try
        {
            var visibleAddon = Plugin.GameGui.GetAddonByName(addonName, 1);
            if (!visibleAddon.IsNull && visibleAddon.Address != nint.Zero)
            {
                var addon = (AtkUnitBase*)visibleAddon.Address;
                if (addon != null)
                    return addon;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("TryGetAddonByName.GameGui", ex);
        }

        try
        {
            var stage = AtkStage.Instance();
            if (stage == null)
                return null;

            var unitManager = stage->RaptureAtkUnitManager;
            if (unitManager == null)
                return null;

            var addon = unitManager->GetAddonByName(addonName);
            return addon == null ? null : addon;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("TryGetAddonByName.Rapture", ex);
            return null;
        }
    }

    private static unsafe Vector2 ComputeSafeAddonSize(AtkUnitBase* addon)
    {
        if (addon == null)
            return Vector2.Zero;

        var rootNode = addon->RootNode;
        if (rootNode == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)rootNode, (nuint)sizeof(AtkResNode)))
            return Vector2.Zero;

        return NodeGeometry.ComputeAddonSize(rootNode->Width, rootNode->Height, addon->Scale);
    }

    internal static unsafe List<AddonAtkValueInfo> ScanAtkValues(AtkUnitBase* addon)
    {
        var results = new List<AddonAtkValueInfo>();
        if (addon == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase))
            || addon->AtkValues == null
            || addon->AtkValuesCount <= 0)
            return results;

        var count = (int)addon->AtkValuesCount;
        if (!IsPlausibleNativeCount(count, MaxNativeAtkValueSlots))
        {
            DiagnosticLog.WarningOnce(
                "ScanAtkValues.InvalidCount",
                $"Skipped addon values for an implausible native count ({count}).");
            return results;
        }

        var byteLength = (nuint)count * (nuint)sizeof(AtkValue);
        if (!ClientStructRuntimeInspector.IsReadableRange((nint)addon->AtkValues, byteLength))
        {
            DiagnosticLog.WarningOnce(
                "ScanAtkValues.UnreadableRange",
                "Skipped an unreadable native addon-value range.");
            return results;
        }

        for (var i = 0; i < count; i++)
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
                AtkValueType.Null => "(null)",
                AtkValueType.Int => value->Int.ToString(CultureInfo.InvariantCulture),
                AtkValueType.Int64 => value->Int64.ToString(CultureInfo.InvariantCulture),
                AtkValueType.UInt => value->UInt.ToString(CultureInfo.InvariantCulture),
                AtkValueType.UInt64 => value->UInt64.ToString(CultureInfo.InvariantCulture),
                AtkValueType.Float => value->Float.ToString("G9", CultureInfo.InvariantCulture),
                AtkValueType.Bool => (value->Byte != 0).ToString(),
                AtkValueType.Pointer => $"0x{(nint)value->Pointer:X}",
                AtkValueType.ManagedString or AtkValueType.String or AtkValueType.ConstString
                    => value->String.Value == null ? "null" : ReadNativeString((nint)value->String.Value),
                AtkValueType.WideString
                    => value->WideString == null ? "null" : ReadNativeString((nint)value->WideString, true),
                AtkValueType.Vector or AtkValueType.ManagedVector
                    => $"Vector @ {FormatPointer((nint)value->Vector)} (contents not traversed)",
                AtkValueType.AtkValues
                    => $"AtkValues @ {FormatPointer((nint)value->AtkValues)} (count unavailable)",
                _ => $"Unsupported type {value->Type} ({(ushort)value->Type})"
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("FormatAtkValueDisplay", ex);
            return "<unreadable>";
        }
    }

    internal static unsafe List<NodeInfo> ScanNodes(AtkUnitBase* addon)
    {
        var nodes = new List<NodeInfo>();
        if (addon == null
            || !ClientStructRuntimeInspector.IsReadableRange((nint)addon, (nuint)sizeof(AtkUnitBase)))
            return nodes;

        var count = (int)addon->UldManager.NodeListCount;
        if (addon->UldManager.NodeList == null || count <= 0)
            return nodes;
        if (!IsPlausibleNativeCount(count, MaxNativeNodeSlots))
        {
            DiagnosticLog.WarningOnce(
                "ScanNodes.InvalidCount",
                $"Refusing an implausible native node count ({count}).");
            return nodes;
        }

        if (!TrySnapshotPointerArray(
                (nint)addon->UldManager.NodeList,
                count,
                MaxNativeNodeSlots,
                out var nativeSlots))
        {
            DiagnosticLog.WarningOnce(
                "ScanNodes.UnreadableNodeList",
                "Skipped an invalid or unreadable native addon-node list.");
            return nodes;
        }

        foreach (var slot in NodeProjection.Project(nativeSlots))
        {
            var node = (AtkResNode*)slot.Address;
            if (!ClientStructRuntimeInspector.IsReadableRange((nint)node, (nuint)sizeof(AtkResNode)))
            {
                DiagnosticLog.WarningOnce(
                    "ScanNodes.UnreadableNode",
                    "Skipped an unreadable native addon-node pointer.");
                continue;
            }

            var typeVal = (ushort)node->Type;
            var geometry = NodeGeometry.ComputeNodeRect(
                node->ScreenX,
                node->ScreenY,
                node->Width,
                node->Height,
                node->ScaleX,
                node->ScaleY,
                addon->Scale);
            var nodeInfo = new NodeInfo
            {
                Index = slot.Index,
                NodeId = node->NodeId,
                Type = node->Type,
                TypeRaw = typeVal,
                IsVisible = node->NodeFlags.HasFlag(NodeFlags.Visible),
                Position = new Vector2(node->X, node->Y),
                ScreenPosition = geometry.Min,
                Size = geometry.Size,
                Address = (nint)node,
            };

            // Read the event storage independently of the node's readable range.
            if (TryReadFirstEvent(node, out var eventParam, out _, out _))
            {
                nodeInfo.HasEvents = true;
                nodeInfo.EventParam = eventParam;
                nodeInfo.IsInteractive = true;
            }
            nodeInfo.Text = TryGetNodeText(node);

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
