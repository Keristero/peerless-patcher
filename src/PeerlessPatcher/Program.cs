using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PeerlessPatcher.Detection;
using PeerlessPatcher.Models;
using PeerlessPatcher.Patching;
using PeerlessPatcher.Profiles;
using PeerlessPatcher.UI;

namespace PeerlessPatcher;

internal static class Program
{
    private static void Main()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        var log = loggerFactory.CreateLogger("Program");

        // ── Load profiles ──────────────────────────────────────────────────────
        var profilesDir = Path.Combine(AppContext.BaseDirectory, "profiles");
        var loader = new ProfileLoader(loggerFactory.CreateLogger<ProfileLoader>());
        var profiles = loader.LoadAll(profilesDir);

        if (profiles.Count == 0)
            log.LogWarning("No valid profiles found in {Dir}. Add a profile JSON to get started.", profilesDir);

        // ── Load settings ──────────────────────────────────────────────────────
        // Persisted in ~/.config/PeerlessPatcher/settings.yaml
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PeerlessPatcher");
        Directory.CreateDirectory(configDir);
        var settingsFile = Path.Combine(configDir, "settings.yaml");

        // Migrate from old overrides.json if it exists
        var legacyJson = Path.Combine(configDir, "overrides.json");
        var settings = File.Exists(settingsFile)
            ? LoadSettings(settingsFile, log)
            : MigrateFromJson(legacyJson, settingsFile, log);

        var pathOverrides = settings.GamePaths;

        // ── Patch engine ───────────────────────────────────────────────────────
        var patchEngine = new PatchEngine(
            [
                new HexEditPatchHandler(),
                new FileHexEditPatchHandler(loggerFactory.CreateLogger<FileHexEditPatchHandler>()),
                new FileReplacePatchHandler(profilesDir, loggerFactory.CreateLogger<FileReplacePatchHandler>()),
            ],
            loggerFactory.CreateLogger<PatchEngine>());

        // ── Overlay window ─────────────────────────────────────────────────────
        var overlay = new OverlayWindow(patchEngine, profiles);
        overlay.SetResolution(settings.ScreenWidth, settings.ScreenHeight);

        // Propagate saved resolution into the patch engine immediately.
        patchEngine.ScreenWidth  = settings.ScreenWidth;
        patchEngine.ScreenHeight = settings.ScreenHeight;

        // ── Pre-game setup: find installed profiles and load them immediately ──
        // File-based patches (file-hex-edit) don't need the game to be running.
        // We probe Steam for each profile's install path and show it right away
        // so the user can apply patches before launching the game.
        PatchProfile? firstInstalled = null;
        foreach (var profile in profiles)
        {
            // Use user-saved override path first, then Steam auto-detection.
            string? installPath = null;
            if (pathOverrides.TryGetValue(profile.GameId, out var manualPath) &&
                Directory.Exists(manualPath))
            {
                installPath = manualPath;
                log.LogInformation("Using manual path override for {Name}: {Path}", profile.GameName, installPath);
            }
            else
            {
                installPath = SteamLocator.FindGameInstallPath(profile.SteamAppId, profile.InstallDir);
            }

            // Always register the profile so it appears in the Patches tab immediately,
            // even before the install path is known. If no path is found yet the user can
            // set one manually via the Paths tab; the auto-detect from a running process
            // will also fill it in later.
            overlay.OnProfileLoaded(profile);

            if (installPath is not null)
            {
                if (firstInstalled is null)
                {
                    firstInstalled = profile;
                    patchEngine.AttachInstallOnly(profile);
                    if (pathOverrides.TryGetValue(profile.GameId, out var ovr) && Directory.Exists(ovr))
                        patchEngine.SetInstallPath(ovr);
                }
                overlay.SetResolvedPath(profile.GameId, installPath,
                    isManual: pathOverrides.ContainsKey(profile.GameId));
                log.LogInformation("Found installed game: {Name} at {Path}", profile.GameName, installPath);
            }
            else
            {
                log.LogDebug("Game not installed or install path not found: {Name} (AppID {Id})", profile.GameName, profile.SteamAppId);
            }
        }

        // ── Path override handler ──────────────────────────────────────────────
        // Build a lookup of profiles by gameId for the event handler.
        var profilesById = profiles.ToDictionary(p => p.GameId, StringComparer.OrdinalIgnoreCase);

        overlay.PathOverrideChanged += (gameId, newPath) =>
        {
            pathOverrides[gameId] = newPath;
            SaveSettings(settingsFile, settings, log);

            // If this is the active profile, update the patch engine immediately.
            if (profilesById.TryGetValue(gameId, out var profile))
            {
                patchEngine.AttachInstallOnly(profile);
                patchEngine.SetInstallPath(newPath);

                // Also show the profile in the UI if it wasn't found before.
                overlay.OnProfileLoaded(profile);
                overlay.SetResolvedPath(gameId, newPath, isManual: true);
            }

            log.LogInformation("Path override saved for {GameId}: {Path}", gameId, newPath);
        };

        // ── Resolution change handler ──────────────────────────────────────────
        overlay.ResolutionChanged += (width, height) =>
        {
            settings.ScreenWidth  = width;
            settings.ScreenHeight = height;
            patchEngine.ScreenWidth  = width;
            patchEngine.ScreenHeight = height;
            SaveSettings(settingsFile, settings, log);
            log.LogInformation("Screen resolution updated to {W}x{H}", width, height);
        };

        // ── Game detector ──────────────────────────────────────────────────────
        var detector = new GameDetector(profiles, loggerFactory.CreateLogger<GameDetector>());

        detector.GameDetected += (_, e) =>
        {
            patchEngine.Attach(e.ProcessId, e.Profile);

            // If Steam auto-detection failed at startup and we don't have a path yet,
            // try to derive it from the running process's executable path.
            if (!patchEngine.HasInstallPath)
            {
                var derived = SteamLocator.FindFromProcess(e.ProcessId);
                if (derived is not null)
                {
                    patchEngine.SetInstallPath(derived);
                    overlay.SetResolvedPath(e.Profile.GameId, derived, isManual: false);

                    // Persist so it is used next time without needing the game to be running.
                    pathOverrides[e.Profile.GameId] = derived;
                    SaveSettings(settingsFile, settings, log);

                    log.LogInformation(
                        "Auto-detected install path from running process for {Name}: {Path}",
                        e.Profile.GameName, derived);
                }
                else
                {
                    log.LogWarning(
                        "Could not determine install path for {Name} from PID {Pid}.",
                        e.Profile.GameName, e.ProcessId);
                }
            }

            overlay.OnGameDetected(e.Profile, e.ProcessId);

            // Auto-apply all memory (hex-edit) patches the user has toggled ON.
            // File patches must be applied before launch; memory patches require the process.
            foreach (var patch in overlay.GetEnabledPatchesForProfile(e.Profile.GameId)
                                         .Where(p => p.Type == "hex-edit"))
            {
                try
                {
                    var r = patchEngine.Apply(patch);
                    log.LogInformation("Auto-applied memory patch '{Name}': {Status}", patch.Name, r.Status);
                }
                catch (Exception ex)
                {
                    log.LogWarning("Failed to auto-apply memory patch '{Name}': {Msg}", patch.Name, ex.Message);
                }
            }

            log.LogInformation("Game detected: {Name} (PID {Pid})", e.Profile.GameName, e.ProcessId);
        };

        detector.GameExited += (_, e) =>
        {
            // Don't revert file-based patches on exit — they were intentionally applied
            // and should persist across game sessions. Re-attach install-only context
            // so the user can toggle patches again after closing the game.
            patchEngine.AttachInstallOnly(e.Profile);

            // If Steam auto-detection failed, restore from the saved path override so
            // the user can revert patches without having to re-enter the path manually.
            if (!patchEngine.HasInstallPath &&
                pathOverrides.TryGetValue(e.Profile.GameId, out var savedPath) &&
                Directory.Exists(savedPath))
            {
                patchEngine.SetInstallPath(savedPath);
            }

            overlay.OnGameExited(e.Profile.GameId);
            log.LogInformation("Game exited: {Name}", e.Profile.GameName);
        };

        detector.Start();

        log.LogInformation("Peerless Patcher running.");

        // ── Run the window loop (blocks until window closes) ───────────────────
        overlay.Run();

        // ── Cleanup ────────────────────────────────────────────────────────────
        detector.Dispose();
        patchEngine.Dispose();
        overlay.Dispose();
    }

    // ── Settings model ─────────────────────────────────────────────────────────

    /// <summary>Root settings object serialized to settings.yaml.</summary>
    private sealed class AppSettings
    {
        /// <summary>Manually-confirmed or auto-detected install paths, keyed by gameId.</summary>
        public Dictionary<string, string> GamePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Screen width for aspect-ratio patches (default 3440).</summary>
        public int ScreenWidth { get; set; } = 3440;

        /// <summary>Screen height for aspect-ratio patches (default 1440).</summary>
        public int ScreenHeight { get; set; } = 1440;
    }

    // ── YAML helpers ───────────────────────────────────────────────────────────

    private static readonly IDeserializer YamlDeserializer =
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static readonly ISerializer YamlSerializer =
        new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

    private static AppSettings LoadSettings(string path, ILogger log)
    {
        try
        {
            var yaml = File.ReadAllText(path);
            return YamlDeserializer.Deserialize<AppSettings>(yaml) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not load settings from {File}: {Msg}", path, ex.Message);
            return new AppSettings();
        }
    }

    private static void SaveSettings(string path, AppSettings settings, ILogger log)
    {
        try
        {
            var yaml = YamlSerializer.Serialize(settings);
            File.WriteAllText(path, yaml);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not save settings to {File}: {Msg}", path, ex.Message);
        }
    }

    /// <summary>One-time migration from the old overrides.json format.</summary>
    private static AppSettings MigrateFromJson(string jsonPath, string yamlPath, ILogger log)
    {
        var settings = new AppSettings();
        if (!File.Exists(jsonPath)) return settings;
        try
        {
            var json = File.ReadAllText(jsonPath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is not null)
                foreach (var (k, v) in dict)
                    settings.GamePaths[k] = v;
            SaveSettings(yamlPath, settings, log);
            File.Delete(jsonPath);
            log.LogInformation("Migrated settings from overrides.json to settings.yaml");
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not migrate overrides.json: {Msg}", ex.Message);
        }
        return settings;
    }
}
