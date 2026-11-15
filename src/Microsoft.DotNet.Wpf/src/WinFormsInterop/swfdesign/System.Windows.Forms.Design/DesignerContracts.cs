// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The rest of .NET's System.Windows.Forms.Design contracts: the component-actions kinds, the themed-scrollbar
// contract a host's windows answer, and AxImporter -- which .NET keeps only for its signature (its constructor
// throws, by design: ActiveX wrappers are made by the SDK's tools, not at design time).

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;

namespace System.Windows.Forms.Design
{
	public enum ComponentActionsType
	{
		All,
		Component,
		Service,
	}

	public enum ThemedScrollbarMode
	{
		All = 1,
		None = 2,
		OnlyTopLevel = 3,
	}

	public struct ThemedScrollbarWindow
	{
		public IntPtr Handle;
		public ThemedScrollbarMode Mode;
	}

	public interface IContainsThemedScrollbarWindows
	{
		IEnumerable ThemedScrollbarWindows ();
	}

	public partial class AxImporter
	{
		public AxImporter (Options options)
		{
			throw new NotImplementedException ("This method/object is not implemented by design.");
		}

		public sealed class Options
		{
			public bool delaySign;
			public bool genSources;
			public bool ignoreRegisteredOcx;
			public string keyContainer;
			public string keyFile;
#pragma warning disable SYSLIB0017
			public StrongNameKeyPair keyPair;
#pragma warning restore SYSLIB0017
			public bool msBuildErrors;
			public bool noLogo;
			public string outputDirectory;
			public string outputName;
			public bool overwriteRCW;
			public byte [] publicKey;
			public IReferenceResolver references;
			public bool silentMode;
			public bool verboseMode;
		}

		public interface IReferenceResolver
		{
			string ResolveManagedReference (string assemName);

			string ResolveComReference (UCOMITypeLib typeLib);

			string ResolveComReference (AssemblyName name);

			string ResolveActiveXReference (UCOMITypeLib typeLib);
		}
	}
}
