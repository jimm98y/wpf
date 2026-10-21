// wintrust and crypt32: Authenticode, verified as Windows verifies it.
//
//   WinVerifyTrust(Ex)        WINTRUST_ACTION_GENERIC_VERIFY_V2 on a file: the PE image's Authenticode
//                             signature -- the image hash as Authenticode defines it (the checksum, the
//                             certificate table's directory entry and the table left out) against the
//                             digest the signature carries, the signer's signature, and the chain to a
//                             trusted root for code signing, at the time of the signature's timestamp
//                             when it has one (as an expired certificate's timestamped signature stays
//                             valid on Windows). Answers 0 or the WinTrust error Windows gives.
//   CryptQueryObject          an embedded PKCS #7 signature (a PE file's) or a PKCS #7 blob, as an
//                             HCRYPTMSG for
//   CryptMsgGetParam          its signers, certificates and content, laid out as Windows lays them out;
//   CryptMsgClose.
//
// ICONICS checks every component's signature this way before it runs it in-process ("custom Point
// Manager ... should not run in-process" for each, with the checks failing for want of these).

using System.Formats.Asn1;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100), TRUST_E_BAD_DIGEST = unchecked((int)0x80096010),
        TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003), TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001),
        TRUST_E_ACTION_UNKNOWN = unchecked((int)0x800B0002), CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109),
        CERT_E_EXPIRED = unchecked((int)0x800B0101), CERT_E_CHAINING = unchecked((int)0x800B010A),
        CERT_E_WRONG_USAGE = unchecked((int)0x800B0110), TRUST_E_FAIL = unchecked((int)0x800B010B);
    private const int CRYPT_E_NO_MATCH = unchecked((int)0x80092009), CRYPT_E_INVALID_MSG_TYPE = unchecked((int)0x80091004),
        CRYPT_E_INVALID_INDEX = unchecked((int)0x80091008), ERROR_MORE_DATA = 234;

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private static readonly Guid WINTRUST_ACTION_GENERIC_CHAIN_VERIFY = new("FC451C16-AC75-11D1-B4B8-00C04FB66EA0");
    private static readonly Guid HTTPSPROV_ACTION = new("573E31F8-AABA-11D0-8CCB-00C04FC295EE");

    // ---- the PE image's signature ------------------------------------------------------------------

    /// <summary>A PE file's Authenticode signature (its certificate table's first PKCS #7 entry) and the
    /// three regions Authenticode leaves out of the image hash; null if the file has none.</summary>
    private sealed class PeSignature
    {
        public required byte[] Pkcs7;
        public int ChecksumOffset, DirectoryOffset, TableOffset, TableSize;
    }

    private static PeSignature? ReadPeSignature(byte[] file)
    {
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z') return null;
        int pe = BitConverter.ToInt32(file, 0x3C);
        if (pe < 0 || pe + 24 > file.Length || BitConverter.ToUInt32(file, pe) != 0x00004550) return null;   // "PE\0\0"
        int optional = pe + 24;
        ushort magic = BitConverter.ToUInt16(file, optional);
        int directories = optional + (magic == 0x20B ? 112 : magic == 0x10B ? 96 : -1);   // PE32+ / PE32
        if (directories < optional) return null;
        int security = directories + 4 * 8;                                               // IMAGE_DIRECTORY_ENTRY_SECURITY
        if (security + 8 > file.Length) return null;
        int table = BitConverter.ToInt32(file, security), size = BitConverter.ToInt32(file, security + 4);
        if (table <= 0 || size < 8 || table + size > file.Length) return null;
        // WIN_CERTIFICATE: dwLength, wRevision, wCertificateType (2 = WIN_CERT_TYPE_PKCS_SIGNED_DATA), data.
        int length = BitConverter.ToInt32(file, table);
        if (BitConverter.ToUInt16(file, table + 6) != 2 || length < 8 || length > size) return null;
        return new PeSignature
        {
            Pkcs7 = file.AsSpan(table + 8, length - 8).ToArray(),
            ChecksumOffset = optional + 64, DirectoryOffset = security, TableOffset = table, TableSize = size,
        };
    }

    /// <summary>The Authenticode image hash: the file without its checksum, its certificate table
    /// entry and the table itself.</summary>
    private static byte[] ImageHash(byte[] file, PeSignature sig, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(file, 0, sig.ChecksumOffset);
        hash.AppendData(file, sig.ChecksumOffset + 4, sig.DirectoryOffset - (sig.ChecksumOffset + 4));
        hash.AppendData(file, sig.DirectoryOffset + 8, sig.TableOffset - (sig.DirectoryOffset + 8));
        int after = sig.TableOffset + sig.TableSize;
        if (after < file.Length) hash.AppendData(file, after, file.Length - after);
        return hash.GetHashAndReset();
    }

    /// <summary>SpcIndirectDataContent's messageDigest: SEQUENCE { data, DigestInfo { algorithm, digest } }.</summary>
    private static (string Oid, byte[] Digest)? IndirectDigest(byte[] content)
    {
        try
        {
            var reader = new AsnReader(content, AsnEncodingRules.BER);
            AsnReader indirect = reader.ReadSequence();
            indirect.ReadEncodedValue();                 // SpcAttributeTypeAndOptionalValue
            AsnReader digestInfo = indirect.ReadSequence();
            AsnReader algorithm = digestInfo.ReadSequence();
            string oid = algorithm.ReadObjectIdentifier();
            return (oid, digestInfo.ReadOctetString());
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    private static HashAlgorithmName? HashFor(string oid) => oid switch
    {
        "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        "1.2.840.113549.2.5" => HashAlgorithmName.MD5,
        _ => null,
    };

    /// <summary>When the signature was made, by its timestamp: an RFC 3161 token (the Microsoft
    /// unsigned attribute) or a PKCS #9 countersignature's signing time; null without one.</summary>
    private static DateTime? TimestampOf(SignerInfo signer)
    {
        foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid?.Value != "1.3.6.1.4.1.311.3.3.1") continue;
            foreach (AsnEncodedData value in attribute.Values)
                if (Rfc3161TimestampToken.TryDecode(value.RawData, out Rfc3161TimestampToken? token, out _) && token != null)
                    return token.TokenInfo.Timestamp.UtcDateTime;
        }
        foreach (SignerInfo counter in signer.CounterSignerInfos)
            foreach (CryptographicAttributeObject attribute in counter.SignedAttributes)
                foreach (AsnEncodedData value in attribute.Values)
                    if (value is Pkcs9SigningTime time) return time.SigningTime.ToUniversalTime();
        return null;
    }

    /// <summary>WinVerifyTrust's answer for a file's Authenticode signature.</summary>
    private static int VerifyAuthenticode(string path)
    {
        byte[] file;
        try { file = File.ReadAllBytes(path); }
        catch (IOException) { return TRUST_E_SUBJECT_FORM_UNKNOWN; }
        catch (UnauthorizedAccessException) { return TRUST_E_SUBJECT_FORM_UNKNOWN; }
        PeSignature? sig = ReadPeSignature(file);
        if (sig == null) return TRUST_E_NOSIGNATURE;
        var cms = new SignedCms();
        try { cms.Decode(sig.Pkcs7); }
        catch (CryptographicException) { return TRUST_E_NOSIGNATURE; }
        if (cms.ContentInfo.ContentType.Value != "1.3.6.1.4.1.311.2.1.4" || cms.SignerInfos.Count == 0) return TRUST_E_NOSIGNATURE;   // SPC_INDIRECT_DATA

        // The image, against the digest the signature carries.
        if (IndirectDigest(cms.ContentInfo.Content) is not var (oid, digest) || HashFor(oid) is not HashAlgorithmName algorithm)
            return TRUST_E_BAD_DIGEST;
        if (!CryptographicOperations.FixedTimeEquals(ImageHash(file, sig, algorithm), digest)) return TRUST_E_BAD_DIGEST;

        // The signer's signature over that content.
        SignerInfo signer = cms.SignerInfos[0];
        try { signer.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { return TRUST_E_BAD_DIGEST; }
        if (signer.Certificate is not X509Certificate2 certificate) return TRUST_E_NOSIGNATURE;

        // The signer's chain, to a trusted root, for code signing.
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));   // code signing
        if (TimestampOf(signer) is DateTime signedAt) chain.ChainPolicy.VerificationTime = signedAt.ToLocalTime();
        if (chain.Build(certificate)) return 0;
        X509ChainStatusFlags status = 0;
        foreach (X509ChainStatus s in chain.ChainStatus) status |= s.Status;
        if ((status & X509ChainStatusFlags.NotTimeValid) != 0) return CERT_E_EXPIRED;
        if ((status & X509ChainStatusFlags.NotValidForUsage) != 0) return CERT_E_WRONG_USAGE;
        if ((status & X509ChainStatusFlags.UntrustedRoot) != 0) return CERT_E_UNTRUSTEDROOT;
        if ((status & X509ChainStatusFlags.PartialChain) != 0) return CERT_E_CHAINING;
        return TRUST_E_FAIL;
    }

    // WINTRUST_DATA (x64): cbStruct, pPolicyCallbackData @8, pSIPClientData @16, dwUIChoice @24,
    // fdwRevocationChecks @28, dwUnionChoice @32, the union @40, dwStateAction @48, hWVTStateData @56.
    // WINTRUST_FILE_INFO: cbStruct, pcwszFilePath @8, hFile @16.
    [Win32Export("WinVerifyTrust", "WinVerifyTrustEx")]
    public static int WinVerifyTrust(nint hwnd, Guid* action, byte* data)
    {
        if (action == null || data == null) return E_INVALIDARG;
        if (*action != WINTRUST_ACTION_GENERIC_VERIFY_V2 && *action != WINTRUST_ACTION_GENERIC_CHAIN_VERIFY && *action != HTTPSPROV_ACTION)
            return TRUST_E_ACTION_UNKNOWN;
        uint stateAction = *(uint*)(data + 48);
        if (stateAction == 2) { *(nint*)(data + 56) = 0; return 0; }   // WTD_STATEACTION_CLOSE: nothing kept
        uint choice = *(uint*)(data + 32);
        if (choice != 1) return TRUST_E_NOSIGNATURE;                    // WTD_CHOICE_FILE only: no catalogs here
        byte* fileInfo = *(byte**)(data + 40);
        if (fileInfo == null) return E_INVALIDARG;
        string? path = ReadEither(*(byte**)(fileInfo + 8));
        if (string.IsNullOrEmpty(path)) return TRUST_E_SUBJECT_FORM_UNKNOWN;
        // Revocation (fdwRevocationChecks) is not checked: there is no CRL cache here to answer from.
        int result = VerifyAuthenticode(path);
        if (stateAction == 1) *(nint*)(data + 56) = 0;                  // WTD_STATEACTION_VERIFY: no state to keep
        LogOnce("wintrust:" + path, $"win32 shim: WinVerifyTrust {Path.GetFileName(path)}: 0x{result:x8}");
        return result;
    }

    // ---- PKCS #7 messages ----------------------------------------------------------------------------

    private const uint CERT_QUERY_OBJECT_FILE = 1, CERT_QUERY_OBJECT_BLOB = 2;
    private const uint CERT_QUERY_CONTENT_PKCS7_SIGNED = 8, CERT_QUERY_CONTENT_PKCS7_SIGNED_EMBED = 10;
    private const uint X509_PKCS7_ENCODING = 0x00010001, CERT_QUERY_FORMAT_BINARY = 1;

    [Win32Export("CryptQueryObject")]
    public static int CryptQueryObject(uint objectType, void* obj, uint contentFlags, uint formatFlags, uint flags,
        uint* encoding, uint* contentType, uint* formatType, nint* certStore, nint* message, void** context)
    {
        if (certStore != null) *certStore = 0;
        if (message != null) *message = 0;
        if (context != null) *context = null;
        byte[]? pkcs7 = null;
        uint found = 0;
        try
        {
            if (objectType == CERT_QUERY_OBJECT_FILE)
            {
                string? path = ReadEither((byte*)obj);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) { SetLastError(2); return FALSE; }   // ERROR_FILE_NOT_FOUND
                byte[] file = File.ReadAllBytes(path);
                if ((contentFlags & (1u << (int)CERT_QUERY_CONTENT_PKCS7_SIGNED_EMBED)) != 0 && ReadPeSignature(file) is { } sig)
                {
                    pkcs7 = sig.Pkcs7;
                    found = CERT_QUERY_CONTENT_PKCS7_SIGNED_EMBED;
                }
                else if ((contentFlags & (1u << (int)CERT_QUERY_CONTENT_PKCS7_SIGNED)) != 0)
                {
                    pkcs7 = file;
                    found = CERT_QUERY_CONTENT_PKCS7_SIGNED;
                }
            }
            else if (objectType == CERT_QUERY_OBJECT_BLOB && obj != null && (contentFlags & (1u << (int)CERT_QUERY_CONTENT_PKCS7_SIGNED)) != 0)
            {
                uint size = *(uint*)obj;                                     // CRYPT_DATA_BLOB: cbData, pbData
                pkcs7 = new ReadOnlySpan<byte>(*(byte**)((byte*)obj + 8), (int)size).ToArray();
                found = CERT_QUERY_CONTENT_PKCS7_SIGNED;
            }
            if (pkcs7 == null) { SetLastError(CRYPT_E_NO_MATCH); return FALSE; }
            var cms = new SignedCms();
            cms.Decode(pkcs7);
            if (encoding != null) *encoding = X509_PKCS7_ENCODING;
            if (contentType != null) *contentType = found;
            if (formatType != null) *formatType = CERT_QUERY_FORMAT_BINARY;
            if (message != null) *message = NewHandle(cms);
            if (certStore != null) LogOnce("crypt32:store", "win32 shim: CryptQueryObject's certificate store is not provided (the message is)");
            return TRUE;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            SetLastError(CRYPT_E_NO_MATCH);
            return FALSE;
        }
    }

    [Win32Export("CryptMsgClose")]
    public static int CryptMsgClose(nint message)
    {
        if (message != 0) s_handles.TryRemove(message, out _);
        return TRUE;
    }

    private const uint CMSG_TYPE_PARAM = 1, CMSG_CONTENT_PARAM = 2, CMSG_SIGNER_COUNT_PARAM = 5, CMSG_SIGNER_INFO_PARAM = 6,
        CMSG_CERT_COUNT_PARAM = 11, CMSG_CERT_PARAM = 12, CMSG_INNER_CONTENT_TYPE_PARAM = 4, CMSG_SIGNED = 2;

    /// <summary>A parameter's bytes, laid out for a caller's buffer at <paramref name="at"/> (pointers
    /// inside a structure point into the same buffer, as Windows returns them); null if there is none.</summary>
    private static byte[]? MessageParam(SignedCms cms, uint type, uint index, nint at)
    {
        switch (type)
        {
            case CMSG_TYPE_PARAM: return BitConverter.GetBytes(CMSG_SIGNED);
            case CMSG_CONTENT_PARAM: return cms.ContentInfo.Content;
            case CMSG_INNER_CONTENT_TYPE_PARAM: return System.Text.Encoding.ASCII.GetBytes(cms.ContentInfo.ContentType.Value + "\0");
            case CMSG_SIGNER_COUNT_PARAM: return BitConverter.GetBytes((uint)cms.SignerInfos.Count);
            case CMSG_CERT_COUNT_PARAM: return BitConverter.GetBytes((uint)cms.Certificates.Count);
            case CMSG_CERT_PARAM: return index < cms.Certificates.Count ? cms.Certificates[(int)index].RawData : null;
            case CMSG_SIGNER_INFO_PARAM: return index < cms.SignerInfos.Count ? SignerInfoBytes(cms.SignerInfos[(int)index], at) : null;
            default: return null;
        }
    }

    // CMSG_SIGNER_INFO (x64): dwVersion @0; Issuer {cbData, pbData} @8; SerialNumber @24; HashAlgorithm
    // {pszObjId, Parameters {cbData, pbData}} @40; HashEncryptionAlgorithm @64; EncryptedHash @88;
    // AuthAttrs {cAttr, rgAttr} @104; UnauthAttrs @120. 136 bytes, then the data the pointers name. The
    // serial number is little-endian, as CryptoAPI keeps integers.
    private static byte[] SignerInfoBytes(SignerInfo signer, nint at)
    {
        X509Certificate2? cert = signer.Certificate;
        byte[] issuer = cert?.IssuerName.RawData ?? Array.Empty<byte>();
        string hex = cert?.SerialNumber ?? "";   // the signer's certificate is the one SignerIdentifier names
        byte[] serial = Convert.FromHexString(hex);
        Array.Reverse(serial);   // big-endian hex to CryptoAPI's little-endian
        byte[] hashOid = System.Text.Encoding.ASCII.GetBytes((signer.DigestAlgorithm.Value ?? "") + "\0");
        byte[] encOid = System.Text.Encoding.ASCII.GetBytes((signer.SignatureAlgorithm.Value ?? "") + "\0");
        byte[] encrypted = signer.GetSignature();
        const int header = 136;
        var data = new byte[header + issuer.Length + serial.Length + hashOid.Length + encOid.Length + encrypted.Length];
        int offset = header;
        long Place(byte[] bytes)
        {
            bytes.CopyTo(data, offset);
            long p = at == 0 ? 0 : (long)at + offset;
            offset += bytes.Length;
            return p;
        }
        void Blob(int field, byte[] bytes) { BitConverter.GetBytes((uint)bytes.Length).CopyTo(data, field); BitConverter.GetBytes(Place(bytes)).CopyTo(data, field + 8); }
        BitConverter.GetBytes((uint)signer.Version).CopyTo(data, 0);
        Blob(8, issuer);
        Blob(24, serial);
        BitConverter.GetBytes(Place(hashOid)).CopyTo(data, 40);
        BitConverter.GetBytes(Place(encOid)).CopyTo(data, 64);
        Blob(88, encrypted);
        // Attributes: none listed (counts 0), the parameters empty.
        return data;
    }

    [Win32Export("CryptMsgGetParam")]
    public static int CryptMsgGetParam(nint message, uint type, uint index, byte* data, uint* size)
    {
        if (size == null) { SetLastError(E_INVALIDARG); return FALSE; }
        if (Handle(message) is not SignedCms cms) { SetLastError(E_INVALIDARG); return FALSE; }
        byte[]? bytes = MessageParam(cms, type, index, (nint)data);
        if (bytes == null)
        {
            SetLastError(type is CMSG_CERT_PARAM or CMSG_SIGNER_INFO_PARAM ? CRYPT_E_INVALID_INDEX : CRYPT_E_INVALID_MSG_TYPE);
            return FALSE;
        }
        if (data == null) { *size = (uint)bytes.Length; return TRUE; }
        if (*size < bytes.Length) { *size = (uint)bytes.Length; SetLastError(ERROR_MORE_DATA); return FALSE; }
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        *size = (uint)bytes.Length;
        return TRUE;
    }
}
