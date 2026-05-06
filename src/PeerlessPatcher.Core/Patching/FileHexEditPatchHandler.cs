using Microsoft.Extensions.Logging;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Patches a game file on disk by replacing bytes at a known offset or by scanning for a byte pattern.
/// This is the handler for the <c>"file-hex-edit"</c> patch type.
///
/// <para>
/// Offset behaviour:
/// <list type="bullet">
///   <item><description>
///     If <see cref="PatchEntry.Offset"/> is <c>-1</c> the handler scans the file from the
///     beginning and applies the patch at the <em>first</em> occurrence of <see cref="PatchEntry.FindBytes"/>.
///   </description></item>
///   <item><description>
///     Any other value is treated as an absolute byte offset within the file.
///   </description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FileHexEditPatchHandler : IPatchHandler
{
    private readonly ILogger<FileHexEditPatchHandler> _logger;

    public FileHexEditPatchHandler(ILogger<FileHexEditPatchHandler> logger)
    {
        _logger = logger;
    }

    public string HandledType => "file-hex-edit";

    public PatchResult Apply(PatchContext context, PatchEntry entry) =>
        Patch(context, entry, applyDirection: true);

    public PatchResult Revert(PatchContext context, PatchEntry entry) =>
        Patch(context, entry, applyDirection: false);

    public PatchResult Probe(PatchContext context, PatchEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FilePath))
            return new PatchResult(PatchResultStatus.Error, "file-hex-edit patch is missing the 'filePath' field.");

        if (string.IsNullOrWhiteSpace(context.GameInstallPath))
            return new PatchResult(PatchResultStatus.Error, "Game install path could not be determined.");

        var fullPath = Path.Combine(context.GameInstallPath, entry.FilePath);
        if (!File.Exists(fullPath))
            return new PatchResult(PatchResultStatus.Error, $"Target file not found: {fullPath}");

        byte[] fileBytes;
        try { fileBytes = File.ReadAllBytes(fullPath); }
        catch (Exception ex) { return new PatchResult(PatchResultStatus.Error, $"Failed to read '{entry.FilePath}': {ex.Message}"); }

        byte[] effectiveReplaceBytes = ComputeReplaceBytes(entry, context);

        // ── Sites mode ────────────────────────────────────────────────────────
        if (entry.Sites is { Count: > 0 })
        {
            int patchedCount = 0, unpatchedCount = 0;
            for (int i = 0; i < entry.Sites.Count; i++)
            {
                var site = entry.Sites[i];
                byte[] siteReplaceBytes = site.AspectRatioReplace
                    ? BitConverter.GetBytes((float)context.ScreenWidth / context.ScreenHeight)
                    : site.FovDenominatorReplace
                        ? BitConverter.GetBytes(16.0f * context.ScreenHeight / context.ScreenWidth)
                        : site.ReplaceBytes ?? effectiveReplaceBytes;
                byte[] siteFindBytes = site.FindBytes ?? entry.FindBytes ?? [];

                if (site.Offset < 0 || site.Offset + siteFindBytes.Length > fileBytes.Length)
                    return new PatchResult(PatchResultStatus.Error, $"Site {i} offset 0x{site.Offset:X} is out of bounds.");

                var atOffset = fileBytes[(int)site.Offset..(int)(site.Offset + siteFindBytes.Length)];
                if (atOffset.SequenceEqual(siteReplaceBytes))
                    patchedCount++;
                else if (atOffset.SequenceEqual(siteFindBytes))
                    unpatchedCount++;
                else
                    return new PatchResult(PatchResultStatus.SignatureNotFound,
                        $"Site {i} at 0x{site.Offset:X}: bytes don't match original or patched state — game version may differ.");
            }

            if (patchedCount == entry.Sites.Count) return new PatchResult(PatchResultStatus.AlreadyPatched);
            if (unpatchedCount == entry.Sites.Count) return new PatchResult(PatchResultStatus.AlreadyUnpatched);
            // Partially applied — treat as unpatched so user can apply cleanly
            return new PatchResult(PatchResultStatus.AlreadyUnpatched);
        }

        // ── Patch-all / single mode ───────────────────────────────────────────
        if (entry.FindBytes is null) return new PatchResult(PatchResultStatus.Error, "Patch entry is missing findBytes.");

        bool foundPatched   = FindPattern(fileBytes, effectiveReplaceBytes) != -1;
        bool foundUnpatched = FindPattern(fileBytes, entry.FindBytes) != -1;

        if (foundPatched && !foundUnpatched) return new PatchResult(PatchResultStatus.AlreadyPatched);
        if (foundUnpatched) return new PatchResult(PatchResultStatus.AlreadyUnpatched);
        return new PatchResult(PatchResultStatus.SignatureNotFound, $"Byte pattern not found in '{entry.FilePath}'.");
    }

    /// <summary>Computes the patch-level replacement bytes from formula flags or static data.</summary>
    private static byte[] ComputeReplaceBytes(PatchEntry entry, PatchContext context) =>
        entry.AspectRatioReplace
            ? BitConverter.GetBytes((float)context.ScreenWidth / context.ScreenHeight)
            : entry.FovDenominatorReplace
                ? BitConverter.GetBytes(16.0f * context.ScreenHeight / context.ScreenWidth)
                : (entry.ReplaceBytes ?? []);

    // ── Core logic ────────────────────────────────────────────────────────────

    private PatchResult Patch(PatchContext context, PatchEntry entry, bool applyDirection)
    {
        // Sites mode only requires replaceBytes (each site has its own findBytes).
        // Other modes require both findBytes and replaceBytes.
        bool isSitesMode = entry.Sites is { Count: > 0 };
        if (!isSitesMode && (entry.FindBytes is null || entry.ReplaceBytes is null))
            return new PatchResult(PatchResultStatus.Error, "Patch entry is missing findBytes or replaceBytes.");

        if (string.IsNullOrWhiteSpace(entry.FilePath))
            return new PatchResult(PatchResultStatus.Error, "file-hex-edit patch is missing the 'filePath' field.");

        if (string.IsNullOrWhiteSpace(context.GameInstallPath))
            return new PatchResult(PatchResultStatus.Error,
                "Game install path could not be determined. Verify the game is installed via Steam.");

        var fullPath = Path.Combine(context.GameInstallPath, entry.FilePath);
        if (!File.Exists(fullPath))
            return new PatchResult(PatchResultStatus.Error, $"Target file not found: {fullPath}");

        // Compute the effective replace bytes — either from the profile directly,
        // or derived from the user's configured screen resolution.
        byte[] effectiveReplaceBytes = ComputeReplaceBytes(entry, context);

        // When applying we look for findBytes; when reverting we look for replaceBytes.
        // (Not used in sites mode, but kept for patchAll / single modes below.)
        byte[] searchPattern = applyDirection ? (entry.FindBytes ?? []) : effectiveReplaceBytes;
        byte[] writeBytes    = applyDirection ? effectiveReplaceBytes : (entry.FindBytes ?? []);
        var alreadyDone      = applyDirection ? PatchResultStatus.AlreadyPatched : PatchResultStatus.AlreadyUnpatched;

        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Failed to read '{entry.FilePath}': {ex.Message}");
        }

        // ── Multi-site mode: each site has an explicit offset + original bytes ─
        // This is the safe mode — revert always restores the exact original bytes at
        // the exact offset, regardless of what other patches have done elsewhere.
        if (entry.Sites is { Count: > 0 })
        {
            // Each site may declare its own formula flag; fall back to patch-level flags.
            // A site (or the patch) must have replaceBytes or a formula flag.
            bool patchHasReplacement = entry.AspectRatioReplace || entry.FovDenominatorReplace || entry.ReplaceBytes is not null;
            bool allSitesHaveOwnReplacement = entry.Sites.All(
                s => s.AspectRatioReplace || s.FovDenominatorReplace || s.ReplaceBytes is not null);
            if (!patchHasReplacement && !allSitesHaveOwnReplacement)
                return new PatchResult(PatchResultStatus.Error, "Multi-site patch is missing 'replaceBytes'.");

            int applied = 0, skipped = 0;
            for (int i = 0; i < entry.Sites.Count; i++)
            {
                var site = entry.Sites[i];

                // Compute this site's replacement bytes: site flags > site.ReplaceBytes > patch flags > static replaceBytes.
                byte[] siteEffectiveReplaceBytes =
                    site.AspectRatioReplace  ? BitConverter.GetBytes((float)context.ScreenWidth / context.ScreenHeight) :
                    site.FovDenominatorReplace ? BitConverter.GetBytes(16.0f * context.ScreenHeight / context.ScreenWidth) :
                    site.ReplaceBytes ?? effectiveReplaceBytes;

                byte[] siteFindBytes  = applyDirection ? (site.FindBytes ?? entry.FindBytes!) : siteEffectiveReplaceBytes;
                byte[] siteWriteBytes = applyDirection ? siteEffectiveReplaceBytes : (site.FindBytes ?? entry.FindBytes!);

                if (site.Offset < 0 || site.Offset + siteFindBytes.Length > fileBytes.Length)
                    return new PatchResult(PatchResultStatus.Error,
                        $"Site {i} offset 0x{site.Offset:X} is out of bounds in '{entry.FilePath}'.");

                var atOffset = fileBytes[(int)site.Offset..(int)(site.Offset + siteFindBytes.Length)];

                if (atOffset.SequenceEqual(siteWriteBytes))
                {
                    skipped++;
                    continue;
                }

                if (!atOffset.SequenceEqual(siteFindBytes))
                    return new PatchResult(PatchResultStatus.SignatureNotFound,
                        $"Site {i} at 0x{site.Offset:X}: expected {BitConverter.ToString(siteFindBytes)} but found {BitConverter.ToString(atOffset.ToArray())} — game version may differ.");

                Array.Copy(siteWriteBytes, 0, fileBytes, (int)site.Offset, siteWriteBytes.Length);
                applied++;
            }

            if (applied == 0)
                return new PatchResult(alreadyDone);

            try
            {
                File.WriteAllBytes(fullPath, fileBytes);
            }
            catch (Exception ex)
            {
                return new PatchResult(PatchResultStatus.Error, $"Failed to write to '{entry.FilePath}': {ex.Message}");
            }

            _logger.LogInformation(
                "{Action} file-hex-edit patch '{Name}' at {Count} site(s) in '{File}'.",
                applyDirection ? "Applied" : "Reverted", entry.Name, applied, entry.FilePath);

            return new PatchResult(applyDirection ? PatchResultStatus.Applied : PatchResultStatus.Reverted);
        }

        // ── Patch-all mode: replace every occurrence in the file ──────────────
        if (entry.PatchAll)
        {
            var targets = FindAllPatterns(fileBytes, searchPattern);
            if (targets.Count == 0)
            {
                if (FindPattern(fileBytes, writeBytes) != -1)
                    return new PatchResult(alreadyDone);

                return new PatchResult(PatchResultStatus.SignatureNotFound,
                    $"Byte pattern not found in '{entry.FilePath}' — game version may be unsupported.");
            }

            foreach (var off in targets)
                Array.Copy(writeBytes, 0, fileBytes, (int)off, writeBytes.Length);

            try
            {
                File.WriteAllBytes(fullPath, fileBytes);
            }
            catch (Exception ex)
            {
                return new PatchResult(PatchResultStatus.Error, $"Failed to write to '{entry.FilePath}': {ex.Message}");
            }

            _logger.LogInformation(
                "{Action} file-hex-edit patch '{Name}' at {Count} location(s) in '{File}'.",
                applyDirection ? "Applied" : "Reverted", entry.Name, targets.Count, entry.FilePath);

            return new PatchResult(applyDirection ? PatchResultStatus.Applied : PatchResultStatus.Reverted);
        }

        // ── Single-occurrence mode ─────────────────────────────────────────────
        long offset = entry.Offset;
        if (offset == -1)
        {
            // Search mode: scan for the first occurrence of searchPattern
            long found = FindPattern(fileBytes, searchPattern);
            if (found == -1)
            {
                // Maybe it's already in the target state — check for writeBytes
                long alreadyAt = FindPattern(fileBytes, writeBytes);
                if (alreadyAt != -1)
                    return new PatchResult(alreadyDone);

                return new PatchResult(PatchResultStatus.SignatureNotFound,
                    $"Byte pattern not found in '{entry.FilePath}' — game version may be unsupported or patch is already applied in a different way.");
            }
            offset = found;
        }
        else
        {
            // Fixed-offset mode: validate the bytes at the specified offset
            if (offset + searchPattern.Length > fileBytes.Length)
                return new PatchResult(PatchResultStatus.Error,
                    $"Offset 0x{offset:X} is out of bounds for file '{entry.FilePath}'.");

            var atOffset = fileBytes[(int)offset..(int)(offset + searchPattern.Length)];

            if (atOffset.SequenceEqual(writeBytes))
                return new PatchResult(alreadyDone);

            if (!atOffset.SequenceEqual(searchPattern))
                return new PatchResult(PatchResultStatus.SignatureNotFound,
                    $"Byte signature not found at offset 0x{offset:X} in '{entry.FilePath}' — game version may differ.");
        }

        // Write the patch
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.Write(writeBytes, 0, writeBytes.Length);
        }
        catch (Exception ex)
        {
            return new PatchResult(PatchResultStatus.Error, $"Failed to write to '{entry.FilePath}': {ex.Message}");
        }

        _logger.LogInformation(
            "{Action} file-hex-edit patch '{Name}' at 0x{Offset:X} in '{File}'.",
            applyDirection ? "Applied" : "Reverted", entry.Name, offset, entry.FilePath);

        return new PatchResult(applyDirection ? PatchResultStatus.Applied : PatchResultStatus.Reverted);
    }

    /// <summary>Returns the index of the first occurrence of <paramref name="pattern"/> in <paramref name="data"/>, or -1.</summary>
    private static long FindPattern(byte[] data, byte[] pattern)
    {
        int limit = data.Length - pattern.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>Returns the file offsets of every non-overlapping occurrence of <paramref name="pattern"/> in <paramref name="data"/>.</summary>
    private static List<long> FindAllPatterns(byte[] data, byte[] pattern)
    {
        var result = new List<long>();
        int limit = data.Length - pattern.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match)
            {
                result.Add(i);
                i += pattern.Length - 1; // skip past this match (no overlaps)
            }
        }
        return result;
    }
}
