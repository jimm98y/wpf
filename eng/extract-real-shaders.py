#!/usr/bin/env python3
"""Extract genuine fxc-compiled D3D9 pixel shaders for validating D3D9ShaderTranslator.

There is no fxc on macOS and no .ps asset in any of these repos, so the shader translator
was originally testable only against bytecode hand-assembled from the spec -- which cannot
catch an assumption shared by the assembler and the translator (it already let a DEF/IF
opcode mix-up through).

WPF's own effect shaders are compiled into milcore's native wpfgfx_cor3.dll. This finds
them by scanning for the ps_2_x/ps_3_x version token and validating that each candidate
walks to an END token through plausible opcodes, then writes them out as .ps files.

    python3 eng/extract-real-shaders.py [output-dir]
    WPF_REAL_SHADER_DIR=<output-dir> dotnet run --project \
        src/Microsoft.DotNet.Wpf/src/WgpuInterop/tests/WgpuInterop.ShaderEffectTest
"""
import glob, hashlib, os, re, struct, sys

PATTERN = re.compile(rb'\x00[\x02\x03]\xff\xff')

def shader_extent(buf, off):
    """Byte length of the shader at off, or 0 if it does not parse as one."""
    n, p, instrs = len(buf), off + 4, 0
    while p + 4 <= n and instrs < 4000:
        tok = struct.unpack_from('<I', buf, p)[0]
        op = tok & 0xFFFF
        if op == 0xFFFF:                       # END
            return (p + 4 - off) if instrs >= 2 else 0
        if op == 0xFFFE:                       # comment block
            p += 4 + 4 * ((tok >> 16) & 0x7FFF); instrs += 1; continue
        if op > 200:                           # not a real D3DSIO opcode
            return 0
        p += 4 + 4 * ((tok >> 24) & 0xF)
        instrs += 1
    return 0

def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "/tmp/realshaders"
    os.makedirs(out, exist_ok=True)
    home = os.path.expanduser("~")
    sources = glob.glob(f"{home}/.nuget/packages/**/wpfgfx_cor3.dll", recursive=True)
    if not sources:
        print("no wpfgfx_cor3.dll found under ~/.nuget/packages "
              "(restore a package that carries milcore, e.g. librewpf.transport)")
        return 1

    seen = {}
    for src in sources:
        buf = open(src, 'rb').read()
        for m in PATTERN.finditer(buf):
            ln = shader_extent(buf, m.start())
            if not ln:
                continue
            blob = buf[m.start():m.start() + ln]
            seen.setdefault(hashlib.sha1(blob).hexdigest()[:10], blob)

    for i, (h, blob) in enumerate(sorted(seen.items())):
        ver = struct.unpack_from('<I', blob, 0)[0]
        open(os.path.join(out, f"{i:02d}_{h}_ps{(ver >> 8) & 0xff}_{ver & 0xff}.ps"), 'wb').write(blob)
    print(f"extracted {len(seen)} distinct shaders from {len(sources)} binaries -> {out}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
