// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A compact, faithful miniature of WPF's DUCE channel protocol -- the
// serialized command/resource stream that flows from managed WPF to the native
// compositor. The real protocol is the generated wgx_commands.cs MILCMD packet
// stream (RecordHeader = [size,id] followed by payload, resources referenced by
// 32-bit handles, visual content carried as an inline render-data byte stream).
// This mirrors that shape closely enough to prove the architecture: the client
// (CompositionChannel) serializes scene mutations; the server (CompositionEngine)
// rebuilds the composition tree and renders it with WebGPU -- exactly the seam
// where milcore is being replaced.
//
// Wire format (all little-endian):
//   batch    := record*
//   record   := u32 payloadSize, u32 commandId, byte[payloadSize] payload
//   content  := renderOp*                       (carried by VisualSetContent)
//   renderOp := u8 op, byte[...] payload
//

using System;
using System.Buffers.Binary;
using System.IO;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    /// <summary>Command ids, named after their MILCMD_* counterparts.</summary>
    internal enum MilCommand : uint
    {
        CreateResource = 1,   // MILCMD_CHANNEL_CREATERESOURCE
        VisualSetOffset = 2,  // MILCMD_VISUAL_SETOFFSET
        VisualSetTransform = 3, // MILCMD_VISUAL_SETTRANSFORM
        VisualSetOpacity = 4, // MILCMD_VISUAL_SETALPHA
        VisualSetClip = 5,    // MILCMD_VISUAL_SETCLIP
        VisualSetContent = 6, // MILCMD_VISUAL_SETCONTENT (inline render data)
        VisualAddChild = 7,   // MILCMD_VISUAL_ADDCHILD
        TargetSetRoot = 8,    // MILCMD_TARGET_SETROOT
    }

    /// <summary>Resource type tags (subset of wgx_resource_types).</summary>
    internal enum MilResourceType : byte
    {
        Visual = 1,
    }

    /// <summary>Render-data instruction opcodes (subset of the DrawingContext stream).</summary>
    internal enum RenderDataOp : byte
    {
        FillRectangle = 1,
        FillPolygon = 2,
        DrawGlyphRun = 3,
        FillPath = 4,
    }

    /// <summary>Path segment discriminator in the render-data stream.</summary>
    internal enum PathSegmentKind : byte
    {
        Line = 0,
        Quadratic = 1,
        Cubic = 2,
    }

    /// <summary>Brush discriminator carried in the render-data stream.</summary>
    internal enum BrushKind : byte
    {
        Solid = 0,
        LinearGradient = 1,
        Image = 2,
    }

    /// <summary>Little-endian append-only writer with record back-patching.</summary>
    internal sealed class CommandWriter
    {
        private readonly MemoryStream _ms = new();

        public long Position => _ms.Position;

        public void U8(byte v) => _ms.WriteByte(v);

        public void U16(ushort v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            _ms.Write(b);
        }

        public void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            _ms.Write(b);
        }

        public void F32(float v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(b, v);
            _ms.Write(b);
        }

        public void F64(double v)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(b, v);
            _ms.Write(b);
        }

        public void Bytes(ReadOnlySpan<byte> data) => _ms.Write(data);

        /// <summary>Writes one record, back-patching the payload size header.</summary>
        public void Record(MilCommand command, Action<CommandWriter> writePayload)
        {
            long sizePos = _ms.Position;
            U32(0);                  // payload size placeholder
            U32((uint)command);
            long payloadStart = _ms.Position;
            writePayload(this);
            long end = _ms.Position;

            _ms.Position = sizePos;
            U32((uint)(end - payloadStart));
            _ms.Position = end;
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>Little-endian forward cursor over a received batch.</summary>
    internal sealed class CommandReader
    {
        private readonly byte[] _data;
        private int _pos;

        public CommandReader(byte[] data) => _data = data;

        public bool AtEnd => _pos >= _data.Length;
        public int Position { get => _pos; set => _pos = value; }

        public byte U8() => _data[_pos++];

        public ushort U16()
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos));
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        public float F32()
        {
            float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos));
            _pos += 4;
            return v;
        }

        public double F64()
        {
            double v = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_pos));
            _pos += 8;
            return v;
        }

        public byte[] Bytes(int count)
        {
            var slice = new byte[count];
            Array.Copy(_data, _pos, slice, 0, count);
            _pos += count;
            return slice;
        }
    }
}
