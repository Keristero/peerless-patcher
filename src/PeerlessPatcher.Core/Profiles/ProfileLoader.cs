using System.Text.Json;
using Microsoft.Extensions.Logging;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Profiles;

/// <summary>
/// Scans <c>profiles/*.json</c> at startup and loads valid <see cref="PatchProfile"/> entries.
/// Invalid or malformed files are logged and skipped.
/// </summary>
public sealed class ProfileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new ByteArrayNumberArrayConverter() },
    };

    private readonly ILogger<ProfileLoader> _logger;

    public ProfileLoader(ILogger<ProfileLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads all valid profiles from <paramref name="profilesDirectory"/>.
    /// Returns an empty list if the directory does not exist or contains no valid profiles.
    /// </summary>
    public IReadOnlyList<PatchProfile> LoadAll(string profilesDirectory)
    {
        if (!Directory.Exists(profilesDirectory))
        {
            _logger.LogWarning("Profiles directory not found: {Dir}", profilesDirectory);
            return [];
        }

        var results = new List<PatchProfile>();
        var files = Directory.GetFiles(profilesDirectory, "*.json");

        if (files.Length == 0)
        {
            _logger.LogWarning("No profile files found in {Dir}", profilesDirectory);
        }

        foreach (var file in files)
        {
            var profile = TryLoad(file);
            if (profile is not null)
                results.Add(profile);
        }

        return results;
    }

    private PatchProfile? TryLoad(string filePath)
    {
        PatchProfile? profile;
        try
        {
            var json = File.ReadAllText(filePath);
            profile = JsonSerializer.Deserialize<PatchProfile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError("Failed to parse profile '{File}': {Msg}", Path.GetFileName(filePath), ex.Message);
            return null;
        }

        if (profile is null)
        {
            _logger.LogError("Profile '{File}' deserialized to null, skipping.", Path.GetFileName(filePath));
            return null;
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(profile.GameId) ||
            string.IsNullOrWhiteSpace(profile.GameName) ||
            string.IsNullOrWhiteSpace(profile.ProcessName))
        {
            _logger.LogError(
                "Profile '{File}' is missing required fields (gameId, gameName, processName), skipping.",
                Path.GetFileName(filePath));
            return null;
        }

        // Validate individual patches
        var validPatches = new List<PatchEntry>();
        foreach (var patch in profile.Patches)
        {
            if (!ValidatePatchEntry(patch, Path.GetFileName(filePath)))
                continue;
            validPatches.Add(patch);
        }

        // Return a profile with only valid patches
        return new PatchProfile
        {
            GameId = profile.GameId,
            GameName = profile.GameName,
            SteamAppId = profile.SteamAppId,
            ProcessName = profile.ProcessName,
            InstallDir = profile.InstallDir,
            Patches = [.. validPatches],
        };
    }

    private bool ValidatePatchEntry(PatchEntry entry, string fileName)
    {
        if (string.IsNullOrWhiteSpace(entry.Type))
        {
            _logger.LogWarning("Patch '{Name}' in '{File}' has no type, skipping.", entry.Name, fileName);
            return false;
        }

        if (entry.Type == "hex-edit")
        {
            if (entry.FindBytes is null || entry.ReplaceBytes is null)
            {
                _logger.LogError(
                    "Hex-edit patch '{Name}' in '{File}' is missing findBytes or replaceBytes, skipping.",
                    entry.Name, fileName);
                return false;
            }

            if (entry.FindBytes.Length != entry.ReplaceBytes.Length)
            {
                _logger.LogError(
                    "Hex-edit patch '{Name}' in '{File}' has mismatched findBytes ({F}) and replaceBytes ({R}) lengths, skipping.",
                    entry.Name, fileName, entry.FindBytes.Length, entry.ReplaceBytes.Length);
                return false;
            }
        }

        if (entry.Type == "file-hex-edit")
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath))
            {
                _logger.LogError(
                    "File-hex-edit patch '{Name}' in '{File}' is missing the 'filePath' field, skipping.",
                    entry.Name, fileName);
                return false;
            }

            bool hasSites = entry.Sites is { Count: > 0 };

            if (hasSites)
            {
                // Sites mode: each site may have its own formula flag; otherwise falls back to patch-level.
                // At least one of: patch replaceBytes, patch formula flag, or per-site formula flags must cover all sites.
                bool patchHasReplacement = entry.AspectRatioReplace || entry.FovDenominatorReplace || entry.ReplaceBytes is not null;
                bool allSitesHaveOwnReplacement = entry.Sites!.All(s => s.AspectRatioReplace || s.FovDenominatorReplace || s.ReplaceBytes is not null);
                if (!patchHasReplacement && !allSitesHaveOwnReplacement)
                {
                    _logger.LogError(
                        "File-hex-edit patch '{Name}' in '{File}' uses sites mode but is missing replaceBytes, skipping.",
                        entry.Name, fileName);
                    return false;
                }

                for (int i = 0; i < entry.Sites!.Count; i++)
                {
                    var site = entry.Sites[i];
                    var siteFindBytes = site.FindBytes ?? entry.FindBytes;
                    if (siteFindBytes is null)
                    {
                        _logger.LogError(
                            "File-hex-edit patch '{Name}' in '{File}' site {I} has no findBytes and no patch-level findBytes fallback, skipping.",
                            entry.Name, fileName, i);
                        return false;
                    }

                    // For runtime-computed replacements the bytes are always 4 (float).
                    bool siteRuntimeReplace = site.AspectRatioReplace || site.FovDenominatorReplace
                        || entry.AspectRatioReplace || entry.FovDenominatorReplace;
                    int expectedReplaceLen = siteRuntimeReplace ? 4
                        : (site.ReplaceBytes?.Length ?? entry.ReplaceBytes!.Length);

                    if (siteFindBytes.Length != expectedReplaceLen)
                    {
                        _logger.LogError(
                            "File-hex-edit patch '{Name}' in '{File}' site {I} findBytes length ({F}) does not match replaceBytes length ({R}), skipping.",
                            entry.Name, fileName, i, siteFindBytes.Length, expectedReplaceLen);
                        return false;
                    }
                }
            }
            else
            {
                if (entry.FindBytes is null || entry.ReplaceBytes is null)
                {
                    _logger.LogError(
                        "File-hex-edit patch '{Name}' in '{File}' is missing findBytes or replaceBytes, skipping.",
                        entry.Name, fileName);
                    return false;
                }

                if (entry.FindBytes.Length != entry.ReplaceBytes.Length)
                {
                    _logger.LogError(
                        "File-hex-edit patch '{Name}' in '{File}' has mismatched findBytes ({F}) and replaceBytes ({R}) lengths, skipping.",
                        entry.Name, fileName, entry.FindBytes.Length, entry.ReplaceBytes.Length);
                    return false;
                }
            }
        }

        if (entry.Type == "file-replace")
        {
            if (string.IsNullOrWhiteSpace(entry.TargetPath))
            {
                _logger.LogError(
                    "File-replace patch '{Name}' in '{File}' is missing 'targetPath', skipping.",
                    entry.Name, fileName);
                return false;
            }

            if (string.IsNullOrWhiteSpace(entry.SourcePath))
            {
                _logger.LogError(
                    "File-replace patch '{Name}' in '{File}' is missing 'sourcePath', skipping.",
                    entry.Name, fileName);
                return false;
            }
        }

        return true;
    }
}
