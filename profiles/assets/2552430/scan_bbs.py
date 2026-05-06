#!/usr/bin/env python3
"""
scan_bbs.py — Research tool for KINGDOM HEARTS Birth by Sleep FINAL MIX.exe
==============================================================================

Technique: Shadow-Constant Redirect
  BBS has a single 9.0 float at 0x6337B4 read by 9 instructions covering both
  3D projection AND UI bounds/clamping.  Patching it in-place gives widescreen
  but cuts off the UI top/bottom.  Instead we:
    1. Write the patched value to a shadow slot (0x6337BC, currently 0x00000000).
    2. Redirect each movss xmm,[rip+disp] instruction's displacement to point at
       the shadow slot instead of the original.

Usage:
    python3 scan_bbs.py [--exe /path/to/exe] [--width 3440] [--height 1440]

Subcommands (run interactively — just edit TASK below):
    'callers'   — Find all RIP-relative callers of a float constant and print
                  the site table for profiles/2552430.json.
    'context'   — Dump code context around each caller for manual analysis.
    'verify'    — Check current patch state against the expected values.
    'pe'        — Print PE section layout of the exe.
"""

import struct, sys, os

# ── Configuration ──────────────────────────────────────────────────────────────
EXE_DEFAULT = (
    "/run/media/system/Samsung980Pro/SteamLibrary/steamapps/common/"
    "KINGDOM HEARTS -HD 1.5+2.5 ReMIX-/"
    "KINGDOM HEARTS Birth by Sleep FINAL MIX.exe"
)

# PE layout (from parse — re-run 'pe' task if the exe is updated)
IMAGE_BASE   = 0x140000000
TEXT_RAW     = 0x400;     TEXT_VADDR  = 0x1000;   TEXT_RAWSIZE  = 0x62E200
RDATA_RAW    = 0x62FA00;  RDATA_VADDR = 0x632000; RDATA_RAWSIZE = 0x1DC000
DATA_RAW     = 0x80BA00;  DATA_VADDR  = 0x80E000; DATA_RAWSIZE  = 0x30600

# Key offsets
ORIG_CONST_FILE   = 0x6337B4   # 9.0 float — original constant
SHADOW_CONST_FILE = 0x6337BC   # 0.0 currently — shadow slot for patched value

# ── Helpers ────────────────────────────────────────────────────────────────────

def load_exe(path=None):
    p = path or EXE_DEFAULT
    if not os.path.exists(p):
        sys.exit(f"ERROR: exe not found at {p!r}")
    return open(p, 'rb').read()

def text_va(file_off):
    return IMAGE_BASE + TEXT_VADDR + (file_off - TEXT_RAW)

def rdata_va(file_off):
    return IMAGE_BASE + RDATA_VADDR + (file_off - RDATA_RAW)

def data_va(file_off):
    return IMAGE_BASE + DATA_VADDR + (file_off - DATA_RAW)

def instr_next_va(instr_file_off, instr_len=8):
    """VA of the instruction following a text-section instruction."""
    return IMAGE_BASE + TEXT_VADDR + (instr_file_off - TEXT_RAW) + instr_len

def compute_new_disp(instr_file_off, orig_file, new_file, instr_len=8):
    data = load_exe()
    cur_disp = struct.unpack_from('<i', data, instr_file_off + 4)[0]
    nv = instr_next_va(instr_file_off, instr_len)
    # Verify current target matches orig_file
    cur_target_va = nv + cur_disp
    expected_va   = rdata_va(orig_file)
    assert cur_target_va == expected_va, (
        f"0x{instr_file_off:08X}: expected target VA 0x{expected_va:016X}, "
        f"got 0x{cur_target_va:016X}"
    )
    delta    = new_file - orig_file
    new_disp = cur_disp + delta
    return struct.pack('<i', cur_disp), struct.pack('<i', new_disp)

# ── Tasks ──────────────────────────────────────────────────────────────────────

def task_pe(exe_path=None):
    """Print PE section table."""
    data = load_exe(exe_path)
    pe_off = struct.unpack_from('<I', data, 0x3C)[0]
    num_sec = struct.unpack_from('<H', data, pe_off + 6)[0]
    opt_sz  = struct.unpack_from('<H', data, pe_off + 20)[0]
    opt_off = pe_off + 24
    magic   = struct.unpack_from('<H', data, opt_off)[0]
    ib = struct.unpack_from('<Q', data, opt_off+24)[0] if magic==0x20B else struct.unpack_from('<I', data, opt_off+28)[0]
    print(f"Image base: 0x{ib:016X}  PE{'32+' if magic==0x20B else '32'}")
    sec_off = opt_off + opt_sz
    print(f"\n{'Name':<12} {'VirtAddr':>10} {'VirtSize':>10} {'RawOff':>10} {'RawSize':>10}")
    for i in range(num_sec):
        s = sec_off + i * 40
        name = data[s:s+8].rstrip(b'\x00').decode('ascii', errors='replace')
        vs   = struct.unpack_from('<I', data, s+8)[0]
        va   = struct.unpack_from('<I', data, s+12)[0]
        rs   = struct.unpack_from('<I', data, s+16)[0]
        ro   = struct.unpack_from('<I', data, s+20)[0]
        print(f"{name:<12} {va:>10X} {vs:>10X} {ro:>10X} {rs:>10X}")


def task_callers(target_file=ORIG_CONST_FILE, exe_path=None, screen_width=3440, screen_height=1440):
    """
    Find all RIP-relative movss instructions in .text that load from target_file,
    and print the full sites block for the profile JSON.
    """
    data = load_exe(exe_path)
    target_va = rdata_va(target_file)
    shadow_va  = rdata_va(SHADOW_CONST_FILE)

    hits = []
    for i in range(TEXT_RAW, TEXT_RAW + TEXT_RAWSIZE - 8):
        if data[i] == 0xF3 and data[i+1] == 0x0F and data[i+2] in (0x10, 0x28):
            modrm = data[i+3]
            if (modrm & 0xC7) == 0x05:
                disp = struct.unpack_from('<i', data, i+4)[0]
                nv   = instr_next_va(i, 8)
                if nv + disp == target_va:
                    hits.append(i)

    # Deduplicate (the 0F28 / F30F10 overlap)
    seen = set()
    unique = []
    for h in hits:
        if h not in seen and h-1 not in seen:
            seen.add(h)
            unique.append(h)

    fov_denom = 16.0 * screen_height / screen_width
    fov_bytes = struct.pack('<f', fov_denom)

    print(f"// Target constant: file=0x{target_file:08X}  VA=0x{target_va:016X}")
    print(f"// Shadow slot:     file=0x{SHADOW_CONST_FILE:08X}  VA=0x{shadow_va:016X}")
    print(f"// FOV denominator for {screen_width}x{screen_height}: {fov_denom:.6f}  bytes={list(fov_bytes)}")
    print(f"// Found {len(unique)} unique caller(s)\n")

    # Site 1: shadow constant
    shadow_cur = data[SHADOW_CONST_FILE:SHADOW_CONST_FILE+4]
    print(f'        {{"offset": {SHADOW_CONST_FILE},')
    print(f'         "findBytes": {list(shadow_cur)},')
    print(f'         "fovDenominatorReplace": true,')
    print(f'         "notes": "Shadow FOV constant at 0x{SHADOW_CONST_FILE:08X}."}}')

    for h in unique:
        disp_off = h + 4
        cur_bytes = data[disp_off:disp_off+4]
        cur_disp  = struct.unpack_from('<i', cur_bytes)[0]
        new_disp  = cur_disp + (SHADOW_CONST_FILE - target_file)
        new_bytes = struct.pack('<i', new_disp)
        print(f',\n        {{"offset": {disp_off},')
        print(f'         "findBytes": {list(cur_bytes)},')
        print(f'         "replaceBytes": {list(new_bytes)},')
        print(f'         "notes": "Redirect movss at 0x{h:08X} from 0x{target_file:08X} to 0x{SHADOW_CONST_FILE:08X}."}}')


def task_context(target_file=ORIG_CONST_FILE, exe_path=None):
    """Dump 48-byte code context around each caller."""
    data = load_exe(exe_path)
    target_va = rdata_va(target_file)

    hits = []
    for i in range(TEXT_RAW, TEXT_RAW + TEXT_RAWSIZE - 8):
        if data[i] == 0xF3 and data[i+1] == 0x0F and data[i+2] in (0x10, 0x28):
            modrm = data[i+3]
            if (modrm & 0xC7) == 0x05:
                disp = struct.unpack_from('<i', data, i+4)[0]
                nv   = instr_next_va(i, 8)
                if nv + disp == target_va:
                    hits.append(i)
    seen = set()
    unique = []
    for h in hits:
        if h not in seen and h-1 not in seen:
            seen.add(h)
            unique.append(h)

    for h in unique:
        va = text_va(h)
        print(f"\n=== 0x{h:08X}  VA=0x{va:016X} ===")
        start = max(TEXT_RAW, h - 32)
        end   = min(len(data), h + 64)
        chunk = data[start:end]
        for j in range(0, len(chunk), 16):
            off = start + j
            row = chunk[j:j+16]
            mark = " <--" if start+j <= h < start+j+16 else ""
            print(f"  0x{off:08X}: {row.hex()}{mark}")


def task_verify(screen_width=3440, screen_height=1440, exe_path=None):
    """Check current patch state of the BBS exe."""
    data = load_exe(exe_path)

    fov_bytes = struct.pack('<f', 16.0 * screen_height / screen_width)

    print("=== BBS patch state ===\n")
    # Original constant
    orig_at = data[ORIG_CONST_FILE:ORIG_CONST_FILE+4]
    orig_f  = struct.unpack('<f', orig_at)[0]
    print(f"Original 0x{ORIG_CONST_FILE:08X}: {list(orig_at)} = {orig_f:.4f}  "
          f"{'OK (9.0)' if abs(orig_f - 9.0) < 0.001 else 'CHANGED'}")

    # Shadow constant
    shadow_at = data[SHADOW_CONST_FILE:SHADOW_CONST_FILE+4]
    shadow_f  = struct.unpack('<f', shadow_at)[0]
    if shadow_at == bytes([0, 0, 0, 0]):
        state = "UNPATCHED (0.0)"
    elif shadow_at == fov_bytes:
        state = f"PATCHED ({shadow_f:.4f} = 16*{screen_height}/{screen_width})"
    else:
        state = f"UNKNOWN ({shadow_f:.4f})"
    print(f"Shadow   0x{SHADOW_CONST_FILE:08X}: {list(shadow_at)}  {state}")

    # Confirmed redirect sites (✓ = needed for widescreen, ✗ = leave at 9.0 to preserve UI)
    # Determined by binary search testing — excluded sites cause UI cutoff or have no effect.
    sites = [
        (2354479,  [129, 100, 63, 0], [137, 100, 63, 0], "0x23ED2B  ✓ mulss projection"),
        (2441709,  [195,  15, 62, 0], [203,  15, 62, 0], "0x2541E9  ✓ stack store"),
        (2553386,  [134,  91, 60, 0], [142,  91, 60, 0], "0x26F626  ✓ subss viewport calc"),
        (2690503,  [233,  67, 58, 0], [241,  67, 58, 0], "0x290DC3  ✗ comiss bounds-check (UI cutoff if redirected)"),
        (3082281,  [135,  73, 52, 0], [143,  73, 52, 0], "0x2F0825  ✗ comiss bounds-check (UI cutoff if redirected)"),
        (5129501,  [147,  12, 21, 0], [155,  12, 21, 0], "0x4E4519  ✓ minss viewport clamp"),
        (5163396,  [ 44, 136, 20, 0], [ 52, 136, 20, 0], "0x4EC980  ✓ mulss projection"),
        (5166197,  [ 59, 125, 20, 0], [ 67, 125, 20, 0], "0x4ED471  ✓ mulss projection"),
        (5425634,  [206, 135, 16, 0], [214, 135, 16, 0], "0x52C9DE  ✗ xorps/load (no effect on widescreen)"),
    ]
    print()
    for (off, find, rep, label) in sites:
        at = list(data[off:off+4])
        if at == rep:
            st = "PATCHED"
        elif at == find:
            st = "ORIGINAL"
        else:
            st = f"UNKNOWN {at}"
        print(f"  disp @ {off} ({label}): {st}")


# ── Entry point ────────────────────────────────────────────────────────────────

TASK = 'verify'   # change to: 'pe', 'callers', 'context', 'verify'

if __name__ == '__main__':
    if TASK == 'pe':
        task_pe()
    elif TASK == 'callers':
        task_callers()
    elif TASK == 'context':
        task_context()
    elif TASK == 'verify':
        task_verify()
    else:
        print(f"Unknown task {TASK!r}. Set TASK to: pe, callers, context, verify")
