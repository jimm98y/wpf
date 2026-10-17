// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.ComponentModel.Design.DesignerActionUIService): ShowUI/HideUI/Refresh raise
// DesignerActionUIStateChange, ShouldAutoShow follows the designer option and the component's lists. The Mono
// stub threw from all four.

namespace System.ComponentModel.Design
{
	public sealed class DesignerActionUIService : IDisposable
	{
		private DesignerActionUIStateChangeEventHandler _designerActionUIStateChangedEventHandler;
		private readonly IServiceProvider _serviceProvider;
		private readonly DesignerActionService _designerActionService;

		internal DesignerActionUIService ()
		{
		}

		internal DesignerActionUIService (IServiceProvider serviceProvider)
		{
			_serviceProvider = serviceProvider;
			if (serviceProvider != null) {
				(serviceProvider.GetService (typeof (IDesignerHost)) as IDesignerHost)?.AddService (typeof (DesignerActionUIService), this);
				_designerActionService = serviceProvider.GetService (typeof (DesignerActionService)) as DesignerActionService;
			}
		}

		public void Dispose ()
		{
			(_serviceProvider?.GetService (typeof (IDesignerHost)) as IDesignerHost)?.RemoveService (typeof (DesignerActionUIService));
		}

		public event DesignerActionUIStateChangeEventHandler DesignerActionUIStateChange {
			add => _designerActionUIStateChangedEventHandler += value;
			remove => _designerActionUIStateChangedEventHandler -= value;
		}

		public void HideUI (IComponent component)
			=> OnDesignerActionUIStateChange (new DesignerActionUIStateChangeEventArgs (component, DesignerActionUIStateChangeType.Hide));

		public void ShowUI (IComponent component)
			=> OnDesignerActionUIStateChange (new DesignerActionUIStateChangeEventArgs (component, DesignerActionUIStateChangeType.Show));

		public void Refresh (IComponent component)
			=> OnDesignerActionUIStateChange (new DesignerActionUIStateChangeEventArgs (component, DesignerActionUIStateChangeType.Refresh));

		private void OnDesignerActionUIStateChange (DesignerActionUIStateChangeEventArgs e)
			=> _designerActionUIStateChangedEventHandler?.Invoke (this, e);

		public bool ShouldAutoShow (IComponent component)
		{
			// The designer option first.
			if (_serviceProvider?.GetService (typeof (DesignerOptionService)) is DesignerOptionService opts) {
				PropertyDescriptor p = opts.Options.Properties ["ObjectBoundSmartTagAutoShow"];
				if (p != null && p.PropertyType == typeof (bool) && !(bool) p.GetValue (null))
					return false;
			}
			if (_designerActionService != null) {
				DesignerActionListCollection coll = _designerActionService.GetComponentActions (component);
				if (coll != null) {
					for (int i = 0; i < coll.Count; i++) {
						if (coll [i]?.AutoShow == true)
							return true;
					}
				}
			}
			return false;
		}
	}
}
