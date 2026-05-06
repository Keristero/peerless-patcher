using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Reads and writes process memory on Windows using <c>ReadProcessMemory</c> / <c>WriteProcessMemory</c>.
/// Requires the calling process to have sufficient privileges (run as Administrator).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessMemory : IProcessMemory
{
    private const uint PROCESS_VM_READ           = 0x0010;
    private const uint PROCESS_VM_WRITE          = 0x0020;
    private const uint PROCESS_VM_OPERATION      = 0x0008;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;

    private const uint MEM_COMMIT = 0x1000;

    // Base page protection values (readable)
    private const uint PAGE_READONLY          = 0x02;
    private const uint PAGE_READWRITE         = 0x04;
    private const uint PAGE_WRITECOPY         = 0x08;
    private const uint PAGE_EXECUTE_READ      = 0x20;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    // Modifier bits to strip before checking base protection
    private const uint PAGE_GUARD        = 0x100;
    private const uint PAGE_NOCACHE      = 0x200;
    private const uint PAGE_WRITECOMBINE = 0x400;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int nSize, out nint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int nSize, out nint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQueryEx(nint hProcess, nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nint dwLength);

    /// <summary>
    /// MEMORY_BASIC_INFORMATION for 64-bit processes (48 bytes).
    /// Uses explicit field offsets to avoid layout differences caused by the
    /// optional PartitionId/Reserved fields added in Windows 10 build 1703.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct MEMORY_BASIC_INFORMATION
    {
        [FieldOffset(0)]  public nint BaseAddress;
        [FieldOffset(8)]  public nint AllocationBase;
        [FieldOffset(16)] public uint AllocationProtect;
        // offset 20: PartitionId (uint16) + Reserved (uint16) on Win10+ — skipped via explicit layout
        [FieldOffset(24)] public nint RegionSize;
        [FieldOffset(32)] public uint State;
        [FieldOffset(36)] public uint Protect;
        [FieldOffset(40)] public uint Type;
    }

    private readonly nint _handle;

    /// <param name="processId">The PID of the target process.</param>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot be opened (e.g. insufficient privileges).</exception>
    public WindowsProcessMemory(int processId)
    {
        _handle = OpenProcess(
            PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
            false, processId);
        if (_handle == nint.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                err == 5
                    ? "Insufficient permissions — run PeerlessPatcher as Administrator."
                    : $"Failed to open process {processId}. Win32 error: {err}");
        }
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, (nint)offset, buffer, count, out _))
            throw new IOException($"ReadProcessMemory failed at offset 0x{offset:X}. Win32 error: {Marshal.GetLastWin32Error()}");
        return buffer;
    }

    public void WriteBytes(long offset, byte[] data)
    {
        if (!WriteProcessMemory(_handle, (nint)offset, data, data.Length, out _))
            throw new IOException($"WriteProcessMemory failed at offset 0x{offset:X}. Win32 error: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>
    /// Enumerates all committed, readable memory regions in the process via <c>VirtualQueryEx</c>.
    /// </summary>
    public IReadOnlyList<(long Start, long Length)> GetScanRegions()
    {
        var regions = new List<(long, long)>();
        nint address = nint.Zero;
        var structSize = (nint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();

        while (true)
        {
            if (VirtualQueryEx(_handle, address, out var mbi, structSize) == 0)
                break;

            if (mbi.State == MEM_COMMIT && IsReadable(mbi.Protect))
                regions.Add(((long)mbi.BaseAddress, (long)mbi.RegionSize));

            var next = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            if (next <= (long)address) break; // Guard against overflow / not advancing
            address = (nint)next;
        }

        return regions;
    }

    private static bool IsReadable(uint protect)
    {
        var baseProtect = protect & ~(PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE);
        return baseProtect is PAGE_READONLY or PAGE_READWRITE or PAGE_WRITECOPY
                           or PAGE_EXECUTE_READ or PAGE_EXECUTE_READWRITE or PAGE_EXECUTE_WRITECOPY;
    }

    public void Dispose()
    {
        if (_handle != nint.Zero)
            CloseHandle(_handle);
    }
}
