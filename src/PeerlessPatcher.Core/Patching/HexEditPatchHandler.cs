using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Applies and reverts in-memory hex patches to a running process.
/// Validates the find-bytes signature before writing to avoid corrupting unrelated memory.
/// </summary>
public sealed class HexEditPatchHandler : IPatchHandler
{
    public string HandledType => "hex-edit";

    public PatchResult Apply(PatchContext context, PatchEntry entry)
    {
        var memory = context.ProcessMemory;
        if (memory is null)
            return new PatchResult(PatchResultStatus.Error, "No process memory handle — ensure the game is running and the patcher has permissions.");

        if (entry.FindBytes is null || entry.ReplaceBytes is null)
            return new PatchResult(PatchResultStatus.Error, "Patch entry is missing findBytes or replaceBytes.");

        byte[] current;
        try
        {
            current = memory.ReadBytes(entry.Offset, entry.FindBytes.Length);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Memory read failed: {ex.Message}");
        }

        if (current.SequenceEqual(entry.ReplaceBytes))
            return new PatchResult(PatchResultStatus.AlreadyPatched);

        if (!current.SequenceEqual(entry.FindBytes))
            return new PatchResult(PatchResultStatus.SignatureNotFound,
                "Patch signature not found — game version may be unsupported.");

        try
        {
            memory.WriteBytes(entry.Offset, entry.ReplaceBytes);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Memory write failed: {ex.Message}");
        }

        return new PatchResult(PatchResultStatus.Applied);
    }

    public PatchResult Revert(PatchContext context, PatchEntry entry)
    {
        var memory = context.ProcessMemory;
        if (memory is null)
            return new PatchResult(PatchResultStatus.Error, "No process memory handle.");

        if (entry.FindBytes is null || entry.ReplaceBytes is null)
            return new PatchResult(PatchResultStatus.Error, "Patch entry is missing findBytes or replaceBytes.");

        byte[] current;
        try
        {
            current = memory.ReadBytes(entry.Offset, entry.ReplaceBytes.Length);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Memory read failed: {ex.Message}");
        }

        if (current.SequenceEqual(entry.FindBytes))
            return new PatchResult(PatchResultStatus.AlreadyUnpatched);

        try
        {
            memory.WriteBytes(entry.Offset, entry.FindBytes);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Memory write failed: {ex.Message}");
        }

        return new PatchResult(PatchResultStatus.Reverted);
    }
}
