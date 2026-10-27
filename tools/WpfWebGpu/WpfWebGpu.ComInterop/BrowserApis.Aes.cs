// AES where .NET's browser build refuses it (Aes.Create throws PlatformNotSupportedException): a managed
// FIPS 197 cipher (Crypto/AesCore.cs) in every mode and padding .NET's Aes offers. app keeps its
// project and configuration secrets with it (IcoSecurityCryptography, IcoWorkbenchDefinitions, the
// camera control), as do SharpZipLib's AES zip entries and Opc.Ua. Anywhere else it is the platform's own.

using System.Security.Cryptography;
using WpfWebGpu.Crypto;

namespace WpfWebGpu.ComInterop;

public static partial class BrowserApis
{
    public static Aes Aes_Create() => OperatingSystem.IsBrowser() ? new ManagedAes() : Aes.Create();

#pragma warning disable SYSLIB0045, IL2026
    // CryptoConfig's names for AES, as .NET maps them; any other name is CryptoConfig's to answer, cast
    // as Aes.Create casts it (null for a name it does not know, InvalidCastException for another cipher's).
    public static Aes? Aes_Create(string algorithmName)
    {
        ArgumentNullException.ThrowIfNull(algorithmName);
        if (!OperatingSystem.IsBrowser()) return Aes.Create(algorithmName);
        return algorithmName is "AES" or "System.Security.Cryptography.Aes" or "AesManaged" or "System.Security.Cryptography.AesManaged"
            or "AesCryptoServiceProvider" or "System.Security.Cryptography.AesCryptoServiceProvider" or "AesCng" or "System.Security.Cryptography.AesCng"
            ? new ManagedAes() : (Aes?)CryptoConfig.CreateFromName(algorithmName);
    }
#pragma warning restore SYSLIB0045, IL2026
}

/// <summary>AES, FIPS 197: ECB, CBC and CFB (8 or 128 bits of feedback), with every padding.</summary>
public sealed class ManagedAes : Aes
{
    public override ICryptoTransform CreateEncryptor(byte[] rgbKey, byte[]? rgbIV) => new BlockModeTransform(Cipher(rgbKey), Mode, Padding, FeedbackSize / 8, rgbIV, encrypt: true);

    public override ICryptoTransform CreateDecryptor(byte[] rgbKey, byte[]? rgbIV) => new BlockModeTransform(Cipher(rgbKey), Mode, Padding, FeedbackSize / 8, rgbIV, encrypt: false);

    public override void GenerateKey() { KeyValue = RandomNumberGenerator.GetBytes(KeySizeValue / 8); }

    public override void GenerateIV() { IVValue = RandomNumberGenerator.GetBytes(16); }

    private static IBlockCipher Cipher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new AesBlock(new AesCore(key));
    }

    protected override bool TryEncryptEcbCore(ReadOnlySpan<byte> plaintext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.ECB, paddingMode, 16, default, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptEcbCore(ReadOnlySpan<byte> ciphertext, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.ECB, paddingMode, 16, default, false, ciphertext, destination, out bytesWritten);

    protected override bool TryEncryptCbcCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.CBC, paddingMode, 16, iv, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptCbcCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.CBC, paddingMode, 16, iv, false, ciphertext, destination, out bytesWritten);

    protected override bool TryEncryptCfbCore(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.CFB, paddingMode, feedbackSizeInBits / 8, iv, true, plaintext, destination, out bytesWritten);

    protected override bool TryDecryptCfbCore(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode, int feedbackSizeInBits, out int bytesWritten)
        => BlockModeTransform.OneShot(Cipher(Key), CipherMode.CFB, paddingMode, feedbackSizeInBits / 8, iv, false, ciphertext, destination, out bytesWritten);

    private sealed class AesBlock(AesCore core) : IBlockCipher
    {
        public int BlockSize => 16;
        public void Encrypt(ReadOnlySpan<byte> input, Span<byte> output) => core.Encrypt(input, output);
        public void Decrypt(ReadOnlySpan<byte> input, Span<byte> output) => core.Decrypt(input, output);
    }
}
