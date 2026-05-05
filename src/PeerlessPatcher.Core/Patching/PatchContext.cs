namespace PeerlessPatcher.Patching;

/// <summary>
/// Context passed to <see cref="IPatchHandler"/> implementations.
/// Contains both process memory (for in-process hex patches) and the game's
/// install path on disk (for file-based patches).
/// Either field may be null depending on the patch type.
/// </summary>
public sealed class PatchContext : IDisposable
{
    /// <summary>
    /// Handle to the running game process's virtual memory.
    /// Available for <c>hex-edit</c> patches. Null if memory access failed or is not needed.
    /// </summary>
    public IProcessMemory? ProcessMemory { get; init; }

    /// <summary>
    /// Absolute path to the game's installation directory on disk.
    /// Available for <c>file-hex-edit</c> and <c>file-replace</c> patches.
    /// Null if the install path could not be located.
    /// </summary>
    public string? GameInstallPath { get; init; }

    /// <summary>PID of the running game process.</summary>
    public int ProcessId { get; init; }

    /// <summary>Screen width used to compute aspect-ratio replacement bytes.</summary>
    public int ScreenWidth { get; init; } = 3440;

    /// <summary>Screen height used to compute aspect-ratio replacement bytes.</summary>
    public int ScreenHeight { get; init; } = 1440;

    public void Dispose() => ProcessMemory?.Dispose();
}
