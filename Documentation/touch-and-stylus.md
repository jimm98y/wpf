# Touch, stylus and manipulation off Windows

WPF's touch stack is `StylusTouchDeviceBase`, which sits on Wisp and WM_POINTER and exists on Windows
alone. Every other head therefore had no touch at all: the mobile backends turned a single finger
into mouse moves plus wheel notches, which is why a drag could scroll a `ScrollViewer` and do nothing
else. No `TouchDown`, no second contact, and — because `TouchDevice` is what promotes itself to a
manipulator — no pinch, rotate or inertia anywhere.

This describes what replaced that.

## The seam

`MS.Internal.Interop.PlatformTouch` is the fourth sibling of `PlatformWindow`, `PlatformClipboard`
and `PlatformDragDrop`, and exists for the same reason: the backends live in WindowsBase and
`TouchDevice`, hit-testing and the Touch events live in PresentationCore, which references
WindowsBase and not the reverse. PresentationCore installs itself into the seam from `HwndSource`.

| piece | where |
|---|---|
| `IPlatformTouchSink`, `PenState`, `TouchSample`, `PenGeometry` | `Shared/MS/Internal/Interop/PlatformTouch.cs` |
| `PlatformPenPoints` (pen to `StylusPoint`) | `PresentationFramework/MS/Internal/Ink/PlatformPenPoints.cs` |
| `CocoaPen` (macOS tablet) | `Shared/MS/Internal/Interop/CocoaPen.cs` |
| `UIKitPencil` (Pencil eraser, angles) | `Shared/MS/Internal/Interop/UIKitPencil.cs` |
| `PlatformTouchDevice`, `PlatformTouchSink` | `PresentationCore/System/Windows/Input/PlatformTouchDevice.cs` |
| `IPlatformGestureSink` (macOS) | `Shared/MS/Internal/Interop/PlatformGesture.cs` |
| `PlatformGestureManipulator` | `PresentationCore/System/Windows/Input/PlatformGestureManipulator.cs` |

A CONTACT is one finger or pen tip from down to up, identified by the platform's own id, which need
only be unique among the contacts alive at one time. Points are in SCREEN device pixels, matching
`PlatformDragDrop`.

## Three things that are easy to get wrong

**Manipulation is free; the mouse is not.** `TouchDevice` implements `IManipulator` and calls
`Manipulation.AddManipulator` on itself, so pinch, rotate and inertia follow from delivering contacts
accurately — no head needs manipulation code. It does NOT promote itself to the mouse, though, so a
head that reported contacts and stopped synthesizing mouse input would gain pinch and lose
`Button.Click`. Every touch head therefore does both: contacts for all pointers, mouse for the first.

Nothing below WPF will do it for you. X11 emulated a pointer from a touchscreen; Wayland does not,
and neither does Android or the browser once you are reading the raw pointers. Linux follows WPF's
own rule for *when* to promote, which the other heads do not: a contact whose `TouchDown` was
**handled** drives no mouse at all, and stays unpromoted for its whole life so an up cannot arrive
without its down. That is what the seam's `bool` return is for.

**Cancel is not up.** A cancel means the platform took the contact away — the compositor started a
gesture, the touch was rejected as a palm. It must not complete a tap or finish a manipulation.
`wl_touch.cancel` names no contact at all (the whole sequence is gone), which is why the seam has
`TouchCancelAll`.

**An up may carry no position.** `wl_touch.up` names only the id. Passing placeholder coordinates
would lift the finger at the screen corner, and a tap whose down and up land on different elements is
not a click — so `PlatformTouch.NoPosition` means "lift it where it was last seen". It is
`int.MinValue` rather than a negative number because a window straddling the screen origin reports
genuine negatives.

## Pen

`PenState` carries what the digitizer measured beyond position: pressure, per-axis tilt, which end
of the pen is down, twist, the contact's size and the barrel button. It is a struct so those
additions do not widen the signature — which they have not, three times now.

Every field has its own "not reported" value, and that matters: a finger measures none of them and a
cheap digitizer only some. A finger's pressure is a constant that means nothing, and a browser
reports `0.5` for a held mouse button. Substituting a default for a measurement is exactly how
`StylusPoint` ends up asserting a tilt nobody sensed, so unmeasured values stay unmeasured.

Android and UIKit report tilt spherically — an angle from vertical plus the direction the tip leans —
and both resolve it into the two per-axis tilts the browser and Windows use through
`PenGeometry.TiltFromSpherical`, which is exact (`tan tiltX = tan θ · dirX`). Scaling the angle by
the direction's cosine and sine, which both heads did before, under-reports every diagonal lean: 60°
towards a corner is 50.8° per axis, not 42.4°.

**Coalesced samples.** A digitizer samples faster than a platform delivers — a Pencil at 240Hz into
a 60Hz touch stream — and each platform keeps what it folded together: `coalescedTouchesForTouch:`,
`MotionEvent` history, `getCoalescedEvents()`. `TouchMoveCoalesced` carries them on the ONE move
they belong to, so the touch and mouse event counts are unchanged, as on Windows, where the packets
ride inside the stylus event. The sink keeps the primary contact's samples (root-visual DIPs) until
the ink path takes them, and replaces them on every move rather than accumulating: the mouse move
that follows a contact move is the one they precede. `TouchDevice.GetIntermediateTouchPoints`
returns them too, the current point last.

**Reaching InkCanvas.** `EditingCoordinator` takes its points from a `StylusDevice` when one holds
capture and from the MOUSE otherwise. Off Windows there is no `StylusDevice`, so every head falls
through to the mouse branch, which built a `StylusPointCollection` from bare `Point`s and gave every
sample the default pressure. That branch now asks the seam for the live contact's `PenState`.

Carrying tilt needs more than a value: a `StylusPoint` can only hold properties its
`StylusPointDescription` names, and every point of a stroke must share ONE description —
`StylusPointCollection.Add` throws otherwise, which is what a tilted pen used to do on its first
move. So the description is fixed at the down, exactly as Windows fixes it: the common description of
`InkCanvas.DefaultStylusPointDescription` and the pen's. An InkCanvas therefore keeps tilt, twist,
`Width`/`Height` or `BarrelButton` only when the application declared them there, on every platform
alike; by default a stroke is X, Y and pressure. `PlatformPenPoints` writes the values in Windows'
units: tilt and twist in tenths of a degree (tilt −900..900, twist 0..3599, clockwise), contact size in
HIMETRIC. A property the stroke has and a sample lacks takes the neutral value.

**The eraser.** `InkCanvas` already has the whole feature — `EditingModeInverted`, defaulting to
`EraseByStroke` — and it was unreachable here, because `EditingCoordinator` learns the pen is
inverted from `StylusDevice.Inverted` and there is no `StylusDevice`. It now asks the seam in the
same place it asks for pressure, so turning the pen over erases.

Three things about that are worth knowing. It is decided at the **down**, because
`UpdateInvertedState` refuses to change mode mid-stroke — which is why every backend must report the
contact *before* the mouse, and why Android's order was wrong and is now fixed. The eraser is a
separate *tool* on Linux (`zwp_tablet_tool_v2` of type `eraser`) and a separate *tool type* on
Android, but on the web it is a fifth mouse **button** — bit 5 of `PointerEvent.buttons` — which is
the one place the flag has to be recomputed per event rather than fixed for the device. And
`PenState.IsInverted` is a plain `bool` where every other field carries a "not reported" value: a
head that cannot tell reports false, and no consumer would treat "upright" and "no idea" differently.

**Why not a managed `StylusDevice`.** `StylusDevice` is sealed over a `StylusDeviceBase` with some
thirty abstract members, plus a `TabletDevice` subsystem and `StylusPlugInCollection` — the whole Wisp
pipeline. A partial implementation would not fail; it would mislead `InkCanvas` in ways that surface
as subtly wrong strokes. The one branch above delivers the same user-visible feature.

## Per head

| head | touch | pen | eraser | manipulation |
|---|---|---|---|---|
| Windows | WM_POINTER (unchanged) | WM_POINTER | WM_POINTER | native |
| Android | `MotionEvent`, all pointers, touch ellipse | pressure, tilt, barrel (`BUTTON_STYLUS_PRIMARY`), history | `TOOL_TYPE_ERASER` | via seam |
| iPadOS | `UITouch`, all touches, `majorRadius` | Pencil: pressure, tilt, roll as twist (Pro), coalesced touches | `UIPencilInteraction` tool switch | via seam |
| Linux | `wl_touch` | `tablet-v2`: pressure, tilt, rotation as twist, `BTN_STYLUS` barrel | tool of type `eraser` | via seam |
| WebAssembly | pointer events, `width`/`height` | pressure, tilt, `twist`, barrel (`buttons` bit 1), `getCoalescedEvents()` | `buttons` bit 5 | via seam |
| macOS | **n/a** | tablet: pressure, tilt, rotation as twist, barrel, de-coalesced drags | proximity `NSPointingDeviceTypeEraser` | AppKit gestures |

Twist is reported only where a sensor measures it. Android has none: its `AXIS_ORIENTATION` for a
stylus is the direction of the lean the tilt already uses, not the barrel's rotation. Where a
platform reports an unsensed twist as zero (the browser, a Pencil without roll, a Mac tablet seen
without a proximity event), a zero is taken as "not reported".

**The iPad eraser.** An Apple Pencil has no inverted end; iPadOS gives the eraser through
`UIPencilInteraction` — a double tap, or a squeeze on a Pencil Pro — interpreted by the user's own
setting (`preferredTapAction`, `preferredSqueezeAction`). "Switch eraser" toggles an eraser state;
"switch previous" goes back to the other tool once the eraser has been used. While it is on, every
Pencil contact reports `IsInverted`. Each WPF view carries an interaction.

**A Mac tablet is a digitizer** even though the trackpad is not: its surface is mapped onto the
screen, and AppKit reports the pen as the mouse with tablet data on the same events
(`NSEventSubtypeTabletPoint`). `CocoaPen` reports a left press, drag and release that carry that data
through the seam as well, contact first; a mouse or trackpad carries no subtype and never reaches it.
Tilt is scaled (±1 is a full tilt, taken as 60°, with AppKit's y up), rotation is counterclockwise, and
the proximity event's capability mask says which axes the device has. AppKit's own drag coalescing is
turned off for the length of a pen stroke and done in the pump instead, so the drags already queued
become one delivered drag carrying the others as samples.

**Order.** Every head reports a contact's down and moves BEFORE the mouse, and iOS and Android report
the primary contact's LIFT after it: their press is held back until the touch is known not to be a
press-and-hold right click, and a tap releases it on the way up — a contact lifted first had taken the
eraser with it before the press it was for.

**Windows keeps its own stack, by design.** This port forces the WM_POINTER path because Wisp reaches
the tablet through `PenImc_cor3.dll` and no native DLL ships here; see the comment on
`StylusLogic.IsPointerStackEnabled`. Windows has a real `StylusDevice` and needs nothing from this
seam.

**Linux binds a whole protocol for the pen,** and doing so is not free. `tablet-v2` is three objects
deep — manager, tablet seat, tool — and a "tool" is a physical implement rather than a device, so the
tip and the eraser end of one pen are two of them. The trap is that binding it turns the compositor's
pointer emulation OFF for this client: a pen already worked here as a plain mouse precisely because
the client had *not* bound the protocol, and mutter, KWin and wlroots all decide that per surface. So
`WaylandTablet` drives the mouse itself as well as the seam. Adding pressure would otherwise have
taken the pen from working-without-pressure to not working at all.

Two smaller things it has to get right: the axes arrive as separate events batched by a `frame`, so a
position reported the moment `motion` arrives would be paired with the *previous* sample's pressure —
visible in ink as a stroke whose width lags the pen. And `down`/`up` carry no position (nor, for the
up, a serial), which is the same rule `wl_touch.up` follows.

**macOS reports gestures, not contacts,** and the distinction is not stylistic. A Mac has no
touchscreen, and its trackpad reports positions on the TRACKPAD — normalised, unrelated to any window
— so a `TouchDevice` built from them would claim a finger touched a place it never did. AppKit
instead hands over the gesture it already recognised, and `PlatformGesture` turns that into a PAIR of
`IManipulator`s. The pair is necessary rather than tidy: one manipulator can only express
translation, because scale is a change in the DISTANCE between two points and rotation a change in
the ANGLE between them.

## Tests

`tests/CrossPlatform/Wpf.Input.Tests`, and it holds two very different kinds.

The **injection** suites (`TouchTests`, `PenTests`, `GestureTests`, `ManipulationTests`, `InkTests`)
push contacts into the operating system's own pointer queue, aimed at a real on-screen window. That
is the right test for the Windows path and impossible anywhere else, so they skip off Windows — which
is why the project ignores exit code 8, the test platform's "zero tests ran" policy.

`PlatformTouchSeamTests` takes the other half of that: it calls the seam exactly as a backend does and asserts
what WPF raised. That covers everything above the backends — contact tracking, hit-testing, the Touch
events, promotion to Manipulation — on any machine, and it is what makes the other heads developable
at all. Eleven tests: the event sequence, hit-testing, two contacts tracked independently, cancel
raising no up, an up with no position, manipulation translation and scale, the macOS gesture path
both firing and correctly not firing, and the pen — pressure and the inverted end reaching
`CurrentPen`, forgotten again on lift, and a finger asserting neither.

One macOS constraint shaped the whole project: AppKit aborts the process unless a window is created on
the PROCESS MAIN THREAD, and a test runner does not run tests there. `Program.cs` replaces xunit's
generated entry point, runs the tests on a worker and pumps a `Dispatcher` on the main thread, which
`UiThread` adopts. Disabling the generated entry point also means re-registering
`SelfRegisteredExtensions` by hand — without it `dotnet test` fails with
`Unknown option '--internal-msbuild-node'`, which the apphost run never shows.

## Verification status

Honest, because most of this cannot be exercised here:

* the **seam** is executed — nine tests, macOS — and so is the **ink path above it**:
  `PlatformPenInkTests` draws on a real `InkCanvas` through the seam and the off-Windows mouse entry,
  and checks the stroke's points, their order with coalesced samples, tilt/twist/size/barrel in
  Windows' units, the default description, and the inverted end erasing;
* the heads' **decoding** is executed in `Wpf.Platform.Tests`: `PenGeometryTests`, `CocoaPenTests`
  (real NSEvents built from CGEvents with tablet fields set, and a stroke reported to a recording
  sink), `HeadPenInputTests` (the browser's JSON, Android's axes and history, the Pencil tool switch);
* Linux's **protocol tables** are executed — `WaylandProtocolTableTests`, on any machine, because
  the authored `wl_interface` tables are built without libwayland present. That covers the
  transcription (opcode order, argument types, listener length), not the behaviour;
* **Android** compiles against the real bindings and both heads AOT clean, but has not run on a
  device;
* **Linux** and **iPadOS** are compile-verified only;
* the **WebAssembly** JavaScript has never been parsed, let alone run — there is no JS runtime on the
  development machine;
* **Windows** was not touched and wants its injection suites run to confirm tilt reaches
  `StylusPoint`.

No real pen has been used on any of these heads: no tablet, Pencil, S-Pen or browser pen was
available. The browser's `contactMeasurement`/`pushContact` were run in JavaScriptCore against fake
`PointerEvent`s; `AndroidHost.cs` compiles against the real Android bindings.

## Known gaps

* The tablet pad — the buttons, rings and strips on the tablet body — is not handled anywhere.
* `wl_touch.shape` is still received and dropped, so a Linux finger reports no contact size: the
  shape arrives after the down or motion it describes, and the touch events are not yet deferred
  to the frame.
* Off Windows there is no `StylusDevice`, so `Stylus*` events and `StylusDevice.GetStylusPoints` see
  none of this; only `InkCanvas` (through the mouse) and the Touch events do.
