// Windows: writes the image of a DLL that stands in for a system DLL (user32) towards an app's own
// P/Invokes. See WindowsShim.cs for why such a DLL exists at all; this file only builds it.
//
// The image mirrors the real DLL's export table ordinal for ordinal, so an import by name and an
// import by ordinal ("#2005") find the same function they would in the real DLL. Every export is a
// stub that jumps through its own entry in a table in the image's writable data:
//
//   entry i = { target, managed, handle-argument mask, 0 }          32 bytes
//
//   - an export nobody implements: `jump [entry.target]`, the real function, set when the image is
//     loaded (GetProcAddress of the real DLL by the same ordinal, which follows forwarders). The
//     arguments are never touched, so one stub shape serves every signature.
//   - an export with a managed implementation: the stub hands its entry to one shared dispatcher,
//     which saves the argument registers, asks the managed classifier which of the two to run --
//     classify(entry, saved argument registers) returns entry.target or entry.managed -- restores the
//     registers and the stack exactly as they were at the call, and jumps there. Whichever runs
//     returns straight to the app's caller; the dispatcher leaves no frame behind.
//
// The image is position independent (every reference is PC-relative), so it carries an empty base
// relocation block and loads at any address. It has no entry point, no imports and no code that runs
// when loaded. Machine code is emitted here for arm64 and x64.

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfMac.Bootstrap.Win32
{
    internal static class ShimImage
    {
        public const int EntrySize = 32;
        /// <summary>Where the classifier's address goes: the first 16 bytes of the data section.</summary>
        public const int ClassifierOffset = 0;
        /// <summary>Where entry 0 starts in the data section.</summary>
        public const int TableOffset = 16;

        private const int FileAlign = 0x200, SectionAlign = 0x1000;
        private const int StubSize = 16;

        /// <summary>One export of the real DLL. Name is null for an export only reachable by ordinal;
        /// Present is false for a gap in the ordinal range (no function at that ordinal).</summary>
        public readonly record struct Export(int Ordinal, string? Name, bool Present, bool Dispatched);

        public sealed class Layout
        {
            public required byte[] Image { get; init; }
            /// <summary>RVA of the data section (classifier slot, then the entry table).</summary>
            public required int DataRva { get; init; }
        }

        /// <summary>Builds the image. <paramref name="exports"/> is indexed by ordinal - <paramref name="ordinalBase"/>.</summary>
        public static Layout Build(string dllName, int ordinalBase, IReadOnlyList<Export> exports, Architecture arch)
        {
            bool arm64 = arch == Architecture.Arm64;
            if (!arm64 && arch != Architecture.X64) throw new PlatformNotSupportedException($"no shim image for {arch}");
            int n = exports.Count;

            // ---- section layout ---------------------------------------------------------------
            const int textRva = 0x1000;
            const int dispatcherSize = 128;                     // the shared dispatcher, padded
            int textSize = dispatcherSize + n * StubSize;
            int rdataRva = Align(textRva + textSize, SectionAlign);

            var named = exports.Where(e => e.Present && e.Name != null)
                .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
            // Export directory, then EAT, name pointers, name ordinals, then strings.
            int edirOff = 0;
            int eatOff = edirOff + 40;
            int namesOff = eatOff + 4 * n;
            int ordsOff = namesOff + 4 * named.Count;
            int stringsOff = Align(ordsOff + 2 * named.Count, 4);
            var strings = new MemoryStream();
            int dllNameOff = stringsOff + (int)strings.Length;
            WriteZ(strings, dllName);
            var nameOffsets = new int[named.Count];
            for (int i = 0; i < named.Count; i++)
            {
                nameOffsets[i] = stringsOff + (int)strings.Length;
                WriteZ(strings, named[i].Name!);
            }
            int rdataSize = stringsOff + (int)strings.Length;
            int dataRva = Align(rdataRva + rdataSize, SectionAlign);
            int dataSize = TableOffset + n * EntrySize;
            int relocRva = Align(dataRva + dataSize, SectionAlign);
            const int relocSize = 12;
            int imageSize = Align(relocRva + relocSize, SectionAlign);

            const int headersSize = 0x400;
            int textRaw = headersSize;
            int textRawSize = Align(textSize, FileAlign);
            int rdataRaw = textRaw + textRawSize;
            int rdataRawSize = Align(rdataSize, FileAlign);
            int relocRaw = rdataRaw + rdataRawSize;              // .data has no raw bytes: it starts zeroed
            int relocRawSize = Align(relocSize, FileAlign);
            int fileSize = relocRaw + relocRawSize;
            var img = new byte[fileSize];
            Span<byte> s = img;

            // ---- headers ------------------------------------------------------------------------
            s[0] = (byte)'M'; s[1] = (byte)'Z';
            W32(s, 0x3C, 0x40);
            int pe = 0x40;
            s[pe] = (byte)'P'; s[pe + 1] = (byte)'E';
            int coff = pe + 4;
            W16(s, coff + 0, arm64 ? 0xAA64 : 0x8664);           // Machine
            W16(s, coff + 2, 4);                                 // NumberOfSections
            W16(s, coff + 16, 240);                              // SizeOfOptionalHeader
            W16(s, coff + 18, 0x2022);                           // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL
            int opt = coff + 20;
            W16(s, opt + 0, 0x20B);                              // PE32+
            s[opt + 2] = 14;                                     // linker version
            W32(s, opt + 4, textRawSize);                        // SizeOfCode
            W32(s, opt + 8, rdataRawSize + relocRawSize);        // SizeOfInitializedData
            W32(s, opt + 12, Align(dataSize, FileAlign));        // SizeOfUninitializedData
            W32(s, opt + 16, 0);                                 // AddressOfEntryPoint: none
            W32(s, opt + 20, textRva);                           // BaseOfCode
            W64(s, opt + 24, 0x180000000);                       // ImageBase
            W32(s, opt + 32, SectionAlign);
            W32(s, opt + 36, FileAlign);
            W16(s, opt + 40, 6); W16(s, opt + 42, arm64 ? 2 : 0); // OS version
            W16(s, opt + 48, 6); W16(s, opt + 50, arm64 ? 2 : 0); // subsystem version
            W32(s, opt + 56, imageSize);
            W32(s, opt + 60, headersSize);
            W16(s, opt + 68, 2);                                 // Subsystem: WINDOWS_GUI
            W16(s, opt + 70, 0x0160);                            // HIGH_ENTROPY_VA | DYNAMIC_BASE | NX_COMPAT
            W64(s, opt + 72, 0x100000); W64(s, opt + 80, 0x1000); // stack reserve/commit
            W64(s, opt + 88, 0x100000); W64(s, opt + 96, 0x1000); // heap reserve/commit
            W32(s, opt + 108, 16);                               // NumberOfRvaAndSizes
            int dirs = opt + 112;
            W32(s, dirs + 0 * 8, rdataRva + edirOff); W32(s, dirs + 0 * 8 + 4, rdataSize);   // export
            W32(s, dirs + 5 * 8, relocRva); W32(s, dirs + 5 * 8 + 4, relocSize);            // base relocations

            int sh = opt + 240;
            Section(s, sh + 0 * 40, ".text", textSize, textRva, textRawSize, textRaw, 0x60000020);    // CODE | EXECUTE | READ
            Section(s, sh + 1 * 40, ".rdata", rdataSize, rdataRva, rdataRawSize, rdataRaw, 0x40000040); // INITIALIZED | READ
            Section(s, sh + 2 * 40, ".data", dataSize, dataRva, 0, 0, 0xC0000080);                     // UNINITIALIZED | READ | WRITE
            Section(s, sh + 3 * 40, ".reloc", relocSize, relocRva, relocRawSize, relocRaw, 0x42000040); // INITIALIZED | DISCARDABLE | READ

            // ---- .text ----------------------------------------------------------------------------
            Span<byte> text = s.Slice(textRaw, textRawSize);
            int classifierRva = dataRva + ClassifierOffset;
            if (arm64) Arm64.Dispatcher(text, textRva, classifierRva);
            else X64.Dispatcher(text, textRva, classifierRva);
            for (int i = 0; i < n; i++)
            {
                int stubRva = textRva + dispatcherSize + i * StubSize;
                int entryRva = dataRva + TableOffset + i * EntrySize;
                Span<byte> stub = text.Slice(dispatcherSize + i * StubSize, StubSize);
                if (arm64)
                {
                    if (exports[i].Dispatched) Arm64.DispatchStub(stub, stubRva, entryRva, textRva);
                    else Arm64.JumpStub(stub, stubRva, entryRva);
                }
                else
                {
                    if (exports[i].Dispatched) X64.DispatchStub(stub, stubRva, entryRva, textRva);
                    else X64.JumpStub(stub, stubRva, entryRva);
                }
            }

            // ---- .rdata: the export directory -------------------------------------------------------
            Span<byte> rd = s.Slice(rdataRaw, rdataRawSize);
            W32(rd, edirOff + 12, rdataRva + dllNameOff);        // Name
            W32(rd, edirOff + 16, ordinalBase);                  // Base
            W32(rd, edirOff + 20, n);                            // NumberOfFunctions
            W32(rd, edirOff + 24, named.Count);                  // NumberOfNames
            W32(rd, edirOff + 28, rdataRva + eatOff);
            W32(rd, edirOff + 32, rdataRva + namesOff);
            W32(rd, edirOff + 36, rdataRva + ordsOff);
            for (int i = 0; i < n; i++)
            {
                W32(rd, eatOff + 4 * i, exports[i].Present ? textRva + dispatcherSize + i * StubSize : 0);
            }
            for (int i = 0; i < named.Count; i++)
            {
                W32(rd, namesOff + 4 * i, rdataRva + nameOffsets[i]);
                W16(rd, ordsOff + 2 * i, named[i].Ordinal - ordinalBase);
            }
            strings.ToArray().CopyTo(rd.Slice(stringsOff));

            // ---- .reloc: one block of padding entries, nothing to relocate ----------------------------
            Span<byte> rl = s.Slice(relocRaw, relocRawSize);
            W32(rl, 0, textRva);
            W32(rl, 4, relocSize);

            return new Layout { Image = img, DataRva = dataRva };
        }

        private static void Section(Span<byte> s, int at, string name, int vsize, int rva, int rawSize, int raw, uint flags)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(s.Slice(at, 8));
            W32(s, at + 8, vsize);
            W32(s, at + 12, rva);
            W32(s, at + 16, rawSize);
            W32(s, at + 20, raw);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(at + 36), flags);
        }

        private static void WriteZ(MemoryStream m, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            m.Write(b);
            m.WriteByte(0);
        }

        private static int Align(int v, int a) => (v + a - 1) / a * a;
        private static void W16(Span<byte> s, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(at), (ushort)v);
        private static void W32(Span<byte> s, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(s.Slice(at), v);
        private static void W64(Span<byte> s, int at, long v) => BinaryPrimitives.WriteInt64LittleEndian(s.Slice(at), v);

        /// <summary>
        /// Where the classifier finds argument k in the block the dispatcher saved: arm64 saves x0..x7
        /// and points at them (arguments 8 and up are the caller's stack, 160 bytes further on); x64
        /// saves rcx, rdx, r8, r9 (arguments 4 and up are on the caller's stack, past the return address
        /// and the shadow space).
        /// </summary>
        public static int ArgumentOffset(int k, Architecture arch) => arch == Architecture.Arm64
            ? (k < 8 ? 8 * k : 160 + 8 * (k - 8))
            : (k < 4 ? 8 * k : 0x90 + 8 * (k - 4));

        // ---- arm64 ----------------------------------------------------------------------------------

        private static class Arm64
        {
            const int X0 = 0, X1 = 1, X8 = 8, X9 = 9, X16 = 16, X17 = 17, FP = 29, LR = 30, SP = 31;

            public static void Dispatcher(Span<byte> text, int rva, int classifierRva)
            {
                var c = new List<uint>
                {
                    StpPre(FP, LR, SP, -176),
                    AddImm(FP, SP, 0),
                    Stp(0, 1, SP, 16), Stp(2, 3, SP, 32), Stp(4, 5, SP, 48), Stp(6, 7, SP, 64),
                    StpD(0, 1, SP, 80), StpD(2, 3, SP, 96), StpD(4, 5, SP, 112), StpD(6, 7, SP, 128),
                    Stp(X8, X16, SP, 144),
                    Mov(X0, X16),                       // classify(entry,
                    AddImm(X1, SP, 16),                 //          saved x0..x7)
                };
                c.Add(Adrp(X9, rva + 4 * c.Count, classifierRva));
                c.Add(LdrImm(X9, X9, classifierRva & 0xFFF));
                c.Add(Blr(X9));
                c.Add(Mov(X17, X0));
                c.AddRange(new[]
                {
                    Ldp(X8, X16, SP, 144),
                    LdpD(6, 7, SP, 128), LdpD(4, 5, SP, 112), LdpD(2, 3, SP, 96), LdpD(0, 1, SP, 80),
                    Ldp(6, 7, SP, 64), Ldp(4, 5, SP, 48), Ldp(2, 3, SP, 32), Ldp(0, 1, SP, 16),
                    LdpPost(FP, LR, SP, 176),
                    Br(X17),
                });
                if (c.Count * 4 > 128) throw new InvalidOperationException("arm64 dispatcher does not fit");
                for (int i = 0; i < 32; i++) Put(text, i, i < c.Count ? c[i] : Brk);
            }

            public static void JumpStub(Span<byte> stub, int rva, int entryRva)
            {
                Put(stub, 0, Adrp(X16, rva, entryRva));
                Put(stub, 1, LdrImm(X16, X16, entryRva & 0xFFF));
                Put(stub, 2, Br(X16));
                Put(stub, 3, Brk);
            }

            public static void DispatchStub(Span<byte> stub, int rva, int entryRva, int dispatcherRva)
            {
                Put(stub, 0, Adrp(X16, rva, entryRva));
                Put(stub, 1, AddImm(X16, X16, entryRva & 0xFFF));
                Put(stub, 2, B(rva + 8, dispatcherRva));
                Put(stub, 3, Brk);
            }

            const uint Brk = 0xD4200000;

            static void Put(Span<byte> s, int index, uint insn) => BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(4 * index), insn);
            static uint Imm7(int bytes) => (uint)((bytes / 8) & 0x7F) << 15;
            static uint StpPre(int t1, int t2, int n, int imm) => 0xA9800000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint LdpPost(int t1, int t2, int n, int imm) => 0xA8C00000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint Stp(int t1, int t2, int n, int imm) => 0xA9000000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint Ldp(int t1, int t2, int n, int imm) => 0xA9400000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint StpD(int t1, int t2, int n, int imm) => 0x6D000000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint LdpD(int t1, int t2, int n, int imm) => 0x6D400000 | Imm7(imm) | (uint)t2 << 10 | (uint)n << 5 | (uint)t1;
            static uint AddImm(int d, int n, int imm) => 0x91000000 | (uint)(imm & 0xFFF) << 10 | (uint)n << 5 | (uint)d;
            static uint Mov(int d, int m) => 0xAA0003E0 | (uint)m << 16 | (uint)d;
            static uint LdrImm(int t, int n, int imm) => 0xF9400000 | (uint)((imm / 8) & 0xFFF) << 10 | (uint)n << 5 | (uint)t;
            static uint Br(int n) => 0xD61F0000 | (uint)n << 5;
            static uint Blr(int n) => 0xD63F0000 | (uint)n << 5;
            static uint B(int pc, int target) => 0x14000000 | (uint)(((target - pc) / 4) & 0x3FFFFFF);
            static uint Adrp(int d, int pc, int target)
            {
                int pages = (target >> 12) - (pc >> 12);
                return 0x90000000 | (uint)(pages & 3) << 29 | (uint)((pages >> 2) & 0x7FFFF) << 5 | (uint)d;
            }
        }

        // ---- x64 ------------------------------------------------------------------------------------

        private static class X64
        {
            public static void Dispatcher(Span<byte> text, int rva, int classifierRva)
            {
                var b = new List<byte>();
                void E(params byte[] bytes) => b.AddRange(bytes);
                E(0x48, 0x81, 0xEC, 0x88, 0x00, 0x00, 0x00);     // sub rsp, 0x88
                E(0x48, 0x89, 0x4C, 0x24, 0x20);                 // mov [rsp+20h], rcx
                E(0x48, 0x89, 0x54, 0x24, 0x28);                 // mov [rsp+28h], rdx
                E(0x4C, 0x89, 0x44, 0x24, 0x30);                 // mov [rsp+30h], r8
                E(0x4C, 0x89, 0x4C, 0x24, 0x38);                 // mov [rsp+38h], r9
                E(0xF2, 0x0F, 0x11, 0x44, 0x24, 0x40);           // movsd [rsp+40h], xmm0
                E(0xF2, 0x0F, 0x11, 0x4C, 0x24, 0x48);           // movsd [rsp+48h], xmm1
                E(0xF2, 0x0F, 0x11, 0x54, 0x24, 0x50);           // movsd [rsp+50h], xmm2
                E(0xF2, 0x0F, 0x11, 0x5C, 0x24, 0x58);           // movsd [rsp+58h], xmm3
                E(0x4C, 0x89, 0xD9);                             // mov rcx, r11          (entry)
                E(0x48, 0x8D, 0x54, 0x24, 0x20);                 // lea rdx, [rsp+20h]    (saved args)
                E(0xFF, 0x15); int disp = b.Count; E(0, 0, 0, 0); // call [rip+classifier]
                Disp(b, disp, rva + disp + 4, classifierRva);
                E(0x49, 0x89, 0xC3);                             // mov r11, rax
                E(0x48, 0x8B, 0x4C, 0x24, 0x20);                 // mov rcx, [rsp+20h]
                E(0x48, 0x8B, 0x54, 0x24, 0x28);                 // mov rdx, [rsp+28h]
                E(0x4C, 0x8B, 0x44, 0x24, 0x30);                 // mov r8, [rsp+30h]
                E(0x4C, 0x8B, 0x4C, 0x24, 0x38);                 // mov r9, [rsp+38h]
                E(0xF2, 0x0F, 0x10, 0x44, 0x24, 0x40);           // movsd xmm0, [rsp+40h]
                E(0xF2, 0x0F, 0x10, 0x4C, 0x24, 0x48);           // movsd xmm1, [rsp+48h]
                E(0xF2, 0x0F, 0x10, 0x54, 0x24, 0x50);           // movsd xmm2, [rsp+50h]
                E(0xF2, 0x0F, 0x10, 0x5C, 0x24, 0x58);           // movsd xmm3, [rsp+58h]
                E(0x48, 0x81, 0xC4, 0x88, 0x00, 0x00, 0x00);     // add rsp, 0x88
                E(0x41, 0xFF, 0xE3);                             // jmp r11
                if (b.Count > 128) throw new InvalidOperationException("x64 dispatcher does not fit");
                text.Slice(0, 128).Fill(0xCC);
                b.ToArray().CopyTo(text);
            }

            public static void JumpStub(Span<byte> stub, int rva, int entryRva)
            {
                stub.Fill(0xCC);
                stub[0] = 0xFF; stub[1] = 0x25;                  // jmp [rip+entry.target]
                W32(stub, 2, entryRva - (rva + 6));
            }

            public static void DispatchStub(Span<byte> stub, int rva, int entryRva, int dispatcherRva)
            {
                stub.Fill(0xCC);
                stub[0] = 0x4C; stub[1] = 0x8D; stub[2] = 0x1D;   // lea r11, [rip+entry]
                W32(stub, 3, entryRva - (rva + 7));
                stub[7] = 0xE9;                                  // jmp dispatcher
                W32(stub, 8, dispatcherRva - (rva + 12));
            }

            static void Disp(List<byte> b, int at, int next, int target)
            {
                int d = target - next;
                b[at] = (byte)d; b[at + 1] = (byte)(d >> 8); b[at + 2] = (byte)(d >> 16); b[at + 3] = (byte)(d >> 24);
            }
        }
    }
}
