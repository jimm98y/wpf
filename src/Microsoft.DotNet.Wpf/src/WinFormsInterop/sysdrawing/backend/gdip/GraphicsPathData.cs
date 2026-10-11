// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A path's serialization (DpPath::GetData @1800893e0 / SetData @18008a640) -- the EMF+ path object:
// version 0xDBC01002, point count, flags (0x4000 16-bit points, 0x800 relative points, 0x1000
// run-length types, 0x2000 winding), the points, the types, padded to four bytes.
//
// MetafilePointData @180098688 writes 16-bit points when every point is a whole number within
// +-16384, floats otherwise; relative points and run-length types only when the caller allows them
// (an EMF+ recording), which a region's data does not.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.IO;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GraphicsPathData
    {
        public const int Version = unchecked ((int) 0xDBC01002);

        public static byte[] Serialize (PointF[] pts, byte[] types, FillMode fill, bool allowCompression = false)
        {
            var ms = new MemoryStream ();
            var w = new BinaryWriter (ms);
            int n = pts?.Length ?? 0;
            int flags = fill == FillMode.Winding ? 0x2000 : 0;
            byte[] pointBytes;
            int pflags = PointData (pts, allowCompression, out pointBytes);
            byte[] typeBytes = types ?? new byte [0];
            int tflags = 0;
            if (allowCompression && TypeRle (typeBytes, out byte[] rle)) { typeBytes = rle; tflags = 0x1000; }
            w.Write (Version);
            w.Write (n);
            w.Write (flags | pflags | tflags);
            w.Write (pointBytes);
            w.Write (typeBytes);
            int pad = ((typeBytes.Length + 3) & ~3) - typeBytes.Length;
            for (int i = 0; i < pad; i++) w.Write ((byte) 0);
            w.Flush ();
            return ms.ToArray ();
        }

        /// <summary>The point bytes and their flag (0, 0x4000 or 0x800).</summary>
        public static int PointData (PointF[] pts, bool allowRelative, out byte[] bytes)
        {
            int n = pts?.Length ?? 0;
            var ms = new MemoryStream ();
            var w = new BinaryWriter (ms);
            bool integral = n > 0;
            bool inRange = true;
            var shorts = new short [n * 2];
            for (int i = 0; i < n && integral; i++) {
                int x = (int) (pts [i].X + 0.5f), y = (int) (pts [i].Y + 0.5f);
                shorts [i * 2] = (short) x; shorts [i * 2 + 1] = (short) y;
                if (shorts [i * 2] != pts [i].X || shorts [i * 2 + 1] != pts [i].Y) integral = false;
                if (((shorts [i * 2] + 0x4000) & 0xffff) > 0x8000 || ((shorts [i * 2 + 1] + 0x4000) & 0xffff) > 0x8000) inRange = false;
            }
            if (!integral) {
                for (int i = 0; i < n; i++) { w.Write (pts [i].X); w.Write (pts [i].Y); }
                w.Flush (); bytes = ms.ToArray ();
                return 0;
            }
            if (!(allowRelative && inRange)) {
                foreach (short s in shorts) w.Write (s);
                w.Flush (); bytes = ms.ToArray ();
                return 0x4000;
            }
            int px = 0, py = 0;
            for (int i = 0; i < n; i++) {
                WriteDelta (w, shorts [i * 2] - px);
                WriteDelta (w, shorts [i * 2 + 1] - py);
                px = shorts [i * 2]; py = shorts [i * 2 + 1];
            }
            while (ms.Length % 4 != 0) w.Write ((byte) 0);
            w.Flush (); bytes = ms.ToArray ();
            return 0x800;
        }

        static void WriteDelta (BinaryWriter w, int d)
        {
            if (d >= -64 && d <= 63) w.Write ((byte) (d & 0x7f));
            else { w.Write ((byte) (0x80 | ((d >> 8) & 0x7f))); w.Write ((byte) d); }
        }

        /// <summary>MetafileTypeData's run-length encoding of the types, when it is shorter.</summary>
        static bool TypeRle (byte[] t, out byte[] rle)
        {
            rle = null;
            var o = new List<byte> ();
            int run = 0;
            byte cur = t.Length > 0 ? t [0] : (byte) 0;
            for (int i = 0; i < t.Length; i++) {
                byte v = t [i];
                if (((v >> 6) & 1) != 0) return false;
                if (run == 0x3f || (run > 0 && v != cur)) {
                    o.Add ((byte) (((cur & 3) == 3 ? 0xc0 : 0x40) | run));
                    run = 0;
                }
                if (((v - 1) & 0xfd) == 0) { run++; cur = v; }
                else o.Add (v);
            }
            if (run > 0) o.Add ((byte) (((cur & 3) == 3 ? 0xc0 : 0x40) | run));
            if (o.Count >= t.Length) return false;
            rle = o.ToArray ();
            return true;
        }

        public static void Deserialize (byte[] d, int pos, int size, out PointF[] pts, out byte[] types, out FillMode fill)
        {
            int end = pos + size;
            if (size < 12 || (BitConverter.ToInt32 (d, pos) & unchecked ((int) 0xfffff000)) != unchecked ((int) 0xDBC01000))
                throw new ArgumentException ("Parameter is not valid.");
            int n = BitConverter.ToInt32 (d, pos + 4);
            int flags = BitConverter.ToInt32 (d, pos + 8);
            fill = (flags & 0x2000) != 0 ? FillMode.Winding : FillMode.Alternate;
            int p = pos + 12;
            pts = ReadPoints (d, ref p, n, flags);
            types = ReadTypes (d, ref p, n, flags);
        }

        public static PointF[] ReadPoints (byte[] d, ref int p, int n, int flags)
        {
            var pts = new PointF [n];
            if ((flags & 0x800) != 0) {
                int x = 0, y = 0;
                for (int i = 0; i < n; i++) {
                    x += ReadDelta (d, ref p);
                    y += ReadDelta (d, ref p);
                    pts [i] = new PointF (x, y);
                }
                p = (p + 3) & ~3;
            } else if ((flags & 0x4000) != 0) {
                for (int i = 0; i < n; i++) { pts [i] = new PointF (BitConverter.ToInt16 (d, p), BitConverter.ToInt16 (d, p + 2)); p += 4; }
            } else {
                for (int i = 0; i < n; i++) { pts [i] = new PointF (BitConverter.ToSingle (d, p), BitConverter.ToSingle (d, p + 4)); p += 8; }
            }
            return pts;
        }

        static int ReadDelta (byte[] d, ref int p)
        {
            byte b = d [p++];
            if ((b & 0x80) == 0) return (b & 0x40) != 0 ? b - 0x80 : b;
            int v = ((b & 0x7f) << 8) | d [p++];
            if ((v & 0x4000) != 0) v -= 0x8000;
            return v;
        }

        public static byte[] ReadTypes (byte[] d, ref int p, int n, int flags)
        {
            var t = new byte [n];
            if ((flags & 0x1000) != 0) {
                int i = 0;
                while (i < n) {
                    byte b = d [p++];
                    if ((b & 0x40) != 0) {
                        int run = b & 0x3f;
                        byte type = (b & 0x80) != 0 ? (byte) 3 : (byte) 1;
                        for (int k = 0; k < run && i < n; k++) t [i++] = type;
                    } else t [i++] = b;
                }
            } else {
                Array.Copy (d, p, t, 0, n);
                p += n;
            }
            return t;
        }
    }
}
