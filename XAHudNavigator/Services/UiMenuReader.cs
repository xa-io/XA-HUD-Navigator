using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

internal static unsafe class UiMenuReader
{
    internal const int MaxEntries = 128;
    internal const int MaxStringBytes = 4096;
    private const int MaxNativeCount = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static UiMenuSnapshot? Capture(string addonName, AtkUnitBase* addon)
    {
        if (addonName is not ("SelectString" or "SelectIconString" or "SelectYesno" or "Talk" or "ContextMenu"))
            return null;

        var state = new CaptureState(addonName, addon);
        if (!Readable(addon))
        {
            state.Problem("The supported addon is unavailable or unreadable.");
            state.Decoded = false;
            return state.Finish();
        }
        state.Visible = addon->IsVisible;
        state.Field("Addon ID", addon->Id, "AtkUnitBase.Id", (nint)addon);
        try
        {
            switch (addonName)
            {
                case "SelectString" when Readable((AddonSelectString*)addon):
                    ReadPopup(state, &((AddonSelectString*)addon)->PopupMenu.PopupMenu);
                    break;
                case "SelectIconString" when Readable((AddonSelectIconString*)addon):
                    ReadPopup(state, &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu);
                    break;
                case "SelectYesno" when Readable((AddonSelectYesno*)addon):
                    ReadYesno(state, (AddonSelectYesno*)addon);
                    break;
                case "Talk" when Readable((AddonTalk*)addon):
                    ReadTalk(state, (AddonTalk*)addon);
                    break;
                case "ContextMenu":
                    state.Decoded = false;
                    state.Problem("This ClientStructs AddonContextMenu layout exposes no typed menu entries; inspect the raw values and node fallback.");
                    break;
                default:
                    state.Decoded = false;
                    state.Problem("The addon's derived native layout is unreadable.");
                    break;
            }
        }
        catch (Exception ex)
        {
            state.Problem($"Menu inspection stopped: {ex.GetType().Name}.");
        }
        return state.Finish();
    }

    private static void ReadPopup(CaptureState state, PopupMenu* popup)
    {
        var count = popup->EntryCount;
        state.Field("Entry count", count, "PopupMenu.EntryCount", (nint)popup);
        ReadWindowTitle(state, popup->Window);
        if (count < 0 || count > MaxNativeCount)
        {
            state.Problem("The popup entry count is outside the supported bounds.");
            return;
        }
        if (count > MaxEntries)
            state.Problem($"The popup is truncated to {MaxEntries} entries.");

        var list = popup->List;
        var listReadable = Readable(list);
        if (listReadable)
        {
            state.Field("List length", list->ListLength, "PopupMenu.List.ListLength", (nint)list);
            state.Field("Selected index", list->SelectedItemIndex, "PopupMenu.List.SelectedItemIndex", (nint)list);
            if (list->ListLength != count)
                state.Problem("Popup and list counts differ; optional row metadata may be unavailable.");
        }

        for (var index = 0; index < Math.Min(count, MaxEntries); index++)
        {
            var source = $"PopupMenu.EntryNames[{index}]";
            string label;
            nint textAddress = 0;
            if (TryElement((nint)popup->EntryNames, index, sizeof(nint), out var slot))
            {
                textAddress = *(nint*)slot;
                label = state.Text(textAddress, source);
            }
            else
            {
                label = "<unavailable>";
                state.Problem($"{source} is unreadable.");
            }

            var fields = new List<UiMenuField> { new("Label pointer", Pointer(textAddress), source) };
            bool? enabled = null;
            uint? iconId = null;
            uint? nodeId = null;
            bool? selected = null;
            if (listReadable && list->ListLength == count)
            {
                selected = list->SelectedItemIndex == index;
                var allocated = list->AllocatedItemRendererListLength;
                if (allocated >= 0 && allocated <= MaxNativeCount && index < allocated
                    && TryElement((nint)list->ItemRendererList, index, sizeof(AtkComponentList.ListItem), out var itemAddress))
                {
                    var item = (AtkComponentList.ListItem*)itemAddress;
                    fields.Add(new("Stored icon ID", item->IconId.ToString(CultureInfo.InvariantCulture), $"AtkComponentList.ItemRendererList[{index}].IconId"));
                    fields.Add(new("Stored disabled flag", item->IsDisabled.ToString(), $"AtkComponentList.ItemRendererList[{index}].IsDisabled"));
                    var renderer = item->AtkComponentListItemRenderer;
                    // Renderers can be recycled for a different logical row. Do not attach their state to the wrong entry.
                    if (Readable(renderer) && renderer->ListItemIndex == index)
                    {
                        enabled = !item->IsDisabled;
                        iconId = item->IconId;
                        var owner = (AtkResNode*)renderer->OwnerNode;
                        if (Readable(owner))
                            nodeId = owner->NodeId;
                    }
                }
            }
            state.Entries.Add(new(index, label, source)
            {
                Enabled = enabled,
                Selected = selected,
                IconId = iconId,
                NodeId = nodeId,
                Fields = fields.ToArray(),
            });
        }
    }

    private static void ReadWindowTitle(CaptureState state, AtkComponentWindow* window)
    {
        if (window == null)
            return;
        if (!Readable(window))
        {
            state.Problem("The popup window is unreadable.");
            return;
        }
        var titleId = window->NodeIds[0];
        state.Field("Title node ID", titleId, "PopupMenu.Window.NodeIds[0]", (nint)window);
        var count = window->UldManager.NodeListCount;
        if (count > MaxNativeCount)
        {
            state.Problem("The popup window node count is outside the supported bounds.");
            return;
        }
        for (var index = 0; index < Math.Min((int)count, 256); index++)
        {
            if (!TryElement((nint)window->UldManager.NodeList, index, sizeof(nint), out var slot))
            {
                state.Problem("The popup title node list is unreadable.");
                return;
            }
            var node = *(AtkResNode**)slot;
            if (Readable(node) && node->NodeId == titleId && node->Type == NodeType.Text)
            {
                var text = ReadTextNode(state, (AtkTextNode*)node, "PopupMenu.Window title", out _);
                if (!string.IsNullOrWhiteSpace(text))
                    state.Title = text;
                return;
            }
        }
        if (count > 256)
            state.Problem("The popup title search was limited to 256 nodes.");
    }

    private static void ReadYesno(CaptureState state, AddonSelectYesno* addon)
    {
        var prompt = ReadTextNode(state, addon->PromptText, "AddonSelectYesno.PromptText", out _);
        state.Field("Prompt", prompt, "AddonSelectYesno.PromptText", (nint)addon->PromptText);
        if (!string.IsNullOrWhiteSpace(prompt))
            state.Title = prompt;
        ReadButton(state, addon->YesButton, 0, "AddonSelectYesno.YesButton");
        ReadButton(state, addon->NoButton, 1, "AddonSelectYesno.NoButton");
        if (addon->AtkComponentButton238 != null)
            ReadButton(state, addon->AtkComponentButton238, 2, "AddonSelectYesno.AtkComponentButton238");
    }

    private static void ReadButton(CaptureState state, AtkComponentButton* button, int index, string source)
    {
        if (!Readable(button))
        {
            state.Problem($"{source} is unavailable or unreadable.");
            state.Entries.Add(new(index, "<unavailable>", source));
            return;
        }
        var label = ReadTextNode(state, button->ButtonTextNode, source + ".ButtonTextNode", out var nodeId);
        var owner = (AtkResNode*)button->OwnerNode;
        bool? enabled = Readable(owner) ? (owner->NodeFlags & NodeFlags.Enabled) != 0 : null;
        state.Entries.Add(new(index, label, source)
        {
            Enabled = enabled,
            NodeId = nodeId,
            Fields = [new("Component address", Pointer((nint)button), source), new("Flags", $"0x{button->Flags:X8}", source + ".Flags")],
        });
    }

    private static void ReadTalk(CaptureState state, AddonTalk* addon)
    {
        state.Title = "Dialogue";
        ReadTalkNode(state, addon->AtkTextNode220, "AddonTalk.AtkTextNode220");
        ReadTalkNode(state, addon->AtkTextNode228, "AddonTalk.AtkTextNode228");
        ReadTalkNode(state, addon->AtkTextNode238, "AddonTalk.AtkTextNode238");
        ReadTalkNode(state, addon->AtkTextNode240, "AddonTalk.AtkTextNode240");
        ReadTalkNode(state, addon->AtkTextNode248, "AddonTalk.AtkTextNode248");
        if (state.Entries.Count == 0)
            state.Problem("No readable dialogue text nodes are available.");
    }

    private static void ReadTalkNode(CaptureState state, AtkTextNode* node, string source)
    {
        if (node == null)
            return;
        var text = ReadTextNode(state, node, source, out var nodeId);
        var visible = Readable(node) && (node->NodeFlags & NodeFlags.Visible) != 0;
        state.Entries.Add(new(state.Entries.Count, text, source)
        {
            NodeId = nodeId,
            Fields = [new("Visible", visible.ToString(), source + ".NodeFlags"), new("Text node address", Pointer((nint)node), source)],
        });
    }

    private static string ReadTextNode(CaptureState state, AtkTextNode* node, string source, out uint? nodeId)
    {
        nodeId = null;
        if (!Readable(node) || node->Type != NodeType.Text)
        {
            state.Problem($"{source} is unavailable, unreadable, or not a text node.");
            return "<unavailable>";
        }
        nodeId = node->NodeId;
        return state.Text((nint)node->NodeText.StringPtr.Value, source + ".NodeText");
    }

    internal static string ReadText(nint address, out string issue)
        => ReadText(address, out issue, out _);

    internal static string ReadText(nint address, out string issue, out string rawHex, bool plainLabel = false)
    {
        issue = string.Empty;
        rawHex = string.Empty;
        if (address == 0)
        {
            issue = "The text pointer is null.";
            return "<unavailable>";
        }
        var bytes = new byte[MaxStringBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            if (!ClientStructRuntimeInspector.TryAddAddress(address, length, out var current))
                break;
            var remainingInPage = Environment.SystemPageSize - (int)((nuint)current % (nuint)Environment.SystemPageSize);
            var count = Math.Min(bytes.Length - length, remainingInPage);
            var chunk = bytes.AsSpan(length, count);
            if (!NativeSheetReader.CopyBytes(current, chunk))
                break;
            var terminator = chunk.IndexOf((byte)0);
            if (terminator >= 0)
            {
                var captured = bytes.AsSpan(0, length + terminator);
                rawHex = Convert.ToHexString(captured);
                return DecodeText(captured, out issue, plainLabel);
            }
            length += count;
        }
        rawHex = Convert.ToHexString(bytes.AsSpan(0, length));
        issue = length == MaxStringBytes ? $"Text exceeded {MaxStringBytes} bytes without a terminator." : "Text crossed unreadable memory.";
        return "<unavailable>";
    }

    internal static string DecodeText(ReadOnlySpan<byte> bytes, out string issue)
        => DecodeText(bytes, out issue, false);

    private static string DecodeText(ReadOnlySpan<byte> bytes, out string issue, bool plainLabel)
    {
        issue = string.Empty;
        var text = new StringBuilder();
        try
        {
            for (var index = 0; index < bytes.Length;)
            {
                if (bytes[index] != 0x02)
                {
                    var start = index;
                    while (index < bytes.Length && bytes[index] != 0x02)
                        index++;
                    text.Append(StrictUtf8.GetString(bytes[start..index]));
                    continue;
                }
                if (index + 2 >= bytes.Length)
                    throw new FormatException();
                var kind = bytes[index + 1];
                index += 2;
                if (!ReadPackedLength(bytes, ref index, out var count) || index >= bytes.Length
                    || count > (uint)(bytes.Length - index - 1) || bytes[index + (int)count] != 0x03)
                    throw new FormatException();
                if (kind == 0x10)
                    text.Append('\n');
                else if (kind == 0x1F)
                    text.Append('-');
                else if (plainLabel && kind is (0x12 or 0x13 or 0x14 or 0x15 or 0x16 or 0x1E))
                {
                    // Installed Lumina macro codes: icons, colors and optional soft hyphens have no item-name text.
                }
                else if (kind is not (0x1A or 0x27 or 0x48 or 0x49))
                {
                    if (plainLabel)
                    {
                        issue = $"The item label contains an unsupported SeString payload (0x{kind:X2}).";
                        return "<unsupported text>";
                    }
                    text.Append($"[payload 0x{kind:X2}]");
                    issue = "Some non-text SeString payloads are shown as markers.";
                }
                index += (int)count + 1;
            }
            return text.ToString();
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            issue = "The text contains malformed UTF-8 or SeString payload framing.";
            return "<invalid text>";
        }
    }

    private static bool ReadPackedLength(ReadOnlySpan<byte> bytes, ref int index, out uint value)
    {
        value = 0;
        if (index >= bytes.Length)
            return false;
        var marker = bytes[index++];
        if (marker is > 0 and < 0xD0)
        {
            value = (uint)marker - 1;
            return true;
        }
        if (marker < 0xF0)
            return false;
        var mask = (marker + 1) & 15;
        for (var shift = 3; shift >= 0; shift--)
        {
            if ((mask & (1 << shift)) == 0)
                continue;
            if (index >= bytes.Length)
                return false;
            value |= (uint)bytes[index++] << (shift * 8);
        }
        return true;
    }

    private static bool Readable<T>(T* pointer) where T : unmanaged
        => pointer != null && ClientStructRuntimeInspector.IsReadableRange((nint)pointer, (nuint)sizeof(T));

    private static bool TryElement(nint address, int index, int size, out nint element)
        => ClientStructRuntimeInspector.TryAddAddress(address, checked(index * size), out element)
            && ClientStructRuntimeInspector.IsReadableRange(element, (nuint)size);

    private static string Pointer(nint address) => $"0x{address:X}";

    private sealed class CaptureState
    {
        public string Title;
        public bool Visible;
        public bool Decoded = true;
        public readonly List<UiMenuEntry> Entries = new();
        private readonly string name;
        private readonly ulong address;
        private readonly List<UiMenuField> fields = new();
        private readonly List<string> issues = new();

        public CaptureState(string name, AtkUnitBase* addon)
        {
            this.name = name;
            address = (ulong)(nuint)addon;
            Title = name;
        }

        public void Problem(string issue)
        {
            if (issues.Count < 16 && !issues.Contains(issue))
                issues.Add(issue);
        }

        public void Field(string label, object value, string source, nint address)
            => fields.Add(new(label, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, $"{source} @ {Pointer(address)}"));

        public string Text(nint address, string source)
        {
            var text = ReadText(address, out var issue);
            if (issue.Length != 0)
                Problem($"{source}: {issue}");
            return text;
        }

        public UiMenuSnapshot Finish()
            => new(name, address, name == "Talk" ? "Dialogue" : "Menu", Title, Entries.ToArray())
            {
                CapturedAtUtc = DateTime.UtcNow,
                IsVisible = Visible,
                IsDecoded = Decoded,
                IsPartial = issues.Count != 0,
                Status = issues.Count == 0 ? "Decoded from guarded native menu fields." : string.Join(" ", issues),
                Fields = fields.ToArray(),
            };
    }
}
