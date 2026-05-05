using Microsoft.Extensions.Logging.Abstractions;
using PeerlessPatcher.Models;
using PeerlessPatcher.Patching;

namespace PeerlessPatcher.Tests;

public class FileHexEditPatchHandlerTests
{
    private readonly FileHexEditPatchHandler _handler = new(NullLogger<FileHexEditPatchHandler>.Instance);

    private static PatchEntry MakePatch(byte[] find, byte[] replace, string filePath = "game.exe", long offset = -1) => new()
    {
        Type = "file-hex-edit",
        Name = "Test Patch",
        Description = "",
        FilePath = filePath,
        Offset = offset,
        FindBytes = find,
        ReplaceBytes = replace,
    };

    private static PatchContext MakeContext(string installPath) => new() { GameInstallPath = installPath };

    [Fact]
    public void Apply_ScanMode_FindsPatternAndPatches()
    {
        using var dir = TempDir.Create();
        var find = new byte[] { 0x39, 0x8E, 0xE3, 0x3F };
        var replace = new byte[] { 0x55, 0x55, 0x15, 0x40 };

        // Write a file with some padding then the find-bytes
        var contents = new byte[100];
        Array.Copy(find, 0, contents, 60, find.Length);
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), contents);

        var result = _handler.Apply(MakeContext(dir), MakePatch(find, replace));

        Assert.Equal(PatchResultStatus.Applied, result.Status);
        var written = File.ReadAllBytes(Path.Combine(dir, "game.exe"));
        Assert.Equal(replace, written[60..64]);
    }

    [Fact]
    public void Apply_FixedOffset_PatchesCorrectly()
    {
        using var dir = TempDir.Create();
        var find = new byte[] { 0x01, 0x02, 0x03 };
        var replace = new byte[] { 0xAA, 0xBB, 0xCC };
        var contents = new byte[] { 0xFF, 0x01, 0x02, 0x03, 0xFF };
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), contents);

        var result = _handler.Apply(MakeContext(dir), MakePatch(find, replace, offset: 1));

        Assert.Equal(PatchResultStatus.Applied, result.Status);
        var written = File.ReadAllBytes(Path.Combine(dir, "game.exe"));
        Assert.Equal(new byte[] { 0xFF, 0xAA, 0xBB, 0xCC, 0xFF }, written);
    }

    [Fact]
    public void Apply_PatternNotFound_ReturnsSignatureNotFound()
    {
        using var dir = TempDir.Create();
        var find = new byte[] { 0x01, 0x02, 0x03 };
        var replace = new byte[] { 0xAA, 0xBB, 0xCC };
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), new byte[20]); // all zeros

        var result = _handler.Apply(MakeContext(dir), MakePatch(find, replace));

        Assert.Equal(PatchResultStatus.SignatureNotFound, result.Status);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Apply_AlreadyPatched_ReturnsAlreadyPatched()
    {
        using var dir = TempDir.Create();
        var find = new byte[] { 0x01, 0x02 };
        var replace = new byte[] { 0xAA, 0xBB };
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), replace); // already has replace bytes

        var result = _handler.Apply(MakeContext(dir), MakePatch(find, replace));

        Assert.Equal(PatchResultStatus.AlreadyPatched, result.Status);
    }

    [Fact]
    public void Revert_PatchedFile_RestoresOriginalBytes()
    {
        using var dir = TempDir.Create();
        var find = new byte[] { 0x39, 0x8E, 0xE3, 0x3F };
        var replace = new byte[] { 0x55, 0x55, 0x15, 0x40 };
        // File currently has replace bytes (patched state)
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), replace);

        var result = _handler.Revert(MakeContext(dir), MakePatch(find, replace));

        Assert.Equal(PatchResultStatus.Reverted, result.Status);
        var written = File.ReadAllBytes(Path.Combine(dir, "game.exe"));
        Assert.Equal(find, written);
    }

    [Fact]
    public void Apply_MissingGameInstallPath_ReturnsError()
    {
        var entry = MakePatch(new byte[] { 0x01 }, new byte[] { 0x02 });
        var context = new PatchContext { GameInstallPath = null };

        var result = _handler.Apply(context, entry);

        Assert.Equal(PatchResultStatus.Error, result.Status);
        Assert.Contains("install path", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_FileNotFound_ReturnsError()
    {
        using var dir = TempDir.Create();
        var entry = MakePatch(new byte[] { 0x01 }, new byte[] { 0x02 }, "missing.exe");

        var result = _handler.Apply(MakeContext(dir), entry);

        Assert.Equal(PatchResultStatus.Error, result.Status);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
