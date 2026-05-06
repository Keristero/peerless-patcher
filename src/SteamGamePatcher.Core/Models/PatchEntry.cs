using System.Text.Json.Serialization;

namespace SteamGamePatcher.Models;

/// <summary>
/// One explicitly-addressed site within a multi-site patch.
/// Used by <see cref="PatchEntry.Sites"/> to anchor each edit to an exact file offset,
/// ensuring revert always restores the correct original bytes regardless of patch order.
/// </summary>
public sealed class PatchSite
{
    /// <summary>Exact byte offset from the start of the file.</summary>
    [JsonPropertyName("offset")]
    public long Offset { get; init; }

    /// <summary>Original bytes expected at <see cref="Offset"/> (used to apply and revert).</summary>
    [JsonPropertyName("findBytes")]
    public byte[]? FindBytes { get; init; }

    /// <summary>Human-readable note describing what this site controls (ignored at runtime).</summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// A single patchable entry within a game profile.
/// The <see cref="Type"/> field determines which handler processes this entry.
/// </summary>
public sealed class PatchEntry
{
    // ── Common fields ──────────────────────────────────────────────────────────

    /// <summary>Patch type: "hex-edit" or "file-replace".</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Human-readable patch name shown in the overlay.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Short description shown in the overlay.</summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    // ── hex-edit fields ────────────────────────────────────────────────────────

    /// <summary>Byte offset from the start of the process image.</summary>
    [JsonPropertyName("offset")]
    public long Offset { get; init; }

    /// <summary>Original bytes at the offset (used to validate and revert).</summary>
    [JsonPropertyName("findBytes")]
    public byte[]? FindBytes { get; init; }

    /// <summary>Replacement bytes to write when the patch is applied.</summary>
    [JsonPropertyName("replaceBytes")]
    public byte[]? ReplaceBytes { get; init; }

    // ── file-hex-edit fields ───────────────────────────────────────────────────

    /// <summary>
    /// Path to the file to hex-edit, relative to the game's Steam installation directory.
    /// Used only for type "file-hex-edit".
    /// Example: "KINGDOM HEARTS III.exe"
    /// </summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    /// <summary>
    /// When <c>true</c>, every occurrence of <see cref="FindBytes"/> in the file is patched
    /// rather than just the first. Useful when the same constant appears at multiple sites
    /// that all need to change (e.g. a hardcoded aspect ratio stored in both code and data).
    /// </summary>
    [JsonPropertyName("patchAll")]
    public bool PatchAll { get; init; }

    /// <summary>
    /// Explicitly-addressed sites for a multi-site patch. Each site has its own
    /// <see cref="PatchSite.Offset"/> and <see cref="PatchSite.FindBytes"/>; all share
    /// the patch-level <see cref="ReplaceBytes"/>. When this is non-null, <see cref="PatchAll"/>
    /// and <see cref="Offset"/> are ignored.
    /// </summary>
    [JsonPropertyName("sites")]
    public List<PatchSite>? Sites { get; init; }

    /// <summary>
    /// When <c>true</c>, <see cref="ReplaceBytes"/> is computed at runtime from the user's
    /// configured screen resolution (width ÷ height encoded as a little-endian IEEE 754 float).
    /// <see cref="ReplaceBytes"/> must be omitted from the profile when this flag is set.
    /// </summary>
    [JsonPropertyName("aspectRatioReplace")]
    public bool AspectRatioReplace { get; init; }

    /// <summary>
    /// When <c>true</c>, <see cref="ReplaceBytes"/> is computed at runtime as
    /// <c>16.0f × screenHeight / screenWidth</c> — the height denominator of a "16:N" aspect
    /// ratio scaled to the user's screen.  Use this to patch the "9" in a hardcoded 16:9
    /// FOV/viewport parameter so it becomes the correct value for any ultrawide resolution.
    /// <see cref="ReplaceBytes"/> must be omitted from the profile when this flag is set.
    /// </summary>
    [JsonPropertyName("fovDenominatorReplace")]
    public bool FovDenominatorReplace { get; init; }

    // ── file-replace fields ────────────────────────────────────────────────────

    /// <summary>
    /// Path to the replacement file, relative to the profile's asset directory.
    /// Used only for type "file-replace".
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    /// <summary>
    /// Target path relative to the game's Steam installation directory.
    /// Used only for type "file-replace".
    /// </summary>
    [JsonPropertyName("targetPath")]
    public string? TargetPath { get; init; }
}
