#!/usr/bin/env python3
"""Check AtSpiRoles.cs against the AT-SPI2 enums actually installed on this machine.

AtspiRole and AtspiStateType are ABI. Nothing in the C# compiler, and nothing in the D-Bus protocol
either, notices a wrong number: the app answers every question, the client believes the answer, and
the result is a screen reader that describes a different application than the one on screen. That is
exactly what happened here -- the first version of this table had TEXT as 60 (which is TERMINAL),
FRAME as 22 (FORM), and VISIBLE as bit 22 (SELECTABLE), so Orca was told every control was selectable
and none of them was on screen.

Unit tests cannot catch it, because a test written from the same source as the table agrees with the
table. So this checks against a third party: the at-spi2 typelib installed on the box, which is the
same enum libatspi and Orca compile against.

    python3 eng/check-atspi-constants.py

Skips (exit 0) where at-spi2's GObject introspection data is not installed, since that is every
non-Linux developer machine and most CI images.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLE = ROOT / "src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/Interop/Wayland/AtSpiRoles.cs"

try:
    import gi

    gi.require_version("Atspi", "2.0")
    from gi.repository import Atspi
except (ImportError, ValueError) as exc:
    print(f"skipped: at-spi2 introspection data not available ({exc})")
    sys.exit(0)


def screaming(camel):
    """PushButton -> PUSH_BUTTON, the spelling the Atspi enums use."""
    return re.sub(r"(?<!^)(?=[A-Z])", "_", camel).upper()


source = TABLE.read_text()
failures = []


def check(pattern, enum, label, strip_prefix=""):
    seen = 0
    for name, value in re.findall(pattern, source):
        member = screaming(name[len(strip_prefix):])
        try:
            expected = int(getattr(enum, member))
        except AttributeError:
            failures.append(f"{label} {name}: no Atspi member named {member}")
            continue
        seen += 1
        if int(value) != expected:
            failures.append(f"{label} {name} = {value}, but Atspi.{member} is {expected}")
    return seen


roles = check(r"private const uint (\w+) = (\d+);", Atspi.Role, "role")
states = check(r"private const int State(\w+) = (\d+);", Atspi.StateType, "state")

# The role NAMES too: these are what a client displays and what Orca falls back to speaking, and
# "statusbar" for "status bar" is the kind of near-miss that reads as correct in review.
names = 0
for member, spoken in re.findall(r"^\s+(\w+) => \"([^\"]+)\",", source, re.MULTILINE):
    match = re.search(rf"private const uint {member} = (\d+);", source)
    if not match:
        continue
    names += 1
    expected = Atspi.role_get_name(Atspi.Role(int(match.group(1))))
    if spoken != expected:
        failures.append(f"role name for {member} is {spoken!r}, but at-spi2 calls it {expected!r}")

if not roles or not states:
    print(f"error: parsed {roles} roles and {states} states from {TABLE.name} -- has it been renamed?")
    sys.exit(1)

for failure in failures:
    print(f"error: {failure}")

print(f"checked {roles} roles, {states} states and {names} role names against "
      f"at-spi2 {Atspi.get_version() if hasattr(Atspi, 'get_version') else ''}".rstrip())
sys.exit(1 if failures else 0)
