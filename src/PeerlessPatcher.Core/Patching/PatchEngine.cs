using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Dispatches patch Apply/Revert operations to the correct <see cref="IPatchHandler"/>
/// based on the <see cref="PatchEntry.Type"/> field.
/// Manages a <see cref="PatchContext"/> (process memory + install path) per game session.
/// </summary>
public sealed class PatchEngine : IDisposable
{
    private readonly Dictionary<string, IPatchHandler> _handlers;
    private readonly ILogger<PatchEngine> _logger;
    private PatchContext? _context;

    /// <summary>Screen width used to compute aspect-ratio replacement bytes.</summary>
    public int ScreenWidth { get; set; } = 3440;

    /// <summary>Screen height used to compute aspect-ratio replacement bytes.</summary>
    public int ScreenHeight { get; set; } = 1440;

    public PatchEngine(IEnumerable<IPatchHandler> handlers, ILogger<PatchEngine> logger)
    {
        _handlers = handlers.ToDictionary(h => h.HandledType, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// Attaches to the running game process and resolves the game's install path.
    /// Must be called before Apply/Revert.
    /// </summary>
    public void Attach(int processId, PatchProfile profile)
    {
        Detach();

        IProcessMemory? memory = null;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                memory = new WindowsProcessMemory(processId);
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                memory = new LinuxProcessMemory(processId);
            else
                _logger.LogWarning("Process memory patching is not supported on this platform.");
        }
        catch (Exception ex)
        {
            // Non-fatal — file-hex-edit patches don't need process memory
            _logger.LogWarning("Could not open process {Pid} for memory access: {Msg}", processId, ex.Message);
        }

        var installPath = SteamLocator.FindGameInstallPath(profile.SteamAppId, profile.InstallDir);
        if (installPath is null)
            _logger.LogWarning("Could not locate Steam install directory for AppID {AppId}.", profile.SteamAppId);
        else
            _logger.LogInformation("Game install path: {Path}", installPath);

        _context = new PatchContext
        {
            ProcessMemory = memory,
            GameInstallPath = installPath,
            ProcessId = processId,
            ScreenWidth = ScreenWidth,
            ScreenHeight = ScreenHeight,
        };
    }

    /// <summary>
    /// Resolves the game's install path without a running process.
    /// Use this to apply file-based patches before or after launching the game.
    /// </summary>
    public void AttachInstallOnly(PatchProfile profile)
    {
        Detach();

        var installPath = SteamLocator.FindGameInstallPath(profile.SteamAppId, profile.InstallDir);
        if (installPath is null)
            _logger.LogWarning("Could not locate Steam install directory for AppID {AppId}.", profile.SteamAppId);
        else
            _logger.LogInformation("Game install path: {Path}", installPath);

        _context = new PatchContext
        {
            ProcessMemory = null,
            GameInstallPath = installPath,
            ProcessId = 0,
            ScreenWidth = ScreenWidth,
            ScreenHeight = ScreenHeight,
        };
    }

    /// <summary>Returns true if the current context has a resolved game install path.</summary>
    public bool HasInstallPath =>
        _context is not null && !string.IsNullOrEmpty(_context.GameInstallPath);

    /// <summary>
    /// Directly sets the game install path without re-attaching to a process.
    /// Use when the user manually overrides the path that Steam auto-detection returned.
    /// </summary>
    public void SetInstallPath(string path)
    {
        if (_context is not null)
            _context = new PatchContext
            {
                ProcessMemory = _context.ProcessMemory,
                GameInstallPath = path,
                ProcessId = _context.ProcessId,
                ScreenWidth = _context.ScreenWidth,
                ScreenHeight = _context.ScreenHeight,
            };
        else
            _context = new PatchContext { GameInstallPath = path, ScreenWidth = ScreenWidth, ScreenHeight = ScreenHeight };

        _logger.LogInformation("Install path manually set to: {Path}", path);
    }

    /// <summary>Detaches from the current game process and disposes the context.</summary>
    public void Detach()
    {
        _context?.Dispose();
        _context = null;
    }

    public PatchResult Apply(PatchEntry entry) => Dispatch(entry, isApply: true);
    public PatchResult Revert(PatchEntry entry) => Dispatch(entry, isApply: false);

    /// <summary>
    /// Checks whether the patch is currently applied without modifying anything.
    /// Requires the engine to be attached (install path resolved).
    /// </summary>
    public PatchResult Probe(PatchEntry entry)
    {
        if (_context is null)
            return new PatchResult(PatchResultStatus.Error, "Not attached to any game process.");

        if (!_handlers.TryGetValue(entry.Type, out var handler))
            return new PatchResult(PatchResultStatus.Unsupported, $"Unknown patch type: {entry.Type}");

        return handler.Probe(_context, entry);
    }

    private PatchResult Dispatch(PatchEntry entry, bool isApply)
    {
        if (_context is null)
            return new PatchResult(PatchResultStatus.Error, "Not attached to any game process.");

        if (!_handlers.TryGetValue(entry.Type, out var handler))
        {
            _logger.LogWarning("Unknown patch type '{Type}' for patch '{Name}', skipping.", entry.Type, entry.Name);
            return new PatchResult(PatchResultStatus.Unsupported, $"Unknown patch type: {entry.Type}");
        }

        return isApply ? handler.Apply(_context, entry) : handler.Revert(_context, entry);
    }

    /// <summary>Reverts all currently-active patches. Safe to call on exit.</summary>
    public void RevertAll(IEnumerable<(PatchEntry Entry, bool IsActive)> patchStates)
    {
        if (_context is null) return;

        foreach (var (entry, isActive) in patchStates)
        {
            if (!isActive) continue;
            try
            {
                var result = Revert(entry);
                if (result.Status is PatchResultStatus.Error)
                    _logger.LogWarning("Failed to revert patch '{Name}' on exit: {Msg}", entry.Name, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception reverting patch '{Name}' on exit.", entry.Name);
            }
        }
    }

    public void Dispose() => Detach();
}
