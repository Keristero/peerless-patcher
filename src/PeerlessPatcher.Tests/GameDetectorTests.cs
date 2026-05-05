using Microsoft.Extensions.Logging.Abstractions;
using PeerlessPatcher.Detection;
using PeerlessPatcher.Models;

namespace PeerlessPatcher.Tests;

public class GameDetectorTests
{
    private static PatchProfile MakeProfile(string processName) => new()
    {
        GameId = "test",
        GameName = "Test Game",
        SteamAppId = 1,
        ProcessName = processName,
        Patches = [],
    };

    [Fact]
    public void Poll_NoMatchingProcess_DoesNotFireEvent()
    {
        var profiles = new List<PatchProfile> { MakeProfile("ThisProcessWillNeverExist_XYZ_12345") };
        using var detector = new GameDetector(profiles, NullLogger<GameDetector>.Instance, TimeSpan.FromHours(1));

        bool fired = false;
        detector.GameDetected += (_, _) => fired = true;
        detector.Poll();

        Assert.False(fired);
    }

    [Fact]
    public void Poll_MatchingProcess_FiresGameDetectedEvent()
    {
        // Use the current process name as the "game"
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var profiles = new List<PatchProfile> { MakeProfile(currentProcess.ProcessName) };

        using var detector = new GameDetector(profiles, NullLogger<GameDetector>.Instance, TimeSpan.FromHours(1));

        GameDetectedEventArgs? detected = null;
        detector.GameDetected += (_, e) => detected = e;
        detector.Poll();

        Assert.NotNull(detected);
        Assert.Equal(currentProcess.ProcessName, detected.Profile.ProcessName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Poll_GameExits_FiresGameExitedEvent()
    {
        // Simulate a game that was previously detected (inject via a second poll where process is gone)
        var profiles = new List<PatchProfile> { MakeProfile("ThisProcessWillNeverExist_XYZ_12345") };
        using var detector = new GameDetector(profiles, NullLogger<GameDetector>.Instance, TimeSpan.FromHours(1));

        // Manually set active profile via reflection to simulate a previously detected game
        typeof(GameDetector)
            .GetField("_activeProfile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(detector, profiles[0]);
        typeof(GameDetector)
            .GetField("_activeProcessId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(detector, 999999999); // PID that can't exist

        GameExitedEventArgs? exited = null;
        detector.GameExited += (_, e) => exited = e;
        detector.Poll();

        Assert.NotNull(exited);
        Assert.Equal(profiles[0].GameName, exited.Profile.GameName);
    }
}
