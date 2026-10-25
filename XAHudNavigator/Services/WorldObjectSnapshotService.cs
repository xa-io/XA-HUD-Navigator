using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XAHudNavigator.Services;

internal readonly record struct WorldObjectKey(ulong GameObjectId, ulong Address, uint BaseId, ObjectKind Kind, ushort Index);
internal readonly record struct WorldContext(uint TerritoryId, uint MapId, ulong LocalPlayerId);
internal sealed record ObjectSheetLink(string Sheet, uint RowId);
internal sealed record ObjectStatus(uint Id, string Name, ushort Stacks, float RemainingSeconds, uint SourceId);
internal sealed record ObjectCast(uint ActionId, byte ActionType, ulong TargetId, float Elapsed, float Total, bool Interruptible);
internal sealed record ObjectCharacter(byte Level, uint NameId, uint ClassJobId, string ClassJob, uint Hp, uint MaxHp,
    uint Mp, uint MaxMp, byte Shield, string Flags, byte? ForayLevel, byte? ForayElement);
internal sealed record WorldObjectRow(WorldObjectKey Key, string Name, uint EntityId, uint DataId, uint OwnerId,
    byte SubKind, string NpcType, bool IsTargetable, bool IsDead, bool IsLocalPlayer, Vector3 Position,
    float Rotation, float HitboxRadius, ulong TargetId, float? Distance, float? HorizontalDistance, float? HeightDelta,
    ObjectCharacter? Character, ObjectCast? Cast, IReadOnlyList<ObjectStatus> Statuses, IReadOnlyList<ObjectSheetLink> Sheets,
    string ReadWarning)
{
    public string Kind => Key.Kind.ToString();
    public string AddressHex => $"0x{Key.Address:X}";
    public string ObjectIdHex => $"0x{Key.GameObjectId:X}";
}

internal sealed record WorldObjectFrame(long Sequence, DateTime CapturedAtUtc, WorldContext Context,
    IReadOnlyList<WorldObjectRow> Objects, WorldObjectKey? Target, WorldObjectKey? FocusTarget, WorldObjectKey? MouseoverTarget,
    int TableLength, int FailedEntries, string Status, bool IsCurrent, ResolvedNativeStructSnapshot? NativeDetail)
{
    public static readonly WorldObjectFrame Empty = new(0, DateTime.MinValue, default, Array.Empty<WorldObjectRow>(),
        null, null, null, 0, 0, "Scanning stopped. Press Start scanning to capture loaded objects.", false, null);
}

/// <summary>Pure query/export policy shared by the UI and regression tests.</summary>
internal static class WorldObjectQuery
{
    public static bool Matches(WorldObjectRow row, string query, ObjectKind? kind, bool targetableOnly, string levelFilter)
    {
        if (kind.HasValue && row.Key.Kind != kind.Value || targetableOnly && !row.IsTargetable)
            return false;
        if (!MatchesLevel(row.Character?.Level, levelFilter))
            return false;
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var text = string.Join(" ", row.Name, row.Kind, row.NpcType, row.Key.BaseId.ToString(CultureInfo.InvariantCulture),
            row.DataId.ToString(CultureInfo.InvariantCulture), row.Character?.NameId.ToString(CultureInfo.InvariantCulture),
            row.Key.GameObjectId.ToString(CultureInfo.InvariantCulture), row.ObjectIdHex, row.AddressHex,
            row.EntityId.ToString(CultureInfo.InvariantCulture));
        return query.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesLevel(byte? level, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return true;
        var op = filter.StartsWith(">=", StringComparison.Ordinal) || filter.StartsWith("<=", StringComparison.Ordinal) ? filter[..2]
            : filter.StartsWith('=') ? "=" : string.Empty;
        if (!byte.TryParse(filter[op.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !level.HasValue)
            return false;
        return op switch { ">=" => level >= value, "<=" => level <= value, _ => level == value };
    }

    public static int Compare(WorldObjectRow left, WorldObjectRow right, int column, bool descending)
    {
        object? l = SortValue(left, column);
        object? r = SortValue(right, column);
        if (l == null != (r == null)) return l == null ? 1 : -1; // Unavailable stays last in either direction.
        var result = l is string ls && r is string rs ? StringComparer.OrdinalIgnoreCase.Compare(ls, rs)
            : l is IComparable comparable ? comparable.CompareTo(r) : 0;
        if (result != 0) return descending ? -result : result;
        return 0;
    }

    private static object? SortValue(WorldObjectRow row, int column) => column switch
    {
        0 => row.Name, 1 => row.Kind, 2 => row.NpcType, 3 => row.Character?.Level,
        4 => row.Character?.ForayLevel, 5 => row.Character?.ForayElement, 6 => row.Distance,
        7 => row.HeightDelta, 8 => row.Key.BaseId, 9 => row.Key.GameObjectId, _ => row.Key.Index,
    };

    public static WorldObjectRow? ResolveSelection(WorldObjectFrame frame, WorldContext context, WorldObjectKey? key)
        => frame.IsCurrent && frame.Context == context && key.HasValue ? frame.Objects.FirstOrDefault(row => row.Key == key.Value) : null;

    public static string ElementName(byte? value) => value switch
    {
        null => "Unavailable", 1 => "Fire", 2 => "Ice", 3 => "Wind", 4 => "Earth", 5 => "Lightning", 6 => "Water", _ => $"Unknown ({value})",
    };

    public static IReadOnlyList<ObjectSheetLink> SheetLinks(ObjectKind kind, uint baseId, uint nameId, uint classJobId)
    {
        var links = new List<ObjectSheetLink>();
        if (baseId != 0)
        {
            if (kind == ObjectKind.BattleNpc) links.Add(new("BNpcBase", baseId));
            if (kind == ObjectKind.EventNpc) { links.Add(new("ENpcBase", baseId)); links.Add(new("ENpcResident", baseId)); }
            if (kind == ObjectKind.EventObj) links.Add(new("EObj", baseId));
            if (kind == ObjectKind.Companion) links.Add(new("Companion", baseId));
        }
        if (nameId != 0 && kind == ObjectKind.BattleNpc) links.Add(new("BNpcName", nameId));
        if (classJobId != 0) links.Add(new("ClassJob", classJobId));
        return links.AsReadOnly();
    }

    public static string Export(WorldObjectFrame frame) => JsonSerializer.Serialize(new
    {
        FormatVersion = 1, PluginVersion = BuildInfo.Version, frame.Sequence, frame.CapturedAtUtc, frame.Context,
        frame.IsCurrent, frame.Status, frame.TableLength, frame.FailedEntries, LoadedObjects = frame.Objects.Count,
        frame.Target, frame.FocusTarget, frame.MouseoverTarget, frame.Objects,
    }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
}

/// <summary>All native access stays inside Framework.Update; consumers receive value-only snapshots.</summary>
internal sealed class WorldObjectSnapshotService : IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<DateTime> utcNow;
    private readonly Func<WorldContext?> readContext;
    private readonly Func<DateTime, WorldContext, WorldObjectFrame> capture;
    private readonly Action unsubscribe;
    private WorldObjectFrame current = WorldObjectFrame.Empty;
    private int running;
    private long runGeneration;
    private int refreshRequested;
    private DateTime lastCapture;
    private bool disposed;
    private long sequence;
    private readonly object selectionLock = new();
    private WorldObjectKey? nativeSelection;
    private WorldContext nativeContext;
    public WorldObjectFrame Current => Volatile.Read(ref current);
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public WorldObjectSnapshotService(Configuration configuration)
        : this(configuration, () => DateTime.UtcNow, ReadContext, null, Subscribe)
    {
    }

    internal WorldObjectSnapshotService(Configuration configuration, Func<DateTime> utcNow,
        Func<WorldContext?> readContext, Func<DateTime, WorldContext, WorldObjectFrame>? capture, Func<Action, Action> subscribe)
    {
        this.configuration = configuration;
        this.utcNow = utcNow;
        this.readContext = readContext;
        this.capture = capture ?? Capture;
        unsubscribe = subscribe(OnUpdate);
    }

    private static Action Subscribe(Action update)
    {
        void Tick(IFramework framework) => update();
        Plugin.Framework.Update += Tick;
        return () => Plugin.Framework.Update -= Tick;
    }

    private static WorldContext? ReadContext()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        return !Plugin.ClientState.IsLoggedIn || local == null ? null
            : new(Plugin.ClientState.TerritoryType, Plugin.ClientState.MapId, local.GameObjectId);
    }

    public void Start()
    {
        lock (selectionLock)
        {
            if (disposed || Interlocked.Exchange(ref running, 1) != 0) return;
            Interlocked.Increment(ref runGeneration);
            RequestRefresh();
        }
    }

    public void Stop()
    {
        Interlocked.Exchange(ref running, 0);
        Interlocked.Increment(ref runGeneration);
        Interlocked.Exchange(ref refreshRequested, 0);
        lock (selectionLock)
        {
            Interlocked.Exchange(ref running, 0);
            nativeSelection = null;
            Volatile.Write(ref current, Current with { IsCurrent = false, Status = "Scanning stopped. Last capture is historical.", NativeDetail = null });
        }
    }
    public void RequestRefresh() { if (IsRunning) Interlocked.Exchange(ref refreshRequested, 1); }
    public void SelectNative(WorldObjectKey? key, WorldContext context)
    {
        lock (selectionLock) { nativeSelection = key; nativeContext = context; }
        RequestRefresh();
    }

    private void OnUpdate()
    {
        // Stop signals immediately, then waits for an in-flight native capture before publishing the historical frame.
        // Sharing this gate prevents a late current-frame publication after Stop has returned.
        lock (selectionLock) OnUpdateCore();
    }

    private void OnUpdateCore()
    {
        if (disposed || !IsRunning) return;
        var generation = Interlocked.Read(ref runGeneration);
        var now = utcNow();
        try
        {
            var context = readContext();
            if (!IsRunning || generation != Interlocked.Read(ref runGeneration)) return;
            if (!context.HasValue || context.Value != Current.Context)
            {
                if (Current.Objects.Count > 0 || Current.Context != context.GetValueOrDefault()
                    || !context.HasValue && Current.Status != "No local player; waiting for a logged-in world.")
                    Volatile.Write(ref current, WorldObjectFrame.Empty with { Sequence = ++sequence, Context = context.GetValueOrDefault(),
                        Status = !context.HasValue ? "No local player; waiting for a logged-in world." : "World context changed; waiting for capture." });
                lock (selectionLock) nativeSelection = null;
                lastCapture = DateTime.MinValue;
                if (!context.HasValue) return;
            }
            var forced = Interlocked.Exchange(ref refreshRequested, 0) != 0;
            var elapsed = (now - lastCapture).TotalSeconds;
            if (!forced && elapsed >= 0 && elapsed < configuration.RefreshInterval) return;
            lastCapture = now;
            var captured = capture(now, context.Value) with { Sequence = ++sequence };
            if (IsRunning && generation == Interlocked.Read(ref runGeneration)) Volatile.Write(ref current, captured);
        }
        catch (Exception ex)
        {
            if (IsRunning && generation == Interlocked.Read(ref runGeneration))
                Volatile.Write(ref current, Current with { IsCurrent = false, Status = $"Capture failed; retained frame is historical: {ex.GetType().Name}", NativeDetail = null });
            DiagnosticLog.VerboseThrottled("WorldObjectSnapshotService.Capture", ex);
        }
    }

    private WorldObjectFrame Capture(DateTime now, WorldContext context)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.GameObjectId != context.LocalPlayerId) throw new InvalidOperationException("Local player changed during capture.");
        var origin = local.Position;
        var rows = new List<WorldObjectRow>();
        var failures = 0;
        var length = Plugin.ObjectTable.Length;
        var count = Math.Min(length, 4096);
        for (var index = 0; index < count && IsRunning; index++)
        {
            try
            {
                var obj = Plugin.ObjectTable[index];
                if (obj != null) rows.Add(CaptureObject(obj, context, origin));
            }
            catch (Exception ex) { failures++; DiagnosticLog.VerboseThrottled("WorldObjectSnapshotService.Object", ex); }
        }
        WorldObjectKey? Find(IGameObject? obj) => obj == null ? null : rows.FirstOrDefault(row => row.Key.Address == (ulong)obj.Address && row.Key.GameObjectId == obj.GameObjectId)?.Key;
        ResolvedNativeStructSnapshot? detail = null;
        var detailError = string.Empty;
        WorldObjectKey? selected;
        WorldContext selectedContext;
        lock (selectionLock) { selected = nativeSelection; selectedContext = nativeContext; }
        var selectedRow = context == selectedContext ? rows.FirstOrDefault(row => row.Key == selected) : null;
        if (selectedRow != null && IsRunning)
        {
            try
            {
                var type = selectedRow.Character == null ? typeof(NativeGameObject) : typeof(NativeCharacter);
                detail = ClientStructRuntimeInspector.SnapshotStruct("World object", selectedRow.Name, (nint)selectedRow.Key.Address,
                    type, "Known base layout selected from the current object kind; inline data only, no arbitrary pointer traversal.");
            }
            catch (Exception ex) { detailError = $" Native detail unavailable: {ex.GetType().Name}."; }
        }
        if (!IsRunning) return WorldObjectFrame.Empty;
        return new(0, now, context, rows.AsReadOnly(), Find(Plugin.TargetManager.Target), Find(Plugin.TargetManager.FocusTarget),
            Find(Plugin.TargetManager.MouseOverTarget), length, failures,
            (failures > 0 || count < length ? $"Partial capture: {failures} unreadable entries; {Math.Max(0, length - count)} beyond cap." : "Captured loaded object table.") + detailError, true, detail);
    }

    private static WorldObjectRow CaptureObject(IGameObject obj, WorldContext context, Vector3 origin)
    {
        var key = new WorldObjectKey(obj.GameObjectId, (ulong)obj.Address, obj.BaseId, obj.ObjectKind, obj.ObjectIndex);
        ObjectCharacter? character = null;
        ObjectCast? cast = null;
        var statuses = new List<ObjectStatus>();
        var warning = string.Empty;
        if (obj is ICharacter c)
        {
            var foray = ReadForay(obj);
            character = new(c.Level, c.NameId, c.ClassJob.RowId, c.ClassJob.ValueNullable?.Abbreviation.ToString() ?? string.Empty,
                c.CurrentHp, c.MaxHp, c.CurrentMp, c.MaxMp, c.ShieldPercentage, c.StatusFlags.ToString(), foray.Level, foray.Element);
        }
        if (obj is IBattleChara battle)
        {
            if (battle.IsCasting) cast = new(battle.CastActionId, battle.CastActionType, battle.CastTargetObjectId,
                battle.CurrentCastTime, battle.TotalCastTime, battle.IsCastInterruptible);
            foreach (var status in battle.StatusList.Take(60))
            {
                if (status.StatusId != 0) statuses.Add(new(status.StatusId, status.GameData.ValueNullable?.Name.ToString() ?? string.Empty,
                    status.Param, status.RemainingTime, status.SourceId));
            }
            if (battle.StatusList.Length > 60) warning = "Status list capped at 60 entries.";
        }
        var position = obj.Position;
        var delta = position - origin;
        var finite = float.IsFinite(delta.X) && float.IsFinite(delta.Y) && float.IsFinite(delta.Z);
        var npcType = obj is IBattleNpc npc ? npc.BattleNpcKind.ToString()
            : obj.ObjectKind == ObjectKind.EventNpc ? "Event NPC (role in ENpcBase)" : string.Empty;
        // DataId is the former name of BaseId in API 15; retain the export alias without calling an obsolete getter.
        return new(key, obj.Name.TextValue, obj.EntityId, key.BaseId, obj.OwnerId, obj.SubKind, npcType,
            obj.IsTargetable, obj.IsDead, obj.GameObjectId == context.LocalPlayerId, position, obj.Rotation, obj.HitboxRadius,
            obj.TargetObjectId, finite ? delta.Length() : null, finite ? new Vector2(delta.X, delta.Z).Length() : null,
            finite ? delta.Y : null, character, cast, statuses.AsReadOnly(),
            WorldObjectQuery.SheetLinks(key.Kind, key.BaseId, character?.NameId ?? 0, character?.ClassJobId ?? 0), warning);
    }

    private static unsafe (byte? Level, byte? Element) ReadForay(IGameObject obj)
    {
        if (obj is not IBattleChara || !ClientStructRuntimeInspector.IsReadableRange(obj.Address, (nuint)sizeof(NativeCharacter))) return (null, null);
        var character = (NativeCharacter*)obj.Address;
        if (character->VirtualTable == null || !ClientStructRuntimeInspector.IsReadableRange((nint)character->VirtualTable, (nuint)sizeof(NativeCharacter.CharacterVirtualTable))
            || character->VirtualTable->GetForayInfo == null) return (null, null);
        var info = character->GetForayInfo();
        return info == null || !ClientStructRuntimeInspector.IsReadableRange((nint)info, (nuint)sizeof(FFXIVClientStructs.FFXIV.Client.Game.Character.ForayInfo)) || info->Level == 0
            ? (null, null) : (info->Level, info->Element);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        unsubscribe();
        Volatile.Write(ref current, WorldObjectFrame.Empty);
    }
}
