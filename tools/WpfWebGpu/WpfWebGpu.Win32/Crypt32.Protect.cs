// crypt32's DPAPI, CryptProtectData and CryptUnprotectData, for the apps that call it themselves rather
// than through ProtectedData (AWSSDK.Core keeps its credential profiles this way). The same blobs as
// ProtectedData's, from the stack's ProtectedData: the shim's off Windows, so a secret protected one way
// opens the other, as on Windows.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public byte* pbData;
    }

    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;
    private const int ERROR_INVALID_DATA = 13;
    private const int NTE_BAD_KEY_STATE = unchecked((int)0x8009000B);

    // pDataIn, szDataDescr, pOptionalEntropy, pvReserved, pPromptStruct, dwFlags, pDataOut. The
    // description is not kept: ProtectedData, which most callers go through, has none to give.
    [Win32Export("CryptProtectData")]
    public static int CryptProtectData(void* dataIn, char* description, void* entropy, void* reserved, void* prompt, uint flags, void* dataOut)
        => Dpapi(dataIn, null, entropy, flags, dataOut, protect: true);

    [Win32Export("CryptUnprotectData")]
    public static int CryptUnprotectData(void* dataIn, char** description, void* entropy, void* reserved, void* prompt, uint flags, void* dataOut)
        => Dpapi(dataIn, description, entropy, flags, dataOut, protect: false);

    private static int Dpapi(void* dataIn, char** description, void* entropy, uint flags, void* dataOut, bool protect)
    {
        var input = (DATA_BLOB*)dataIn;
        var output = (DATA_BLOB*)dataOut;
        if (input == null || output == null || (input->cbData != 0 && input->pbData == null)) { SetLastError(ERROR_INVALID_PARAMETER); return FALSE; }
        output->cbData = 0;
        output->pbData = null;
        if (description != null) *description = null;
        var data = new ReadOnlySpan<byte>(input->pbData, (int)input->cbData);
        var extra = (DATA_BLOB*)entropy;
        ReadOnlySpan<byte> salt = extra == null || extra->pbData == null ? default : new ReadOnlySpan<byte>(extra->pbData, (int)extra->cbData);
        var scope = (flags & CRYPTPROTECT_LOCAL_MACHINE) != 0 ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
        byte[] result;
        try
        {
            result = protect ? ProtectedData.Protect(data, scope, salt) : ProtectedData.Unprotect(data, scope, salt);
        }
        catch (CryptographicException e)
        {
            SetLastError(e.Message.StartsWith("Key not valid", StringComparison.Ordinal) ? NTE_BAD_KEY_STATE : ERROR_INVALID_DATA);
            return FALSE;
        }
        // LocalAlloc'd, as the caller LocalFrees them
        output->pbData = (byte*)LocalAlloc(0, (nuint)result.Length);
        result.CopyTo(new Span<byte>(output->pbData, result.Length));
        output->cbData = (uint)result.Length;
        CryptographicOperations.ZeroMemory(result);
        if (description != null) *description = (char*)LocalAlloc(0x40, 2);    // ""
        SetLastError(0);
        return TRUE;
    }
}
