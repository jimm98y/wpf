# File dialogs, and why three heads need an asynchronous API

`OpenFileDialog.ShowDialog()` is synchronous. On three of this port's six heads it cannot be, and no
amount of design inside WPF changes that. This is what was done about it.

## The constraint

`Dispatcher.PushFrameImpl` refuses a nested frame on browser, iOS and Android:

| head | who owns the loop | why it cannot be re-entered |
|---|---|---|
| browser | the JS event loop | single-threaded; there is no way to run a nested loop and return |
| iOS | UIKit | `Dispatcher.Run` is reached from inside `scene:willConnectToSession:`; blocking never returns to UIKit and the scene stays half-connected |
| Android | the main `Looper` | `Dispatcher.Run` is reached from inside `Activity.onCreate`; blocking never returns and the activity stays half-created |

Every native picker on those platforms is asynchronous for exactly the same reason:
`<input type=file>` raises a `change` event, `UIDocumentPickerViewController` is *presented* and
answers on a delegate, and Android's Storage Access Framework is an activity whose result arrives in
`onActivityResult`. None of them has a `runModal`.

So a synchronous call cannot wait for the answer, and a synchronous call that returns anyway has to
invent one.

## What it used to do

It invented `false`. `CommonItemDialog.RunDialogPortable` had arms for macOS and Linux and fell
through to `return false` for everything else, which `ShowDialog` reports as *the user cancelled*.

That is the worst available answer, and the reason is worth stating: a cancellation is exactly what
it looks like. Nothing is logged, nothing throws, no dialog flickers. An application calls
`ShowDialog`, gets `false`, and does nothing — which is also correct behaviour when the user really
did press Cancel. There is nothing for a developer to notice.

## What it does now

**`ShowDialog()` refuses on those three heads**, with a message naming the alternative:

```
A file dialog cannot be shown synchronously on this platform: its run loop cannot be
re-entered, so ShowDialog() has no way to wait for the user's answer. Use
ShowDialogAsync() instead, which works on every platform.
```

**`CommonDialog.ShowDialogAsync()` is the API that works everywhere.** On Windows, macOS and Linux it
runs the ordinary modal dialog and hands back an already-completed task, so one piece of application
code is correct on all six heads — which is the point of putting it on `CommonDialog` rather than
inventing a browser-only entry point.

```csharp
var dialog = new OpenFileDialog { Filter = "Text files|*.txt;*.log" };
if (await dialog.ShowDialogAsync() == true)
{
    string text = File.ReadAllText(dialog.FileName);   // works on every head
}
```

## Saving is the other way round

None of the three can hand an application a writable destination *before* the write: the browser has
no file system to write into, and iOS and Android confine an app to its own container. So the order
reverses, and `SaveFileDialog.CommitAsync()` is the step that reaches the user.

```csharp
var dialog = new SaveFileDialog { FileName = "report.csv" };
if (await dialog.ShowDialogAsync() == true)
{
    File.WriteAllText(dialog.FileName, csv);   // a private path on browser/iOS/Android
    await dialog.CommitAsync();                // download / export sheet / SAF create-document
}
```

`CommitAsync` returns true and does nothing on the desktop heads, where the write already went where
the user asked for it. Calling it unconditionally is correct, and is what keeps the snippet above
from needing a per-platform branch.

## What `FileName` means on each head

Every backend hands back a path that `File.ReadAllBytes` can open, because that is what WPF callers
assume `FileName` is for. What differs is where that path points.

| head | opening | saving |
|---|---|---|
| Windows / macOS / Linux | the real file the user chose | the real destination the user chose |
| browser | a copy in the wasm virtual file system under `/wpf-picked`; the page is never told the real path | a reserved path in the same place, offered as a **download** by `CommitAsync` |
| iOS | a copy in the app's temporary directory — a picked document is security-scoped and its URL is useless without `startAccessingSecurityScopedResource` | a reserved path in the temporary directory, offered through the **export sheet** |
| Android | a copy in the app's cache directory — a SAF result is a `content://` URI, which nothing in .NET can open | a reserved path in the cache directory, copied to the user's choice by **create-document** |

## What an Android head must add

The Storage Access Framework answers on the Activity, and only the Activity receives it. Each
`net10.0-android` head therefore forwards one callback; without it every pick awaits for ever rather
than failing.

```csharp
AndroidDialogs.Host = host;      // alongside AndroidWindow.Host

protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
{
    if (_host?.HandleActivityResult(requestCode, resultCode, data) == true) return;
    base.OnActivityResult(requestCode, resultCode, data);
}

protected override void OnDestroy()
{
    _host?.CancelPendingDialogs();   // completes anything still outstanding
    base.OnDestroy();
}
```

`samples/wpf-gallery-android/MainActivity.cs` is the worked example.

## Filters

WPF's filter string has named groups (`"Text files|*.txt;*.log|All files|*.*"`). No other platform's
picker does — a browser, iOS and Android picker each show one list — so the translation offers every
extension from every group, and a `*.*` anywhere clears the restriction entirely, because an accept
list containing `.*` matches nothing. `BrowserDialogs.FilterToAccept` does this and is covered by
`Wpf.Platform.Tests/DialogBackendTests`.

## Where the code is

| file | what |
|---|---|
| `Microsoft/Win32/CommonDialog.cs` | `ShowDialogAsync`, defaulting to the synchronous path |
| `Microsoft/Win32/CommonItemDialog.cs` | the refusal, and the per-head async dispatch |
| `Microsoft/Win32/SaveFileDialog.cs` | `CommitAsync` |
| `Shared/MS/Internal/Interop/BrowserDialogs.cs` + the file-dialog section of `browser-window.js` | the browser backend |
| `Shared/MS/Internal/Interop/UIKitDialogs.cs` | `UIDocumentPickerViewController` and its delegate |
| `Shared/MS/Internal/Interop/AndroidDialogs.cs` + `AndroidHost.cs` | the SAF seam and its payload half |
| `tests/CrossPlatform/Wpf.Dialog.Tests` | the contract at the PresentationFramework layer |
| `tests/CrossPlatform/Wpf.Platform.Tests/DialogBackendTests.cs` | the backends, at the WindowsBase layer |
