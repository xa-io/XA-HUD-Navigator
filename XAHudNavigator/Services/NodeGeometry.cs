using System.Numerics;

namespace XAHudNavigator.Services;

internal static class NodeGeometry
{
    internal static (Vector2 Min, Vector2 Size) ComputeNodeRect(
        float screenX,
        float screenY,
        float width,
        float height,
        float nodeScaleX,
        float nodeScaleY,
        float addonScale)
    {
        var resolvedAddonScale = ResolveScale(addonScale);
        var scaleX = ResolveScale(nodeScaleX) * resolvedAddonScale;
        var scaleY = ResolveScale(nodeScaleY) * resolvedAddonScale;
        return (
            new Vector2(ResolveCoordinate(screenX), ResolveCoordinate(screenY)),
            new Vector2(ResolveExtent(width) * scaleX, ResolveExtent(height) * scaleY));
    }

    internal static Vector2 ComputeAddonSize(float width, float height, float addonScale)
    {
        var resolvedAddonScale = ResolveScale(addonScale);
        return new Vector2(ResolveExtent(width), ResolveExtent(height)) * resolvedAddonScale;
    }

    private static float ResolveScale(float value)
        => float.IsFinite(value) && value > 0f ? value : 1f;

    private static float ResolveCoordinate(float value)
        => float.IsFinite(value) ? value : 0f;

    private static float ResolveExtent(float value)
        => float.IsFinite(value) && value > 0f ? value : 0f;
}
