using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Interface.Utility;
using XAHudNavigator.Services;

namespace XAHudNavigator.Windows;

internal sealed class ObjectExplorerView
{
    private readonly WorldObjectSnapshotService snapshots;
    private readonly Action<string, uint> openSheet;
    private string query = string.Empty;
    private string level = string.Empty;
    private ObjectKind? kind;
    private bool targetableOnly;
    private bool pin = true;
    private bool nativeDetail;
    private bool showRadar;
    private float radarRange = 60;
    private WorldObjectKey? selected;
    private WorldContext selectionContext;
    private string message = string.Empty;
    private string fieldFilter = string.Empty;
    private static readonly ObjectKind[] Kinds = Enum.GetValues<ObjectKind>();
    private static readonly string[] Columns = { "Name", "Kind", "NPC type", "Level", "Foray level", "Element", "Distance", "Delta Y", "Base ID", "Object ID" };

    public ObjectExplorerView(WorldObjectSnapshotService snapshots, Action<string, uint> openSheet)
    {
        this.snapshots = snapshots;
        this.openSheet = openSheet;
    }

    public void Draw()
    {
        var frame = snapshots.Current;
        if (frame.Context != selectionContext || !frame.Objects.Any(row => row.Key == selected))
            Select(null, frame.Context);
        ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##objectSearch", "Name, type, ID or address; | = OR", ref query, 256);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##objectKind", kind?.ToString() ?? "All kinds"))
        {
            if (ImGui.Selectable("All kinds", !kind.HasValue)) kind = null;
            foreach (var candidate in Kinds)
                if (ImGui.Selectable(candidate.ToString(), kind == candidate)) kind = candidate;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(125 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##objectLevel", "Level: >=90", ref level, 12);
        ImGui.SameLine(); ImGui.Checkbox("Targetable", ref targetableOnly);
        ImGui.BeginDisabled(snapshots.IsRunning);
        if (ImGui.Button("Start scanning")) { snapshots.Start(); snapshots.SelectNative(nativeDetail ? selected : null, frame.Context); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!snapshots.IsRunning);
        if (ImGui.Button("Stop scanning")) snapshots.Stop();
        ImGui.EndDisabled();
        frame = snapshots.Current;
        ImGui.SameLine(); ImGui.TextUnformatted(snapshots.IsRunning ? "RUNNING" : "STOPPED");
        ImGui.SameLine(); ImGui.Checkbox("Pin selection", ref pin);
        ImGui.SameLine(); ImGui.Checkbox("Radar", ref showRadar);
        ImGui.SameLine();
        ImGui.BeginDisabled(!snapshots.IsRunning);
        if (ImGui.Button("Refresh##objects")) snapshots.SelectNative(nativeDetail ? selected : null, frame.Context);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Select target")) Select(frame.Target, frame.Context);
        ImGui.SameLine();
        if (ImGui.Button("Focus")) Select(frame.FocusTarget, frame.Context);
        ImGui.SameLine();
        if (ImGui.Button("Mouseover")) Select(frame.MouseoverTarget, frame.Context);
        if (ImGui.Button("Copy all JSON")) Copy(WorldObjectQuery.Export(frame));
        ImGui.SameLine();
        if (ImGui.Button("Save all JSON")) Save(frame);
        ImGui.SameLine();
        ImGui.TextDisabled("Exports include every captured object, independent of filters.");
        var age = frame.CapturedAtUtc == DateTime.MinValue ? "not captured" : $"{Math.Max(0, (DateTime.UtcNow - frame.CapturedAtUtc).TotalSeconds):F1}s old";
        ImGui.TextWrapped($"{frame.Status} {age} | Territory {frame.Context.TerritoryId} / Map {frame.Context.MapId} | {frame.Objects.Count}/{frame.TableLength} loaded");
        if (!string.IsNullOrEmpty(message)) ImGui.TextWrapped(message);
        var rows = frame.Objects.Where(row => WorldObjectQuery.Matches(row, query, kind, targetableOnly, level)).ToList();
        ImGui.TextDisabled($"Showing {rows.Count}. Right-click table headers to choose columns. NPC roles require sheet evidence; current map is capture context.");
        if (showRadar) DrawRadar(frame, rows);
        DrawTable(frame, rows);
        var selectedRow = frame.Objects.FirstOrDefault(row => row.Key == selected);
        if (selectedRow != null) DrawDetails(frame, selectedRow);
    }

    private void Select(WorldObjectKey? key, WorldContext context)
    {
        if (selected == key && selectionContext == context) return;
        selected = key;
        selectionContext = context;
        if (snapshots.IsRunning) snapshots.SelectNative(nativeDetail ? selected : null, context);
    }

    private unsafe void DrawTable(WorldObjectFrame frame, List<WorldObjectRow> rows)
    {
        var height = Math.Max(110 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y * (selected.HasValue ? .48f : .9f));
        if (!ImGui.BeginTable("WorldObjects", Columns.Length,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit,
                new Vector2(0, height))) return;
        for (var i = 0; i < Columns.Length; i++)
            ImGui.TableSetupColumn(Columns[i], i == 0 ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.None, i == 0 ? 200 * ImGuiHelpers.GlobalScale : 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableHeadersRow();
        var specs = ImGui.TableGetSortSpecs();
        var criteria = new List<(int Column, bool Descending)>();
        for (var i = 0; i < specs.SpecsCount; i++) criteria.Add((specs.Specs[i].ColumnIndex, specs.Specs[i].SortDirection == ImGuiSortDirection.Descending));
        rows.Sort((left, right) =>
        {
            if (pin && (left.Key == selected) != (right.Key == selected)) return left.Key == selected ? -1 : 1;
            foreach (var criterion in criteria)
            {
                var result = WorldObjectQuery.Compare(left, right, criterion.Column, criterion.Descending);
                if (result != 0) return result;
            }
            var distance = WorldObjectQuery.Compare(left, right, 6, false);
            return distance != 0 ? distance : left.Key.Index.CompareTo(right.Key.Index);
        });
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count);
        while (clipper.Step())
        {
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var row = rows[index];
                ImGui.PushID(row.Key.Index);
                ImGui.TableNextRow();
                if (ImGui.TableSetColumnIndex(0))
                {
                    var label = string.IsNullOrWhiteSpace(row.Name) ? "(unnamed)" : row.Name;
                    if (row.IsLocalPlayer) label += " [you]";
                    if (frame.Target == row.Key) label += " [target]";
                    if (ImGui.Selectable(label + "##object", selected == row.Key)) Select(row.Key, frame.Context);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{row.Kind} / subkind {row.SubKind} / {row.NpcType}\n{row.ObjectIdHex} / {row.AddressHex}\nXYZ {row.Position.X:F2}, {row.Position.Y:F2}, {row.Position.Z:F2}");
                }
                Cell(1, row.Kind); Cell(2, row.NpcType); Cell(3, row.Character?.Level.ToString() ?? "—");
                Cell(4, row.Character?.ForayLevel?.ToString() ?? "—"); Cell(5, WorldObjectQuery.ElementName(row.Character?.ForayElement));
                Cell(6, row.Distance?.ToString("F2", CultureInfo.InvariantCulture) ?? "—");
                Cell(7, row.HeightDelta?.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) ?? "—");
                Cell(8, row.Key.BaseId.ToString(CultureInfo.InvariantCulture)); Cell(9, row.ObjectIdHex);
                ImGui.PopID();
            }
        }
        clipper.End();
        clipper.Destroy();
        ImGui.EndTable();
    }

    private static void Cell(int column, string text)
    {
        if (ImGui.TableSetColumnIndex(column)) ImGui.TextUnformatted(text);
    }

    private void DrawDetails(WorldObjectFrame frame, WorldObjectRow row)
    {
        if (!ImGui.BeginChild("ObjectDetails", new Vector2(0, 0), true)) { ImGui.EndChild(); return; }
        ImGui.TextUnformatted($"{(string.IsNullOrEmpty(row.Name) ? "(unnamed)" : row.Name)} — {row.Kind} / {row.NpcType}");
        if (ImGui.SmallButton("Copy selected JSON")) Copy(WorldObjectQuery.Export(frame with { Objects = new[] { row } }));
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy XYZ")) Copy(FormattableString.Invariant($"{row.Position.X:F3}, {row.Position.Y:F3}, {row.Position.Z:F3}"));
        ImGui.SameLine();
        if (ImGui.Checkbox("Native inline fields", ref nativeDetail) && snapshots.IsRunning) snapshots.SelectNative(nativeDetail ? selected : null, frame.Context);
        ImGui.TextWrapped($"Object ID {row.Key.GameObjectId} ({row.ObjectIdHex}) | Entity {row.EntityId} | Index {row.Key.Index} | Address {row.AddressHex}");
        ImGui.TextWrapped($"Kind {row.Kind} ({(byte)row.Key.Kind}) | Subkind {row.SubKind} | Base / Data ID {row.Key.BaseId} | Name ID {row.Character?.NameId.ToString() ?? "unavailable"} | Owner {row.OwnerId}");
        ImGui.TextWrapped($"Targetable {row.IsTargetable} | Dead {row.IsDead} | Target 0x{row.TargetId:X} | Hitbox radius {row.HitboxRadius:F2} | Rotation {row.Rotation:F3}");
        ImGui.TextWrapped(FormattableString.Invariant($"XYZ {row.Position.X:F3}, {row.Position.Y:F3}, {row.Position.Z:F3} | 3D {row.Distance:F2} | Horizontal {row.HorizontalDistance:F2} | Signed delta Y {row.HeightDelta:F2}"));
        if (row.Character is { } c)
            ImGui.TextWrapped($"Level {c.Level} | Job {c.ClassJob} ({c.ClassJobId}) | HP {c.Hp}/{c.MaxHp} | MP {c.Mp}/{c.MaxMp} | Shield {c.Shield}% | {c.Flags} | Foray {c.ForayLevel?.ToString() ?? "unavailable"} / {WorldObjectQuery.ElementName(c.ForayElement)}");
        foreach (var link in row.Sheets)
        {
            if (ImGui.SmallButton($"{link.Sheet} #{link.RowId}")) openSheet(link.Sheet, link.RowId);
            ImGui.SameLine();
        }
        ImGui.NewLine();
        if (row.Cast is { } cast)
        {
            ImGui.TextWrapped($"Casting type {cast.ActionType}, action {cast.ActionId}, {cast.Elapsed:F2}/{cast.Total:F2}s, interruptible {cast.Interruptible}, target 0x{cast.TargetId:X}");
            if (cast.ActionType == 1 && ImGui.SmallButton($"Action #{cast.ActionId}")) openSheet("Action", cast.ActionId);
        }
        if (row.Statuses.Count > 0 && ImGui.TreeNode($"Statuses ({row.Statuses.Count})"))
        {
            foreach (var status in row.Statuses)
            {
                ImGui.PushID((int)status.Id); ImGui.PushID((int)status.SourceId);
                if (ImGui.Selectable($"{status.Id} {status.Name} | param/stacks {status.Stacks} | {status.RemainingSeconds:F1}s | source {status.SourceId}")) openSheet("Status", status.Id);
                ImGui.PopID(); ImGui.PopID();
            }
            ImGui.TreePop();
        }
        if (!string.IsNullOrEmpty(row.ReadWarning)) ImGui.TextWrapped(row.ReadWarning);
        if (nativeDetail)
        {
            ImGui.Separator();
            if (frame.NativeDetail is { } detail && (ulong)detail.Address == row.Key.Address)
            {
                ImGui.TextWrapped(detail.ResolutionInfo);
                if (ImGui.SmallButton("Copy native report")) Copy(ClientStructRuntimeInspector.BuildStructReport(detail));
                ImGui.SameLine(); ImGui.SetNextItemWidth(240 * ImGuiHelpers.GlobalScale);
                ImGui.InputTextWithHint("##nativeObjectFilter", "Filter native fields", ref fieldFilter, 128);
                foreach (var field in detail.Fields)
                {
                    var line = ClientStructRuntimeInspector.FormatFieldEntry(field);
                    if (line.Contains(fieldFilter, StringComparison.OrdinalIgnoreCase)) ImGui.TextUnformatted(line);
                }
            }
            else ImGui.TextDisabled("Native detail requires a running capture. Use Start scanning, then select an object.");
        }
        ImGui.EndChild();
    }

    private void DrawRadar(WorldObjectFrame frame, IReadOnlyList<WorldObjectRow> rows)
    {
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        ImGui.SliderFloat("Radar radius (yalms)", ref radarRange, 10, 300, "%.0f");
        var local = frame.Objects.FirstOrDefault(row => row.IsLocalPlayer);
        if (local == null) { ImGui.TextDisabled("Radar unavailable without local-player position."); return; }
        var size = new Vector2(250, 180) * ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("ObjectRadar", size);
        var center = start + size / 2;
        var radius = Math.Min(size.X, size.Y) * .46f;
        var scale = radius / radarRange;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + size, 0xFF202020);
        draw.AddCircle(center, radius, 0xFF777777);
        draw.AddCircle(center, radius / 2, 0xFF444444);
        draw.AddCircleFilled(center, 4, 0xFF00FF00);
        var mouse = ImGui.GetMousePos();
        WorldObjectRow? hovered = null;
        var closest = 9f * ImGuiHelpers.GlobalScale;
        foreach (var row in rows)
        {
            if (row.IsLocalPlayer || row.HorizontalDistance is not { } distance || distance > radarRange) continue;
            var point = center + new Vector2(row.Position.X - local.Position.X, row.Position.Z - local.Position.Z) * scale;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) continue;
            draw.AddCircleFilled(point, row.Key == selected ? 5 : 3, row.Key == selected ? 0xFF00FFFF : row.Key.Kind == ObjectKind.BattleNpc ? 0xFF7777FF : 0xFFFFBB77);
            var mouseDistance = Vector2.Distance(mouse, point);
            if (mouseDistance < closest) { closest = mouseDistance; hovered = row; }
        }
        if (ImGui.IsItemHovered() && hovered != null)
        {
            ImGui.SetTooltip($"{hovered.Name} / {hovered.Kind} / {hovered.Distance:F1}y\nClick to inspect");
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) Select(hovered.Key, frame.Context);
        }
        ImGui.TextDisabled("Local X/Z projection; north up. Dots use the current filters. Height and map membership are separate.");
    }

    private void Copy(string text) { ImGui.SetClipboardText(text); message = "Copied snapshot values."; }
    private void Save(WorldObjectFrame frame)
    {
        try
        {
            var directory = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "object-snapshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"objects-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.json");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream)) writer.Write(WorldObjectQuery.Export(frame));
            message = $"Saved {path}";
        }
        catch (Exception ex) { message = $"Export failed: {ex.Message}"; }
    }
}
