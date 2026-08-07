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
| fonts (bundled) | Two families travel **with the app**, in a `fonts/` folder beside it: **Symbols** (the Fluent theme's glyph icons substitute onto it; no Linux or macOS box has Segoe Fluent Icons / Segoe MDL2 Assets, and their glyphs are in a private-use range no text font covers) and **Cascadia Code** (its programming ligatures; the fallbacks have none). The SDK deploys both for any unix desktop app — macOS included — and `SystemFontCatalog` probes the app-local folder on both. Samples that reference the fork by path do it themselves. |
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

**The cause is NOT known.** Rendering at `WPF_LINUX_FORCE_SCALE=2` makes text look right in both
themes, which pointed at DPI -- but **1x on macOS looks good and 1x on Linux does not**, with the same
managed rasterizer and the same 1x resolution. So resolution is not the variable and neither is the
text code. There is no CoreText path on macOS either (the only mention in the tree is a comment about
a possible future port), so both platforms rasterize glyphs identically.

More samples clearly HELP on Linux (that is what 2x shows) but macOS does not need them, so the
deficiency is something Linux-specific between the rendered buffer and the screen -- not the glyph
rasterizer itself.

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

Two things fall out:

1. **The axes are not treated alike.** Vertical keeps each glyph's exact sub-pixel offset; horizontal
   is snapped to two variants, so nearly half of all glyphs straddle a pixel boundary horizontally.
   That asymmetry fits the measured symptom (stems spread across two pixels) far better than any of
   the eliminated theories. Whether macOS shows the same split is THE question -- run the same command
   there and diff.
2. **`scale=3.000` is unexplained** and worth checking on its own; 1.0 was expected on a 1x display.
   If the scene is being rendered at 3x and resampled somewhere, that is a separate defect.

Caveat worth checking before assuming a platform difference: this code is shared, so establish whether
macOS actually takes this same path and the same phases -- if it does, the fix helps both platforms and
Linux only looks worse for some other reason.

Remaining candidates, if that is not enough:
1. **A different font resolves.** "Segoe UI" substitutes to Helvetica Neue/Helvetica on macOS but to
   Liberation Sans / DejaVu Sans on Linux (see FontFactoryState's table). DejaVu in particular is
   muddy at small sizes unhinted. Print the resolved face on both platforms before anything else --
   this costs nothing and would explain the whole difference.
2. **No hinting / stem snapping**, which the managed rasterizer does not implement at all. If macOS
   resolves a different face that happens to survive it better, (1) is really the cause.

**Nothing is left of the obvious explanations.** Blend space, backend, rasterizer sampling, DPI and
compositor scaling are all eliminated by experiment, yet 2x rendering fixes it on Linux and macOS
needs no such help at 1x. The next thing to establish is whether the two platforms are really
producing the same coverage for the same glyph: dump the same string's glyph mask on both (same font,
same size, same colour) and compare the bitmaps directly. If they match, the difference is downstream
of rasterization; if they differ, it is in the rasterizer or what feeds it (font resolution, hinting
flags, subpixel positioning, the em-size/transform actually used).

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

```bash
dotnet run --project src/.../tests/WgpuInterop.SmokeTest          # device path, no display
dotnet run --project src/.../tests/WgpuInterop.RenderBaselineTest # 11 offscreen scenes
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
