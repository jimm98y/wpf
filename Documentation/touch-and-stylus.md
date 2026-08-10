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
| `IPlatformTouchSink`, `PenState` | `Shared/MS/Internal/Interop/PlatformTouch.cs` |
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

`PenState` carries what the digitizer measured beyond position: pressure and per-axis tilt today,
twist and the inverted end next. It is a struct so those additions do not widen the signature again.

Every field has its own "not reported" value, and that matters: a finger measures none of them and a
cheap digitizer only some. A finger's pressure is a constant that means nothing, and a browser
reports `0.5` for a held mouse button. Substituting a default for a measurement is exactly how
`StylusPoint` ends up asserting a tilt nobody sensed, so unmeasured values stay unmeasured.

Android and UIKit report tilt spherically — an angle from vertical plus the direction the tip leans —
and each resolves it into the two per-axis tilts the browser and Windows use, which is what
`StylusPointProperties.XTiltOrientation`/`YTiltOrientation` take.

**Reaching InkCanvas.** `EditingCoordinator` takes its points from a `StylusDevice` when one holds
capture and from the MOUSE otherwise. Off Windows there is no `StylusDevice`, so every head falls
through to the mouse branch, which built a `StylusPointCollection` from bare `Point`s and gave every
sample the default pressure. That branch now asks the seam for the live contact's `PenState`.

Carrying tilt needs more than a value: a `StylusPoint` can only hold properties its
`StylusPointDescription` names, so the point is built with a description declaring both tilt axes
beside X, Y and pressure. Note that the `StylusPointCollection(description, int)` overload takes a
CAPACITY, not the points.

**Why not a managed `StylusDevice`.** `StylusDevice` is sealed over a `StylusDeviceBase` with some
thirty abstract members, plus a `TabletDevice` subsystem and `StylusPlugInCollection` — the whole Wisp
pipeline. A partial implementation would not fail; it would mislead `InkCanvas` in ways that surface
as subtly wrong strokes. The one branch above delivers the same user-visible feature.

## Per head

| head | touch | pen | manipulation |
|---|---|---|---|
| Windows | WM_POINTER (unchanged) | WM_POINTER | native |
| Android | `MotionEvent`, all pointers | S-Pen: pressure + tilt | via seam |
| iPadOS | `UITouch`, all touches | Pencil: pressure + tilt | via seam |
| Linux | `wl_touch` | `tablet-v2`: pressure + tilt | via seam |
| WebAssembly | pointer events | pen: pressure + tilt | via seam |
| macOS | **n/a** | n/a | AppKit gestures |

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

`PlatformTouchSeamTests` takes the other half: it calls the seam exactly as a backend does and asserts
what WPF raised. That covers everything above the backends — contact tracking, hit-testing, the Touch
events, promotion to Manipulation — on any machine, and it is what makes the other heads developable
at all. Nine tests: the event sequence, hit-testing, two contacts tracked independently, cancel
raising no up, an up with no position, manipulation translation and scale, and the macOS gesture path
both firing and correctly not firing.

One macOS constraint shaped the whole project: AppKit aborts the process unless a window is created on
the PROCESS MAIN THREAD, and a test runner does not run tests there. `Program.cs` replaces xunit's
generated entry point, runs the tests on a worker and pumps a `Dispatcher` on the main thread, which
`UiThread` adopts. Disabling the generated entry point also means re-registering
`SelfRegisteredExtensions` by hand — without it `dotnet test` fails with
`Unknown option '--internal-msbuild-node'`, which the apphost run never shows.

## Verification status

Honest, because most of this cannot be exercised here:

* the **seam** is executed — nine tests, macOS;
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

## Known gaps

* Linux touch does not promote to the mouse. `wl_touch` contacts reach the seam, but no compositor
  emulates a pointer from a touchscreen, so a finger raises the Touch events and drives Manipulation
  and does not click anything. The pen does not have this problem — `WaylandTablet` drives the mouse
  itself, for the reason above.
* No twist, and no inverted (eraser) end, on any head. Linux is the closest: `zwp_tablet_tool_v2`
  announces the eraser as a separate tool of type `eraser`, which `WaylandTablet` records and has
  nowhere to put, because `PenState` has no inverted flag.
* The tablet pad — the buttons, rings and strips on the tablet body — is not handled anywhere.
* `TouchPoint` reports a zero-size contact rect everywhere. `wl_touch.shape` is received and dropped;
  no other head reports an ellipse at all.
* No intermediate touch points: `GetIntermediateTouchPoints` returns empty, so a backend that
  coalesces (Android's `MotionEvent` history) currently drops the samples between reports.
