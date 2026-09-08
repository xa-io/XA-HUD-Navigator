using System;
using System.Globalization;

namespace XAHudNavigator.Services;

internal static class TextDisplay
{
    internal static string Truncate(string? text, int maxTextElements, string emptyText = "<empty>", string suffix = "...")
    {
        if (string.IsNullOrEmpty(text))
            return emptyText;
        ArgumentOutOfRangeException.ThrowIfNegative(maxTextElements);

        var textInfo = new StringInfo(text);
        if (textInfo.LengthInTextElements <= maxTextElements)
            return text;

        return textInfo.SubstringByTextElements(0, maxTextElements) + suffix;
    }
}
