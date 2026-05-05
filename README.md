# Peerless Patcher

A lightweight C# desktop application that applies and reverts game patches — including
ultrawide hex edits — to Steam games. Run it alongside your game, toggle patches on or off,
and your original files are always fully restorable.

## Features

- **File hex patching** (`file-hex-edit`) — patches a game executable or data file on disk; fully reversible
- **File replacement** (`file-replace`) — replaces a game file with a bundled asset; original backed up and restored on revert
- **In-process hex patching** (`hex-edit`) — writes bytes to process memory; auto-reverts when the game exits
- Declarative JSON profiles — add support for a new game by dropping a JSON file, no recompile needed
- Screen resolution settings persisted per-machine — patches adapt to your actual aspect ratio
- Manual install path override for non-default Steam library locations

## Bundled Profiles

| Game | Steam App ID | Patch |
|------|-------------|-------|
| Kingdom Hearts III | 897780 | Ultrawide aspect ratio (patches 7 hardcoded 16:9 constants in the exe) |

---

## Downloads

Pre-built binaries are produced by CI on every push to `main` and for every `v*` tag release.

| Platform | Download |
|----------|---------|
| Windows x64 | `PeerlessPatcher-windows-x64.zip` from [Releases](../../releases) |
| Linux x64 | `PeerlessPatcher-linux-x64.AppImage` from [Releases](../../releases) |

---

## Running

**Windows:**
1. Extract the zip and run `PeerlessPatcher.exe`.  
   Administrator is only needed for `hex-edit` (live memory) patches — file patches work without it unless the game folder is write-protected.

**Linux:**
```bash
chmod +x PeerlessPatcher-linux-x64.AppImage
./PeerlessPatcher-linux-x64.AppImage
```

**Usage:**
1. Open the patcher.
2. Toggle patches on in the **Patches** tab.
3. Launch your game.
4. To revert, toggle patches off and close the patcher.

> File patches modify the executable on disk. If Steam runs **Verify file integrity**, it will revert the patched files — just re-apply them in the patcher.

---

## Environment Setup (for development)

This project uses [mise-en-place](https://mise.jdx.dev/) to pin the .NET SDK version.

```bash
# Install mise (Linux/macOS)
curl https://mise.run | sh

git clone https://github.com/youruser/peerless-patcher
cd peerless-patcher
mise trust && mise install   # installs .NET SDK 8.0.420
```

---

## Building

```bash
# Windows exe (cross-compiles from Linux too)
mise run build:windows
# → dist/win-x64/

# Linux binary
mise run build:linux
# → dist/linux-x64/

# Run tests
mise run test
```

---

## Adding a New Game Profile

See [`profiles/README.md`](profiles/README.md) for the full JSON schema.

Quick example:

```json
// profiles/<steamAppId>.json
{
  "gameId": "my-game",
  "gameName": "My Game",
  "steamAppId": 123456,
  "installDir": "My Game",
  "processName": "MyGame",
  "patches": [
    {
      "type": "file-hex-edit",
      "name": "Ultrawide Support",
      "description": "Patches 16:9 aspect ratio to 21:9.",
      "filePath": "MyGame/Binaries/Win64/MyGame.exe",
      "offset": -1,
      "findBytes": [57, 142, 227, 63],
      "replaceBytes": [85, 85, 21, 64]
    }
  ]
}
```

Drop the file in `profiles/` next to the executable — the patcher picks it up on next launch.

---

## Patch Types Reference

| Type | Target | Reversible | Notes |
|------|--------|------------|-------|
| `file-hex-edit` | File on disk | Yes (toggle off) | Scans whole file if `offset: -1`; supports multiple named sites |
| `file-replace` | File on disk | Yes (toggle off) | Backs up original as `<file>.sgp.bak` |
| `hex-edit` | Process memory (live) | Auto on process exit | Requires Admin on Windows |

---

## Known Limitations

- File patches modify the executable on disk — **Steam's Verify File Integrity will revert them**. Re-apply after a game update.
- In-process `hex-edit` requires Administrator on Windows.
- Linux/Proton support for in-process `hex-edit` patching requires `ptrace` access (`sudo` or a permissive `kernel.yama.ptrace_scope`); `file-hex-edit` works without elevated privileges.
