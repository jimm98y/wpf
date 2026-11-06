// crypt32's DPAPI where there is no crypt32: CryptProtectData and CryptUnprotectData on the ProtectedData
// stand-in, the DPAPI this stack has on every system (its blobs are Windows' DPAPI blobs, its keys the machine's),
// so ProtectKeysWithDpapi protects a key ring here as it does on Windows. The calls are crypt32's own: the blob
// out allocated as LocalAlloc allocates (what the caller frees with Marshal.FreeHGlobal), CRYPTPROTECT_LOCAL_MACHINE
// the machine's scope, and a failure the last error, as CryptographicException(GetLastWin32Error()) reads it.

using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Microsoft.AspNetCore.Cryptography;

internal static unsafe partial class UnsafeNativeMethods
{
    private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    /// <summary>Whether DPAPI is to hand: crypt32's on Windows, the stand-in's elsewhere.</summary>
    internal static bool HasCrypt32 => true;

    internal static bool CryptProtectData(DATA_BLOB* pDataIn, IntPtr szDataDescr, DATA_BLOB* pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut)
    {
        if (OperatingSystem.IsWindows()) return CryptProtectDataNative(pDataIn, szDataDescr, pOptionalEntropy, pvReserved, pPromptStruct, dwFlags, pDataOut);
        return Dpapi(pDataIn, pOptionalEntropy, pDataOut, (data, entropy) =>
            ProtectedData.Protect(data, entropy, (dwFlags & CRYPTPROTECT_LOCAL_MACHINE) != 0 ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser));
    }

    internal static bool CryptUnprotectData(DATA_BLOB* pDataIn, IntPtr ppszDataDescr, DATA_BLOB* pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, uint dwFlags, DATA_BLOB* pDataOut)
    {
        if (OperatingSystem.IsWindows()) return CryptUnprotectDataNative(pDataIn, ppszDataDescr, pOptionalEntropy, pvReserved, pPromptStruct, dwFlags, pDataOut);
        return Dpapi(pDataIn, pOptionalEntropy, pDataOut, (data, entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser));
    }

    private static bool Dpapi(DATA_BLOB* pDataIn, DATA_BLOB* pOptionalEntropy, DATA_BLOB* pDataOut, Func<byte[], byte[]?, byte[]> transform)
    {
        const int ERROR_INVALID_PARAMETER = 87;
        if (pDataIn == null || pDataOut == null)
        {
            Marshal.SetLastPInvokeError(ERROR_INVALID_PARAMETER);
            return false;
        }
        byte[] data = new ReadOnlySpan<byte>(pDataIn->pbData, checked((int)pDataIn->cbData)).ToArray();
        byte[]? entropy = pOptionalEntropy == null || pOptionalEntropy->cbData == 0 ? null
            : new ReadOnlySpan<byte>(pOptionalEntropy->pbData, checked((int)pOptionalEntropy->cbData)).ToArray();
        byte[] result;
        try
        {
            result = transform(data, entropy);
        }
        catch (CryptographicException e)
        {
            // an HRESULT_FROM_WIN32 the Win32 error it carries, as crypt32 reports it
            Marshal.SetLastPInvokeError((e.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? e.HResult & 0xFFFF : e.HResult);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
        byte* to = (byte*)Marshal.AllocHGlobal(Math.Max(result.Length, 1));
        result.CopyTo(new Span<byte>(to, result.Length));
        CryptographicOperations.ZeroMemory(result);
        pDataOut->pbData = to;
        pDataOut->cbData = (uint)result.Length;
        Marshal.SetLastPInvokeError(0);
        return true;
    }
}
