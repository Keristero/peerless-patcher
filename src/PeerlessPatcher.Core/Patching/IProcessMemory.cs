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
}
