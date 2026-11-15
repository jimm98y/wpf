// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The rest of WinForms' HTML object model, on the same script bridge as HtmlElement (HtmlDocument.cs): the document
// and window of a page or one of its frames (an expression for each, as for an element), the window's frames and
// history, and the events of all three.
//
// EVENTS. An element's Click, a document's MouseMove, a window's Load: a listener in the page (one script per
// document, the __wfEv registry below) posts each event back over the web message channel, and HtmlEvents raises it
// with what mshtml's event object carried -- the keys, the buttons, the mouse in the client, the screen and the
// element, the elements the mouse came from and went to. Handlers are kept by the page object they listen on, not by
// the wrapper, since a wrapper is made anew at every access: an element's handlers stay its own however it is reached.
// What cannot be: ReturnValue and BubbleEvent. The engine has handled the event before the message arrives, so
// neither cancels anything (mshtml ran the handler inside the event; nothing outside the page process can).
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using MS.Internal.Interop.WebView;

namespace System.Windows.Forms
{
	public delegate void HtmlElementEventHandler (object sender, HtmlElementEventArgs e);

	public delegate void HtmlElementErrorEventHandler (object sender, HtmlElementErrorEventArgs e);

	/// <summary>What an HTML event carried (mshtml's IHTMLEventObj).</summary>
	public sealed class HtmlElementEventArgs : EventArgs
	{
		private readonly HtmlBridge _bridge;
		private readonly int _from, _to;

		internal HtmlElementEventArgs (HtmlBridge bridge, string eventType, int clientX, int clientY, int offsetX, int offsetY,
			int screenX, int screenY, int buttons, int keyCode, bool alt, bool ctrl, bool shift, int from, int to)
		{
			_bridge = bridge;
			EventType = eventType;
			ClientMousePosition = new Point (clientX, clientY);
			OffsetMousePosition = new Point (offsetX, offsetY);
			MousePosition = new Point (screenX, screenY);
			MouseButtonsPressed = (buttons & 1) != 0 ? MouseButtons.Left : MouseButtons.None;
			if ((buttons & 2) != 0) MouseButtonsPressed |= MouseButtons.Right;
			if ((buttons & 4) != 0) MouseButtonsPressed |= MouseButtons.Middle;
			KeyPressedCode = keyCode;
			AltKeyPressed = alt;
			CtrlKeyPressed = ctrl;
			ShiftKeyPressed = shift;
			_from = from;
			_to = to;
		}

		public bool AltKeyPressed { get; }

		/// <summary>Whether the event goes on to the parents (cancelBubble); set after the page has handled it, so it
		/// changes nothing here.</summary>
		public bool BubbleEvent { get; set; } = true;

		/// <summary>The mouse in the client area.</summary>
		public Point ClientMousePosition { get; }

		public bool CtrlKeyPressed { get; }

		/// <summary>The event's name, without "on" (click, mousemove).</summary>
		public string EventType { get; }

		/// <summary>The element the mouse came from (mouseover, mouseenter).</summary>
		public HtmlElement FromElement => _from < 0 ? null : new HtmlElement (_bridge, HtmlEvents.SlotExpression (_from));

		public int KeyPressedCode { get; }

		public MouseButtons MouseButtonsPressed { get; }

		/// <summary>The mouse on the screen.</summary>
		public Point MousePosition { get; }

		/// <summary>The mouse in the element the event happened on.</summary>
		public Point OffsetMousePosition { get; }

		/// <summary>The event's return value (false cancels the default action in mshtml); set after the page has
		/// handled it, so it changes nothing here.</summary>
		public bool ReturnValue { get; set; } = true;

		public bool ShiftKeyPressed { get; }

		/// <summary>The element the mouse went to (mouseout, mouseleave).</summary>
		public HtmlElement ToElement => _to < 0 ? null : new HtmlElement (_bridge, HtmlEvents.SlotExpression (_to));
	}

	/// <summary>A script error in the page (window.onerror).</summary>
	public sealed class HtmlElementErrorEventArgs : EventArgs
	{
		internal HtmlElementErrorEventArgs (string description, int lineNumber, Uri url)
		{
			Description = description ?? string.Empty;
			LineNumber = lineNumber;
			Url = url;
		}

		public string Description { get; }

		/// <summary>Whether the error was handled; set after the page has reported it.</summary>
		public bool Handled { get; set; }

		public int LineNumber { get; }

		public Uri Url { get; }
	}

	/// <summary>The handlers of the page's events, by the page object they listen on.</summary>
	internal sealed class HtmlEvents
	{
		internal const string SlotArray = "window.__wfEvSlots";

		// the page's side: a listener per subscription, each event posted back with what .NET's args carry
		internal const string Script = @"(function(){
if (window.__wfEv) { return; }
var slots = window.__wfEvSlots = [];
function park(n) { if (!n || n.nodeType !== 1) { return -1; } slots.push(n); return slots.length - 1; }
window.__wfEv = {
  subs: {},
  on: function (id, t, type) {
    if (!t) { return; }
    var h = function (e) {
      var over = e.type === 'mouseover' || e.type === 'mouseenter', out = e.type === 'mouseout' || e.type === 'mouseleave';
      var m = { id: id, type: e.type, cx: e.clientX | 0, cy: e.clientY | 0, ox: e.offsetX | 0, oy: e.offsetY | 0,
        sx: e.screenX | 0, sy: e.screenY | 0, b: e.buttons | 0, k: (e.keyCode || e.which || 0) | 0,
        alt: !!e.altKey, ctrl: !!e.ctrlKey, shift: !!e.shiftKey,
        from: park(over ? e.relatedTarget : out ? e.target : null), to: park(over ? e.target : out ? e.relatedTarget : null),
        msg: e.message || null, line: e.lineno | 0, url: e.filename || null };
      window.chrome.webview.postMessage('__wfEvent:' + JSON.stringify(m));
    };
    t.addEventListener(type, h);
    this.subs[id] = [t, type, h];
  },
  off: function (id) { var s = this.subs[id]; if (s) { s[0].removeEventListener(s[1], s[2]); delete this.subs[id]; } }
};
})();";

		internal const string MessagePrefix = "__wfEvent:";

		private sealed class Subscription
		{
			public int Id;
			public string Target;
			public string Type;
			public Func<object> Sender;
			public readonly List<Delegate> Handlers = new List<Delegate> ();
		}

		private readonly HtmlBridge _bridge;
		private readonly Dictionary<string, Subscription> _byKey = new Dictionary<string, Subscription> ();
		private readonly Dictionary<int, Subscription> _byId = new Dictionary<int, Subscription> ();
		private int _nextId = 1;

		internal HtmlEvents (HtmlBridge bridge) => _bridge = bridge;

		internal static string SlotExpression (int slot) => SlotArray + "[" + slot.ToString (CultureInfo.InvariantCulture) + "]";

		/// <summary>Adds <paramref name="handler"/> for <paramref name="type"/> on the page object
		/// <paramref name="target"/> (an expression); the page listens from the first handler on.</summary>
		internal void Add (string target, string type, Delegate handler, Func<object> sender)
		{
			if (handler == null)
				return;
			string key = target + "|" + type;
			if (!_byKey.TryGetValue (key, out Subscription sub)) {
				sub = new Subscription { Id = _nextId++, Target = target, Type = type, Sender = sender };
				_byKey [key] = sub;
				_byId [sub.Id] = sub;
				// a stop is the browser's (WebBrowser.Stop): nothing in the page listens for it
				if (!type.StartsWith ("__", StringComparison.Ordinal))
					_bridge.Eval (Script + "window.__wfEv.on(" + sub.Id.ToString (CultureInfo.InvariantCulture) + ", (function(){try{return " + target + ";}catch(e){return null;}})(), " + WebViewScript.ToJson (type) + ");");
			}
			sub.Handlers.Add (handler);
		}

		internal void Remove (string target, string type, Delegate handler)
		{
			string key = target + "|" + type;
			if (handler == null || !_byKey.TryGetValue (key, out Subscription sub))
				return;
			sub.Handlers.Remove (handler);
			if (sub.Handlers.Count > 0)
				return;
			_byKey.Remove (key);
			_byId.Remove (sub.Id);
			if (!type.StartsWith ("__", StringComparison.Ordinal))
				_bridge.Eval ("window.__wfEv && window.__wfEv.off(" + sub.Id.ToString (CultureInfo.InvariantCulture) + ");");
		}

		/// <summary>A new document: the listeners were the old one's.</summary>
		internal void Clear ()
		{
			_byKey.Clear ();
			_byId.Clear ();
		}

		/// <summary>An event the browser raises itself (a document's Stop).</summary>
		internal void RaiseLocal (string target, string type)
		{
			if (_byKey.TryGetValue (target + "|" + type, out Subscription sub))
				Invoke (sub, new HtmlElementEventArgs (_bridge, type.TrimStart ('_'), 0, 0, 0, 0, 0, 0, 0, 0, false, false, false, -1, -1), null);
		}

		/// <summary>The page posted an event.</summary>
		internal void Dispatch (string json)
		{
			JsonElement m;
			try {
				m = JsonDocument.Parse (json).RootElement;
			} catch (JsonException) {
				return;
			}
			if (!_byId.TryGetValue (Int (m, "id"), out Subscription sub))
				return;
			string type = Str (m, "type") ?? sub.Type;
			var args = new HtmlElementEventArgs (_bridge, type, Int (m, "cx"), Int (m, "cy"), Int (m, "ox"), Int (m, "oy"),
				Int (m, "sx"), Int (m, "sy"), Int (m, "b"), Int (m, "k"), Bool (m, "alt"), Bool (m, "ctrl"), Bool (m, "shift"),
				Int (m, "from", -1), Int (m, "to", -1));
			HtmlElementErrorEventArgs error = null;
			if (type == "error") {
				Uri.TryCreate (Str (m, "url"), UriKind.Absolute, out Uri url);
				error = new HtmlElementErrorEventArgs (Str (m, "msg"), Int (m, "line"), url);
			}
			Invoke (sub, args, error);
		}

		private static void Invoke (Subscription sub, HtmlElementEventArgs args, HtmlElementErrorEventArgs error)
		{
			object sender = sub.Sender ();
			foreach (Delegate handler in sub.Handlers.ToArray ()) {
				switch (handler) {
				case HtmlElementEventHandler h: h (sender, args); break;
				case HtmlElementErrorEventHandler h when error != null: h (sender, error); break;
				case EventHandler h: h (sender, EventArgs.Empty); break;
				}
			}
		}

		private static int Int (JsonElement m, string name, int fallback = 0) =>
			m.TryGetProperty (name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32 (out int i) ? i : fallback;

		private static bool Bool (JsonElement m, string name) =>
			m.TryGetProperty (name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

		private static string Str (JsonElement m, string name) =>
			m.TryGetProperty (name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString () : null;

		/// <summary>An event name as AttachEventHandler takes it ("onclick"), as the page's ("click").</summary>
		internal static string DomName (string eventName)
		{
			if (string.IsNullOrEmpty (eventName))
				throw new ArgumentException ("The event name is empty.", nameof (eventName));
			return eventName.StartsWith ("on", StringComparison.OrdinalIgnoreCase) ? eventName.Substring (2).ToLowerInvariant () : eventName.ToLowerInvariant ();
		}
	}

	internal sealed partial class HtmlBridge
	{
		private HtmlEvents _events;

		internal HtmlEvents Events => _events ??= new HtmlEvents (this);
	}
	public sealed class HtmlDocument : IEquatable<HtmlDocument>
	{
		private readonly HtmlBridge _bridge;
		// the document's expression: "document", or a frame's ("window.frames[1].document")
		private readonly string _doc;

		internal HtmlDocument (HtmlBridge bridge) : this (bridge, "document")
		{
		}

		internal HtmlDocument (HtmlBridge bridge, string documentExpression)
		{
			_bridge = bridge;
			_doc = documentExpression;
		}

		private string Target => _doc;
		private object Sender () => this;

		private HtmlElement Element (string expression) =>
			_bridge.Exists (expression) ? new HtmlElement (_bridge, expression) : null;

		private Color GetColor (string property) =>
			ColorFromHtml (_bridge.EvalString (_doc + "." + property));

		private void SetColor (string property, Color value) =>
			_bridge.Eval (_doc + "." + property + " = " + WebViewScript.ToJson (ColorTranslator.ToHtml (value)));

		internal static Color ColorFromHtml (string value)
		{
			if (string.IsNullOrEmpty (value))
				return Color.Empty;
			try {
				return ColorTranslator.FromHtml (value);
			} catch (Exception) {
				return Color.Empty;
			}
		}

		public string Title {
			get => _bridge.EvalString (_doc + ".title");
			set => _bridge.Eval (_doc + ".title = " + WebViewScript.ToJson (value));
		}

		public Uri Url {
			get {
				string href = _bridge.EvalString (_doc + ".location ? " + _doc + ".location.href : null");
				return Uri.TryCreate (href, UriKind.Absolute, out Uri parsed) ? parsed : null;
			}
		}

		public string Domain {
			get => _bridge.EvalString (_doc + ".domain");
			set => _bridge.Eval (_doc + ".domain = " + WebViewScript.ToJson (value));
		}

		public HtmlElement ActiveElement => Element (_doc + ".activeElement");

		public HtmlElement Body => Element (_doc + ".body");

		public HtmlElement DocumentElement => Element (_doc + ".documentElement");

		public HtmlWindow Window => new HtmlWindow (_bridge, _doc + ".defaultView");

		public Color BackColor { get => GetColor ("bgColor"); set => SetColor ("bgColor", value); }

		public Color ForeColor { get => GetColor ("fgColor"); set => SetColor ("fgColor", value); }

		public Color LinkColor { get => GetColor ("linkColor"); set => SetColor ("linkColor", value); }

		public Color ActiveLinkColor { get => GetColor ("alinkColor"); set => SetColor ("alinkColor", value); }

		public Color VisitedLinkColor { get => GetColor ("vlinkColor"); set => SetColor ("vlinkColor", value); }

		/// <summary>Whether the document has the focus.</summary>
		public bool Focused => _bridge.EvalBool (_doc + ".hasFocus()");

		/// <summary>The COM document mshtml had; there is none behind a script engine.</summary>
		public object DomDocument => null;

		public bool RightToLeft {
			get => string.Equals (_bridge.EvalString (_doc + ".dir || (" + _doc + ".documentElement ? " + _doc + ".documentElement.dir : '')"), "rtl", StringComparison.OrdinalIgnoreCase);
			set => _bridge.Eval (_doc + ".dir = " + (value ? "'rtl'" : "'ltr'"));
		}

		/// <summary>The document's character set.</summary>
		public string Encoding {
			get => _bridge.EvalString (_doc + ".characterSet");
			set {
				// a page's charset is its own once it is parsed; a meta tag says it for what is written next
				_bridge.Eval ("(function(d,c){var m=d.querySelector('meta[charset]')||d.createElement('meta');m.setAttribute('charset',c);if(!m.parentNode&&d.head){d.head.insertBefore(m,d.head.firstChild);}})(" + _doc + ", " + WebViewScript.ToJson (value) + ")");
			}
		}

		public string DefaultEncoding => "utf-8";

		public HtmlElementCollection Links => new HtmlElementCollection (_bridge, _doc + ".links");

		public HtmlElementCollection Images => new HtmlElementCollection (_bridge, _doc + ".images");

		public HtmlElementCollection Forms => new HtmlElementCollection (_bridge, _doc + ".forms");

		public HtmlElementCollection All => new HtmlElementCollection (_bridge, _doc + ".getElementsByTagName('*')");

		public HtmlElement GetElementById (string id) =>
			Element (_doc + ".getElementById(" + WebViewScript.ToJson (id) + ")");

		public HtmlElementCollection GetElementsByTagName (string tagName) =>
			new HtmlElementCollection (_bridge, _doc + ".getElementsByTagName(" + WebViewScript.ToJson (tagName) + ")");

		/// <summary>The element at a point of the client area.</summary>
		public HtmlElement GetElementFromPoint (Point point)
		{
			string expression = _doc + ".elementFromPoint(" + point.X.ToString (CultureInfo.InvariantCulture) + ", " + point.Y.ToString (CultureInfo.InvariantCulture) + ")";
			if (!_bridge.Exists (expression))
				return null;
			return new HtmlElement (_bridge, HtmlBridge.SlotExpression (_bridge.Park (expression)));
		}

		public HtmlElement CreateElement (string elementTag)
		{
			int slot = _bridge.Park (_doc + ".createElement(" + WebViewScript.ToJson (elementTag) + ")");
			return new HtmlElement (_bridge, HtmlBridge.SlotExpression (slot));
		}

		/// <summary>Runs an editing command (bold, copy, ...) on the document or its selection.</summary>
		public void ExecCommand (string command, bool showUI, object value) =>
			_bridge.Eval (_doc + ".execCommand(" + WebViewScript.ToJson (command) + ", " + (showUI ? "true" : "false") + ", " + WebViewScript.ToJson (value) + ")");

		public void Focus () => _bridge.Eval ("(" + _doc + ".defaultView || window).focus()");

		/// <summary>Opens the document for writing anew (document.open), replacing it in the history where asked.</summary>
		public HtmlDocument OpenNew (bool replaceInHistory)
		{
			_bridge.Eval (_doc + ".open('text/html', " + (replaceInHistory ? "'replace'" : "''") + ")");
			return this;
		}

		public object InvokeScript (string scriptName) => InvokeScript (scriptName, null);

		public object InvokeScript (string scriptName, object [] args) =>
			_bridge.Invoke (WebViewScript.BuildInvokeExpression (scriptName, args));

		public void Write (string text) =>
			_bridge.Eval (_doc + ".open(); " + _doc + ".write(" + WebViewScript.ToJson (text) + "); " + _doc + ".close();");

		public string Cookie {
			get => _bridge.EvalString (_doc + ".cookie");
			set => _bridge.Eval (_doc + ".cookie = " + WebViewScript.ToJson (value));
		}

		/// <summary>Listens for <paramref name="eventName"/> ("onclick") on the document.</summary>
		public void AttachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Add (Target, HtmlEvents.DomName (eventName), eventHandler, Sender);

		public void DetachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Remove (Target, HtmlEvents.DomName (eventName), eventHandler);

		internal void RaiseStop () => _bridge.Events.RaiseLocal (Target, "__stop");

		public event HtmlElementEventHandler Click {
			add => _bridge.Events.Add (Target, "click", value, Sender);
			remove => _bridge.Events.Remove (Target, "click", value);
		}
		public event HtmlElementEventHandler ContextMenuShowing {
			add => _bridge.Events.Add (Target, "contextmenu", value, Sender);
			remove => _bridge.Events.Remove (Target, "contextmenu", value);
		}
		public event HtmlElementEventHandler Focusing {
			add => _bridge.Events.Add (Target, "focusin", value, Sender);
			remove => _bridge.Events.Remove (Target, "focusin", value);
		}
		public event HtmlElementEventHandler LosingFocus {
			add => _bridge.Events.Add (Target, "focusout", value, Sender);
			remove => _bridge.Events.Remove (Target, "focusout", value);
		}
		public event HtmlElementEventHandler MouseDown {
			add => _bridge.Events.Add (Target, "mousedown", value, Sender);
			remove => _bridge.Events.Remove (Target, "mousedown", value);
		}
		public event HtmlElementEventHandler MouseLeave {
			add => _bridge.Events.Add (Target, "mouseleave", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseleave", value);
		}
		public event HtmlElementEventHandler MouseMove {
			add => _bridge.Events.Add (Target, "mousemove", value, Sender);
			remove => _bridge.Events.Remove (Target, "mousemove", value);
		}
		public event HtmlElementEventHandler MouseOver {
			add => _bridge.Events.Add (Target, "mouseover", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseover", value);
		}
		public event HtmlElementEventHandler MouseUp {
			add => _bridge.Events.Add (Target, "mouseup", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseup", value);
		}
		public event HtmlElementEventHandler Stop {
			add => _bridge.Events.Add (Target, "__stop", value, Sender);
			remove => _bridge.Events.Remove (Target, "__stop", value);
		}

		public static bool operator == (HtmlDocument left, HtmlDocument right) =>
			ReferenceEquals (left, right) || (left is not null && left.Equals (right));

		public static bool operator != (HtmlDocument left, HtmlDocument right) => !(left == right);

		public bool Equals (HtmlDocument other) =>
			other is not null && (other._doc == _doc || _bridge.EvalBool ("(" + _doc + ") === (" + other._doc + ")"));

		public override bool Equals (object obj) => obj is HtmlDocument other && Equals (other);

		public override int GetHashCode () => _doc.GetHashCode ();
	}
	public sealed class HtmlWindow
	{
		private readonly HtmlBridge _bridge;
		// the window's expression: "window", a frame's ("window.frames[1]"), an opener's
		private readonly string _win;

		internal HtmlWindow (HtmlBridge bridge) : this (bridge, "window")
		{
		}

		internal HtmlWindow (HtmlBridge bridge, string windowExpression)
		{
			_bridge = bridge;
			_win = windowExpression;
		}

		private string Target => _win;
		private object Sender () => this;

		private HtmlWindow Window (string expression) =>
			_bridge.Exists (expression) ? new HtmlWindow (_bridge, expression) : null;

		public HtmlDocument Document => new HtmlDocument (_bridge, _win + ".document");

		public string Name {
			get => _bridge.EvalString (_win + ".name");
			set => _bridge.Eval (_win + ".name = " + WebViewScript.ToJson (value));
		}

		public Uri Url {
			get {
				string href = _bridge.EvalString (_win + ".location.href");
				return Uri.TryCreate (href, UriKind.Absolute, out Uri parsed) ? parsed : null;
			}
		}

		public Size Size {
			get {
				int [] v = HtmlBridge.ReadIntArray (_bridge.EvalRaw ("[" + _win + ".innerWidth, " + _win + ".innerHeight]"));
				return v.Length == 2 ? new Size (v [0], v [1]) : Size.Empty;
			}
			set => ResizeTo (value);
		}

		/// <summary>Where the window is on the screen.</summary>
		public Point Position {
			get {
				int [] v = HtmlBridge.ReadIntArray (_bridge.EvalRaw ("[" + _win + ".screenX, " + _win + ".screenY]"));
				return v.Length == 2 ? new Point (v [0], v [1]) : Point.Empty;
			}
		}

		public HtmlWindowCollection Frames => new HtmlWindowCollection (_bridge, _win + ".frames");

		public HtmlHistory History => new HtmlHistory (_bridge, _win + ".history");

		public bool IsClosed => _bridge.EvalBool ("!!" + _win + ".closed");

		public HtmlWindow Opener => Window (_win + ".opener");

		public HtmlWindow Parent => Window (_win + ".parent");

		/// <summary>The window's status bar text (window.status, which the WebBrowser shows as StatusText).</summary>
		public string StatusBarText {
			get => _bridge.EvalString (_win + ".status");
			set => _bridge.Eval (_win + ".status = " + WebViewScript.ToJson (value));
		}

		/// <summary>The frame element a frame's window is shown in; null for a top-level window.</summary>
		public HtmlElement WindowFrameElement =>
			_bridge.Exists (_win + ".frameElement") ? new HtmlElement (_bridge, _win + ".frameElement") : null;

		/// <summary>The COM window mshtml had; there is none behind a script engine.</summary>
		public object DomWindow => null;

		public void Alert (string message) => _bridge.Eval (_win + ".alert(" + WebViewScript.ToJson (message) + ")");

		public bool Confirm (string message) => _bridge.EvalBool (_win + ".confirm(" + WebViewScript.ToJson (message) + ")");

		public string Prompt (string message, string defaultInputValue) =>
			_bridge.EvalString (_win + ".prompt(" + WebViewScript.ToJson (message) + ", " + WebViewScript.ToJson (defaultInputValue) + ")");

		public void Close () => _bridge.Eval (_win + ".close()");

		public void Focus () => _bridge.Eval (_win + ".focus()");

		public void RemoveFocus () => _bridge.Eval (_win + ".blur()");

		public void MoveTo (int x, int y) => _bridge.Eval (_win + ".moveTo(" + x.ToString (CultureInfo.InvariantCulture) + ", " + y.ToString (CultureInfo.InvariantCulture) + ")");

		public void MoveTo (Point point) => MoveTo (point.X, point.Y);

		public void ResizeTo (int width, int height) => _bridge.Eval (_win + ".resizeTo(" + width.ToString (CultureInfo.InvariantCulture) + ", " + height.ToString (CultureInfo.InvariantCulture) + ")");

		public void ResizeTo (Size size) => ResizeTo (size.Width, size.Height);

		public void ScrollTo (int x, int y) => _bridge.Eval (_win + ".scrollTo(" + x.ToString (CultureInfo.InvariantCulture) + ", " + y.ToString (CultureInfo.InvariantCulture) + ")");

		public void ScrollTo (Point point) => ScrollTo (point.X, point.Y);

		public void Navigate (string urlString) => _bridge.Eval (_win + ".location.href = " + WebViewScript.ToJson (urlString));

		public void Navigate (Uri url) => Navigate (url?.ToString () ?? throw new ArgumentNullException (nameof (url)));

		/// <summary>Opens <paramref name="urlString"/> in the window named <paramref name="target"/> (a new one for
		/// "_blank"), as window.open does; the page's new-window handling decides where a new one goes.</summary>
		public HtmlWindow Open (string urlString, string target, string windowOptions, bool replaceEntry)
		{
			int slot = _bridge.Park (_win + ".open(" + WebViewScript.ToJson (urlString) + ", " + WebViewScript.ToJson (target ?? "_blank") + ", " + WebViewScript.ToJson (windowOptions ?? "") + ")");
			string opened = HtmlBridge.SlotExpression (slot);
			return _bridge.Exists (opened) ? new HtmlWindow (_bridge, opened) : null;
		}

		public HtmlWindow Open (Uri url, string target, string windowOptions, bool replaceEntry) =>
			Open (url?.ToString () ?? throw new ArgumentNullException (nameof (url)), target, windowOptions, replaceEntry);

		public HtmlWindow OpenNew (string urlString, string windowOptions) => Open (urlString, "_blank", windowOptions, false);

		public HtmlWindow OpenNew (Uri url, string windowOptions) =>
			OpenNew (url?.ToString () ?? throw new ArgumentNullException (nameof (url)), windowOptions);

		/// <summary>Listens for <paramref name="eventName"/> ("onload") on the window.</summary>
		public void AttachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Add (Target, HtmlEvents.DomName (eventName), eventHandler, Sender);

		public void DetachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Remove (Target, HtmlEvents.DomName (eventName), eventHandler);

		public event HtmlElementErrorEventHandler Error {
			add => _bridge.Events.Add (Target, "error", value, Sender);
			remove => _bridge.Events.Remove (Target, "error", value);
		}

		public event HtmlElementEventHandler GotFocus {
			add => _bridge.Events.Add (Target, "focus", value, Sender);
			remove => _bridge.Events.Remove (Target, "focus", value);
		}
		public event HtmlElementEventHandler Load {
			add => _bridge.Events.Add (Target, "load", value, Sender);
			remove => _bridge.Events.Remove (Target, "load", value);
		}
		public event HtmlElementEventHandler LostFocus {
			add => _bridge.Events.Add (Target, "blur", value, Sender);
			remove => _bridge.Events.Remove (Target, "blur", value);
		}
		public event HtmlElementEventHandler Resize {
			add => _bridge.Events.Add (Target, "resize", value, Sender);
			remove => _bridge.Events.Remove (Target, "resize", value);
		}
		public event HtmlElementEventHandler Scroll {
			add => _bridge.Events.Add (Target, "scroll", value, Sender);
			remove => _bridge.Events.Remove (Target, "scroll", value);
		}
		public event HtmlElementEventHandler Unload {
			add => _bridge.Events.Add (Target, "unload", value, Sender);
			remove => _bridge.Events.Remove (Target, "unload", value);
		}

		public static bool operator == (HtmlWindow left, HtmlWindow right) =>
			ReferenceEquals (left, right) || (left is not null && left.Equals (right));

		public static bool operator != (HtmlWindow left, HtmlWindow right) => !(left == right);

		public bool Equals (HtmlWindow other) =>
			other is not null && (other._win == _win || _bridge.EvalBool ("(" + _win + ") === (" + other._win + ")"));

		public override bool Equals (object obj) => obj is HtmlWindow other && Equals (other);

		public override int GetHashCode () => _win.GetHashCode ();
	}

	/// <summary>A window's frames.</summary>
	public class HtmlWindowCollection : ICollection
	{
		private readonly HtmlBridge _bridge;
		private readonly string _frames;

		internal HtmlWindowCollection (HtmlBridge bridge, string framesExpression)
		{
			_bridge = bridge;
			_frames = framesExpression;
		}

		public int Count => (int) _bridge.EvalDouble ("(" + _frames + ") ? (" + _frames + ").length : 0");

		public HtmlWindow this [int index] {
			get {
				if (index < 0 || index >= Count)
					throw new ArgumentOutOfRangeException (nameof (index));
				return new HtmlWindow (_bridge, "(" + _frames + ")[" + index.ToString (CultureInfo.InvariantCulture) + "]");
			}
		}

		public HtmlWindow this [string windowId] {
			get {
				string item = "(" + _frames + ")[" + WebViewScript.ToJson (windowId) + "]";
				if (!_bridge.Exists (item))
					throw new ArgumentException ($"No frame named '{windowId}'.", nameof (windowId));
				return new HtmlWindow (_bridge, item);
			}
		}

		public IEnumerator GetEnumerator ()
		{
			int count = Count;
			var frames = new List<HtmlWindow> (count);
			for (int i = 0; i < count; i++)
				frames.Add (this [i]);
			return frames.GetEnumerator ();
		}

		void ICollection.CopyTo (Array array, int index)
		{
			foreach (object item in this)
				array.SetValue (item, index++);
		}

		object ICollection.SyncRoot => this;
		bool ICollection.IsSynchronized => false;
	}

	/// <summary>A window's history: back, forward, go.</summary>
	public sealed class HtmlHistory : IDisposable
	{
		private readonly HtmlBridge _bridge;
		private readonly string _history;

		internal HtmlHistory (HtmlBridge bridge, string historyExpression)
		{
			_bridge = bridge;
			_history = historyExpression;
		}

		public int Length => (int) _bridge.EvalDouble (_history + ".length");

		/// <summary>The COM history mshtml had; there is none behind a script engine.</summary>
		public object DomHistory => null;

		public void Back (int numberBack)
		{
			if (numberBack < 0)
				throw new ArgumentOutOfRangeException (nameof (numberBack));
			if (numberBack > 0)
				Go (-numberBack);
		}

		public void Forward (int numberForward)
		{
			if (numberForward < 0)
				throw new ArgumentOutOfRangeException (nameof (numberForward));
			if (numberForward > 0)
				Go (numberForward);
		}

		public void Go (int relativePosition) =>
			_bridge.Eval (_history + ".go(" + relativePosition.ToString (CultureInfo.InvariantCulture) + ")");

		/// <summary>Goes to <paramref name="urlString"/> (mshtml looked it up in the history; a page goes there).</summary>
		public void Go (string urlString) =>
			_bridge.Eval (_history.Substring (0, _history.Length - ".history".Length) + ".location.href = " + WebViewScript.ToJson (urlString));

		public void Go (Uri url) => Go (url?.ToString () ?? throw new ArgumentNullException (nameof (url)));

		public void Dispose ()
		{
		}
	}
	public sealed partial class HtmlElement
	{
		private string Target => _path;
		private object Sender () => this;

		private HtmlElement Related (string member)
		{
			string expression = _path + "." + member;
			return _bridge.Exists (expression) ? new HtmlElement (_bridge, expression) : null;
		}

		private Rectangle Rect (string script)
		{
			int [] v = HtmlBridge.ReadIntArray (_bridge.EvalRaw (script));
			return v.Length == 4 ? new Rectangle (v [0], v [1], v [2], v [3]) : Rectangle.Empty;
		}

		/// <summary>Every element under this one.</summary>
		public HtmlElementCollection All => new HtmlElementCollection (_bridge, _path + ".getElementsByTagName('*')");

		// HTML's void elements (and the frames mshtml would not fill)
		private static readonly HashSet<string> s_void = new HashSet<string> (StringComparer.OrdinalIgnoreCase) {
			"AREA", "BASE", "BR", "COL", "EMBED", "HR", "IMG", "INPUT", "LINK", "META", "PARAM", "SOURCE", "TRACK", "WBR", "FRAME", "IFRAME",
		};

		/// <summary>Whether the element can have children (it is not a void element).</summary>
		public bool CanHaveChildren => !s_void.Contains (TagName ?? string.Empty);

		/// <summary>The element's client area: its border's width and height in, its client width and height.</summary>
		public Rectangle ClientRectangle =>
			Rect ("(function(e){return e?[e.clientLeft,e.clientTop,e.clientWidth,e.clientHeight]:[];})(" + _path + ")");

		/// <summary>The element's scrolled content: where it is scrolled to, and how big it is.</summary>
		public Rectangle ScrollRectangle =>
			Rect ("(function(e){return e?[e.scrollLeft,e.scrollTop,e.scrollWidth,e.scrollHeight]:[];})(" + _path + ")");

		public HtmlElement FirstChild => Related ("firstElementChild");

		public HtmlElement NextSibling => Related ("nextElementSibling");

		public HtmlElement OffsetParent => Related ("offsetParent");

		public short TabIndex {
			get => (short) _bridge.EvalDouble (_path + ".tabIndex");
			set => _bridge.Eval (_path + ".tabIndex = " + value.ToString (CultureInfo.InvariantCulture));
		}

		/// <summary>The COM element mshtml had; there is none behind a script engine.</summary>
		public object DomElement => null;

		/// <summary>Raises <paramref name="eventName"/> ("onclick") on the element, as the page would.</summary>
		public void RaiseEvent (string eventName)
		{
			string type = HtmlEvents.DomName (eventName);
			string create = type is "click" or "dblclick" or "mousedown" or "mouseup" or "mousemove" or "mouseover" or "mouseout" or "contextmenu"
				? "new MouseEvent(" + WebViewScript.ToJson (type) + ", {bubbles:true, cancelable:true, view:window})"
				: "new Event(" + WebViewScript.ToJson (type) + ", {bubbles:true, cancelable:true})";
			_bridge.Eval ("(function(e){if(e){e.dispatchEvent(" + create + ");}})(" + _path + ")");
		}

		/// <summary>Listens for <paramref name="eventName"/> ("onclick") on the element.</summary>
		public void AttachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Add (Target, HtmlEvents.DomName (eventName), eventHandler, Sender);

		public void DetachEventHandler (string eventName, EventHandler eventHandler) =>
			_bridge.Events.Remove (Target, HtmlEvents.DomName (eventName), eventHandler);

		public event HtmlElementEventHandler Click {
			add => _bridge.Events.Add (Target, "click", value, Sender);
			remove => _bridge.Events.Remove (Target, "click", value);
		}
		public event HtmlElementEventHandler DoubleClick {
			add => _bridge.Events.Add (Target, "dblclick", value, Sender);
			remove => _bridge.Events.Remove (Target, "dblclick", value);
		}
		public event HtmlElementEventHandler Drag {
			add => _bridge.Events.Add (Target, "drag", value, Sender);
			remove => _bridge.Events.Remove (Target, "drag", value);
		}
		public event HtmlElementEventHandler DragEnd {
			add => _bridge.Events.Add (Target, "dragend", value, Sender);
			remove => _bridge.Events.Remove (Target, "dragend", value);
		}
		public event HtmlElementEventHandler DragLeave {
			add => _bridge.Events.Add (Target, "dragleave", value, Sender);
			remove => _bridge.Events.Remove (Target, "dragleave", value);
		}
		public event HtmlElementEventHandler DragOver {
			add => _bridge.Events.Add (Target, "dragover", value, Sender);
			remove => _bridge.Events.Remove (Target, "dragover", value);
		}
		public event HtmlElementEventHandler Focusing {
			add => _bridge.Events.Add (Target, "focusin", value, Sender);
			remove => _bridge.Events.Remove (Target, "focusin", value);
		}
		public event HtmlElementEventHandler GotFocus {
			add => _bridge.Events.Add (Target, "focus", value, Sender);
			remove => _bridge.Events.Remove (Target, "focus", value);
		}
		public event HtmlElementEventHandler LosingFocus {
			add => _bridge.Events.Add (Target, "focusout", value, Sender);
			remove => _bridge.Events.Remove (Target, "focusout", value);
		}
		public event HtmlElementEventHandler LostFocus {
			add => _bridge.Events.Add (Target, "blur", value, Sender);
			remove => _bridge.Events.Remove (Target, "blur", value);
		}
		public event HtmlElementEventHandler KeyDown {
			add => _bridge.Events.Add (Target, "keydown", value, Sender);
			remove => _bridge.Events.Remove (Target, "keydown", value);
		}
		public event HtmlElementEventHandler KeyPress {
			add => _bridge.Events.Add (Target, "keypress", value, Sender);
			remove => _bridge.Events.Remove (Target, "keypress", value);
		}
		public event HtmlElementEventHandler KeyUp {
			add => _bridge.Events.Add (Target, "keyup", value, Sender);
			remove => _bridge.Events.Remove (Target, "keyup", value);
		}
		public event HtmlElementEventHandler MouseMove {
			add => _bridge.Events.Add (Target, "mousemove", value, Sender);
			remove => _bridge.Events.Remove (Target, "mousemove", value);
		}
		public event HtmlElementEventHandler MouseDown {
			add => _bridge.Events.Add (Target, "mousedown", value, Sender);
			remove => _bridge.Events.Remove (Target, "mousedown", value);
		}
		public event HtmlElementEventHandler MouseOver {
			add => _bridge.Events.Add (Target, "mouseover", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseover", value);
		}
		public event HtmlElementEventHandler MouseUp {
			add => _bridge.Events.Add (Target, "mouseup", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseup", value);
		}
		public event HtmlElementEventHandler MouseEnter {
			add => _bridge.Events.Add (Target, "mouseenter", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseenter", value);
		}
		public event HtmlElementEventHandler MouseLeave {
			add => _bridge.Events.Add (Target, "mouseleave", value, Sender);
			remove => _bridge.Events.Remove (Target, "mouseleave", value);
		}

		public static bool operator == (HtmlElement left, HtmlElement right) =>
			ReferenceEquals (left, right) || (left is not null && left.Equals (right));

		public static bool operator != (HtmlElement left, HtmlElement right) => !(left == right);

		public override bool Equals (object obj) => obj is HtmlElement other && Equals (other);

		public override int GetHashCode () => _path.GetHashCode ();
	}
}
