#!/usr/bin/env python3
"""Check that every font FontFactoryState substitutes onto is actually obtainable.

Three separate defects in this repo have had the same shape: the substitution table names a font as
the preferred replacement for a Windows one, and no head ships it, so the chain silently falls through
to a worse face and the UI renders wrong in a way nobody attributes to fonts.

  * Segoe Fluent Icons / Segoe MDL2 Assets -> "Symbols"   -- bundled, but not DEPLOYED on unix desktop,
                                                             so every Fluent icon was a missing-glyph box
  * Cascadia Code                                         -- bundled, not deployed: programming
                                                             ligatures silently degraded
  * Segoe UI -> "Selawik"                                 -- named FIRST and not bundled AT ALL, so the
                                                             UI falls through to Liberation Sans

The first name in each chain is the one that matters: it is the face the substitution was designed
around, and everything after it is a compromise. This script fails when that name is neither bundled
nor plausibly provided by the OS.

    python3 eng/check-font-substitutions.py
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLE = ROOT / "src/Microsoft.DotNet.Wpf/src/DirectWriteForwarder/Stub/Managed/FontFactoryState.cs"
BUNDLED_DIR = ROOT / "sdk/WpfWebGpu.Sdk/web/fonts"

# Faces we deliberately rely on the OS for, rather than shipping. Everything here is either present on
# every target of the head that needs it, or is a same-platform alias we can assume.
OS_PROVIDED = {
    # macOS
    "helvetica neue", "helvetica", "apple symbols", "menlo", "monaco", "times", "geneva",
    "apple color emoji", "sf pro", "sf pro text", "lucida grande", "courier",
    # Linux (the head lists these as prerequisites: fonts-liberation, fonts-dejavu-core)
    "liberation sans", "liberation serif", "liberation mono",
    "dejavu sans", "dejavu serif", "dejavu sans mono",
    "noto serif", "noto sans", "noto color emoji",
    # Android
    "roboto", "droid sans", "droid serif", "droid sans mono", "cutive mono",
    # Windows, where the original font exists anyway
    "arial", "consolas", "courier new", "times new roman", "segoe ui", "segoe ui symbol",
    "cascadia mono", "symbol", "wingdings",
}


def bundled_faces():
    """Family names we ship, inferred from the vendored file names."""
    faces = set()
    for f in sorted(BUNDLED_DIR.glob("*.ttf")):
        stem = f.stem.split("-")[0]                      # LiberationSans-Bold -> LiberationSans
        spaced = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", stem)   # LiberationSans -> Liberation Sans
        faces.add(spaced.lower())
    return faces


def main():
    source = TABLE.read_text(encoding="utf-8")
    bundled = bundled_faces()

    # ["segoe ui"] = new[] { "Selawik", "Helvetica Neue", ... },
    entries = re.findall(r'\["([^"]+)"\]\s*=\s*new\[\]\s*\{([^}]*)\}', source)
    if not entries:
        print("error: no substitution entries parsed -- has the table's shape changed?", file=sys.stderr)
        return 2

    problems = []
    for requested, body in entries:
        chain = re.findall(r'"([^"]+)"', body)
        if not chain:
            continue
        preferred = chain[0]
        key = preferred.lower()
        if key in bundled or key in OS_PROVIDED:
            continue
        rest = ", ".join(chain[1:]) or "(nothing)"
        problems.append(f"  '{requested}' prefers '{preferred}', which is neither bundled nor OS-provided.\n"
                        f"      It will silently fall through to: {rest}")

    print(f"checked {len(entries)} substitution chain(s) against {len(bundled)} bundled face(s)")
    if problems:
        print("\nFONT SUBSTITUTION CHECK FAILED\n")
        print("\n".join(problems))
        print("\nEither vendor the font into sdk/WpfWebGpu.Sdk/web/fonts (and add it to the deploy lists,\n"
              "which name files explicitly), or add it to OS_PROVIDED if the OS really does supply it.")
        return 1

    print("all preferred substitutes are bundled or OS-provided")
    return 0


if __name__ == "__main__":
    sys.exit(main())
