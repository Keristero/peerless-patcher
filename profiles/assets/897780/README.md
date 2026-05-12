# 897780 — KINGDOM HEARTS III — Research Notes

## PE Layout — KINGDOM HEARTS III.exe (150,713,896 bytes)

| Field | Value |
|-------|-------|
| IMAGE_BASE | `0x140000000` |
| `.text` RAW | `0x600` size `0x5C0B800` VA `0x1000` |
| `.rdata` RAW | `0x5C0BE00` VA derived from RAW |
| `.pdata` RAW | `0x87C5400` |

VA → file offset (`.text`):  `foff = 0x600 + (VA − 0x140001000)`  
VA → file offset (`.rdata`): `foff = 0x5C0BE00 + (VA − (IMAGE_BASE + 0x5C0C000))`  
Shortcut for `.rdata` constants: `VA = 0x140000000 + foff + (0x5C0C000 − 0x5C0BE00)` = `foff + 0x13FFE200` (approx — verify per section)

---

## Confirmed

| Mechanism | Detail | Status |
|-----------|--------|--------|
| Viewport pillarbox float | rdata foff=`0x065675C8` (VA `0x1465687C8`), value `1.7777`. Controls the D3D viewport pillarbox calculation for all renderers. Removing this site kills ultrawide entirely. | ✅ Confirmed |
| Camera struct +0x408 / +0x428 writes | Five code-immediate `movss [reg+0x408/0x428], 1.7778f` sites in .text initialize camera AR at object creation. Patching these alone makes splash screens ultrawide. | ✅ Confirmed |
| Splash screens use camera struct AR | Splash/intro cutscenes go ultrawide from camera struct writes alone (even without viewport float). | ✅ Confirmed |
| test byte [reg+0x94],1 + jne branches | 56 sites in 0x0423–0x0441xxxx range. In KH3 these are **ENABLE branches for the AR constraint** (not guards). NOPping them removes the constraint and forces 16:9 native — opposite of 0.2 behaviour. | ✅ Confirmed (refuted as fix) |
| test byte [reg+0x94],1 mixed polarity | KH3 has both `jz` (74) and `jne` (75) variants unlike 0.2 which was uniformly `jne`. Mixed NOP kills ultrawide entirely. | ✅ Confirmed |

---

## Speculation

| Hypothesis | Confidence | Basis | Next test |
|------------|-----------|-------|-----------|
| Viewport float (site 6) alone causes UI stretching | Medium | Site 6 is read by 8 callers including functions that compute UI element centering offsets (the `subss xmm0, [rel site6]` + `comiss` + letterbox pattern) | Apply only "Viewport Float" patch, disable "Camera Structs" |
| Camera struct writes alone cause UI stretching | Low | Struct fields are read by AR-dependent systems, but the pillarbox calc that shifts UI elements is driven by the viewport float | Apply only "Camera Structs" patch, disable "Viewport Float" |
| UI stretching is entirely from viewport float; camera structs only affect 3D | Medium | The 8 site-6 callers include clear letterbox/centering math; struct writes are more isolated to 3D camera | Follows from viewport-only test |
| Shadow-constant redirect on site 6 can fix UI | Medium | Same technique worked for DDD's FOV constant; site 6 callers include both UI-centering and 3D viewport code so selective redirect may be possible | First confirm which callers cause UI stretch, then redirect only 3D callers |

---

## Site 6 Callers (viewport float at foff=`0x065675C8`)

8 RIP-relative loads/subs of the 1.7777 float found in .text:

| foff | VA | Pattern | Notes |
|------|----|---------|-------|
| `0x00ECB8FD` | `0x140ECC2FD` | `movss xmm4,[rel]` → `comiss xmm2,xmm4` → `jnc` | Pillarbox check — computes viewport offset if aspect < 1.7777 |
| `0x0117905D` | `0x14117 9A5D` | `subss xmm0,[rel]` → `comiss xmm0,xmm6` → `jnc` | Letterbox calc A |
| `0x011790AA` | `0x141179AAA` | `subss xmm0,[rel]` → `comiss xmm0,xmm6` → `jna` | Letterbox calc B |
| `0x011790D8` | `0x141179AD8` | `mulss xmm0,[rel]` | Width multiply in letterbox path |
| `0x01722D92` | `0x141723792` | `movss xmm5,[rel]` → `subss xmm1,xmm5` → `andps` → `comiss` | Centering offset calc |
| `0x01725973` | `0x141726373` | `movss xmm7,[rel]` → used in loop | Centering offset calc (loop variant) |
| `0x038F866F` | `0x1438F906F` | `movss xmm6,[rel]` → `subss xmm0,xmm6` → `comiss xmm0,xmm9` → `jnc` | Viewport centering |
| `0x0411462B` | `0x14411502B` | `movss xmm7,[rel]` → `subss xmm0,xmm7` → `comiss xmm0,xmm9` → `jnc` | Viewport centering (2nd renderer) |

All 8 callers follow the same pattern: subtract 1.7777 from actual AR, compare to zero, branch into a centering/letterbox calculation. These are the UI centering functions — they compute how much to offset UI elements when the screen is wider than 16:9. When site 6 is patched to ultrawide AR, the subtraction result is ≈0, the branch skips the offset calc, and UI elements are placed at full-width coordinates → stretching.
