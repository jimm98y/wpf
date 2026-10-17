// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.ComponentModel.Design.DesignerActionService). Every member of the Mono stub
// threw, its constructor included.
//
// One addition: .NET's component sites hand out a DesignerCommandSet built over the component's designer (its
// ActionLists and Verbs); this stack's sites have none, so when a site offers no command set the designer the
// host has for the component is asked directly -- the same two collections by the same route.

using System.Collections;
using System.Collections.Generic;

namespace System.ComponentModel.Design
{
	public class DesignerActionService : IDisposable
	{
		private readonly Dictionary<IComponent, DesignerActionListCollection> _designerActionLists = new Dictionary<IComponent, DesignerActionListCollection> ();
		private DesignerActionListsChangedEventHandler _designerActionListsChanged;
		private readonly IServiceProvider _serviceProvider;
		private readonly ISelectionService _selectionService;
		// Components whose verbs have their CommandChanged hooked.
		private readonly HashSet<IComponent> _componentToVerbsEventHookedUp = new HashSet<IComponent> ();
		// A designer that sets command status when its Verbs are read would recurse through OnVerbStatusChanged.
		private bool _reEntrantCode;

		public DesignerActionService (IServiceProvider serviceProvider)
		{
			if (serviceProvider != null) {
				_serviceProvider = serviceProvider;
				(serviceProvider.GetService (typeof (IDesignerHost)) as IDesignerHost)?.AddService (typeof (DesignerActionService), this);
				if (serviceProvider.GetService (typeof (IComponentChangeService)) is IComponentChangeService componentChangeService)
					componentChangeService.ComponentRemoved += OnComponentRemoved;
				_selectionService = serviceProvider.GetService (typeof (ISelectionService)) as ISelectionService;
			}
		}

		public event DesignerActionListsChangedEventHandler DesignerActionListsChanged {
			add => _designerActionListsChanged += value;
			remove => _designerActionListsChanged -= value;
		}

		public void Add (IComponent comp, DesignerActionListCollection designerActionListCollection)
		{
			if (comp == null)
				throw new ArgumentNullException (nameof (comp));
			if (designerActionListCollection == null)
				throw new ArgumentNullException (nameof (designerActionListCollection));
			if (_designerActionLists.TryGetValue (comp, out DesignerActionListCollection collection))
				collection.AddRange (designerActionListCollection);
			else
				_designerActionLists.Add (comp, designerActionListCollection);
			OnDesignerActionListsChanged (new DesignerActionListsChangedEventArgs (comp, DesignerActionListsChangedType.ActionListsAdded, GetComponentActions (comp)));
		}

		public void Add (IComponent comp, DesignerActionList actionList)
		{
			Add (comp, new DesignerActionListCollection (new DesignerActionList [] { actionList }));
		}

		public void Clear ()
		{
			if (_designerActionLists.Count == 0)
				return;
			IComponent [] compsRemoved = new IComponent [_designerActionLists.Count];
			_designerActionLists.Keys.CopyTo (compsRemoved, 0);
			_designerActionLists.Clear ();
			foreach (IComponent comp in compsRemoved)
				OnDesignerActionListsChanged (new DesignerActionListsChangedEventArgs (comp, DesignerActionListsChangedType.ActionListsRemoved, GetComponentActions (comp)));
		}

		public bool Contains (IComponent comp)
		{
			if (comp == null)
				throw new ArgumentNullException (nameof (comp));
			return _designerActionLists.ContainsKey (comp);
		}

		public void Dispose ()
		{
			Dispose (true);
		}

		protected virtual void Dispose (bool disposing)
		{
			if (disposing && _serviceProvider != null) {
				(_serviceProvider.GetService (typeof (IDesignerHost)) as IDesignerHost)?.RemoveService (typeof (DesignerActionService));
				if (_serviceProvider.GetService (typeof (IComponentChangeService)) is IComponentChangeService componentChangeService)
					componentChangeService.ComponentRemoved -= OnComponentRemoved;
			}
		}

		public DesignerActionListCollection GetComponentActions (IComponent component)
			=> GetComponentActions (component, ComponentActionsType.All);

		public virtual DesignerActionListCollection GetComponentActions (IComponent component, ComponentActionsType type)
		{
			if (component == null)
				throw new ArgumentNullException (nameof (component));
			DesignerActionListCollection result = new DesignerActionListCollection ();
			switch (type) {
			case ComponentActionsType.All:
				GetComponentDesignerActions (component, result);
				GetComponentServiceActions (component, result);
				break;
			case ComponentActionsType.Component:
				GetComponentDesignerActions (component, result);
				break;
			case ComponentActionsType.Service:
				GetComponentServiceActions (component, result);
				break;
			}
			return result;
		}

		/// <summary>The site's command set (or, without one, the component's designer).</summary>
		private DesignerActionListCollection PullActionLists (IComponent component, out DesignerVerbCollection verbs)
		{
			if ((component.Site as IServiceContainer)?.GetService (typeof (DesignerCommandSet)) is DesignerCommandSet commandSet) {
				verbs = commandSet.Verbs;
				return commandSet.ActionLists;
			}
			IDesignerHost host = (component.Site?.GetService (typeof (IDesignerHost)) ?? _serviceProvider?.GetService (typeof (IDesignerHost))) as IDesignerHost;
			IDesigner designer = host?.GetDesigner (component);
			verbs = designer?.Verbs;
			return (designer as ComponentDesigner)?.ActionLists;
		}

		protected virtual void GetComponentDesignerActions (IComponent component, DesignerActionListCollection actionLists)
		{
			if (component == null)
				throw new ArgumentNullException (nameof (component));
			if (actionLists == null)
				throw new ArgumentNullException (nameof (actionLists));

			DesignerActionListCollection pullCollection = PullActionLists (component, out DesignerVerbCollection verbs);
			if (pullCollection == null && verbs == null)
				return;
			if (pullCollection != null)
				actionLists.AddRange (pullCollection);

			// None: the component's verbs stand in for them.
			if (actionLists.Count == 0 && verbs != null && verbs.Count != 0) {
				List<DesignerVerb> verbsArray = new List<DesignerVerb> ();
				bool hookupEvents = _componentToVerbsEventHookedUp.Add (component);
				foreach (DesignerVerb verb in verbs) {
					if (verb == null)
						continue;
					if (hookupEvents)
						verb.CommandChanged += OnVerbStatusChanged;
					if (verb.Enabled && verb.Visible)
						verbsArray.Add (verb);
				}
				if (verbsArray.Count != 0)
					actionLists.Add (new DesignerActionVerbList (verbsArray.ToArray ()));
			}

			// The empty ones go (GetSortedActionItems returns nothing): an empty panel is no panel. After the verb
			// check, so returning an empty collection is how a designer turns the verb upgrade off.
			if (pullCollection != null) {
				foreach (DesignerActionList actionList in pullCollection) {
					DesignerActionItemCollection collection = actionList?.GetSortedActionItems ();
					if (collection == null || collection.Count == 0)
						actionLists.Remove (actionList);
				}
			}
		}

		private void OnVerbStatusChanged (object sender, EventArgs args)
		{
			if (_reEntrantCode)
				return;
			try {
				_reEntrantCode = true;
				if (_selectionService?.PrimarySelection is IComponent comp) {
					PullActionLists (comp, out DesignerVerbCollection verbs);
					if (verbs == null)
						return;
					foreach (DesignerVerb verb in verbs) {
						if (verb == sender) {
							// A verb on the current panel changed state: refresh it.
							DesignerActionUIService ui = (comp.Site?.GetService (typeof (DesignerActionUIService))
								?? _serviceProvider?.GetService (typeof (DesignerActionUIService))) as DesignerActionUIService;
							ui?.Refresh (comp);
						}
					}
				}
			} finally {
				_reEntrantCode = false;
			}
		}

		protected virtual void GetComponentServiceActions (IComponent component, DesignerActionListCollection actionLists)
		{
			if (component == null)
				throw new ArgumentNullException (nameof (component));
			if (actionLists == null)
				throw new ArgumentNullException (nameof (actionLists));
			if (_designerActionLists.TryGetValue (component, out DesignerActionListCollection pushCollection)) {
				actionLists.AddRange (pushCollection);
				foreach (DesignerActionList actionList in pushCollection) {
					DesignerActionItemCollection collection = actionList?.GetSortedActionItems ();
					if (collection == null || collection.Count == 0)
						actionLists.Remove (actionList);
				}
			}
		}

		private void OnComponentRemoved (object source, ComponentEventArgs componentEventArgs)
		{
			Remove (componentEventArgs.Component);
		}

		private void OnDesignerActionListsChanged (DesignerActionListsChangedEventArgs e)
		{
			_designerActionListsChanged?.Invoke (this, e);
		}

		public void Remove (IComponent comp)
		{
			if (comp == null)
				throw new ArgumentNullException (nameof (comp));
			if (_designerActionLists.Remove (comp))
				OnDesignerActionListsChanged (new DesignerActionListsChangedEventArgs (comp, DesignerActionListsChangedType.ActionListsRemoved, GetComponentActions (comp)));
		}

		public void Remove (DesignerActionList actionList)
		{
			if (actionList == null)
				throw new ArgumentNullException (nameof (actionList));
			foreach (KeyValuePair<IComponent, DesignerActionListCollection> entry in _designerActionLists) {
				if (entry.Value.Contains (actionList)) {
					Remove (entry.Key, actionList);
					break;
				}
			}
		}

		public void Remove (IComponent comp, DesignerActionList actionList)
		{
			if (comp == null)
				throw new ArgumentNullException (nameof (comp));
			if (actionList == null)
				throw new ArgumentNullException (nameof (actionList));
			if (!_designerActionLists.TryGetValue (comp, out DesignerActionListCollection actionLists) || !actionLists.Contains (actionList))
				return;
			if (actionLists.Count == 1) {
				// The last list of the component: the component goes.
				Remove (comp);
				return;
			}
			for (int i = actionLists.Count - 1; i >= 0; i--) {
				if (actionList.Equals (actionLists [i]))
					actionLists.RemoveAt (i);
			}
			OnDesignerActionListsChanged (new DesignerActionListsChangedEventArgs (comp, DesignerActionListsChangedType.ActionListsRemoved, GetComponentActions (comp)));
		}

		internal event DesignerActionUIStateChangeEventHandler DesignerActionUIStateChange {
			add {
				if (_serviceProvider?.GetService (typeof (DesignerActionUIService)) is DesignerActionUIService ui)
					ui.DesignerActionUIStateChange += value;
			}
			remove {
				if (_serviceProvider?.GetService (typeof (DesignerActionUIService)) is DesignerActionUIService ui)
					ui.DesignerActionUIStateChange -= value;
			}
		}
	}

	/// <summary>A component's verbs as an action list (dotnet/winforms DesignerActionVerbList).</summary>
	internal class DesignerActionVerbList : DesignerActionList
	{
		private readonly DesignerVerb [] _verbs;

		public DesignerActionVerbList (DesignerVerb [] verbs)
			: base (null)
		{
			_verbs = verbs;
		}

		public override bool AutoShow => false;

		public override DesignerActionItemCollection GetSortedActionItems ()
		{
			DesignerActionItemCollection items = new DesignerActionItemCollection ();
			for (int i = 0; i < _verbs.Length; i++) {
				if (_verbs [i].Visible && _verbs [i].Enabled && _verbs [i].Supported)
					items.Add (new DesignerActionVerbItem (_verbs [i]));
			}
			return items;
		}
	}

	/// <summary>A verb as an action item (dotnet/winforms DesignerActionVerbItem).</summary>
	internal class DesignerActionVerbItem : DesignerActionMethodItem
	{
		private readonly DesignerVerb _targetVerb;

		public DesignerActionVerbItem (DesignerVerb verb)
			: base (null, null, null)
		{
			_targetVerb = verb;
		}

		public override string Category => "Verbs";

		public override string DisplayName => _targetVerb.Text;

		public override void Invoke () => _targetVerb.Invoke ();
	}
}
