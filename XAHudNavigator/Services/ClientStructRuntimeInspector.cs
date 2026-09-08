using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
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

        snapshot.Fields = ReadFields(structType, snapshot.Address, snapshot.Size);
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

    private static List<ResolvedNativeFieldInfo> ReadFields(Type structType, nint baseAddress, int structSize)
    {
        var fields = structType
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => new
            {
                Field = field,
                Offset = field.GetCustomAttribute<FieldOffsetAttribute>()?.Value
            })
            .Where(x => x.Offset.HasValue)
            .OrderBy(x => x.Offset!.Value)
            .ThenBy(x => x.Field.Name, StringComparer.Ordinal)
            .ToList();

        var results = new List<ResolvedNativeFieldInfo>(fields.Count);
        foreach (var entry in fields)
        {
            var offset = entry.Offset!.Value;
            var field = entry.Field;
            var hasAddress = TryAddAddress(baseAddress, offset, out var fieldAddress);
            var fieldInfo = new ResolvedNativeFieldInfo
            {
                Offset = offset,
                Name = field.Name,
                TypeName = FormatTypeName(field.FieldType),
                Address = hasAddress ? fieldAddress : 0,
            };

            var notes = BuildFieldNotes(field, structSize, offset);
            var readSize = GetDirectReadSize(field.FieldType);
            var withinStruct = offset >= 0
                               && (structSize <= 0
                                   || (readSize > 0 ? offset <= structSize - readSize : offset < structSize));
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
            else if (TryReadFieldValue(fieldInfo.Address, field.FieldType, out var value, out var readNotes))
            {
                fieldInfo.IsReadable = true;
                fieldInfo.Value = value;
                fieldInfo.Notes = JoinNotes(notes, readNotes);
            }
            else
            {
                fieldInfo.Value = value;
                fieldInfo.Notes = JoinNotes(notes, readNotes);
            }
            results.Add(fieldInfo);
        }

        return results;
    }

    private static string BuildFieldNotes(FieldInfo field, int structSize, int offset)
    {
        var notes = new List<string>();
        if (structSize > 0 && offset >= structSize)
            notes.Add($"offset exceeds reported size 0x{structSize:X}");

        if (field.CustomAttributes.Any(attr => attr.AttributeType.Name.Contains("FixedSizeArray", StringComparison.Ordinal)))
            notes.Add("fixed-size array");

        return string.Join("; ", notes);
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

            if (fieldType.IsPointer || fieldType == typeof(IntPtr) || fieldType == typeof(UIntPtr) || IsClientStructPointer(fieldType))
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
    {
        if (elementType.IsPointer || elementType == typeof(IntPtr) || elementType == typeof(UIntPtr))
            return IntPtr.Size;

        try
        {
            return Marshal.SizeOf(elementType);
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.GetElementStride", ex);
            return 0;
        }
    }

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

        if (fieldType.IsPointer
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
    {
        try
        {
            return Marshal.SizeOf(structType);
        }
        catch (Exception ex)
        {
            DiagnosticLog.VerboseThrottled("ClientStructRuntimeInspector.TryGetStructSize", ex);
            return 0;
        }
    }

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
}
