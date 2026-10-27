// The ciphers .NET's browser build refuses (PlatformNotSupportedException), which an app's licence checks
// use: Ab3d.DXEngine decrypts its licence with DES and verifies its signature with RSA (PKCS #1 v1.5,
// RSACryptoServiceProvider.VerifyHash), as do Telerik's and Ab2d's readers. In the browser each is a managed
// implementation here (DES per FIPS 46-3; RSA public-key verification over BigInteger); anywhere else it is
// the platform's own. Calls reach these through BuildRewrite's BrowserSubstitutions.

using System.Numerics;
using System.Security.Cryptography;

namespace WpfWebGpu.ComInterop;

public static partial class BrowserApis
{
    public static DES DES_Create() => OperatingSystem.IsBrowser() ? new ManagedDes() : DES.Create();

    public static object RSACryptoServiceProvider_new() => OperatingSystem.IsBrowser() ? new ManagedRsa() : new RSACryptoServiceProvider();

    public static object RSACryptoServiceProvider_new(CspParameters parameters)
        => OperatingSystem.IsBrowser() ? new ManagedRsa() : new RSACryptoServiceProvider(parameters);

    /// <summary>RSACryptoServiceProvider.VerifyHash(hash, name or OID, signature): PKCS #1 v1.5.</summary>
    public static bool RSACryptoServiceProvider_VerifyHash(object rsa, byte[] rgbHash, string str, byte[] rgbSignature)
    {
        if (rsa is RSACryptoServiceProvider csp) return csp.VerifyHash(rgbHash, str, rgbSignature);
        // no name: what the hash's length says (the browser's CryptoConfig maps no names to OIDs, so an app
        // passing CryptoConfig.MapNameToOID("SHA1") passes nothing)
        HashAlgorithmName name = string.IsNullOrEmpty(str)
            ? rgbHash.Length switch { 16 => HashAlgorithmName.MD5, 32 => HashAlgorithmName.SHA256, 48 => HashAlgorithmName.SHA384, 64 => HashAlgorithmName.SHA512, _ => HashAlgorithmName.SHA1 }
            : ManagedRsa.HashNamed(str);
        return ((RSA)rsa).VerifyHash(rgbHash, rgbSignature, name, RSASignaturePadding.Pkcs1);
    }

    // RSA's own members, called through RSA: an app's object is held in a local of type
    // RSACryptoServiceProvider, a sealed class, and Mono's interpreter calls the member that type declares
    // without looking at the object -- ManagedRsa's ImportParameters was never reached, and VerifyHash
    // then had no key
    public static void RSA_ImportParameters(object rsa, RSAParameters parameters) => ((RSA)rsa).ImportParameters(parameters);

    public static RSAParameters RSA_ExportParameters(object rsa, bool includePrivateParameters) => ((RSA)rsa).ExportParameters(includePrivateParameters);

    public static bool RSA_VerifyHash(object rsa, byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        => ((RSA)rsa).VerifyHash(hash, signature, hashAlgorithm, padding);

    public static bool RSA_VerifyData(object rsa, byte[] data, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
        => ((RSA)rsa).VerifyData(data, signature, hashAlgorithm, padding);

    public static void RSACryptoServiceProvider_ImportParameters(object rsa, RSAParameters parameters) => ((RSA)rsa).ImportParameters(parameters);

    public static RSAParameters RSACryptoServiceProvider_ExportParameters(object rsa, bool includePrivateParameters) => ((RSA)rsa).ExportParameters(includePrivateParameters);

    public static void RSACryptoServiceProvider_set_PersistKeyInCsp(object rsa, bool value)
    {
        if (rsa is RSACryptoServiceProvider csp) csp.PersistKeyInCsp = value;
    }
}

/// <summary>DES, FIPS 46-3, in every mode and padding .NET's DES offers (BlockModeTransform).</summary>
public sealed class ManagedDes : DES
{
    // CFB a byte at a time, as the DES that DES.Create hands out
    public ManagedDes() { FeedbackSizeValue = 8; }

    public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV) => new BlockModeTransform(new DesCipher(rgbKey), Mode, Padding, FeedbackSize / 8, rgbIV, encrypt: true);

    public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV) => new BlockModeTransform(new DesCipher(rgbKey), Mode, Padding, FeedbackSize / 8, rgbIV, encrypt: false);

    public override void GenerateKey() { KeyValue = RandomNumberGenerator.GetBytes(8); }

    public override void GenerateIV() { IVValue = RandomNumberGenerator.GetBytes(8); }

    protected override bool TryEncryptEcbCore(ReadOnlySpan<byte> plaintext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.ECB, paddingMode, 8, default, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptEcbCore(ReadOnlySpan<byte> ciphertext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.ECB, paddingMode, 8, default, false, ciphertext, destination, out bytesWritten);

    protected override bool TryEncryptCbcCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.CBC, paddingMode, 8, iv, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptCbcCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.CBC, paddingMode, 8, iv, false, ciphertext, destination, out bytesWritten);

    protected override bool TryEncryptCfbCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.CFB, paddingMode, feedbackSizeInBits / 8, iv, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptCfbCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
        => BlockModeTransform.OneShot(new DesCipher(Key), CipherMode.CFB, paddingMode, feedbackSizeInBits / 8, iv, false, ciphertext, destination, out bytesWritten);
}

internal sealed class DesCipher : IBlockCipher
{
    private readonly ulong[] _keys = new ulong[16];

    public DesCipher(byte[] key)
    {
        if (key == null || key.Length != 8) throw new CryptographicException("Specified key is not a valid size for this algorithm.");
        Schedule(Be(key, 0));
    }

    public int BlockSize => 8;

    public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output) => Store(Crypt(Be(input), decrypt: false), output);

    public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output) => Store(Crypt(Be(input), decrypt: true), output);

    private static ulong Be(ReadOnlySpan<byte> b) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b);

    private static void Store(ulong v, Span<byte> output) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(output, v);

    private static ulong Be(byte[] b, int at)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[at + i];
        return v;
    }

    // bit k of a table is input bit k counted from 1 at the most significant end, as FIPS 46-3 counts
    private static ulong Permute(ulong input, byte[] table, int inBits)
    {
        ulong r = 0;
        foreach (byte p in table) r = (r << 1) | ((input >> (inBits - p)) & 1);
        return r;
    }

    private void Schedule(ulong key)
    {
        ulong cd = Permute(key, PC1, 64);
        uint c = (uint)(cd >> 28) & 0x0FFFFFFF, d = (uint)cd & 0x0FFFFFFF;
        for (int r = 0; r < 16; r++)
        {
            int s = Shifts[r];
            c = ((c << s) | (c >> (28 - s))) & 0x0FFFFFFF;
            d = ((d << s) | (d >> (28 - s))) & 0x0FFFFFFF;
            _keys[r] = Permute(((ulong)c << 28) | d, PC2, 56);
        }
    }

    private ulong Crypt(ulong block, bool decrypt)
    {
        ulong ip = Permute(block, IP, 64);
        uint l = (uint)(ip >> 32), r = (uint)ip;
        for (int i = 0; i < 16; i++)
        {
            ulong k = _keys[decrypt ? 15 - i : i];
            uint next = l ^ F(r, k);
            l = r;
            r = next;
        }
        return Permute(((ulong)r << 32) | l, FP, 64);
    }

    private static uint F(uint r, ulong k)
    {
        ulong x = Permute(r, E, 32) ^ k;
        uint s = 0;
        for (int i = 0; i < 8; i++)
        {
            int six = (int)(x >> (42 - 6 * i)) & 0x3F;
            int row = ((six & 0x20) >> 4) | (six & 1), col = (six >> 1) & 0xF;
            s = (s << 4) | S[i][row * 16 + col];
        }
        return (uint)Permute(s, P, 32);
    }

    private static readonly byte[] IP = { 58,50,42,34,26,18,10,2, 60,52,44,36,28,20,12,4, 62,54,46,38,30,22,14,6, 64,56,48,40,32,24,16,8, 57,49,41,33,25,17,9,1, 59,51,43,35,27,19,11,3, 61,53,45,37,29,21,13,5, 63,55,47,39,31,23,15,7 };
    private static readonly byte[] FP = { 40,8,48,16,56,24,64,32, 39,7,47,15,55,23,63,31, 38,6,46,14,54,22,62,30, 37,5,45,13,53,21,61,29, 36,4,44,12,52,20,60,28, 35,3,43,11,51,19,59,27, 34,2,42,10,50,18,58,26, 33,1,41,9,49,17,57,25 };
    private static readonly byte[] E = { 32,1,2,3,4,5, 4,5,6,7,8,9, 8,9,10,11,12,13, 12,13,14,15,16,17, 16,17,18,19,20,21, 20,21,22,23,24,25, 24,25,26,27,28,29, 28,29,30,31,32,1 };
    private static readonly byte[] P = { 16,7,20,21,29,12,28,17, 1,15,23,26,5,18,31,10, 2,8,24,14,32,27,3,9, 19,13,30,6,22,11,4,25 };
    private static readonly byte[] PC1 = { 57,49,41,33,25,17,9, 1,58,50,42,34,26,18, 10,2,59,51,43,35,27, 19,11,3,60,52,44,36, 63,55,47,39,31,23,15, 7,62,54,46,38,30,22, 14,6,61,53,45,37,29, 21,13,5,28,20,12,4 };
    private static readonly byte[] PC2 = { 14,17,11,24,1,5, 3,28,15,6,21,10, 23,19,12,4,26,8, 16,7,27,20,13,2, 41,52,31,37,47,55, 30,40,51,45,33,48, 44,49,39,56,34,53, 46,42,50,36,29,32 };
    private static readonly int[] Shifts = { 1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1 };
    private static readonly byte[][] S =
    {
        new byte[] { 14,4,13,1,2,15,11,8,3,10,6,12,5,9,0,7, 0,15,7,4,14,2,13,1,10,6,12,11,9,5,3,8, 4,1,14,8,13,6,2,11,15,12,9,7,3,10,5,0, 15,12,8,2,4,9,1,7,5,11,3,14,10,0,6,13 },
        new byte[] { 15,1,8,14,6,11,3,4,9,7,2,13,12,0,5,10, 3,13,4,7,15,2,8,14,12,0,1,10,6,9,11,5, 0,14,7,11,10,4,13,1,5,8,12,6,9,3,2,15, 13,8,10,1,3,15,4,2,11,6,7,12,0,5,14,9 },
        new byte[] { 10,0,9,14,6,3,15,5,1,13,12,7,11,4,2,8, 13,7,0,9,3,4,6,10,2,8,5,14,12,11,15,1, 13,6,4,9,8,15,3,0,11,1,2,12,5,10,14,7, 1,10,13,0,6,9,8,7,4,15,14,3,11,5,2,12 },
        new byte[] { 7,13,14,3,0,6,9,10,1,2,8,5,11,12,4,15, 13,8,11,5,6,15,0,3,4,7,2,12,1,10,14,9, 10,6,9,0,12,11,7,13,15,1,3,14,5,2,8,4, 3,15,0,6,10,1,13,8,9,4,5,11,12,7,2,14 },
        new byte[] { 2,12,4,1,7,10,11,6,8,5,3,15,13,0,14,9, 14,11,2,12,4,7,13,1,5,0,15,10,3,9,8,6, 4,2,1,11,10,13,7,8,15,9,12,5,6,3,0,14, 11,8,12,7,1,14,2,13,6,15,0,9,10,4,5,3 },
        new byte[] { 12,1,10,15,9,2,6,8,0,13,3,4,14,7,5,11, 10,15,4,2,7,12,9,5,6,1,13,14,0,11,3,8, 9,14,15,5,2,8,12,3,7,0,4,10,1,13,11,6, 4,3,2,12,9,5,15,10,11,14,1,7,6,0,8,13 },
        new byte[] { 4,11,2,14,15,0,8,13,3,12,9,7,5,10,6,1, 13,0,11,7,4,9,1,10,14,3,5,12,2,15,8,6, 1,4,11,13,12,3,7,14,10,15,6,8,0,5,9,2, 6,11,13,8,1,4,10,7,9,5,0,15,14,2,3,12 },
        new byte[] { 13,2,8,4,6,15,11,1,10,9,3,14,5,0,12,7, 1,15,13,8,10,3,7,4,12,5,6,11,0,14,9,2, 7,11,4,1,9,12,14,2,0,6,10,13,15,3,5,8, 2,1,14,7,4,10,8,13,15,12,9,0,3,5,6,11 },
    };
}

/// <summary>RSA with a public key: PKCS #1 v1.5 signature verification (what a licence check does).
/// Signing and decryption need the private key, which a licence check never has.</summary>
public sealed class ManagedRsa : RSA
{
    private byte[]? _modulus, _exponent;

    public ManagedRsa() { LegalKeySizesValue = new[] { new KeySizes(384, 16384, 8) }; KeySizeValue = 1024; }

    public override RSAParameters ExportParameters(bool includePrivateParameters)
    {
        if (includePrivateParameters) throw new CryptographicException("This key has no private part.");
        return new RSAParameters { Modulus = _modulus, Exponent = _exponent };
    }

    public override void ImportParameters(RSAParameters parameters)
    {
        _modulus = parameters.Modulus ?? throw new CryptographicException("The RSA key has no modulus.");
        _exponent = parameters.Exponent ?? throw new CryptographicException("The RSA key has no exponent.");
        KeySizeValue = _modulus.Length * 8;
    }

    internal static HashAlgorithmName HashNamed(string str) => str?.ToUpperInvariant() switch
    {
        "SHA1" or "SHA" or "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "SHA256" or "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "SHA384" or "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "SHA512" or "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "MD5" or "1.2.840.113549.2.5" => HashAlgorithmName.MD5,
        _ => throw new CryptographicException($"Unknown hash algorithm '{str}'."),
    };

    private static byte[] DigestInfo(HashAlgorithmName h) => h.Name switch
    {
        "SHA1" => new byte[] { 0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e, 0x03, 0x02, 0x1a, 0x05, 0x00, 0x04, 0x14 },
        "SHA256" => new byte[] { 0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05, 0x00, 0x04, 0x20 },
        "SHA384" => new byte[] { 0x30, 0x41, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x02, 0x05, 0x00, 0x04, 0x30 },
        "SHA512" => new byte[] { 0x30, 0x51, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x03, 0x05, 0x00, 0x04, 0x40 },
        "MD5" => new byte[] { 0x30, 0x20, 0x30, 0x0c, 0x06, 0x08, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x02, 0x05, 0x05, 0x00, 0x04, 0x10 },
        _ => throw new CryptographicException($"Unknown hash algorithm '{h.Name}'."),
    };

    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (padding != RSASignaturePadding.Pkcs1) throw new CryptographicException("Only PKCS #1 v1.5 signatures are verified.");
        if (_modulus == null || _exponent == null) throw new CryptographicException("No RSA key was imported.");
        int k = _modulus.Length;
        while (k > 0 && _modulus[_modulus.Length - k] == 0) k--;     // a leading zero byte is not part of the length
        if (signature.Length != k) return false;
        var n = new BigInteger(_modulus, isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(signature, isUnsigned: true, isBigEndian: true);
        if (s >= n) return false;
        byte[] m = BigInteger.ModPow(s, new BigInteger(_exponent, isUnsigned: true, isBigEndian: true), n).ToByteArray(isUnsigned: true, isBigEndian: true);
        var em = new byte[k];
        if (m.Length > k) return false;
        Buffer.BlockCopy(m, 0, em, k - m.Length, m.Length);
        byte[] info = DigestInfo(hashAlgorithm);
        int t = info.Length + hash.Length;
        if (k < t + 11) return false;
        var expected = new byte[k];
        expected[0] = 0; expected[1] = 1;
        for (int i = 2; i < k - t - 1; i++) expected[i] = 0xFF;
        expected[k - t - 1] = 0;
        Buffer.BlockCopy(info, 0, expected, k - t, info.Length);
        Buffer.BlockCopy(hash, 0, expected, k - hash.Length, hash.Length);
        return CryptographicOperations.FixedTimeEquals(em, expected);
    }
}
