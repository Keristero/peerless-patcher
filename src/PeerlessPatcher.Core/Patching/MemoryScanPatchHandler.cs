using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Patches a running game process by scanning its readable memory regions for a byte pattern,
/// then replacing each occurrence with the replacement bytes.
/// This is the handler for the <c>"memory-scan"</c> patch type.
///
/// <para>
/// Unlike <see cref="HexEditPatchHandler"/> (which requires a fixed absolute address),
/// this handler does not need to know where the target instruction lives in virtual memory.
/// It walks all readable, file-backed memory regions exposed by the OS and locates the
/// pattern at runtime — robust against ASLR and minor version differences in the exe layout.
/// </para>
/// <para>
/// <c>findBytes</c> and <c>replaceBytes</c> must be the same length.
/// </para>
/// </summary>
public sealed class MemoryScanPatchHandler : IPatchHandler
{
    public string HandledType => "memory-scan";

    public PatchResult Apply(PatchContext context, PatchEntry entry) => Patch(context, entry, applyDirection: true);
    public PatchResult Revert(PatchContext context, PatchEntry entry) => Patch(context, entry, applyDirection: false);
    public PatchResult Probe(PatchContext context, PatchEntry entry) =>
        new PatchResult(PatchResultStatus.Unsupported);

    private static PatchResult Patch(PatchContext context, PatchEntry entry, bool applyDirection)
    {
        var memory = context.ProcessMemory;
        if (memory is null)
            return new PatchResult(PatchResultStatus.Error,
                "No process memory handle — ensure the game is running and the patcher has permissions.");

        if (entry.FindBytes is null || entry.ReplaceBytes is null)
            return new PatchResult(PatchResultStatus.Error,
                "memory-scan patch requires both findBytes and replaceBytes.");

        if (entry.FindBytes.Length != entry.ReplaceBytes.Length)
            return new PatchResult(PatchResultStatus.Error,
                "memory-scan patch: findBytes and replaceBytes must be the same length.");

        byte[] findBytes  = applyDirection ? entry.FindBytes  : entry.ReplaceBytes;
        byte[] writeBytes = applyDirection ? entry.ReplaceBytes : entry.FindBytes;
        var alreadyStatus = applyDirection ? PatchResultStatus.AlreadyPatched : PatchResultStatus.AlreadyUnpatched;

        IReadOnlyList<(long Start, long Length)> regions;
        try
        {
            regions = memory.GetScanRegions();
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error,
                $"Failed to enumerate process memory regions: {ex.Message}");
        }

        int applied = 0;
        bool alreadyDone = false;

        foreach (var (start, length) in regions)
        {
            if (length < findBytes.Length) continue;

            byte[] regionData;
            try
            {
                // Cap at int.MaxValue to avoid overflow; no single region should realistically exceed 2 GB
                regionData = memory.ReadBytes(start, (int)Math.Min(length, int.MaxValue));
            }
            catch
            {
                // Skip regions that can't be read (race conditions, guard pages, etc.)
                continue;
            }

            for (int i = 0; i <= regionData.Length - findBytes.Length; i++)
            {
                if (MatchesAt(regionData, i, findBytes))
                {
                    try
                    {
                        memory.WriteBytes(start + i, writeBytes);
                    }
                    catch (Exception ex)
                    {
                        return new PatchResult(PatchResultStatus.Error,
                            $"Memory write failed at 0x{(start + i):X}: {ex.Message}");
                    }

                    applied++;
                    i += findBytes.Length - 1; // advance past the match
                }
                else if (!alreadyDone && MatchesAt(regionData, i, writeBytes))
                {
                    alreadyDone = true;
                }
            }
        }

        if (applied > 0)
        {
            var detail = applied > 1 ? $" ({applied} occurrences)" : null;
            return new PatchResult(
                applyDirection ? PatchResultStatus.Applied : PatchResultStatus.Reverted,
                detail);
        }

        return alreadyDone
            ? new PatchResult(alreadyStatus)
            : new PatchResult(PatchResultStatus.SignatureNotFound,
                "Pattern not found in process memory — game version may be unsupported or the game is not running.");
    }

    private static bool MatchesAt(byte[] data, int index, byte[] pattern)
    {
        if (index + pattern.Length > data.Length) return false;
        for (int j = 0; j < pattern.Length; j++)
            if (data[index + j] != pattern[j]) return false;
        return true;
    }
}
