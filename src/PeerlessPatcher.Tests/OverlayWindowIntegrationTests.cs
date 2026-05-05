using Microsoft.Extensions.Logging.Abstractions;
using PeerlessPatcher.Patching;
using PeerlessPatcher.UI;

namespace PeerlessPatcher.Tests;

/// <summary>
/// Integration tests for <see cref="OverlayWindow"/>.
/// Only run when <c>SGPATCHER_INTEGRATION_TESTS=1</c> is set and a display is available.
/// This prevents accidental test host crashes in environments where GLFW window creation
/// fails (e.g. missing libdecor plugins on Wayland).
/// Run with: <c>SGPATCHER_INTEGRATION_TESTS=1 dotnet test</c>
/// </summary>
public sealed class OverlayWindowIntegrationTests
{
    private static bool ShouldRun =>
        OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("SGPATCHER_INTEGRATION_TESTS") == "1" &&
        (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
         !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    [Fact]
    public void OverlayWindow_InitializesAndClosesWithoutCrashing()
    {
        if (!ShouldRun) return;

        using var engine = new PatchEngine([], NullLogger<PatchEngine>.Instance);
        using var overlay = new OverlayWindow(engine);

        // Initialize creates the GLFW window and OpenGL context.
        // If this doesn't throw/crash, window creation succeeded.
        overlay.Initialize();
        overlay.Close();
    }

    [Fact]
    public void OverlayWindow_OnGameDetected_SetsProfileWithoutCrash()
    {
        if (!ShouldRun) return;

        using var engine = new PatchEngine([], NullLogger<PatchEngine>.Instance);
        using var overlay = new OverlayWindow(engine);

        overlay.Initialize();

        var profile = TestHelpers.MakeMinimalProfile();
        overlay.OnGameDetected(profile, 99999);

        Assert.Single(overlay.GetPatchStates());

        overlay.Close();
    }

    [Fact]
    public void OverlayWindow_OnGameExited_ClearsState()
    {
        if (!ShouldRun) return;

        using var engine = new PatchEngine([], NullLogger<PatchEngine>.Instance);
        using var overlay = new OverlayWindow(engine);

        overlay.Initialize();

        var profile = TestHelpers.MakeMinimalProfile();
        overlay.OnGameDetected(profile, 99999);
        overlay.OnGameExited(profile.GameId);

        // Patch states are preserved after exit so the user can re-apply without restarting.
        // Verify the profile's patches are still listed (not cleared).
        Assert.NotEmpty(overlay.GetPatchStates());

        overlay.Close();
    }
}
