using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using FFXIVClientStructs.Attributes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XAHudNavigator.Services;

public sealed class ResolvedNativeStructSnapshot
{
    public string Kind { get; set; } = "";
    public string LookupKey { get; set; } = "";
    public string TypeName { get; set; } = "";
    public string FullTypeName { get; set; } = "";
    public nint Address { get; set; }
    public int Size { get; set; }
    public bool IsResolved { get; set; }
    public string ResolutionInfo { get; set; } = "";
    public bool IsTruncated { get; set; }
    public List<ResolvedNativeFieldInfo> Fields { get; set; } = new();
}

public sealed class ResolvedNativeFieldInfo
{
    public int Offset { get; set; }
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public nint Address { get; set; }
    public string Value { get; set; } = "";
    public bool IsReadable { get; set; }
    public string Notes { get; set; } = "";
}

public static partial class ClientStructRuntimeInspector
{
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    internal const long MaxVectorElements = 1_000_000;
    internal const int MaxInspectionFields = 512;
    internal const int MaxInlineDepth = 4;
    internal const int MaxInlineArrayElements = 32;

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly ConcurrentDictionary<Type, NativeTypeLayout> FieldLayouts = new();
    private static readonly ConcurrentDictionary<Type, int> StructSizes = new();
    private static readonly MethodInfo ReferenceFreeSizeMethod = typeof(ClientStructRuntimeInspector)
        .GetMethod(nameof(GetReferenceFreeSize), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly Lazy<ClientStructTypeMaps> StructTypeMaps = new(BuildStructTypeMaps);
    private static readonly AgentId[] AgentIds = Enum.GetValues<AgentId>().Distinct().ToArray();

    public static void Warmup()
        => _ = StructTypeMaps.Value;

    public static ResolvedNativeStructSnapshot ResolveAddonStruct(string addonName, nint addonAddress)
    {
        var snapshot = CreateSnapshot("Addon Struct", addonName, addonAddress);
        if (addonAddress == 0)
        {
            snapshot.ResolutionInfo = "Addon pointer is 0.";
            return snapshot;
        }

        if (!StructTypeMaps.Value.Addons.TryGetValue(addonName, out var structType))
        {
            snapshot.ResolutionInfo = $"No generated AddonAttribute match for '{addonName}'.";
            DiagnosticLog.WarningOnce(
                "ClientStructRuntimeInspector.AddonAttributeMiss",
                $"FFXIVClientStructs addon recognition degraded; no generated AddonAttribute matched '{addonName}'. Patch-day review may be required.");
            return snapshot;
        }

        PopulateSnapshot(snapshot, structType, $"Matched AddonAttribute identifier '{addonName}'.");
        return snapshot;
    }

    public static ResolvedNativeStructSnapshot ResolveBaseAddonStruct(nint addonAddress)
        => SnapshotStruct(
            "Addon Base Struct",
            "AtkUnitBase",
            addonAddress,
            typeof(AtkUnitBase),
            "Generic snapshot of the selected addon's base AtkUnitBase layout.");

    public static unsafe ResolvedNativeStructSnapshot ResolveAgentStruct(nint agentAddress)
    {
        var snapshot = CreateSnapshot("Agent Struct", "(agent)", agentAddress);
        if (agentAddress == 0)
        {
            snapshot.ResolutionInfo = "No live agent pointer is available for this addon.";
            return snapshot;
        }

        var agentId = TryResolveAgentId(agentAddress);
        if (agentId == null)
        {
            snapshot.LookupKey = $"0x{agentAddress:X}";
            snapshot.ResolutionInfo = "Could not map the agent pointer back to AgentModule internal ids.";
            return snapshot;
        }

        snapshot.LookupKey = agentId.Value.ToString();
        if (!StructTypeMaps.Value.Agents.TryGetValue(agentId.Value, out var structType))
        {
            snapshot.ResolutionInfo = $"Resolved AgentId.{agentId.Value}, but FFXIVClientStructs has no generated AgentAttribute struct for it.";
            DiagnosticLog.WarningOnce(
                "ClientStructRuntimeInspector.AgentAttributeMiss",
                $"FFXIVClientStructs agent recognition degraded; AgentId.{agentId.Value} has no generated AgentAttribute struct. Patch-day review may be required.");
            return snapshot;
        }

        PopulateSnapshot(snapshot, structType, $"Matched AgentId.{agentId.Value} through AgentModule->GetAgentByInternalId.");
        return snapshot;
    }

    public static ResolvedNativeStructSnapshot SnapshotStruct(string kind, string lookupKey, nint address, Type structType, string resolutionInfo)
    {
        var snapshot = CreateSnapshot(kind, lookupKey, address);
        if (address == 0)
        {
            snapshot.ResolutionInfo = $"{kind} pointer is 0.";
            return snapshot;
        }

        PopulateSnapshot(snapshot, structType, resolutionInfo);
        return snapshot;
    }

    public static string FormatFieldEntry(ResolvedNativeFieldInfo field)
    {
        var parts = new List<string>
        {
            $"+0x{field.Offset:X3}",
            $"{field.TypeName} {field.Name}",
            $"= {field.Value}",
            $"Addr=0x{field.Address:X}"
        };

        if (!string.IsNullOrEmpty(field.Notes))
            parts.Add(field.Notes);

        return string.Join(" | ", parts);
    }

    public static string BuildStructReport(ResolvedNativeStructSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"{snapshot.Kind}: {snapshot.LookupKey}",
            $"Resolved: {snapshot.IsResolved}",
            $"Type: {snapshot.TypeName}",
            $"Full Type: {snapshot.FullTypeName}",
            $"Address: 0x{snapshot.Address:X}",
            $"Size: 0x{snapshot.Size:X}",
            $"Resolution: {snapshot.ResolutionInfo}",
            $"Fields [{snapshot.Fields.Count}]"
        };

        foreach (var field in snapshot.Fields)
            lines.Add(FormatFieldEntry(field));

        return string.Join("\n", lines);
    }

    private static ResolvedNativeStructSnapshot CreateSnapshot(string kind, string lookupKey, nint address)
        => new()
        {
            Kind = kind,
            LookupKey = lookupKey,
            Address = address,
            TypeName = "(unresolved)",
            FullTypeName = "(unresolved)",
        };

    private static void PopulateSnapshot(ResolvedNativeStructSnapshot snapshot, Type structType, string resolutionInfo)
    {
        snapshot.IsResolved = true;
        snapshot.TypeName = FormatTypeName(structType);
        snapshot.FullTypeName = structType.FullName ?? structType.Name;
        snapshot.Size = TryGetStructSize(structType);
        snapshot.ResolutionInfo = resolutionInfo;
        if (snapshot.Size <= 0)
        {
            snapshot.ResolutionInfo += " Reported struct size is unavailable; field reads were refused.";
            return;
        }

        var fields = new List<ResolvedNativeFieldInfo>();
        var truncated = false;
        ReadFields(structType, snapshot.Address, snapshot.Size, 0, string.Empty, 0, fields, ref truncated);
        snapshot.Fields = fields;
        snapshot.IsTruncated = truncated;
        snapshot.ResolutionInfo += $" Inline inspection: depth {MaxInlineDepth}, {MaxInlineArrayElements} elements per array, {MaxInspectionFields} fields maximum; arbitrary pointers are not followed.";
        if (truncated)
            snapshot.ResolutionInfo += " Some inline fields were omitted at an inspection limit.";
    }

    private static ClientStructTypeMaps BuildStructTypeMaps()
    {
        var addons = new Dictionary<string, Type>(NameComparer);
        var agents = new Dictionary<AgentId, Type>();
        foreach (var type in GetClientStructTypes())
        {
            var addonAttribute = type.GetCustomAttribute<AddonAttribute>();
            if (addonAttribute != null)
            {
                foreach (var identifier in addonAttribute.AddonIdentifiers)
                {
                    if (!string.IsNullOrWhiteSpace(identifier))
                        addons[identifier] = type;
                }
            }

            var agentAttribute = type.GetCustomAttribute<AgentAttribute>();
            if (agentAttribute != null)
                agents[agentAttribute.Id] = type;
        }

        return new ClientStructTypeMaps(addons, agents);
    }

    private static IEnumerable<Type> GetClientStructTypes()
    {
        try
        {
            return typeof(AtkResNode).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            DiagnosticLog.WarningOnce(
                "ClientStructRuntimeInspector.ReflectionTypeLoad",
                "FFXIVClientStructs type discovery loaded only a partial type set. String-keyed addon/agent recognition may be incomplete.",
                ex);
            return ex.Types.Where(type => type != null)!;
        }
    }

    private static unsafe AgentId? TryResolveAgentId(nint agentAddress)
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return null;

            foreach (var agentId in AgentIds)
            {
                try
                {
                    var agent = agentModule->GetAgentByInternalId(agentId);
                    if ((nint)agent == agentAddress)
                        return agentId;
                }
                catch (Exception ex)
                {
                    // Some agent ids are noisy in live memory. Keep scanning.
                    DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryResolveAgentId.Entry", ex);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryResolveAgentId", ex);
        }

        return null;
    }

    private static void ReadFields(Type structType, nint baseAddress, int structSize, int rootOffset,
        string prefix, int depth, List<ResolvedNativeFieldInfo> results, ref bool truncated)
    {
        var layout = FieldLayouts.GetOrAdd(structType, BuildFieldLayout);
        foreach (var entry in layout.Fields)
        {
            if (results.Count >= MaxInspectionFields)
            {
                truncated = true;
                return;
            }

            var offset = entry.Offset;
            var field = entry.Field;
            var hasAddress = TryAddAddress(baseAddress, offset, out var fieldAddress);
            var fieldInfo = new ResolvedNativeFieldInfo
            {
                Offset = rootOffset + offset,
                Name = prefix + field.Name,
                TypeName = FormatTypeName(field.FieldType),
                Address = hasAddress ? fieldAddress : 0,
            };

            var notes = entry.Notes;
            var withinStruct = IsFieldWithinBounds(offset, entry.StorageSize, structSize);
            if (!hasAddress)
            {
                fieldInfo.Value = "<unreadable>";
                fieldInfo.Notes = JoinNotes(notes, "field address overflow");
            }
            else if (!withinStruct)
            {
                fieldInfo.Value = "<unreadable>";
                fieldInfo.Notes = JoinNotes(notes, "field range exceeds reported struct size");
            }
            else if (entry.StorageSize > 0 && !IsReadableRange(fieldAddress, (nuint)entry.StorageSize))
            {
                fieldInfo.Value = "<unreadable>";
                fieldInfo.Notes = JoinNotes(notes, $"native range is not readable ({entry.StorageSize} bytes)");
            }
            else
            {
                var readable = TryReadFieldValue(fieldInfo.Address, field.FieldType, out var value, out var readNotes);
                fieldInfo.IsReadable = readable;
                fieldInfo.Value = value;
                fieldInfo.Notes = JoinNotes(notes, readNotes);

                if (entry.ArrayElementType != null || (!readable && CanExpandInline(field.FieldType)))
                {
                    fieldInfo.IsReadable = entry.StorageSize > 0;
                    fieldInfo.Value = entry.ArrayElementType == null
                        ? $"<{FormatTypeName(field.FieldType)} inline>"
                        : $"<{FormatTypeName(entry.ArrayElementType)}[{entry.ArrayLength}] inline>";
                    results.Add(fieldInfo);
                    if (depth >= MaxInlineDepth)
                    {
                        fieldInfo.Notes = JoinNotes(notes, "inline depth limit reached");
                        truncated = true;
                    }
                    else if (entry.ArrayElementType != null)
                    {
                        ReadInlineArray(entry, fieldInfo, depth + 1, results, ref truncated);
                    }
                    else
                    {
                        var before = results.Count;
                        ReadFields(field.FieldType, fieldAddress, entry.StorageSize, fieldInfo.Offset,
                            fieldInfo.Name + ".", depth + 1, results, ref truncated);
                        fieldInfo.Notes = JoinNotes(notes, before == results.Count ? "inline layout has no supported field offsets" : "inline fields expanded below");
                    }
                    continue;
                }
            }
            results.Add(fieldInfo);
        }
    }

    private static void ReadInlineArray(NativeFieldLayout entry, ResolvedNativeFieldInfo parent, int depth,
        List<ResolvedNativeFieldInfo> results, ref bool truncated)
    {
        parent.Notes = JoinNotes(entry.Notes, "inline elements expanded below");
        var elementType = entry.ArrayElementType!;
        var stride = GetNativeStorageSize(elementType);
        if (stride <= 0 || entry.ArrayLength <= 0 || (long)stride * entry.ArrayLength > entry.StorageSize)
        {
            parent.Notes = JoinNotes(parent.Notes, "array element layout is unavailable or exceeds its field");
            return;
        }

        var count = Math.Min(entry.ArrayLength, MaxInlineArrayElements);
        for (var i = 0; i < count; i++)
        {
            if (results.Count >= MaxInspectionFields)
            {
                truncated = true;
                break;
            }
            var offset = i * stride;
            if (!TryAddAddress(parent.Address, offset, out var address))
                break;
            var item = new ResolvedNativeFieldInfo
            {
                Offset = parent.Offset + offset,
                Name = $"{parent.Name}[{i}]",
                TypeName = FormatTypeName(elementType),
                Address = address,
            };
            item.IsReadable = TryReadFieldValue(address, elementType, out var value, out var notes);
            item.Value = value;
            item.Notes = notes;
            results.Add(item);
            if (!item.IsReadable && CanExpandInline(elementType) && IsReadableRange(address, (nuint)stride))
            {
                item.IsReadable = true;
                item.Value = $"<{FormatTypeName(elementType)} inline>";
                if (depth >= MaxInlineDepth)
                {
                    item.Notes = "inline depth limit reached";
                    truncated = true;
                }
                else
                {
                    ReadFields(elementType, address, stride, item.Offset, item.Name + ".", depth + 1, results, ref truncated);
                }
            }
        }
        if (entry.ArrayLength > count)
        {
            parent.Notes = JoinNotes(parent.Notes, $"array preview limited to first {count} of {entry.ArrayLength} elements");
            truncated = true;
        }
    }

    internal static bool IsFieldWithinBounds(int offset, int fieldSize, int ownerSize)
        => offset >= 0 && ownerSize > 0 && fieldSize > 0 && fieldSize <= ownerSize && offset <= ownerSize - fieldSize;

    private static bool CanExpandInline(Type type)
        => type.IsValueType && !type.IsPrimitive && !type.IsEnum && GetDirectReadSize(type) == 0;

    private static NativeTypeLayout BuildFieldLayout(Type type)
    {
        var fields = new List<NativeFieldLayout>();
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var offset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
            // Marshal offsets are safe for blittable sequential layouts, but bool/char marshaling
            // can change the actual inline CLR layout. Leave those unsupported rather than guess.
            if (!offset.HasValue && type.IsLayoutSequential && IsBlittableLayout(type, 0))
            {
                try { offset = checked((int)Marshal.OffsetOf(type, field.Name)); }
                catch (Exception) { }
            }
            if (!offset.HasValue)
                continue;
            var fixedBuffer = field.GetCustomAttribute<FixedBufferAttribute>();
            var element = fixedBuffer?.ElementType;
            var count = fixedBuffer?.Length ?? 0;
            if (element == null && TryGetInlineArray(field.FieldType, out var inlineElement, out var inlineCount))
            {
                element = inlineElement;
                count = inlineCount;
            }
            var size = element == null ? GetNativeStorageSize(field.FieldType) : GetArrayStorageSize(element, count);
            var notes = element == null ? string.Empty : $"fixed-size inline array ({count} elements)";
            fields.Add(new NativeFieldLayout(field, offset.Value, size, element, count, notes));
        }
        return new NativeTypeLayout(fields.OrderBy(field => field.Offset).ThenBy(field => field.Field.Name, StringComparer.Ordinal).ToArray());
    }

    private static bool IsBlittableLayout(Type type, int depth)
    {
        if (depth > 8 || type == typeof(bool) || type == typeof(char))
            return false;
        if (type.IsPointer || type.IsFunctionPointer || type.IsEnum || type.IsPrimitive || type == typeof(nint) || type == typeof(nuint))
            return true;
        return type.IsValueType && (type.IsExplicitLayout || type.IsLayoutSequential)
            && type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .All(field => IsBlittableLayout(field.FieldType, depth + 1));
    }

    private static bool TryGetInlineArray(Type type, out Type? elementType, out int count)
    {
        elementType = null;
        count = type.GetCustomAttribute<InlineArrayAttribute>()?.Length ?? 0;
        if (count <= 0)
            return false;
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fields.Length != 1)
            return false;
        elementType = fields[0].FieldType;
        return true;
    }

    private static int GetArrayStorageSize(Type elementType, int count)
    {
        var stride = GetNativeStorageSize(elementType);
        return stride > 0 && count > 0 && count <= int.MaxValue / stride ? stride * count : 0;
    }

    private static int GetNativeStorageSize(Type type)
    {
        var direct = GetDirectReadSize(type);
        return direct > 0 ? direct : type.IsValueType ? TryGetStructSize(type) : 0;
    }

    private static string JoinNotes(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;

        return $"{first}; {second}";
    }

    private static bool TryReadFieldValue(nint address, Type fieldType, out string value, out string notes)
    {
        value = "<unreadable>";
        notes = string.Empty;

        if (address == 0)
        {
            notes = "address is 0";
            return false;
        }

        var readSize = GetDirectReadSize(fieldType);
        if (readSize <= 0)
        {
            notes = "nested struct / metadata only";
            value = $"<{FormatTypeName(fieldType)}>";
            return false;
        }

        if (!IsReadableRange(address, (nuint)readSize))
        {
            notes = $"native range is not readable ({readSize} bytes)";
            return false;
        }

        try
        {
            if (fieldType.IsEnum)
            {
                if (!TryReadPrimitive(fieldType.GetEnumUnderlyingType(), address, out var rawValue))
                {
                    notes = "enum underlying type not supported";
                    value = $"<{FormatTypeName(fieldType)}>";
                    return false;
                }

                var enumValue = Enum.ToObject(fieldType, rawValue);
                value = $"{enumValue} ({Convert.ToString(rawValue, CultureInfo.InvariantCulture)})";
                return true;
            }

            if (IsCStringPointerType(fieldType))
                return TryReadCStringPointerValue(address, out value, out notes);

            if (fieldType.IsPointer || fieldType.IsFunctionPointer || fieldType == typeof(IntPtr) || fieldType == typeof(UIntPtr) || IsClientStructPointer(fieldType))
            {
                var pointerValue = Marshal.ReadIntPtr(address);
                value = $"0x{pointerValue.ToInt64():X}";
                return true;
            }

            if (fieldType == typeof(bool))
            {
                value = (Marshal.ReadByte(address) != 0).ToString();
                return true;
            }

            if (fieldType == typeof(byte))
            {
                value = Marshal.ReadByte(address).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(sbyte))
            {
                value = unchecked((sbyte)Marshal.ReadByte(address)).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(short))
            {
                value = Marshal.ReadInt16(address).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(ushort))
            {
                value = unchecked((ushort)Marshal.ReadInt16(address)).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(int))
            {
                value = Marshal.ReadInt32(address).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(uint))
            {
                value = unchecked((uint)Marshal.ReadInt32(address)).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(long))
            {
                value = Marshal.ReadInt64(address).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(ulong))
            {
                value = unchecked((ulong)Marshal.ReadInt64(address)).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(float))
            {
                value = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(address)).ToString("G9", CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(double))
            {
                value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(address)).ToString("G17", CultureInfo.InvariantCulture);
                return true;
            }

            if (fieldType == typeof(char))
            {
                value = $"'{(char)Marshal.ReadInt16(address)}'";
                return true;
            }

            if (TryReadStdVectorSummary(fieldType, address, out value, out notes))
                return true;

            if (!fieldType.IsValueType)
            {
                notes = "managed reference field";
                value = $"<{FormatTypeName(fieldType)}>";
                return false;
            }

            notes = "nested struct / metadata only";
            value = $"<{FormatTypeName(fieldType)}>";
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryReadFieldValue", ex);
            notes = "native field read failed";
            value = "<unreadable>";
            return false;
        }
    }

    private static bool TryReadPrimitive(Type fieldType, nint address, out object value)
    {
        value = 0;

        if (fieldType == typeof(byte))
        {
            value = Marshal.ReadByte(address);
            return true;
        }

        if (fieldType == typeof(sbyte))
        {
            value = unchecked((sbyte)Marshal.ReadByte(address));
            return true;
        }

        if (fieldType == typeof(short))
        {
            value = Marshal.ReadInt16(address);
            return true;
        }

        if (fieldType == typeof(ushort))
        {
            value = unchecked((ushort)Marshal.ReadInt16(address));
            return true;
        }

        if (fieldType == typeof(int))
        {
            value = Marshal.ReadInt32(address);
            return true;
        }

        if (fieldType == typeof(uint))
        {
            value = unchecked((uint)Marshal.ReadInt32(address));
            return true;
        }

        if (fieldType == typeof(long))
        {
            value = Marshal.ReadInt64(address);
            return true;
        }

        if (fieldType == typeof(ulong))
        {
            value = unchecked((ulong)Marshal.ReadInt64(address));
            return true;
        }

        return false;
    }

    private static bool IsClientStructPointer(Type fieldType)
        => fieldType.IsGenericType
           && fieldType.GetGenericTypeDefinition().FullName == "FFXIVClientStructs.Interop.Pointer`1";

    private static bool IsCStringPointerType(Type fieldType)
        => string.Equals(fieldType.FullName, "InteropGenerator.Runtime.CStringPointer", StringComparison.Ordinal);

    private static bool IsStdVectorType(Type fieldType, out Type? elementType)
    {
        elementType = null;
        if (!fieldType.IsGenericType)
            return false;

        var genericType = fieldType.GetGenericTypeDefinition();
        if (!string.Equals(genericType.FullName, "FFXIVClientStructs.STD.StdVector`1", StringComparison.Ordinal)
            && !string.Equals(genericType.FullName, "FFXIVClientStructs.STD.StdVector`2", StringComparison.Ordinal))
            return false;

        var arguments = fieldType.GetGenericArguments();
        if (arguments.Length == 0)
            return false;

        elementType = arguments[0];
        return true;
    }

    private static bool TryReadCStringPointerValue(nint address, out string value, out string notes)
    {
        value = "<unreadable>";
        notes = string.Empty;

        try
        {
            if (!IsReadableRange(address, (nuint)IntPtr.Size))
            {
                notes = "CStringPointer storage is not readable";
                return false;
            }

            var pointerValue = Marshal.ReadIntPtr(address);
            if (pointerValue == 0)
            {
                value = "0x0";
                return true;
            }

            if (TryReadUtf8CString(pointerValue, 160, out var text, out var truncated))
            {
                value = $"0x{pointerValue.ToInt64():X} \"{SanitizeInlineText(text, 96)}\"";
                if (truncated)
                    notes = "string preview truncated";

                return true;
            }

            value = $"0x{pointerValue.ToInt64():X}";
            notes = "string preview unavailable";
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryReadCStringPointerValue", ex);
            value = "<unreadable>";
            notes = "CStringPointer read failed";
            return false;
        }
    }

    private static bool TryReadStdVectorSummary(Type fieldType, nint address, out string value, out string notes)
    {
        value = "<unreadable>";
        notes = string.Empty;

        if (!IsStdVectorType(fieldType, out var elementType) || elementType == null)
            return false;

        try
        {
            if (!IsReadableRange(address, (nuint)(IntPtr.Size * 3)))
            {
                notes = "StdVector header is not readable";
                return false;
            }

            var first = Marshal.ReadIntPtr(address);
            var last = Marshal.ReadIntPtr(address + IntPtr.Size);
            var end = Marshal.ReadIntPtr(address + (IntPtr.Size * 2));
            var elementSize = GetElementStride(elementType);

            if (!TryValidateVectorHeader(first, last, end, elementSize, out var count, out var capacity, out notes))
                return false;

            var parts = new List<string>
            {
                $"First=0x{first.ToInt64():X}",
                $"Last=0x{last.ToInt64():X}",
                $"End=0x{end.ToInt64():X}",
                $"Count={count}",
                $"Capacity={capacity}"
            };

            if (count > 0 && IsCStringPointerType(elementType))
            {
                var preview = ReadCStringPointerVectorPreview(first, count, elementSize, 5);
                if (preview.Count > 0)
                    parts.Add($"Values=[{string.Join("; ", preview)}]");

                if (count > preview.Count && preview.Count > 0)
                    notes = $"preview shows first {preview.Count} values";
            }

            if (elementSize <= 0)
                notes = JoinNotes(notes, $"unsupported element size for {FormatTypeName(elementType)}");

            value = string.Join(" | ", parts);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryReadStdVectorSummary", ex);
            value = "<unreadable>";
            notes = "StdVector read failed";
            return false;
        }
    }

    private static int GetElementStride(Type elementType)
        => GetNativeStorageSize(elementType);

    internal static bool TryValidateVectorHeader(
        nint first,
        nint last,
        nint end,
        int elementSize,
        out long count,
        out long capacity,
        out string notes)
    {
        count = 0;
        capacity = 0;
        notes = string.Empty;

        if (first == 0 && last == 0 && end == 0)
            return true;

        if (elementSize <= 0)
        {
            notes = "StdVector element size is invalid";
            return false;
        }

        if (first == 0 || last == 0 || end == 0)
        {
            notes = "StdVector header mixes null and non-null pointers";
            return false;
        }

        var firstValue = (nuint)first;
        var lastValue = (nuint)last;
        var endValue = (nuint)end;
        if (firstValue > lastValue || lastValue > endValue)
        {
            notes = "invalid StdVector pointer ordering";
            return false;
        }

        var stride = (nuint)elementSize;
        var usedBytes = lastValue - firstValue;
        var capacityBytes = endValue - firstValue;
        if (usedBytes % stride != 0 || capacityBytes % stride != 0)
        {
            notes = "StdVector byte span is not aligned to its element size";
            return false;
        }

        var countValue = usedBytes / stride;
        var capacityValue = capacityBytes / stride;
        if (capacityValue > (nuint)MaxVectorElements
            || capacityValue < countValue)
        {
            notes = $"StdVector count/capacity is implausible ({countValue}/{capacityValue})";
            return false;
        }

        count = (long)countValue;
        capacity = (long)capacityValue;
        return true;
    }

    private static List<string> ReadCStringPointerVectorPreview(nint first, long count, int elementSize, int maxItems)
    {
        var preview = new List<string>();
        if (first == 0 || count <= 0 || elementSize <= 0)
            return preview;

        var previewCount = (int)Math.Min(count, maxItems);
        for (var i = 0; i < previewCount; i++)
        {
            try
            {
                if (!TryAddAddress(first, i * elementSize, out var elementAddress)
                    || !IsReadableRange(elementAddress, (nuint)IntPtr.Size))
                {
                    preview.Add($"{i}:<unreadable>");
                    continue;
                }

                var stringPointer = Marshal.ReadIntPtr(elementAddress);
                if (stringPointer == 0)
                {
                    preview.Add($"{i}:0x0");
                    continue;
                }

                if (TryReadUtf8CString(stringPointer, 80, out var text, out var truncated))
                {
                    var suffix = truncated ? "..." : string.Empty;
                    preview.Add($"{i}:\"{SanitizeInlineText(text, 36)}{suffix}\"");
                }
                else
                {
                    preview.Add($"{i}:0x{stringPointer.ToInt64():X}");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.ReadCStringPointerVectorPreview", ex);
                preview.Add($"{i}:<error>");
            }
        }

        return preview;
    }

    internal static bool TryReadUtf8CString(nint pointerValue, int maxBytes, out string value, out bool truncated)
    {
        value = string.Empty;
        truncated = false;

        if (pointerValue == 0 || maxBytes <= 0)
            return false;

        if (!IsReadableRange(pointerValue, (nuint)maxBytes))
            return false;

        try
        {
            var buffer = new byte[maxBytes];
            var length = 0;
            for (; length < maxBytes; length++)
            {
                var current = Marshal.ReadByte(pointerValue + length);
                if (current == 0)
                    break;

                buffer[length] = current;
            }

            truncated = length == maxBytes;
            value = Encoding.UTF8.GetString(buffer, 0, length);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryReadUtf8CString", ex);
            value = string.Empty;
            truncated = false;
            return false;
        }
    }

    private static string SanitizeInlineText(string value, int maxLength)
    {
        var normalized = value
            .Replace("\r", "\\r")
            .Replace("\n", "\\n");

        if (normalized.Length <= maxLength)
            return normalized;

        return TextDisplay.Truncate(normalized, maxLength, string.Empty, string.Empty);
    }

    internal static int GetDirectReadSize(Type fieldType)
    {
        if (fieldType.IsEnum)
            return GetDirectReadSize(fieldType.GetEnumUnderlyingType());

        if (fieldType.IsPointer || fieldType.IsFunctionPointer
            || fieldType == typeof(IntPtr)
            || fieldType == typeof(UIntPtr)
            || IsClientStructPointer(fieldType)
            || IsCStringPointerType(fieldType))
            return IntPtr.Size;

        if (IsStdVectorType(fieldType, out _))
            return IntPtr.Size * 3;

        if (fieldType == typeof(bool) || fieldType == typeof(byte) || fieldType == typeof(sbyte))
            return 1;
        if (fieldType == typeof(short) || fieldType == typeof(ushort) || fieldType == typeof(char))
            return 2;
        if (fieldType == typeof(int) || fieldType == typeof(uint) || fieldType == typeof(float))
            return 4;
        if (fieldType == typeof(long) || fieldType == typeof(ulong) || fieldType == typeof(double))
            return 8;

        return 0;
    }

    internal static bool TryAddAddress(nint baseAddress, int offset, out nint address)
    {
        address = 0;
        if (baseAddress == 0 || offset < 0)
            return false;

        try
        {
            var sum = checked(baseAddress.ToInt64() + offset);
            if (IntPtr.Size == 4 && (sum < int.MinValue || sum > int.MaxValue))
                return false;

            address = (nint)sum;
            return address != 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool IsReadableRange(nint address, nuint length)
    {
        if (address == 0 || length == 0)
            return false;

        var start = (nuint)address;
        if (nuint.MaxValue - start < length)
            return false;

        var requestedEnd = start + length;
        var current = start;
        while (current < requestedEnd)
        {
            if (!TryQueryMemory((nint)current, out var info))
                return false;

            var regionStart = (nuint)info.BaseAddress;
            if (info.RegionSize == 0 || nuint.MaxValue - regionStart < info.RegionSize)
                return false;

            var regionEnd = regionStart + info.RegionSize;
            if (current < regionStart || current >= regionEnd || !IsReadableProtection(info.State, info.Protect))
                return false;

            var next = regionEnd < requestedEnd ? regionEnd : requestedEnd;
            if (next <= current)
                return false;

            current = next;
        }

        return true;
    }

    private static bool TryQueryMemory(nint address, out MemoryBasicInformation info)
    {
        try
        {
            return VirtualQuery(
                address,
                out info,
                (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0;
        }
        catch (Exception ex)
        {
            info = default;
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.VirtualQuery", ex);
            return false;
        }
    }

    internal static bool IsReadableProtection(uint state, uint protect)
    {
        if (state != MemCommit || (protect & (PageNoAccess | PageGuard)) != 0)
            return false;

        return (protect & 0xFF) is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }

    private static int TryGetStructSize(Type structType)
        => StructSizes.GetOrAdd(structType, CalculateStructSize);

    private static int CalculateStructSize(Type structType)
    {
        if (structType == typeof(void) || !structType.IsValueType || structType.ContainsGenericParameters || structType.IsByRefLike
            || (!structType.IsExplicitLayout && !structType.IsLayoutSequential))
            return 0;

        try
        {
            // Native memory uses the actual CLR storage layout, including generic padding and inline arrays.
            // Marshal.SizeOf(Type) rejects closed generics and can enlarge bool/char fields for marshaling.
            return (int)ReferenceFreeSizeMethod.MakeGenericMethod(structType).Invoke(null, null)!;
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryGetStructSize", ex);
            return 0;
        }
    }

    private static int GetReferenceFreeSize<T>()
        => RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? 0 : Unsafe.SizeOf<T>();

    internal static string FormatTypeName(Type type)
    {
        if (type.IsPointer)
            return $"{FormatTypeName(type.GetElementType()!)}*";

        if (type.IsArray)
            return $"{FormatTypeName(type.GetElementType()!)}[]";

        if (type.IsGenericType)
        {
            var genericName = type.Name;
            var tickIndex = genericName.IndexOf('`');
            if (tickIndex >= 0)
                genericName = genericName[..tickIndex];

            var arguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
            return $"{genericName}<{arguments}>";
        }

        return type.Name;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    private sealed record ClientStructTypeMaps(
        Dictionary<string, Type> Addons,
        Dictionary<AgentId, Type> Agents);

    private sealed record NativeTypeLayout(NativeFieldLayout[] Fields);
    private sealed record NativeFieldLayout(FieldInfo Field, int Offset, int StorageSize,
        Type? ArrayElementType, int ArrayLength, string Notes);
}
