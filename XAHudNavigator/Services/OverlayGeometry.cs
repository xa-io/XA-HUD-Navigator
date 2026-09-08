using System;
using System.Collections.Generic;
using System.Numerics;

namespace XAHudNavigator.Services;

internal static class OverlayGeometry
{
    internal static bool HasFinitePositiveRect(Vector2 position, Vector2 size)
        => float.IsFinite(position.X)
           && float.IsFinite(position.Y)
           && float.IsFinite(size.X)
           && float.IsFinite(size.Y)
           && size.X > 0f
           && size.Y > 0f;

    internal static bool ShouldDrawNode(NodeInfo node, bool interactiveOnly)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.IsVisible
               && (!interactiveOnly || node.IsInteractive)
               && HasFinitePositiveRect(node.ScreenPosition, node.Size);
    }

    internal static NodeInfo? FindHoveredNode(
        IReadOnlyList<NodeInfo> nodes,
        Vector2 mousePosition,
        bool interactiveOnly)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!float.IsFinite(mousePosition.X) || !float.IsFinite(mousePosition.Y))
            return null;

        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            var node = nodes[index];
            if (!ShouldDrawNode(node, interactiveOnly))
                continue;

            var maximum = node.ScreenPosition + node.Size;
            if (mousePosition.X >= node.ScreenPosition.X
                && mousePosition.X <= maximum.X
                && mousePosition.Y >= node.ScreenPosition.Y
                && mousePosition.Y <= maximum.Y)
            {
                return node;
            }
        }

        return null;
    }
}
