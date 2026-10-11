#!/usr/bin/env python3
"""Find csproj item paths whose case does not match the filesystem.

Windows is case-insensitive and so is a default macOS volume, so a csproj can say
`System\\windows\\Documents\\TextSelection.cs` for a file that is really under `System/Windows/...`
and nobody notices for years. On Linux it is a hard build failure -- and a confusing one, because
the compiler reports the file as simply missing.

Run this after editing project files, or when a Linux build reports CS2001 for a file that is
plainly there:

    python3 eng/check-path-casing.py

Exits non-zero if any path disagrees with the filesystem, printing the on-disk spelling.
"""
import os, re, sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROPS = {
    "WpfSourceDir": f"{REPO}/src/Microsoft.DotNet.Wpf/src",
    "WpfSharedDir": f"{REPO}/src/Microsoft.DotNet.Wpf/src/Shared",
    "WpfCommonDir": f"{REPO}/src/Microsoft.DotNet.Wpf/src/Common/",
    "WpfCodeGenDir": f"{REPO}/src/Microsoft.DotNet.Wpf/src/Common/CodeGen/",
    "WpfGenDir": f"{REPO}/artifacts/obj",
}

def resolve(raw, projdir):
    p = raw.replace("\\", "/")
    for name, value in PROPS.items():
        p = p.replace(f"$({name})", value)
    if "$(" in p:
        return None                       # unresolved property: skip rather than guess
    if not p.startswith("/"):
        p = os.path.join(projdir, p)
    return os.path.normpath(p)

def case_insensitive_find(path):
    """Walk the path component by component, matching case-insensitively."""
    parts = path.strip("/").split("/")
    cur = "/"
    for part in parts:
        try:
            entries = os.listdir(cur)
        except OSError:
            return None
        if part in entries:
            cur = os.path.join(cur, part)
            continue
        matches = [e for e in entries if e.lower() == part.lower()]
        if len(matches) != 1:
            return None
        cur = os.path.join(cur, matches[0])
    return cur

problems = 0
checked = 0
for root, dirs, files in os.walk(f"{REPO}/src"):
    if "/obj" in root or "/bin" in root or "/artifacts" in root:
        continue
    for f in files:
        if not f.endswith(".csproj"):
            continue
        proj = os.path.join(root, f)
        text = open(proj, encoding="utf-8-sig", errors="replace").read()
        for m in re.finditer(r'<(?:Compile|None|EmbeddedResource|Page|Resource)\s+Include="([^"*?]+)"', text):
            raw = m.group(1)
            path = resolve(raw, root)
            if path is None:
                continue
            checked += 1
            if os.path.exists(path):
                continue
            actual = case_insensitive_find(path)
            if actual:
                print(f"CASE MISMATCH  {os.path.relpath(proj, REPO)}")
                print(f"    csproj : {raw}")
                print(f"    on disk: {os.path.relpath(actual, REPO)}")
                problems += 1

print(f"\nchecked {checked} include path(s); {problems} case mismatch(es)")
sys.exit(1 if problems else 0)
