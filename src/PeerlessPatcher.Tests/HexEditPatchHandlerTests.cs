using PeerlessPatcher.Models;
using PeerlessPatcher.Patching;

namespace PeerlessPatcher.Tests;

public class HexEditPatchHandlerTests
{
    private readonly HexEditPatchHandler _handler = new();

    private static PatchEntry MakePatch(byte[] find, byte[] replace, long offset = 0) => new()
    {
        Type = "hex-edit",
        Name = "Test",
        Description = "",
        Offset = offset,
        FindBytes = find,
        ReplaceBytes = replace,
    };

    [Fact]
    public void Apply_FindBytesMatch_WritesReplaceBytes()
    {
        var find = new byte[] { 0x01, 0x02, 0x03 };
        var replace = new byte[] { 0xAA, 0xBB, 0xCC };
        var memory = new FakeProcessMemory(find);
        var context = new PatchContext { ProcessMemory = memory };
        var entry = MakePatch(find, replace);

        var result = _handler.Apply(context, entry);

        Assert.Equal(PatchResultStatus.Applied, result.Status);
        Assert.Equal(replace, memory.Written);
    }

    [Fact]
    public void Apply_FindBytesMismatch_ReturnsSignatureNotFound_WithoutWriting()
    {
        var find = new byte[] { 0x01, 0x02, 0x03 };
        var replace = new byte[] { 0xAA, 0xBB, 0xCC };
        var memory = new FakeProcessMemory(new byte[] { 0xFF, 0xFF, 0xFF }); // different
        var context = new PatchContext { ProcessMemory = memory };
        var entry = MakePatch(find, replace);

        var result = _handler.Apply(context, entry);

        Assert.Equal(PatchResultStatus.SignatureNotFound, result.Status);
        Assert.Null(memory.Written); // no write occurred
    }

    [Fact]
    public void Apply_AlreadyPatched_ReturnsAlreadyPatched()
    {
        var find = new byte[] { 0x01, 0x02 };
        var replace = new byte[] { 0xAA, 0xBB };
        var memory = new FakeProcessMemory(replace); // already has replace bytes
        var context = new PatchContext { ProcessMemory = memory };
        var entry = MakePatch(find, replace);

        var result = _handler.Apply(context, entry);

        Assert.Equal(PatchResultStatus.AlreadyPatched, result.Status);
        Assert.Null(memory.Written);
    }

    [Fact]
    public void Revert_PatchedState_WritesOriginalBytes()
    {
        var find = new byte[] { 0x01, 0x02, 0x03 };
        var replace = new byte[] { 0xAA, 0xBB, 0xCC };
        var memory = new FakeProcessMemory(replace); // currently patched
        var context = new PatchContext { ProcessMemory = memory };
        var entry = MakePatch(find, replace);

        var result = _handler.Revert(context, entry);

        Assert.Equal(PatchResultStatus.Reverted, result.Status);
        Assert.Equal(find, memory.Written);
    }

    [Fact]
    public void Revert_AlreadyUnpatched_ReturnsAlreadyUnpatched()
    {
        var find = new byte[] { 0x01, 0x02 };
        var replace = new byte[] { 0xAA, 0xBB };
        var memory = new FakeProcessMemory(find); // already original
        var context = new PatchContext { ProcessMemory = memory };
        var entry = MakePatch(find, replace);

        var result = _handler.Revert(context, entry);

        Assert.Equal(PatchResultStatus.AlreadyUnpatched, result.Status);
        Assert.Null(memory.Written);
    }

    // ── Fake in-memory implementation ─────────────────────────────────────────

    private sealed class FakeProcessMemory(byte[] contents) : IProcessMemory
    {
        private byte[] _contents = contents;
        public byte[]? Written { get; private set; }

        public byte[] ReadBytes(long offset, int count) => _contents[..(int)Math.Min(count, _contents.Length)];

        public void WriteBytes(long offset, byte[] data)
        {
            Written = data;
            _contents = data;
        }

        public IReadOnlyList<(long Start, long Length)> GetScanRegions() => [];

        public void Dispose() { }
    }
}
