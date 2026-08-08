# The Linux head (Wayland + WebGPU)

WPF on Linux: a native Wayland windowing backend under the same WebGPU renderer the macOS, Android,
iOS and browser heads use. A WPF app is a plain `net10.0` executable with a normal `Main` — there is
no host project and no app delegate, exactly as on macOS.

## Prerequisites

| | |
|---|---|
| session | Wayland (`WAYLAND_DISPLAY` set). There is no X11 fallback — see "Why Wayland-native" below. |
| libraries | `libwayland-client`, `libdecor-0` **plus its GTK plugin**, `libxkbcommon`, `libdbus-1` |
| fonts | `fonts-liberation` and `fonts-dejavu-core`. The managed font stack's fallback chains end in Liberation/DejaVu/Noto, and `FontFamily` fails hard on an empty catalogue. |
| desktop | `xdg-desktop-portal` (+ a backend such as `-gnome`) for file dialogs and the colour-scheme setting. Optional; their absence degrades gracefully. |
| fonts (bundled) | Families that travel **with the app**, in a `fonts/` folder beside it: **Symbols** (the Fluent theme's glyph icons substitute onto it; no Linux or macOS box has Segoe Fluent Icons / Segoe MDL2 Assets, and their glyphs are in a private-use range no text font covers), **Cascadia Code** (its programming ligatures; the fallbacks have none), **Selawik** (the metric-compatible Segoe UI stand-in) and **Noto Sans CJK** (Japanese, both Chinese, Korean — unlike Liberation/DejaVu this is *not* a safe assumption on a Linux install without the CJK language packs, and its absence renders every CJK character as a box). The SDK deploys them for any unix desktop app — macOS included — and `SystemFontCatalog` probes the app-local folder on both. Samples that reference the fork by path do it themselves. |
| media | `libgstreamer1.0-0`, `gstreamer1.0-plugins-base/-good` for `MediaElement`/`MediaPlayer`. Optional; without them media raises `MediaFailed` instead of crashing. |

`libdecor` is not optional for toplevels. GNOME implements no server-side decorations, so without it
a window is an undecorated rectangle with no titlebar, no close button and no way to move or resize.

## Build and run

```bash
./eng/common/dotnet.sh                    # bootstrap ./.dotnet (once)
pwsh src/Microsoft.DotNet.Wpf/src/WgpuInterop/eng/fetch-wgpu.ps1 -Rid linux-arm64
eng/run-linux.sh                          # smoke app: build + run
eng/run-linux.sh --gallery                # the shared code-only gallery
eng/run-linux.sh --no-rebuild             # skip the fork build
```

Note `./build.sh` does **not** work off-Windows past bootstrapping: `global.json` declares
Windows-only `native-tools` (strawberry-perl, net-framework-48-ref-assemblies). `eng/build-sdk.sh`
is the cross-platform build.

`eng/run-linux.sh --help`-worthy flags: `--scale N`, `--cpu-raster` / `--gpu-raster`,
`--backend vulkan|gl|all`, `--poll-pump`, `--wayland-log`, `--wayland-debug`, `--wgpu-log`, `--dump`.

## Layout

Everything Wayland lives in `src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/Interop/Wayland/`:

| file | role |
|---|---|
| `Wl.cs` | libwayland binding: the three native layouts, core-interface resolution, request marshalling |
| `WlProtocols.cs` | hand-authored `wl_interface` tables for xdg-shell and extensions |
| `protocols/` | the vendored protocol XML **and `validate-tables.py`**, which checks the tables against it |
| `WlDecor.cs` | libdecor (client-side decorations) |
| `WlXkb.cs` | libxkbcommon (keymaps, compose) |
| `WaylandDisplay.cs` | the one connection, the globals, the read/dispatch pump |
| `WaylandWindow.cs` | `IPlatformWindow`; toplevels, popups, the coordinate model |
| `WaylandInput.cs` | `wl_seat` pointer + keyboard, key repeat |
| `WaylandCursor.cs` | `cursor-shape-v1`, with an XCursor-theme fallback for GNOME 46 and older |
| `WaylandClipboard.cs` | `wl_data_device` selections |
| `WaylandDragDrop.cs` | `wl_data_device` drags, both directions |
| `DBusLite.cs`, `PortalDialogs.cs`, `LinuxDesktopSettings.cs` | xdg-desktop-portal over D-Bus |

The renderer side is `WgpuInterop/Composition/Platform/LinuxInterop.cs` (the wgpu surface) and
`LinuxPlatform.cs` (the public seam WPF installs itself into, reflectively, so neither assembly
references the other).

**Run `protocols/validate-tables.py` after touching `WlProtocols.cs`.** Nothing in the C# compiler
checks opcode order, signature strings or `types[]` arrays, and a mistake there is not a compile
error — it is a protocol error at runtime that kills the whole connection.

## Two things worth knowing before reading the code

**Coordinates.** Wayland has no global window coordinates, by design: a client cannot learn where
its window is and cannot ask to be put anywhere. WPF assumes an absolute screen space throughout.
The backend keeps a *virtual* space in which screen bounds are real, toplevel positions are
remembered fiction, and popup positions are real because the compositor reports them back. The
consequences are stated in `WaylandWindow.cs`'s header; the short version is that `Window.Left/Top`
cannot move a toplevel, so `WindowStartupLocation.Manual/CenterScreen/CenterOwner` cannot place one.

**Cursors have two paths.** `cursor-shape-v1` (the compositor draws them) is preferred, but it only
reached mutter in GNOME 47. Below that it is not advertised, so the backend loads the user's XCursor
theme with libwayland-cursor and drives `wl_pointer.set_cursor` itself. The theme name and size come
from `XCURSOR_THEME`/`XCURSOR_SIZE` or GNOME's settings — *not* from libwayland-cursor's own default,
which resolves to `/usr/share/icons/default` and on Ubuntu inherits DMZ-White rather than the theme
the user actually chose.

**Popups are composited into their owner.** The GL backend advertises `alphaModes: Opaque` only, so
a popup with its own surface cannot be transparent and its rounded corners and shadow would scan out
black. `NativePlatform.PopupsShareOwnerSurface` therefore includes Linux, drawing popups into the
owner's surface — the route Android and iOS already use. `WPF_LINUX_COMPOSITE_POPUPS=0` opts out.

## Media

`MediaElement`/`MediaPlayer` run on GStreamer, via `PresentationCore/System/Windows/Media/Platform/`
`LinuxMediaBackend.cs` behind the same `IMediaBackend` seam the macOS (AVFoundation) and browser
(HTML5 `<video>`) heads use. The pipeline is `playbin` with its video-sink replaced by
`videoconvert ! video/x-raw,format=BGRA ! appsink`, so frames arrive in the byte order WPF's `Bgra32`
already uses. Everything runs on the media dispatcher thread: a 16 ms `DispatcherTimer` pumps the bus
and pulls the newest sample with a **zero** timeout, so no frame crosses a thread boundary.

Three GStreamer behaviours the code has to work around, each of which produced a real bug:

- **`gst_bus_pop_filtered` discards non-matching messages** rather than skipping them, so popping once
  per message type destroys the other types — a first pop for `ERROR` silently threw the `EOS` away and
  playback never reported finishing. There is one pop with a combined mask, and the type is read from
  the message.
- **`appsink` does not chain EOS up to the bus** (`gstappsink.c`: "no need to chain up"), so the sink
  is also asked directly via `gst_app_sink_is_eos`. That call returns TRUE for a sink that is not in
  PAUSED/PLAYING, and for audio-only media playbin never links the video sink — hence the guards in
  `SinkIsEos`, without which audio-only playback "ends" a second in.
- **`try_pull_sample` does not return the preroll sample**, only buffers. Opening reports `0x0` unless
  the preroll is pulled explicitly, so `MediaOpened` is held until real dimensions exist.

GLib is bound without variadics: `g_object_set` is variadic (unreliable on aarch64), so properties go
through `g_object_set_property` + `GValue`, and anything expressible in the pipeline description string
is. `GstMessage.type` sits behind `GstMiniObject`, whose size is an ABI detail — rather than hardcode
it, the offset is **measured at startup** from two probe messages with known types that must agree.

Not wired: `SetBalance` (playbin has no balance property; it would need an `audiopanorama` spliced into
the audio path) and `DownloadProgress` (reports 1.0).

```bash
eng/run-linux.sh --media -- video.webm              # interactive: transport controls
eng/run-linux.sh --media -- video.webm --auto 13    # unattended: prints RESULT pass/fail
WPF_MEDIA_LOG=1 …                                   # open/EOS/volume/type-offset trace
```

`--auto N` drives play → seek → 2x → 1x → pause → volume → close → reopen off a timer and exits with a
non-zero code if the media never opened, which is what makes this checkable without a human watching.

## Clipboard, DataObject, and why drag-and-drop is not done

`System.Windows.DataObject` **cannot be constructed off Windows**. Its constructor builds an OLE
composition that registers in the COM Global Interface Table, which P/Invokes `OLE32.dll`:

```
TypeInitializationException: ... 'Windows.Win32.Foundation.GlobalInterfaceTable'
  inner DllNotFoundException: Unable to load shared library 'OLE32.dll'
```

That code lives in the `System.Private.Windows.Core` binary shared with WinForms, not in this repo, so
it cannot be patched here. `Clipboard` already routes around it through `MacDataObject` — an
`IDataObject` that never touches OLE, despite the name — and `PlatformClipboard`.

**Drag-and-drop works around it the same way.** `WaylandDragDrop.cs` speaks `wl_data_device` in both
directions; `LinuxDragDrop.cs` in PresentationCore turns that into WPF's events, supplying its own
`DragDataObject` (over a `wl_data_offer`) and `MemoryDataObject` (for a bare value handed to
`DoDragDrop`). `DragDrop.GetDataObject` gained an `IDataObject` arm so a non-`DataObject` is accepted.

The hit-testing is **not** reimplemented: `OleDropTarget` already resolves the element under the point,
honours `AllowDrop`, synthesises the DragLeave/DragEnter pair when the target changes mid-drag, and
runs the tunnel/bubble pass — and despite the name it is ordinary managed code behind a plain
interface. Only the transport was OLE, and that is what the Wayland side replaces.

Three things that bite here:

- **`wl_data_device.enter` is `(serial, surface, x, y, id)`** and the vtable was missing `surface`,
  shifting every argument after it. Harmless while the handler was an empty stub; not once it ran.
- **`start_drag` needs the serial of a button that is still held.** mutter drops a request carrying any
  other serial *without saying so*, which strands the nested frame waiting for a drag that never began.
  `WaylandInput.LastPointerButtonSerial` tracks it and clears on release, so a programmatic
  `DoDragDrop` with no button down returns `None` instead of hanging.
- **`wl_data_offer.finish` on an offer that was not dropped is a protocol error**, and one that kills
  the connection rather than the drag. It is sent only after an accepted drop, and only at v3+.

Status: the **source** side is verified only as far as "asks correctly and returns `None` when it may
not start" — `eng/run-linux.sh -- --auto-dragdrop`. The **target** side compiles and is wired but has
not been exercised, because no flag can synthesise a drag; run the smoke app and drop a file onto it
from Files, and it prints the formats and paths it received.

Not wired: a drag image (the compositor draws its default), `DragDropEffects.Link` (Wayland has no
link action, so it rides on copy), and `GiveFeedback`/`QueryContinueDrag`.

`Clipboard` answers a read from its own offer when this process owns the selection, rather than asking
the compositor to hand the bytes back. That is not an optimisation: the compositor announces a new
selection by sending us a `wl_data_offer`, and until that event is dispatched the cached offer still
describes the previous owner — so `SetText` immediately followed by `GetText` read back empty.

## Imaging, printing, and other Windows-only leaves

`ManagedImageDecoder` is the codec off Windows, but only `BitmapImage` and `BitmapFrame.Create` used
it — the whole **`BitmapDecoder` family went straight to `wpfgfx_cor3.dll`** and threw
`DllNotFoundException`, which is what an app hits as soon as it wants frames or metadata rather than
just an image to show. Both entry points now take the managed route: `BitmapDecoder.Create` returns a
`ManagedBitmapDecoder`, and the typed constructors (`PngBitmapDecoder(stream, …)` and friends) decode
into themselves, since a constructor cannot hand back a different object.

Encoding is managed too, and **all five standard encoders now work**, behind
`BitmapEncoder.TryManagedEncode`:

| encoder | how |
|---|---|
| `ManagedPngEncoder` | 8-bit RGBA, deflate in a zlib container, `pHYs` for DPI. Lossless. |
| `ManagedJpegEncoder` | Baseline sequential DCT (SOF0), 4:2:0, standard T.81 Annex K tables. `QualityLevel` scales them on libjpeg's own curve, so the number means what it means elsewhere. Alpha is dropped, as WIC does — a semi-transparent pixel keeps its colour rather than being composited onto a guessed background. |
| `ManagedBmpEncoder` | 32-bit BGRA with a **BITMAPV5HEADER**, not the usual BITMAPINFOHEADER: a plain 32-bit BI_RGB bitmap has an undefined fourth byte that most readers discard, and V5 states the channel masks and colour space so the alpha is not a matter of interpretation. Lossless. |
| `ManagedTiffEncoder` | Baseline uncompressed RGBA, one strip, little-endian. `ExtraSamples = unassociated alpha`, without which a reader may treat the fourth sample as premultiplied and darken everything transparent. Lossless. |
| `ManagedGifEncoder` | GIF89a, global colour table, LZW. Images with few enough distinct colours keep them exactly (**lossless** — the common case for icons and diagrams); otherwise median cut. Alpha collapses to GIF's single transparent index, which is all the format can express. |

These are checked against other people's codecs rather than against themselves:

```bash
eng/run-linux.sh -- --jpeg-out DIR    # writes a reference PNG plus every format, at several sizes
```

* **JPEG**: encoding the same reference at the same quality and subsampling with PIL/libjpeg-turbo and
  comparing both to the source agrees **within 0.04 dB PSNR at every quality**, with matching maximum
  per-channel error — including the 13x7 and 17x33 cases, where the right and bottom MCUs are padded
  and edge-handling bugs would show. GStreamer's `jpegdec` also decodes the output, so the check does
  not rest on one decoder.
* **BMP and TIFF**: **pixel-exact** against the source across all four channels, alpha included.
* **GIF**: pixel-exact for the few-colour and transparent cases. For a 27,776-colour gradient — which
  is what forces median cut — 37.14 dB against PIL's own MEDIANCUT at 36.30 dB, in a 21% smaller file.

Adding an encoder without a check like this is how the `BitmapDecoder` gap survived: it compiled.

What still does not work off Windows, and fails clearly rather than mysteriously:

| | |
|---|---|
| `WmpBitmapEncoder` (Windows Media Photo / JPEG XR) | `PlatformNotSupportedException`. The other five encoders work. |
| `SystemSounds` | `PlatformNotSupportedException`, thrown by the `System.Windows.Extensions` package itself. |
| `System.Printing` (`LocalPrintServer`, `XpsDocumentWriter`) | **`NullReferenceException`.** It is C++/CLI, so only its contract-only reference assembly ships here and every member faults. A managed reimplementation over CUPS is its own project. `PrintDialog.PrintQueue` is the one entry point that was fixed: it returns null ("no default printer"), a state it already had to report. |
| `System.Windows.DataObject` | `DllNotFoundException` for OLE32 — see above. |

## Windows version gates

`Standard.Utility`'s `IsOSVistaOrNewer` / `IsOSWindows10OrNewer` family means "is this a Windows new
enough to have feature X", and each one guards a Windows-only P/Invoke. `Environment.OSVersion.Version`
returns the **kernel** version on Linux — 7.0 here — which sails past every comparison and lets those
calls through to a `DllNotFoundException`. `Utility._osVersion` is therefore pinned to 0.0 off Windows
so each gate answers false and each call site takes the downlevel path it already has.
`SystemParameters.WindowGlassBrush` is what surfaced this: it reaches `DwmGetColorizationColor`, whose
own guard is `IsOSVistaOrNewer && IsThemeActive()`, and `uxtheme.dll` does not exist here.

## Text rendering: gamma, and two fixes that were tried and rejected

Text on Linux does not look as good as on macOS. Two plausible fixes were tried against the real WPF
Gallery and **both were reverted** — recorded here so they are not re-attempted.

**1. Turning gamma-space compositing off for OpenGL.** `s_gammaComposite`'s comment says it is
"settable (not readonly) so the sink can turn it OFF for backends that can't present a gamma-space
swapchain faithfully — notably OpenGL/ANGLE", and nothing ever assigned it. Implementing that override
made the LIGHT theme clearly better and the DARK theme clearly worse. macOS runs Metal, keeps
gamma-space compositing, and looks right in **both** themes — so linear compositing is not the answer,
and the default now matches macOS again.

If you try this: the flip must happen immediately after the context is created, before the renderer
exists. `MilcoreEngine` sRGB-encodes every colour as it parses, so flipping later (e.g. once the
swapchain format is chosen) leaves gamma-encoded colours stored to an sRGB surface, encoded a second
time — the whole scene comes out washed out.

**2. A direction-aware glyph coverage curve.** Compositing coverage linearly and encoding to sRGB
makes dark-on-light text read too THIN, which the boost curve (`cov^(1/2.2)`) corrects; light-on-dark
has the opposite error, so flipping the exponent for light text looks like the answer. It is not —
dark-theme text came out washed out and illegible, worse than the single curve.

**The diagnosis, confirmed from the sink log** (`WPF_WEBGPU_SINK_LOG`):

```
WebGPU device created backend=OpenGL type=IntegratedGPU device='virgl (Apple M4 (Compat))'
ChooseFormat gamma=True gl=True formats[0]=RGBA8UnormSrgb chosen=RGBA8Unorm
```

The surface's native format is **sRGB**, and gamma mode deliberately picks a **UNORM** format instead
so the pre-encoded bytes store verbatim -- which is precisely the case the comment says GL scans out
too dark. So the fault is not the blend space or the glyph curve (both were tried, above); it is that
on GL we hand the compositor a format it does not display correctly.

Note the first adapter line in that log is the REJECTED software Vulkan attempt (`backends 0x1 offered
only a software adapter ... llvmpipe`); the backend selector then retries with GL and takes the
hardware virgl adapter. `gl=True` is therefore correct -- do not chase it as a mismatch.

**Ruled out, by experiment.** Text on Linux still does not look as good as on macOS, and the
following are NOT the cause -- do not re-try them:

| hypothesis | test | result |
|---|---|---|
| Blend space (gamma vs linear) | `WPF_WEBGPU_GAMMA=0/1` | Neither is right in both themes; linear fixes light and breaks dark |
| Direction-aware glyph curve | flip the exponent for light text | Dark theme washed out and illegible |
| GL scanout / swapchain format | run the same build on Vulkan | **Identical** to GL. Both backends also choose the same `RGBA8Unorm`, so `ChooseFormat`'s `gl` flag changes nothing here |
| Coverage rasterizer quality | `VerticalSamples` 4 -> 16 | 0.56% of pixels changed; AA gradation unchanged (113 vs 111 levels). Not the limiting factor |

The GL-scanout theory (and the offscreen + linearizing-blit plan built on it) is dead: if Vulkan and
GL render identically, the presentation backend is not the variable.

> **ANSWERED, further down: jump to "SOLVED: the text-gamma correction was applied twice".** The text
> was never aliased or soft -- it was ~23% too heavy, because the text-gamma LUT was applied on top of
> gamma-space compositing, which is already the thing that LUT emulates. The excess landed on
> partially-covered edge pixels, i.e. as a halo of alpha around every glyph. The narrative below is
> kept because each step eliminates a real hypothesis, but note that its central assumption -- that a
> difference this visible had to be a SCALE or a RESAMPLE -- is what sent it round in circles.

**The cause is NOT known** *(at the time this was written)*. Rendering at `WPF_LINUX_FORCE_SCALE=2`
makes text look right in both themes, which pointed at DPI -- but **1x on macOS looks good and 1x on
Linux does not**, with the same managed rasterizer and the same 1x resolution. So resolution is not the
variable and neither is the text code. There is no CoreText path on macOS either (the only mention in
the tree is a comment about a possible future port), so both platforms rasterize glyphs identically.

More samples clearly HELP on Linux (that is what 2x shows) but macOS does not need them, so the
deficiency is something Linux-specific between the rendered buffer and the screen -- not the glyph
rasterizer itself. (That last sentence was right, and "between the rendered buffer and the screen"
turned out to include the hypervisor.)

**Compositor resampling: ruled out.** `WAYLAND_DEBUG=1` shows buffer, viewport and scale all agreeing
at 1:1, so the surface reaches the screen unscaled:

```
wp_fractional_scale_v1.preferred_scale(120)   -> 120/120 = 1.0
wl_surface.set_buffer_scale(1)
wp_viewport.set_destination(1014, 731)        -> buffer is 1014x731 too
```

**"Aliased" is the wrong word -- it is SOFT.** Reading the actual pixels of small body text (crop the
sink dump and print values) shows proper grayscale antialiasing: 137 distinct intermediate levels in a
112x16 crop, e.g. `39 129 255 ... 144 53 88`. The masks are not aliased. What they are is SOFT: stems
land spread across two pixels instead of snapped to one, giving low-contrast small text. That is the
signature of UNHINTED rendering at fractional positions, and it explains why 2x appears to fix it (a
two-pixel stem is proportionally half as soft at twice the size).

`Segoe UI` resolves to **Liberation Sans** on Linux and **Helvetica Neue** on macOS (`fc-list` shows
Selawik/Helvetica/Arial all absent here, so the table falls through to Liberation Sans). The fonts
therefore genuinely differ between the two platforms.

**The controlled test that decides it:** Cascadia Code is now bundled on BOTH platforms, so it is the
same font file either side. Compare the gallery's Cascadia text (the code samples) on macOS and Linux:

* soft on Linux, crisp on macOS -> SAME font, different result, so it is the RENDERING (hinting /
  stem snapping / subpixel positioning), and the font substitution is a red herring;
* soft on both -> the font choice was the difference, and the fix is the substitution table.

**Selawik is now bundled (but was NOT the cause).** `FontFactoryState` lists
`Selawik` as the first substitute for `segoe ui` -- it is Microsoft's metric-compatible open
replacement, i.e. exactly the right face -- but there is no Selawik anywhere in this repo and none
installed on a normal Linux box. So the chain falls through to Helvetica Neue on macOS and all the way
to **Liberation Sans** on Linux (Arial metrics, different weight and spacing from the UI's design).

That was the SAME defect as the missing Cascadia Code: a substitution table naming a font the head did
not bundle. **Fixed** -- Selawik 1.01 (SIL OFL 1.1, github.com/microsoft/Selawik) is vendored in
`sdk/WpfWebGpu.Sdk/web/fonts/` as `Selawik-{Regular,Semibold,Bold,Light,Semilight}.ttf` with its
licence beside it, and deployed by a `Selawik*.ttf` glob so added weights need no further edit. Every
head now renders the UI in the face the substitution was designed around, on macOS and Linux alike.

`eng/check-font-substitutions.py` guards against a fourth occurrence: it fails when the FIRST name in
any chain is neither bundled nor OS-provided. Run it after touching FontFactoryState's table.

**Bundling it did NOT fix the appearance.** Verified with the sdk-check probe -- `UI FONT:
resolved=True face='Selawik'` -- and text still looks wrong, so the substituted face was never the
cause. Keep the change (it is the face the UI was designed around), but the defect is elsewhere.

**This leaves a clean controlled experiment.** The SDK deploys Selawik to every unix head, so macOS and
Linux now render the UI from the SAME font file, with the same managed rasterizer, at the same 1x.
macOS looks right and Linux does not, which isolates the defect to the rendering pipeline -- not the
font, not the resolution, not the compositor (all measured, above).

**The mechanism, located.** The device-space coverage path quantises a glyph's subpixel position to
HALF-pixel phases (`WgpuSceneRenderer`, ~line 2890):

```csharp
float qy = MathF.Round(dy * 2f) * 0.5f;   // snap to nearest 0.5 px
oy = MathF.Floor(qy);
phaseY = qy - oy;                          // 0 or 0.5
```

So about half of all glyphs are rasterized at a 0.5-pixel offset, which spreads a stem across two
pixels -- exactly the measured symptom, and why 2x halves its visibility (the offset is half as large
relative to the glyph).

**Whole-pixel snapping was tried and is WRONG.** Forcing `phaseX`/`phaseY` to 0 for glyphs (whole-pixel
X and baseline) does not sharpen text -- it destroys it. The pixel row across small text goes from real
glyph structure to a uniform grey smear:

```
before:  255 255  39 129 255 255 255 144  53  88 255 148  41 182
after:   255 255 255 255 255 255 255 175 122 123 122 123 123 122   <- mush
```

The mask cache keys and the quad offset are computed FROM the phase, so changing the snap without
changing everything that consumes it leaves the rasterized mask and the position it is drawn at
disagreeing, and the result is resampled to porridge. Any future attempt has to move the mask
geometry, the cache key and the quad offset together -- and note the half-pixel grid is deliberate:
the comment above it records that per-glyph vertical snapping caused a "sunken letters" bug, which is
why the baseline is snapped once per run instead.

**A measured Linux profile now exists to diff against macOS.** `WPF_TEXT_LOG=1` dumps every glyph
placement (device offset, snapped position, sub-pixel phase, scale) through the sink log. On the Linux
gallery, ~135k placements in 20s give:

```
phaseX: 77240 @ 0.000 | 57416 @ 0.500      <- HALF-PIXEL, only two variants; 43% straddle
phaseY: 0.011 0.024 0.056 0.102 0.127 ...  <- effectively CONTINUOUS (~255 buckets)
scale = 3.000
```

**The macOS profile has now been measured the same way** (gallery, `WPF_TEXT_LOG=1`, 4.9k placements
on a 1x external display, window surface 1014x730):

```
phaseX: 2369 @ 0.000 | 2488 @ 0.500        <- HALF-PIXEL, only two variants; 51% straddle
phaseY: 0.012 0.014 0.020 0.021 0.026 ...  <- effectively CONTINUOUS (116 buckets in 4.9k samples)
scale = 1.000
```

Diffing the two answers both questions:

1. **The axis asymmetry is NOT the differentiator.** macOS snaps X to the same two variants and keeps
   Y continuous, exactly as Linux does -- and it straddles pixel boundaries slightly MORE (51% vs
   43%) while looking BETTER. So the shared placement path is not what makes Linux muddy. Fixing the
   asymmetry may still be worth doing on its own merits, but it cannot explain the platform gap, and
   the "stems spread across two pixels" theory is now eliminated along with the earlier ones.
2. **The FONT is not the differentiator either.** Installing Selawik on the Linux box (so both
   platforms resolve "Segoe UI" to the same metric-compatible, hinted face) did not help -- Linux
   still looks bad. Candidate 1 below is therefore eliminated too.

**What DOES reproduce it: a render scale that does not match the display.** Forcing the macOS gallery
to `--scale 3` on a 1x display (`WPF_MAC_FORCE_SCALE=3`) makes its text look bad in exactly the way
Linux does. The numbers confirm the render itself is unchanged apart from the scale:

```
macOS --scale 3: phaseX 2032 @ 0.500 | 2029 @ 0.000 (50% straddle), phaseY 109 buckets, scale = 3.000
                 target/surface 3042x2190 for a window that is still 1014x730 physical pixels
```

CocoaWindow.GetBackingScale is the single source of truth for the HwndTarget DPI scale, the client
rects AND the CAMetalLayer contentsScale, so forcing 3 makes WPF render a 3042x2190 drawable for a
1014x730-pixel window and Core Animation filters it back down. Glyph coverage -- thin, high-contrast,
gamma-mapped -- is what visibly dies in that resample; large geometry survives it, which is why only
text "looks bad".

**This unifies every observation in this section, including the ones that looked contradictory:**

| | render scale | display scale | result |
|---|---|---|---|
| macOS default | 1 | 1 | sharp |
| macOS `--scale 3` | 3 | 1 | **mush** (downsampled) |
| Linux default | 1 | 2 (the Parallels host upscale -- see below) | **mush** (upsampled) |
| Linux `WPF_LINUX_FORCE_SCALE=2` | 2 | 2 | sharp |

Text degrades whenever the render scale differs from the display's true backing scale, in EITHER
direction -- macOS shows the too-high case, Linux's "2x fixes it" is the too-low case. That also
explains why more samples "help" on Linux without the rasterizer being at fault: 2x is not adding
quality, it is removing a mismatch. **The `display scale` column stayed a `?` for both Linux rows and
was never resolved, because there was nothing to resolve: Linux renders 1 into a 1:1 buffer on a 1:1
display. 2x "fixing" it was 2x diluting a 23% ink excess, not correcting a mismatch.**

**RESOLVED -- there is no 3. The render scale on Linux IS 1, and the `scale = 3.000` lines were a
misread of this log.** `WPF_TEXT_LOG` did not say which PASS a placement belonged to, and it
interleaves the window's pass with supersampled brush/offscreen realizations. Re-measured on the
gallery with the pass now tagged (`target=WxH`, added for exactly this reason):

```
128571  scale=1.000  target=1014x731     <- the window. 1x, matching the display.
  5812  scale=1.000  target=264x240      <- an offscreen layer (a card), also 1x
    26  scale=3.000  target=1024x600     <- a DrawingBrush realization, supersampled x3
```

The 3 is `const int supersample = 3` in `MilcoreEngine.RasterizeBrushSources` (~line 1623): a tile
brush motif is deliberately rasterized at 3x into its bitmap so tiling/scaling it stays crisp, and
the wrapper transform it renders through is therefore a 3x scale. The recurring `scale = 2.000` seen
in earlier runs is the same thing one step down -- `const int supersample = 2` for a VisualBrush
realization (~line 2483); its 11 glyphs are literally the word "VisualBrush" in the brushes card.
Both are correct and both are platform-independent.

Why it read as a platform fault: brushes realize BEFORE the first present, so those 26 lines are the
FIRST 26 lines of a 175k-line log -- whoever read the head of the file saw nothing but `scale =
3.000`. **Always group by `target=` before drawing a conclusion from this log.**

So the earlier table's "Linux default | render scale 1" row was right all along, the DPI chain is
correct end to end, and there is no scale mismatch to fix. What follows is the (still valid) evidence
that got us here; the 3-specific hunt below is dead.

Also re-checked while here, and NOT the cause:

* **GPU vs CPU coverage rasterizer.** `WPF_WEBGPU_CPU_RASTER=1` vs the default GPU path produce
  BYTE-IDENTICAL text (same crop: 110 levels, 362 ink px, 275 partial-coverage px, mean ink 94.7 in
  both). The fs_coverage shader on virgl/GL is not mis-rasterizing glyphs.
* **Whole-pixel baseline snapping.** Changing the run baseline snap from the half-pixel grid to
  `MathF.Round(bdy)` changes 2543 of 741234 pixels and nothing visible: layout puts baselines on
  integer DIPs at 1x, so both grids round to the same row. The continuous `phaseY` in the log is each
  glyph's fractional INK-BOX TOP, not a fractional baseline -- it is not evidence of a snapping bug.
  Reverted; don't re-try it as a sharpness fix.

**A FreeType comparison pointed the right way but was read wrong at first.** Rendering the SAME font
file at the SAME size (Selawik-Semibold 13px, "Interactive") and comparing pixel counts DOES isolate
the defect -- but only once coverage is normalised against the run's ink colour rather than black.
Scored against black it looked like an antialiasing-quality gap ("twice as many partial-coverage
pixels"), which was an artifact; scored correctly it is a 23% TOTAL-INK excess with a normal
distribution. See "SOLVED" below for the corrected numbers and what they turned out to mean.

**MEASURED: the presentation path does not touch the pixels.** `WF_SURF_DUMP` reads back the REAL
swapchain texture rather than re-rendering offscreen, so diffing it against `WPF_WEBGPU_SINK_DUMP` of
the same frame answers "does what we render survive being handed to the compositor". On the Vulkan
backend, where the surface allows the copy:

```
vksurf.png (swapchain readback)  vs  vkoff_040.png (offscreen re-render), 1014x731
per-channel difference extrema: ((0,0), (0,0), (0,0))
differing pixels: 0 of 741234
```

Byte-identical. Nothing re-encodes, resamples or gamma-shifts between the render and the buffer the
compositor scans out. So the "GL scans a UNORM swapchain out too dark" family, and the unimplemented
`gl` branch in `ChooseFormat` it rests on (the `gl` flag reaches only the log line -- Linux gets
`chosen=RGBA8Unorm` regardless), cannot be what makes text look worse: there is nothing left between
the pixels we verified and the glass except the compositor, which treats our buffer exactly as it
treats every other client's.

Two caveats worth stating rather than hiding: the readback is on **Vulkan** (llvmpipe -- the GL
surface refuses `CopySrc`, see below), and it proves the BUFFER is right, not that the monitor shows
it right. But GL and Vulkan agree on text to within rounding, so the Vulkan result carries:

```
'Interactive' (Selawik-Semibold 13px), same crop, GL vs Vulkan offscreen:
  GL  110 levels, 362 ink px, 275 partial-coverage px, mean ink 94.7
  VK  121 levels, 364 ink px, 279 partial-coverage px, mean ink 94.8
  per-pixel deltas across the stems: <= 3/255
```

**`WF_SURF_DUMP` used to ABORT the process on this head** -- the OpenGL surface advertises
`COLOR_TARGET` only, and configuring it with `COPY_SRC` anyway is a wgpu validation error, which
panics across the FFI boundary and kills the app being diagnosed. It now asks
`wgpuSurfaceGetCapabilities().usages` first and logs that the readback is unavailable instead. That is
why the Vulkan backend had to be forced (`WPF_WEBGPU_BACKEND=vulkan`) for the measurement above; note
it selects the llvmpipe CPU adapter here, so the gallery runs at ~8fps against ~26fps on GL/virgl.
That is the diagnostic being slow, not a regression.

**So the platform gap is cornered OUTSIDE the rendered buffer entirely.** Scale, buffer/viewport
ratio, rasterizer (GPU and CPU), font, blend space, backend, placement phases and the swapchain
contents are all eliminated by measurement -- everything from the scene graph to the bytes handed to
the compositor is verifiably what we intended.

## SOLVED: the text-gamma correction was applied twice

Text on the Linux head was not aliased and was not soft. It was **too heavy** -- every glyph carried
about 23% more ink than its outlines contain, and the excess landed on partially-covered EDGE pixels,
which reads as a dark halo around each glyph. `EmitCoverageMask` remapped glyph coverage through the
text-gamma LUT (`cov' = cov^(1/2.2)`) whenever `(s_gammaComposite || _srgbOutput)`, i.e. **including
in gamma-space mode, which is the default and is already the thing that LUT emulates.**

The LUT exists to make text match WPF when this engine blends in LINEAR space: an sRGB target decodes
to linear on read and re-encodes on write, so coverage blends linearly and needs the correction. In
gamma-space mode (colours pre-encoded at their source, blended on those encoded values, plain-UNORM
target) the blend IS the correction, and doing both counts it twice. The gate is now `_srgbOutput`
alone, which is exactly "this pass blends linearly" -- `_srgbOutput` can only be true when
`s_gammaComposite` is false (see its two assignment sites).

Measured on the gallery, one 13px word, coverage normalised against the run's ink colour:

| | full-coverage px | partial px | **total coverage** |
|---|---|---|---|
| before (LUT applied twice) | 136 | 226 | **268.7** (+23%) |
| after (LUT gated on `_srgbOutput`) | 110 | 225 | **215.6** |
| FreeType rasterizing the SAME outlines at the SAME size | | | 219.2 |

After the fix we are within 1.6% of the glyph geometry.

**What this does NOT explain, and nobody should pretend otherwise: why macOS looked BETTER.** The
double correction was not Linux-specific -- `s_gammaComposite` defaults true on Metal too, so the
macOS head was applying the same 23% ink excess and was judged to look fine. So this fixes a real
defect on every head, but the *platform gap* that started the investigation is still unaccounted for.
The untested hypothesis is display density: a 23% excess on a partially-covered edge pixel is a
sub-pixel-wide halo, which is far less visible at 2x than at 1x, and the macOS head normally runs on a
Retina panel. Test it by rendering the SAME app at 1x on both and diffing the dumps numerically --
that comparison has still never been done, and every conclusion about "macOS looks better" rests on
eyeballing.

**Coverage gap that let this ship, now closed.** `WgpuInterop.RenderBaselineTest`'s `text-run` scene
is unchanged by this fix (0 pixels) and `WgpuInterop.GammaTest` still passes -- because both exercise
the LINEAR / sRGB path, and NEITHER covered the default display path (gamma-space compositing into a
plain-UNORM target), which is what every real window uses. That is the hole the bug lived in for the
life of the renderer.

`WgpuInterop.TextCoverageTest` now covers it. It asserts a physical invariant rather than a golden
image, so it needs no per-platform baselines and cannot rot: **anti-aliasing conserves area**, so
summing coverage over a rendered glyph run must equal the area enclosed by its outlines. The expected
area comes from the FONT (shoelace over the flattened contours, signed per glyph so counters subtract),
independently of anything the renderer does, so it cannot agree with a broken rasterizer by
construction. Verified in both directions:

```
fixed renderer : expected=1279.6  measured=1279.3  ratio=1.000   PASS
pre-fix gate   : expected=1279.6  measured=1450.6  ratio=1.134   FAIL (as it must)
```

Any change that re-weights coverage -- a stray gamma curve, a contrast boost, a double-applied LUT --
breaks that equality while still LOOKING like text, which is exactly the class of defect an
"is it legible" test cannot catch.

**The +13.4% there is at em 32; the gallery measured +23% at em 13.** The excess scales INVERSELY with
glyph size, because it lands on partially-covered edge pixels and those are a smaller fraction of a
larger glyph. That is independent support for the display-density hypothesis above: the same
double-applied LUT would have cost the macOS head visibly less on a 2x panel than it cost Linux at 1x.
It is support, not proof -- the 1x-on-both dump diff is still the test that would settle it.

**Normalise against the ink colour, not against black.** This is why the defect survived so many
passes: the gallery's ink is `#222833`, which is grayscale **39**, so a FULLY covered pixel reads 39
and never 0. Every "count the dark pixels" comparison against a black reference therefore scored our
fully-inked pixels as partial coverage and our over-inked edge pixels as normal, and made a 23% ink
excess look like an antialiasing-quality problem. Coverage is `(255 - v) / (255 - ink)`; measure that.

**Corrections to earlier entries in this section, all from the same mistake:**

* "our output puts twice as many pixels at partial coverage as FreeType" -- **wrong**, an artifact of
  the black-ink baseline. Normalised, our partial-coverage share is 62% against FreeType's 68-72%
  unhinted and 58% hinted: our rasterizer's coverage DISTRIBUTION is fine, better than unhinted and
  close to hinted. Only the total was wrong.
* "the unhinted rasterizer is the remaining quality ceiling" -- overstated on that evidence. Hinting
  is still absent and still worth having, but it is not what made this look bad.

**Also ruled out, by measurement, on the way here** (do not re-try):

| hypothesis | test | result |
|---|---|---|
| A Parallels host upscale (guest framebuffer resampled to a Retina panel) | guest at 3840x1200 "More Space", GNOME at 100%, `WAYLAND_DEBUG` | Dead. `wl_output.scale(1)`, `preferred_scale(120)`, `set_buffer_scale(1)`, `set_destination(1014,731)` with a 1014x731 buffer -- 1:1 end to end, no resample anywhere. VS Code renders text well on the same display, which is the control |
| The presentation path altering pixels | `WF_SURF_DUMP` swapchain readback vs `WPF_WEBGPU_SINK_DUMP` | Byte-identical, 0 of 741234 pixels differ |
| The GPU coverage rasterizer | `WPF_WEBGPU_CPU_RASTER=1` vs GPU | Byte-identical text |
| Whole-pixel baseline snapping | `MathF.Round(bdy)` | No-op at 1x: 2543 of 741234 pixels change. Baselines already land on integer DIPs |
| Whole-pixel glyph X snapping | `MathF.Round(dx)` for runs | Not adopted -- the isolating A/B produced non-comparable frames (the gallery's scroll state differs between runs), so it is unvalidated either way. If retried, pin the scene first |

**Historical: the Wayland backend was never the source of the 3 either.** Every scale the compositor
reports is 1:

```
wl_output@6.scale(1)
wp_fractional_scale_v1@28.preferred_scale(120)   -> 120/120 = 1.0
wl_surface@27.preferred_buffer_scale(1)
```

`WaylandWindow.GetBackingScale()` prefers `_scale120 / 120.0`, so with `preferred_scale(120)` it
returns **1.0** -- correct, and matching the display, and that is what the window's pass actually
renders at (measured above).

Every source OUTSIDE WPF also says 1, and so does WPF:

| source | value |
|---|---|
| `wl_output.scale`, `preferred_scale`, `preferred_buffer_scale` | 1 |
| GNOME `text-scaling-factor` / `scaling-factor` | 1.0 / 0 |
| `Xft.dpi` | 96 |
| the gallery sample | no global transform (only per-shape ones) |
| `WPF_TEXT_LOG`, `target=1014x731` (the window's pass) | 1.000 |

`WPF_LINUX_FORCE_SCALE` was unset for those runs, `HwndTarget`'s off-Windows path sets
`CurrentDpiScale` straight from `platformWindow.GetBackingScale()`, and every link in that chain is
measured at 1.0. The DPI chain is correct end to end; nothing manufactures a 3.

Remaining candidates:
1. ~~**A different font resolves.**~~ Eliminated: the sink log shows Linux resolving the bundled
   `Selawik-{Regular,Semibold,Bold}.ttf`, the same files macOS gets.
2. **No hinting / stem snapping**, which the managed rasterizer does not implement at all. Still true
   and still worth having, but NOT what made this look bad -- normalised, our coverage distribution
   is better than unhinted FreeType (62% partial vs 68-72%) and close to hinted (58%). See "SOLVED".

**Nothing is left of the obvious explanations.** Blend space, backend, rasterizer (GPU and CPU,
byte-identical), DPI, render scale, font and compositor scaling are all eliminated by experiment, yet
2x rendering fixes it on Linux and macOS needs no such help at 1x. Everything measurable from the
rendered buffer now agrees between the platforms, so the next thing to establish is whether the buffer
survives PRESENTATION intact on Linux -- capture the window off the screen (portal screenshot; the
`org.gnome.Shell.Screenshot` D-Bus method is AccessDenied to non-interactive callers) and diff it
against the `WPF_WEBGPU_SINK_DUMP` PNG of the same frame. Equal means the defect is upstream after
all and the rasterizer comparison has to be redone glyph-mask-by-glyph-mask against macOS; different
means it is the swapchain/scanout, and `ChooseFormat`'s unimplemented `gl` branch is the first
suspect.

Do NOT reach for SSAA or 2x glyph coverage until that comparison is done -- more samples demonstrably
mask this, and masking it would end the investigation with the cause still unknown.

`WPF_WEBGPU_GAMMA=0/1` still pins the mode either way, which is how both experiments above were A/B'd.

> **Also open:** the GPU coverage rasterizer (`GpuRasterizeCoverage` -> `GpuCoveragePass` -> shader)
> takes a `bool gamma` and applies only the one curve. Not visible under VirGL, which forces CPU
> rasterization, but it will show on real GL/Vulkan hardware.

## Mica and window backdrops

Not implemented, and not planned. `WindowBackdropManager.IsSupported` declines material backdrops off
Windows except macOS, which has `NSVisualEffectView`. GNOME/mutter exposes no blur-behind protocol at
all (KDE has one; there is no portable equivalent), so there is nothing to call. It declines cleanly
rather than stripping the window background, so a Fluent app gets a solid window rather than a
transparent one.

## Backend selection

Which backend reaches the GPU is not knowable up front, so `WgpuContext` tries Vulkan, then GL, and
rejects a software adapter while a hardware one is still available. Pin it with
`WPF_WEBGPU_BACKEND=vulkan|gl|all`.

**Under VirGL** (Mesa's virtio-gpu driver — Parallels, QEMU, GNOME Boxes) the GPU path rasterizer
mis-renders: closed paths lose an edge and the fill escapes to its bounding box. This is a bug in
virgl's guest-to-host GLSL translation, not in the shader: the same build passes on Vulkan, and on
GL under llvmpipe (`LIBGL_ALWAYS_SOFTWARE=1`). The renderer detects a virgl adapter and falls back
to CPU path rasterization; the GPU still does compositing, blending, effects and text. Real Linux GL
hardware is unaffected and keeps the GPU rasterizer. `WPF_WEBGPU_CPU_RASTER=0` reproduces it.

## Debugging

| lever | shows |
|---|---|
| `WAYLAND_DEBUG=1` | every protocol request and event, decoded — the single best tool here |
| `WPF_WAYLAND_LOG=1` | connection, globals, popup placement arithmetic |
| `WPF_WEBGPU_SINK_LOG=<file>` | surface configure, per-frame present |
| `WPF_WEBGPU_WGPU_LOG=debug` | wgpu's own backend/adapter selection |
| `MESA_SHADER_CAPTURE_PATH=<dir>` | the GLSL Mesa actually receives (how the virgl bug was isolated) |
| `WPF_LINUX_POLL_PUMP=1` | drive the dispatcher with a periodic slice instead of the compositor fd |

## Verification

The suite is `eng/run-tests.sh` (`eng\run-tests.cmd` on Windows), which runs two xunit.v3 projects
on Microsoft.Testing.Platform, so `dotnet test` and the VS Test Explorer discover them too:

| project | covers | needs |
|---|---|---|
| `src/.../WgpuInterop/tests/WgpuInterop.Tests` | the renderer: geometry, brushes, text, compositing, shaders | a GPU adapter (skips with a reason without one) |
| `src/.../tests/CrossPlatform/Wpf.Platform.Tests` | the per-OS windowing heads: DPI chain, sizing, geometry, screen bounds, popups, clipboard, keyboard mapping | a display, and the built fork (excludes itself otherwise) |

`Wpf.Platform.Tests` is PUBLIC-SIGNED with the ECMA key (`eng/snk/ECMA.snk`) because WindowsBase
grants it friend access for the internal `PlatformClipboard` seam, and a strong-named assembly only
accepts strong-named friends -- without it the build fails with CS0281. The Arcade UnitTests projects
get this from `StrongNameKeyId=ECMA`; this project sits outside Arcade on purpose, so it says so
itself.

**Keyboard: translation is tested, delivery is not.** `wl_keyboard` events come from the compositor
and a client cannot synthesise them, so there is no way to test input DELIVERY unattended. The
translation half is a pure function this suite supplies its own inputs to:
`KeyboardMappingTests` compiles an inline keymap and drives the `WlXkb` P/Invoke binding directly.
That is worth having because a DllImport compiles perfectly and fails at run time -- the same hazard
the protocol tables carry. It pins down `EvdevOffset` (XKB keycodes are evdev + 8; forgetting it
silently shifts the whole keyboard rather than crashing) and that control keys DO yield text
(Escape is U+001B), which the layer above has to filter or every Escape types a control character
into a TextBox.

**The clipboard write tests skip on an idle Wayland session, and that is correct.**
`wl_data_device.set_selection` needs a serial from REAL USER INPUT; an unattended run has produced
none, so `WaylandClipboard` defers the publish and a set genuinely cannot be read back. A client may
not silently seize the clipboard. They run for real on macOS and Windows. The READ path
(`ReadPath_IsSelfConsistent_AndNeverThrows`) needs no ownership and runs everywhere.

Both are plain `net10.0` and run on Linux, macOS and Windows. What a machine cannot exercise SKIPS
with a stated reason rather than passing -- a suite that returns green on a box with no GPU reports
coverage it did not deliver, which is worse than no suite.

The wrappers exist only to set `DOTNET_ROOT`: xunit.v3 requires a native apphost, and an apphost
resolves the runtime through `DOTNET_ROOT` or a system install, neither of which knows about this
repo's private `./.dotnet`. Where the SDK is installed normally, plain `dotnet test` is enough.

**Migration complete for everything that can be a test.** These replace the standalone
`WgpuInterop.*Test` apps, which each had their own `Main` and their own copy of the MILCMD builders --
byte layouts mirroring generated native struct offsets, so a drifted copy did not fail to compile, it
silently decoded as a different command while the test still "passed". 53 apps are folded in and
deleted; the shared builders now live in `Harness/MilCmd.cs` (~45 commands) and
`Harness/D3D9Bytecode.cs`.

**Six apps deliberately survive, because they are TOOLS rather than tests:**

| app | why it stays |
|---|---|
| `LiveCompositionTest` | on-screen integration: needs a real window and `SurfaceDemo` |
| `CurveFidelityTest`, `ScaleProbe` | measurement harnesses; print trend tables under `--report` / `--cost` / `--segscale` / `--strokegeom`. Their ASSERTIONS are in the suite (`ScaleStabilityTests`); the reporting has nowhere to live in a test runner |
| `SmokeTest` | drives the raw wgpu binding with hand-built descriptors, no window and no renderer |
| `AdapterProbe`, `LocalCacheProbe` | pure diagnostics, no pass/fail at all |

Golden-image baselines live beside the source at
`WgpuInterop.Tests/Baseline/baselines/{gpu,cpu}` so they are reviewable in a diff. Two environment
variables replace the old CLI flags:

```bash
WPF_BASELINE_UPDATE=1        # rewrite the baselines for the ACTIVE raster mode
WPF_BASELINE_WRITE_ACTUAL=1  # also dump actual+diff PNGs beside them
```

`WPF_BASELINE_UPDATE` reports every scene as SKIPPED rather than passed: a run that rewrote the
baselines has verified nothing. A MISSING baseline fails rather than being written silently -- else a
new scene is "guarded" by whatever it happened to render first, bug included.

```bash
eng/run-tests.sh                                                  # THE test suite: renderer + platform heads
eng/run-tests.sh --filter Text                                    # one area
dotnet run --project src/.../tests/WgpuInterop.SmokeTest          # raw wgpu binding, no display
dotnet run --project src/.../tests/WgpuInterop.ScaleProbe -- --cost        # flattening cost table
dotnet run --project src/.../tests/WgpuInterop.CurveFidelityTest -- --report  # curve fidelity trend
dotnet run --project src/.../tests/WgpuInterop.AdapterProbe      # what this box's GPU offers
dotnet run --project src/.../tests/WaylandSpike -- --frames 20    # a Wayland window, no WPF
eng/run-linux.sh -- --auto-popup                                  # popup placement, unattended
eng/run-linux.sh -- --auto-clipboard                              # clipboard round-trip (in-process)
eng/run-linux.sh -- --auto-desktop                                # SystemParameters, colors, fonts, imaging, print, MessageBox
eng/run-linux.sh -- --jpeg-out DIR                                # write PNG/JPEG files for external checking
eng/run-linux.sh -- --auto-dragdrop                               # DoDragDrop source side
eng/run-linux.sh --media -- video.webm --auto 13                  # media transport, unattended

# The real third-party app, the final gate. WPF-Samples' WPFGallery builds against the packaged SDK
# through a WPFGallery.Linux.csproj added ALONGSIDE the original (net10.0, not net10.0-windows), so
# the upstream sample still builds for Windows. Repack + evict first or the build silently uses the
# last-packed SDK:
#   ./.dotnet/dotnet build src/.../WgpuInterop/WgpuInterop.csproj -c Release   # assemble.sh does NOT build
#   sdk/WpfWebGpu.Sdk/assemble.sh && rm -rf ~/.nuget/packages/wpfwebgpu.sdk
#   ./.dotnet/dotnet build "../WPF-Samples/Sample Applications/WPFGallery/WPFGallery.Linux.csproj" -c Release
python3 eng/check-path-casing.py                                  # csproj paths vs the filesystem
python3 eng/check-font-substitutions.py                           # every preferred substitute is bundled or OS-provided

eng/build-sdk.sh -p:SkipBrowser=true                              # ...and PACK the SDK
dotnet run --project samples/wpf-linux-sdk-check -- --seconds 6   # consume the packaged SDK
```

## XAML

The markup compiler works off Windows. `samples/wpf-linux-sdk-check` carries the repo's only compiled
XAML (`XamlWindow.xaml` + code-behind), and building it runs the packaged `PresentationBuildTasks`,
which emits `XamlWindow.g.cs` and `XamlWindow.baml` on Linux; the BAML then loads at runtime through
`InitializeComponent`. `x:Class` code-behind, `StaticResource`, `Style` setters, `ControlTemplate`,
`ElementName` binding and `Click=` handler wiring all resolve.

This matters because **every other sample in this repo is deliberately code-only**, so the route
essentially every real WPF app takes was untested here until that file existed. If you touch the SDK's
markup wiring, this is the sample that will notice.

The runtime side is exercised separately by `--auto-desktop`: `XamlReader.Parse`, resource
dictionaries, styles and templates, pack URIs into the Fluent theme's BAML (411 keys), storyboards and
effects.

`wpf-linux-sdk-check` is the only sample that goes through the shipping path. Every other one
references the fork's assemblies straight out of `artifacts/bin`, which skips the package, its
RID-keyed native payload and the shim overlay entirely — so those can break without any other sample
noticing. It carries an empty `Directory.Build.props` so it builds like a consumer outside the repo
rather than inheriting the repo's output layout.

The `--auto-*` probes exist because several of these features were written and never actually
executed. Running them found five real defects: the samples that reference the fork by path were
missing `System.Private.Windows.Core` and `System.Formats.Nrbf` (so `Clipboard` threw
`FileNotFoundException` — the packaged SDK already carried them, which is why the SDK gate passed),
the clipboard read-back above, `SystemParameters.WindowGlassBrush`, the whole `BitmapDecoder` family,
and `PrintDialog`. Each was ordinary code that simply had never been run here, so **add a probe when
you add a feature** — a Linux arm that compiles proves very little. **Cross-application** clipboard
and drag still need a human: the in-process probe deliberately no longer exercises the
`wl_data_source.send` write path.

`WaylandSpike` is the one to reach for first when something is wrong at the platform level: it
brings a decorated window up and presents wgpu frames through `WaylandDisplay` alone, with no WPF in
the picture, so a protocol-table or handshake fault is debuggable in isolation.

`check-path-casing.py` exists because Windows and macOS are case-insensitive: a csproj can name
`System\windows\...` for a file that is really under `System/Windows/` and nobody notices until a
Linux build reports it as simply missing.

## Why Wayland-native, and not XWayland

XWayland would have been less work and would have reused the existing X11 surface path. It was not
taken because the head is meant for modern Linux desktops, where Wayland is the session and X11 is
the compatibility layer. The cost is the coordinate model above; the benefit is correct HiDPI
(including fractional scaling), no XWayland blurring, and native input.

`LinuxInterop` still contains an Xlib surface path for a genuinely-X11 session, but it deliberately
does **not** auto-fall-back when Wayland fails: that would silently create a window the input and
windowing layers know nothing about, which is worse than a clear error.
