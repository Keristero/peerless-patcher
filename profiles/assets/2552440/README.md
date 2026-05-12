# 2552440 — KINGDOM HEARTS HD 2.8 Final Chapter Prologue — Research Tools

Helper scripts for finding and verifying patch offsets in the DDD executable.

## Scripts

| Script | Purpose |
|--------|---------|
| `scan_ddd.py` | DDD — find all RIP-relative callers of the 9.0 FOV constant, compute shadow-constant redirect sites, verify patch state |

## Technique: Shadow-Constant Redirect (DDD)

`KINGDOM HEARTS Dream Drop Distance.exe` (11,581,712 bytes) uses a single `9.0` float constant
at file offset `0x783bb8` (VA `0x1407855b8`) that is read by **20** separate instructions.
These span 3D projection, UI bounds-checks, and tile/grid computations. Patching in-place would
break UI layout, so the shadow-constant redirect technique is used.

### PE Layout

| Field           | Value                |
|-----------------|----------------------|
| IMAGE_BASE      | `0x140000000`        |
| `.text` VA      | `0x1000`             |
| `.text` RawOff  | `0x400`              |
| `.rdata` VA     | `0x782000`           |
| `.rdata` RawOff | `0x780600`           |

VA → file offset (`.rdata`): `foff = 0x780600 + (VA − 0x140782000)`  
VA → file offset (`.text`):  `foff = 0x400   + (VA − 0x140001000)`

### Constants

| Constant | Value   | File offset  | VA               | Note                                    |
|----------|---------|-------------|------------------|-----------------------------------------|
| FOV denom| `9.0`   | `0x783bb8`  | `0x1407855b8`    | Part of table [0.15,1.5,0,3.09,**9**,25,225,900] |
| Shadow   | `0.0`   | `0x783bb0`  | `0x1407855b0`    | 4-byte zero, delta=−8 from original     |
| Aspect   | `1.7777`| `0x838118`  | `0x140839b18`    | In resolution config table [ptr,ptr,**1.7777**,720,1080,1440,...]; accessed via `movups xmm11,[rip]` at VA `0x1406febdb` loading 16 bytes |

### Confirmed caller classification (DDD — 9.0 at `0x1407855b8`, 20 total)

| File offset  | Instruction VA    | xmm reg | Following opcode               | Decision                                       |
|-------------|-------------------|---------|--------------------------------|------------------------------------------------|
| `0x000f0d6f` | `0x1400f196f`    | xmm1    | `comiss xmm1,xmm0`             | **LEAVE ALONE** — UI bounds-check              |
| `0x000f118f` | `0x1400f1d8f`    | xmm1    | `comiss xmm1,xmm0`             | **LEAVE ALONE** — UI bounds-check              |
| `0x00145af1` | `0x1401466f1`    | xmm2    | `xorps xmm0,xmm0` then `mulss xmm1,xmm2` then `cvttss2si` | **LEAVE ALONE** — tile/grid index calc (9.0 as row count) |
| `0x0020de10` | `0x14020ea10`    | xmm0    | `movss [rbp-1],xmm0` (stack store) then call | LEAVE ALONE (unclear, deferred load) |
| `0x0030c2b2` | `0x14030ceb2`    | xmm3    | `movaps xmm0,xmm2` … `mulss xmm0,xmm3` | **REDIRECT** — secondary projection multiply   |
| `0x0031997b` | `0x14031a57b`    | xmm7    | `lea rsi,…` integer ops        | LEAVE ALONE (unclear, deferred load)           |
| `0x0032d272` | `0x14032de72`    | xmm3    | `movaps xmm0,xmm2` … `mulss xmm0,xmm3` | **REDIRECT** — secondary projection multiply   |
| `0x003471ee` | `0x140347dee`    | xmm6    | integer store + call           | LEAVE ALONE (unclear, deferred load)           |
| `0x0039c32f` | `0x14039cf2f`    | xmm7    | `lea rbx,…` integer ops + loop | LEAVE ALONE (unclear, deferred load)           |
| `0x003a08b5` | `0x1403a14b5`    | xmm2    | `inc eax` … `divss xmm2,xmm0` → tail call | LEAVE ALONE (divss pattern, unclear purpose) |
| `0x003a2a9f` | `0x1403a369f`    | xmm6    | `lea r14d,…` integer ops + loop | LEAVE ALONE (unclear, deferred load)          |
| `0x003a7900` | `0x1403a8500`    | xmm2    | `movsx ecx,…` → `divss xmm2,xmm0` → tail call | LEAVE ALONE (divss pattern, unclear purpose) |
| `0x003ab669` | `0x1403ac269`    | xmm2    | `movd xmm0,eax; cvtdq2ps; divss xmm2,xmm0` → tail call | LEAVE ALONE (divss pattern, unclear purpose) |
| `0x0050bef8` | `0x14050caf8`    | xmm2    | `xor r9d,r9d` … `divss xmm2,xmm0` → call + ret | LEAVE ALONE (divss pattern, unclear purpose) |
| `0x0050e13f` | `0x14050ed3f`    | xmm2    | same divss pattern             | LEAVE ALONE (divss pattern, unclear purpose)   |
| `0x0051121f` | `0x140511e1f`    | xmm2    | same divss pattern             | LEAVE ALONE (divss pattern, unclear purpose)   |
| `0x00652589` | `0x140653189`    | xmm4    | `minss xmm4,[rdx+0x28]`        | **REDIRECT** — viewport clamp                  |
| `0x0065a9f0` | `0x14065b5f0`    | xmm4    | `mulss xmm2,xmm4`              | **REDIRECT** — primary projection multiply     |
| `0x0065b4e1` | `0x14065c0e1`    | xmm4    | `mulss xmm2,xmm4`              | **REDIRECT** — primary projection multiply     |
| `0x006765ae` | `0x1406771ae`    | xmm4    | `xorps xmm5,xmm5`              | **LEAVE ALONE** — xorps, unrelated             |

**Note on divss callers (0x3a08b5, 0x3a7900, 0x3ab669, 0x50bef8, 0x50e13f, 0x51121f):**  
These six callers all load 9.0 then compute `9.0 / float(register)`, multiply the result by
another .rdata constant, then tail-call (or call+ret) another function. Their purpose is
uncertain — they could be computing screen-space radii or rendering block sizes. They were
left alone in the initial patch. If in-game black bars persist in specific contexts (e.g.
pre-rendered sequences), try adding these as redirects using binary search.

### Shadow slot verification

The 4 bytes at `0x783bb0` are `00 00 00 00` (unused zero slot, no callers reference it).
The original 9.0 at `0x783bb8` is `00 00 10 41`. The shadow slot is part of the same data
block, sitting 8 bytes before the 9.0:

```
0x783ba0: 0e 00 00 00 35 fa 8e 3c 9a 99 19 3e 00 00 c0 3f
0x783bb0: 00 00 00 00   ← shadow slot (delta = -8)
0x783bb4: 00 00 46 40   ← 3.09375
0x783bb8: 00 00 10 41   ← 9.0 (original FOV denominator)
0x783bbc: 00 00 c8 41   ← 25.0
0x783bc0: 00 00 61 43   ← 225.0
0x783bc4: 00 00 61 44   ← 900.0
```

### Displacement arithmetic

For all redirect sites, delta = `0x783bb0 − 0x783bb8 = −8` (shadow is 8 bytes before original).
New displacement = `old_displacement + (−8)`. Since delta is only 8 bytes, only the lowest byte
of the little-endian int32 displacement changes (no borrow from upper bytes in all 5 cases).

| Instruction foff | Disp field foff | findBytes (disp LE)     | replaceBytes              |
|-----------------|-----------------|------------------------|---------------------------|
| `0x652589`       | `0x65258d`       | `[39, 36, 19, 0]`      | `[31, 36, 19, 0]`         |
| `0x65a9f0`       | `0x65a9f4`       | `[192, 159, 18, 0]`    | `[184, 159, 18, 0]`       |
| `0x65b4e1`       | `0x65b4e5`       | `[207, 148, 18, 0]`    | `[199, 148, 18, 0]`       |
| `0x30c2b2`       | `0x30c2b6`       | `[254, 134, 71, 0]`    | `[246, 134, 71, 0]`       |
| `0x32d272`       | `0x32d276`       | `[62, 119, 69, 0]`     | `[54, 119, 69, 0]`        |

### Notes on primary projection callers (0x65xxxx cluster)

Both `0x65a9f0` and `0x65b4e1` load 9.0 into xmm4, then use xmm4 in:
1. `mulss xmm2,xmm4` — scale projection value up by 9.0
2. A `comiss xmm2,xmm0` comparison
3. `divss xmm0,xmm4` — divide a threshold value by 9.0 (conditional branch)

Because both the mulss and divss in each function read from the same xmm4 (our redirect target),
replacing 9.0 → `16×H/W` affects both operations consistently, preserving the intended ratio
relationship within the function.

### Aspect ratio constant

The 1.7777 float at `0x838118` is part of a 16-byte resolution config record:
`[1.7777 (4B)][720.0 (4B)][1080.0 (4B)][1440.0 (4B)]`. This record is prefixed by two 8-byte
absolute VAs (struct pointers). The record is read by a `movups xmm11,[rip+0x13af35]` at VA
`0x1406febdb` (foff `0x6fdfdb`), loading all 16 bytes at once.

The aspect ratio site is **not included** in the initial patch (following the BBS precedent where
the FOV denominator redirect alone was sufficient). If the HUD/viewport aspect is wrong after
applying the FOV sites, add:
```json
{
  "offset": 8618264,
  "findBytes": [172, 139, 227, 63],
  "aspectRatioReplace": true,
  "notes": "Aspect ratio 1.7777 in resolution config table at 0x140839b18 (foff=0x838118)."
}
```
