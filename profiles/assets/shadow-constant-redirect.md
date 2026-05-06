# Shadow-Constant Redirect — Patch Research Skill

## When to use this technique

Use when a game exe has a single float constant that is read by multiple instructions
serving **different purposes** (e.g. 3D projection AND UI layout), and you need to
change it for only a subset of those readers.

Simpler alternatives (patching the constant in-place) will break the undesired callers.
Runtime-only fixes (Lua mods, memory patching) work but require a mod loader.
This technique achieves a clean static file patch.

---

## Step-by-step process

### 1. Find the constant and its callers

Scan the `.text` section for all `movss xmm,[rip+disp32]` instructions
(`F3 0F 10 xx` where ModRM byte & `0xC7 == 0x05`) whose displacement resolves
to the target constant's VA.

```python
for i in range(TEXT_RAW, TEXT_RAW + TEXT_RAWSIZE - 8):
    if data[i]==0xF3 and data[i+1]==0x0F and data[i+2]==0x10:
        modrm = data[i+3]
        if (modrm & 0xC7) == 0x05:
            disp = struct.unpack_from('<i', data, i+4)[0]
            next_va = IMAGE_BASE + TEXT_VADDR + (i - TEXT_RAW) + 8
            if next_va + disp == target_va:
                # hit at file offset i
```

Dump 32–64 bytes of code context around each hit to identify the instruction
that follows (what the loaded value is used for).

### 2. Classify callers

For each caller, look at the instruction immediately following the load:

| Following instruction | Likely role |
|----------------------|-------------|
| `mulss` | Projection matrix multiply — change this |
| `minss` / `maxss` | Viewport dimension clamp — usually change |
| `subss` / `addss` | Viewport calculation — usually change |
| `movss [mem]` | Stores value — check what reads it next |
| `comiss` / `ucomiss` | Bounds/frustum comparison — **leave alone** (UI cutoff) |
| `xorps` | Zeroing / unrelated — usually leave alone |

### 3. Find a shadow slot

Look for 4 bytes of zeros (`00 00 00 00`) adjacent to the original constant
in the same data block (`.rdata`). The bytes immediately before or after the
constant are ideal — the displacement delta stays small and easy to reason about.

Verify the candidate is genuinely unused: check it is not a valid float for
any neighboring value, and that no other instruction references it directly.

### 4. Compute redirect displacements

For each caller at instruction file offset `F` (instruction length always 8):

```python
next_va  = IMAGE_BASE + TEXT_VADDR + (F - TEXT_RAW) + 8
cur_disp = struct.unpack_from('<i', data, F + 4)[0]
# Verify cur_disp points to original constant
assert next_va + cur_disp == original_va

delta    = shadow_file_offset - original_file_offset
new_disp = cur_disp + delta
find_bytes    = list(struct.pack('<i', cur_disp))
replace_bytes = list(struct.pack('<i', new_disp))
```

These `find_bytes` / `replace_bytes` are the profile JSON site entries.

### 5. Build the profile sites

```json
[
  {
    "offset": <shadow_file_offset>,
    "findBytes": [0, 0, 0, 0],
    "fovDenominatorReplace": true
  },
  {
    "offset": <F + 4>,
    "findBytes": <find_bytes>,
    "replaceBytes": <replace_bytes>
  }
  ...
]
```

Site 1 always writes the patched constant to the shadow slot.
Sites 2-N redirect each selected caller.

### 6. Binary search to isolate UI-affecting callers

Start with ALL callers redirected (maximum working set).
Confirm this reproduces the desired effect (widescreen, but with side-effects).

Then iteratively remove half the non-obvious sites and test:
- Effect present → removed sites were not needed
- Effect absent → one of the removed sites was required — add them back and split differently

For side-effects (UI cutoff etc.): start from the working set and remove half,
test whether the side-effect is gone. Repeat until the minimum set is found.

---

## Profile JSON requirements

The `PatchSite` model requires `replaceBytes` at the site level (not just at the patch level)
for redirect sites. The `ProfileLoader` validates that every site in a multi-site patch has
either a formula flag (`aspectRatioReplace`, `fovDenominatorReplace`) or explicit `replaceBytes`.

---

## Reference: BBS ultrawide fix (2552430.json)

Constant: `9.0` float at file offset `0x6337B4`
Shadow slot: `0x0` at file offset `0x6337BC` (delta = +8)
Result: 6 of 9 callers redirected → widescreen without UI cutoff

Excluded callers (left reading `9.0`):
- `0x290DC3` `comiss` bounds-check — causes top/bottom UI cutoff
- `0x2F0825` `comiss` bounds-check — causes top/bottom UI cutoff
- `0x52C9DE` `xorps`/load — no effect on widescreen
