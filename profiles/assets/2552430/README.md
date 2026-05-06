# 2552430 — KINGDOM HEARTS HD 1.5+2.5 ReMIX — Research Tools

Helper scripts for finding and verifying patch offsets in the KH1.5+2.5 executables.

## Scripts

| Script | Purpose |
|--------|---------|
| `scan_bbs.py` | BBS — find all RIP-relative callers of a float constant, compute shadow-constant redirect sites, verify patch state |

## Technique: Shadow-Constant Redirect (BBS)

BBS uses a single `9.0` float constant at `0x6337B4` that is read by 9 separate instructions
covering both 3D projection and UI bounds/clamping logic. Patching that constant in-place
(the simple approach) widens the 3D view but also affects UI layout, causing top/bottom cutoff.

The shadow-constant redirect technique avoids this:

1. **Choose a shadow slot** — an unused (zero) 4-byte region adjacent to the original constant.
   `0x6337BC` sits in the same data block, 8 bytes after the original.
2. **Write the patched value to the shadow slot** — `16 * H / W` for ultrawide.
3. **Redirect callers selectively** — change the 32-bit RIP-relative displacement in each
   `movss xmm,[rip+disp32]` instruction so it points to the shadow slot instead of the original.
   Leave callers that affect UI layout reading the original `9.0`.

### Confirmed caller classification (BBS)

| File offset  | Instruction addr | Purpose                               | Redirect?                        |
|--------------|-----------------|---------------------------------------|----------------------------------|
| disp@2354479 | 0x23ED2B        | `mulss xmm0,xmm1` projection multiply | yes                              |
| disp@2441709 | 0x2541E9        | stores FOV value to stack             | yes                              |
| disp@2553386 | 0x26F626        | `subss xmm6,xmm0` viewport calc       | yes                              |
| disp@2690503 | 0x290DC3        | `comiss` bounds-check                 | **no** — UI cutoff if changed    |
| disp@3082281 | 0x2F0825        | `comiss` bounds-check                 | **no** — UI cutoff if changed    |
| disp@5129501 | 0x4E4519        | `minss` viewport clamp                | yes                              |
| disp@5163396 | 0x4EC980        | `mulss xmm2,xmm4` projection multiply | yes                              |
| disp@5166197 | 0x4ED471        | `mulss xmm2,xmm4` projection multiply | yes                              |
| disp@5425634 | 0x52C9DE        | `xorps` / load                        | **no** — no effect on widescreen |

### Displacement arithmetic

For a RIP-relative load at file offset `F` with instruction length `L=8` (`F3 0F 10 xx imm32`):

```
next_VA  = IMAGE_BASE + (F - TEXT_RAW + TEXT_VADDR) + L
cur_disp = int32 at file offset F+4
new_disp = cur_disp + (shadow_file_offset - original_file_offset)  # = cur_disp + 8
```

The `findBytes` / `replaceBytes` for a redirect site are the 4 raw displacement bytes
(little-endian int32), **not** float values.
