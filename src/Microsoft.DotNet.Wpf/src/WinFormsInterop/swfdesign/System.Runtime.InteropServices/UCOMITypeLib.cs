// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices.ComTypes;

namespace System.Runtime.InteropServices
{
	/// <summary>ITypeLib as the .NET Framework's interop declared it (what AxImporter's resolver is handed).</summary>
	[Guid ("00020402-0000-0000-C000-000000000046")]
	[InterfaceType (ComInterfaceType.InterfaceIsIUnknown)]
	[ComImport]
	public interface UCOMITypeLib
	{
		[PreserveSig]
		int GetTypeInfoCount ();

		void GetTypeInfoType (int index, out TYPEKIND pTKind);

		void GetLibAttr (out IntPtr ppTLibAttr);

		void GetDocumentation (int index, out string strName, out string strDocString, out int dwHelpContext, out string strHelpFile);

		[return: MarshalAs (UnmanagedType.Bool)]
		bool IsName ([MarshalAs (UnmanagedType.LPWStr)] string szNameBuf, int lHashVal);

		[PreserveSig]
		void ReleaseTLibAttr (IntPtr pTLibAttr);
	}
}
