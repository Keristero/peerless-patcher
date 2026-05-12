---
name: ghidra-headless
description: 'Run Ghidra headlessly from the command line to analyze KH3 or KH2.8 exe files. Use when reverse engineering game binaries: finding byte patterns, scanning for instructions, decompiling functions, or running analysis scripts (analyzeHeadless). No Ghidra GUI required.'
argument-hint: 'Describe the RE task: decompile a function at an offset, find a float constant, scan for an instruction pattern, run a scan script, etc.'
---

# Ghidra Headless Analysis

## Prerequisites

Run the setup task first (only needed once):
```
mise run setup:ghidra
```
This downloads Ghidra into `tools/ghidra/` if it isn't already installed system-wide.
Version is configured via `GHIDRA_VERSION` in `mise.toml` → `[env]`.

---

## Resolving the `analyzeHeadless` Binary

In this project, check in order:
1. `tools/ghidra/support/analyzeHeadless` (local install by setup task)
2. `analyzeHeadless` on system PATH

From the shell:
```bash
HEADLESS="${MISE_PROJECT_ROOT}/tools/ghidra/support/analyzeHeadless"
if [ ! -x "$HEADLESS" ]; then
  HEADLESS=$(command -v analyzeHeadless)
fi
echo "$HEADLESS"
```

For the examples below, `$HEADLESS` refers to whichever path resolves.

---

## Project Directory

Analysis projects are stored in `tools/ghidra-projects/` (gitignored):
```bash
mkdir -p tools/ghidra-projects
PROJECT_DIR="$(pwd)/tools/ghidra-projects"
```

Ghidra project files (`.gpr`, `.rep/`) are written here. Reuse the same project name across runs so Ghidra skips re-analysis and goes straight to your script.

---

## Game Exe Paths (this repo)

| Game | AppID | Exe path (relative to Steam library) |
|------|-------|--------------------------------------|
| KH3 | 897780 | `KINGDOM HEARTS III/Binaries/Win64/KINGDOM HEARTS III.exe` |
| DDD (KH HD 2.8) | 2552440 | `KINGDOM HEARTS Dream Drop Distance.exe` |
| BBS (KH HD 2.8) | 2552430 | `KINGDOM HEARTS Birth by Sleep Final Mix.exe` |

KH3 exe: 150,713,896 bytes · IMAGE_BASE=0x140000000 · `.text` FileOff=0x600  
DDD exe: 11,581,712 bytes · IMAGE_BASE=0x140000000 · `.text` RawOff=0x400 VA=0x1000 · `.rdata` RawOff=0x780600 VA=0x782000

---

## Core Commands

### Import and Auto-Analyze an Exe (first time)
```bash
$HEADLESS "$PROJECT_DIR" KH3 \
  -import "/path/to/KINGDOM HEARTS III.exe" \
  -overwrite \
  -analysisTimeoutPerFile 3600
```
Use `-overwrite` to re-import. Analysis of KH3 takes several minutes.

### Re-use an Existing Project (fast)
After the first import, subsequent runs skip analysis:
```bash
$HEADLESS "$PROJECT_DIR" KH3 -process
```

### Run a Post-Script After Analysis
```bash
$HEADLESS "$PROJECT_DIR" KH3 \
  -import "/path/to/KINGDOM HEARTS III.exe" \
  -postScript MyScript.py arg1 arg2 \
  -scriptPath "$(pwd)/scripts"
```

### Import + Script in One Step
```bash
$HEADLESS "$PROJECT_DIR" KH3 \
  -import "/path/to/KINGDOM HEARTS III.exe" \
  -postScript scan_02.py \
  -scriptPath "$(pwd)/scripts" \
  -overwrite
```

### Scripting Without Importing (project already exists)
```bash
$HEADLESS "$PROJECT_DIR" KH3 \
  -process "KINGDOM HEARTS III.exe" \
  -postScript scan_02.py \
  -scriptPath "$(pwd)/scripts"
```

---

## Existing Scan Scripts (in `scripts/`)

| Script | Purpose |
|--------|---------|
| `scan_02.py` | Main KH3 scan — finds letterbox/aspect/viewport patches |
| `scan_02_aspect.py` | Aspect ratio constant scan |
| `scan_02_3dvp.py` | 3D viewport instruction scan |
| `scan_02_viewport.py` | Viewport patch candidates |
| `scan_02_viewport2.py` / `scan_02_viewport3.py` | Viewport variant scans |
| `scan_02_constrain.py` | bConstrainAspectRatio patch scan |
| `scan_02_ctx.py` | Context scan |
| `scan_02_fast.py` | Fast/reduced scan |
| `scan_ddd.py` / `scan_ddd2.py` | DDD FOV/aspect scans |
| `profiles/assets/2552440/scan_ddd.py` | DDD profile verify script |
| `profiles/assets/2552430/scan_bbs.py` | BBS profile verify script |

These are plain Python scripts (not Ghidra scripts). Run them directly against a binary file:
```bash
python3 scripts/scan_02.py "/path/to/KINGDOM HEARTS III.exe"
```

---

## Common RE Patterns for This Project

### Find a Float Constant (e.g. 9.0 = `00 00 10 41`)
```bash
python3 - <<'EOF'
import struct, sys
data = open(sys.argv[1], 'rb').read()
target = struct.pack('<f', 9.0)
for i in range(len(data) - 4):
    if data[i:i+4] == target:
        print(f"  foff=0x{i:08X}")
EOF "/path/to/game.exe"
```

### Find All RIP-Relative `movss xmm,[rip+disp32]` Callers of a VA
```bash
python3 - <<'EOF'
import struct, sys
data = open(sys.argv[1], 'rb').read()
IMAGE_BASE   = 0x140000000
TEXT_RAW     = 0x600         # KH3 .text file offset
TEXT_VADDR   = 0x1000        # KH3 .text VA (relative to IMAGE_BASE)
TEXT_RAWSIZE = len(data) - TEXT_RAW  # approximate
TARGET_VA    = IMAGE_BASE + 0xNNNNNNNN  # fill in constant VA

for i in range(TEXT_RAW, TEXT_RAW + TEXT_RAWSIZE - 8):
    if data[i] == 0xF3 and data[i+1] == 0x0F and data[i+2] == 0x10:
        modrm = data[i+3]
        if (modrm & 0xC7) == 0x05:
            disp = struct.unpack_from('<i', data, i+4)[0]
            next_va = IMAGE_BASE + TEXT_VADDR + (i - TEXT_RAW) + 8
            if next_va + disp == TARGET_VA:
                print(f"  caller foff=0x{i:08X}  VA=0x{IMAGE_BASE + TEXT_VADDR + (i - TEXT_RAW):016X}")
EOF "/path/to/game.exe"
```

### Decompile a Function at a Known VA (via Ghidra headless script)
Create a one-off Ghidra script, e.g. `scripts/decompile_at.py`:
```python
# @runtime Python 3
from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor

addr = currentProgram.getAddressFactory().getAddress(getScriptArgs()[0])
func = getFunctionContaining(addr)
if func is None:
    print(f"No function at {addr}")
else:
    ifc = DecompInterface()
    ifc.openProgram(currentProgram)
    result = ifc.decompileFunction(func, 60, ConsoleTaskMonitor())
    print(result.getDecompiledFunction().getC())
```
Then run:
```bash
$HEADLESS "$PROJECT_DIR" KH3 -process "KINGDOM HEARTS III.exe" \
  -postScript decompile_at.py 0x140EB01CC \
  -scriptPath "$(pwd)/scripts"
```

---

## Useful Flags Reference

| Flag | Purpose |
|------|---------|
| `-import <path>` | Import (and analyze) a binary |
| `-process [name]` | Run scripts on existing project program |
| `-postScript <script> [args...]` | Ghidra script to run after analysis |
| `-scriptPath <dir>` | Directory containing your scripts |
| `-overwrite` | Overwrite existing project file |
| `-deleteProject` | Delete project before starting (clean slate) |
| `-analysisTimeoutPerFile <sec>` | Max seconds for auto-analysis (default: 300) |
| `-noanalysis` | Skip auto-analysis (fast import; useful if you already analyzed) |
| `-log <file>` | Write log output to file instead of stdout |
| `-readOnly` | Don't save changes back to the project |

---

## Notes

- Ghidra requires **Java 21+**. Verify with `java -version`. If missing, install OpenJDK 21.
- `analyzeHeadless` output goes to stdout — pipe through `tee` to keep a log:  
  `$HEADLESS ... 2>&1 | tee analysis.log`
- KH3 auto-analysis is slow (~5–15 min first run). Use `-noanalysis` + a custom importer script if you only need raw bytes.
- The `tools/ghidra-projects/` directory and `tools/ghidra/` are gitignored; don't commit them.
