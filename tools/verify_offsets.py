#!/usr/bin/env python3
"""Verify the Il2CppClass / Il2CppMethodInfo field offsets of a GameAssembly.dll.

Il2CppInterop picks its struct handlers from the Unity version string reported by the
player. A game that ships a patched il2cpp keeps that string while changing the layout,
so every field past the insertion point silently reads garbage. This script reads the
real offsets out of the binary instead of trusting the version: each probe export below
touches exactly one field, so the first `[rcx + N]` in its body is that field's offset.

Usage:
    python verify_offsets.py path/to/GameAssembly.dll

Requires: pefile, capstone  (pip install pefile capstone)
"""

import argparse
import sys

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

# (export, field, stock offset, counts toward the verdict)
# `flags` sits before the insertion point, so it must stay at +0 on both layouts and
# only serves as a sanity check.
PROBES = [
    ("il2cpp_class_get_flags", "Il2CppClass.flags", 0x114, False),
    ("il2cpp_class_get_rank", "Il2CppClass.rank", 0x12E, True),
    ("il2cpp_class_has_references", "Il2CppClass.bitfield1 (size_inited)", 0x133, True),
    ("il2cpp_method_get_param_count", "Il2CppMethodInfo.parameters_count", 0x52, True),
    ("il2cpp_method_is_generic", "Il2CppMethodInfo.bitfield0", 0x53, True),
]

MAX_HOPS = 4
SCAN_BYTES = 0x60


def follow_jumps(md, pe, image_base, rva, code_size):
    """Exports are often `jmp rel32` thunks; return the real body RVA."""
    for _ in range(MAX_HOPS):
        data = pe.get_data(rva, 16)
        if not data:
            return rva
        insns = list(md.disasm(data, image_base + rva, count=1))
        if not insns:
            return rva
        insn = insns[0]
        if insn.mnemonic != "jmp" or not insn.op_str.startswith("0x"):
            return rva
        target = int(insn.op_str, 16)
        rva = target - image_base
        if rva <= 0 or rva >= code_size:
            return rva
    return rva


def first_rcx_offset(md, pe, image_base, rva):
    data = pe.get_data(rva, SCAN_BYTES)
    for insn in md.disasm(data, image_base + rva):
        op = insn.op_str
        idx = op.find("[rcx + 0x")
        if idx < 0:
            continue
        end = op.find("]", idx)
        if end < 0:
            continue
        try:
            return int(op[idx + len("[rcx + "):end], 16)
        except ValueError:
            continue
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dll", help="GameAssembly.dll / UserAssembly.dll")
    args = ap.parse_args()

    pe = pefile.PE(args.dll, fast_load=True)
    pe.parse_data_directories(
        directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]]
    )
    image_base = pe.OPTIONAL_HEADER.ImageBase
    code_size = pe.OPTIONAL_HEADER.SizeOfImage
    md = Cs(CS_ARCH_X86, CS_MODE_64)

    exports = {e.name.decode(): e.address
               for e in pe.DIRECTORY_ENTRY_EXPORT.symbols if e.name}

    print(f"module base 0x{image_base:X}  exports {len(exports)}")
    print(f"{'field':<42}{'stock':>7}{'found':>8}{'delta':>7}")
    print("-" * 64)

    deltas = {}
    for export, field, stock, counts in PROBES:
        if export not in exports:
            print(f"{field:<42}{stock:#7x}{'n/a':>8}{'':>7}   (export missing)")
            continue
        rva = follow_jumps(md, pe, image_base, exports[export], code_size)
        found = first_rcx_offset(md, pe, image_base, rva)
        if found is None:
            print(f"{field:<42}{stock:#7x}{'?':>8}{'':>7}   (no [rcx+N] in prologue)")
            continue
        delta = found - stock
        if counts:
            deltas[field] = delta
        flag = "" if delta == 0 else "   <-- differs"
        print(f"{field:<42}{stock:#7x}{found:#8x}{delta:+7d}{flag}")

    class_deltas = {d for f, d in deltas.items() if f.startswith("Il2CppClass")}
    method_deltas = {d for f, d in deltas.items() if f.startswith("Il2CppMethodInfo")}

    print()
    print("verdict:")
    print(f"  Il2CppClass      extra bytes after `token` : {sorted(class_deltas) or 'unknown'}")
    print(f"  Il2CppMethodInfo extra bytes after `slot`  : {sorted(method_deltas) or 'unknown'}")
    print()
    print("  stock layout  -> no patch needed")
    print("  +2 / +1       -> this patch (Endfield-style patched runtime)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
