# Inspecting the visual tree

On Windows you open Visual Studio's Live Visual Tree, which an app exposes through the
`IVisualTreeService3`/`XamlDiagnostics` COM contract. This fork does not implement that
contract, and there is no Visual Studio on the other heads anyway. What it has instead is a
**Chrome DevTools Protocol endpoint**: any Chrome can attach to a running app and browse its
visual tree in the Elements panel.

It is off unless asked for, bound to loopback, and ships with the SDK.

```sh
WPF_DEVTOOLS=1 dotnet MyApp.dll
```

then in Chrome: `chrome://inspect` → **Configure…** → add `localhost:9222` → **inspect**.

`eng/run-gallery.sh --devtools` does the same for the macOS gallery loop.

## Why CDP

The mapping is close to exact, and in one place it is better than what Windows gives you:

| DevTools | WPF |
|---|---|
| DOM tree | the `Visual` tree, via `VisualTreeHelper` |
| element attributes | the node's **local** dependency-property values |
| Computed pane | **every** dependency property on the node |
| Styles pane | one rule per `BaseValueSource`, in WPF's precedence order |
| box model | `RenderSize` + `Margin`/`BorderThickness`/`Padding` |
| hover highlight | an `Adorner` on the real window |
| element picker | `VisualTreeHelper.HitTest` |
| screencast | the renderer's composed frame (`RenderTargetBitmap` as fallback) |

The Styles pane is the part worth the trouble. `DependencyPropertyHelper.GetValueSource`
says where a value came from — Local, Style, ParentTemplate, StyleTrigger, Inherited — and
DevTools renders a list of rules with overridden declarations struck through. So a
`Background` that is wrong shows you *which* of the eleven places it could have come from
actually won, which the Live Visual Tree does not.

A second payoff: the endpoint is **scriptable**. A test, a shell script, or a coding agent
can ask a running app what its visual tree is, with no debugger attached. That is what
`tests/CrossPlatform/Wpf.DevTools.Tests` does.

## Environment variables

| variable | effect |
|---|---|
| `WPF_DEVTOOLS` | `1` turns it on at port 9222; a number names a port; `0`/`off`/`no` disables |
| `WPF_DEVTOOLS_DUMP` | write a plain-text tree to this file shortly after startup |
| `WPF_DEVTOOLS_DUMP_PROPS` | detail for the dump: `local`, `set`, or `all` |
| `WPF_DEVTOOLS_DUMP_DELAY` | milliseconds to wait before dumping (default 1500) |
| `WPF_DEVTOOLS_BROWSER_ID` | override the `Browser` string in `/json/version` |

`WPF_DEVTOOLS_DUMP` needs no browser and no socket:

```
[3] Window  (0,0 1024x768)
  ~ composition: handle 0x5, 1 child visual(s), 0 drawing primitive(s), opacity 1
  [4] Border  (0,0 1014x730)
    - Panel.Background = #80FFFFFF  [ParentTemplate]
    - Grid.Row = 1  [Local]
```

## Composition correlation

This port has **two** trees. WPF builds a managed `Visual` tree; `MediaContext` turns it into
a MILCMD stream; `MilcoreEngine` decodes that back into a `SceneVisual` graph, and that is
what gets drawn. When an element is laid out correctly and still not on screen, the question
that splits the search space is which of the two is wrong.

Both address a visual by the same DUCE resource handle, so the Computed pane carries a
`Composition.Scene` entry saying exactly what the compositor holds:

```
Composition.Scene: handle 0x1A, 0 child visual(s), 55 drawing primitive(s), opacity 1
Composition.Scene: not published to the compositor (no DUCE handle on this channel)
```

The renderer is reached by reflection, deliberately — PresentationCore loads it reflectively
too, so an app running with `WPF_USE_WEBGPU_COMPOSITION=0` still gets an inspector, just
without this section.

### The composition target

The endpoint advertises **two** targets, because there are two trees and they answer different
questions:

```
wpf          WPF Gallery          ws://127.0.0.1:9222/devtools/page/wpf
composition  MILCMD scene graph   ws://127.0.0.1:9222/devtools/page/composition
```

Attach to `composition` and the decoded graph is an ordinary Elements panel of its own —
`SceneVisual`s with their transforms and clips, and **drawing primitives as child nodes**
(`GeometryFill`, `GeometryDrawing`, `GlyphRunDraw`), so "what does the compositor actually draw
for this element" is something you expand into rather than a count:

```
Composition targets=1 visuals=577 renderData=218 solidBrushes=24 geometries=70 pens=3
  SceneVisual id=0x5  element=MainWindow  Offset=<0 0> Opacity=1
    SceneVisual id=0x6  element=Border
      GeometryFill  BaselineAnchor=<0 12> IsGlyph=True
```

Select the `Composition` root and read Computed for the **MILCMD op histogram**: every command,
record and resource kind the decoder has seen, with counts. That is what distinguishes "WPF
never sent the command" from "the decoder ignored it", which no per-node view can answer.

The two trees are joined by the DUCE handle, both ways — an element reports its handle, and a
scene node reports the `element` it was decoded from and borrows that element's bounds so it can
be highlighted and picked. `SceneVisual.Id` is NOT that handle: it is the hit-test id, zero
almost everywhere, and using it left every node anonymous. The handles come from the engine's
own table (`MilcoreEngine.Visuals`).

Scene-node properties are reflected generically rather than per type — a `GlyphRunDraw` and a
`GeometryStroke` share only a base class, and the renderer grows new primitive kinds — so new
kinds appear without this being taught about them.

## Editing

Edit an **attribute** on a node in the Elements panel and the live dependency property
changes; values are converted with the property type's own `TypeConverter`, so you write
what you would have written in XAML (`Red`, `10,20,30,40`, `Collapsed`). Removing an
attribute calls `ClearValue`, restoring whatever was underneath.

The **Styles** pane is read-only. Its editor needs source ranges in a document it can
rewrite, and there is no such document behind a dependency property.

## Screencast frames

Frames come from the renderer's own composed render — the WPF scene WITH any hosted scenes
merged in — not from `RenderTargetBitmap`. RTB re-renders the WPF *visual tree*, and a hosted
control tree is a separate `SceneVisual` graph merged in at render time, so a `WindowsFormsHost`
card simply does not appear in an RTB capture, with nothing to say why.

That composed frame covers the CLIENT area, which is not the root visual's box: the root's
`RenderSize` includes WindowChrome's non-client band (measured 3840x1049 against a 3850x1087
root). Same origin, smaller extent — so the page size reported in the metadata is the FRAME's
extent, not the root's. Reporting the root's would put the frontend's click mapping out by the
difference, silently.

Frames are flattened onto opaque white, because that non-client band is transparent and the
frontend composites onto black, so it otherwise arrives as a border painted round the picture.
Flattening rather than cropping keeps the image's extent equal to the page's.

## Frontend behaviour worth knowing

Two things about the protocol are not in the docs and cost real time to find. Both are
enforced by the tests.

**`DOM.setChildNodes` must be on the wire BEFORE the `DOM.requestChildNodes` response.**
The frontend does not wait for the event — it invokes the command and reads the children
inside the *response* callback:

```js
invoke_requestChildNodes({nodeId}).then(() => callback(this.children()))
```

Send the event afterwards, which is the obvious reading of "reply, then push", and that
callback sees an empty child list. The node expands to nothing, the protocol log shows both
messages sent, and there is no error anywhere.

**`Page.screencastFrame`'s `deviceWidth`/`deviceHeight` are the PAGE size, not the image
size.** The frame is scaled down to fit the panel, but the frontend maps a click on the
screencast back through those two numbers. Report the scaled image size and every click
lands high and to the left by exactly the scale factor — while the picture still looks
perfectly correct.

A third, less subtle one: do **not** push children the frontend is about to ask for. The
second delivery makes it rebuild that subtree under fresh node objects while the tree it is
drawing still holds the first set, so every later expansion lands on nodes nothing is
showing. Send them in the `getDocument` response instead.

## Input

Input goes in where a real event goes in. `Input.dispatchMouseEvent` and
`dispatchKeyEvent` are delivered through the platform head's own entry point — on macOS
`CocoaWindow.InjectMouse`, which is what AppKit's reporting calls — so they travel the same
path as a physical mouse. Capture, hover states, `Mouse.DirectlyOver`, drag, text selection
and scrollbar thumbs all behave normally, because none of it is being approximated.

This is the only arrangement that works. `MouseEventArgs.GetPosition` and
`UIElement.CaptureMouse` read the `MouseDevice`, never the event, so routed events raised by
hand leave a drag handler written the ordinary way (capture on down, `GetPosition` on move)
looking at the real cursor wherever it happens to be. An earlier version of this simulated
its way around that — automation peers to make a `Button` click, `Track.ValueFromDistance`
to reconstruct a thumb drag, `GetCharacterIndexFromPoint` for text selection — and every one
of those went away when the events started arriving properly.

Two things the real path needs and simulation did not. A move has to PRECEDE a click or a
wheel: a real mouse is somewhere before it acts, `Mouse.DirectlyOver` is established by
movement, and a wheel at a position the device was never told about scrolls nothing. And a
move with a button held is a distinct event type (`NSLeftMouseDragged`, not `NSMouseMoved`) —
reporting it as a plain move loses the drag on anything that tells them apart.

The seam is per head, because input entry is — each backend reports in its own vocabulary and
has a static event the framework's input providers already subscribe to. `PlatformInput` holds
one translation each and the domains speak a neutral vocabulary, so adding a head is a case in
two switches:

| head | entry point | vocabulary |
|---|---|---|
| macOS | `CocoaWindow.InjectMouse` / `InjectKey` | raw NSEventTypes |
| browser | `BrowserWindow.InjectMouse` / `InjectKey` | DOM kinds, DOM button indices |
| Linux | `WaylandInput.InjectMouse` / `InjectKey` | `WaylandMouseKind`, evdev codes |
| iOS | `UIKitWindow.InjectMouse` | touch kinds |
| Android | `AndroidWindow.InjectMouse` | touch kinds |

Two gaps, both stated rather than papered over. **iOS and Android have no key path at all** —
`HwndKeyboardInputProvider` does not subscribe to one there, because text arrives through the
soft keyboard's own route — so a dispatched key is reported as unsupported. And on Linux a key
is injected with keysym 0 and its characters, which reaches a text box but not a key that is
only a keysym (Tab, the arrows); that needs the XKB mapping reconstructed.

**macOS and Android are verified at runtime.** On Android the tree, both targets, wheel
scrolling and a scrollbar-thumb drag all work over `adb forward tcp:9222 tcp:9222` — and a thumb
drag is the interesting one, because it needs mouse capture and a device position, which is
precisely what routed events could not provide. Hover states stay false there, correctly: a
touch head has no hover.

**Browser, Linux and iOS are not verified.** They are the same shape and compile against the
same providers, and each handler matches on the `HwndSource.Handle` passed to it — but neither
of those is proof, as Android showed: it needed two unrelated fixes before it ran at all.

### Running the inspector on Android

Two things differ from a desktop head, and both fail in ways that do not name themselves:

- **An environment variable cannot be handed to the app at launch.** `adb shell setprop
  debug.mono.env` is ignored for a non-debuggable build, which is every Release build, so the
  variable has to be baked in with an `AndroidEnvironment` file. `samples/wpf-gallery-android`
  does this behind `-p:WpfDevToolsEnv=true` rather than always.
- **Binding a socket needs the `INTERNET` permission**, even for a listener on loopback that
  nothing off the device can reach. Without it the inspector starts and then reports
  `could not listen on 127.0.0.1:9222: SocketException: Permission denied`. The same sample adds
  it through an `AndroidManifestOverlay` under the same condition.

```sh
dotnet build samples/wpf-gallery-android/WpfGalleryAndroid.csproj -c Release -p:WpfDevToolsEnv=true
adb install -r .../net.dot.wpf.gallery-Signed.apk
adb shell am start -n net.dot.wpf.gallery/crc64c4055fd6c1ce9b55.MainActivity
adb forward tcp:9222 tcp:9222
```

An Android app also has to REFERENCE the inspector to get it, since assemblies are packaged into
the APK — dropping it beside the app, which is enough on a desktop head, has nowhere to go.

While the element picker is armed, mouse events go to the picker instead — that is how
"Select element" works when driven over the screencast rather than over the real window.

## What does not work, and why

- **Live tree deltas are opt-in.** Subscribing to `VisualDiagnostics.VisualTreeChanged` arms
  `VerifyVisualTreeChange` inside PresentationCore, which makes re-entrant tree mutation throw
  in an app that was working fine until someone opened the inspector. The default is a polled
  structural hash, which also avoids re-fetching the tree on every frame of an animation.
- **A WinForms-only deployment cannot load the inspector.** See "WinForms" below.
- **The browser head is a message port, not a server** (see below).
- **The DevTools frontend is not a stable API.** A Chrome update can change what it probes on
  attach. Unimplemented methods answer with an empty result rather than an error precisely
  because a frontend treats an unexpected error as a reason to give up on the panel. The
  regression suite drives the endpoint directly and does not involve the frontend at all.

## WinForms

WinForms control trees are inspected too, in the same panel and over the same protocol.
A `Form` never creates an `HwndSource`, so the trigger is separate — `Application.RunLoop`,
as the message loop starts — and the tree comes from `Application.OpenForms` rather than
`PresentationSource.CurrentSources`. A mixed app shows both trees side by side under the
synthetic `Application` node, each rooted where it actually is.

WinForms has no equivalent of WPF's value precedence, so the Styles pane maps onto the three
origins it does have:

| origin | means |
|---|---|
| `Local` | `PropertyDescriptor.ShouldSerializeValue` — set on this control |
| `Inherited` | an ambient property (`Font`, `BackColor`, `ForeColor`, `Cursor`, `RightToLeft`) falling back to the parent |
| `Default` | the property's own default, and every read-only property |

`[Browsable(false)]` properties are filtered out — that attribute is WinForms' own statement
that a property is not for a human to look at — along with structural members (`Parent`,
`Controls`, `Bounds`) that the tree and box model already show.

Bounds are summed from `Left`/`Top` up to the owning `Form`, so they are client-area
coordinates: the space `Control.Location` is expressed in, and the one the designer uses.

WinForms is reached entirely by reflection. This assembly is one AnyCPU build shared by every
head and most never deploy a `System.Windows.Forms`; a compile-time reference would make the
inspector fail to load on a plain WPF app.

Hosted controls are hit-tested and highlighted like anything else. Their bounds are summed
from `Left`/`Top` up to the owning `Form` and then anchored on the hosting element's box — the
host places the form there, so the element's origin IS the form's client origin. `PointToScreen`
cannot supply that offset: a hosted form has no OS window and answers with driver-space numbers.
When no host can be located in a process that has WPF, a control reports NO bounds rather than
form-relative ones, because a confidently-wrong highlight is worse than none.

Input reaches hosted controls through the host's own forwarding, so nothing special is needed
for a click to land on a WinForms button.

**Editing is WPF-only.** A WinForms property is an ordinary CLR member with no `ClearValue` and
no dependency-property contract.

**A WinForms app that deploys no WPF cannot load the inspector at all.** `Microsoft.Wpf.DevTools`
references `PresentationFramework`, and an SDK-built `UseWindowsForms`-only app gets no WPF
assemblies (see `Sdk.targets` — it takes `Accessibility.dll` and nothing else). So WinForms
inspection works today in an app that also has WPF present: anything using `ElementHost`, or
`UseWpf` together with `UseWindowsForms`. Making it work for a WPF-free WinForms app means
splitting this assembly into a WPF-free core (transport, session, JSON, node identity) plus
per-framework adapters, which is worth doing but is not done.

## The browser head

WASM has no `TcpListener`, and a page cannot accept a connection. So there the transport is a
JS message port rather than a server:

```js
__wpfDevTools.onmessage = m => console.log(JSON.parse(m));
__wpfDevTools.send({ id: 1, method: 'DOM.getDocument', params: { depth: -1 } });
```

Forwarding both directions to a WebSocket gets you a real frontend; that relay is not shipped,
because it is deployment and differs per setup. See `devtools-bridge.js`. **This half is
untested** — the browser head cannot be exercised from a terminal.

## Layout

| path | what |
|---|---|
| `src/WpfDevTools/` | the inspector — `Microsoft.Wpf.DevTools` |
| `src/WpfDevTools/Domains/` | DOM, CSS, Overlay, Page, Runtime, Input |
| `PresentationCore/System/Windows/Diagnostics/DevToolsBootstrap.cs` | loads it when `WPF_DEVTOOLS` is set |
| `PresentationCore/System/Windows/Diagnostics/CompositionDiagnostics.cs` | the DUCE handle seam |
| `src/WpfDevTools/WinFormsTree.cs` | the WinForms control tree, reflectively |
| `WinFormsInterop/swf/System.Windows.Forms/DevToolsBootstrap.cs` | starts it from the WinForms message loop |
| `Shared/MS/Internal/Interop/devtools-bridge.js` | the browser transport's JS half |
| `tests/CrossPlatform/Wpf.DevTools.Tests` | the protocol contract |

The inspector references `PresentationFramework`, so PresentationCore reaches it by
`Assembly.Load` — the same arrangement `DUCE.ManagedComposition` uses for the renderer, and
for the same reason: an app that ships no inspector must still run. It probes the application
directory too, so dropping the assembly next to an app that never referenced it works (an
assembly missing from `App.deps.json` is invisible to `Assembly.Load` alone).

It is otherwise built entirely against public API, with one exception:
`CompositionDiagnostics.GetCompositionHandle`, which is why PresentationCore grants it friend
access and why it is public-signed with the ECMA key.

## Turning it off in a shipped app

The SDK references it by default. To leave it out of a drop entirely:

```xml
<WpfDevTools>false</WpfDevTools>
```

It starts nothing without `WPF_DEVTOOLS`, but the endpoint gives full read *and write* access
to a running app's UI and there is no authentication — which is why it is loopback-only and
opt-in.
