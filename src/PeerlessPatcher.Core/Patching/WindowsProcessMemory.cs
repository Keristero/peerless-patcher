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
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int nSize, out nint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int nSize, out nint lpNumberOfBytesWritten);

    private readonly nint _handle;

    /// <param name="processId">The PID of the target process.</param>
    /// <exception cref="InvalidOperationException">Thrown when the process cannot be opened (e.g. insufficient privileges).</exception>
    public WindowsProcessMemory(int processId)
    {
        _handle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, processId);
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

    public void Dispose()
    {
        if (_handle != nint.Zero)
            CloseHandle(_handle);
    }
}
