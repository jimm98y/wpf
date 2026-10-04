// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Recording into a metafile: GDI+'s MetafileRecorder, managed (gdiplus.dll 10.0.26100, arm64,
// public PDB). A Graphics made by Graphics.FromImage(metafile) calls these methods with the
// arguments its caller gave it, in world units, before any transform; this writes what GDI+ writes:
//
//   GpMetafile::InitForRecording   @1800995c0   frame (GetFrameRectInMM100Units @180099320 /
//                                               FrameToMM100 @1801bb0b0), the recorder, its header
//   MetafileRecorder::MetafileRecorder @180098930 + RecordHeader @18009bdc0
//   MetafileRecorder::Record*      @180099c30..18009cd00   one EMF+ record per call
//   MetafileRecorder::RecordObject @18009bf30 + MetafileRecordObjectList::InsertAt @180099910
//                                               the 64-slot object table, least-recently-used reuse
//   MetafileRecorder::WriteRecordHeader @18009d340   the device bounds of what is drawn
//   EmfPlusCommentStream::WriteRecordHeader @18009d1f0 / Write @18009ce70 / Flush @1801bb020 /
//                         EndObjectRecord @1801bac50   EMF+ records batched into GDI comments of at
//                                               most 0xfdfc bytes; objects bigger than that split
//                                               into continued records (flag 0x8000)
//   MetafileRecorder::WriteGdiComment @18009d000    a dual file flushes after every record
//   MetafileRecorder::EndRecording @180098f80   flush, the no-op PatBlt over the bounds
//                                               (GpGraphics::NoOpPatBlt @180099b78, ROP 0x00AA0029,
//                                               between SaveDC/SetICMMode/RestoreDC as DpContext::
//                                               GetHdc/ReleaseHdc leave it), EndOfFile, flush, and
//                                               the header GDI+ derives from the EMF's
//
// and the bounds GpGraphics hands each Record* call (GpGraphics::FillRects @18000e598, DrawRects
// @18000d3f8, FillEllipse @1800761c8, DrawPath @180075bd0, FillRegion @180076548, Clear @180075440,
// DrawImage @18000f530, ...): the primitive's device bounds, a pen's width added as GpPath::GetBounds
// @18001c020 adds it.
//
// What the EMF around the EMF+ comments holds is GDI's (GpEmfWriter).
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        readonly Metafile _owner;
        readonly GpRefDevice _dev;
        readonly EmfType _type;
        readonly Stream _stream;
        readonly string _fileName;
        internal readonly GpEmfWriter Emf;
        internal readonly GpMetafileHeader Header;
        readonly RectangleF _metafileBounds;      // +0x6c0: the frame in device pixels, or the device
        bool _ended;
        internal bool GraphicsTaken;

        /// <summary>The desktop's dpi: what a Graphics on a metafile reports (GetForMetafile builds it
        /// on the desktop device).</summary>
        public float DpiX => _dev.DesktopDpiX;
        public float DpiY => _dev.DesktopDpiY;

        GpMetafileRecorder(Metafile owner, GpRefDevice dev, EmfType type, int[] frame, string description, Stream stream, string fileName,
            RectangleF metafileBounds, GpMetafileHeader header)
        {
            _owner = owner;
            _dev = dev;
            _type = type;
            _stream = stream;
            _fileName = fileName;
            _metafileBounds = metafileBounds;
            Header = header;
            Emf = new GpEmfWriter(dev, frame, description);
            _state = new GState();
            if (type != EmfType.EmfOnly)
            {
                _comment = new CommentStream(Emf);
                RecordHeader();
            }
        }

        // ---- creation: GpMetafile::InitForRecording -------------------------------------------------

        static bool FrameToMM100(GpRefDevice dev, RectangleF f, MetafileFrameUnit unit, out int[] rect)
        {
            rect = null;
            if (dev.HorzRes <= 0 || dev.VertRes <= 0 || dev.HorzSize <= 0 || dev.VertSize <= 0) return false;
            float dpiX = ((float)dev.HorzRes / (float)dev.HorzSize) * 25.4f;
            float dpiY = ((float)dev.VertRes / (float)dev.VertSize) * 25.4f;
            float kx = 2540f / dpiX, ky = 2540f / dpiY;
            float x, y, w, h;
            switch (unit)
            {
                case MetafileFrameUnit.Point:
                    x = f.X * 35.27777862548828f; y = f.Y * 35.27777862548828f;
                    w = f.Width * (dpiX / 72f); h = f.Height * (dpiY / 72f);
                    break;
                case MetafileFrameUnit.Inch:
                    x = f.X * 2540f; y = f.Y * 2540f;
                    w = f.Width * dpiX; h = f.Height * dpiY;
                    break;
                case MetafileFrameUnit.Document:
                    x = f.X * 8.466666221618652f; y = f.Y * 8.466666221618652f;
                    w = f.Width * (dpiX / 300f); h = f.Height * (dpiY / 300f);
                    break;
                case MetafileFrameUnit.Millimeter:
                    x = f.X * 100f; y = f.Y * 100f;
                    w = (dpiX / 25.4f) * f.Width; h = (dpiY / 25.4f) * f.Height;
                    break;
                default: // Pixel
                    x = f.X * kx; y = f.Y * ky;
                    w = f.Width; h = f.Height;
                    break;
            }
            w = (w - 1.0f) * kx;
            h = (h - 1.0f) * ky;
            int l = GpMetafileFormat.Round(x), t = GpMetafileFormat.Round(y);
            int r = GpMetafileFormat.Round(w + x), b = GpMetafileFormat.Round(h + y);
            if (l > r || t > b) return false;
            rect = new[] { l, t, r, b };
            return true;
        }

        internal static GpMetafileRecorder Create(Metafile owner, GpRefDevice dev, EmfType type, RectangleF? frame,
            MetafileFrameUnit unit, string description, Stream stream, string fileName)
        {
            int[] lprc = null;
            if (frame.HasValue)
            {
                RectangleF f = frame.Value;
                if (f.Width < 0f || f.Height < 0f) return null;
                if (unit == MetafileFrameUnit.GdiCompatible)
                {
                    int l = GpMetafileFormat.Round(f.X), t = GpMetafileFormat.Round(f.Y);
                    int r = GpMetafileFormat.Round(f.Width + f.X), b = GpMetafileFormat.Round(f.Height + f.Y);
                    if (r < l || b < t) return null;
                    lprc = new[] { l, t, r, b };
                }
                else if (!FrameToMM100(dev, f, unit, out lprc))
                    return null;
            }
            if (dev.HorzRes <= 0 || dev.VertRes <= 0 || dev.HorzSize <= 0 || dev.VertSize <= 0) return null;
            float pxPerMmX = (float)dev.HorzRes / (float)dev.HorzSize, pxPerMmY = (float)dev.VertRes / (float)dev.VertSize;
            var h = new GpMetafileHeader
            {
                Version = GpMetafileFormat.EmfPlusVersion,
                DpiX = pxPerMmX * 25.4f,
                DpiY = pxPerMmY * 25.4f,
            };
            RectangleF mb;
            if (lprc == null)
                mb = new RectangleF(0f, 0f, (float)dev.HorzRes - 1.0f, (float)dev.VertRes - 1.0f);
            else
            {
                float kx = pxPerMmX * 0.01f, ky = pxPerMmY * 0.01f;
                mb = new RectangleF((float)lprc[0] * kx, (float)lprc[1] * ky,
                    (float)(lprc[2] - lprc[0]) * kx, (float)(lprc[3] - lprc[1]) * ky);
            }
            if (type == EmfType.EmfOnly)
                h.Type = MetafileType.Emf;
            else
            {
                h.EmfPlusHeaderSize = 0x1c;
                h.LogicalDpiX = dev.LogPixelsX;
                h.LogicalDpiY = dev.LogPixelsY;
                h.EmfPlusFlags = dev.IsDisplay ? 1 : 0;
                h.Type = type == EmfType.EmfPlusOnly ? MetafileType.EmfPlusOnly : MetafileType.EmfPlusDual;
            }
            return new GpMetafileRecorder(owner, dev, type, lprc, description, stream, fileName, mb, h);
        }

        // ---- the comment stream: EmfPlusCommentStream ------------------------------------------------

        sealed class CommentStream
        {
            const int Capacity = 0xfdfc;
            readonly GpEmfWriter _emf;
            readonly byte[] _buf = new byte[4 + Capacity];   // "EMF+" then the records
            int _used;
            bool _continuing;

            public CommentStream(GpEmfWriter emf)
            {
                _emf = emf;
                Le.W32(_buf, 0, GpMetafileFormat.EmfPlusSignature);
            }

            int SpaceLeft => Capacity - _used;

            public void Flush()
            {
                if (_used > 0xb)
                {
                    _emf.Comment(_buf, 0, _used + 4);
                    _used = _continuing ? 0x10 : 0;
                }
            }

            public void WriteRecordHeader(int dataSize, int type, int flags)
            {
                uint total = (uint)dataSize + 12;
                if ((uint)SpaceLeft < total)
                {
                    if ((uint)SpaceLeft < Capacity)
                    {
                        Flush();
                        if (total < Capacity + 1) { Normal(dataSize, type, flags); return; }
                    }
                    // Too big for one comment: a continued record.
                    _continuing = true;
                    Le.W16(_buf, 4, type);
                    Le.W16(_buf, 6, flags | 0x8000);
                    Le.W32(_buf, 8, 0xfdfc);
                    Le.W32(_buf, 12, 0xfdf0);
                    Le.W32(_buf, 16, dataSize);
                    _used = 0x10;
                    return;
                }
                Normal(dataSize, type, flags);
            }

            void Normal(int dataSize, int type, int flags)
            {
                int o = 4 + _used;
                Le.W16(_buf, o, type);
                Le.W16(_buf, o + 2, flags);
                Le.W32(_buf, o + 4, dataSize + 12);
                Le.W32(_buf, o + 8, dataSize);
                _used += 12;
                if (_used > 0xfdfb) Flush();
            }

            public void Write(byte[] data, int off, int n)
            {
                if (n <= 0) return;
                int space = SpaceLeft;
                if (space < n)
                {
                    int chunk = space;
                    int rem = n;
                    while (true)
                    {
                        Buffer.BlockCopy(data, off, _buf, 4 + _used, chunk);
                        _used += chunk;
                        if (_used == Capacity) Flush();
                        rem -= chunk;
                        if (rem == 0) break;
                        off += chunk;
                        chunk = Math.Min(rem, 0xfdec);
                    }
                    return;
                }
                Buffer.BlockCopy(data, off, _buf, 4 + _used, n);
                _used += n;
                if (_used > 0xfdfb) Flush();
            }

            public void EndObjectRecord()
            {
                if (!_continuing) return;
                _continuing = false;
                if (_used < 0x11) _used = 0;
                else
                {
                    Le.W32(_buf, 8, _used);
                    Le.W32(_buf, 12, _used - 12);
                }
            }
        }

        readonly CommentStream _comment;

        bool HasStream => _comment != null;

        // MetafileRecorder::WriteRecordHeader: the record, and the device bounds it draws into.
        void WriteRecordHeader(int dataSize, int type, int flags, RectangleF? bounds)
        {
            _comment.WriteRecordHeader(dataSize, type, flags);
            if (bounds.HasValue)
            {
                RectangleF b = bounds.Value;
                _haveBounds = true;
                if (b.X < _minX) _minX = b.X;
                if (_maxX < b.Width + b.X) _maxX = b.Width + b.X;
                if (b.Y < _minY) _minY = b.Y;
                if (_maxY < b.Height + b.Y) _maxY = b.Height + b.Y;
            }
        }

        float _minX = float.MaxValue, _minY = float.MaxValue, _maxX = -float.MaxValue, _maxY = -float.MaxValue;
        bool _haveBounds;

        void Data(byte[] b) => _comment.Write(b, 0, b.Length);
        void Data(GpEmfPlusBuffer b) { byte[] a = b.ToArray(); _comment.Write(a, 0, a.Length); }

        // WriteGdiComment: a dual metafile ends the comment after each drawing record, so GDI's
        // rendering of the call follows it.
        void EndRecord()
        {
            if (_type == EmfType.EmfPlusDual) _comment.Flush();
        }

        // A record that draws nothing (state, transforms, clipping, comments) stays in the comment the
        // next records batch into, in a dual file as in an EMF+ only one.
        void EndStateRecord() { }

        void ZeroDataRecord(int type, int flags)
        {
            if (!HasStream) return;
            WriteRecordHeader(0, type, flags, null);
        }

        // MetafileRecorder::RecordHeader.
        void RecordHeader()
        {
            WriteRecordHeader(0x10, 0x4001, _type != EmfType.EmfPlusOnly ? 1 : 0, null);
            var b = new GpEmfPlusBuffer();
            b.I32(GpMetafileFormat.EmfPlusVersion);
            b.I32(_dev.IsDisplay ? 1 : 0);
            b.I32(_dev.LogPixelsX);
            b.I32(_dev.LogPixelsY);
            Data(b);
            _comment.Flush();
        }

        // ---- the object table: RecordObject / MetafileRecordObjectList ------------------------------

        sealed class Slot
        {
            public WeakReference Obj;
            public byte[] Data;
            public EmfPlusObjectType Type;
            public int Next = -1, Prev = -1;
        }

        readonly Slot[] _slots = new Slot[64];
        int _count, _head = -1, _tail = -1;

        void LinkAtTail(int i)
        {
            if (_tail >= 0) _slots[_tail].Next = i;
            _slots[i].Next = -1;
            _slots[i].Prev = _tail;
            _tail = i;
        }

        int InsertAt(int existing)
        {
            int idx = existing;
            if (idx == -1)
            {
                if (_count == 0)
                {
                    _count = 1; _head = 0; idx = 0;
                    _slots[0] = new Slot();
                    _slots[0].Prev = _tail;
                    _slots[0].Next = -1;
                    _tail = 0;
                    return idx;
                }
                idx = _head;
                if (_slots[idx].Obj == null || _count == 64)
                {
                    // Reuse the least recently used slot.
                    _head = _slots[idx].Next;
                    _slots[_head].Prev = -1;
                }
                else
                {
                    idx = _count;
                    _count++;
                    _slots[idx] = new Slot();
                }
                LinkAtTail(idx);
                return idx;
            }
            if (idx == _tail) return idx;
            if (idx == _head)
            {
                _head = _slots[idx].Next;
                _slots[_head].Prev = -1;
                LinkAtTail(idx);
                return idx;
            }
            _slots[_slots[idx].Prev].Next = _slots[idx].Next;
            _slots[_slots[idx].Next].Prev = _slots[idx].Prev;
            LinkAtTail(idx);
            return idx;
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>RecordObject: the object's slot, recording it when the table does not hold it as
        /// it stands. GDI+ knows an object by its pointer and its uid (bumped by every change); here
        /// it is the same object and the same serialisation.</summary>
        int RecordObject(object o)
        {
            if (o == null) return -1;
            EmfPlusObjectType type = GpEmfPlusObjects.TypeOf(o);
            byte[] data = GpEmfPlusObjects.Serialize(o);
            int found = -1;
            if (_count != 0)
            {
                for (int i = _tail; i != -1; i = _slots[i].Prev)
                {
                    if (_slots[i].Obj != null && ReferenceEquals(_slots[i].Obj.Target, o))
                    {
                        found = i;
                        if (_slots[i].Type == type && SameBytes(_slots[i].Data, data))
                        {
                            if (i != _tail)
                            {
                                // Move to the most recently used end.
                                if (i == _head) _head = _slots[i].Next;
                                else _slots[_slots[i].Prev].Next = _slots[i].Next;
                                _slots[_slots[i].Next].Prev = _slots[i].Prev;
                                _slots[i].Next = -1;
                                _slots[i].Prev = _tail;
                                _slots[_tail].Next = i;
                                _tail = i;
                            }
                            return i;
                        }
                        break;
                    }
                }
            }
            int idx = InsertAt(found);
            _slots[idx].Obj = new WeakReference(o);
            _slots[idx].Data = data;
            _slots[idx].Type = type;
            WriteObject(type, data, idx);
            return idx;
        }

        // MetafileRecorder::WriteObject.
        void WriteObject(EmfPlusObjectType type, byte[] data, int idx)
        {
            if (data == null || data.Length == 0) return;
            WriteRecordHeader(data.Length, 0x4008, idx | ((int)type << 8), null);
            _comment.Write(data, 0, data.Length);
            _comment.EndObjectRecord();
        }

        // GetBrushValueForRecording: a solid brush is its colour, inline (flag 0x8000).
        int BrushValue(Brush b, ref int flags)
        {
            if (b is SolidBrush s)
            {
                flags |= 0x8000;
                return s.Color.ToArgb();
            }
            return RecordObject(b);
        }

        // ---- the device state the bounds are computed in --------------------------------------------

        sealed class GState
        {
            public GpMat World = GpMat.Identity;
            public GraphicsUnit PageUnit = GraphicsUnit.Display;
            public float PageScale = 1f;
            public GpMat Container = GpMat.Identity;   // container to device
            // The application clip in device coordinates (null: infinite) and the clip the
            // container began under (DpContext +0x1c0 / +0x198); never changed in place.
            public GpRegion Clip;
            public DpRegion ContainerClip;
            // DpContext's rendering state, as GpGraphics::ResetState leaves it: a setter that does
            // not change it records nothing.
            public PixelOffsetMode PixelOffset = PixelOffsetMode.Default;
            public int Smoothing, TextHint, CompMode, CompQuality, Contrast = 4, Interp = 3, OriginX, OriginY;
            public GState Clone() => (GState)MemberwiseClone();
            /// <summary>A container starts with ResetState's rendering state (its origin kept).</summary>
            public void ResetRendering()
            {
                PixelOffset = PixelOffsetMode.Default;
                Smoothing = TextHint = CompMode = CompQuality = 0;
                Contrast = 4;
                Interp = 3;
            }
        }

        GState _state;
        readonly List<KeyValuePair<uint, GState>> _gstack = new List<KeyValuePair<uint, GState>>();

        // DpContext::GetPageMultipliers on the desktop device.
        void PageMultipliers(GraphicsUnit unit, float scale, out float mx, out float my)
        {
            float dx = DpiX, dy = DpiY;
            switch (unit)
            {
                case GraphicsUnit.Point: mx = dx / 72f * scale; my = dy / 72f * scale; break;
                case GraphicsUnit.Inch: mx = dx * scale; my = dy * scale; break;
                case GraphicsUnit.Document: mx = dx / 300f * scale; my = dy / 300f * scale; break;
                case GraphicsUnit.Millimeter: mx = dx / 25.4f * scale; my = dy / 25.4f * scale; break;
                default: mx = scale; my = scale; break;
            }
        }

        /// <summary>GpGraphics::GetImageDestPageSize on the metafile's context: an image's size in
        /// the current page units (a Pixel size at the image's resolution, against the desktop's).</summary>
        public void GetImageDestPageSize(float ix, float iy, float w, float h, GraphicsUnit unit, out float dw, out float dh)
        {
            PageMultipliers(_state.PageUnit, _state.PageScale, out float pmx, out float pmy);
            if (unit != GraphicsUnit.Pixel)
            {
                PageMultipliers(unit, 1f, out float mx, out float my);
                dh = (my * h) / pmy;
                dw = (mx * w) / pmx;
                return;
            }
            dh = (DpiY * h) / (pmy * iy);
            dw = (DpiX * w) / (pmx * ix);
        }

        /// <summary>World to device: the world transform, the page scaling, the container's.</summary>
        GpMat WorldToDevice
        {
            get
            {
                PageMultipliers(_state.PageUnit, _state.PageScale, out float mx, out float my);
                GpMat m = _state.World;
                var page = new GpMat(mx, 0, 0, my, 0, 0);
                return GpMat.Multiply(GpMat.Multiply(m, page), _state.Container);
            }
        }

        static RectangleF? Finish(float l, float t, float r, float b)
        {
            float w = r - l, h = b - t;
            if (w <= 1.1920928955078125e-07f) w = 0f;
            if (h <= 1.1920928955078125e-07f) h = 0f;
            return new RectangleF(l, t, w, h);
        }

        RectangleF? RectsBounds(RectangleF[] rects, Pen pen)
        {
            float l = rects[0].X, t = rects[0].Y, r = rects[0].Width + l, b = rects[0].Height + t;
            for (int i = 1; i < rects.Length; i++)
            {
                RectangleF q = rects[i];
                if (q.X <= l) l = q.X;
                if (r < q.Width + q.X) r = q.Width + q.X;
                if (q.Y <= t) t = q.Y;
                float qb = q.Height + q.Y;
                if (!(qb <= b)) b = qb;
            }
            if (pen != null)
            {
                // A world-unit pen widens the rectangles by its width before they are transformed.
                float pw = pen.Width;
                l -= pw; t -= pw; r = pw + r; b = pw + b;
            }
            GpMat m = WorldToDevice;
            m.TransformBounds(ref l, ref t, ref r, ref b);
            return Finish(l, t, r, b);
        }

        /// <summary>GpPath::GetBounds(world to device, pen): the control points' bounds through the
        /// matrix, grown by what the pen can reach beyond them (GpPath::GetBounds @18001c020).</summary>
        RectangleF? PathBounds(GraphicsPath path, Pen pen)
        {
            GpMat wtd = WorldToDevice;
            RectangleF r;
            using (Matrix m = wtd.ToMatrix())
                r = path.GetBounds(m);
            if (pen == null) return r;
            float w = DeviceWidth(pen, wtd);
            float ext = CapExtent(pen.StartCap, pen.CustomStartRef, w, 1f, 1f);
            if (ext <= w) ext = w;
            float end = CapExtent(pen.EndCap, pen.CustomEndRef, w, 2f, ext);
            if (end <= ext) end = ext;
            ext = end;
            if (path.PointCount >= 3)
            {
                float f = 0.5f;
                if (!IsOnePixelWideSolid(pen, wtd))
                {
                    f = pen.Alignment == PenAlignment.Center ? 0.5f : 1.0f;
                    float w2 = w;
                    if (pen.LineJoin == LineJoin.Miter || pen.LineJoin == LineJoin.MiterClipped)
                        w2 = pen.MiterLimit * w;
                    f *= w2;
                }
                if (f <= ext) f = ext;
                ext = f;
            }
            if (1.1920928955078125e-07f < r.Width || 1.1920928955078125e-07f < r.Height)
                r = new RectangleF(r.X - ext, r.Y - ext, r.Width + ext + ext, r.Height + ext + ext);
            return r;
        }

        /// <summary>A world-unit pen's device width: its width along the matrix's major axis, or
        /// 1.42 when it is thinner than that along the minor one.</summary>
        static float DeviceWidth(Pen pen, GpMat m)
        {
            if (!pen.Xform.IsIdentity)
            {
                var px = pen.Xform;
                m = GpMat.Multiply(new GpMat(px.M11, px.M12, px.M21, px.M22, px.Dx, px.Dy), m);
            }
            MajorMinor(m, out float major, out float minor);
            float width = pen.Width;
            return 1.42f <= width * minor ? width * major : 1.42f;
        }

        static void MajorMinor(GpMat m, out float major, out float minor)
        {
            float a = m.M12 * m.M12 + m.M11 * m.M11;
            float half = (a - (m.M21 * m.M21 + m.M22 * m.M22)) * 0.5f;
            float c = m.M21 * m.M11 + m.M12 * m.M22;
            c = c * c + half * half;
            if (0f < c) c = MathF.Sqrt(c);
            float mean = (m.M21 * m.M21 + a + m.M22 * m.M22) * 0.5f;
            float mj = MathF.Sqrt(mean + c), mn = MathF.Sqrt(mean - c);
            const float eps = 0.0005960464477539062f;
            major = eps <= mj ? mj : eps;
            minor = eps <= mn ? mn : eps;
        }

        /// <summary>How far a line cap reaches: an anchor (or custom with none) 2w + 2 at the start
        /// and 2w + 4 at the end, a custom cap its radius, any other half the width.</summary>
        static float CapExtent(LineCap cap, CustomLineCap custom, float w, float anchorPad, float _)
        {
            if (cap == LineCap.Custom)
            {
                if (custom == null) return w + anchorPad + w + anchorPad;
                return CustomCapRadius(custom, w, 1f);
            }
            if (((int)cap & 0xf0) != 0) return w + anchorPad + w + anchorPad;
            return w * 0.5f;
        }

        // GpCustomLineCap::GetRadius @18007b9c0: the furthest the cap's paths reach once
        // getTransformedPoints @18007c4f0 has scaled them by the larger of the pen width and the
        // scale (about the hot spots, which every constructor leaves at 0); the stroke path's box
        // corner grown by half its stroke (WidthScale x the pen width, at least the scale).
        static float CustomCapRadius(CustomLineCap cap, float penWidth, float scale)
        {
            if (penWidth <= 0f || cap.gp == null) return 0f;
            GpPath fill = cap.gp.FillPath, stroke = cap.gp.StrokePath;
            float ws = scale <= penWidth ? penWidth : scale;
            float best = 0f;
            if (fill != null)
                foreach (PointF p in fill.Points)
                {
                    float x = p.X * ws, y = p.Y * ws;
                    float d = x * x + y * y;
                    if (best <= d) best = d;
                }
            if (stroke != null && stroke.Count > 0)
            {
                float minx = float.MaxValue, miny = float.MaxValue;
                foreach (PointF p in stroke.Points) { minx = Math.Min(minx, p.X * ws); miny = Math.Min(miny, p.Y * ws); }
                float sw = cap.WidthScale * penWidth <= scale ? scale : cap.WidthScale * penWidth;
                float half = sw * 0.5f;
                LineJoin j = cap.StrokeJoin;
                if (j == LineJoin.Miter || j == LineJoin.MiterClipped) half = cap.gp.MiterLimit * sw;
                float a = (minx - half) * (minx - half), b = (miny - half) * (miny - half);
                float c = (miny + half) * (miny + half), d = (minx + half) * (minx + half);
                best = Math.Max(best, Math.Max(Math.Max(b + a, c + a), Math.Max(d + b, d + c)));
            }
            return 0f < best ? MathF.Sqrt(best) : best;
        }

        // DpPen::IsOnePixelWideSolid: solid, no anchor caps, at most 1.5 device pixels wide.
        static bool IsOnePixelWideSolid(Pen pen, GpMat m)
        {
            if (pen.DashStyle != DashStyle.Solid) return false;
            if (((int)pen.StartCap & 0xf0) != 0 || ((int)pen.EndCap & 0xf0) != 0) return false;
            float w = pen.Width;
            float k;
            if ((m.Complexity & ~1) == 0) k = 1f;
            else if ((m.Complexity & ~3) == 0)
            {
                k = Math.Abs(m.M22) < Math.Abs(m.M11) ? m.M11 : m.M22;
                k = Math.Abs(k);
            }
            else
            {
                MajorMinor(m, out float major, out float _);
                k = major;
            }
            return k * w <= 1.5f;
        }

        // ---- the recorder's API ------------------------------------------------------------------------

        void CheckOpen()
        {
            if (_ended) throw new InvalidOperationException("The metafile recording has ended.");
        }

        public void Clear(Color c)
        {
            CheckOpen();
            // GpGraphics::Clear passes the graphics' device bounds: (0, 0, 1, 1) for a metafile's.
            RectangleF b = new RectangleF(0, 0, 1, 1);
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4009, 0, b);
                var d = new GpEmfPlusBuffer(); d.Argb(c); Data(d);
                EndRecord();
            }
            GdiClear(c, b);
        }

        public void FillRects(Brush brush, RectangleF[] rects)
        {
            CheckOpen();
            if (rects == null || rects.Length == 0) return;
            // Only rectangles with no negative extent are drawn (and recorded).
            var keep = new List<RectangleF>();
            foreach (var r in rects)
                if (r.X <= r.Width + r.X && r.Y <= r.Y + r.Height) keep.Add(r);
            if (keep.Count == 0) return;
            RectangleF[] rs = keep.ToArray();
            RectangleF? bounds = RectsBounds(rs, null);
            if (HasStream)
            {
                byte[] rd = GpEmfPlusObjects.RectData(rs, out int rflags);
                int flags = rflags;
                int bv = BrushValue(brush, ref flags);
                WriteRecordHeader(rd.Length + 8, 0x400a, flags, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(bv); d.I32(rs.Length); d.Bytes(rd); Data(d);
                EndRecord();
            }
            GdiFillRects(brush, rs);
        }

        public void DrawRects(Pen pen, RectangleF[] rects)
        {
            CheckOpen();
            if (rects == null || rects.Length == 0) return;
            RectangleF? bounds = RectsBounds(rects, pen);
            if (HasStream)
            {
                byte[] rd = GpEmfPlusObjects.RectData(rects, out int rflags);
                int pid = RecordObject(pen);
                WriteRecordHeader(rd.Length + 4, 0x400b, rflags | pid, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(rects.Length); d.Bytes(rd); Data(d);
                EndRecord();
            }
            GdiDrawRects(pen, rects);
        }

        static GraphicsPath PolygonPath(PointF[] pts, FillMode mode)
        {
            var p = new GraphicsPath(mode);
            p.AddPolygon(pts);
            return p;
        }

        public void FillPolygon(Brush brush, PointF[] pts, FillMode mode)
        {
            CheckOpen();
            if (pts == null || pts.Length < 3) return;
            RectangleF? bounds;
            using (var path = PolygonPath(pts, mode))
                bounds = PathBounds(path, null);
            if (HasStream)
            {
                byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                int flags = pflags;
                int bv = BrushValue(brush, ref flags);
                if (mode == FillMode.Winding) flags |= 0x2000;
                WriteRecordHeader(pd.Length + 8, 0x400c, flags, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(bv); d.I32(pts.Length); d.Bytes(pd); Data(d);
                EndRecord();
            }
            using (var path = PolygonPath(pts, mode)) GdiFillPath(brush, path);
        }

        public void DrawLines(Pen pen, PointF[] pts, bool closed)
        {
            CheckOpen();
            if (pts == null || pts.Length < 2) return;
            RectangleF? bounds;
            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                if (closed) path.CloseFigure();
                bounds = PathBounds(path, pen);
                if (HasStream)
                {
                    byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                    int pid = RecordObject(pen);
                    int flags = pflags | pid;
                    if (closed) flags |= 0x2000;
                    WriteRecordHeader(pd.Length + 4, 0x400d, flags, bounds);
                    var d = new GpEmfPlusBuffer(); d.I32(pts.Length); d.Bytes(pd); Data(d);
                    EndRecord();
                }
                GdiDrawPath(pen, path);
            }
        }

        void RectRecord(int type, Pen pen, Brush brush, RectangleF r, float? start, float? sweep, GraphicsPath shape)
        {
            RectangleF? bounds = PathBounds(shape, pen);
            if (!HasStream) return;
            byte[] rd = GpEmfPlusObjects.RectData(new[] { r }, out int rflags);
            int flags = rflags;
            var d = new GpEmfPlusBuffer();
            int extra = 0;
            if (brush != null)
            {
                int bv = BrushValue(brush, ref flags);
                d.I32(bv);
                extra += 4;
            }
            else flags |= RecordObject(pen);
            if (start.HasValue) { d.F(start.Value); d.F(sweep.Value); extra += 8; }
            d.Bytes(rd);
            WriteRecordHeader(rd.Length + extra, type, flags, bounds);
            Data(d);
            EndRecord();
        }

        public void FillEllipse(Brush brush, RectangleF r)
        {
            CheckOpen();
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(r);
                if (p.PointCount == 0) return;
                RectRecord(0x400e, null, brush, r, null, null, p);
                GdiFillPath(brush, p);
            }
        }

        public void DrawEllipse(Pen pen, RectangleF r)
        {
            CheckOpen();
            using (var p = new GraphicsPath())
            {
                p.AddEllipse(r);
                if (p.PointCount == 0) return;
                RectRecord(0x400f, pen, null, r, null, null, p);
                GdiDrawPath(pen, p);
            }
        }

        public void FillPie(Brush brush, RectangleF r, float start, float sweep)
        {
            CheckOpen();
            using (var p = new GraphicsPath())
            {
                p.AddPie(r.X, r.Y, r.Width, r.Height, start, sweep);
                if (p.PointCount == 0) return;
                RectRecord(0x4010, null, brush, r, start, sweep, p);
                GdiFillPath(brush, p);
            }
        }

        public void DrawPie(Pen pen, RectangleF r, float start, float sweep)
        {
            CheckOpen();
            using (var p = new GraphicsPath())
            {
                p.AddPie(r.X, r.Y, r.Width, r.Height, start, sweep);
                if (p.PointCount == 0) return;
                RectRecord(0x4011, pen, null, r, start, sweep, p);
                GdiDrawPath(pen, p);
            }
        }

        public void DrawArc(Pen pen, RectangleF r, float start, float sweep)
        {
            CheckOpen();
            using (var p = new GraphicsPath())
            {
                p.AddArc(r, start, sweep);
                if (p.PointCount == 0) return;
                RectRecord(0x4012, pen, null, r, start, sweep, p);
                GdiDrawPath(pen, p);
            }
        }

        // UseDriverRects + IsRectangle: a path that is one axis-aligned rectangle (under a transform
        // with no rotation) is filled as FillRects.
        static bool IsAxisRect(GraphicsPath path, out RectangleF rect)
        {
            rect = RectangleF.Empty;
            if (path.PointCount != 4) return false;
            byte[] t = path.PathTypes;
            PointF[] p = path.PathPoints;
            if (t[0] != 0 || (t[1] & 7) != 1 || (t[2] & 7) != 1 || (t[3] & 7) != 1) return false;
            bool h0 = p[0].Y == p[1].Y && p[1].X == p[2].X && p[2].Y == p[3].Y && p[3].X == p[0].X;
            bool v0 = p[0].X == p[1].X && p[1].Y == p[2].Y && p[2].X == p[3].X && p[3].Y == p[0].Y;
            if (!h0 && !v0) return false;
            float l = Math.Min(Math.Min(p[0].X, p[1].X), Math.Min(p[2].X, p[3].X));
            float r = Math.Max(Math.Max(p[0].X, p[1].X), Math.Max(p[2].X, p[3].X));
            float tp = Math.Min(Math.Min(p[0].Y, p[1].Y), Math.Min(p[2].Y, p[3].Y));
            float b = Math.Max(Math.Max(p[0].Y, p[1].Y), Math.Max(p[2].Y, p[3].Y));
            rect = new RectangleF(l, tp, r - l, b - tp);
            return true;
        }

        public void FillPath(Brush brush, GraphicsPath path)
        {
            CheckOpen();
            if (path == null || path.PointCount < 3) return;
            if ((WorldToDevice.Complexity & ~3) == 0 && IsAxisRect(path, out RectangleF rect))
            {
                FillRects(brush, new[] { rect });
                return;
            }
            RectangleF? bounds = PathBounds(path, null);
            if (HasStream)
            {
                int flags = 0;
                int bv = BrushValue(brush, ref flags);
                int pid = RecordObject(path);
                WriteRecordHeader(4, 0x4014, flags | pid, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(bv); Data(d);
                EndRecord();
            }
            GdiFillPath(brush, path);
        }

        public void DrawPath(Pen pen, GraphicsPath path)
        {
            CheckOpen();
            if (path == null || path.PointCount < 1) return;
            RectangleF? bounds = PathBounds(path, pen);
            if (HasStream)
            {
                int penId = RecordObject(pen);
                int pid = RecordObject(path);
                WriteRecordHeader(4, 0x4015, pid, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(penId); Data(d);
                EndRecord();
            }
            GdiDrawPath(pen, path);
        }

        public void FillClosedCurve(Brush brush, PointF[] pts, float tension, FillMode mode)
        {
            CheckOpen();
            if (pts == null || pts.Length < 3) return;
            using (var path = new GraphicsPath(mode))
            {
                path.AddClosedCurve(pts, tension);
                RectangleF? bounds = PathBounds(path, null);
                if (HasStream)
                {
                    byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                    int flags = pflags;
                    int bv = BrushValue(brush, ref flags);
                    if (mode == FillMode.Winding) flags |= 0x2000;
                    WriteRecordHeader(pd.Length + 12, 0x4016, flags, bounds);
                    var d = new GpEmfPlusBuffer(); d.I32(bv); d.F(tension); d.I32(pts.Length); d.Bytes(pd); Data(d);
                    EndRecord();
                }
                GdiFillPath(brush, path);
            }
        }

        public void DrawClosedCurve(Pen pen, PointF[] pts, float tension)
        {
            CheckOpen();
            if (pts == null || pts.Length < 3) return;
            using (var path = new GraphicsPath())
            {
                path.AddClosedCurve(pts, tension);
                RectangleF? bounds = PathBounds(path, pen);
                if (HasStream)
                {
                    byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                    int pid = RecordObject(pen);
                    WriteRecordHeader(pd.Length + 8, 0x4017, pflags | pid, bounds);
                    var d = new GpEmfPlusBuffer(); d.F(tension); d.I32(pts.Length); d.Bytes(pd); Data(d);
                    EndRecord();
                }
                GdiDrawPath(pen, path);
            }
        }

        public void DrawCurve(Pen pen, PointF[] pts, int offset, int numberOfSegments, float tension)
        {
            CheckOpen();
            if (pts == null || pts.Length < 2) return;
            using (var path = new GraphicsPath())
            {
                path.AddCurve(pts, offset, numberOfSegments, tension);
                RectangleF? bounds = PathBounds(path, pen);
                if (HasStream)
                {
                    byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                    int pid = RecordObject(pen);
                    WriteRecordHeader(pd.Length + 16, 0x4018, pflags | pid, bounds);
                    var d = new GpEmfPlusBuffer(); d.F(tension); d.I32(offset); d.I32(numberOfSegments); d.I32(pts.Length); d.Bytes(pd); Data(d);
                    EndRecord();
                }
                GdiDrawPath(pen, path);
            }
        }

        public void DrawBeziers(Pen pen, PointF[] pts)
        {
            CheckOpen();
            if (pts == null || pts.Length < 4) return;
            using (var path = new GraphicsPath())
            {
                path.AddBeziers(pts);
                RectangleF? bounds = PathBounds(path, pen);
                if (HasStream)
                {
                    byte[] pd = GpEmfPlusObjects.PointData(pts, 0, pts.Length, out int pflags);
                    int pid = RecordObject(pen);
                    WriteRecordHeader(pd.Length + 4, 0x4019, pflags | pid, bounds);
                    var d = new GpEmfPlusBuffer(); d.I32(pts.Length); d.Bytes(pd); Data(d);
                    EndRecord();
                }
                GdiDrawPath(pen, path);
            }
        }

        public void FillRegion(Brush brush, Region region)
        {
            CheckOpen();
            if (region == null) return;
            // An empty region draws nothing; any other's bounds are clipped to the metafile's.
            RectangleF rb;
            using (var g = GpRegionProbe.Graphics())
            {
                using (Matrix m = WorldToDevice.ToMatrix())
                using (Region dev = region.Clone())
                {
                    dev.Transform(m);
                    if (dev.IsEmpty(g)) return;
                    rb = dev.IsInfinite(g) ? new RectangleF(-4194304f, -4194304f, 8388608f, 8388608f) : dev.GetBounds(g);
                }
            }
            RectangleF mb = MetafileDeviceBounds();
            float l = Math.Max(rb.X, mb.X), t = Math.Max(rb.Y, mb.Y);
            float r = Math.Min(rb.X + rb.Width, mb.X + mb.Width), b = Math.Min(rb.Y + rb.Height, mb.Y + mb.Height);
            var bounds = new RectangleF(l, t, r - l, b - t);
            if (HasStream)
            {
                int flags = 0;
                int bv = BrushValue(brush, ref flags);
                int rid = RecordObject(region);
                WriteRecordHeader(4, 0x4013, flags | rid, bounds);
                var d = new GpEmfPlusBuffer(); d.I32(bv); Data(d);
                EndRecord();
            }
            GdiFillRegion(brush, region, bounds);
        }

        /// <summary>GetMetafileBounds, as FillRegion uses it: whole pixels, one wider and taller.</summary>
        RectangleF MetafileDeviceBounds()
        {
            RectangleF m = _metafileBounds;
            int x = GpMetafileFormat.Trunc(m.X), y = GpMetafileFormat.Trunc(m.Y);
            int r = (int)Math.Ceiling(m.Width + m.X), b = (int)Math.Ceiling(m.Height + m.Y);
            int w = r - x + 1, h = b - y + 1;
            return new RectangleF(x, y, w, h);
        }

        // GpGraphics::DrawImage(rect, rect): the rectangle-to-rectangle matrix; DrawImage(points,
        // rect): the parallelogram's. Either records as DrawImagePoints, the source corners
        // transformed by that matrix.
        public void DrawImage(Image image, RectangleF dest, RectangleF src, GraphicsUnit srcUnit, ImageAttributes ia)
        {
            CheckOpen();
            if (image == null) throw new ArgumentNullException("image");
            float sw = src.Width + src.X, sh = src.Height + src.Y;
            GpMat m = GpMat.Identity;
            if (src.X != sw && src.Y != sh)
            {
                m.M11 = (dest.Width + dest.X - dest.X) / (sw - src.X);
                m.M22 = (dest.Height + dest.Y - dest.Y) / (sh - src.Y);
                m.Dx = (dest.Width + dest.X) - m.M11 * sw;
                m.Dy = (dest.Height + dest.Y) - m.M22 * sh;
                m.Complexity = m.ComputeComplexity();
            }
            RectangleF s = src;
            if (s.Width < 0f) { s.X = sw; s.Width = -src.Width; }
            if (s.Height < 0f) { s.Y = sh; s.Height = -src.Height; }
            DrawImageCore(image, s, m, srcUnit, ia);
        }

        public void DrawImagePoints(Image image, PointF[] dest3, RectangleF src, GraphicsUnit srcUnit, ImageAttributes ia)
        {
            CheckOpen();
            if (image == null) throw new ArgumentNullException("image");
            if (dest3 == null || dest3.Length != 3) throw new ArgumentException("Parameter is not valid.");
            if (!GpMat.InferAffine(dest3, src, out GpMat m)) return;
            RectangleF s = src;
            if (s.Width < 0f) { s.X = src.Width + src.X; s.Width = -src.Width; }
            if (s.Height < 0f) { s.Y = src.Height + src.Y; s.Height = -src.Height; }
            DrawImageCore(image, s, m, srcUnit, ia);
        }

        // GpGraphics::DrawImage(image, src, matrix, ...) -> RecordDrawImage.
        void DrawImageCore(Image image, RectangleF src, GpMat m, GraphicsUnit srcUnit, ImageAttributes ia)
        {
            // A source in another unit is turned into pixels at the image's resolution.
            if (srcUnit != GraphicsUnit.Pixel && srcUnit != GraphicsUnit.World && srcUnit != GraphicsUnit.Display)
            {
                float rx = image.HorizontalResolution, ry = image.VerticalResolution;
                float fx, fy;
                switch (srcUnit)
                {
                    case GraphicsUnit.Point: fx = rx / 72f; fy = ry / 72f; break;
                    case GraphicsUnit.Document: fx = rx / 300f; fy = ry / 300f; break;
                    case GraphicsUnit.Millimeter: fx = rx / 25.4f; fy = ry / 25.4f; break;
                    default: fx = rx; fy = ry; break;
                }
                src = new RectangleF(src.X * fx, src.Y * fy, src.Width * fx, src.Height * fy);
                m.Scale(1f / fx, 1f / fy, MatrixOrder.Prepend);
            }
            bool isMetafile = image is Metafile;
            PixelOffsetMode pom = _state.PixelOffset;
            if ((pom == PixelOffsetMode.HighQuality || pom == PixelOffsetMode.Half) && !isMetafile)
            {
                src = new RectangleF(src.X - 0.5f, src.Y - 0.5f, src.Width, src.Height);
                m.Translate(0.5f, 0.5f, MatrixOrder.Prepend);
            }
            // The device bounds: the source rectangle under the image matrix and world to device.
            GpMat full = GpMat.Multiply(m, WorldToDevice);
            float l = src.X, t = src.Y, r = src.X + src.Width, b = src.Y + src.Height;
            full.TransformBounds(ref l, ref t, ref r, ref b);
            RectangleF? bounds = Finish(l, t, r, b);
            if (HasStream)
            {
                var corners = new[] { new PointF(src.X, src.Y), new PointF(src.Width + src.X, src.Y), new PointF(src.X, src.Height + src.Y) };
                m.Transform(corners);
                byte[] pd = GpEmfPlusObjects.PointData(corners, 0, 3, out int pflags);
                int iid = RecordObject(image);
                int aid = RecordObject(ia);
                WriteRecordHeader(pd.Length + 0x1c, 0x401b, pflags | iid, bounds);
                var d = new GpEmfPlusBuffer();
                d.I32(aid); d.I32((int)GraphicsUnit.Pixel); d.Rect(src); d.I32(3); d.Bytes(pd);
                Data(d);
                EndRecord();
            }
            GdiDrawImage(image, src, m, ia);
        }

        public void DrawString(string s, Font font, RectangleF layout, StringFormat format, Brush brush)
        {
            CheckOpen();
            if (s == null || s.Length == 0) return;
            RectangleF? bounds = StringBounds(s, font, layout, format);
            if (HasStream)
            {
                int flags = 0;
                int fid = RecordObject(font);
                flags |= fid;
                int sid = RecordObject(format);
                int bv = BrushValue(brush, ref flags);
                int n = s.Length;
                WriteRecordHeader((n * 2 + 0x1f) & ~3, 0x401c, flags, bounds);
                var d = new GpEmfPlusBuffer();
                d.I32(bv); d.I32(sid); d.I32(n); d.Rect(layout);
                foreach (char c in s) d.I16(c);
                if ((n & 1) != 0) d.I16(0);
                Data(d);
                EndRecord();
            }
            GdiDrawString(s, font, layout, format, brush);
        }

        public void DrawDriverString(ushort[] text, Font font, Brush brush, PointF[] positions, int flags, Matrix matrix)
        {
            CheckOpen();
            if (text == null || text.Length < 1) return;
            int n = text.Length;
            RectangleF? bounds = DriverStringBounds(text, font, positions, flags, matrix);
            if (HasStream)
            {
                int rflags = 0;
                int fid = RecordObject(font);
                rflags |= fid;
                int bv = BrushValue(brush, ref rflags);
                int size = matrix == null ? n * 10 + 0x10 : n * 10 + 0x28;
                WriteRecordHeader((size + 3) & ~3, 0x4036, rflags, bounds);
                var d = new GpEmfPlusBuffer();
                d.I32(bv); d.I32(flags); d.I32(matrix != null ? 1 : 0); d.I32(n);
                foreach (ushort g in text) d.I16(g);
                for (int i = 0; i < n; i++)
                {
                    PointF p = positions != null && i < positions.Length ? positions[i] : PointF.Empty;
                    d.F(p.X); d.F(p.Y);
                }
                if (matrix != null) d.Matrix(matrix);
                if ((n & 1) != 0) d.I16(0);
                Data(d);
                EndRecord();
            }
            GdiDrawDriverString(text, font, brush, positions, flags, matrix);
        }

        // ---- transforms ----------------------------------------------------------------------------

        public void SetWorldTransform(Matrix m)
        {
            CheckOpen();
            _state.World = GpMat.From(m);
            if (!HasStream) { GdiTransformChanged(); return; }
            WriteRecordHeader(0x18, 0x402a, 0, null);
            var d = new GpEmfPlusBuffer(); d.Matrix(m); Data(d);
            EndStateRecord();
            GdiTransformChanged();
        }

        public void ResetWorldTransform()
        {
            CheckOpen();
            _state.World = GpMat.Identity;
            ZeroDataRecord(0x402b, 0);
            if (HasStream) EndStateRecord();
            GdiTransformChanged();
        }

        public void MultiplyWorldTransform(Matrix m, MatrixOrder order)
        {
            CheckOpen();
            _state.World.Multiply(GpMat.From(m), order);
            if (HasStream)
            {
                WriteRecordHeader(0x18, 0x402c, order == MatrixOrder.Append ? 0x2000 : 0, null);
                var d = new GpEmfPlusBuffer(); d.Matrix(m); Data(d);
                EndStateRecord();
            }
            GdiTransformChanged();
        }

        public void TranslateWorldTransform(float dx, float dy, MatrixOrder order)
        {
            CheckOpen();
            _state.World.Translate(dx, dy, order);
            if (HasStream)
            {
                WriteRecordHeader(8, 0x402d, order == MatrixOrder.Append ? 0x2000 : 0, null);
                var d = new GpEmfPlusBuffer(); d.F(dx); d.F(dy); Data(d);
                EndStateRecord();
            }
            GdiTransformChanged();
        }

        public void ScaleWorldTransform(float sx, float sy, MatrixOrder order)
        {
            CheckOpen();
            _state.World.Scale(sx, sy, order);
            if (HasStream)
            {
                WriteRecordHeader(8, 0x402e, order == MatrixOrder.Append ? 0x2000 : 0, null);
                var d = new GpEmfPlusBuffer(); d.F(sx); d.F(sy); Data(d);
                EndStateRecord();
            }
            GdiTransformChanged();
        }

        public void RotateWorldTransform(float angle, MatrixOrder order)
        {
            CheckOpen();
            _state.World.Rotate(angle, order);
            if (HasStream)
            {
                WriteRecordHeader(4, 0x402f, order == MatrixOrder.Append ? 0x2000 : 0, null);
                var d = new GpEmfPlusBuffer(); d.F(angle); Data(d);
                EndStateRecord();
            }
            GdiTransformChanged();
        }

        public void SetPageTransform(GraphicsUnit unit, float scale)
        {
            CheckOpen();
            // GpGraphics::SetPageTransform: a unit from Display to Millimeter, a sane scale; the
            // same transform again records nothing.
            if ((int)unit < 1 || (int)unit > 6 || !(scale >= 1e-9f) || scale > 1e9f)
                throw new ArgumentException("Parameter is not valid.");
            if (unit == _state.PageUnit && scale == _state.PageScale) return;
            _state.PageUnit = unit;
            _state.PageScale = scale;
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4030, (int)unit, null);
                var d = new GpEmfPlusBuffer(); d.F(scale); Data(d);
                EndStateRecord();
            }
            GdiTransformChanged();
        }

        // ---- clipping --------------------------------------------------------------------------------

        public void SetClipRect(RectangleF r, CombineMode mode)
        {
            CheckOpen();
            if (HasStream)
            {
                WriteRecordHeader(0x10, 0x4032, (int)mode << 8, null);
                var d = new GpEmfPlusBuffer(); d.Rect(r); Data(d);
                EndStateRecord();
            }
            GdiClipRect(r, mode);
        }

        public void SetClipPath(GraphicsPath path, CombineMode mode)
        {
            CheckOpen();
            if (HasStream)
            {
                int pid = RecordObject(path);
                WriteRecordHeader(0, 0x4033, pid | ((int)mode << 8), null);
                EndStateRecord();
            }
            GdiClipPath(path, mode);
        }

        public void SetClipRegion(Region region, CombineMode mode)
        {
            CheckOpen();
            if (HasStream)
            {
                int rid = RecordObject(region);
                WriteRecordHeader(0, 0x4034, rid | ((int)mode << 8), null);
                EndStateRecord();
            }
            GdiClipRegion(region, mode);
        }

        /// <summary>The EmfPlusSetClipPath GDI+'s own CombineClip records (DrvDrawImage), the clip itself
        /// left to the caller.</summary>
        void RecordClipPath(GraphicsPath path, CombineMode mode)
        {
            if (!HasStream) return;
            int pid = RecordObject(path);
            WriteRecordHeader(0, 0x4033, pid | ((int)mode << 8), null);
            EndStateRecord();
        }

        /// <summary>The EmfPlusSetClipRegion of a GpGraphics::SetClip GDI+ makes itself.</summary>
        void RecordClipRegion(Region region, CombineMode mode)
        {
            if (!HasStream) return;
            int rid = RecordObject(region);
            WriteRecordHeader(0, 0x4034, rid | ((int)mode << 8), null);
            EndStateRecord();
        }

        /// <summary>GpGraphics::GetClip: the device clip back in world space.</summary>
        Region WorldClip(GpRegion device)
        {
            GpRegion w = device.Clone();
            GpMatrix inv = DeviceMatrix;
            if (inv.Invert()) w.Transform(inv);
            return new Region(w);
        }

        public void ResetClip()
        {
            CheckOpen();
            ZeroDataRecord(0x4031, 0);
            if (HasStream) EndStateRecord();
            GdiResetClip();
        }

        public void OffsetClip(float dx, float dy)
        {
            CheckOpen();
            if (HasStream)
            {
                WriteRecordHeader(8, 0x4035, 0, null);
                var d = new GpEmfPlusBuffer(); d.F(dx); d.F(dy); Data(d);
                EndStateRecord();
            }
            GdiOffsetClip(dx, dy);
        }

        // ---- state -----------------------------------------------------------------------------------

        // The save/container stack GDI+ keeps (+0x50): the caller's ids; a record names its depth.
        readonly List<uint> _ids = new List<uint>();
        int _maxContainerDepth;

        public void Save(uint stackIndex)
        {
            CheckOpen();
            int depth = _ids.Count;
            _ids.Add(stackIndex);
            _gstack.Add(new KeyValuePair<uint, GState>(stackIndex, _state.Clone()));
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4025, 0, null);
                var d = new GpEmfPlusBuffer(); d.I32(depth); Data(d);
                EndStateRecord();
            }
            GdiSave();
        }

        bool Pop(uint stackIndex, out int depth)
        {
            depth = -1;
            for (int i = _ids.Count - 1; i >= 0; i--)
            {
                if (_ids[i] == stackIndex)
                {
                    depth = i;
                    _state = _gstack[i].Value.Clone();
                    _ids.RemoveRange(i, _ids.Count - i);
                    _gstack.RemoveRange(i, _gstack.Count - i);
                    return true;
                }
            }
            return false;
        }

        public void Restore(uint stackIndex)
        {
            CheckOpen();
            if (!Pop(stackIndex, out int depth)) return;
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4026, 0, null);
                var d = new GpEmfPlusBuffer(); d.I32(depth); Data(d);
                EndStateRecord();
            }
            GdiRestore(depth);
        }

        public void BeginContainer(RectangleF dst, RectangleF src, GraphicsUnit unit, uint stackIndex)
        {
            CheckOpen();
            int depth = _ids.Count;
            if (_maxContainerDepth <= depth) _maxContainerDepth = depth + 1;
            _ids.Add(stackIndex);
            _gstack.Add(new KeyValuePair<uint, GState>(stackIndex, _state.Clone()));
            // GpGraphics::BeginContainer: src (in its unit) to dst, then the old world to device.
            PageMultipliers(unit, 1f, out float mx, out float my);
            var s = new RectangleF(src.X * mx, src.Y * my, src.Width * mx, src.Height * my);
            GpMat wtd = WorldToDevice;
            if (GpMat.InferAffine(dst, s, out GpMat c))
            {
                _state.Container = GpMat.Multiply(c, wtd);
                _state.World = GpMat.Identity;
                _state.PageUnit = GraphicsUnit.Display;
                _state.PageScale = 1f;
            }
            _state.ResetRendering();
            BeginContainerClip();
            if (HasStream)
            {
                WriteRecordHeader(0x24, 0x4027, (int)unit, null);
                var d = new GpEmfPlusBuffer(); d.Rect(dst); d.Rect(src); d.I32(depth); Data(d);
                EndStateRecord();
            }
            GdiSave();
        }

        public void BeginContainerNoParams(uint stackIndex)
        {
            CheckOpen();
            int depth = _ids.Count;
            if (_maxContainerDepth <= depth) _maxContainerDepth = depth + 1;
            _ids.Add(stackIndex);
            _gstack.Add(new KeyValuePair<uint, GState>(stackIndex, _state.Clone()));
            GpMat wtd = WorldToDevice;
            _state.Container = wtd;
            _state.World = GpMat.Identity;
            _state.PageUnit = GraphicsUnit.Display;
            _state.PageScale = 1f;
            _state.ResetRendering();
            BeginContainerClip();
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4028, 0, null);
                var d = new GpEmfPlusBuffer(); d.I32(depth); Data(d);
                EndStateRecord();
            }
            GdiSave();
        }

        public void EndContainer(uint stackIndex)
        {
            CheckOpen();
            if (!Pop(stackIndex, out int depth)) return;
            if (HasStream)
            {
                WriteRecordHeader(4, 0x4029, 0, null);
                var d = new GpEmfPlusBuffer(); d.I32(depth); Data(d);
                EndStateRecord();
            }
            GdiRestore(depth);
        }

        // ---- rendering hints -------------------------------------------------------------------------

        // Each records only a change (GpGraphics::SetAntiAliasMode @180005a88 and its siblings).
        public void SetAntiAliasMode(SmoothingMode mode)
        {
            CheckOpen();
            int m = (int)mode;
            if (m == _state.Smoothing) return;
            _state.Smoothing = m;
            ZeroDataRecord(0x401e, (((m & ~3) != 0 || m == 2) ? 1 : 0) | (m << 1));
            if (HasStream) EndStateRecord();
        }

        public void SetTextRenderingHint(TextRenderingHint hint)
        {
            CheckOpen();
            if ((int)hint == _state.TextHint) return;
            _state.TextHint = (int)hint;
            ZeroDataRecord(0x401f, (int)hint);
            if (HasStream) EndStateRecord();
        }

        public void SetTextContrast(int contrast)
        {
            CheckOpen();
            if ((uint)contrast >= 0xd) throw new ArgumentException("Parameter is not valid.");
            if (contrast == _state.Contrast) return;
            _state.Contrast = contrast;
            ZeroDataRecord(0x4020, contrast);
            if (HasStream) EndStateRecord();
        }

        public void SetInterpolationMode(InterpolationMode mode)
        {
            CheckOpen();
            if ((int)mode == _state.Interp) return;
            _state.Interp = (int)mode;
            ZeroDataRecord(0x4021, (int)mode);
            if (HasStream) EndStateRecord();
        }

        public void SetPixelOffsetMode(PixelOffsetMode mode)
        {
            CheckOpen();
            if (mode == _state.PixelOffset) return;
            _state.PixelOffset = mode;
            ZeroDataRecord(0x4022, (int)mode);
            if (HasStream) EndStateRecord();
        }

        public void SetCompositingMode(CompositingMode mode)
        {
            CheckOpen();
            if ((int)mode == _state.CompMode) return;
            _state.CompMode = (int)mode;
            ZeroDataRecord(0x4023, (int)mode);
            if (HasStream) EndStateRecord();
        }

        public void SetCompositingQuality(CompositingQuality q)
        {
            CheckOpen();
            if ((int)q == _state.CompQuality) return;
            _state.CompQuality = (int)q;
            ZeroDataRecord(0x4024, (int)q);
            if (HasStream) EndStateRecord();
        }

        public void SetRenderingOrigin(int x, int y)
        {
            CheckOpen();
            if (x == _state.OriginX && y == _state.OriginY) return;
            _state.OriginX = x;
            _state.OriginY = y;
            if (!HasStream) return;
            WriteRecordHeader(8, 0x401d, 0, null);
            var d = new GpEmfPlusBuffer(); d.I32(x); d.I32(y); Data(d);
            EndStateRecord();
        }

        /// <summary>RecordComment: an EmfPlusComment, or a plain GDI comment in an EMF-only file.</summary>
        public void Comment(byte[] data)
        {
            CheckOpen();
            if (data == null || data.Length == 0) throw new ArgumentException("Parameter is not valid.");
            if (!HasStream)
            {
                Emf.Comment(data, 0, data.Length);
                return;
            }
            int n = data.Length;
            int padded = (n + 3) & ~3;
            WriteRecordHeader(padded, 0x4003, padded - n, null);
            _comment.Write(data, 0, n);
            if (padded > n) _comment.Write(new byte[padded - n], 0, padded - n);
            EndStateRecord();
        }

        /// <summary>Graphics.Flush: GDI+ records nothing for it.</summary>
        public void Flush(FlushIntention intention)
        {
            CheckOpen();
        }

        bool _hdcOut;
        IntPtr _userDc;

        /// <summary>GpGraphics::GetHdc @180015e18 on a metafile: the HDC's saved level put back, then
        /// RecordGetDC (the EmfPlusGetDC record, the comment flushed so the GDI drawing a caller does
        /// next lands after it), and the metafile's own HDC handed out. On Windows that is a GDI EMF DC on
        /// the screen whose records ReleaseHdc splices into this recording; elsewhere there is no GDI
        /// and the handle is zero (Graphics makes it one FromHdc maps back to the recording).</summary>
        public IntPtr GetHdc()
        {
            CheckOpen();
            if (_hdcOut) throw new InvalidOperationException("Object is currently in use elsewhere.");
            lock (GpMetaDriverState.Lock) ResetHdc();
            if (HasStream)
            {
                WriteRecordHeader(0, 0x4004, 0, null);
                _comment.Flush();
            }
            _hdcOut = true;
            if (OperatingSystem.IsWindows())
                _userDc = GpWindowsMetafile.CreateEmfDc();
            return _userDc;
        }

        public void ReleaseHdc()
        {
            if (!_hdcOut) return;
            _hdcOut = false;
            if (_userDc == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
            byte[] emf = GpWindowsMetafile.CloseEmfDc(_userDc);
            _userDc = IntPtr.Zero;
            if (emf != null)
                lock (GpMetaDriverState.Lock) Dc.Splice(emf);
        }

        // ---- the end: MetafileRecorder::EndRecording ---------------------------------------------------

        public void End()
        {
            if (_ended) return;
            _ended = true;
            if (HasStream)
            {
                _comment.Flush();
                if (_haveBounds)
                {
                    int l = RasterizerCeiling(_minX), t = RasterizerCeiling(_minY);
                    int r = RasterizerCeiling(_maxX), b = RasterizerCeiling(_maxY);
                    if (l < r && t < b) NoOpPatBlt(l, t, r - l, b - t);
                }
                WriteRecordHeader(0, 0x4002, 0, null);
                _comment.Flush();
            }
            GdiEnd();
            byte[] emf = Emf.Finish();
            // The header GDI+ keeps: the EMF's, with the bounds computed at the recording's dpi.
            GpMetafileHeader h = Header.Clone();
            h.Raw = new byte[88];
            Buffer.BlockCopy(emf, 0, h.Raw, 0, 88);
            h.Size = emf.Length;
            float sx = h.DpiX / 2540f, sy = h.DpiY / 2540f;
            int fl = Le.I32(emf, 24), ft = Le.I32(emf, 28), fr = Le.I32(emf, 32), fb = Le.I32(emf, 36);
            h.X = GpMetafileFormat.Round((float)fl * sx);
            h.Y = GpMetafileFormat.Round((float)ft * sy);
            h.Width = GpMetafileFormat.Round((float)(fr - fl) * sx + 1.0f);
            h.Height = GpMetafileFormat.Round((float)(fb - ft) * sy + 1.0f);
            if (_stream != null)
                _stream.Write(emf, 0, emf.Length);
            if (_fileName != null)
                File.WriteAllBytes(_fileName, emf);
            _owner?.RecordingEnded(new GpMetafileData { Header = h, Emf = emf });
        }

        static int RasterizerCeiling(float v) => (GpMetafileFormat.Round(v * 16.0f) + 0xf) >> 4;

        // GpGraphics::NoOpPatBlt @180099b78: DpContext::GetHdc (saving the DC and turning ICM off,
        // unless the down-level drawing already did), PatBlt with the do-nothing ROP, ResetHdc.
        void NoOpPatBlt(int x, int y, int w, int h)
        {
            GpEmfDc dc = ContextHdc();
            dc.PatBlt(x, y, w, h, 0x00AA0029);
            ResetHdc();
        }

        static byte[] Int32s(params int[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Le.W32(b, i * 4, v[i]);
            return b;
        }
    }

    /// <summary>A Graphics to ask a Region its bounds with (they are asked in device units already).</summary>
    internal static class GpRegionProbe
    {
        public static Graphics Graphics()
        {
            var b = new Bitmap(1, 1);
            var g = System.Drawing.Graphics.FromImage(b);
            return g;
        }
    }
}
