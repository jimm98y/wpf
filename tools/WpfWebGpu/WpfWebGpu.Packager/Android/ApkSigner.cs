// APK Signature Scheme v2 (Android 7+; the Packager's APKs need Android 8): the whole file signed, in a block
// between the zip entries and the central directory. What the platform's apksigner writes, in managed code:
//
//   digest  = SHA-256 over 1 MB chunks of the entries, the central directory and its end record (whose
//             directory offset is taken as where the block starts), each chunk 0xa5 | length | bytes, and
//             the chunks' digests 0x5a | count | digests
//   signer  = signed data (the digest, the certificate), its signature, the public key
//   block   = size | (id 0x7109871a, the signers) | size | "APK Sig Block 42"
//
// The key: the user's (a PKCS#12 file, as keytool writes them), or a debug key the Packager makes once and
// keeps, so that each build of an app installs over the one before.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.Packager.Android;

public static class ApkSigner
{
    private const uint V2BlockId = 0x7109871a;
    private const uint RsaPkcs1Sha256 = 0x0103, EcdsaSha256 = 0x0201;
    private const int Chunk = 1 << 20;

    /// <summary>Signs the unsigned APK at <paramref name="unsigned"/> (entries, central directory at
    /// <paramref name="cdOffset"/>, end record) into <paramref name="output"/>.</summary>
    public static void Sign(string unsigned, long cdOffset, X509Certificate2 certificate, string output)
    {
        using var input = File.OpenRead(unsigned);
        long length = input.Length;
        var eocd = new byte[22];
        input.Position = length - 22;
        input.ReadExactly(eocd);
        if (BinaryPrimitives.ReadUInt32LittleEndian(eocd) != 0x06054b50) throw new PackagerError("the APK has a zip comment, which signing does not expect");

        byte[] digest = Digest(input, cdOffset, length - 22, eocd);

        using RSA? rsa = certificate.GetRSAPrivateKey();
        using ECDsa? ec = rsa == null ? certificate.GetECDsaPrivateKey() : null;
        if (rsa == null && ec == null) throw new PackagerError("the signing certificate has no RSA or EC private key");
        uint algorithm = rsa != null ? RsaPkcs1Sha256 : EcdsaSha256;

        // signed data: digests, certificates, additional attributes
        byte[] signedData = Concat(
            LengthPrefixed(LengthPrefixed(Concat(U32(algorithm), LengthPrefixed(digest)))),
            LengthPrefixed(LengthPrefixed(certificate.RawData)),
            LengthPrefixed(Array.Empty<byte>()));
        byte[] signature = rsa != null
            ? rsa.SignData(signedData, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : ec!.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        byte[] publicKey = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        byte[] signer = Concat(
            LengthPrefixed(signedData),
            LengthPrefixed(LengthPrefixed(Concat(U32(algorithm), LengthPrefixed(signature)))),
            LengthPrefixed(publicKey));
        byte[] value = LengthPrefixed(LengthPrefixed(signer));

        // the block: its size (not counting this field), the pair, its size again, the magic
        byte[] pair = Concat(U64((ulong)(4 + value.Length)), U32(V2BlockId), value);
        long blockSize = pair.Length + 8 + 16;
        byte[] block = Concat(U64((ulong)blockSize), pair, U64((ulong)blockSize), "APK Sig Block 42"u8.ToArray());

        using var output_ = File.Create(output);
        input.Position = 0;
        Archives.ZipReader.CopyBytes(input, output_, cdOffset);
        output_.Write(block);
        Archives.ZipReader.CopyBytes(input, output_, length - 22 - cdOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)(cdOffset + block.Length));
        output_.Write(eocd);
    }

    private static byte[] Digest(FileStream input, long cdOffset, long eocdOffset, byte[] eocd)
    {
        var digests = new List<byte[]>();
        var buffer = new byte[Chunk];
        void Section(long start, long end)
        {
            input.Position = start;
            for (long at = start; at < end; at += Chunk)
            {
                int n = (int)Math.Min(Chunk, end - at);
                input.ReadExactly(buffer, 0, n);
                digests.Add(ChunkDigest(buffer.AsSpan(0, n)));
            }
        }
        Section(0, cdOffset);
        Section(cdOffset, eocdOffset);
        // the end record with its directory offset as the block's start: where the directory is now
        var end = (byte[])eocd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)cdOffset);
        digests.Add(ChunkDigest(end));

        using var top = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> head = stackalloc byte[5];
        head[0] = 0x5a;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], digests.Count);
        top.AppendData(head);
        foreach (var d in digests) top.AppendData(d);
        return top.GetHashAndReset();
    }

    private static byte[] ChunkDigest(ReadOnlySpan<byte> chunk)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> head = stackalloc byte[5];
        head[0] = 0xa5;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], chunk.Length);
        h.AppendData(head);
        h.AppendData(chunk);
        return h.GetHashAndReset();
    }

    /// <summary>The key a build is signed with: the given PKCS#12 file (its password from
    /// WPFPKG_ANDROID_KEYSTORE_PASSWORD), or the Packager's own debug key.</summary>
    public static X509Certificate2 LoadKey(string? keystore, string? alias)
    {
        if (string.IsNullOrEmpty(keystore)) return DebugKey();
        if (!File.Exists(keystore)) throw new PackagerError($"no such keystore: {keystore}");
        string? password = Environment.GetEnvironmentVariable("WPFPKG_ANDROID_KEYSTORE_PASSWORD");
        var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(keystore, password, X509KeyStorageFlags.Exportable);
        var withKey = all.Where(c => c.HasPrivateKey).ToList();
        if (withKey.Count == 0) throw new PackagerError($"{keystore} holds no key (a JKS keystore? convert it: keytool -importkeystore -deststoretype pkcs12)");
        return (string.IsNullOrEmpty(alias) ? null : withKey.FirstOrDefault(c => c.FriendlyName == alias)) ?? withKey[0];
    }

    private static X509Certificate2 DebugKey()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WpfWebGpu", "packager", "android-debug.p12");
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Android Debug, O=Android, C=US", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    private static byte[] LengthPrefixed(byte[] data) => Concat(U32((uint)data.Length), data);
    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}
