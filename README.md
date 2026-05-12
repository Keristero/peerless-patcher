# Peerless Patcher

![Screenshot A](screenshot_a.png)
![Screenshot B](screenshot_b.png)

I wanted to play the steam kingdom hearts games in ultrawide, but I was dissatisfied with how hard it is to patch them.

This tool will automatically find your steam installs for kingdom hearts games, allowing you to enable and disable ultrawide patches with a single click

It might work with the epic games store versions too, but I have not tested it - and you would likely need to configure the path to the install manually

### What this is
- A really easy way to install basic ultrawide fixes that remove black bars for gameplay
- A generic patching tool with some built in patches from the pc gaming wiki, additionally there are new and improved patches for BBS, 2.8 and DDD.


### What this is not
- A handcrafted high quality tool
  - I heavily used AI to speed up the development of this tool, it might not work on your machine, if it does not - open a report and I'll try fix it ASAP. I would not have had time to develop this without AI - developing this using AI was easier for me than installing the patches manually.
- A replacement for something like (KH-ReFined)[https://github.com/KH-ReFined/KH-ReFined]
  - ReFined is an awesome project that will hopefully make this tool redundant in the future as it enables true aspect ratio + FOV modifications for any screen size (and much much more)
  - ReFined is already by far the best way to play Kingdom Hearts 2 in ultrawide

# Usage
1. Open the peerless patcher
2. Enable the patches you want
3. Launch the games and play them

# Usage (b)
If the game paths were not detected, try launching the games with the patcher open before you try to apply the patches, the paths should be auto detected.
- try patch them after this, you will need to restart the game

# Usage (c)
If the game paths are not auto detected, go to the paths tab and manually point the tool to your install folders, for example on linux this might look like:
`/run/media/system/Samsung980Pro/SteamLibrary/steamapps/common/KINGDOM HEARTS -HD 1.5+2.5 ReMIX-`
- hit apply then try patch them after this

# Known issues
- There are often glitchy looking effects outside the original 16:9 aspect ratio
- Using narrower aspect ratios than 16:9 wont work well at all because I've avoided scaling the UI

# Report bugs
- If none of the above worked, add a issue to the github and if I have time i'll try fix it.


## Features

- `file-hex-edit` - patches a game exe or data file on disk, reversible
- `file-replace` - swaps a game file with a bundled asset, backs up the original
- `hex-edit` - writes bytes to process memory, reverts automatically when the game exits
- JSON profiles - add a new game by dropping a JSON file, no recompile needed
- Screen resolution saved per machine, patches adapt to your aspect ratio
- Manual install path override if Steam auto-detection fails

## Status
- I've only tested the first few minutes of gameplay in every game, if you have any issues report them in issues or something, try to include as much detail as you can, as well as what platform you are on.
- I've only tested on bazzite linux using proton, with 3440x1440 patches so far, it should work for other screen aspect ratios also, but im not sure.


## Bundled Profiles

| Game | Steam App ID | Status | Patch |
|------|-------------|--------|-------|
| Kingdom Hearts III | 897780 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 2.8 — 0.2 Birth by Sleep | 2552440 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 2.8 — Dream Drop Distance | 2552440 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 1.5+2.5 ReMIX — KH2 Final Mix | 2552430 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 1.5+2.5 ReMIX — KH1 Final Mix | 2552430 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 1.5+2.5 ReMIX — Re:Chain of Memories | 2552430 | Working | Ultrawide without stretching UI |
| Kingdom Hearts HD 1.5+2.5 ReMIX — Birth by Sleep | 2552430 | Working | Ultrawide without stretching UI |

## Downloads

Builds are produced by CI on every push to `main` and on `v*` tag releases.

| Platform | Download |
|----------|---------|
| Windows x64 | `PeerlessPatcher-windows-x64.zip` from [Releases](../../releases) |
| Linux x64 | `PeerlessPatcher-linux-x64.AppImage` from [Releases](../../releases) |

## Credits

Thanks to the KH community for their combined efforts, I pulled most of the hex editing information from the PC Gaming Wiki

## Running

**Windows:** extract the zip and run `PeerlessPatcher.exe`. Administrator is only needed for `hex-edit` (live memory) patches.

**Linux:**
```bash
chmod +x PeerlessPatcher-linux-x64.AppImage
./PeerlessPatcher-linux-x64.AppImage
```

1. Open the patcher.
2. Toggle patches on in the Patches tab.
3. Launch your game.
4. To revert, toggle patches off.

Note: if Steam runs Verify File Integrity, it will revert any patched files. Just re-apply them.

## Dev Setup

Uses [mise](https://mise.jdx.dev/) to pin the .NET SDK version.

```bash
curl https://mise.run | sh
git clone https://github.com/youruser/peerless-patcher
cd peerless-patcher
mise trust && mise install
```

## Building

```bash
mise run build:windows   # dist/win-x64/
mise run build:linux     # dist/linux-x64/
mise run test
```

## Adding a Game Profile

See [`profiles/README.md`](profiles/README.md) for the full schema. For advanced cases where a single constant is shared between 3D projection and UI code, see [`profiles/assets/shadow-constant-redirect.md`](profiles/assets/shadow-constant-redirect.md).

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

Drop the file in `profiles/` next to the executable and restart the patcher.

## Patch Types

| Type | Target | Reversible | Notes |
|------|--------|------------|-------|
| `file-hex-edit` | File on disk | Yes | Scans whole file if `offset: -1` |
| `file-replace` | File on disk | Yes | Backs up original as `<file>.sgp.bak` |
| `hex-edit` | Process memory | Auto on exit | Requires Admin on Windows |

## Notes

- Steam Verify File Integrity will revert file patches. Re-apply after a game update.
- `hex-edit` requires Administrator on Windows.
- On Linux/Proton, `hex-edit` needs `ptrace` access. `file-hex-edit` works without elevated privileges.

