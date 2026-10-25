using System;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using InteropGenerator.Runtime;

namespace XAHudNavigator.Services;

/// <summary>Copies bounded native data into owned memory before decoding it. No native string getters are invoked.</summary>
internal static unsafe partial class NativeSheetReader
{
    internal const int MaxTextBytes = 4096;
    internal const int MaxStructBytes = 1024 * 1024;
    private static readonly nint CurrentProcess = -1;

    internal static bool TryCopy<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        var size = sizeof(T);
        if (size <= 0 || size > MaxStructBytes)
            return false;
        fixed (T* destination = &value)
        {
            if (Copy(address, destination, (nuint)size))
                return true;
        }
        // ReadProcessMemory may have copied a prefix before reporting failure.
        value = default;
        return false;
    }

    internal static string ReadUtf8String(Utf8String* address)
    {
        if (address == null)
            return string.Empty;
        if (!TryCopy((nint)address, out Utf8String header))
            return Unavailable("Utf8String header is unreadable");
        var pointer = (nint)header.StringPtr.Value;
        if (header.BufUsed < 0 || header.BufSize < 0 || header.StringLength < 0)
            return Unavailable("Utf8String has negative length or capacity");
        if (pointer == 0)
            return header.BufUsed <= 1 && header.StringLength == 0 && header.BufSize >= header.BufUsed
                ? string.Empty
                : Unavailable("Utf8String text pointer is null for nonempty data");
        if (header.BufUsed < 1 || header.BufUsed > MaxTextBytes + 1L)
            return Unavailable($"Utf8String length exceeds the {MaxTextBytes}-byte text limit or omits its terminator");
        if (header.BufSize < header.BufUsed || header.StringLength > header.BufUsed - 1)
            return Unavailable("Utf8String length and capacity disagree");
        if (header.IsUsingInlineBuffer && (header.BufUsed > 64 || header.BufSize > 64))
            return Unavailable("Utf8String inline length or capacity exceeds 64 bytes");

        var used = (int)header.BufUsed;
        Span<byte> buffer = stackalloc byte[MaxTextBytes + 1];
        var payload = buffer[..used];
        if (!CopyBytes(pointer, payload))
            return Unavailable("Utf8String payload is unreadable");
        if (payload[^1] != 0 || payload[..^1].IndexOf((byte)0) >= 0)
            return Unavailable("Utf8String terminator disagrees with its declared length");
        // UTF-8 conversion is now restricted to our owned bytes. Preserve the previous replacement-character behavior for SeString payload bytes.
        return Encoding.UTF8.GetString(payload[..^1]);
    }

    internal static string ReadCString(CStringPointer value)
        => ReadTerminated((nint)value.Value, MaxTextBytes + 1, "C string");

    internal static string ReadFixedString(nint address, int capacity)
    {
        if (capacity < 0 || capacity > MaxTextBytes + 1)
            return Unavailable($"Fixed string capacity exceeds the {MaxTextBytes + 1}-byte inspection limit");
        if (capacity == 0)
            return string.Empty;
        return ReadTerminated(address, capacity, "Fixed string");
    }

    private static string ReadTerminated(nint address, int capacity, string kind)
    {
        if (address == 0)
            return string.Empty;
        Span<byte> buffer = stackalloc byte[MaxTextBytes + 1];
        var copied = 0;
        while (copied < capacity)
        {
            if (!TryOffset(address, copied, out var current))
                return Unavailable($"{kind} address overflow");
            var pageRemaining = Environment.SystemPageSize - (int)((nuint)current % (nuint)Environment.SystemPageSize);
            var count = Math.Min(capacity - copied, pageRemaining);
            var chunk = buffer.Slice(copied, count);
            if (!CopyBytes(current, chunk))
                return Unavailable($"{kind} memory is unreadable");
            var terminator = chunk.IndexOf((byte)0);
            if (terminator >= 0)
                return Encoding.UTF8.GetString(buffer[..(copied + terminator)]);
            copied += count;
        }
        return Unavailable($"{kind} has no terminator within {capacity} bytes");
    }

    internal static bool CopyBytes(nint address, Span<byte> destination)
    {
        if (destination.Length == 0)
            return true;
        if (destination.Length > MaxTextBytes + 1)
            return false;
        fixed (byte* buffer = destination)
            return Copy(address, buffer, (nuint)destination.Length);
    }

    private static bool Copy(nint address, void* destination, nuint size)
    {
        if (address == 0 || size == 0 || size > MaxStructBytes || (nuint)address > nuint.MaxValue - (size - 1))
            return false;
        return ReadProcessMemory(CurrentProcess, address, destination, size, out var bytesRead) && bytesRead == size;
    }

    private static bool TryOffset(nint address, int offset, out nint result)
    {
        result = 0;
        if (offset < 0 || (nuint)address > nuint.MaxValue - (nuint)offset)
            return false;
        result = (nint)((nuint)address + (nuint)offset);
        return result != 0;
    }

    private static string Unavailable(string reason) => $"<unavailable: {reason}>";

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint bytesRead);
}
