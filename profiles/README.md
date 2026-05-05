# Game Patch Profiles

Each `.json` file in this directory defines patches for one Steam game.
The filename must be the Steam App ID (e.g. `897780.json` for Kingdom Hearts III).

## Profile Schema

```json
{
  "gameId":      "kebab-case-id",
  "gameName":    "Display Name",
  "steamAppId":  123456,
  "processName": "GameExecutable",
  "patches": [ ... ]
}
```

| Field         | Type   | Required | Description |
|---------------|--------|----------|-------------|
| `gameId`      | string | ✓ | Unique kebab-case identifier |
| `gameName`    | string | ✓ | Human-readable name shown in the overlay |
| `steamAppId`  | int    | ✓ | Steam App ID (also the filename, without `.json`) |
| `processName` | string | ✓ | Process name to detect (no `.exe`, case-insensitive) |
| `patches`     | array  | ✓ | List of patch entries (see below) |

---

## Patch Entry Types

### `hex-edit` — In-process memory patch

Writes bytes directly to the **running game process's virtual memory**.
No file on disk is touched. The patch is automatically undone when the process exits.

```json
{
  "type":         "hex-edit",
  "name":         "Ultrawide Support",
  "description":  "Patches aspect ratio in process memory.",
  "offset":       5242880,
  "findBytes":    [57, 142, 227, 63],
  "replaceBytes": [85, 85, 21, 64]
}
```

| Field          | Type     | Required | Description |
|----------------|----------|----------|-------------|
| `offset`       | int64    | ✓ | Byte offset from the start of the process image |
| `findBytes`    | int[]    | ✓ | Original bytes (used to validate and revert). Must equal length of `replaceBytes`. |
| `replaceBytes` | int[]    | ✓ | Replacement bytes to write on Apply |

> **Requires Administrator** on Windows (WriteProcessMemory API).

---

### `file-hex-edit` — Executable / file on disk patch

Writes bytes directly into a **file on disk** (e.g. the game executable).
The patcher locates the game's Steam installation directory automatically.

```json
{
  "type":         "file-hex-edit",
  "name":         "Ultrawide (21:9) Aspect Ratio",
  "description":  "Patches the hardcoded 16:9 aspect ratio to 21:9.",
  "filePath":     "KINGDOM HEARTS III.exe",
  "offset":       -1,
  "findBytes":    [57, 142, 227, 63],
  "replaceBytes": [85, 85, 21, 64]
}
```

| Field          | Type     | Required | Description |
|----------------|----------|----------|-------------|
| `filePath`     | string   | ✓ | Path to the file, **relative to the game's install directory** |
| `offset`       | int64    | ✓ | Byte offset within the file. Use `-1` to scan the entire file for the first occurrence of `findBytes`. |
| `findBytes`    | int[]    | ✓ | Original bytes (validates before patching, used to revert). Must equal length of `replaceBytes`. |
| `replaceBytes` | int[]    | ✓ | Replacement bytes to write on Apply |

> **Revert** works by searching the file for `replaceBytes` and writing `findBytes` back.
> It is safe to Apply/Revert while the game is running (the game loads the exe into memory on launch).

---

### `file-replace` — Full file replacement *(not yet implemented)*

Replaces an entire game file with a mod asset. Displayed as disabled in the overlay.

```json
{
  "type":        "file-replace",
  "name":        "HD Texture Pack",
  "description": "Replaces textures with high-resolution versions.",
  "sourcePath":  "assets/textures.pak",
  "targetPath":  "Content/Paks/textures.pak"
}
```

---

## Adding a New Game

1. Find the Steam App ID (visible in the game's Steam store URL).
2. Copy an existing profile as a template: `cp 897780.json <appid>.json`
3. Set `processName` to the game's `.exe` name **without** the extension.
   - On Windows: open Task Manager while the game is running.
   - On Linux/Proton: `ps aux | grep -i game`
4. For `file-hex-edit` patches:
   - Open the executable in a hex editor (e.g. ImHex, HxD).
   - Find the bytes you want to change.
   - Fill in `findBytes`, `replaceBytes`, and either a fixed `offset` or `-1` for scan mode.
5. Drop the profile in this `profiles/` directory — no rebuild required.
