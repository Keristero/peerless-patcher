using System.Runtime.Versioning;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Reads and writes process memory on Linux via <c>/proc/&lt;pid&gt;/mem</c>.
/// Best-effort support for Proton/Wine game processes.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessMemory : IProcessMemory
{
    private readonly FileStream _mem;

    /// <param name="processId">PID of the target process.</param>
    /// <exception cref="InvalidOperationException">Thrown when /proc/pid/mem cannot be opened.</exception>
    public LinuxProcessMemory(int processId)
    {
        var memPath = $"/proc/{processId}/mem";
        try
        {
            _mem = new FileStream(memPath, FileMode.Open, FileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot open {memPath}. Ensure the patcher has ptrace permissions or runs as root. ({ex.Message})");
        }
    }

    public byte[] ReadBytes(long offset, int count)
    {
        _mem.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[count];
        int read = _mem.Read(buffer, 0, count);
        if (read != count)
            throw new IOException($"Expected {count} bytes at 0x{offset:X}, got {read}.");
        return buffer;
    }

    public void WriteBytes(long offset, byte[] data)
    {
        _mem.Seek(offset, SeekOrigin.Begin);
        _mem.Write(data, 0, data.Length);
        _mem.Flush();
    }

    public void Dispose() => _mem.Dispose();
}
