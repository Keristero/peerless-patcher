using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Detection;

/// <summary>
/// Polls running processes and fires events when a supported game starts or exits.
/// Only one game profile is active at a time.
/// </summary>
public sealed class GameDetector : IDisposable
{
    private readonly IReadOnlyList<PatchProfile> _profiles;
    private readonly ILogger<GameDetector> _logger;
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private PatchProfile? _activeProfile;
    private int _activeProcessId;

    public event EventHandler<GameDetectedEventArgs>? GameDetected;
    public event EventHandler<GameExitedEventArgs>? GameExited;

    public GameDetector(IReadOnlyList<PatchProfile> profiles, ILogger<GameDetector> logger, TimeSpan? pollInterval = null)
    {
        _profiles = profiles;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public PatchProfile? ActiveProfile => _activeProfile;

    /// <summary>Starts the background polling loop.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _pollingTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    /// <summary>Stops the background polling loop.</summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during game detection poll");
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Poll()
    {
        // Check if the active process is still alive
        if (_activeProfile is not null)
        {
            bool stillRunning = IsProcessRunning(_activeProcessId, _activeProfile.ProcessName);
            if (!stillRunning)
            {
                var exited = _activeProfile;
                _activeProfile = null;
                _activeProcessId = 0;
                _logger.LogInformation("Game exited: {Game}", exited.GameName);
                GameExited?.Invoke(this, new GameExitedEventArgs(exited));
            }
            return; // Don't search for new games while one is already active
        }

        // Search for a matching process
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate processes");
            return;
        }

        PatchProfile? firstMatch = null;
        int firstMatchPid = 0;
        bool warnedMultiple = false;

        foreach (var proc in all)
        {
            string procName;
            try { procName = proc.ProcessName; }
            catch { continue; }

            foreach (var profile in _profiles)
            {
                if (!MatchesProfile(proc, procName, profile))
                    continue;

                if (firstMatch is null)
                {
                    firstMatch = profile;
                    firstMatchPid = proc.Id;
                }
                else if (!warnedMultiple)
                {
                    // Same profile matching multiple processes is normal for Proton/Wine
                    // (game + overlay + child processes). Only warn for distinct profiles.
                    if (!string.Equals(firstMatch.GameId, profile.GameId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Multiple supported games detected simultaneously. Activating '{First}', ignoring '{Other}'.",
                            firstMatch.GameName, profile.GameName);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Multiple processes matched '{Game}' — using PID {Pid}.",
                            firstMatch.GameName, firstMatchPid);
                    }
                    warnedMultiple = true;
                }
            }
        }

        // Dispose all process objects
        foreach (var proc in all) proc.Dispose();

        if (firstMatch is not null)
        {
            _activeProfile = firstMatch;
            _activeProcessId = firstMatchPid;
            _logger.LogInformation("Game detected: {Game} (PID {Pid})", firstMatch.GameName, firstMatchPid);
            GameDetected?.Invoke(this, new GameDetectedEventArgs(firstMatch, firstMatchPid));
        }
    }

    private static bool IsProcessRunning(int pid, string expectedName)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            bool match = MatchesProfile(proc, proc.ProcessName, new PatchProfile { ProcessName = expectedName });
            proc.Dispose();
            return match;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if <paramref name="proc"/> matches <paramref name="profile"/>.
    /// Handles two Linux/Proton quirks:
    /// 1. Wine/Proton games run as wine64-preloader with the exe in the cmdline.
    ///    We read /proc/pid/cmdline and check if any argument contains the profile
    ///    process name (case-insensitive, .exe optional).
    /// 2. The kernel truncates process names to 15 chars in /proc/pid/status — so
    ///    "KINGDOM HEARTS III" (18 chars) appears as "KINGDOM HEARTS I". We use this
    ///    as a last resort only when cmdline cannot be read, to avoid false positives
    ///    when two games share the same 15-char prefix (e.g. "KINGDOM HEARTS II FINAL MIX"
    ///    and "KINGDOM HEARTS III" both truncate to "KINGDOM HEARTS ").
    /// 3. Newer Proton builds (wine64-preloader) blank both the process name and
    ///    cmdline entirely. In this case we fall back to scanning /proc/pid/maps for
    ///    a mapping whose path contains the profile's process name.
    /// </summary>
    private static bool MatchesProfile(Process proc, string procName, PatchProfile profile)
    {
        // Direct match (Windows / short names)
        if (string.Equals(procName, profile.ProcessName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (OperatingSystem.IsLinux())
        {
            // Linux/Proton: wine64-preloader stores the full exe path in cmdline.
            // Check this first — it gives the untruncated name and is authoritative.
            // If cmdline is readable and non-empty, return definitively (true or false)
            // without falling through to the imprecise checks below.
            try
            {
                var cmdline = File.ReadAllText($"/proc/{proc.Id}/cmdline")
                    .Replace('\0', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(cmdline))
                {
                    return cmdline.Contains(profile.ProcessName, StringComparison.OrdinalIgnoreCase) ||
                           cmdline.Contains(profile.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* /proc not readable — fall through */ }

            // Newer Proton builds (wine64-preloader) blank both name and cmdline.
            // Fall back to /proc/pid/maps: if the profile's exe is mapped in this
            // process's address space it must be running that game.
            try
            {
                var mapsExeName = profile.ProcessName + ".exe";
                foreach (var line in File.ReadLines($"/proc/{proc.Id}/maps"))
                {
                    // Each line ends with the file path (or nothing for anonymous)
                    var slash = line.LastIndexOf('/');
                    if (slash < 0) continue;
                    var mappedFile = line[(slash + 1)..];
                    if (mappedFile.Equals(mapsExeName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* /proc/pid/maps not readable */ }

            // Last resort: kernel truncates to 15 chars. Match if the profile name
            // starts with the (truncated) proc name. Only reached when cmdline is unreadable.
            // Guard: procName must be non-empty — a blank name matches every StartsWith.
            if (!string.IsNullOrEmpty(procName) &&
                profile.ProcessName.Length > 15 &&
                profile.ProcessName.StartsWith(procName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

public sealed class GameDetectedEventArgs(PatchProfile profile, int processId) : EventArgs
{
    public PatchProfile Profile { get; } = profile;
    public int ProcessId { get; } = processId;
}

public sealed class GameExitedEventArgs(PatchProfile profile) : EventArgs
{
    public PatchProfile Profile { get; } = profile;
}
