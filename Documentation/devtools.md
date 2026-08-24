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
| screencast | `RenderTargetBitmap` over the root visual |

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

## Editing

Edit an **attribute** on a node in the Elements panel and the live dependency property
changes; values are converted with the property type's own `TypeConverter`, so you write
what you would have written in XAML (`Red`, `10,20,30,40`, `Collapsed`). Removing an
attribute calls `ClearValue`, restoring whatever was underneath.

The **Styles** pane is read-only. Its editor needs source ranges in a document it can
rewrite, and there is no such document behind a dependency property.

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

## What does not work, and why

- **Input is not OS-level input.** WPF's real input path starts at an `InputReport` raised by
  the platform head, and both the report types and the heads' injection points are internal to
  PresentationCore — which the inspector deliberately cannot reference. `Input.dispatchMouseEvent`
  therefore raises the routed events *and* invokes the element's automation peer, which is what
  makes a `Button` actually click. You do not get mouse capture, hover visual states,
  `Mouse.DirectlyOver`, or drag. While the element picker is armed, mouse events go to the
  picker instead — that is how "Select element" works when driven over the screencast rather
  than over the real window.
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

**Editing and highlighting are WPF-only.** A WinForms property is an ordinary CLR member with
no `ClearValue` and no dependency-property contract, and the highlight is an `Adorner`.

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
