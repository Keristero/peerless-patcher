---
name: shadow-constant-redirect
description: 'Patch a shared float constant in a compiled game exe without breaking unrelated callers. Use when a single constant in .rdata is read by both code you want to change (e.g. 3D projection, FOV) and code you must leave intact (e.g. UI bounds checks, comiss comparisons). Technique: write a shadow copy to an adjacent unused slot, then edit RIP-relative displacements in selected callers to point there.'
argument-hint: 'Describe the constant, its purpose, and which callers should change'
---

# Shadow-Constant Redirect

A static file-patch technique for compiled game executables (PE32+/ELF x64) where a single
float constant in the read-only data section is shared between callers with **different purposes**,
and you need to change it for only a subset of those callers.

## When to Use

- You found a float constant (e.g. aspect ratio, FOV, clip distance) you want to patch.
- Patching it in-place breaks something else (UI cutoff, frustum errors, rendering artifacts).
- You cannot use a runtime mod loader — you want a clean, static file patch.
- The exe is x64 and uses `movss xmm,[rip+disp32]` (or equivalent RIP-relative load) to read the constant.

**Do not use** if the value is runtime-written (BSS / heap). Verify the constant is in `.rdata`
(non-zero raw size, read-only static data) before proceeding.

---

## Step 1 — Locate the Constant

1. Identify the target float value (e.g. `9.0` → IEEE 754 bytes `00 00 10 41`).
2. Find it in the binary: scan `.rdata` for the byte pattern.
3. Record its **file offset** (`original_file_offset`) and compute its **virtual address**:
   ```
   original_va = IMAGE_BASE + RDATA_VADDR + (original_file_offset - RDATA_RAW)
   ```
   For ELF: use section `.rodata` and the corresponding load address.

---

## Step 2 — Find All Callers

Scan `.text` for every `movss xmm,[rip+disp32]` instruction whose displacement resolves to the
constant's VA. The opcode pattern is `F3 0F 10 xx` where `(ModRM & 0xC7) == 0x05` (instruction
length = 8 bytes).

```python
for i in range(TEXT_RAW, TEXT_RAW + TEXT_RAWSIZE - 8):
    if data[i]==0xF3 and data[i+1]==0x0F and data[i+2]==0x10:
        modrm = data[i+3]
        if (modrm & 0xC7) == 0x05:
            disp = struct.unpack_from('<i', data, i+4)[0]
            next_va = IMAGE_BASE + TEXT_VADDR + (i - TEXT_RAW) + 8
            if next_va + disp == original_va:
                callers.append(i)  # file offset of the movss
```

Also check `movss [rip+disp32]` stores (`F3 0F 11`) and `movaps`/`movss` with other ModRM forms
if the initial scan returns fewer hits than expected.

---

## Step 3 — Classify Callers

Dump 32–64 bytes around each caller. Look at the instruction **immediately following** the load:

| Following instruction | Likely role | Decision |
|----------------------|-------------|----------|
| `mulss` / `divss` | Projection / FOV multiply | **Redirect** |
| `minss` / `maxss` | Viewport dimension clamp | Usually redirect |
| `subss` / `addss` | Viewport / offset calculation | Usually redirect |
| `movss [mem]` | Stores value for later use | Inspect downstream readers |
| `comiss` / `ucomiss` | Bounds / frustum comparison | **Leave alone** (UI cutoff risk) |
| `xorps` | Zeroing or unrelated | Usually leave alone |

If the role is ambiguous, use binary search (Step 6) to confirm.

---

## Step 4 — Find a Shadow Slot

Look for 4 bytes of `00 00 00 00` immediately adjacent to the original constant in `.rdata`.
The bytes **directly before or after** are ideal — the displacement delta is minimal and easy
to audit.

Requirements for a valid shadow slot:
- 4 bytes of zeros at a known file offset.
- No other instruction references this offset (scan for it as a target VA, same as Step 2).
- Not the padding of a meaningful neighboring float (check surrounding values in context).

Record `shadow_file_offset` and compute:
```
shadow_va = IMAGE_BASE + RDATA_VADDR + (shadow_file_offset - RDATA_RAW)
delta     = shadow_file_offset - original_file_offset   # signed, usually ±4 or ±8
```

---

## Step 5 — Compute Redirect Displacements

For each caller selected for redirect (file offset `F`, instruction length = 8):

```python
next_va  = IMAGE_BASE + TEXT_VADDR + (F - TEXT_RAW) + 8
cur_disp = struct.unpack_from('<i', data, F + 4)[0]

# Sanity check
assert next_va + cur_disp == original_va

new_disp = cur_disp + delta

find_bytes    = list(struct.pack('<i', cur_disp))
replace_bytes = list(struct.pack('<i', new_disp))
```

These are the 4-byte little-endian int32 values used as `findBytes` / `replaceBytes` in the
patch profile, at site `offset = F + 4` (the displacement field, not the start of the opcode).

---

## Step 6 — Binary Search to Isolate UI-Affecting Callers

Start with **all** callers redirected. Confirm the desired effect is present (e.g. widescreen).
If there are side effects (UI cutoff, rendering corruption):

1. Split the redirect set in half. Remove the second half and test.
   - Side effect gone → culprit is in the removed half → add them back and split again.
   - Side effect present → culprit is in the retained half → split that.
2. Repeat until the minimum offending set is identified.
3. Remove those callers from the redirect list (leave them reading the original constant).

---

## Step 7 — Build the Patch Profile

The profile has N+1 sites: one site to write the patched value to the shadow slot, and one
site per redirected caller.

```json
{
  "type": "file-hex-edit",
  "name": "My Fix",
  "filePath": "Game.exe",
  "sites": [
    {
      "offset": 1234567,
      "findBytes": [0, 0, 0, 0],
      "fovDenominatorReplace": true,
      "notes": "Shadow slot at 0x12D407. Receives patched value."
    },
    {
      "offset": 987654,
      "findBytes": [129, 100, 63, 0],
      "replaceBytes": [137, 100, 63, 0],
      "notes": "Redirect projection mulss at 0xF1206."
    }
  ]
}
```

Rules:
- Site 1 always targets the shadow slot and uses a formula flag (`fovDenominatorReplace`,
  `aspectRatioReplace`, or `replaceBytes` with explicit value).
- Sites 2-N use explicit `replaceBytes` (not formula flags) — they are displacement edits.
- Every site must have either a formula flag or `replaceBytes` (validator requirement).

---

## Step 8 — Verify

After applying the patch, re-run a scan of the binary to confirm:
- Shadow slot contains the expected patched float bytes.
- Each redirected caller's displacement resolves to `shadow_va`.
- Non-redirected callers still resolve to `original_va`.

```python
for caller_file_offset in redirected:
    disp = struct.unpack_from('<i', patched_data, caller_file_offset + 4)[0]
    next_va = IMAGE_BASE + TEXT_VADDR + (caller_file_offset - TEXT_RAW) + 8
    assert next_va + disp == shadow_va

for caller_file_offset in left_alone:
    disp = struct.unpack_from('<i', patched_data, caller_file_offset + 4)[0]
    next_va = IMAGE_BASE + TEXT_VADDR + (caller_file_offset - TEXT_RAW) + 8
    assert next_va + disp == original_va
```

---

## Reference: BBS Ultrawide Fix

Game: Kingdom Hearts Birth by Sleep Final Mix (AppID 2552430)
Constant: `9.0` float (`00 00 10 41`) at file offset `0x6337B4`
Shadow slot: `0x00000000` at file offset `0x6337BC` (delta = +8)
Total callers: 9 — 6 redirected, 3 left alone
Left alone: two `comiss` UI bounds-checks, one `xorps`
Result: widescreen with no UI cutoff

See [`profiles/assets/2552430/README.md`](../../2552430/README.md) for the full caller
classification table and displacement arithmetic for each site.
