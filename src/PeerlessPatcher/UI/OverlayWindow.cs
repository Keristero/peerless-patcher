using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using Silk.NET.Windowing.Sdl;
using PeerlessPatcher.Models;
using PeerlessPatcher.Patching;

namespace PeerlessPatcher.UI;

/// <summary>
/// Standalone patcher window rendered with ImGui via Silk.NET + OpenGL/SDL2.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private readonly PatchEngine _patchEngine;
    private readonly IWindow _window;
    private GL _gl = null!;
    private ImGuiController _imgui = null!;
    private IInputContext _input = null!;

    // Per-profile UI state — one entry per installed/manually-added game
    private sealed class ProfileUiState
    {
        public string InstallPath { get; set; } = string.Empty;
        /// <summary>True when the install path was entered by the user rather than auto-detected from Steam.</summary>
        public bool IsManuallyAdded { get; set; }
        public bool IsGameRunning { get; set; }
        public readonly Dictionary<PatchEntry, bool> PatchStates = new();
        public readonly List<PatchEntry> PatchOrder = new();
        /// <summary>
        /// Desired state for file patches queued while the game exe was running.
        /// Applied automatically when IsGameRunning goes false.
        /// </summary>
        public readonly Dictionary<PatchEntry, bool> PendingStates = new();
        /// <summary>Last error message per patch, shown inline in the row.</summary>
        public readonly Dictionary<PatchEntry, string> PatchErrors = new();
        /// <summary>Set true after the user dismisses the pending-patches notice for this run.</summary>
        public bool PendingNoticeShown { get; set; }
    }

    private readonly IReadOnlyList<PatchProfile> _allProfiles;
    private readonly Dictionary<string, ProfileUiState> _profileStates = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedGameId;

    // Paths tab input / error buffers
    private readonly Dictionary<string, string> _pathInputs = new();
    private readonly Dictionary<string, string> _pathErrors = new();

    // Resolution settings buffers
    private int _screenWidth  = 3440;
    private int _screenHeight = 1440;

    /// <summary>Fired when the user applies a manual path override. Args: (gameId, newPath).</summary>
    public event Action<string, string>? PathOverrideChanged;

    /// <summary>Fired when the user saves a new screen resolution. Args: (width, height).</summary>
    public event Action<int, int>? ResolutionChanged;

    /// <summary>Sets the initial resolution values shown in the UI (call before the render loop).</summary>
    public void SetResolution(int width, int height)
    {
        _screenWidth  = width;
        _screenHeight = height;
    }

    // Colours (ABGR packed for ImGui)
    private static readonly uint ColBg          = ImGui.ColorConvertFloat4ToU32(new Vector4(0.08f, 0.08f, 0.12f, 0.93f));
    private static readonly uint ColRowBg       = ImGui.ColorConvertFloat4ToU32(new Vector4(0.14f, 0.14f, 0.20f, 1f));
    private static readonly uint ColOn          = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.47f, 0.24f, 1f));
    private static readonly uint ColOff         = ImGui.ColorConvertFloat4ToU32(new Vector4(0.24f, 0.24f, 0.36f, 1f));
    private static readonly uint ColNA          = ImGui.ColorConvertFloat4ToU32(new Vector4(0.31f, 0.31f, 0.31f, 1f));
    private static readonly uint ColErr         = ImGui.ColorConvertFloat4ToU32(new Vector4(0.86f, 0.47f, 0.47f, 1f));

    public event EventHandler? ExitRequested;

    public OverlayWindow(PatchEngine patchEngine, IReadOnlyList<PatchProfile>? allProfiles = null)
    {
        _allProfiles = allProfiles ?? [];
        _patchEngine = patchEngine;

        // Use SDL2 on Linux — has native Wayland support without libdecor.
        // Force X11 driver to avoid EGL failures in Flatpak/container environments.
        // Use GLFW on Windows — SDL2 native libraries are not bundled for Windows.
        if (OperatingSystem.IsLinux())
        {
            Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "x11");
            SdlWindowing.Use();
        }
        else
        {
            GlfwWindowing.Use();
        }

        var opts = WindowOptions.Default with
        {
            Title = "Peerless Patcher",
            Size = new Vector2D<int>(460, 520),
            ShouldSwapAutomatically = false,
            VSync = false,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3)),
        };

        _window = Window.Create(opts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += sz => _imgui?.Resize(sz.X, sz.Y);
        _window.Closing += () => ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Initializes the window and GL context without entering the render loop. Useful for testing.</summary>
    public void Initialize() => _window.Initialize();

    /// <summary>Requests the window to close and exit the render loop.</summary>
    public void Close() => _window.Close();

    /// <summary>Runs the window/render loop. Blocks until the window is closed.</summary>
    public void Run() => _window.Run();

    /// <summary>
    /// Records the resolved install path for a game. Call once per profile after detection.
    /// </summary>
    /// <param name="isManual">True when the path was entered by the user, false when auto-detected from Steam.</param>
    public void SetResolvedPath(string gameId, string path, bool isManual = false)
    {
        var state = GetOrCreateState(gameId);
        state.InstallPath = path;
        state.IsManuallyAdded = isManual;
        _pathInputs.TryAdd(gameId, path);
    }

    /// <summary>
    /// Called on startup for each installed game. Creates the profile state entry and
    /// pre-selects the game if none is selected yet.
    /// </summary>
    public void OnProfileLoaded(PatchProfile profile)
    {
        var state = GetOrCreateState(profile.GameId);
        if (state.PatchOrder.Count == 0) // don't overwrite user's desired patch states
        {
            foreach (var p in profile.Patches)
            {
                state.PatchStates.TryAdd(p, false);
                state.PatchOrder.Add(p);
            }
        }
        _selectedGameId ??= profile.GameId;
    }

    /// <summary>
    /// Syncs patch toggle states from disk by using probe results. Called after an install
    /// path is resolved so the UI correctly reflects the current state of the game files.
    /// </summary>
    public void SyncPatchStatesFromDisk(string gameId, IEnumerable<(PatchEntry Entry, PatchResultStatus Status)> probeResults)
    {
        if (!_profileStates.TryGetValue(gameId, out var state)) return;
        foreach (var (entry, status) in probeResults)
        {
            if (status == PatchResultStatus.AlreadyPatched)
                state.PatchStates[entry] = true;
            else if (status == PatchResultStatus.AlreadyUnpatched)
                state.PatchStates[entry] = false;
            // Unsupported / SignatureNotFound / Error — leave state as-is
        }
    }

    /// <summary>Called when the game process starts. Marks the profile as running and selects it in the UI.</summary>
    public void OnGameDetected(PatchProfile profile, int _processId)
    {
        if (!_profileStates.ContainsKey(profile.GameId))
            OnProfileLoaded(profile);
        var state = _profileStates[profile.GameId];
        state.IsGameRunning = true;
        state.PendingNoticeShown = false; // reset so notice can show once this session
        _selectedGameId = profile.GameId; // auto-focus the running game
    }

    /// <summary>Called when the game process exits. Applies any pending file patches, then re-enables buttons.</summary>
    public void OnGameExited(string gameId)
    {
        if (!_profileStates.TryGetValue(gameId, out var state)) return;
        state.IsGameRunning = false;

        // Apply all queued pending patches now that the exe is closed.
        foreach (var (patch, desiredActive) in state.PendingStates)
        {
            PatchResult result;
            try
            {
                result = desiredActive ? _patchEngine.Apply(patch) : _patchEngine.Revert(patch);
            }
            catch (Exception ex)
            {
                result = new PatchResult(PatchResultStatus.Error, ex.Message);
            }

            switch (result.Status)
            {
                case PatchResultStatus.Applied:
                case PatchResultStatus.AlreadyPatched:
                    state.PatchStates[patch] = true;
                    state.PatchErrors.Remove(patch);
                    break;
                case PatchResultStatus.Reverted:
                case PatchResultStatus.AlreadyUnpatched:
                    state.PatchStates[patch] = false;
                    state.PatchErrors.Remove(patch);
                    break;
                case PatchResultStatus.Error:
                    state.PatchErrors[patch] = result.ErrorMessage ?? "Unknown error";
                    break;
            }
        }
        state.PendingStates.Clear();
    }

    /// <summary>Returns the patch states for the currently selected profile.</summary>
    public IEnumerable<(PatchEntry Entry, bool IsActive)> GetPatchStates()
    {
        if (_selectedGameId is null || !_profileStates.TryGetValue(_selectedGameId, out var state))
            return [];
        return state.PatchOrder.Select(e => (e, state.PatchStates.TryGetValue(e, out var v) && v));
    }

    /// <summary>
    /// Returns all patches currently toggled ON for a profile.
    /// Used by Program.cs to auto-apply memory patches when the game is detected.
    /// </summary>
    public IEnumerable<PatchEntry> GetEnabledPatchesForProfile(string gameId)
    {
        if (!_profileStates.TryGetValue(gameId, out var state)) return [];
        return state.PatchOrder.Where(e => state.PatchStates.TryGetValue(e, out var v) && v);
    }

    private ProfileUiState GetOrCreateState(string gameId)
    {
        if (!_profileStates.TryGetValue(gameId, out var state))
            _profileStates[gameId] = state = new ProfileUiState();
        return state;
    }

    // ── Silk.NET lifecycle ─────────────────────────────────────────────────────

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _imgui = new ImGuiController(_gl, _window.Size.X, _window.Size.Y);
        _input = _window.CreateInput();

        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += OnKeyDown;
            kb.KeyUp += OnKeyUp;
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseMove += (_, pos) => _imgui.SetMousePos(pos.X, pos.Y);
            mouse.MouseDown += (_, btn) => _imgui.SetMouseButton((int)btn, true);
            mouse.MouseUp += (_, btn) => _imgui.SetMouseButton((int)btn, false);
            mouse.Scroll += (_, scroll) => _imgui.SetMouseWheel(scroll.X, scroll.Y);
        }
    }

    private void OnKeyDown(IKeyboard kb, Key key, int _scancode)
    {
        _imgui.AddKeyEvent(SilkKeyToImGui(key), true);
    }

    private void OnKeyUp(IKeyboard kb, Key key, int _scancode)
    {
        _imgui.AddKeyEvent(SilkKeyToImGui(key), false);
    }

    private void OnRender(double dt)
    {
        _gl.ClearColor(0, 0, 0, 0);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _imgui.NewFrame(dt);
        DrawUI();
        _imgui.Render();
        _window.SwapBuffers();
    }

    // ── ImGui UI ───────────────────────────────────────────────────────────────

    private void DrawUI()
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(io.DisplaySize);
        ImGui.SetNextWindowBgAlpha(0.93f);

        var flags = ImGuiWindowFlags.NoTitleBar
                  | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar
                  | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.12f, 0.93f));
        ImGui.Begin("##overlay", flags);

        // ── Header ────────────────────────────────────────────────────────────
        ImGui.SetCursorPosX(12);
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = ver is null ? string.Empty : $" v{ver.Major}.{ver.Minor}.{ver.Build}";
        ImGui.TextColored(new Vector4(1, 1, 1, 1), $"Peerless Patcher{verStr}");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ── Tab bar ───────────────────────────────────────────────────────────
        if (ImGui.BeginTabBar("##tabs"))
        {
            if (ImGui.BeginTabItem("Patches"))
            {
                ImGui.Spacing();
                DrawPatchesTab(io.DisplaySize);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Paths"))
            {
                ImGui.Spacing();
                DrawPathsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
        ImGui.PopStyleColor();
    }

    private void DrawPatchesTab(System.Numerics.Vector2 displaySize)
    {
        if (_profileStates.Count == 0)
        {
            string msg = "No supported game found.\n\nInstall a supported game via Steam,\nthen restart this patcher.\n\nIf it is installed, set the path\nmanually in the Paths tab.";
            var msgSz = ImGui.CalcTextSize(msg);
            ImGui.SetCursorPosX((displaySize.X - msgSz.X) * 0.5f);
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.78f, 1f), msg);
            return;
        }

        var loadedProfiles = _allProfiles.Where(p => _profileStates.ContainsKey(p.GameId)).ToList();

        // ── Game selector (only when multiple profiles are loaded) ──────────────────────
        if (loadedProfiles.Count > 1)
        {
            const float rowH = 22;
            ImGui.BeginChild("##gameList", new Vector2(0, loadedProfiles.Count * rowH + 6), ImGuiChildFlags.None);
            foreach (var profile in loadedProfiles)
            {
                var ps = _profileStates[profile.GameId];
                bool isSel = _selectedGameId == profile.GameId;

                if (isSel)
                    ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.18f, 0.18f, 0.28f, 1f));
                ImGui.BeginChild($"##gs_{profile.GameId}", new Vector2(0, rowH), ImGuiChildFlags.None);

                if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    _selectedGameId = profile.GameId;

                ImGui.TextColored(
                    isSel ? new Vector4(1, 1, 1, 1) : new Vector4(0.75f, 0.75f, 0.85f, 1f),
                    profile.GameName);

                if (ps.IsGameRunning)
                { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), "\u25cf Running"); }
                else if (ps.IsManuallyAdded)
                { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.8f, 0.65f, 0.2f, 1f), "[Manual]"); }

                ImGui.EndChild();
                if (isSel) ImGui.PopStyleColor();
            }
            ImGui.EndChild();
            ImGui.Separator();
            ImGui.Spacing();
        }

        // ── Selected game header + patches ─────────────────────────────────────────
        // Auto-select the only game, or the user-selected one.
        var selected = loadedProfiles.Count == 1
            ? loadedProfiles[0]
            : loadedProfiles.FirstOrDefault(p => p.GameId == _selectedGameId);

        if (selected is null || !_profileStates.TryGetValue(selected.GameId, out var selState))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.78f, 1f), "Select a game above.");
            return;
        }

        _selectedGameId ??= selected.GameId;

        // Game name + status badges
        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), selected.GameName);
        if (selState.IsGameRunning)
        { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), "\u25cf Running"); }
        if (selState.IsManuallyAdded)
        { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.8f, 0.65f, 0.2f, 1f), "[Manual]"); }
        ImGui.Spacing();

        // Patch rows
        ImGui.BeginChild("##patches", new Vector2(0, -50), ImGuiChildFlags.None);

        // ── Pending-patches notice (shown once per game run, dismissable) ─────
        bool hasPendingPatches = selState.IsGameRunning &&
                                 selState.PendingStates.Count > 0 &&
                                 !selState.PendingNoticeShown;
        if (hasPendingPatches)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.28f, 0.22f, 0.04f, 1f));
            ImGui.BeginChild("##pendingNotice", new Vector2(0, 44), ImGuiChildFlags.Border);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
                $"\u23f3 {selState.PendingStates.Count} patch(es) queued");
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.5f, 1f),
                "Exe patches can't apply while the game runs. Will apply on close.");
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 18);
            if (ImGui.SmallButton("X")) selState.PendingNoticeShown = true;
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        foreach (var patch in selState.PatchOrder)
        {
            DrawPatchRow(patch, selState);
            ImGui.Spacing();
        }
        ImGui.EndChild();
    }

    private void DrawPathsTab()
    {
        // ── Resolution settings ────────────────────────────────────────────────
        bool anyPatchApplied = _profileStates.Values.Any(s => s.PatchStates.Values.Any(v => v));

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.11f, 0.11f, 0.17f, 1f));
        ImGui.BeginChild("##resolutionSection", new Vector2(0, 64), ImGuiChildFlags.Border);

        ImGui.TextColored(new Vector4(0.85f, 0.85f, 1f, 1f), "Screen Resolution");
        if (anyPatchApplied)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.8f, 0.65f, 0.2f, 1f), "(revert patches to edit)");
        }

        if (anyPatchApplied) ImGui.BeginDisabled();

        float saveW  = 50;
        float xW     = ImGui.CalcTextSize("×").X + 8;
        float fieldW = (ImGui.GetContentRegionAvail().X - saveW - xW - 16) * 0.5f;
        fieldW = Math.Max(fieldW, 80);
        int w = _screenWidth, h = _screenHeight;
        ImGui.SetNextItemWidth(fieldW);
        ImGui.InputInt("##resW", ref w, 0, 0);
        ImGui.SameLine(0, 6);
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.7f, 1f), "×");
        ImGui.SameLine(0, 6);
        ImGui.SetNextItemWidth(fieldW);
        ImGui.InputInt("##resH", ref h, 0, 0);
        ImGui.SameLine(0, 8);
        if (ImGui.Button("Save##res", new Vector2(saveW, 0)))
        {
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            _screenWidth  = w;
            _screenHeight = h;
            ResolutionChanged?.Invoke(w, h);
        }

        if (anyPatchApplied) ImGui.EndDisabled();

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();

        // ── Install paths ──────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.63f, 0.63f, 0.71f, 1f),
            "Override the install path if Steam auto-detection fails.");
        ImGui.Spacing();

        if (_allProfiles.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.78f, 1f), "No profiles loaded.");
            return;
        }

        ImGui.BeginChild("##pathrows", new Vector2(0, -50), ImGuiChildFlags.None);

        foreach (var profile in _allProfiles)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.14f, 0.14f, 0.20f, 1f));
            ImGui.BeginChild($"##pathrow_{profile.GameId}", new Vector2(0, 86), ImGuiChildFlags.Border);

            // Game name
            ImGui.TextColored(new Vector4(1, 1, 1, 1), profile.GameName);

            // Current resolved path
            bool hasPath = _profileStates.TryGetValue(profile.GameId, out var pathState) &&
                           !string.IsNullOrEmpty(pathState.InstallPath);
            if (hasPath)
            {
                ImGui.TextColored(new Vector4(0.42f, 0.78f, 0.42f, 1f), pathState!.InstallPath);
                if (pathState.IsManuallyAdded)
                { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.8f, 0.65f, 0.2f, 1f), "[Manual]"); }
            }
            else
                ImGui.TextColored(new Vector4(0.86f, 0.47f, 0.47f, 1f), "Not found - enter path below");

            // Text input + Apply button
            _pathInputs.TryAdd(profile.GameId, string.Empty);
            var buf = _pathInputs[profile.GameId];
            float applyW = 62;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - applyW - 8);
            if (ImGui.InputText($"##pathInput_{profile.GameId}", ref buf, 512))
                _pathInputs[profile.GameId] = buf;

            ImGui.SameLine();
            bool hasError = _pathErrors.ContainsKey(profile.GameId);
            if (hasError)
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f));

            if (ImGui.Button($"Apply##{profile.GameId}", new Vector2(applyW, 0)))
            {
                var path = _pathInputs[profile.GameId].Trim();
                if (Directory.Exists(path))
                {
                    _pathErrors.Remove(profile.GameId);
                    PathOverrideChanged?.Invoke(profile.GameId, path);
                    // PathOverrideChanged handler calls SetResolvedPath(isManual:true),
                    // which updates _profileStates — no local _resolvedPaths needed.
                }
                else
                {
                    _pathErrors[profile.GameId] = "Directory not found";
                }
            }

            if (hasError) ImGui.PopStyleColor();

            if (_pathErrors.TryGetValue(profile.GameId, out var err))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.86f, 0.47f, 0.47f, 1f), err);
            }

            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    private void DrawPatchRow(PatchEntry patch, ProfileUiState state)
    {
        bool isActive  = state.PatchStates.TryGetValue(patch, out var v) && v;
        bool isPending = state.PendingStates.ContainsKey(patch);
        bool pendingDesired = isPending && state.PendingStates[patch];
        bool hasError  = state.PatchErrors.TryGetValue(patch, out var errMsg);
        bool isUnsupported = false;
        bool isMemory  = patch.Type == "hex-edit";
        bool isFileType = patch.Type == "file-hex-edit" || patch.Type == "file-replace";

        float rowH = (hasError || (isPending && !state.IsGameRunning)) ? 84f : 68f;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.14f, 0.14f, 0.20f, 1f));
        ImGui.BeginChild($"##row_{patch.Name}", new Vector2(0, rowH), ImGuiChildFlags.Border);

        // Name + description
        ImGui.TextColored(new Vector4(1, 1, 1, 1), patch.Name);
        ImGui.TextColored(new Vector4(0.63f, 0.63f, 0.71f, 1f), patch.Description ?? string.Empty);

        // Toggle button — right-aligned
        var btnSz = new Vector2(70, 26);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - btnSz.X + ImGui.GetCursorPosX() - 8);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 30);

        if (isUnsupported)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.31f, 0.31f, 0.31f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));
            ImGui.Button("N/A", btnSz);
            ImGui.PopStyleColor(2);
            ImGui.TextColored(new Vector4(0.86f, 0.47f, 0.47f, 1f), "Not supported yet");
        }
        else if (isPending)
        {
            // Queued change — amber PENDING button, click to cancel
            var amberBtn = new Vector4(0.55f, 0.40f, 0.05f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, amberBtn);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, amberBtn with { W = 0.8f });
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.4f, 1f));
            if (ImGui.Button($"PEND##" + patch.Name, btnSz))
                TogglePatch(patch, isActive, state); // cancels pending
            ImGui.PopStyleColor(3);
            string pendingLabel = pendingDesired ? "\u2192 ON on close" : "\u2192 OFF on close";
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), pendingLabel);
        }
        else
        {
            var btnCol = isActive
                ? new Vector4(0.12f, 0.47f, 0.24f, 1f)
                : new Vector4(0.24f, 0.24f, 0.36f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, btnCol);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnCol with { W = 0.8f });
            if (ImGui.Button(isActive ? "ON##" + patch.Name : "OFF##" + patch.Name, btnSz))
                TogglePatch(patch, isActive, state);
            ImGui.PopStyleColor(2);

            if (isMemory && !state.IsGameRunning && isActive)
                ImGui.TextColored(new Vector4(0.5f, 0.78f, 1f, 1f), "Auto-applies on start");
        }

        // Inline error (shown even after the game is closed if patch still failed)
        if (hasError)
            ImGui.TextColored(new Vector4(0.95f, 0.35f, 0.35f, 1f), $"\u26a0 {errMsg}");

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void TogglePatch(PatchEntry patch, bool isActive, ProfileUiState state)
    {
        // Memory patches when game is not running: store desired state only.
        // Program.cs auto-applies all enabled memory patches when the game starts.
        if (patch.Type == "hex-edit" && !state.IsGameRunning)
        {
            state.PatchStates[patch] = !isActive;
            return;
        }

        // File patches while game is running: toggle pending queue.
        bool isFileType = patch.Type == "file-hex-edit" || patch.Type == "file-replace";
        if (isFileType && state.IsGameRunning)
        {
            if (state.PendingStates.ContainsKey(patch))
                state.PendingStates.Remove(patch); // cancel pending on second click
            else
                state.PendingStates[patch] = !isActive; // queue desired change
            return;
        }

        // File-hex-edit patches only need a valid install path, not a running process.
        // Ensure the engine is pointed at this profile's resolved path before dispatching.
        if (!string.IsNullOrEmpty(state.InstallPath))
            _patchEngine.SetInstallPath(state.InstallPath);

        // Normal immediate apply/revert
        PatchResult result;
        try
        {
            result = isActive ? _patchEngine.Revert(patch) : _patchEngine.Apply(patch);
        }
        catch (Exception ex)
        {
            result = new PatchResult(PatchResultStatus.Error, ex.Message);
        }

        switch (result.Status)
        {
            case PatchResultStatus.Applied:
            case PatchResultStatus.AlreadyPatched:
                state.PatchStates[patch] = true;
                state.PatchErrors.Remove(patch);
                break;
            case PatchResultStatus.Reverted:
            case PatchResultStatus.AlreadyUnpatched:
                state.PatchStates[patch] = false;
                state.PatchErrors.Remove(patch);
                break;
            case PatchResultStatus.Error:
                state.PatchErrors[patch] = result.ErrorMessage ?? "Unknown error";
                break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void CentreWindow()
    {
        // GLFW doesn't expose monitor resolution easily here; default to 1920x1080 fallback
        var mon = _window.Monitor;
        int sw = mon?.VideoMode.Resolution?.X ?? 1920;
        int sh = mon?.VideoMode.Resolution?.Y ?? 1080;
        _window.Position = new Vector2D<int>((sw - _window.Size.X) / 2, (sh - _window.Size.Y) / 2);
    }

    // ── P/Invoke / native delegates ───────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GlfwInitHintDelegate(int hint, int value);


    private static ImGuiKey SilkKeyToImGui(Key key) => key switch
    {
        Key.Tab => ImGuiKey.Tab,
        Key.Left => ImGuiKey.LeftArrow,
        Key.Right => ImGuiKey.RightArrow,
        Key.Up => ImGuiKey.UpArrow,
        Key.Down => ImGuiKey.DownArrow,
        Key.PageUp => ImGuiKey.PageUp,
        Key.PageDown => ImGuiKey.PageDown,
        Key.Home => ImGuiKey.Home,
        Key.End => ImGuiKey.End,
        Key.Delete => ImGuiKey.Delete,
        Key.Backspace => ImGuiKey.Backspace,
        Key.Enter => ImGuiKey.Enter,
        Key.Escape => ImGuiKey.Escape,
        Key.ControlLeft or Key.ControlLeft => ImGuiKey.LeftCtrl,
        Key.ControlRight => ImGuiKey.RightCtrl,
        Key.ShiftLeft => ImGuiKey.LeftShift,
        Key.ShiftRight => ImGuiKey.RightShift,
        Key.AltLeft => ImGuiKey.LeftAlt,
        Key.AltRight => ImGuiKey.RightAlt,
        _ => ImGuiKey.None,
    };

    public void Dispose()
    {
        _input?.Dispose();
        _imgui?.Dispose();
        _gl?.Dispose();
        _window?.Dispose();
    }
}
