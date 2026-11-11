using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WpfWebGpu.Packager.Android;
using WpfWebGpu.Packager.Archives;
using Xunit;

namespace WpfWebGpu.Tests;

// The APK signing block read back as Android's verifier reads it (ApkSignatureSchemeV2Verifier / V3Verifier): the
// layout, each signer's signature over its signed data, and the content digest computed here on its own.
public class ApkSignerTests
{
    private static string Unsigned(out long cd)
    {
        string path = Path.Combine(TestEnvironment.NewDirectory("apk"), "unsigned.apk");
        using var f = File.Create(path);
        var zip = new ZipWriter(f);
        zip.Add("AndroidManifest.xml", new byte[] { 3, 0, 8, 0 }, compress: false);
        var big = new byte[2_500_000];                 // the entries span three 1 MB chunks
        new Random(1).NextBytes(big);
        zip.Add("assets/big.bin", big, compress: false);
        zip.Add("lib/arm64-v8a/libx.so", new byte[] { 1, 2, 3 }, compress: false);
        cd = zip.Finish();
        return path;
    }

    public static TheoryData<string> Keys => new() { "rsa", "ec" };

    [Theory]
    [MemberData(nameof(Keys))]
    public void Signs_with_v2_and_v3_as_Android_verifies_them(string kind)
    {
        string unsigned = Unsigned(out long cd);
        using X509Certificate2 certificate = kind == "rsa" ? Rsa() : Ec();
        string signed = Path.Combine(Path.GetDirectoryName(unsigned)!, "signed.apk");
        ApkSigner.Sign(unsigned, cd, certificate, signed);

        byte[] apk = File.ReadAllBytes(signed);
        int eocd = apk.Length - 22;
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(eocd + 16));
        Assert.Equal("APK Sig Block 42", System.Text.Encoding.ASCII.GetString(apk, (int)cdOffset - 16, 16));
        long size = BinaryPrimitives.ReadInt64LittleEndian(apk.AsSpan((int)cdOffset - 24));
        int blockStart = (int)(cdOffset - size - 8);
        Assert.Equal(size, BinaryPrimitives.ReadInt64LittleEndian(apk.AsSpan(blockStart)));
        Assert.Equal(cd, blockStart);                     // the entries are where they were

        // the id-value pairs
        var pairs = new Dictionary<uint, byte[]>();
        for (int p = blockStart + 8; p < cdOffset - 24;)
        {
            long length = BinaryPrimitives.ReadInt64LittleEndian(apk.AsSpan(p));
            pairs[BinaryPrimitives.ReadUInt32LittleEndian(apk.AsSpan(p + 8))] = apk.AsSpan(p + 12, (int)length - 4).ToArray();
            p += 8 + (int)length;
        }
        Assert.Equal(2, pairs.Count);

        byte[] expected = ContentDigest(apk, blockStart, (int)cdOffset, eocd);
        var v2 = Signer(pairs[0x7109871a], v3: false);
        var v3 = Signer(pairs[0xf05368c0], v3: true);
        foreach (var signer in new[] { v2, v3 })
        {
            Assert.True(Verify(signer.PublicKey, signer.Algorithm, signer.SignedData, signer.Signature), "the signature checks out");
            Assert.Equal(expected, signer.Digest);
            Assert.Equal(certificate.RawData, signer.Certificate);
        }
        Assert.Equal((28u, (uint)int.MaxValue), (v3.MinSdk, v3.MaxSdk));
        // v2 says a v3 signature is there too, so that taking it out is noticed
        Assert.Contains(v2.Attributes, a => a.Id == 0xbeeff00d && BinaryPrimitives.ReadUInt32LittleEndian(a.Value) == 3);
    }

    private sealed record SignerInfo(byte[] SignedData, uint Algorithm, byte[] Digest, byte[] Certificate, List<(uint Id, byte[] Value)> Attributes,
        uint MinSdk, uint MaxSdk, byte[] Signature, byte[] PublicKey);

    private static SignerInfo Signer(byte[] value, bool v3)
    {
        var signers = new Reader(value).Prefixed();
        Assert.True(signers.Left > 0);
        var signer = new Reader(signers.PrefixedBytes());
        Assert.Equal(0, signers.Left);                    // one signer

        byte[] signedData = signer.PrefixedBytes();
        uint min = 0, max = 0;
        if (v3) { min = signer.U32(); max = signer.U32(); }
        var signatures = signer.Prefixed();
        var signature = signatures.Prefixed();
        uint signatureAlgorithm = signature.U32();
        byte[] signatureBytes = signature.PrefixedBytes();
        byte[] publicKey = signer.PrefixedBytes();
        Assert.Equal(0, signer.Left);

        var data = new Reader(signedData);
        var digest = data.Prefixed().Prefixed();
        uint digestAlgorithm = digest.U32();
        byte[] digestBytes = digest.PrefixedBytes();
        Assert.Equal(signatureAlgorithm, digestAlgorithm);
        byte[] certificate = data.Prefixed().PrefixedBytes();
        if (v3)
        {
            Assert.Equal(min, data.U32());               // the range signed is the range given
            Assert.Equal(max, data.U32());
        }
        var attributes = new List<(uint, byte[])>();
        var attributeList = data.Prefixed();
        while (attributeList.Left > 0)
        {
            var a = attributeList.Prefixed();
            attributes.Add((a.U32(), a.Rest()));
        }
        Assert.Equal(0, data.Left);
        return new SignerInfo(signedData, signatureAlgorithm, digestBytes, certificate, attributes, min, max, signatureBytes, publicKey);
    }

    private static bool Verify(byte[] spki, uint algorithm, byte[] data, byte[] signature)
    {
        if (algorithm == 0x0103)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        Assert.Equal(0x0201u, algorithm);
        using var ec = ECDsa.Create();
        ec.ImportSubjectPublicKeyInfo(spki, out _);
        return ec.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    // the entries, the central directory, and its end record pointing where the block starts: 1 MB chunks, each
    // 0xa5 | length | bytes hashed, then 0x5a | count | their hashes
    private static byte[] ContentDigest(byte[] apk, int blockStart, int cdOffset, int eocd)
    {
        byte[] end = apk.AsSpan(eocd).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)blockStart);
        var chunks = new List<byte[]>();
        foreach (byte[] section in new[] { apk.AsSpan(0, blockStart).ToArray(), apk.AsSpan(cdOffset, eocd - cdOffset).ToArray(), end })
            for (int i = 0; i < section.Length; i += 1 << 20)
            {
                int n = Math.Min(1 << 20, section.Length - i);
                var head = new byte[5];
                head[0] = 0xa5;
                BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(1), n);
                chunks.Add(SHA256.HashData([.. head, .. section.AsSpan(i, n)]));
            }
        var top = new byte[5];
        top[0] = 0x5a;
        BinaryPrimitives.WriteInt32LittleEndian(top.AsSpan(1), chunks.Count);
        return SHA256.HashData([.. top, .. chunks.SelectMany(c => c)]);
    }

    private static X509Certificate2 Rsa()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 Ec()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=Test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    // little-endian, length-prefixed (uint32) fields, as the signing block nests them
    private sealed class Reader(byte[] data)
    {
        private int _at;
        public int Left => data.Length - _at;
        public uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_at)); _at += 4; return v; }
        public byte[] PrefixedBytes() { int n = (int)U32(); var b = data.AsSpan(_at, n).ToArray(); _at += n; return b; }
        public Reader Prefixed() => new(PrefixedBytes());
        public byte[] Rest() { var b = data.AsSpan(_at).ToArray(); _at = data.Length; return b; }
    }
}
