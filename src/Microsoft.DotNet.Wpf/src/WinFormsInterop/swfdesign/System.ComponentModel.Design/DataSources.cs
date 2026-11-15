// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The data-source designer contracts .NET's System.Windows.Forms.Design declares for a host (Visual Studio's Data
// Sources window) to implement, and the multitargeting ones a designer asks its host for: as .NET's.

using System.Collections;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace System.ComponentModel.Design
{
	public abstract class DataSourceDescriptor
	{
		public abstract string Name { get; }

		public abstract Bitmap Image { get; }

		public abstract string TypeName { get; }

		public abstract bool IsDesignable { get; }
	}

	public class DataSourceDescriptorCollection : CollectionBase
	{
		public DataSourceDescriptorCollection () : base ()
		{
		}

		public int Add (DataSourceDescriptor value) => List.Add (value);

		public int IndexOf (DataSourceDescriptor value) => List.IndexOf (value);

		public void Insert (int index, DataSourceDescriptor value) => List.Insert (index, value);

		public bool Contains (DataSourceDescriptor value) => List.Contains (value);

		public void CopyTo (DataSourceDescriptor [] array, int index) => List.CopyTo (array, index);

		public void Remove (DataSourceDescriptor value) => List.Remove (value);

		public DataSourceDescriptor this [int index] {
			get => List [index] as DataSourceDescriptor;
			set => List [index] = value;
		}
	}

	public abstract class DataSourceGroup
	{
		public abstract string Name { get; }

		public abstract Bitmap Image { get; }

		public abstract DataSourceDescriptorCollection DataSources { get; }

		public abstract bool IsDefault { get; }
	}

	public class DataSourceGroupCollection : CollectionBase
	{
		public DataSourceGroupCollection () : base ()
		{
		}

		public int Add (DataSourceGroup value) => List.Add (value);

		public int IndexOf (DataSourceGroup value) => List.IndexOf (value);

		public void Insert (int index, DataSourceGroup value) => List.Insert (index, value);

		public bool Contains (DataSourceGroup value) => List.Contains (value);

		public void CopyTo (DataSourceGroup [] array, int index) => List.CopyTo (array, index);

		public void Remove (DataSourceGroup value) => List.Remove (value);

		public DataSourceGroup this [int index] {
			get => List [index] as DataSourceGroup;
			set => List [index] = value;
		}
	}

	[Guid ("ABE5C1F0-C96E-40c4-A22D-4A5CEC899BDC")]
	public abstract class DataSourceProviderService
	{
		public abstract bool SupportsAddNewDataSource { get; }

		public abstract bool SupportsConfigureDataSource { get; }

		public abstract DataSourceGroupCollection GetDataSources ();

		public abstract DataSourceGroup InvokeAddNewDataSource (IWin32Window parentWindow, FormStartPosition startPosition);

		public abstract bool InvokeConfigureDataSource (IWin32Window parentWindow, FormStartPosition startPosition, DataSourceDescriptor dataSourceDescriptor);

		public abstract object AddDataSourceInstance (IDesignerHost host, DataSourceDescriptor dataSourceDescriptor);

		public abstract void NotifyDataSourceComponentAdded (object dsc);
	}

	/// <summary>The assembly-qualified name of a type as the project's target framework names it.</summary>
	public interface IMultitargetHelperService
	{
		string GetAssemblyQualifiedName (Type type);
	}

	/// <summary>The framework a type's project targets.</summary>
	[AttributeUsage (AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct)]
	public sealed class ProjectTargetFrameworkAttribute : Attribute
	{
		public ProjectTargetFrameworkAttribute (string targetFrameworkMoniker)
		{
			TargetFrameworkMoniker = targetFrameworkMoniker;
		}

		public string TargetFrameworkMoniker { get; }
	}
}
