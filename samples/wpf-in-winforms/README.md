# WPF inside Windows Forms — `ElementHost` on WebGPU

The mirror of [`winforms-in-wpf`](../winforms-in-wpf). There a WinForms control tree is embedded in a
WPF app (`WindowsFormsHost`); here a **WPF element tree is embedded in a WinForms app**
(`ElementHost`), on this fork's cross-platform stack: Mono's managed `System.Windows.Forms` driven by
the `XplatUIWebGpu` driver, hosting WPF rendered by the managed WebGPU compositor.

`ElementHost` is **`System.Windows.Forms.Integration.ElementHost`**, shipped in
`WindowsFormsIntegration.dll` by `WpfWebGpu.Sdk` on every desktop head — same namespace, type name
and core API as the Windows-only original, so existing WinForms+WPF code compiles and runs unchanged:

```csharp
var host = new ElementHost { Dock = DockStyle.Fill, Child = myWpfControl };
Controls.Add(host);
Application.Run(new Form1());
```

This sample is an ordinary `Application.Run` app — no host loop, no windowing code, no compositor
code. The window, the message pump and the WebGPU present belong to `System.Windows.Forms` itself
(`WinFormsInterop/host`), exactly as real WinForms puts its own windows on screen.

![the sample](docs/wpf-in-winforms.png)

Left: ordinary WinForms controls, painted through the GPU-raster `System.Drawing` backend. Right: a
WPF element tree — rounded card, drop shadow, gradients, a rotating vector spinner, `Slider`,
`Button`, `TextBox` — inside an `ElementHost`. Both halves are composited by **one WebGPU render pass
onto one surface**.

## What it demonstrates

| | |
|---|---|
| WPF renders inside the WinForms window | the card, its shadow and its vector content are WPF, drawn by `WgpuSceneRenderer` |
| live WPF animation on WPF's own clock | the spinner runs off a WPF `DispatcherTimer` while WinForms is idle |
| WinForms → WPF | the trackbar drives a WPF `Slider`; the radios mutate a shared `SolidColorBrush`; the checkbox adds/removes a WPF `DropShadowEffect` |
| WPF → WinForms | clicking the WPF button updates a WinForms `Label` |
| real input on both sides | the WPF tree is a real `HwndSource`, so it hit-tests, hovers, captures the mouse and takes the keyboard; the WinForms text box keeps its own caret |

## How it works

The WPF side is **ordinary WPF**. The element tree is the `RootVisual` of a real `HwndSource` sized
and positioned to the hosting control, so layout, hit-testing, input, focus, capture, popups,
animation and DPI all work exactly as in a standalone WPF app. None of that is re-implemented.

Only *presentation* changes, via a small bridge added for this:

- `HostedWpfContent.Claim(hwnd)` (in `WgpuInterop`) tells `WpfCompositionSink` **not** to give that
  window a swap chain of its own.
- The sink instead **publishes** the window's decoded root `SceneVisual` each frame.
- `ElementHost.Collect` hands that scene to the WinForms host, which appends it to the driver's own
  control scenes.

So there is no intermediate bitmap, no readback, no second swap chain, and no z-order fight between
two native windows — the WinForms controls and the WPF tree end up in the same scene graph. This is
exactly the shape `EmbeddedContent` already had for the opposite direction, and it now has a
counterpart, `HostedWpfContent`.

The host shell (`Win32Host`, `CocoaHost`, `WgpuPresenter`, `PresentationHost`) lives in the WinForms
assembly, and the driver's message loop drives it from `GetMessage`'s idle path — which is what makes
`Application.Run` behave like real WinForms. `ElementHost` plugs into it through `EmbeddedScenes`, a
registry the host consults for extra scenes each frame and which is empty in a plain WinForms app.

### Per-platform

`ElementHost` is available on every head; what differs is what "a child window" means:

| Head | Hosted window | Presentation | Status |
|------|---------------|--------------|--------|
| Windows | real `WS_CHILD` HWND, never painted | claimed → composited into the host's single frame | ✅ verified (this sample, `selftest`) |
| macOS / Linux | borderless platform window (NSWindow / `wl_surface`) owned by the host, positioned over the control — the same construct WPF popups already use there | presents its own surface as an overlay pinned to the control | builds; ⚠ not run — no Mac/Linux box here |

Both use the same code path for placement, sizing, lifetime and the WPF tree itself; only
`ElementHost.CompositeIntoHostFrame` differs. Input is native in both cases (Cocoa and Wayland
deliver into `HwndMouseInputProvider` exactly as for any WPF window). Moves and resizes go through
WPF's own portable windowing seam (`MS.Win32.UnsafeNativeMethods.SetWindowPos`), not `user32`.

## Run

```powershell
dotnet build WpfInWinForms.csproj -c Release
.\bin\Release\net10.0\WpfInWinForms.exe 60          # interactive for 60s (0 = until closed)
```

Click the WPF button and the WinForms button, drag the trackbar, toggle the radios and the checkbox,
type in either text box.

### Self-test

```powershell
.\bin\Release\net10.0\WpfInWinForms.exe 30 selftest          # exit 0 = pass
.\bin\Release\net10.0\WpfInWinForms.exe 30 selftest mouse    # also drives the real pointer
```

`selftest` clicks the WinForms button through the driver, drives the trackbar, flips the radio and
checkbox (the WinForms → WPF paths), saves before/after frames, and asserts:

- the WPF tree is live,
- where the WPF button is **drawn** is where it is **clickable** (the scene is composited at the
  control's position in the host's frame, while input arrives at the hosted window's own client rect
  — nothing ties those together automatically),
- the host's client area equals the presented surface, so the compositor is not **rescaling** the
  frame on the way to the screen. That one is not cosmetic: the window was originally created with
  the form size as the *window* size, so the client area came out smaller (864×521 for an 880×560
  form), the frame was stretched into it, and every hit-test drifted — a click landed on the control
  *above* the cursor, worse the further down the window you clicked.

It deliberately needs **no mouse**, so it is safe to run on a machine somebody is using. Add `mouse`
to also click the WPF button with the physical pointer (`SetCursorPos` + `SendInput`) — a stronger
end-to-end proof, but it loses races against a human and against any other UI test running at the
same time.

Synthetic window messages are not an option for the WPF half: WPF drops mouse messages sent to an
inactive window that has neither capture nor the real cursor over it ("spurious mouse event").

Diagnostics: `WF_TRACE_INPUT=1` traces the messages the hosted window sees, how WPF routed them, and
the scene/window placement; `WF_WEBGPU_SAVE=<png>` sets where the self-test writes its frames.

## Prerequisites

`build.cmd -configuration Release -platform AnyCPU -warnAsError 0` (populates `artifacts/bin`), and
the `wgpu-native` binary for this RID under `src/Microsoft.DotNet.Wpf/src/WgpuInterop/native/<rid>/`.
