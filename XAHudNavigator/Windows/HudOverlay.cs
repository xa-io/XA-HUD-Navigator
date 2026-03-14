using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using XAHudNavigator.Services;

namespace XAHudNavigator.Windows;

/// <summary>
/// Transparent fullscreen overlay that draws colored bounding boxes.
/// Only draws nodes for the SELECTED addon (not all addons) to avoid clutter.
/// Optionally shows a mouse-hover tooltip with addon/node info.
/// </summary>
public class HudOverlay : Window, IDisposable
{
    private readonly Plugin plugin;

    private static readonly uint AddonOutlineColor = 0xFF00FFFF;  // cyan
    private static readonly uint InteractiveColor = 0xFF00FF00;   // bright green
    private static readonly uint TextNodeColor = 0xFFFFCC44;      // gold
    private static readonly uint NonInteractiveColor = 0xFF888888; // grey
    private static readonly uint LabelBgColor = 0xCC000000;       // semi-transparent black
    private static readonly uint HoverColor = 0xFFFF8800;         // orange

    public HudOverlay(Plugin plugin)
        : base("XA HUD Navigator Overlay##XAHudNavigatorOverlay",
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoBringToFrontOnFocus)
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        Position = Vector2.Zero;
        PositionCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        if (!plugin.Configuration.OverlayEnabled) return;

        var mainWindow = plugin.GetMainWindow();
        var selectedAddonName = mainWindow?.SelectedAddonName;

        // Only draw for the selected addon to avoid the clutter seen in screenshots
        AddonInfo? selectedAddon = null;
        if (!string.IsNullOrEmpty(selectedAddonName))
            selectedAddon = AddonInspector.ScanSingleAddon(selectedAddonName);

        if (selectedAddon == null || !selectedAddon.IsVisible) return;

        var drawList = ImGui.GetForegroundDrawList();
        var showBoxes = plugin.Configuration.OverlayShowBoundingBoxes;
        var showIds = plugin.Configuration.OverlayShowNodeIds;
        var showNames = plugin.Configuration.OverlayShowAddonNames;
        var interactiveOnly = plugin.Configuration.OverlayShowInteractiveOnly;

        // If ALL drawing options are off, draw nothing (no clutter)
        if (!showBoxes && !showIds && !showNames && !interactiveOnly)
            return;

        // Draw addon bounding box
        if (showBoxes && selectedAddon.Size.X > 0 && selectedAddon.Size.Y > 0)
        {
            var addonMin = selectedAddon.Position;
            var addonMax = selectedAddon.Position + selectedAddon.Size;
            drawList.AddRect(addonMin, addonMax, AddonOutlineColor, 0, ImDrawFlags.None, 2.0f);
        }

        // Draw addon name label
        if (showNames)
        {
            var labelPos = selectedAddon.Position + new Vector2(2, -16);
            var label = $"{selectedAddon.Name} [{selectedAddon.NodeCount}]";
            var textSize = ImGui.CalcTextSize(label);
            drawList.AddRectFilled(labelPos, labelPos + textSize + new Vector2(6, 2), LabelBgColor);
            drawList.AddText(labelPos + new Vector2(3, 0), AddonOutlineColor, label);
        }

        // Skip node drawing entirely if boxes and IDs are both off
        if (!showBoxes && !showIds)
            return;

        // Get hovered node index from main window for pulsation
        var hoveredIdx = mainWindow?.HoveredNodeIndex ?? -1;
        var pulseAlpha = (float)(0.5 + 0.5 * Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds * 6.0)); // pulsate ~1Hz

        // Draw node outlines for selected addon only
        foreach (var node in selectedAddon.Nodes)
        {
            if (!node.IsVisible) continue;
            if (node.Size.X <= 0 || node.Size.Y <= 0) continue;
            if (interactiveOnly && !node.IsInteractive) continue;

            var nodeMin = node.ScreenPosition;
            var nodeMax = nodeMin + node.Size;

            // Pulsation: if this node is hovered in the table, override with bright pulsating outline
            if (hoveredIdx == node.Index)
            {
                var pulseByte = (uint)(byte)(255 * pulseAlpha);
                var pulseColor = 0xFF000000u | (pulseByte << 16) | (pulseByte << 8) | 0xFFu; // pulsating cyan
                drawList.AddRect(nodeMin, nodeMax, pulseColor, 0, ImDrawFlags.None, 3.0f);
                // Filled highlight with low alpha
                var fillAlpha = (byte)(60 * pulseAlpha);
                var fillColor = (uint)((fillAlpha << 24) | 0x00FFFF);
                drawList.AddRectFilled(nodeMin, nodeMax, fillColor);
                // Always show label for pulsating node
                var pulseLabel = $"[{node.Index}] {node.TypeDisplay}";
                var pulseLabelSize = ImGui.CalcTextSize(pulseLabel);
                drawList.AddRectFilled(nodeMin, nodeMin + pulseLabelSize + new Vector2(4, 2), LabelBgColor);
                drawList.AddText(nodeMin + new Vector2(2, 0), pulseColor, pulseLabel);
                continue;
            }

            uint outlineColor;
            float thickness;
            if (node.IsInteractive)
            {
                outlineColor = InteractiveColor;
                thickness = 1.5f;
            }
            else if (node.Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text && !string.IsNullOrEmpty(node.Text))
            {
                outlineColor = TextNodeColor;
                thickness = 1.0f;
            }
            else
            {
                outlineColor = NonInteractiveColor;
                thickness = 0.5f;
            }

            drawList.AddRect(nodeMin, nodeMax, outlineColor, 0, ImDrawFlags.None, thickness);

            // Labels for interactive and component nodes only (reduces clutter)
            if (showIds && (node.IsInteractive || node.IsComponentNode))
            {
                var idLabel = $"[{node.Index}] {node.TypeDisplay}";
                var idSize = ImGui.CalcTextSize(idLabel);
                drawList.AddRectFilled(nodeMin, nodeMin + idSize + new Vector2(4, 2), LabelBgColor);
                drawList.AddText(nodeMin + new Vector2(2, 0), outlineColor, idLabel);
            }
        }

        // Mouse hover tooltip on game screen
        var mousePos = ImGui.GetMousePos();
        if (mousePos.X >= selectedAddon.Position.X && mousePos.X <= selectedAddon.Position.X + selectedAddon.Size.X &&
            mousePos.Y >= selectedAddon.Position.Y && mousePos.Y <= selectedAddon.Position.Y + selectedAddon.Size.Y)
        {
            // Find the node under the mouse
            NodeInfo? hoveredNode = null;
            foreach (var node in selectedAddon.Nodes)
            {
                if (!node.IsVisible || node.Size.X <= 0 || node.Size.Y <= 0) continue;
                var nMin = node.ScreenPosition;
                var nMax = nMin + node.Size;
                if (mousePos.X >= nMin.X && mousePos.X <= nMax.X && mousePos.Y >= nMin.Y && mousePos.Y <= nMax.Y)
                {
                    hoveredNode = node;
                    break; // first match (top-most in list order)
                }
            }

            if (hoveredNode != null)
            {
                // Highlight the hovered node
                var hMin = hoveredNode.ScreenPosition;
                var hMax = hMin + hoveredNode.Size;
                drawList.AddRect(hMin, hMax, HoverColor, 0, ImDrawFlags.None, 2.5f);

                // Draw tooltip near mouse
                var tooltipPos = mousePos + new Vector2(15, 15);
                var lines = new[]
                {
                    $"[{hoveredNode.Index}] {hoveredNode.TypeDisplay}",
                    $"NodeId: {hoveredNode.NodeId}  Raw: {hoveredNode.TypeRaw}",
                    $"Pos: ({hoveredNode.Position.X:F0},{hoveredNode.Position.Y:F0})  Size: {hoveredNode.Size.X:F0}x{hoveredNode.Size.Y:F0}",
                    hoveredNode.IsInteractive ? "INTERACTIVE" : "",
                    !string.IsNullOrEmpty(hoveredNode.Text) ? $"\"{(hoveredNode.Text.Length > 30 ? hoveredNode.Text[..30] + "..." : hoveredNode.Text)}\"" : "",
                };

                float maxW = 0;
                var lineH = ImGui.GetTextLineHeight();
                var validLines = new List<string>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    validLines.Add(line);
                    var w = ImGui.CalcTextSize(line).X;
                    if (w > maxW) maxW = w;
                }

                var bgSize = new Vector2(maxW + 10, validLines.Count * lineH + 6);
                drawList.AddRectFilled(tooltipPos, tooltipPos + bgSize, LabelBgColor, 3);
                drawList.AddRect(tooltipPos, tooltipPos + bgSize, HoverColor, 3, ImDrawFlags.None, 1);

                var y = tooltipPos.Y + 3;
                foreach (var line in validLines)
                {
                    var color = line == "INTERACTIVE" ? InteractiveColor : 0xFFFFFFFF;
                    drawList.AddText(new Vector2(tooltipPos.X + 5, y), color, line);
                    y += lineH;
                }
            }
        }
    }
}
