// X509Certificate2UI, as the package's Windows build has it: Windows' certificate dialog and certificate picker
// (cryptui's CryptUIDlgViewContext and CryptUIDlgSelectCertificateW). Elsewhere there is no such dialog, and it
// throws as the package's own non-Windows build does. XamlAccessLevel: the data it always was.

using System.Reflection;
using System.Runtime.InteropServices;

namespace System.Security.Cryptography.X509Certificates
{
	public enum X509SelectionFlag
	{
		SingleSelection = 0,
		MultiSelection = 1,
	}

	public sealed class X509Certificate2UI
	{
		public X509Certificate2UI ()
		{
		}

		public static void DisplayCertificate (X509Certificate2 certificate) => DisplayCertificate (certificate, IntPtr.Zero);

		/// <summary>Shows Windows' dialog for <paramref name="certificate"/> over <paramref name="hwndParent"/>.</summary>
		public static void DisplayCertificate (X509Certificate2 certificate, IntPtr hwndParent)
		{
			if (certificate == null)
				throw new ArgumentNullException (nameof (certificate));
			DenyOffWindows ();
			const uint CERT_STORE_CERTIFICATE_CONTEXT = 1;
			if (!CryptUIDlgViewContext (CERT_STORE_CERTIFICATE_CONTEXT, certificate.Handle, hwndParent, null, 0, IntPtr.Zero)) {
				int error = Marshal.GetLastPInvokeError ();
				if (error != 1223)   // ERROR_CANCELLED
					throw new CryptographicException (error);
			}
		}

		public static X509Certificate2Collection SelectFromCollection (X509Certificate2Collection certificates, string title, string message, X509SelectionFlag selectionFlag) =>
			SelectFromCollection (certificates, title, message, selectionFlag, IntPtr.Zero);

		/// <summary>Lets the user pick from <paramref name="certificates"/> in Windows' picker.</summary>
		public static unsafe X509Certificate2Collection SelectFromCollection (X509Certificate2Collection certificates, string title, string message, X509SelectionFlag selectionFlag, IntPtr hwndParent)
		{
			if (certificates == null)
				throw new ArgumentNullException (nameof (certificates));
			if (selectionFlag < X509SelectionFlag.SingleSelection || selectionFlag > X509SelectionFlag.MultiSelection)
				throw new ArgumentException ("The selection flag is not valid.", nameof (selectionFlag));
			DenyOffWindows ();

			const uint CERT_STORE_PROV_MEMORY = 2, CERT_STORE_ADD_ALWAYS = 4, CRYPTUI_SELECTCERT_MULTISELECT = 1;
			IntPtr shown = CertOpenStore ((IntPtr) CERT_STORE_PROV_MEMORY, 0, IntPtr.Zero, 0, IntPtr.Zero);
			IntPtr chosen = CertOpenStore ((IntPtr) CERT_STORE_PROV_MEMORY, 0, IntPtr.Zero, 0, IntPtr.Zero);
			try {
				foreach (X509Certificate2 certificate in certificates)
					CertAddCertificateContextToStore (shown, certificate.Handle, CERT_STORE_ADD_ALWAYS, IntPtr.Zero);
				var request = new CRYPTUI_SELECTCERTIFICATE_STRUCTW {
					dwSize = (uint) sizeof (CRYPTUI_SELECTCERTIFICATE_STRUCTW),
					hwndParent = hwndParent,
					dwFlags = selectionFlag == X509SelectionFlag.MultiSelection ? CRYPTUI_SELECTCERT_MULTISELECT : 0,
					szTitle = title,
					szDisplayString = message,
					cDisplayStores = 1,
					rghDisplayStores = (IntPtr) (&shown),
					hSelectedCertStore = chosen,
				};
				IntPtr single = CryptUIDlgSelectCertificateW (ref request);
				var result = new X509Certificate2Collection ();
				if (single != IntPtr.Zero) {
					result.Add (new X509Certificate2 (single));
					CertFreeCertificateContext (single);
					return result;
				}
				for (IntPtr context = CertEnumCertificatesInStore (chosen, IntPtr.Zero); context != IntPtr.Zero; context = CertEnumCertificatesInStore (chosen, context))
					result.Add (new X509Certificate2 (context));
				return result;
			} finally {
				CertCloseStore (shown, 0);
				CertCloseStore (chosen, 0);
			}
		}

		private static void DenyOffWindows ()
		{
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("X509Certificate2UI is not supported on this platform: it shows Windows' certificate dialogs.");
		}

		[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct CRYPTUI_SELECTCERTIFICATE_STRUCTW
		{
			public uint dwSize;
			public IntPtr hwndParent;
			public uint dwFlags;
			public string szTitle;
			public uint dwDontUseColumn;
			public string szDisplayString;
			public IntPtr pFilterCallback;
			public IntPtr pDisplayCallback;
			public IntPtr pvCallbackData;
			public uint cDisplayStores;
			public IntPtr rghDisplayStores;
			public uint cStores;
			public IntPtr rghStores;
			public uint cPropSheetPages;
			public IntPtr rgPropSheetPages;
			public IntPtr hSelectedCertStore;
		}

		[DllImport ("cryptui", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool CryptUIDlgViewContext (uint contextType, IntPtr context, IntPtr hwnd, string title, uint flags, IntPtr reserved);

		[DllImport ("cryptui", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr CryptUIDlgSelectCertificateW (ref CRYPTUI_SELECTCERTIFICATE_STRUCTW request);

		[DllImport ("crypt32", SetLastError = true)]
		private static extern IntPtr CertOpenStore (IntPtr storeProvider, uint encodingType, IntPtr cryptProvider, uint flags, IntPtr para);

		[DllImport ("crypt32", SetLastError = true)]
		private static extern bool CertAddCertificateContextToStore (IntPtr store, IntPtr context, uint disposition, IntPtr storeContext);

		[DllImport ("crypt32", SetLastError = true)]
		private static extern IntPtr CertEnumCertificatesInStore (IntPtr store, IntPtr previous);

		[DllImport ("crypt32")]
		private static extern bool CertFreeCertificateContext (IntPtr context);

		[DllImport ("crypt32")]
		private static extern bool CertCloseStore (IntPtr store, uint flags);
	}
}

namespace System.Xaml.Permissions
{
	/// <summary>What XAML loaded with partial trust may reach: an assembly's internals, or a type's privates.</summary>
	public class XamlAccessLevel
	{
		private XamlAccessLevel (string assemblyName, string typeName)
		{
			AssemblyNameString = assemblyName;
			PrivateAccessToTypeName = typeName;
		}

		private string AssemblyNameString { get; }

		public AssemblyName AssemblyAccessToAssemblyName => new AssemblyName (AssemblyNameString);

		public string PrivateAccessToTypeName { get; }

		public static XamlAccessLevel AssemblyAccessTo (Assembly assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException (nameof (assembly));
			return new XamlAccessLevel (assembly.FullName, null);
		}

		public static XamlAccessLevel AssemblyAccessTo (AssemblyName assemblyName)
		{
			if (assemblyName == null)
				throw new ArgumentNullException (nameof (assemblyName));
			if (assemblyName.Name == null)
				throw new ArgumentException ("The assembly name must have a name.", nameof (assemblyName));
			return new XamlAccessLevel (assemblyName.FullName, null);
		}

		public static XamlAccessLevel PrivateAccessTo (Type type)
		{
			if (type == null)
				throw new ArgumentNullException (nameof (type));
			return new XamlAccessLevel (type.Assembly.FullName, type.FullName);
		}

		public static XamlAccessLevel PrivateAccessTo (string assemblyQualifiedTypeName)
		{
			if (assemblyQualifiedTypeName == null)
				throw new ArgumentNullException (nameof (assemblyQualifiedTypeName));
			int comma = assemblyQualifiedTypeName.IndexOf (',');
			if (comma < 0)
				throw new ArgumentException ("The type name must be assembly-qualified.", nameof (assemblyQualifiedTypeName));
			string typeName = assemblyQualifiedTypeName.Substring (0, comma).Trim ();
			string assemblyName = assemblyQualifiedTypeName.Substring (comma + 1).Trim ();
			return new XamlAccessLevel (new AssemblyName (assemblyName).FullName, typeName);
		}
	}
}
