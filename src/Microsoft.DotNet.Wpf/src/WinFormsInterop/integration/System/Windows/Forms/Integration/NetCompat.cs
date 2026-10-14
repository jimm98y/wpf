// The rest of WindowsFormsIntegration's public surface, as .NET ships it, for libraries compiled
// against the real assembly: HostContainer (the panel the hosted WPF tree sits in), property mapping,
// the integration error events, and WindowsFormsHost's inherited WPF properties. A missing member is
// a MissingMethodException in whatever built against it, even where the feature itself is a no-op.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;
using WPF = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfDocuments = System.Windows.Documents;

namespace System.Windows.Forms.Integration
{
    public partial class ElementHost
    {
        private AvalonAdapter _container;
        private PropertyMap _propertyMap;

        private WpfDocuments.AdornerDecorator _decorator;

        /// <summary>The panel the child sits in; made with the AdornerDecorator .NET puts above it, so
        /// HostContainer has the parent apps look for (app names it "gwxMainViewHostDecorator")
        /// and adorners on the hosted tree have a layer -- without it AdornerLayer.GetAdornerLayer
        /// answered null and app's licence notice was never shown.</summary>
        internal AvalonAdapter Container
        {
            get
            {
                if (_container == null)
                {
                    _container = new AvalonAdapter(this);
                    _decorator = new WpfDocuments.AdornerDecorator { Child = _container };
                }
                return _container;
            }
        }

        /// <summary>The hosted window's root visual: the decorator over the container.</summary>
        internal WPF.UIElement RootElement { get { _ = Container; return _decorator; } }

        /// <summary>The panel the hosted WPF element is placed in: the root of the hosted tree.</summary>
        [System.ComponentModel.Browsable(false)]
        public WpfControls.Panel HostContainer => Container;

        /// <summary>Whether the WPF tree is composited over the control's parent rather than its BackColor.</summary>
        [System.ComponentModel.DefaultValue(false)]
        public bool BackColorTransparent { get; set; }

        /// <summary>Maps WinForms property changes onto the hosted element (accepted; see PropertyMap).</summary>
        [System.ComponentModel.Browsable(false)]
        public PropertyMap PropertyMap => _propertyMap ??= new PropertyMap(this);

        /// <summary>
        /// Lets a modeless WPF window receive keyboard input from a WinForms message loop. Input is the
        /// stack's own on every head here, so there is nothing to route.
        /// </summary>
        public static void EnableModelessKeyboardInterop(WPF.Window window)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
        }

        protected virtual void OnPropertyChanged(string propertyName, object value) => PropertyMap.OnPropertyChanged(this, propertyName, value);
    }

    /// <summary>The DockPanel an ElementHost's child is placed in (the type .NET uses has this name too).</summary>
    internal sealed class AvalonAdapter : WpfControls.DockPanel
    {
        public AvalonAdapter(ElementHost host) => Host = host;
        public ElementHost Host { get; }
    }

    public partial class WindowsFormsHost
    {
        private PropertyMap _propertyMap;

        public static readonly WPF.DependencyProperty BackgroundProperty = WpfControls.Control.BackgroundProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty ForegroundProperty = WpfControls.Control.ForegroundProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty FontFamilyProperty = WpfControls.Control.FontFamilyProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty FontSizeProperty = WpfControls.Control.FontSizeProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty FontStyleProperty = WpfControls.Control.FontStyleProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty FontWeightProperty = WpfControls.Control.FontWeightProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty PaddingProperty = WpfControls.Control.PaddingProperty.AddOwner(typeof(WindowsFormsHost));
        public static readonly WPF.DependencyProperty TabIndexProperty = WpfControls.Control.TabIndexProperty.AddOwner(typeof(WindowsFormsHost));

        public WPF.Media.Brush Background { get => (WPF.Media.Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
        public WPF.Media.Brush Foreground { get => (WPF.Media.Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public WPF.Media.FontFamily FontFamily { get => (WPF.Media.FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
        public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
        public WPF.FontStyle FontStyle { get => (WPF.FontStyle)GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }
        public WPF.FontWeight FontWeight { get => (WPF.FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
        public WPF.Thickness Padding { get => (WPF.Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
        public int TabIndex { get => (int)GetValue(TabIndexProperty); set => SetValue(TabIndexProperty, value); }

        /// <summary>Maps WPF property changes onto the hosted control (accepted; see PropertyMap).</summary>
        public PropertyMap PropertyMap => _propertyMap ??= new PropertyMap(this);

        /// <summary>Raised when laying out the hosted control fails.</summary>
        public event EventHandler<LayoutExceptionEventArgs> LayoutError;

        internal void RaiseLayoutError(Exception exception)
        {
            var args = new LayoutExceptionEventArgs(this, false, exception);
            LayoutError?.Invoke(this, args);
            if (args.ThrowException) throw new InvalidOperationException("Layout of the hosted control failed.", exception);
        }

        /// <summary>Moves keyboard focus into the hosted control, as Tab into the host does.</summary>
        public bool TabInto(TraversalRequest request)
        {
            if (Child == null || !Child.CanFocus) return false;
            return Child.Focus();
        }

        /// <summary>Scales the hosted control by <paramref name="scale"/>; returns the scale applied.</summary>
        protected virtual WPF.Vector ScaleChild(WPF.Vector newScale) => newScale;

        protected override WPF.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new WPF.Automation.Peers.WindowsFormsHostAutomationPeer(this);
    }

    /// <summary>Translates one property of a host onto its hosted content.</summary>
    public delegate void PropertyTranslator(object host, string propertyName, object value);

    /// <summary>
    /// The property translations of an ElementHost or WindowsFormsHost. The table is kept and its
    /// translators run when a mapped property changes; the default translations .NET installs (font,
    /// colours, visibility...) are the stack's own behaviour here, so the table starts empty.
    /// </summary>
    public class PropertyMap
    {
        private readonly Dictionary<string, PropertyTranslator> _map = new Dictionary<string, PropertyTranslator>();
        private readonly object _host;

        public PropertyMap() { }
        public PropertyMap(object source) => _host = source;

        public PropertyTranslator this[string propertyName]
        {
            get => _map.TryGetValue(propertyName, out var t) ? t : null;
            set => _map[propertyName] = value;
        }

        public ICollection Keys => _map.Keys;
        public ICollection Values => _map.Values;
        protected object SourceObject => _host;
        protected Dictionary<string, PropertyTranslator> DefaultTranslators { get; } = new Dictionary<string, PropertyTranslator>();

        public event EventHandler<PropertyMappingExceptionEventArgs> PropertyMappingError;

        public void Add(string propertyName, PropertyTranslator translator)
        {
            if (_map.ContainsKey(propertyName)) throw new ArgumentException($"The property '{propertyName}' is already mapped.", nameof(propertyName));
            _map[propertyName] = translator;
        }

        public void Remove(string propertyName) => _map.Remove(propertyName);
        public void Clear() => _map.Clear();
        public bool Contains(string propertyName) => _map.ContainsKey(propertyName);
        public void Reset(string propertyName) => _map.Remove(propertyName);
        public void ResetAll() => _map.Clear();
        public void Apply(string propertyName) { }
        public void ApplyAll() { }

        internal void OnPropertyChanged(object host, string propertyName, object value)
        {
            if (!_map.TryGetValue(propertyName, out var translator) || translator == null) return;
            try
            {
                translator(host, propertyName, value);
            }
            catch (Exception e)
            {
                var args = new PropertyMappingExceptionEventArgs(e, propertyName, value);
                PropertyMappingError?.Invoke(this, args);
                if (args.ThrowException) throw;
            }
        }
    }

    /// <summary>An exception in the integration layer, with the choice of rethrowing it.</summary>
    public class IntegrationExceptionEventArgs : EventArgs
    {
        public IntegrationExceptionEventArgs(bool throwException, Exception exception)
        {
            ThrowException = throwException;
            Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        public Exception Exception { get; }
        public bool ThrowException { get; set; }
    }

    public class LayoutExceptionEventArgs : IntegrationExceptionEventArgs
    {
        public LayoutExceptionEventArgs(object element, bool throwException, Exception exception) : base(throwException, exception)
        {
            Element = element ?? throw new ArgumentNullException(nameof(element));
        }

        public object Element { get; }
    }

    public class PropertyMappingExceptionEventArgs : IntegrationExceptionEventArgs
    {
        public PropertyMappingExceptionEventArgs(Exception exception, string propertyName, object propertyValue) : base(false, exception)
        {
            PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
            PropertyValue = propertyValue;
        }

        public string PropertyName { get; }
        public object PropertyValue { get; }
    }
}

namespace System.Windows.Automation.Peers
{
    /// <summary>The automation peer of a WindowsFormsHost.</summary>
    public sealed class WindowsFormsHostAutomationPeer : FrameworkElementAutomationPeer
    {
        public WindowsFormsHostAutomationPeer(System.Windows.Forms.Integration.WindowsFormsHost owner) : base(owner) { }
        protected override string GetClassNameCore() => "WindowsFormsHost";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override bool IsHwndHost => true;
    }
}
