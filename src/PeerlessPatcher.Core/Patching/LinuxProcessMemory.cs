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
    private readonly int _processId;

    /// <param name="processId">PID of the target process.</param>
    /// <exception cref="InvalidOperationException">Thrown when /proc/pid/mem cannot be opened.</exception>
    public LinuxProcessMemory(int processId)
    {
        _processId = processId;
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

    /// <summary>
    /// Reads <c>/proc/<i>pid</i>/maps</c> and returns all readable, file-backed memory regions.
    /// This covers both the <c>.text</c> (code) and <c>.rdata</c>/<c>.data</c> sections of the
    /// main executable and loaded libraries, which is sufficient for byte-pattern scanning.
    /// Anonymous regions ([stack], [heap], etc.) are excluded because game code and data
    /// always live in file-backed mappings under Wine/Proton.
    /// </summary>
    public IReadOnlyList<(long Start, long Length)> GetScanRegions()
    {
        var regions = new List<(long, long)>();
        var mapsPath = $"/proc/{_processId}/maps";

        foreach (var line in File.ReadLines(mapsPath))
        {
            // Format: start-end perms offset dev inode pathname
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;

            // Only readable regions
            var perms = parts[1];
            if (perms.Length < 4 || perms[0] != 'r') continue;

            // Skip anonymous / special mappings ([stack], [heap], [vdso], etc.)
            var path = parts.Length >= 6 ? parts[5] : string.Empty;
            if (string.IsNullOrEmpty(path) || path[0] == '[') continue;

            var rangeParts = parts[0].Split('-');
            if (rangeParts.Length != 2) continue;

            if (!long.TryParse(rangeParts[0], System.Globalization.NumberStyles.HexNumber, null, out var start)) continue;
            if (!long.TryParse(rangeParts[1], System.Globalization.NumberStyles.HexNumber, null, out var end)) continue;
            if (end <= start) continue;

            regions.Add((start, end - start));
        }

        return regions;
    }

    public void Dispose() => _mem.Dispose();
}
