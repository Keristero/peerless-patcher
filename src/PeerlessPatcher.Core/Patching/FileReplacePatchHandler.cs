using Microsoft.Extensions.Logging;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Handles "file-replace" patches: backs up the original file to &lt;target&gt;.sgp.bak,
/// copies a bundled replacement asset over it, and restores on revert.
/// </summary>
public sealed class FileReplacePatchHandler : IPatchHandler
{
    private const string BackupSuffix = ".sgp.bak";

    private readonly string _profilesDir;
    private readonly ILogger<FileReplacePatchHandler> _logger;

    public FileReplacePatchHandler(string profilesDir, ILogger<FileReplacePatchHandler> logger)
    {
        _profilesDir = profilesDir;
        _logger = logger;
    }

    public string HandledType => "file-replace";

    public PatchResult Apply(PatchContext context, PatchEntry entry) => Patch(context, entry, apply: true);
    public PatchResult Revert(PatchContext context, PatchEntry entry) => Patch(context, entry, apply: false);
    public PatchResult Probe(PatchContext context, PatchEntry entry) =>
        new PatchResult(PatchResultStatus.Unsupported);

    private PatchResult Patch(PatchContext context, PatchEntry entry, bool apply)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPath))
            return new PatchResult(PatchResultStatus.Error, "file-replace patch is missing 'targetPath'.");

        if (apply && string.IsNullOrWhiteSpace(entry.SourcePath))
            return new PatchResult(PatchResultStatus.Error, "file-replace patch is missing 'sourcePath'.");

        if (string.IsNullOrWhiteSpace(context.GameInstallPath))
            return new PatchResult(PatchResultStatus.Error,
                "Game install path could not be determined. Verify the game is installed via Steam.");

        var targetFull = Path.Combine(context.GameInstallPath, entry.TargetPath);
        var backupFull = targetFull + BackupSuffix;

        if (apply)
        {
            var sourceFull = Path.Combine(_profilesDir, entry.SourcePath!);

            if (!File.Exists(sourceFull))
                return new PatchResult(PatchResultStatus.Error,
                    $"Replacement asset not found: {sourceFull}");

            if (!File.Exists(targetFull))
                return new PatchResult(PatchResultStatus.Error,
                    $"Target file not found: {targetFull}");

            if (File.Exists(backupFull))
                return new PatchResult(PatchResultStatus.AlreadyPatched);

            try
            {
                File.Copy(targetFull, backupFull, overwrite: false);
                File.Copy(sourceFull, targetFull, overwrite: true);
            }
            catch (Exception ex)
            {
                // Try to clean up partial backup
                if (File.Exists(backupFull) && !File.Exists(targetFull))
                    File.Move(backupFull, targetFull);
                return new PatchResult(PatchResultStatus.Error,
                    $"Failed to apply file-replace patch: {ex.Message}");
            }

            _logger.LogInformation(
                "Applied file-replace patch '{Name}': replaced '{Target}' (backup at '{Backup}').",
                entry.Name, entry.TargetPath, backupFull);

            return new PatchResult(PatchResultStatus.Applied);
        }
        else
        {
            if (!File.Exists(backupFull))
                return new PatchResult(PatchResultStatus.AlreadyUnpatched);

            try
            {
                File.Copy(backupFull, targetFull, overwrite: true);
                File.Delete(backupFull);
            }
            catch (Exception ex)
            {
                return new PatchResult(PatchResultStatus.Error,
                    $"Failed to revert file-replace patch: {ex.Message}");
            }

            _logger.LogInformation(
                "Reverted file-replace patch '{Name}': restored '{Target}' from backup.",
                entry.Name, entry.TargetPath);

            return new PatchResult(PatchResultStatus.Reverted);
        }
    }
}
