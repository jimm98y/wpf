// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The rest of .NET's WebBrowser surface, on the engine seam: whether a dropped file is navigated to, the context menu
// and the scroll bars (each said to the page as a document comes up), the encryption level of the page, the engine's
// version, the history state and progress events, a download, and the WebOC's dialogs -- printing through the page's
// own print, saving through a save dialog of this WinForms.

using System.ComponentModel;
using System.IO;
using System.Text;
using MS.Internal.Interop.WebView;

namespace System.Windows.Forms
{
	public partial class WebBrowser
	{
		private static readonly object s_canGoBackChanged = new object ();
		private static readonly object s_canGoForwardChanged = new object ();
		private static readonly object s_encryptionLevelChanged = new object ();
		private static readonly object s_fileDownload = new object ();
		private static readonly object s_progressChanged = new object ();

		private bool _allowWebBrowserDrop = true, _contextMenuEnabled = true, _scrollBarsEnabled = true;
		private bool _lastCanGoBack, _lastCanGoForward;
		private WebBrowserEncryptionLevel _lastEncryption = WebBrowserEncryptionLevel.Insecure;

		/// <summary>Whether a file dropped on the browser is navigated to.</summary>
		[DefaultValue (true)]
		public bool AllowWebBrowserDrop {
			get => _allowWebBrowserDrop;
			set {
				_allowWebBrowserDrop = value;
				InstallPageSettings ();
			}
		}

		/// <summary>Whether the page's own context menu shows on a right click.</summary>
		[DefaultValue (true)]
		public bool IsWebBrowserContextMenuEnabled {
			get => _contextMenuEnabled;
			set {
				_contextMenuEnabled = value;
				if (_backend is not null && _backend.IsAttached)
					_backend.ApplySettings (new WebViewSettings { AreDefaultContextMenusEnabled = value });
				InstallPageSettings ();
			}
		}

		/// <summary>Whether the page shows scroll bars where its content needs them.</summary>
		[DefaultValue (true)]
		public bool ScrollBarsEnabled {
			get => _scrollBarsEnabled;
			set {
				_scrollBarsEnabled = value;
				InstallPageSettings ();
			}
		}

		/// <summary>The kind of document shown, as its content type names it ("HTML Document" for HTML).</summary>
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public string DocumentType {
			get {
				string type = _bridge.EvalString ("document.contentType");
				return type switch {
					null or "" => string.Empty,
					"text/html" or "application/xhtml+xml" => "HTML Document",
					_ => type,
				};
			}
		}

		/// <summary>The encryption of the page: 128-bit for https (every engine here negotiates TLS 1.2 or later),
		/// insecure for http, unknown for anything else.</summary>
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public WebBrowserEncryptionLevel EncryptionLevel {
			get {
				Uri url = Url;
				if (url == null)
					return WebBrowserEncryptionLevel.Unknown;
				if (url.Scheme == Uri.UriSchemeHttps)
					return WebBrowserEncryptionLevel.Bit128;
				return url.Scheme == Uri.UriSchemeHttp ? WebBrowserEncryptionLevel.Insecure : WebBrowserEncryptionLevel.Unknown;
			}
		}

		/// <summary>Whether the browser works offline (IE's "Work Offline"): never, here.</summary>
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public bool IsOffline => false;

		/// <summary>The browser's version: the engine's, where it says one.</summary>
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public Version Version {
			get {
				string agent = _bridge.EvalString ("navigator.userAgent") ?? string.Empty;
				foreach (string engine in new [] { "Edg/", "Chrome/", "Version/", "Firefox/", "AppleWebKit/" }) {
					int at = agent.IndexOf (engine, StringComparison.Ordinal);
					if (at < 0)
						continue;
					string number = agent.Substring (at + engine.Length).Split (' ') [0];
					if (Version.TryParse (number, out Version v))
						return v;
				}
				return new Version (0, 0);
			}
		}

		/// <summary>Navigates to the search page.</summary>
		public void GoSearch () => Navigate ("https://www.bing.com/");

		public void ShowPageSetupDialog () => ShowPrintDialog ();

		/// <summary>The page's print dialog (window.print: the engine's, with its own setup and preview).</summary>
		public void ShowPrintDialog () => Print ();

		public void ShowPrintPreviewDialog () => Print ();

		/// <summary>The page's properties: its title, address, type and encryption.</summary>
		public void ShowPropertiesDialog ()
		{
			var text = new StringBuilder ();
			text.AppendLine (DocumentTitle);
			text.AppendLine ();
			text.AppendLine ("Address: " + Url);
			text.AppendLine ("Type: " + DocumentType);
			text.AppendLine ("Connection: " + EncryptionLevel);
			MessageBox.Show (this, text.ToString (), "Properties");
		}

		/// <summary>Saves the page's HTML where the user says.</summary>
		public void ShowSaveAsDialog ()
		{
			using var dialog = new SaveFileDialog {
				Filter = "Web page, HTML only (*.htm;*.html)|*.htm;*.html|Text file (*.txt)|*.txt|All files (*.*)|*.*",
				FileName = string.IsNullOrEmpty (DocumentTitle) ? "page.htm" : DocumentTitle + ".htm",
			};
			if (dialog.ShowDialog (this) == DialogResult.OK)
				File.WriteAllText (dialog.FileName, DocumentText ?? string.Empty);
		}

		// what is said to each document as it comes up: the drop, the context menu, the scroll bars
		private void InstallPageSettings ()
		{
			if (_backend is null || !_backend.IsAttached)
				return;
			string script = "(function(drop,menu,bars){" +
				"var s=window.__wfSettings||(window.__wfSettings={});" +
				"if(!s.hooked){s.hooked=true;" +
				"document.addEventListener('dragover',function(e){if(!window.__wfSettings.drop){e.preventDefault();e.dataTransfer.dropEffect='none';}},true);" +
				"document.addEventListener('drop',function(e){if(!window.__wfSettings.drop){e.preventDefault();}},true);" +
				"document.addEventListener('contextmenu',function(e){if(!window.__wfSettings.menu){e.preventDefault();}},true);}" +
				"s.drop=drop;s.menu=menu;" +
				"if(document.documentElement){document.documentElement.style.overflow=bars?'':'hidden';}" +
				"})(" + (_allowWebBrowserDrop ? "true" : "false") + "," + (_contextMenuEnabled ? "true" : "false") + "," + (_scrollBarsEnabled ? "true" : "false") + ")";
			_ = _backend.ExecuteScriptAsync (script);
		}

		// a navigation ended: the history and encryption as they now are
		private void NavigationStateChanged ()
		{
			if (CanGoBack != _lastCanGoBack) {
				_lastCanGoBack = CanGoBack;
				OnCanGoBackChanged (EventArgs.Empty);
			}
			if (CanGoForward != _lastCanGoForward) {
				_lastCanGoForward = CanGoForward;
				OnCanGoForwardChanged (EventArgs.Empty);
			}
			WebBrowserEncryptionLevel level = EncryptionLevel;
			if (level != _lastEncryption) {
				_lastEncryption = level;
				OnEncryptionLevelChanged (EventArgs.Empty);
			}
		}

		private void OnBackendDownloadStarting (object sender, WebViewDownloadStartingEventArgs e) => OnFileDownload (EventArgs.Empty);

		public event EventHandler CanGoBackChanged {
			add { Events.AddHandler (s_canGoBackChanged, value); }
			remove { Events.RemoveHandler (s_canGoBackChanged, value); }
		}

		public event EventHandler CanGoForwardChanged {
			add { Events.AddHandler (s_canGoForwardChanged, value); }
			remove { Events.RemoveHandler (s_canGoForwardChanged, value); }
		}

		public event EventHandler EncryptionLevelChanged {
			add { Events.AddHandler (s_encryptionLevelChanged, value); }
			remove { Events.RemoveHandler (s_encryptionLevelChanged, value); }
		}

		public event EventHandler FileDownload {
			add { Events.AddHandler (s_fileDownload, value); }
			remove { Events.RemoveHandler (s_fileDownload, value); }
		}

		public event WebBrowserProgressChangedEventHandler ProgressChanged {
			add { Events.AddHandler (s_progressChanged, value); }
			remove { Events.RemoveHandler (s_progressChanged, value); }
		}

		protected virtual void OnCanGoBackChanged (EventArgs e) => (Events [s_canGoBackChanged] as EventHandler)?.Invoke (this, e);

		protected virtual void OnCanGoForwardChanged (EventArgs e) => (Events [s_canGoForwardChanged] as EventHandler)?.Invoke (this, e);

		protected virtual void OnEncryptionLevelChanged (EventArgs e) => (Events [s_encryptionLevelChanged] as EventHandler)?.Invoke (this, e);

		protected virtual void OnFileDownload (EventArgs e) => (Events [s_fileDownload] as EventHandler)?.Invoke (this, e);

		protected virtual void OnProgressChanged (WebBrowserProgressChangedEventArgs e) =>
			(Events [s_progressChanged] as WebBrowserProgressChangedEventHandler)?.Invoke (this, e);

		protected override void AttachInterfaces (object nativeActiveXObject)
		{
		}

		protected override void DetachInterfaces ()
		{
		}

		protected override WebBrowserSiteBase CreateWebBrowserSiteBase () => new WebBrowserSite (this);
	}
}
