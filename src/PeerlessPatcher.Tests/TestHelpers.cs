// Shared test helpers

using PeerlessPatcher.Models;

namespace PeerlessPatcher.Tests;

internal static class TestHelpers
{
    /// <summary>Creates a minimal valid PatchProfile for use in tests.</summary>
    public static PatchProfile MakeMinimalProfile() => new()
    {
        GameId = "test-game",
        GameName = "Test Game",
        SteamAppId = 1,
        ProcessName = "test",
        Patches =
        [
            new PatchEntry
            {
                Type = "file-hex-edit",
                Name = "Test Patch",
                Description = "A test patch",
                FilePath = "test.exe",
                Offset = -1,
                FindBytes = [0x01, 0x02],
                ReplaceBytes = [0x03, 0x04],
            },
        ],
    };
}

/// <summary>Creates a temporary directory and deletes it on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    private readonly string _path;
    private TempDir(string path) => _path = path;

    public static TempDir Create()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    public static implicit operator string(TempDir d) => d._path;

    public void Dispose()
    {
        try { Directory.Delete(_path, recursive: true); } catch { /* best-effort */ }
    }
}
