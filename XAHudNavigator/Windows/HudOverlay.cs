using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XAHudNavigator.Services;

namespace XAHudNavigator.Windows;

/// <summary>
/// Transparent fullscreen overlay that draws colored bounding boxes.
/// Only draws nodes for the SELECTED addon (not all addons) to avoid clutter.
/// Optionally shows a mouse-hover tooltip with addon/node info.
/// </summary>
public sealed class HudOverlay : Window, IDisposable
{
    private readonly Plugin plugin;
    private static float UiScale => ImGuiHelpers.GlobalScale;

    private static readonly uint AddonOutlineColor = 0xFFFFFF00;  // cyan
    private static readonly uint InteractiveColor = 0xFF00FF00;   // bright green
    private static readonly uint TextNodeColor = 0xFF44CCFF;      // gold
    private static readonly uint NonInteractiveColor = 0xFF888888; // grey
    private static readonly uint LabelBgColor = 0xCC000000;       // semi-transparent black
    private static readonly uint HoverColor = 0xFF0088FF;         // orange

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
        InhibitAtkCollision = false;
        Position = Vector2.Zero;
        PositionCondition = ImGuiCond.Always;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        var io = ImGui.GetIO();
        Size = io.DisplaySize / ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        if (!plugin.Configuration.OverlayEnabled) return;

        var frame = plugin.AddonSnapshots.Current;
        if (!frame.IsCurrent) return;
        var selectedAddon = frame.SelectedAddon;

        if (selectedAddon == null
            || !selectedAddon.IsVisible
            || !OverlayGeometry.HasFinitePositiveRect(selectedAddon.Position, selectedAddon.Size))
            return;

        var drawList = ImGui.GetForegroundDrawList();
        var showBoxes = plugin.Configuration.OverlayShowBoundingBoxes;
        var showIds = plugin.Configuration.OverlayShowNodeIds;
        var showNames = plugin.Configuration.OverlayShowAddonNames;
        var interactiveOnly = plugin.Configuration.OverlayShowInteractiveOnly;

        // If ALL drawing options are off, draw nothing (no clutter)
        if (!showBoxes && !showIds && !showNames)
            return;

        // Draw addon bounding box
        if (showBoxes)
        {
            var addonMin = selectedAddon.Position;
            var addonMax = selectedAddon.Position + selectedAddon.Size;
            drawList.AddRect(addonMin, addonMax, AddonOutlineColor, 0, ImDrawFlags.None, Scale(2.0f));
        }

        // Draw addon name label
        if (showNames)
        {
            var labelPos = selectedAddon.Position + ScaledVector(2f, -16f);
            var label = $"{selectedAddon.Name} [{selectedAddon.NodeCount}]";
            var textSize = ImGui.CalcTextSize(label);
            drawList.AddRectFilled(labelPos, labelPos + textSize + ScaledVector(6f, 2f), LabelBgColor);
            drawList.AddText(labelPos + ScaledVector(3f, 0f), AddonOutlineColor, label);
        }

        // Skip node drawing entirely if boxes and IDs are both off
        if (!showBoxes && !showIds)
            return;

        // Get hovered node index from main window for pulsation
        var hoveredIdx = plugin.AddonSnapshots.HoveredNodeIndex;
        var pulseAlpha = (float)(0.5 + 0.5 * Math.Sin(DateTime.UtcNow.TimeOfDay.TotalSeconds * 6.0)); // pulsate ~1Hz

        // Draw node outlines for selected addon only
        foreach (var node in selectedAddon.Nodes)
        {
            if (!OverlayGeometry.ShouldDrawNode(node, interactiveOnly)) continue;

            var nodeMin = node.ScreenPosition;
            var nodeMax = nodeMin + node.Size;

            // Pulsation: if this node is hovered in the table, override with bright pulsating outline
            if (hoveredIdx == node.Index)
            {
                var pulseByte = (uint)(byte)(255 * pulseAlpha);
                var pulseColor = 0xFF000000u | (pulseByte << 16) | (pulseByte << 8); // pulsating cyan
                drawList.AddRect(nodeMin, nodeMax, pulseColor, 0, ImDrawFlags.None, Scale(3.0f));
                // Filled highlight with low alpha
                var fillAlpha = (byte)(60 * pulseAlpha);
                var fillColor = (uint)((fillAlpha << 24) | 0x00FFFF00);
                drawList.AddRectFilled(nodeMin, nodeMax, fillColor);
                // Always show label for pulsating node
                var pulseLabel = $"[{node.Index}] {node.TypeDisplay}";
                var pulseLabelSize = ImGui.CalcTextSize(pulseLabel);
                drawList.AddRectFilled(nodeMin, nodeMin + pulseLabelSize + ScaledVector(4f, 2f), LabelBgColor);
                drawList.AddText(nodeMin + ScaledVector(2f, 0f), pulseColor, pulseLabel);
                continue;
            }

            uint outlineColor;
            float thickness;
            if (node.IsInteractive)
            {
                outlineColor = InteractiveColor;
                thickness = Scale(1.5f);
            }
            else if (node.Type == FFXIVClientStructs.FFXIV.Component.GUI.NodeType.Text && !string.IsNullOrEmpty(node.Text))
            {
                outlineColor = TextNodeColor;
                thickness = Scale(1.0f);
            }
            else
            {
                outlineColor = NonInteractiveColor;
                thickness = Scale(0.5f);
            }

            drawList.AddRect(nodeMin, nodeMax, outlineColor, 0, ImDrawFlags.None, thickness);

            // Labels for interactive and component nodes only (reduces clutter)
            if (showIds && (node.IsInteractive || node.IsComponentNode))
            {
                var idLabel = $"[{node.Index}] {node.TypeDisplay}";
                var idSize = ImGui.CalcTextSize(idLabel);
                drawList.AddRectFilled(nodeMin, nodeMin + idSize + ScaledVector(4f, 2f), LabelBgColor);
                drawList.AddText(nodeMin + ScaledVector(2f, 0f), outlineColor, idLabel);
            }
        }

        // Mouse hover tooltip on game screen
        var mousePos = ImGui.GetMousePos();
        if (mousePos.X >= selectedAddon.Position.X && mousePos.X <= selectedAddon.Position.X + selectedAddon.Size.X &&
            mousePos.Y >= selectedAddon.Position.Y && mousePos.Y <= selectedAddon.Position.Y + selectedAddon.Size.Y)
        {
            // Resolve in reverse draw order so overlapping nodes select the top-most entry.
            var hoveredNode = OverlayGeometry.FindHoveredNode(
                selectedAddon.Nodes,
                mousePos,
                interactiveOnly);

            if (hoveredNode != null)
            {
                // Highlight the hovered node
                var hMin = hoveredNode.ScreenPosition;
                var hMax = hMin + hoveredNode.Size;
                drawList.AddRect(hMin, hMax, HoverColor, 0, ImDrawFlags.None, Scale(2.5f));

                // Draw tooltip near mouse
                var tooltipPos = mousePos + ScaledVector(15f, 15f);
                var lines = new[]
                {
                    $"[{hoveredNode.Index}] {hoveredNode.TypeDisplay}",
                    $"NodeId: {hoveredNode.NodeId}  Raw: {hoveredNode.TypeRaw}",
                    $"Pos: ({hoveredNode.Position.X:F0},{hoveredNode.Position.Y:F0})  Size: {hoveredNode.Size.X:F0}x{hoveredNode.Size.Y:F0}",
                    hoveredNode.IsInteractive ? "INTERACTIVE" : "",
                    !string.IsNullOrEmpty(hoveredNode.Text) ? $"\"{TextDisplay.Truncate(hoveredNode.Text, 30, string.Empty)}\"" : "",
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

                var bgSize = new Vector2(maxW + Scale(10f), validLines.Count * lineH + Scale(6f));
                drawList.AddRectFilled(tooltipPos, tooltipPos + bgSize, LabelBgColor, Scale(3f));
                drawList.AddRect(tooltipPos, tooltipPos + bgSize, HoverColor, Scale(3f), ImDrawFlags.None, Scale(1f));

                var y = tooltipPos.Y + Scale(3f);
                foreach (var line in validLines)
                {
                    var color = line == "INTERACTIVE" ? InteractiveColor : 0xFFFFFFFF;
                    drawList.AddText(new Vector2(tooltipPos.X + Scale(5f), y), color, line);
                    y += lineH;
                }
            }
        }
    }

    private static float Scale(float value)
        => value * UiScale;

    private static Vector2 ScaledVector(float x, float y)
        => ImGuiHelpers.ScaledVector2(x, y);
}
