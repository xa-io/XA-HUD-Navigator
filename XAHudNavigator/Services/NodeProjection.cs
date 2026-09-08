using System;
using System.Collections.Generic;
using System.Linq;

namespace XAHudNavigator.Services;

internal static class NodeProjection
{
    internal static List<NativeNodeSlot> Project(IReadOnlyList<nint> nativeSlots)
    {
        ArgumentNullException.ThrowIfNull(nativeSlots);
        var projected = new List<NativeNodeSlot>(nativeSlots.Count);
        for (var index = 0; index < nativeSlots.Count; index++)
        {
            if (nativeSlots[index] != 0)
                projected.Add(new NativeNodeSlot(index, nativeSlots[index]));
        }

        return projected;
    }

    internal static T? FindByNativeIndex<T>(IEnumerable<T> nodes, Func<T, int> getIndex, int nativeIndex)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(getIndex);
        return nodes.FirstOrDefault(node => getIndex(node) == nativeIndex);
    }
}

internal readonly record struct NativeNodeSlot(int Index, nint Address);
