using System;
using System.Collections.Generic;

namespace XAHudNavigator.Services;

internal sealed record UiMenuField(string Name, string Value, string Source);

internal sealed record UiInventorySlot(string Container, int Slot, bool IsEmpty, string Flags,
    uint? Condition, ushort? SpiritbondOrCollectability)
{
    public int? DisplaySlot { get; init; }
}

internal readonly record struct UiMenuKey(string Name, ulong Address, ushort Id);
internal sealed record UiMenuSummary(UiMenuKey Key, string Category, int NodeCount);
internal sealed record UiMenuFrame(long Sequence, DateTime CapturedAtUtc, WorldContext Context,
    IReadOnlyList<UiMenuSummary> Menus, UiMenuKey? Selection, UiMenuSnapshot? Detail, string Status, bool IsCurrent)
{
    public static readonly UiMenuFrame Empty = new(0, DateTime.MinValue, default, Array.Empty<UiMenuSummary>(),
        null, null, "Capture stopped. Press Start capture to inspect open game menus.", false);
}

internal sealed record UiMenuEntry(int Index, string Label, string Detail)
{
    public uint? ItemId { get; init; }
    public uint? IconId { get; init; }
    public uint? Quantity { get; init; }
    public UiInventorySlot? Inventory { get; init; }
    public string Cost { get; init; } = string.Empty;
    public bool? Enabled { get; init; }
    public bool? Selected { get; init; }
    public uint? NodeId { get; init; }
    public int? NodeIndex { get; init; }
    public IReadOnlyList<UiMenuField> Fields { get; init; } = Array.Empty<UiMenuField>();
}

internal sealed record UiMenuSnapshot(string AddonName, ulong Address, string Category, string Title,
    IReadOnlyList<UiMenuEntry> Entries)
{
    public DateTime CapturedAtUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public bool IsDecoded { get; init; } = true;
    public bool IsPartial { get; init; }
    public bool IsVisible { get; init; } = true;
    public IReadOnlyList<UiMenuField> Fields { get; init; } = Array.Empty<UiMenuField>();
    public IReadOnlyList<UiMenuField> RawValues { get; init; } = Array.Empty<UiMenuField>();
    public string NativeReport { get; init; } = string.Empty;
}
