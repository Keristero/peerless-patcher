using System.Text.Json.Serialization;

namespace PeerlessPatcher.Models;

/// <summary>
/// Top-level patch profile loaded from profiles/*.json.
/// </summary>
public sealed class PatchProfile
{
    [JsonPropertyName("gameId")]
    public string GameId { get; init; } = string.Empty;

    [JsonPropertyName("gameName")]
    public string GameName { get; init; } = string.Empty;

    [JsonPropertyName("steamAppId")]
    public int SteamAppId { get; init; }

    /// <summary>
    /// The process name to match (without .exe extension), e.g. "KINGDOM HEARTS III".
    /// </summary>
    [JsonPropertyName("processName")]
    public string ProcessName { get; init; } = string.Empty;

    /// <summary>
    /// The directory name under <c>steamapps/common/</c>, e.g. "KINGDOM HEARTS III".
    /// Used as a fallback when the Steam app manifest (appmanifest_&lt;id&gt;.acf) is missing.
    /// </summary>
    [JsonPropertyName("installDir")]
    public string? InstallDir { get; init; }

    [JsonPropertyName("patches")]
    public PatchEntry[] Patches { get; init; } = [];
}
