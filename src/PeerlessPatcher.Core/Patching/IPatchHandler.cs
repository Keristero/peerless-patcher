using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

public enum PatchResultStatus
{
    Applied,
    Reverted,
    AlreadyPatched,
    AlreadyUnpatched,
    SignatureNotFound,
    Unsupported,
    Error,
}

public sealed record PatchResult(PatchResultStatus Status, string? ErrorMessage = null);

/// <summary>
/// Handles applying and reverting a specific patch type.
/// </summary>
public interface IPatchHandler
{
    /// <summary>The patch type string this handler supports (e.g. "hex-edit", "file-hex-edit").</summary>
    string HandledType { get; }

    PatchResult Apply(PatchContext context, PatchEntry entry);
    PatchResult Revert(PatchContext context, PatchEntry entry);
}
