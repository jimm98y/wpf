// Apple's code signature, written in managed code on any machine -- what codesign writes, so that an iOS, visionOS or
// macOS app is signed where the Packager runs, Windows and Linux included.
//
// A thin 64-bit Mach-O's signature is a SuperBlob at the end of its __LINKEDIT, which LC_CODE_SIGNATURE points at:
//
//   CodeDirectory (slot 0)        the identifier, the flags, a SHA-256 of every 16 KB page of the file up to the
//                                 signature, and the special slots: Info.plist (-1), the requirements (-2), the
//                                 bundle's resource seal (-3), the entitlements as XML (-5) and as DER (-7)
//   requirements (2)              an empty set: the designated requirement is then the one the signature implies
//                                 (codesign's own for an ad hoc signature; for a certificate's, what codesign
//                                 --verify and the OS derive from the certificate)
//   entitlements (5, 7)           the XML plist, and the same in Apple's DER form, which iOS 15 and later read
//   CMS (0x10000)                 empty for an ad hoc signature; with a certificate, a detached CMS SignedData over
//                                 the CodeDirectory, with Apple's attributes naming its hash (1.2.840.113635.100.9.1,
//                                 a plist of cdhashes; .9.2, the hash by algorithm) and the certificate chain
//
// All of it big-endian; the page hashes cover the load commands, so the signature's size is settled first and
// written into LC_CODE_SIGNATURE and __LINKEDIT before anything is hashed.

using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WpfWebGpu.Packager.Apple;

public static class MachOSigner
{
    public sealed class Options
    {
        /// <summary>The identifier: a bundle's id for its executable, a library's name for a library.</summary>
        public string Identifier { get; init; } = "";
        /// <summary>The bundle's Info.plist, as it is on disk (its hash is sealed in); null for a library.</summary>
        public byte[]? InfoPlist { get; init; }
        /// <summary>The bundle's _CodeSignature/CodeResources, as written; null where there is none.</summary>
        public byte[]? CodeResources { get; init; }
        /// <summary>The entitlements; null for none.</summary>
        public PlistDict? Entitlements { get; init; }
        /// <summary>The bundle's executable (exec segment flag MAIN_BINARY), not a library.</summary>
        public bool MainExecutable { get; init; }
        /// <summary>The hardened runtime (macOS, for notarization).</summary>
        public bool HardenedRuntime { get; init; }
    }

    private const uint LcCodeSignature = 0x1d, LcSegment64 = 0x19;
    private const uint MagicSuperBlob = 0xfade0cc0, MagicCodeDirectory = 0xfade0c02, MagicRequirements = 0xfade0c01,
        MagicEntitlements = 0xfade7171, MagicDerEntitlements = 0xfade7172, MagicBlobWrapper = 0xfade0b01;
    private const int PageShift = 14, PageSize = 1 << PageShift, HashSize = 32;
    private const uint CsAdhoc = 0x2, CsRuntime = 0x10000;

    /// <summary>A signing certificate with its key, the certificates above it, and the Apple team it names.</summary>
    public sealed class Identity
    {
        public required X509Certificate2 Certificate { get; init; }
        /// <summary>The certificates from the issuer up, the root last (included in the signature).</summary>
        public X509Certificate2Collection Chain { get; init; } = new();
        /// <summary>The team identifier (an Apple-issued certificate's organizational unit); null for others.</summary>
        public string? TeamId { get; init; }

        /// <summary>The identity in a .p12, its chain completed from the issuers the certificate names (its AIA).</summary>
        public static Identity Load(string p12, string? password)
        {
            if (!File.Exists(p12)) throw new PackagerError($"no such certificate: {p12}");
            var all = X509CertificateLoader.LoadPkcs12CollectionFromFile(p12, password,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.Exportable : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            X509Certificate2 leaf = all.FirstOrDefault(c => c.HasPrivateKey) ?? throw new PackagerError($"{p12} holds no private key");
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllFlags;
            chain.ChainPolicy.ExtraStore.AddRange(all);
            chain.Build(leaf);
            var above = new X509Certificate2Collection();
            foreach (X509ChainElement e in chain.ChainElements.Skip(1)) above.Add(e.Certificate);
            bool apple = chain.ChainElements.Count > 1 && chain.ChainElements[^1].Certificate.Subject.Contains("O=Apple Inc.", StringComparison.Ordinal);
            string? ou = leaf.SubjectName.EnumerateRelativeDistinguishedNames().Where(r => r.GetSingleElementType().Value == "2.5.4.11")
                .Select(r => r.GetSingleElementValue()).FirstOrDefault();
            return new Identity { Certificate = leaf, Chain = above, TeamId = apple ? ou : null };
        }
    }

    /// <summary>Signs the thin 64-bit Mach-O at <paramref name="path"/> ad hoc, replacing any signature it has.</summary>
    public static void SignAdHoc(string path, Options options) => Sign(path, options, null);

    /// <summary>Signs the thin 64-bit Mach-O at <paramref name="path"/> with <paramref name="identity"/>'s certificate
    /// (ad hoc where it is null), replacing any signature it has.</summary>
    public static void Sign(string path, Options options, Identity? identity)
    {
        byte[] file = File.ReadAllBytes(path);
        if (BinaryPrimitives.ReadUInt32LittleEndian(file) != 0xfeedfacf)
            throw new PackagerError($"{Path.GetFileName(path)}: not a thin 64-bit Mach-O (a universal one is signed slice by slice)");

        var macho = Layout.Read(file);
        // what the signature covers: the file up to it, 16-byte aligned (the old signature dropped)
        int codeLimit = macho.SignatureOffset > 0 ? macho.SignatureOffset : (file.Length + 15) & ~15;

        byte[] requirements = Blob(MagicRequirements, BigEndian(0u));                            // an empty set
        byte[]? entitlements = options.Entitlements == null ? null : Blob(MagicEntitlements, Plist.WriteXml(options.Entitlements));
        byte[]? der = options.Entitlements == null || !options.MainExecutable ? null : Blob(MagicDerEntitlements, DerEntitlements(options.Entitlements));

        int nSpecial = der != null ? 7 : entitlements != null ? 5 : options.CodeResources != null ? 3 : 2;
        int nCode = (codeLimit + PageSize - 1) / PageSize;
        byte[] ident = Encoding.UTF8.GetBytes(options.Identifier + "\0");
        byte[] team = identity?.TeamId is { Length: > 0 } t ? Encoding.UTF8.GetBytes(t + "\0") : Array.Empty<byte>();
        const int cdHeader = 88;                                                                 // version 0x20400
        int hashOffset = cdHeader + ident.Length + team.Length + nSpecial * HashSize;
        int cdLength = hashOffset + nCode * HashSize;

        // the CMS's room is settled before the pages are hashed (its size is in the load commands they cover): a
        // signature over a CodeDirectory of the same length, and some to spare (a signing time, an ECDSA signature's
        // length vary by a few bytes)
        int cmsRoom = identity == null ? 8 : Blob(MagicBlobWrapper, Cms(new byte[cdLength], identity)).Length + 64;

        var blobs = new List<(uint Slot, int Length)> { (0, cdLength), (2, requirements.Length) };
        if (entitlements != null) blobs.Add((5, entitlements.Length));
        if (der != null) blobs.Add((7, der.Length));
        blobs.Add((0x10000, cmsRoom));
        int superLength = 12 + blobs.Count * 8 + blobs.Sum(b => b.Length);
        int dataSize = (superLength + 15) & ~15;

        // the file as it will be: content, then the signature; its load commands say so before they are hashed
        var output = new byte[codeLimit + dataSize];
        Buffer.BlockCopy(file, 0, output, 0, Math.Min(file.Length, codeLimit));
        macho.SetSignature(output, codeLimit, dataSize);

        // special slots, by index (-1 .. -nSpecial)
        var special = new byte[nSpecial][];
        special[0] = options.InfoPlist == null ? new byte[HashSize] : SHA256.HashData(options.InfoPlist);
        special[1] = SHA256.HashData(requirements);
        if (nSpecial >= 3) special[2] = options.CodeResources == null ? new byte[HashSize] : SHA256.HashData(options.CodeResources);
        if (nSpecial >= 4) special[3] = new byte[HashSize];
        if (nSpecial >= 5) special[4] = entitlements == null ? new byte[HashSize] : SHA256.HashData(entitlements);
        if (nSpecial >= 6) special[5] = new byte[HashSize];
        if (nSpecial >= 7) special[6] = der == null ? new byte[HashSize] : SHA256.HashData(der);

        var cd = new byte[cdLength];
        W32(cd, 0, MagicCodeDirectory);
        W32(cd, 4, (uint)cdLength);
        W32(cd, 8, 0x20400);
        W32(cd, 12, (identity == null ? CsAdhoc : 0) | (options.HardenedRuntime ? CsRuntime : 0));
        W32(cd, 16, (uint)hashOffset);
        W32(cd, 20, cdHeader);                       // identOffset
        W32(cd, 24, (uint)nSpecial);
        W32(cd, 28, (uint)nCode);
        W32(cd, 32, (uint)codeLimit);
        cd[36] = HashSize;
        cd[37] = 2;                                  // SHA-256
        cd[38] = 0;                                  // platform
        cd[39] = PageShift;
        // spare2 (40) and scatter (44) stay 0; the team's name after the identifier; spare3 (52), codeLimit64 (56) 0
        if (team.Length > 0) W32(cd, 48, (uint)(cdHeader + ident.Length));
        W64(cd, 64, macho.TextFileOffset);           // execSegBase
        W64(cd, 72, macho.TextFileSize);             // execSegLimit
        W64(cd, 80, options.MainExecutable ? 1ul : 0ul);   // CS_EXECSEG_MAIN_BINARY
        ident.CopyTo(cd, cdHeader);
        team.CopyTo(cd, cdHeader + ident.Length);
        for (int i = 0; i < nSpecial; i++) special[i].CopyTo(cd, hashOffset - (i + 1) * HashSize);
        for (int page = 0; page < nCode; page++)
        {
            int start = page * PageSize, length = Math.Min(PageSize, codeLimit - start);
            SHA256.HashData(output.AsSpan(start, length), cd.AsSpan(hashOffset + page * HashSize, HashSize));
        }

        // the CMS over the finished CodeDirectory, in the room kept for it (the rest of the room is padding)
        byte[] cms = Blob(MagicBlobWrapper, identity == null ? Array.Empty<byte>() : Cms(cd, identity));
        if (cms.Length > cmsRoom) throw new PackagerError("the CMS signature came out larger than the room kept for it");
        superLength += cms.Length - cmsRoom;
        blobs[^1] = (0x10000, cms.Length);

        // the SuperBlob: its index, then the blobs in slot order
        int at = codeLimit;
        W32(output, at, MagicSuperBlob);
        W32(output, at + 4, (uint)superLength);
        W32(output, at + 8, (uint)blobs.Count);
        int offset = 12 + blobs.Count * 8;
        var contents = new Dictionary<uint, byte[]> { [0] = cd, [2] = requirements, [0x10000] = cms };
        if (entitlements != null) contents[5] = entitlements;
        if (der != null) contents[7] = der;
        for (int i = 0; i < blobs.Count; i++)
        {
            W32(output, at + 12 + i * 8, blobs[i].Slot);
            W32(output, at + 16 + i * 8, (uint)offset);
            contents[blobs[i].Slot].CopyTo(output, at + offset);
            offset += blobs[i].Length;
        }
        File.WriteAllBytes(path, output);
    }

    /// <summary>The CodeDirectory's hash (the cdhash: its first 20 bytes name the signature) of a signed file.</summary>
    public static byte[] CdHash(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        var macho = Layout.Read(file);
        if (macho.SignatureOffset <= 0) throw new PackagerError($"{Path.GetFileName(path)} is not signed");
        int at = macho.SignatureOffset;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8));
        for (int i = 0; i < count; i++)
        {
            uint slot = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 12 + i * 8));
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 16 + i * 8));
            if (slot != 0) continue;
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + offset + 4));
            return SHA256.HashData(file.AsSpan(at + offset, length));
        }
        throw new PackagerError($"{Path.GetFileName(path)}: no CodeDirectory");
    }

    // ---- the CMS signature -------------------------------------------------------------------------------------

    private static byte[] Cms(byte[] codeDirectory, Identity identity)
    {
        byte[] full = SHA256.HashData(codeDirectory);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, identity.Certificate)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.Certificates.AddRange(identity.Chain);
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));
        // the CodeDirectory's hashes, as codesign names them: a plist of truncated cdhashes, and the full one by algorithm
        var plist = new AsnWriter(AsnEncodingRules.DER);
        plist.WriteOctetString(Plist.WriteXml(new PlistDict { new("cdhashes", new List<object> { full[..20] }) }));
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113635.100.9.1"), plist.Encode()));
        var byAlgorithm = new AsnWriter(AsnEncodingRules.DER);
        using (byAlgorithm.PushSequence())
        {
            byAlgorithm.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
            byAlgorithm.WriteOctetString(full);
        }
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113635.100.9.2"), byAlgorithm.Encode()));
        var cms = new SignedCms(new ContentInfo(codeDirectory), detached: true);
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    // ---- the entitlements in DER, as codesign encodes them ---------------------------------------------------
    //
    // [APPLICATION 16] { INTEGER 1, dict } -- a dict [CONTEXT 16] of SEQUENCE { UTF8String key, value } in the keys'
    // ordinal order; an array a SEQUENCE; a string UTF8String, a boolean BOOLEAN, a number INTEGER.

    internal static byte[] DerEntitlements(PlistDict entitlements)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence(new Asn1Tag(TagClass.Application, 16, isConstructed: true)))
        {
            w.WriteInteger(1);
            WriteDer(w, entitlements);
        }
        return w.Encode();
    }

    private static void WriteDer(AsnWriter w, object value)
    {
        switch (value)
        {
            case PlistDict dict:
                // a SET's tag number, context class: DER would sort a SET's members by their encoding; the keys' order is
                // what codesign writes, so it is written as a constructed tag of its own
                using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 16, isConstructed: true)))
                    foreach (var (key, item) in dict.OrderBy(p => p.Key, StringComparer.Ordinal))
                        using (w.PushSequence())
                        {
                            w.WriteCharacterString(UniversalTagNumber.UTF8String, key);
                            WriteDer(w, item);
                        }
                break;
            case IList<object> array:
                using (w.PushSequence()) foreach (object item in array) WriteDer(w, item);
                break;
            case string s: w.WriteCharacterString(UniversalTagNumber.UTF8String, s); break;
            case bool b: w.WriteBoolean(b); break;
            case int or long: w.WriteInteger(Convert.ToInt64(value)); break;
            default: throw new PackagerError($"entitlements: a {value.GetType().Name} has no DER form");
        }
    }

    // ---- the Mach-O's layout ----------------------------------------------------------------------------------

    private sealed class Layout
    {
        public int SignatureCommand = -1, LinkEdit = -1, HeaderEnd, CommandsEnd, Ncmds;
        public int SignatureOffset;
        public ulong TextFileOffset, TextFileSize;

        public static Layout Read(byte[] f)
        {
            var l = new Layout { Ncmds = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(16)) };
            int sizeofcmds = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(20));
            l.HeaderEnd = 32;
            l.CommandsEnd = 32 + sizeofcmds;
            int at = 32;
            for (int i = 0; i < l.Ncmds; i++)
            {
                uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at));
                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at + 4));
                if (cmd == LcCodeSignature)
                {
                    l.SignatureCommand = at;
                    l.SignatureOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(at + 8));
                }
                else if (cmd == LcSegment64)
                {
                    string name = Encoding.ASCII.GetString(f, at + 8, 16).TrimEnd('\0');
                    if (name == "__LINKEDIT") l.LinkEdit = at;
                    if (name == "__TEXT")
                    {
                        l.TextFileOffset = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(at + 40));
                        l.TextFileSize = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(at + 48));
                    }
                }
                at += size;
            }
            if (l.LinkEdit < 0) throw new PackagerError("a Mach-O without __LINKEDIT");
            return l;
        }

        /// <summary>Points LC_CODE_SIGNATURE at the signature and stretches __LINKEDIT over it (adding the command where
        /// there was none, in the room after the load commands).</summary>
        public void SetSignature(byte[] f, int dataOff, int dataSize)
        {
            int cmd = SignatureCommand;
            if (cmd < 0)
            {
                cmd = CommandsEnd;
                // the first section's data starts after the room the linker left; the new command has to fit in it
                if (BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(cmd)) != 0 || BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(cmd + 8)) != 0)
                    throw new PackagerError("no room after the load commands for LC_CODE_SIGNATURE");
                W32Le(f, 16, (uint)(Ncmds + 1));
                W32Le(f, 20, (uint)(CommandsEnd - 32 + 16));
                W32Le(f, cmd, LcCodeSignature);
                W32Le(f, cmd + 4, 16);
            }
            W32Le(f, cmd + 8, (uint)dataOff);
            W32Le(f, cmd + 12, (uint)dataSize);
            ulong fileOff = BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(LinkEdit + 40));
            ulong fileSize = (ulong)(dataOff + dataSize) - fileOff;
            BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(LinkEdit + 48), fileSize);
            ulong vmSize = (fileSize + PageSize - 1) & ~(ulong)(PageSize - 1);
            if (BinaryPrimitives.ReadUInt64LittleEndian(f.AsSpan(LinkEdit + 32)) < vmSize)
                BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(LinkEdit + 32), vmSize);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static byte[] Blob(uint magic, byte[] payload)
    {
        var b = new byte[8 + payload.Length];
        W32(b, 0, magic);
        W32(b, 4, (uint)b.Length);
        payload.CopyTo(b, 8);
        return b;
    }

    private static byte[] BigEndian(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static void W32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);
    private static void W64(byte[] b, int at, ulong v) => BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(at), v);
    private static void W32Le(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
}
