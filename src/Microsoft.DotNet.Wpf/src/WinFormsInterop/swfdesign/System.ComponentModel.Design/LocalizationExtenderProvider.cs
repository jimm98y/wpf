// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// LocalizationExtenderProvider, as the .NET Framework's System.Design has it (it is obsolete there, in favour of
// CodeDomLocalizationProvider, and gone from .NET's System.Windows.Forms.Design): the root component of a design
// surface gains Localizable, Language and LoadLanguage. Making the form non-localizable puts it back to the
// invariant language; choosing a language other than the invariant one makes it localizable. The provider
// registers itself with the host's IExtenderProviderService and leaves on Dispose. Every member of the Mono stub
// threw NotImplementedException.

using System.Globalization;

namespace System.ComponentModel.Design
{
	[Obsolete ("use CodeDomLocalizationProvider")]
	[ProvideProperty ("Localizable", typeof (object))]
	[ProvideProperty ("Language", typeof (object))]
	[ProvideProperty ("LoadLanguage", typeof (object))]
	public class LocalizationExtenderProvider : IExtenderProvider, IDisposable
	{
		private IServiceProvider _serviceProvider;
		private readonly IComponent _baseComponent;
		private bool _localizable;
		private CultureInfo _language;
		private CultureInfo _loadLanguage;

		public LocalizationExtenderProvider (ISite serviceProvider, IComponent baseComponent)
		{
			_serviceProvider = serviceProvider;
			_baseComponent = baseComponent;
			_language = CultureInfo.InvariantCulture;
			if (serviceProvider?.GetService (typeof (IExtenderProviderService)) is IExtenderProviderService es)
				es.AddExtenderProvider (this);
		}

		/// <summary>Only the component it was made for (the root) is extended.</summary>
		public bool CanExtend (object o)
		{
			return o == _baseComponent;
		}

		public void Dispose ()
		{
			Dispose (true);
		}

		protected virtual void Dispose (bool disposing)
		{
			if (disposing && _serviceProvider != null) {
				if (_serviceProvider.GetService (typeof (IExtenderProviderService)) is IExtenderProviderService es)
					es.RemoveExtenderProvider (this);
				_serviceProvider = null;
			}
		}

		[Localizable (true)]
		[DesignOnly (true)]
		public CultureInfo GetLanguage (object o)
		{
			return _language;
		}

		/// <summary>The language the designer loaded the resources of; the current one until set.</summary>
		[DesignOnly (true)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[Browsable (false)]
		public CultureInfo GetLoadLanguage (object o)
		{
			return _loadLanguage ?? _language;
		}

		[Localizable (true)]
		[DesignOnly (true)]
		public bool GetLocalizable (object o)
		{
			return _localizable;
		}

		public void ResetLanguage (object o)
		{
			SetLanguage (o, CultureInfo.InvariantCulture);
		}

		public void SetLanguage (object o, CultureInfo language)
		{
			language ??= CultureInfo.InvariantCulture;
			if (_language.Equals (language))
				return;
			_language = language;
			_loadLanguage = language;
			// A language other than the invariant one only makes sense for a localizable form.
			if (!language.Equals (CultureInfo.InvariantCulture))
				_localizable = true;
		}

		public void SetLocalizable (object o, bool localizable)
		{
			_localizable = localizable;
			if (!localizable)
				SetLanguage (o, CultureInfo.InvariantCulture);
		}

		public bool ShouldSerializeLanguage (object o)
		{
			return _language != null && !_language.Equals (CultureInfo.InvariantCulture);
		}

		private bool ShouldSerializeLocalizable (object o)
		{
			return _localizable;
		}

		private void ResetLocalizable (object o)
		{
			SetLocalizable (o, false);
		}
	}
}
