// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (Design/Behavior/AdornerCollection.cs). The collection a BehaviorService owns
// starts EMPTY and hands its service to every adorner added to it, which is how an adorner's Invalidate reaches
// the adorner window. (Mono's constructor copied the service's own Adorners -- from a getter that threw.)

using System.Collections;

namespace System.Windows.Forms.Design.Behavior
{
	public sealed class BehaviorServiceAdornerCollection : CollectionBase
	{
		private readonly BehaviorService _behaviorService;
		private int _state;

		public BehaviorServiceAdornerCollection (BehaviorService behaviorService)
		{
			_behaviorService = behaviorService;
		}

		public BehaviorServiceAdornerCollection (BehaviorServiceAdornerCollection value)
		{
			AddRange (value);
		}

		public BehaviorServiceAdornerCollection (Adorner [] value)
		{
			AddRange (value);
		}

		/// <summary>Changes so far, for the enumerator's "collection was modified" check.</summary>
		internal int State => _state;

		public Adorner this [int index] {
			get => (Adorner) List [index];
			set => List [index] = value;
		}

		public int Add (Adorner value)
		{
			value.BehaviorService = _behaviorService;
			return List.Add (value);
		}

		public void AddRange (params Adorner [] value)
		{
			for (int i = 0; i < value.Length; i++)
				Add (value [i]);
		}

		public void AddRange (BehaviorServiceAdornerCollection value)
		{
			for (int i = 0; i < value.Count; i++)
				Add (value [i]);
		}

		public bool Contains (Adorner value) => List.Contains (value);

		public void CopyTo (Adorner [] array, int index) => List.CopyTo (array, index);

		public int IndexOf (Adorner value) => List.IndexOf (value);

		public void Insert (int index, Adorner value) => List.Insert (index, value);

		public new BehaviorServiceAdornerCollectionEnumerator GetEnumerator () => new BehaviorServiceAdornerCollectionEnumerator (this);

		public void Remove (Adorner value) => List.Remove (value);

		protected override void OnInsertComplete (int index, object value) => _state++;
		protected override void OnRemoveComplete (int index, object value) => _state++;
		protected override void OnSetComplete (int index, object oldValue, object newValue) => _state++;
		protected override void OnClearComplete () => _state++;
	}
}
