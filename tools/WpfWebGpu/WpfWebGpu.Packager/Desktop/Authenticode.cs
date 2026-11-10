// Authenticode, written here: a Windows program signed as signtool signs it, on any OS. The file's image hash
// (SHA-256 of the whole file but its checksum, the certificate table's directory entry and the table itself) goes
// into an SpcIndirectDataContent, signed as PKCS #7 SignedData with the certificate's key -- written here as signtool
// lays it out (version 1, the content its SEQUENCE itself, the message digest of that SEQUENCE's contents: not
// SignedCms's, which wraps the content in an OCTET STRING and digests the wrapping), with the opus-info and
// statement-type attributes signtool adds, and an RFC 3161 time stamp when a time-stamp server is given, which keeps
// the signature valid after the certificate expires -- and appended as the file's certificate table (a
// WIN_CERTIFICATE, 8-byte aligned), its directory entry and the header's checksum then set.
//
// Setup.exe is signed after its payload is appended: the signature covers the payload too, and follows it
// (Payload.Find looks before it). A certificate table is found by a 32-bit offset, so a signed file is under 4 GB.

using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.Packager.Desktop;

public static class Authenticode
{
    private const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";
    private const string SpcPeImageDataOid = "1.3.6.1.4.1.311.2.1.15";
    private const string SpcSpOpusInfoOid = "1.3.6.1.4.1.311.2.1.12";
    private const string SpcStatementTypeOid = "1.3.6.1.4.1.311.2.1.11";
    private const string SpcIndividualCodeSigningOid = "1.3.6.1.4.1.311.2.1.21";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";
    private const string Rfc3161CounterSignOid = "1.3.6.1.4.1.311.3.3.1";   // szOID_RFC3161_counterSign
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string ContentTypeOid = "1.2.840.113549.1.9.3";
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string EcdsaSha256Oid = "1.2.840.10045.4.3.2";

    /// <summary>The manifest's certificate (a .pfx; its password WPFPKG_AUTHENTICODE_PASSWORD), or none.</summary>
    public static X509Certificate2? Certificate(PackageManifest manifest)
    {
        string path = manifest.Signing.AuthenticodeCertificate;
        if (path.Length == 0) return null;
        if (!File.Exists(path)) throw new PackagerError($"no such certificate: {path}");
        var cert = X509CertificateLoader.LoadPkcs12FromFile(path, Environment.GetEnvironmentVariable("WPFPKG_AUTHENTICODE_PASSWORD"),
            // the key kept in memory only, where the OS can (macOS cannot: a temporary keychain then)
            OperatingSystem.IsMacOS() ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        if (!cert.HasPrivateKey) throw new PackagerError($"{path} holds no private key");
        return cert;
    }

    /// <summary>Signs <paramref name="path"/> in place (a signature it has is replaced).</summary>
    public static void Sign(string path, X509Certificate2 certificate, string timestampUrl = "")
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        var pe = Layout.Read(file);
        // a signature it has goes; what is left is padded to 8 bytes, as the table is aligned
        long end = pe.CertificateTableOffset > 0 ? pe.CertificateTableOffset : file.Length;
        file.SetLength(end);
        long padded = (end + 7) & ~7L;
        if (padded > end) { file.Position = end; file.Write(new byte[padded - end]); }
        WriteDirectory(file, pe, 0, 0);

        byte[] hash = ImageHash(file, pe, padded);
        byte[] signature = SignHash(hash, certificate, timestampUrl);
        int length = 8 + signature.Length;
        int aligned = (length + 7) & ~7;
        if (padded + aligned > uint.MaxValue) throw new PackagerError($"{Path.GetFileName(path)}: a signed file is under 4 GB, this is {(padded + aligned) >> 20} MB");

        var table = new byte[aligned];
        BinaryPrimitives.WriteInt32LittleEndian(table, length);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4), 0x0200);   // WIN_CERT_REVISION_2_0
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(6), 0x0002);   // WIN_CERT_TYPE_PKCS_SIGNED_DATA
        signature.CopyTo(table, 8);
        file.Position = padded;
        file.Write(table);
        WriteDirectory(file, pe, (uint)padded, (uint)aligned);
        WriteChecksum(file, pe);
    }

    /// <summary>Whether the file's signature, if it has one, is of what the file now is (its image hash).</summary>
    public static bool HasValidHash(string path)
    {
        using var file = File.OpenRead(path);
        var pe = Layout.Read(file);
        if (pe.CertificateTableOffset == 0 || pe.CertificateTableSize <= 8) return false;
        file.Position = pe.CertificateTableOffset + 8;
        var blob = new byte[pe.CertificateTableSize - 8];
        file.ReadExactly(blob);
        byte[]? signed;
        try { signed = SignedDigest(blob); } catch (AsnContentException) { return false; } catch (CryptographicException) { return false; }
        return signed != null && signed.Length == 32 && signed.AsSpan().SequenceEqual(ImageHash(file, pe, pe.CertificateTableOffset));
    }

    // the image hash a signature signs: ContentInfo { signedData, [0] SignedData { version, digests, encapContentInfo
    // { spcIndirectData, [0] SpcIndirectDataContent (or that in an OCTET STRING) } ... } }
    private static byte[]? SignedDigest(byte[] blob)
    {
        var info = new AsnReader(blob, AsnEncodingRules.BER).ReadSequence();
        if (info.ReadObjectIdentifier() != SignedDataOid) return null;
        var signedData = info.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        var encap = signedData.ReadSequence();
        if (encap.ReadObjectIdentifier() != SpcIndirectDataOid) return null;
        var wrapped = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        var indirect = wrapped.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString)
            ? new AsnReader(wrapped.ReadOctetString(), AsnEncodingRules.BER).ReadSequence()
            : wrapped.ReadSequence();
        indirect.ReadSequence();
        var digestInfo = indirect.ReadSequence();
        digestInfo.ReadSequence();
        return digestInfo.ReadOctetString();
    }

    // ---- the image hash ---------------------------------------------------------------------------------

    private static byte[] ImageHash(FileStream file, Layout pe, long end)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Range(long from, long to)
        {
            var buffer = new byte[1 << 20];
            file.Position = from;
            for (long left = to - from; left > 0;)
            {
                int n = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new PackagerError("the file ended early");
                hash.AppendData(buffer, 0, n);
                left -= n;
            }
        }
        Range(0, pe.ChecksumOffset);
        Range(pe.ChecksumOffset + 4, pe.CertificateDirectoryOffset);
        Range(pe.CertificateDirectoryOffset + 8, end);
        return hash.GetHashAndReset();
    }

    // ---- the signature ----------------------------------------------------------------------------------

    private static byte[] SignHash(byte[] hash, X509Certificate2 certificate, string timestampUrl)
    {
        byte[] content = IndirectData(hash);
        // the message digest: of the content's SEQUENCE's contents, without its tag and length (PKCS #7, as Authenticode has it)
        AsnDecoder.ReadEncodedValue(content, AsnEncodingRules.DER, out int contentsOffset, out int contentsLength, out _);
        byte[] digest = SHA256.HashData(content.AsSpan(contentsOffset, contentsLength));

        // the authenticated attributes, a SET OF (DER: sorted), signed as that SET
        var attributes = new List<byte[]>
        {
            Attribute(ContentTypeOid, w => w.WriteObjectIdentifier(SpcIndirectDataOid)),
            Attribute(MessageDigestOid, w => w.WriteOctetString(digest)),
            Attribute(SpcSpOpusInfoOid, w => { using (w.PushSequence()) { } }),
            Attribute(SpcStatementTypeOid, w => { using (w.PushSequence()) w.WriteObjectIdentifier(SpcIndividualCodeSigningOid); }),
        };
        var set = new AsnWriter(AsnEncodingRules.DER);
        using (set.PushSetOf()) foreach (byte[] a in attributes) set.WriteEncodedValue(a);
        byte[] signedAttributes = set.Encode();

        string algorithm;
        byte[] signature;
        if (certificate.GetRSAPrivateKey() is { } rsa)
        {
            using (rsa) signature = rsa.SignData(signedAttributes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            algorithm = RsaOid;
        }
        else if (certificate.GetECDsaPrivateKey() is { } ecdsa)
        {
            using (ecdsa) signature = ecdsa.SignData(signedAttributes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            algorithm = EcdsaSha256Oid;
        }
        else throw new PackagerError($"{certificate.Subject}: an RSA or ECDSA key signs code");
        byte[]? stamp = timestampUrl.Length > 0 ? Timestamp(signature, timestampUrl) : null;

        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
        chain.Build(certificate);
        var certificates = chain.ChainElements.Select(e => e.Certificate.RawData).ToList();
        if (certificates.Count == 0) certificates.Add(certificate.RawData);

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())   // ContentInfo
        {
            w.WriteObjectIdentifier(SignedDataOid);
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (w.PushSequence())   // SignedData
            {
                w.WriteInteger(1);
                using (w.PushSetOf()) DigestAlgorithm(w);
                using (w.PushSequence())
                {
                    w.WriteObjectIdentifier(SpcIndirectDataOid);
                    using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0))) w.WriteEncodedValue(content);
                }
                using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) foreach (byte[] c in certificates) w.WriteEncodedValue(c);
                using (w.PushSetOf())
                using (w.PushSequence())   // SignerInfo
                {
                    w.WriteInteger(1);
                    using (w.PushSequence())
                    {
                        w.WriteEncodedValue(certificate.IssuerName.RawData);
                        w.WriteInteger(certificate.SerialNumberBytes.Span);
                    }
                    DigestAlgorithm(w);
                    using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0))) foreach (byte[] a in attributes) w.WriteEncodedValue(a);
                    using (w.PushSequence())
                    {
                        w.WriteObjectIdentifier(algorithm);
                        if (algorithm == RsaOid) w.WriteNull();
                    }
                    w.WriteOctetString(signature);
                    if (stamp != null)
                        using (w.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 1)))
                            w.WriteEncodedValue(Attribute(Rfc3161CounterSignOid, x => x.WriteEncodedValue(stamp)));
                }
            }
        }
        return w.Encode();
    }

    private static void DigestAlgorithm(AsnWriter w)
    {
        using (w.PushSequence()) { w.WriteObjectIdentifier(Sha256Oid); w.WriteNull(); }
    }

    private static byte[] Attribute(string oid, Action<AsnWriter> value)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(oid);
            using (w.PushSetOf()) value(w);
        }
        return w.Encode();
    }

    // SpcIndirectDataContent ::= SEQUENCE { data SpcAttributeTypeAndOptionalValue (SpcPeImageData), messageDigest DigestInfo }
    private static byte[] IndirectData(byte[] hash) => Sequence(w =>
    {
        using (w.PushSequence())
        {
            w.WriteObjectIdentifier(SpcPeImageDataOid);
            using (w.PushSequence())   // SpcPeImageData { flags BIT STRING, file [0] SpcLink }
            {
                w.WriteBitString(ReadOnlySpan<byte>.Empty);
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))   // [0] EXPLICIT SpcLink: file [2] SpcString
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    w.WriteCharacterString(UniversalTagNumber.BMPString, "<<<Obsolete>>>", new Asn1Tag(TagClass.ContextSpecific, 0));
            }
        }
        using (w.PushSequence())
        {
            using (w.PushSequence()) { w.WriteObjectIdentifier(Sha256Oid); w.WriteNull(); }
            w.WriteOctetString(hash);
        }
    });

    private static byte[] Sequence(Action<AsnWriter> body)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence()) body(w);
        return w.Encode();
    }

    // an RFC 3161 time stamp of the signature (its encrypted digest), the token as the server gives it (a SignedData)
    private static byte[] Timestamp(byte[] signature, string url)
    {
        var request = Rfc3161TimestampRequest.CreateFromData(signature, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var body = new ByteArrayContent(request.Encode());
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-query");
        using var response = http.PostAsync(url, body).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) throw new PackagerError($"{url}: {(int)response.StatusCode} {response.ReasonPhrase}");
        var token = request.ProcessResponse(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult(), out _);
        return token.AsSignedCms().Encode();
    }

    // ---- the PE file ------------------------------------------------------------------------------------

    private sealed record Layout(long ChecksumOffset, long CertificateDirectoryOffset, long CertificateTableOffset, int CertificateTableSize)
    {
        public static Layout Read(FileStream file)
        {
            var head = new byte[4096];
            file.Position = 0;
            int n = file.Read(head);
            if (n < 0x40 || head[0] != 'M' || head[1] != 'Z') throw new PackagerError($"{file.Name}: not a Windows program");
            int pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
            if (pe <= 0 || pe + 24 + 128 > n || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pe)) != 0x00004550)
                throw new PackagerError($"{file.Name}: not a Windows program");
            int optional = pe + 24;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(optional));
            int directories = optional + (magic == 0x20B ? 112 : 96);
            int security = directories + 4 * 8;   // IMAGE_DIRECTORY_ENTRY_SECURITY
            uint tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(security));
            int tableSize = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(security + 4));
            return new Layout(optional + 64, security, tableSize > 0 ? tableOffset : 0, tableSize);
        }
    }

    private static void WriteDirectory(FileStream file, Layout pe, uint offset, uint size)
    {
        var entry = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), size);
        file.Position = pe.CertificateDirectoryOffset;
        file.Write(entry);
    }

    // the header's checksum, as ImageHlp's CheckSumMappedFile computes it: 16-bit one's-complement sum of the file
    // (the checksum itself taken as 0), plus the file's length
    private static void WriteChecksum(FileStream file, Layout pe)
    {
        file.Flush();
        long sum = 0;
        var buffer = new byte[1 << 20];
        file.Position = 0;
        long position = 0;
        int n;
        while ((n = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + 1 < n; i += 2)
            {
                long at = position + i;
                ushort word = at >= pe.ChecksumOffset && at < pe.ChecksumOffset + 4 ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i));
                sum += word;
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            if ((n & 1) != 0) { sum += buffer[n - 1]; sum = (sum & 0xFFFF) + (sum >> 16); }
            position += n;
        }
        sum = (sum & 0xFFFF) + (sum >> 16);
        uint checksum = (uint)(sum + file.Length);
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checksum);
        file.Position = pe.ChecksumOffset;
        file.Write(bytes);
    }
}
