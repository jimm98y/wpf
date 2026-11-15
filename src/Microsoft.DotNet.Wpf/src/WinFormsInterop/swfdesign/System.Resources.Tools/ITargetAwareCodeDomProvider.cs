// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace System.Resources.Tools
{
	/// <summary>A CodeDom provider that knows whether the project's target framework has a property.</summary>
	[EditorBrowsable (EditorBrowsableState.Never)]
	public interface ITargetAwareCodeDomProvider
	{
		bool SupportsProperty (Type type, string propertyName, bool isWritable);
	}
}
