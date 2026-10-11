// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a metafile IS to GDI+ before anything is drawn from it: the bytes it keeps and the header it
// derives (gdiplus.dll 10.0.26100, arm64, public PDB):
//
//   GetHeaderAndMetafile          @18001ea38   a stream: EMF first, then placeable WMF, then bare WMF
//   GetMetafileHeader(HENHMETAFILE) @180094378 an EMF's header; EnumGetEmfPlusHeader @1801bfbd0 reads
//                                              the EMF+ header out of the FIRST record after EMR_HEADER
//   GetEmfHeader                  @180093eb0   Type/Version/flags/dpi/Bounds out of ENHMETAHEADER3 and
//                                              that EMF+ header record
//   GetMetafileHeader(HMETAFILE, placeable) @180094490 + GetWmfHeader @180141bd0   a placeable WMF
//   EmfHeaderIsValid              @1801412e8   WmfHeaderIsValid @1801c92d0
//   WmfPlaceableHeaderIsValid     @18001f0a0   IsEmfPlusRecord @1801c6028
//   GetEmfFromWmfData             @1800938e8   a WMF with no placeable header becomes an EMF: GDI plays
//                                              it into an EMF DC the size of the screen, MM_ANISOTROPIC
//                                              1:1; an EMF wrapped in META_ESCAPE_ENHANCED_METAFILE
//                                              comments is unwrapped instead
//
// A metafile is held as its bytes: an EMF (or the EMF a bare WMF became) or a WMF (placeable ones
// keep the WMF with the placeable header beside it), exactly what GDI+ holds as an HENHMETAFILE or
// an HMETAFILE, without GDI.
//

using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>GDI+'s MetafileHeader (0x8c bytes), field for field.</summary>
    internal sealed class GpMetafileHeader
    {
        public MetafileType Type;
        public int Size;
        public int Version;
        public int EmfPlusFlags;
        public float DpiX, DpiY;
        public int X, Y, Width, Height;
        /// <summary>The ENHMETAHEADER3 (88 bytes) of an EMF, or the METAHEADER (18 bytes) of a WMF.</summary>
        public byte[] Raw = new byte[88];
        public int EmfPlusHeaderSize;
        public int LogicalDpiX, LogicalDpiY;

        public bool IsWmf => Type == MetafileType.Wmf || Type == MetafileType.WmfPlaceable;
        public bool IsEmfPlus => Type == MetafileType.EmfPlusOnly || Type == MetafileType.EmfPlusDual;

        public GpMetafileHeader Clone()
        {
            var h = (GpMetafileHeader)MemberwiseClone();
            h.Raw = (byte[])Raw.Clone();
            return h;
        }

        // The ENHMETAHEADER3 fields of an EMF header.
        public int EmfBoundsLeft => Le.I32(Raw, 8);
        public int EmfBoundsTop => Le.I32(Raw, 12);
        public int EmfBoundsRight => Le.I32(Raw, 16);
        public int EmfBoundsBottom => Le.I32(Raw, 20);
        public int FrameLeft => Le.I32(Raw, 24);
        public int FrameTop => Le.I32(Raw, 28);
        public int FrameRight => Le.I32(Raw, 32);
        public int FrameBottom => Le.I32(Raw, 36);
        public int DeviceCx => Le.I32(Raw, 72);
        public int DeviceCy => Le.I32(Raw, 76);
        public int MillimetersCx => Le.I32(Raw, 80);
        public int MillimetersCy => Le.I32(Raw, 84);
    }

    /// <summary>A placeable WMF's header (22 bytes), as GDI+'s WmfPlaceableFileHeader.</summary>
    internal struct GpPlaceable
    {
        public int Key;
        public short Hmf, Left, Top, Right, Bottom, Inch;
        public int Reserved;
        public short Checksum;

        public static GpPlaceable Read(byte[] b, int o) => new GpPlaceable
        {
            Key = Le.I32(b, o), Hmf = Le.I16(b, o + 4),
            Left = Le.I16(b, o + 6), Top = Le.I16(b, o + 8), Right = Le.I16(b, o + 10), Bottom = Le.I16(b, o + 12),
            Inch = Le.I16(b, o + 14), Reserved = Le.I32(b, o + 16), Checksum = Le.I16(b, o + 20),
        };

        public static GpPlaceable FromPublic(WmfPlaceableFileHeader h) => new GpPlaceable
        {
            Key = h.Key, Hmf = h.Hmf, Left = h.BboxLeft, Top = h.BboxTop, Right = h.BboxRight, Bottom = h.BboxBottom,
            Inch = h.Inch, Reserved = h.Reserved, Checksum = h.Checksum,
        };

        public byte[] ToBytes()
        {
            var b = new byte[22];
            Le.W32(b, 0, Key); Le.W16(b, 4, Hmf); Le.W16(b, 6, Left); Le.W16(b, 8, Top); Le.W16(b, 10, Right);
            Le.W16(b, 12, Bottom); Le.W16(b, 14, Inch); Le.W32(b, 16, Reserved); Le.W16(b, 20, Checksum);
            return b;
        }

        /// <summary>WmfPlaceableHeaderIsValid: the key, the XOR of the first ten words, and a box with
        /// some width and some height.</summary>
        public bool IsValid
        {
            get
            {
                if (Key != unchecked((int)0x9AC6CDD7)) return false;
                byte[] b = ToBytes();
                ushort x = 0;
                for (int i = 0; i < 10; i++) x ^= (ushort)Le.I16(b, i * 2);
                return (ushort)Checksum == x && Left != Right && Top != Bottom;
            }
        }
    }

    /// <summary>What GDI+ keeps of a metafile it can play: an EMF or a WMF, and its header.</summary>
    internal sealed class GpMetafileData
    {
        public GpMetafileHeader Header;
        /// <summary>The EMF bytes (EMF, EMF+ and a bare WMF GDI+ turned into an EMF).</summary>
        public byte[] Emf;
        /// <summary>The WMF records (METAHEADER first) of a placeable WMF.</summary>
        public byte[] Wmf;
        public GpPlaceable Placeable;
        public bool HasPlaceable;

        public bool IsWmf => Wmf != null;

        public GpMetafileData Clone() => new GpMetafileData
        {
            Header = Header.Clone(), Emf = Emf, Wmf = Wmf, Placeable = Placeable, HasPlaceable = HasPlaceable,
        };
    }

    /// <summary>Little-endian reads and writes.</summary>
    internal static class Le
    {
        public static int I32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        public static uint U32(byte[] b, int o) => (uint)I32(b, o);
        public static short I16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8));
        public static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        public static float F32(byte[] b, int o) => BitConverter.Int32BitsToSingle(I32(b, o));
        public static void W32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        public static void W16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        public static void WF(byte[] b, int o, float v) => W32(b, o, BitConverter.SingleToInt32Bits(v));
    }

    internal static class GpMetafileFormat
    {
        public const int EmrHeader = 1, EmrEof = 14, EmrGdiComment = 70;
        public const int EmfPlusSignature = 0x2B464D45;     // "EMF+"
        public const int EmfSignature = 0x464D4520;         // " EMF"
        public const int EmfPlusVersion = unchecked((int)0xDBC01002);
        public const int PlaceableKey = unchecked((int)0x9AC6CDD7);

        /// <summary>GpRound: floor(x + 0.5) -- what gdiplus.dll's "(int)(x + 0.5)" sites compute
        /// (a negative integer rounds to itself: the recorder compresses -3 to int16).</summary>
        public static int Round(float v) => Floor(v + 0.5f);

        public static int Floor(float v)
        {
            if (float.IsNaN(v)) return int.MinValue;
            double f = Math.Floor((double)v);
            if (f >= 2147483648.0 || f < -2147483648.0) return int.MinValue;
            return (int)f;
        }

        public static int Trunc(float v)
        {
            if (float.IsNaN(v)) return int.MinValue;
            if (v >= 2147483648f || v < -2147483648f) return int.MinValue;
            return (int)v;
        }

        /// <summary>EmfHeaderIsValid.</summary>
        public static bool EmfHeaderIsValid(byte[] h, int o, int len)
        {
            if (len - o < 88) return false;
            return Le.I32(h, o) == 1 && Le.I32(h, o + 40) == EmfSignature && Le.U32(h, o + 4) >= 0x58
                && Le.I16(h, o + 56) != 0 && Le.U32(h, o + 52) >= 2 && (Le.U32(h, o + 48) & 3) == 0
                && Le.I32(h, o + 72) >= 1 && Le.I32(h, o + 76) >= 1 && Le.I32(h, o + 80) >= 1 && Le.I32(h, o + 84) >= 1;
        }

        /// <summary>WmfHeaderIsValid: mtType 1 or 2, a nine-word header, version 0x100 or 0x300.</summary>
        public static bool WmfHeaderIsValid(byte[] h, int o, int len)
        {
            if (len - o < 18) return false;
            ushort type = Le.U16(h, o), hs = Le.U16(h, o + 2), ver = Le.U16(h, o + 4);
            return (ushort)(type - 1) <= 1 && hs == 9 && ((ver - 0x100) & 0xfdff) == 0;
        }

        /// <summary>IsEmfPlusRecord: an EMR_GDICOMMENT of at least 16 bytes carrying "EMF+".</summary>
        public static bool IsEmfPlusRecord(byte[] b, int o, int len)
            => len - o >= 16 && Le.I32(b, o) == EmrGdiComment && Le.U32(b, o + 4) >= 16 && Le.I32(b, o + 12) == EmfPlusSignature;

        /// <summary>GetEmfHeader: the header of an EMF whose ENHMETAHEADER3 is at <paramref name="emf"/>[0],
        /// with the EMF+ header record (28 bytes) at <paramref name="plus"/> when the first record after
        /// the header carries one. False is InvalidParameter (a frame with no width or height).</summary>
        public static bool GetEmfHeader(GpMetafileHeader m, byte[] emf, byte[] plus)
        {
            if (plus != null && Le.U32(plus, 4) > 0x1b && Le.U16(plus, 0) == 0x4001
                && (long)Le.U32(plus, 4) - 12 == Le.U32(plus, 8)
                && (Le.U32(plus, 12) & 0xfffff000) == 0xDBC01000u
                && Le.I32(plus, 20) > 0 && Le.I32(plus, 24) > 0)
            {
                m.Type = (MetafileType)((Le.U16(plus, 2) & 1) + 4);
                m.EmfPlusHeaderSize = Le.I32(plus, 4);
                m.Version = Le.I32(plus, 12);
                m.EmfPlusFlags = Le.I32(plus, 16);
                m.LogicalDpiX = Le.I32(plus, 20);
                m.LogicalDpiY = Le.I32(plus, 24);
            }
            else
            {
                m.Type = MetafileType.Emf;
                m.Version = Le.I32(emf, 44);
            }
            m.Size = Le.I32(emf, 48);
            int devCx = Le.I32(emf, 72), devCy = Le.I32(emf, 76), mmCx = Le.I32(emf, 80), mmCy = Le.I32(emf, 84);
            float pxPerMmX = (float)devCx / (float)mmCx, pxPerMmY = (float)devCy / (float)mmCy;
            m.DpiX = pxPerMmX * 25.4f;
            m.DpiY = pxPerMmY * 25.4f;
            float sx = pxPerMmX * 0.01f, sy = pxPerMmY * 0.01f;
            int fl = Le.I32(emf, 24), ft = Le.I32(emf, 28), fr = Le.I32(emf, 32), fb = Le.I32(emf, 36);
            int minX = Math.Min(fl, fr), maxX = Math.Max(fl, fr), minY = Math.Min(ft, fb), maxY = Math.Max(ft, fb);
            m.X = Round((float)minX * sx);
            m.Y = Round((float)minY * sy);
            m.Width = Round((float)(maxX - minX) * sx + 1.0f);
            m.Height = Round((float)(maxY - minY) * sy + 1.0f);
            m.Raw = new byte[88];
            Buffer.BlockCopy(emf, 0, m.Raw, 0, 88);
            return m.Width != 0 && m.Height != 0;
        }

        /// <summary>GetMetafileHeader(HENHMETAFILE): the EMF+ header is looked for only when the EMF
        /// has more than two records, and only in the first record after EMR_HEADER.</summary>
        public static bool HeaderFromEmf(byte[] emf, out GpMetafileHeader h)
        {
            h = new GpMetafileHeader();
            if (emf == null || !EmfHeaderIsValid(emf, 0, emf.Length))
                return false;
            byte[] plus = null;
            if (Le.U32(emf, 52) > 2)
            {
                int o = Le.I32(emf, 4);
                if (o >= 0 && o + 8 <= emf.Length && Le.U32(emf, o + 4) > 7 && Le.I32(emf, o) != EmrHeader
                    && IsEmfPlusRecord(emf, o, emf.Length) && Le.U32(emf, o + 4) > 0x2b && o + 44 <= emf.Length)
                {
                    plus = new byte[28];
                    Buffer.BlockCopy(emf, o + 16, plus, 0, 28);
                }
            }
            return GetEmfHeader(h, emf, plus);
        }

        /// <summary>GetWmfHeader: a placeable WMF's header comes from the placeable box and its inch.</summary>
        public static GpMetafileHeader HeaderFromWmf(byte[] wmf, GpPlaceable p)
        {
            var m = new GpMetafileHeader { Type = MetafileType.WmfPlaceable };
            m.Size = Le.I32(wmf, 6) << 1;
            m.Version = Le.U16(wmf, 4);
            m.Raw = new byte[18];
            Buffer.BlockCopy(wmf, 0, m.Raw, 0, 18);
            float dpi = p.Inch < 1 ? 1440f : (float)p.Inch;
            m.DpiX = m.DpiY = dpi;
            if (p.Left < p.Right) { m.X = p.Left; m.Width = p.Right - p.Left; }
            else { m.X = p.Right; m.Width = p.Left - p.Right; }
            if (p.Top < p.Bottom) { m.Y = p.Top; m.Height = p.Bottom - p.Top; }
            else { m.Y = p.Bottom; m.Height = p.Top - p.Bottom; }
            return m;
        }

        /// <summary>The status of reading a metafile: Ok, or the GDI+ status the caller turns into an
        /// exception, and whether GDI+ would call the image "corrupt" (it recognised the format).</summary>
        public enum ReadStatus { Ok, InvalidParameter, OutOfMemory, GenericError, Win32Error }

        /// <summary>GetHeaderAndMetafile over a whole stream's bytes (from its current position).</summary>
        public static ReadStatus Read(byte[] data, int start, out GpMetafileData result, out bool recognised)
        {
            result = null;
            recognised = false;
            int len = data.Length;
            long avail = len - start;
            // An EMF.
            if (avail >= 0x58 && EmfHeaderIsValid(data, start, len))
            {
                byte[] head = new byte[88];
                Buffer.BlockCopy(data, start, head, 0, 88);
                uint nSize = Le.U32(head, 4), nBytes = Le.U32(head, 48), nRecords = Le.U32(head, 52);
                byte[] plus = null;
                int ident = 0;
                if (nRecords >= 3 && nBytes >= nSize + 0x2c && start + nSize + 0x2c <= len)
                {
                    int o = start + (int)nSize;
                    if (IsEmfPlusRecord(data, o, len))
                    {
                        ident = Le.I32(data, o + 12);
                        plus = new byte[28];
                        Buffer.BlockCopy(data, o + 16, plus, 0, 28);
                    }
                }
                var h = new GpMetafileHeader();
                bool ok = GetEmfHeader(h, head, ident == EmfPlusSignature ? plus : null);
                recognised = true;
                if (!ok) return ReadStatus.InvalidParameter;
                long take = Math.Min((long)(uint)h.Size, avail);
                if (take <= 0) return ReadStatus.GenericError;
                var emf = new byte[take];
                Buffer.BlockCopy(data, start, emf, 0, (int)take);
                result = new GpMetafileData { Header = h, Emf = emf };
                return ReadStatus.Ok;
            }
            // A placeable WMF.
            if (avail >= 22)
            {
                GpPlaceable p = GpPlaceable.Read(data, start);
                if (p.Key == PlaceableKey && p.IsValid && avail >= 22 + 18 && WmfHeaderIsValid(data, start + 22, len))
                {
                    long size = Math.Min((long)(Le.U32(data, start + 22 + 6) << 1), avail - 22);
                    if (size < 18) { recognised = true; return ReadStatus.GenericError; }
                    var wmf = new byte[size];
                    Buffer.BlockCopy(data, start + 22, wmf, 0, (int)size);
                    var h = HeaderFromWmf(wmf, p);
                    recognised = true;
                    result = new GpMetafileData { Header = h, Wmf = wmf, Placeable = p, HasPlaceable = true };
                    return ReadStatus.Ok;
                }
            }
            // A WMF with no placeable header (or with an invalid one, which is skipped).
            int skip = avail >= 4 && Le.I32(data, start) == PlaceableKey ? 22 : 0;
            if (avail - skip >= 18 && WmfHeaderIsValid(data, start + skip, len))
            {
                long size = Math.Min((long)(Le.U32(data, start + skip + 6) << 1), avail - skip);
                recognised = true;
                if (size < 18) return ReadStatus.GenericError;
                var wmf = new byte[size];
                Buffer.BlockCopy(data, start + skip, wmf, 0, (int)size);
                byte[] emf = GpWmfToEmf.Convert(wmf, null, out GpMetafileHeader h);
                if (emf == null) return ReadStatus.GenericError;
                result = new GpMetafileData { Header = h, Emf = emf };
                return ReadStatus.Ok;
            }
            return ReadStatus.GenericError;
        }

        /// <summary>The EMF+ records inside an EMR_GDICOMMENT, or -1.</summary>
        public static int EmfPlusPayload(byte[] emf, int rec, out int length)
        {
            length = 0;
            if (!IsEmfPlusRecord(emf, rec, emf.Length)) return -1;
            int cb = Le.I32(emf, rec + 8);
            length = cb - 4;
            if (length < 0 || rec + 16 + length > emf.Length) { length = 0; return -1; }
            return rec + 16;
        }
    }
}
