// Windows: what the real user32 hands back about the app's own windows.
//
// A form of this stack is shown in a real host window (WinFormsInterop's Win32Host), but the form's
// Handle is the stack's own. So when an app asks Windows which windows it has -- EnumWindows,
// GetForegroundWindow, and through them Process.MainWindowHandle -- Windows names the host window, and
// an app comparing that with Form.Handle finds no match: app's runtime view decides it is not in
// the main window and never starts its license model. Here the real function runs and every host
// window in its answer becomes the form it shows; the host window carries the form's handle as a
// window property (Win32Host.StackHandleProperty). Other windows pass through as they are.

using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const string StackHandleProperty = "WinFormsWebGpu.Handle";

    /// <summary>The stack's window a real HWND shows, or the HWND itself.</summary>
    private static nint ToStack(nint h)
    {
        if (h == 0) return 0;
        nint stack = RealGetProp(h, StackHandleProperty);
        return stack != 0 && IsStackWindow(stack) ? stack : h;
    }

    private struct EnumContext
    {
        public delegate* unmanaged<nint, nint, int> Callback;
        public nint Data;
    }

    [UnmanagedCallersOnly]
    private static int EnumTranslated(nint h, nint context)
    {
        var c = (EnumContext*)context;
        return c->Callback(ToStack(h), c->Data);
    }

    [Win32ExportOnWindows(Always, "EnumWindows")]
    public static int WinEnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data)
    {
        var c = new EnumContext { Callback = callback, Data = data };
        return RealEnumWindows(&EnumTranslated, (nint)(&c));
    }

    [Win32ExportOnWindows(Always, "EnumThreadWindows")]
    public static int WinEnumThreadWindows(uint thread, delegate* unmanaged<nint, nint, int> callback, nint data)
    {
        var c = new EnumContext { Callback = callback, Data = data };
        return RealEnumThreadWindows(thread, &EnumTranslated, (nint)(&c));
    }

    [Win32ExportOnWindows(Always, "GetForegroundWindow")] public static nint WinGetForegroundWindow() => ToStack(RealGetForegroundWindow());
    [Win32ExportOnWindows(Always, "GetActiveWindow")] public static nint WinGetActiveWindow() => ToStack(RealGetActiveWindow());
    [Win32ExportOnWindows(Always, "WindowFromPoint")] public static nint WinWindowFromPoint(POINT p) => ToStack(RealWindowFromPoint(p));

    /// <summary>The focus inside a form is the stack's: when Windows has it on a form's host window,
    /// the answer is the form's focused control.</summary>
    [Win32ExportOnWindows(Always, "GetFocus")]
    public static nint WinGetFocus()
    {
        nint real = RealGetFocus();
        nint form = ToStack(real);
        if (form == real) return real;
        object? o = Resolve(form);
        if (o is System.Windows.Forms.Form f)
        {
            nint focused = Ui(f, () => f.ActiveControl is { IsHandleCreated: true } c ? c.Handle : 0);
            if (focused != 0) return focused;
        }
        return form;
    }

    [DllImport("user32", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint RealGetProp(nint h, string name);
    [DllImport("user32", EntryPoint = "EnumWindows")] private static extern int RealEnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data);
    [DllImport("user32", EntryPoint = "EnumThreadWindows")] private static extern int RealEnumThreadWindows(uint thread, delegate* unmanaged<nint, nint, int> callback, nint data);
    [DllImport("user32", EntryPoint = "GetForegroundWindow")] private static extern nint RealGetForegroundWindow();
    [DllImport("user32", EntryPoint = "GetActiveWindow")] private static extern nint RealGetActiveWindow();
    [DllImport("user32", EntryPoint = "GetFocus")] private static extern nint RealGetFocus();
    [DllImport("user32", EntryPoint = "WindowFromPoint")] private static extern nint RealWindowFromPoint(POINT p);
}
