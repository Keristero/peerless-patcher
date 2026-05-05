using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace PeerlessPatcher.Patching;

/// <summary>
/// Locates Steam game installation directories by reading Steam's library configuration.
/// Supports Windows (registry + VDF) and Linux (~/.steam path convention).
/// </summary>
public static class SteamLocator
{
    /// <summary>
    /// Returns the absolute path to the installed game directory for the given <paramref name="steamAppId"/>,
    /// or <c>null</c> if it cannot be found.
    /// </summary>
    /// <param name="steamAppId">The Steam application ID.</param>
    /// <param name="installDir">
    /// Optional fallback: the directory name under <c>steamapps/common/</c> (e.g. "KINGDOM HEARTS III").
    /// Used when the app manifest file is missing but the game files are still present.
    /// </param>
    public static string? FindGameInstallPath(int steamAppId, string? installDir = null)
    {
        var libraryPaths = GetLibraryFolders();

        foreach (var lib in libraryPaths)
        {
            // Primary: read install dir from the app manifest
            var manifestPath = Path.Combine(lib, "steamapps", $"appmanifest_{steamAppId}.acf");
            if (File.Exists(manifestPath))
            {
                var dirFromManifest = ReadInstallDirFromManifest(manifestPath);
                if (dirFromManifest is not null)
                {
                    var fullPath = Path.Combine(lib, "steamapps", "common", dirFromManifest);
                    if (Directory.Exists(fullPath))
                        return fullPath;
                }
            }

            // Fallback: if the manifest is missing but we know the install dir name,
            // check whether the game directory itself is present (e.g. manifest was lost).
            if (installDir is not null)
            {
                var fallbackPath = Path.Combine(lib, "steamapps", "common", installDir);
                if (Directory.Exists(fallbackPath))
                    return fallbackPath;
            }
        }

        return null;
    }

    // ── Library folder discovery ──────────────────────────────────────────────

    private static IEnumerable<string> GetLibraryFolders()
    {
        var roots = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            roots.AddRange(GetWindowsSteamRoots());
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            roots.AddRange(GetLinuxSteamRoots());

        // Expand each root by reading its libraryfolders.vdf
        var all = new List<string>(roots);
        foreach (var root in roots)
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                all.AddRange(ReadLibraryFoldersVdf(vdf));
        }

        return all.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetWindowsSteamRoots()
    {
        // Primary install location from registry
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\WOW6432Node\Valve\Steam") ??
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");

        if (key?.GetValue("InstallPath") is string path && !string.IsNullOrEmpty(path))
            yield return path;

        // Common fallback
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFiles, "Steam");
    }

    [SupportedOSPlatform("linux")]
    private static IEnumerable<string> GetLinuxSteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Standard Steam on Linux locations
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");

        // Bazzite/Fedora Silverblue: /home is a symlink to /var/home, but
        // Environment.SpecialFolder.UserProfile may return /home/... which
        // resolves correctly, but include the /var/home variant explicitly
        // in case of bind-mount scenarios where paths differ.
        if (home.StartsWith("/home/"))
        {
            var varHome = "/var" + home;
            yield return Path.Combine(varHome, ".steam", "steam");
            yield return Path.Combine(varHome, ".local", "share", "Steam");
        }

        // Flatpak Steam
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
    }

    // ── VDF parsing (minimal — extracts "path" entries from libraryfolders.vdf) ──

    // Matches:  "path"   "/some/path"
    private static readonly Regex PathEntry = new(@"""path""\s+""([^""]+)""", RegexOptions.Compiled);

    private static IEnumerable<string> ReadLibraryFoldersVdf(string vdfPath)
    {
        List<string> results = [];
        try
        {
            var text = File.ReadAllText(vdfPath);
            foreach (Match m in PathEntry.Matches(text))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (!string.IsNullOrEmpty(p))
                    results.Add(p);
            }
        }
        catch
        {
            // Non-critical: silently skip unreadable VDF files
        }
        return results;
    }

    // ── ACF manifest parsing ──────────────────────────────────────────────────

    // Matches:  "installdir"   "KINGDOM HEARTS III"
    private static readonly Regex InstallDirEntry = new(@"""installdir""\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? ReadInstallDirFromManifest(string acfPath)
    {
        try
        {
            var text = File.ReadAllText(acfPath);
            var m = InstallDirEntry.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Process-based discovery ───────────────────────────────────────────────

    /// <summary>
    /// Attempts to derive the game's install directory from the running process.
    ///
    /// <para>Strategies (tried in order):</para>
    /// <list type="number">
    ///   <item>Read <c>STEAM_COMPAT_INSTALL_PATH</c> from the process environment (Proton always sets this).</item>
    ///   <item>Read <c>/proc/&lt;pid&gt;/exe</c> for native Linux executables.</item>
    ///   <item>Scan <c>/proc/&lt;pid&gt;/cmdline</c> for a <c>.exe</c> argument, converting Wine's
    ///     <c>Z:\path</c> Windows-style paths to Linux paths.</item>
    ///   <item>On Windows: <c>Process.MainModule.FileName</c>.</item>
    /// </list>
    /// After obtaining an exe path, the function walks up to <c>steamapps/common/&lt;dir&gt;</c>.
    /// </summary>
    public static string? FindFromProcess(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Strategy 1: process environment variable set by Proton/Steam
            var fromEnv = TryGetInstallPathFromEnviron(pid);
            if (fromEnv is not null) return fromEnv;

            // Strategies 2 + 3: derive from exe path then walk up
            var exePath = GetExePathLinux(pid);
            if (exePath is not null)
                return WalkToInstallRoot(exePath);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var exePath = GetExePathWindows(pid);
            if (exePath is not null)
                return WalkToInstallRoot(exePath);
        }

        return null;
    }

    /// <summary>
    /// Reads <c>/proc/&lt;pid&gt;/environ</c> and returns the value of
    /// <c>STEAM_COMPAT_INSTALL_PATH</c> if present and the directory exists.
    /// Proton sets this to the game's install directory before launching the game.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static string? TryGetInstallPathFromEnviron(int pid)
    {
        try
        {
            var environ = File.ReadAllText($"/proc/{pid}/environ");
            const string key = "STEAM_COMPAT_INSTALL_PATH=";
            foreach (var kv in environ.Split('\0'))
            {
                if (!kv.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                var path = kv[key.Length..];
                if (Directory.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Tries to get the game executable path from <c>/proc/&lt;pid&gt;/exe</c> or cmdline.
    /// Handles Wine/Proton's Windows-style <c>Z:\path\to\game.exe</c> paths by converting
    /// the <c>Z:</c> drive letter to the Linux filesystem root.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static string? GetExePathLinux(int pid)
    {
        // Try /proc/<pid>/exe first (works for native executables; wine points to wine binary).
        try
        {
            var exeLink = $"/proc/{pid}/exe";
            if (File.Exists(exeLink))
            {
                var resolved = new FileInfo(exeLink).LinkTarget;
                if (resolved is not null && !resolved.Contains("wine", StringComparison.OrdinalIgnoreCase)
                                        && !resolved.Contains("proton", StringComparison.OrdinalIgnoreCase)
                                        && !resolved.Contains("steam-runtime", StringComparison.OrdinalIgnoreCase))
                    return resolved;
            }
        }
        catch { }

        // For Wine/Proton, scan cmdline for the .exe argument.
        // The path is typically a Windows-style Z:\...\game.exe where Z: maps to / in Wine.
        try
        {
            var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").TrimEnd('\0');
            var args = cmdline.Split('\0');
            foreach (var arg in args)
            {
                if (!arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                // Native Linux path (unlikely for .exe but handle it)
                if (File.Exists(arg)) return arg;

                // Wine maps Z: to the Linux root — convert Z:\path\to\game.exe → /path/to/game.exe
                if (arg.Length > 2 && arg[1] == ':')
                {
                    var linuxPath = "/" + arg[2..].TrimStart('\\', '/').Replace('\\', '/');
                    if (File.Exists(linuxPath)) return linuxPath;
                }
            }
        }
        catch { }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetExePathWindows(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            return proc.MainModule?.FileName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Walks up from <paramref name="exePath"/>'s directory until it finds the directory
    /// whose parent is <c>steamapps/common</c> — that is the install root.
    /// The <c>steamapps/common</c> check takes priority over name matching to avoid
    /// stopping at an inner subdirectory that happens to share the same name as the install dir.
    /// </summary>
    private static string? WalkToInstallRoot(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath);
        while (dir is not null && dir != Path.GetPathRoot(dir))
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent is null) break;

            // The directory whose parent folder is "steamapps/common" is the install root.
            // Check this BEFORE any name-based match: the install dir name can repeat inside
            // itself (e.g. KINGDOM HEARTS III/KINGDOM HEARTS III/...).
            var grandparent = Path.GetDirectoryName(parent);
            if (string.Equals(Path.GetFileName(parent), "common", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(grandparent), "steamapps", StringComparison.OrdinalIgnoreCase))
                return dir;

            dir = parent;
        }
        return null;
    }
}
