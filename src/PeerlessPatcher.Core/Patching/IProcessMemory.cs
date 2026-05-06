namespace PeerlessPatcher.Patching;

/// <summary>
/// Abstraction over process memory for reading and writing bytes.
/// </summary>
public interface IProcessMemory : IDisposable
{
    /// <summary>Reads <paramref name="count"/> bytes from <paramref name="offset"/>.</summary>
    byte[] ReadBytes(long offset, int count);

    /// <summary>Writes <paramref name="data"/> to <paramref name="offset"/>.</summary>
    void WriteBytes(long offset, byte[] data);

    /// <summary>
    /// Returns all readable memory regions in the target process that are suitable for pattern scanning.
    /// On Linux these are file-backed readable mappings from <c>/proc/pid/maps</c>.
    /// On Windows these are committed readable virtual memory regions from <c>VirtualQueryEx</c>.
    /// </summary>
    IReadOnlyList<(long Start, long Length)> GetScanRegions();
}
