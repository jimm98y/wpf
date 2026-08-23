// The Windows binding for the host's accessibility tree: a server-side UI Automation provider.
//
// A window that draws its own content answers WM_GETOBJECT with a provider and describes its tree
// itself, which is exactly the position this host is in -- see Accessibility.cs for why. Everything
// here is translation: A11y answers what an element is, this file says it in UI Automation's words.
//
// Windows-only by nature. CocoaAccessibility.cs says the same things to NSAccessibility.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinFormsWebGpu.Accessibility
{
    internal enum NavigateDirection { Parent = 0, NextSibling = 1, PreviousSibling = 2, FirstChild = 3, LastChild = 4 }

    [Flags]
    internal enum ProviderOptions
    {
        ClientSideProvider = 1,
        ServerSideProvider = 2,
        NonClientAreaProvider = 4,
        OverrideProvider = 8,
        ProviderOwnsSetFocus = 16,
        UseComThreading = 32,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaRect { public double left, top, width, height; }

    [ComImport, Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IRawElementProviderSimple
    {
        ProviderOptions ProviderOptions { get; }
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetPatternProvider(int patternId);
        [return: MarshalAs(UnmanagedType.Struct)] object GetPropertyValue(int propertyId);
        IRawElementProviderSimple HostRawElementProvider { get; }
    }

    [ComImport, Guid("f7063da8-8359-439c-9297-bbc5299a7d87"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IRawElementProviderFragment : IRawElementProviderSimple
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object Navigate(NavigateDirection direction);
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        UiaRect BoundingRectangle { get; }
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)] object[] GetEmbeddedFragmentRoots();
        void SetFocus();
        IRawElementProviderFragmentRoot FragmentRoot { get; }
    }

    [ComImport, Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IRawElementProviderFragmentRoot : IRawElementProviderFragment
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object ElementProviderFromPoint(double x, double y);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetFocus();
    }

    [ComImport, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInvokeProvider { void Invoke(); }

    [ComImport, Guid("c7935180-6fb3-4201-b174-7df73adbf64a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IValueProvider
    {
        void SetValue([MarshalAs(UnmanagedType.LPWStr)] string value);
        string Value { [return: MarshalAs(UnmanagedType.BStr)] get; }
        bool IsReadOnly { get; }
    }

    [ComImport, Guid("d847d3a5-cab0-4a98-8c32-ecb45c59ad24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExpandCollapseProvider
    {
        void Expand();
        void Collapse();
        int ExpandCollapseState { get; }
    }

    [ComImport, Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IToggleProvider
    {
        void Toggle();
        int ToggleState { get; }
    }

    internal static class Uia
    {
        // Property, pattern and control-type ids, from UIAutomationClient.h.
        internal const int BoundingRectangleProperty = 30001;
        internal const int ControlTypeProperty = 30003;
        internal const int NameProperty = 30005;
        internal const int HasKeyboardFocusProperty = 30008;
        internal const int IsKeyboardFocusableProperty = 30009;
        internal const int IsEnabledProperty = 30010;
        internal const int AutomationIdProperty = 30011;
        internal const int ClassNameProperty = 30012;
        internal const int HelpTextProperty = 30013;
        internal const int IsControlElementProperty = 30016;
        internal const int IsContentElementProperty = 30017;
        internal const int NativeWindowHandleProperty = 30020;
        internal const int IsOffscreenProperty = 30022;

        internal const int InvokePattern = 10000;
        internal const int ValuePattern = 10002;
        internal const int TogglePattern = 10015;
        internal const int ExpandCollapsePattern = 10005;

        // UiaAppendRuntimeId: prefixes the host window's own id, so ids only have to be unique
        // within this tree.
        internal const int AppendRuntimeId = 3;
        internal const int RootObjectId = -25;
        internal const uint WM_GETOBJECT = 0x003D;

        internal static int ControlType(A11yRole role)
        {
            switch (role)
            {
                case A11yRole.Window: return 50032;
                case A11yRole.Button: return 50000;
                case A11yRole.Calendar: return 50001;
                case A11yRole.CheckBox: return 50002;
                case A11yRole.ComboBox: return 50003;
                case A11yRole.Edit: return 50004;
                case A11yRole.Link: return 50005;
                case A11yRole.Image: return 50006;
                case A11yRole.ListItem: return 50007;
                case A11yRole.List: return 50008;
                case A11yRole.MenuBar: return 50010;
                case A11yRole.MenuItem: return 50011;
                case A11yRole.ProgressBar: return 50012;
                case A11yRole.RadioButton: return 50013;
                case A11yRole.ScrollBar: return 50014;
                case A11yRole.Slider: return 50015;
                case A11yRole.Spinner: return 50016;
                case A11yRole.StatusBar: return 50017;
                case A11yRole.Tab: return 50018;
                case A11yRole.TabItem: return 50019;
                case A11yRole.Text: return 50020;
                case A11yRole.ToolBar: return 50021;
                case A11yRole.Tree: return 50023;
                case A11yRole.TreeItem: return 50024;
                case A11yRole.Group: return 50026;
                case A11yRole.DataGrid: return 50028;
                case A11yRole.Document: return 50030;
                case A11yRole.Table: return 50036;
                case A11yRole.Separator: return 50038;
                default: return 50033;                      // Pane
            }
        }

        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr UiaReturnRawElementProvider(IntPtr hwnd, IntPtr wParam, IntPtr lParam,
            IRawElementProviderSimple el);

        [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
        internal static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IRawElementProviderSimple provider);

        [DllImport("UIAutomationCore.dll")]
        internal static extern bool UiaClientsAreListening();

        /// <summary>The host's whole side of this: answer WM_GETOBJECT for the automation root.
        /// Returns false when the message is not ours to answer.</summary>
        internal static bool TryAnswerGetObject(IA11yHostSite site, IntPtr wParam, IntPtr lParam, out IntPtr result)
        {
            result = IntPtr.Zero;
            if ((int)lParam != RootObjectId || site.Form == null)
                return false;
            try
            {
                var root = UiaProvider.For(site, site.Form) as IRawElementProviderSimple;
                if (root == null)
                    return false;
                result = UiaReturnRawElementProvider(site.Handle, wParam, lParam, root);
                return true;
            }
            catch (DllNotFoundException)
            {
                // No UI Automation on this machine: let the default handling take the message.
                return false;
            }
        }
    }

    /// <summary>One provider per control, handed out from a cache so a client that asks twice gets
    /// the same runtime id both times -- which is what lets it tell "the same element again" from
    /// "a new element".</summary>
    [ComVisible(true)]
    internal class UiaProvider : IRawElementProviderFragment, IInvokeProvider, IValueProvider, IToggleProvider,
        IExpandCollapseProvider
    {
        private static readonly ConditionalWeakTable<Control, UiaProvider> s_cache = new ConditionalWeakTable<Control, UiaProvider>();
        private static int s_nextId = 1;

        protected readonly IA11yHostSite Site;
        protected readonly Control Control;
        private readonly int _runtimeId;

        protected UiaProvider(IA11yHostSite site, Control control)
        {
            Site = site;
            Control = control;
            lock (s_cache)
                _runtimeId = s_nextId++;
        }

        internal static UiaProvider For(IA11yHostSite site, Control control)
        {
            if (control == null)
                return null;
            lock (s_cache)
            {
                UiaProvider existing;
                if (s_cache.TryGetValue(control, out existing))
                    return existing;
                UiaProvider created = ReferenceEquals(control, site.Form)
                    ? new UiaRootProvider(site, control)
                    : new UiaProvider(site, control);
                s_cache.Add(control, created);
                return created;
            }
        }

        // ---- the tree ----------------------------------------------------------------------------

        public object Navigate(NavigateDirection direction)
        {
            switch (direction)
            {
                case NavigateDirection.Parent:
                    return ReferenceEquals(Control, Site.Form) ? null : (object)For(Site, Control.Parent);

                case NavigateDirection.FirstChild:
                {
                    IList<Control> kids = A11y.Children(Control);
                    return kids.Count > 0 ? For(Site, kids[0]) : null;
                }

                case NavigateDirection.LastChild:
                {
                    IList<Control> kids = A11y.Children(Control);
                    return kids.Count > 0 ? For(Site, kids[kids.Count - 1]) : null;
                }

                case NavigateDirection.NextSibling:
                case NavigateDirection.PreviousSibling:
                {
                    if (ReferenceEquals(Control, Site.Form))
                        return null;
                    IList<Control> siblings = A11y.Children(Control.Parent);
                    int i = siblings.IndexOf(Control);
                    if (i < 0)
                        return null;
                    int j = direction == NavigateDirection.NextSibling ? i + 1 : i - 1;
                    return j >= 0 && j < siblings.Count ? For(Site, siblings[j]) : null;
                }
            }
            return null;
        }

        public int[] GetRuntimeId()
        {
            return new int[] { Uia.AppendRuntimeId, _runtimeId };
        }

        public UiaRect BoundingRectangle
        {
            get
            {
                var r = new UiaRect();
                if (!A11y.IsVisible(Control))
                    return r;
                Site.TryMapToScreen(A11y.DriverBounds(Control), out r.left, out r.top, out r.width, out r.height);
                return r;
            }
        }

        public object[] GetEmbeddedFragmentRoots() { return null; }

        public void SetFocus() { A11y.Focus(Control); }

        public IRawElementProviderFragmentRoot FragmentRoot
        {
            get { return For(Site, Site.Form) as IRawElementProviderFragmentRoot; }
        }

        // ---- properties ---------------------------------------------------------------------------

        public ProviderOptions ProviderOptions
        {
            // UseComThreading: WinForms is not thread safe and a client calls in on its own thread.
            // Without it every one of these calls would read the control tree off the UI thread.
            get { return ProviderOptions.ServerSideProvider | ProviderOptions.UseComThreading; }
        }

        public virtual IRawElementProviderSimple HostRawElementProvider
        {
            // Only the root is hosted by the window. Saying so on a child would graft the window's
            // own provider in underneath it.
            get { return null; }
        }

        public virtual object GetPropertyValue(int propertyId)
        {
            switch (propertyId)
            {
                case Uia.NameProperty: return A11y.NameOf(Control);
                case Uia.ControlTypeProperty: return Uia.ControlType(A11y.RoleOf(Control));
                case Uia.AutomationIdProperty: return A11y.AutomationIdOf(Control);
                case Uia.ClassNameProperty: return Control.GetType().Name;
                case Uia.HelpTextProperty: return A11y.DescriptionOf(Control);
                case Uia.IsEnabledProperty: return A11y.IsEnabled(Control);
                case Uia.HasKeyboardFocusProperty: return A11y.IsFocused(Control);
                case Uia.IsKeyboardFocusableProperty: return A11y.IsFocusable(Control);
                case Uia.IsOffscreenProperty: return !A11y.IsVisible(Control);
                case Uia.IsControlElementProperty: return true;
                case Uia.IsContentElementProperty: return true;
            }
            return null;
        }

        public object GetPatternProvider(int patternId)
        {
            switch (patternId)
            {
                case Uia.InvokePattern: return A11y.CanInvoke(Control) ? this : null;
                case Uia.ValuePattern: return A11y.HasValue(Control) ? this : null;
                case Uia.TogglePattern: return A11y.CanToggle(Control) ? this : null;
                case Uia.ExpandCollapsePattern: return A11y.CanExpand(Control) ? this : null;
            }
            return null;
        }

        // ---- patterns -----------------------------------------------------------------------------

        public void Invoke() { A11y.Invoke(Control); }

        public void SetValue(string value) { A11y.SetValue(Control, value); }

        public string Value { get { return A11y.ValueOf(Control); } }

        public bool IsReadOnly { get { return A11y.IsReadOnly(Control); } }

        public void Toggle() { A11y.Toggle(Control); }

        public void Expand() { A11y.SetExpanded(Control, true); }

        public void Collapse() { A11y.SetExpanded(Control, false); }

        public int ExpandCollapseState { get { return A11y.IsExpanded(Control) ? 1 : 0; } }

        public int ToggleState
        {
            get
            {
                switch (A11y.ToggleStateOf(Control))
                {
                    case A11yToggle.Off: return 0;
                    case A11yToggle.On: return 1;
                    default: return 2;
                }
            }
        }
    }

    /// <summary>The form: everything a control provider does, plus the two jobs only a fragment root
    /// has -- hit testing and reporting the focus.</summary>
    [ComVisible(true)]
    internal sealed class UiaRootProvider : UiaProvider, IRawElementProviderFragmentRoot
    {
        internal UiaRootProvider(IA11yHostSite site, Control form) : base(site, form) { }

        public object ElementProviderFromPoint(double x, double y)
        {
            Point driver;
            if (!Site.TryMapFromScreen(x, y, out driver))
                return this;
            return For(Site, A11y.HitTest(Control, driver)) ?? (object)this;
        }

        public object GetFocus()
        {
            Control focused = A11y.FindFocused(Control);
            return focused != null ? For(Site, focused) : null;
        }

        public override IRawElementProviderSimple HostRawElementProvider
        {
            get
            {
                // The window's own provider supplies what only it knows: the process, the window
                // handle, the title bar. Grafting it in here is what makes this tree a window's
                // tree rather than a free-floating one.
                IRawElementProviderSimple host;
                try
                {
                    return Uia.UiaHostProviderFromHwnd(Site.Handle, out host) == 0 ? host : null;
                }
                catch (DllNotFoundException)
                {
                    return null;
                }
            }
        }

        public override object GetPropertyValue(int propertyId)
        {
            if (propertyId == Uia.NativeWindowHandleProperty)
                return (int)Site.Handle;
            return base.GetPropertyValue(propertyId);
        }
    }
}
