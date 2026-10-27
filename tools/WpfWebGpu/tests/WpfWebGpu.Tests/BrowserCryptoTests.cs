// The managed DES, AES and RSA verification that stand in for the platform's in the browser
// (WpfWebGpu.ComInterop/BrowserApis.Crypto.cs, BrowserApis.Aes.cs), checked against the platform's own here.

using System.Security.Cryptography;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class BrowserCryptoTests
{
    [Fact]
    public void ManagedDes_MatchesTheKnownAnswer()
    {
        // FIPS 46-3's worked example: key 133457799BBCDFF1, block 0123456789ABCDEF
        using var des = new ManagedDes { Mode = CipherMode.ECB, Padding = PaddingMode.None };
        des.Key = Convert.FromHexString("133457799BBCDFF1");
        byte[] c = des.CreateEncryptor().TransformFinalBlock(Convert.FromHexString("0123456789ABCDEF"), 0, 8);
        Assert.Equal("85E813540F0AB405", Convert.ToHexString(c));
    }

    [Theory]
    [InlineData(CipherMode.CBC, PaddingMode.PKCS7)]
    [InlineData(CipherMode.CBC, PaddingMode.Zeros)]
    [InlineData(CipherMode.CBC, PaddingMode.ANSIX923)]
    [InlineData(CipherMode.ECB, PaddingMode.PKCS7)]
    [InlineData(CipherMode.CFB, PaddingMode.PKCS7)]
    public void ManagedDes_EncryptsAndDecryptsAsThePlatform(CipherMode mode, PaddingMode padding)
    {
        var rng = new Random(7);
        foreach (int len in new[] { 0, 1, 7, 8, 9, 23, 64, 1000 })
        {
            byte[] key = new byte[8], iv = new byte[8], data = new byte[len];
            rng.NextBytes(key); rng.NextBytes(iv); rng.NextBytes(data);
            if (padding == PaddingMode.Zeros && len > 0) data[^1] |= 1;     // zero padding cannot tell a trailing zero apart
            using var platform = DES.Create();
            using var managed = new ManagedDes();
            foreach (var d in new SymmetricAlgorithm[] { platform, managed }) { d.Mode = mode; d.Padding = padding; d.Key = key; d.IV = iv; }
            byte[] expected = platform.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
            byte[] got = managed.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(got));
            byte[] back = Decrypt(managed, expected);
            if (padding == PaddingMode.Zeros) back = back[..Math.Min(back.Length, data.Length)];
            Assert.Equal(Convert.ToHexString(data), Convert.ToHexString(back));
        }
    }

    [Fact]
    public void ManagedAes_MatchesFips197sExamples()
    {
        // FIPS 197 appendix C: plaintext 00112233445566778899aabbccddeeff under the key 000102...
        foreach (var (key, expected) in new[]
        {
            ("000102030405060708090a0b0c0d0e0f", "69C4E0D86A7B0430D8CDB78070B4C55A"),
            ("000102030405060708090a0b0c0d0e0f1011121314151617", "DDA97CA4864CDFE06EAF70A0EC0D7191"),
            ("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", "8EA2B7CA516745BFEAFC49904B496089"),
        })
        {
            using var aes = new ManagedAes { Mode = CipherMode.ECB, Padding = PaddingMode.None, Key = Convert.FromHexString(key) };
            byte[] plain = Convert.FromHexString("00112233445566778899aabbccddeeff");
            Assert.Equal(expected, Convert.ToHexString(aes.EncryptEcb(plain, PaddingMode.None)));
            Assert.Equal(Convert.ToHexString(plain), Convert.ToHexString(aes.DecryptEcb(Convert.FromHexString(expected), PaddingMode.None)));
        }
    }

    [Theory]
    [InlineData(CipherMode.CBC, PaddingMode.PKCS7, 0)]
    [InlineData(CipherMode.CBC, PaddingMode.Zeros, 0)]
    [InlineData(CipherMode.CBC, PaddingMode.ANSIX923, 0)]
    [InlineData(CipherMode.CBC, PaddingMode.None, 0)]
    [InlineData(CipherMode.ECB, PaddingMode.PKCS7, 0)]
    [InlineData(CipherMode.CFB, PaddingMode.PKCS7, 8)]
    [InlineData(CipherMode.CFB, PaddingMode.None, 8)]
    [InlineData(CipherMode.CFB, PaddingMode.PKCS7, 128)]
    public void ManagedAes_EncryptsAndDecryptsAsThePlatform(CipherMode mode, PaddingMode padding, int feedback)
    {
        var rng = new Random(11);
        foreach (int keyBits in new[] { 128, 192, 256 })
            foreach (int len in new[] { 0, 1, 15, 16, 17, 31, 64, 1000 })
            {
                if (padding == PaddingMode.None && len % (mode == CipherMode.CFB ? feedback / 8 : 16) != 0) continue;
                byte[] key = new byte[keyBits / 8], iv = new byte[16], data = new byte[len];
                rng.NextBytes(key); rng.NextBytes(iv); rng.NextBytes(data);
                if (padding == PaddingMode.Zeros && len > 0) data[^1] |= 1;
                using var platform = Aes.Create();
                using var managed = new ManagedAes();
                foreach (var a in new Aes[] { platform, managed })
                {
                    a.Mode = mode; a.Padding = padding; a.Key = key; a.IV = iv;
                    if (feedback != 0) a.FeedbackSize = feedback;
                }
                byte[] expected = platform.CreateEncryptor().TransformFinalBlock(data, 0, data.Length);
                Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(managed.CreateEncryptor().TransformFinalBlock(data, 0, data.Length)));
                byte[] back = Decrypt(managed, expected);
                if (padding == PaddingMode.Zeros) back = back[..Math.Min(back.Length, data.Length)];
                Assert.Equal(Convert.ToHexString(data), Convert.ToHexString(back));
                if (mode == CipherMode.CBC)
                    Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(managed.EncryptCbc(data, iv, padding)));
            }
    }

    [Fact]
    public void ManagedAes_RefusesABadPadding()
    {
        using var aes = new ManagedAes();
        byte[] cipher = aes.EncryptCbc(new byte[5], aes.IV);
        cipher[^1] ^= 0x55;
        Assert.Throws<CryptographicException>(() => aes.DecryptCbc(cipher, aes.IV));
    }

    [Fact]
    public void AesCreate_IsThePlatformsOffTheBrowser()
    {
        using Aes aes = BrowserApis.Aes_Create();
        Assert.IsNotType<ManagedAes>(aes);
        Assert.Null(BrowserApis.Aes_Create("NoSuchAlgorithm"));
        Assert.Throws<InvalidCastException>(() => BrowserApis.Aes_Create("DES"));
    }

    // through a CryptoStream, in pieces: TransformBlock holds the padded block back until the end
    private static byte[] Decrypt(SymmetricAlgorithm alg, byte[] cipher)
    {
        using var output = new MemoryStream();
        using (var cs = new CryptoStream(output, alg.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true))
            for (int i = 0; i < cipher.Length; i += 16) cs.Write(cipher, i, Math.Min(16, cipher.Length - i));
        return output.ToArray();
    }

    [Theory]
    [InlineData("SHA1")]
    [InlineData("SHA256")]
    public void ManagedRsa_VerifiesThePlatformsSignatures(string hashName)
    {
        using var signer = RSA.Create(1024);
        var name = new HashAlgorithmName(hashName);
        byte[] data = System.Text.Encoding.UTF8.GetBytes("licence text");
        byte[] hash = hashName == "SHA1" ? SHA1.HashData(data) : SHA256.HashData(data);
        byte[] sig = signer.SignHash(hash, name, RSASignaturePadding.Pkcs1);
        using var managed = new ManagedRsa();
        managed.ImportParameters(signer.ExportParameters(false));
        Assert.True(managed.VerifyHash(hash, sig, name, RSASignaturePadding.Pkcs1));
        Assert.True(BrowserApis.RSACryptoServiceProvider_VerifyHash(managed, hash, hashName, sig));
        sig[5] ^= 1;
        Assert.False(managed.VerifyHash(hash, sig, name, RSASignaturePadding.Pkcs1));
    }
}
