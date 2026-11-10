// Windows: DPAPI itself (crypt32's CryptProtectData and CryptUnprotectData), as dotnet/runtime's ProtectedData calls
// it -- the UI forbidden, the machine's key for LocalMachine -- so a blob is Windows' own, opened by any program of
// the user (or the machine) as stock .NET's are, and Windows' errors are its CryptographicExceptions.

using System.Runtime.InteropServices;

namespace System.Security.Cryptography
{
    internal static unsafe class WindowsDpapi
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public byte* pbData;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptProtectData(DATA_BLOB* dataIn, string description, DATA_BLOB* entropy, IntPtr reserved, IntPtr prompt, int flags, DATA_BLOB* dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CryptUnprotectData(DATA_BLOB* dataIn, IntPtr description, DATA_BLOB* entropy, IntPtr reserved, IntPtr prompt, int flags, DATA_BLOB* dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static byte[] Protect(ReadOnlySpan<byte> data, DataProtectionScope scope, ReadOnlySpan<byte> entropy) => Call(data, scope, entropy, protect: true);

        public static byte[] Unprotect(ReadOnlySpan<byte> data, DataProtectionScope scope, ReadOnlySpan<byte> entropy) => Call(data, scope, entropy, protect: false);

        private static byte[] Call(ReadOnlySpan<byte> data, DataProtectionScope scope, ReadOnlySpan<byte> entropy, bool protect)
        {
            int flags = CRYPTPROTECT_UI_FORBIDDEN | (scope == DataProtectionScope.LocalMachine ? CRYPTPROTECT_LOCAL_MACHINE : 0);
            // an empty input is a valid one: DPAPI is given a pointer all the same
            byte none = 0;
            fixed (byte* d = data)
            fixed (byte* e = entropy)
            {
                var input = new DATA_BLOB { cbData = data.Length, pbData = d != null ? d : &none };
                var salt = new DATA_BLOB { cbData = entropy.Length, pbData = e };
                var output = default(DATA_BLOB);
                bool ok = protect
                    ? CryptProtectData(&input, "", entropy.Length > 0 ? &salt : null, IntPtr.Zero, IntPtr.Zero, flags, &output)
                    : CryptUnprotectData(&input, IntPtr.Zero, entropy.Length > 0 ? &salt : null, IntPtr.Zero, IntPtr.Zero, flags, &output);
                if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
                try
                {
                    return new ReadOnlySpan<byte>(output.pbData, output.cbData).ToArray();
                }
                finally
                {
                    // what was decrypted is cleared before its memory is given back
                    if (!protect && output.pbData != null) new Span<byte>(output.pbData, output.cbData).Clear();
                    if (output.pbData != null) LocalFree((IntPtr)output.pbData);
                }
            }
        }
    }
}
