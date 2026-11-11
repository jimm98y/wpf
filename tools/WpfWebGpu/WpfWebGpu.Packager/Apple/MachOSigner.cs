// Apple's code signature, written in managed code on any machine -- what codesign writes, so that an iOS, visionOS or
// macOS app is signed where the Packager runs, Windows and Linux included.
//
// A thin 64-bit Mach-O's signature is a SuperBlob at the end of its __LINKEDIT, which LC_CODE_SIGNATURE points at:
//
//   CodeDirectory (slot 0)        the identifier, the flags, a SHA-256 of every 16 KB page of the file up to the
//                                 signature, and the special slots: Info.plist (-1), the requirements (-2), the
//                                 bundle's resource seal (-3), the entitlements as XML (-5) and as DER (-7)
//   requirements (2)              none: an empty set, as codesign writes for an ad hoc signature
//   entitlements (5, 7)           the XML plist, and the same in Apple's DER form, which iOS 15 and later read
//   CMS (0x10000)                 empty for an ad hoc signature; a certificate's signature over the CodeDirectory
//
// All of it big-endian; the page hashes cover the load commands, so the signature's size is settled first and
// written into LC_CODE_SIGNATURE and __LINKEDIT before anything is hashed.

using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
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

    /// <summary>Signs the thin 64-bit Mach-O at <paramref name="path"/> ad hoc, replacing any signature it has.</summary>
    public static void SignAdHoc(string path, Options options)
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
        byte[] cms = Blob(MagicBlobWrapper, Array.Empty<byte>());                                // ad hoc: no CMS

        int nSpecial = der != null ? 7 : entitlements != null ? 5 : options.CodeResources != null ? 3 : 2;
        int nCode = (codeLimit + PageSize - 1) / PageSize;
        byte[] ident = Encoding.UTF8.GetBytes(options.Identifier + "\0");
        const int cdHeader = 88;                                                                 // version 0x20400
        int hashOffset = cdHeader + ident.Length + nSpecial * HashSize;
        int cdLength = hashOffset + nCode * HashSize;

        var blobs = new List<(uint Slot, int Length)> { (0, cdLength), (2, requirements.Length) };
        if (entitlements != null) blobs.Add((5, entitlements.Length));
        if (der != null) blobs.Add((7, der.Length));
        blobs.Add((0x10000, cms.Length));
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
        W32(cd, 12, CsAdhoc | (options.HardenedRuntime ? CsRuntime : 0));
        W32(cd, 16, (uint)hashOffset);
        W32(cd, 20, cdHeader);                       // identOffset
        W32(cd, 24, (uint)nSpecial);
        W32(cd, 28, (uint)nCode);
        W32(cd, 32, (uint)codeLimit);
        cd[36] = HashSize;
        cd[37] = 2;                                  // SHA-256
        cd[38] = 0;                                  // platform
        cd[39] = PageShift;
        // spare2 (40), scatter (44), team (48), spare3 (52), codeLimit64 (56) stay 0
        W64(cd, 64, macho.TextFileOffset);           // execSegBase
        W64(cd, 72, macho.TextFileSize);             // execSegLimit
        W64(cd, 80, options.MainExecutable ? 1ul : 0ul);   // CS_EXECSEG_MAIN_BINARY
        ident.CopyTo(cd, cdHeader);
        for (int i = 0; i < nSpecial; i++) special[i].CopyTo(cd, hashOffset - (i + 1) * HashSize);
        for (int page = 0; page < nCode; page++)
        {
            int start = page * PageSize, length = Math.Min(PageSize, codeLimit - start);
            SHA256.HashData(output.AsSpan(start, length), cd.AsSpan(hashOffset + page * HashSize, HashSize));
        }

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
