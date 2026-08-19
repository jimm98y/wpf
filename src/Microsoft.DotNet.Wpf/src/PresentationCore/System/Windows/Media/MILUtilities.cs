// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//

using System.Windows.Media.Media3D;
using System.Windows.Media.Composition;
using MS.Internal;
using System.Runtime.InteropServices;

namespace System.Windows.Media
{
    internal static class MILUtilities
    {
        internal static readonly D3DMATRIX D3DMATRIXIdentity =
                                new D3DMATRIX(1, 0, 0, 0,
                                              0, 1, 0, 0,
                                              0, 0, 1, 0,
                                              0, 0, 0, 1);

        /// <summary>
        /// Converts a System.Windows.Media.Matrix to a D3DMATRIX.
        /// </summary>
        /// <param name="matrix"> Input Matrix to convert </param>
        /// <param name="d3dMatrix"> Output convered D3DMATRIX </param>        
        internal static unsafe void ConvertToD3DMATRIX(
            /* in */ Matrix* matrix,
            /* out */ D3DMATRIX* d3dMatrix
            )
        {
            *d3dMatrix = D3DMATRIXIdentity;

            float* pD3DMatrix = (float*)d3dMatrix;
            double* pMatrix = (double*)matrix;

            // m11 = m11
            pD3DMatrix[0] = (float)pMatrix[0];

            // m12 = m12
            pD3DMatrix[1] = (float)pMatrix[1];

            // m21 = m21
            pD3DMatrix[4] = (float)pMatrix[2];

            // m22 = m22
            pD3DMatrix[5] = (float)pMatrix[3];

            // m41 = offsetX
            pD3DMatrix[12] = (float)pMatrix[4];

            // m42 = offsetY
            pD3DMatrix[13] = (float)pMatrix[5];
        }

        /// <summary>
        /// Converts a D3DMATRIX to a System.Windows.Media.Matrix.
        /// </summary>
        /// <param name="d3dMatrix"> Input D3DMATRIX to convert </param>
        /// <param name="matrix"> Output converted Matrix </param>
        internal static unsafe void ConvertFromD3DMATRIX(
            /* in */ D3DMATRIX* d3dMatrix,
            /* out */ Matrix* matrix
            )
        {
            float* pD3DMatrix = (float*)d3dMatrix;
            double* pMatrix = (double*)matrix;

            //
            // Convert first D3DMatrix Vector
            //

            pMatrix[0] = (double) pD3DMatrix[0]; // m11 = m11
            pMatrix[1] = (double) pD3DMatrix[1]; // m12 = m12

            // Assert that non-affine fields are identity or NaN
            //
            // Multiplication with an affine 2D matrix (i.e., a matrix
            // with only _11, _12, _21, _22, _41, & _42 set to non-identity
            // values) containing NaN's, can cause the NaN's to propagate to
            // all other fields.  Thus, we allow NaN's in addition to 
            // identity values.
            Debug.Assert(pD3DMatrix[2] == 0.0f || Single.IsNaN(pD3DMatrix[2]));
            Debug.Assert(pD3DMatrix[3] == 0.0f || Single.IsNaN(pD3DMatrix[3]));

            //
            // Convert second D3DMatrix Vector
            //

            pMatrix[2] = (double) pD3DMatrix[4]; // m21 = m21
            pMatrix[3] = (double) pD3DMatrix[5]; // m22 = m22
            Debug.Assert(pD3DMatrix[6] == 0.0f || Single.IsNaN(pD3DMatrix[6]));
            Debug.Assert(pD3DMatrix[7] == 0.0f || Single.IsNaN(pD3DMatrix[7]));

            //
            // Convert third D3DMatrix Vector
            //

            Debug.Assert(pD3DMatrix[8] == 0.0f || Single.IsNaN(pD3DMatrix[8]));
            Debug.Assert(pD3DMatrix[9] == 0.0f || Single.IsNaN(pD3DMatrix[9]));
            Debug.Assert(pD3DMatrix[10] == 1.0f || Single.IsNaN(pD3DMatrix[10]));
            Debug.Assert(pD3DMatrix[11] == 0.0f || Single.IsNaN(pD3DMatrix[11]));

            //
            // Convert fourth D3DMatrix Vector
            //

            pMatrix[4] = (double) pD3DMatrix[12]; // m41 = offsetX
            pMatrix[5] = (double) pD3DMatrix[13]; // m42 = offsetY
            Debug.Assert(pD3DMatrix[14] == 0.0f || Single.IsNaN(pD3DMatrix[14]));
            Debug.Assert(pD3DMatrix[15] == 1.0f || Single.IsNaN(pD3DMatrix[15]));

            *((MatrixTypes*)(pMatrix+6)) = MatrixTypes.TRANSFORM_IS_UNKNOWN;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MILRect3D
        {
            public MILRect3D(ref Rect3D rect)
            {
                X = (float)rect.X;
                Y = (float)rect.Y;
                Z = (float)rect.Z;
                LengthX = (float)rect.SizeX;
                LengthY = (float)rect.SizeY;
                LengthZ = (float)rect.SizeZ;
            }
            
            public float X; 
            public float Y; 
            public float Z;
            public float LengthX; 
            public float LengthY; 
            public float LengthZ;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MilRectF
        {
            public float Left;
            public float Top;
            public float Right;
            public float Bottom;
        };

        [DllImport(DllImport.MilCore)]
        private static extern /*HRESULT*/ int MIL3DCalcProjected2DBounds(
            ref D3DMATRIX pFullTransform3D,
            ref MILRect3D pboxBounds,
            out MilRectF prcDestRect); 

        [DllImport(DllImport.MilCore, EntryPoint = "MilUtility_CopyPixelBuffer", PreserveSig = false)]
        private static extern unsafe void MILCopyPixelBufferNative(
            byte *  pOutputBuffer,
            uint    outputBufferSize,
            uint    outputBufferStride,
            uint    outputBufferOffsetInBits,
            byte *  pInputBuffer,
            uint    inputBufferSize,
            uint    inputBufferStride,
            uint    inputBufferOffsetInBits,
            uint    height,
            uint    copyWidthInBits
            );

        internal static unsafe void MILCopyPixelBuffer(
            byte* pOutputBuffer,
            uint outputBufferSize,
            uint outputBufferStride,
            uint outputBufferOffsetInBits,
            byte* pInputBuffer,
            uint inputBufferSize,
            uint inputBufferStride,
            uint inputBufferOffsetInBits,
            uint height,
            uint copyWidthInBits
            )
        {
            // A row is a BIT stream, most-significant bit first -- the packing every sub-byte WPF
            // format uses (BlackWhite, Indexed1/2/4, Gray2/4), and the same order ManagedPixelConverter
            // reads and writes. Offsets and width are in bits because a rectangle of an Indexed4 image
            // starts and ends mid-byte whenever its X or Width is odd.
            //
            // The last byte a row touches is the one holding its last bit, so the extent to validate
            // depends on the offset as well as the width. With both offsets zero and a whole-byte
            // width this reduces to the plain row length, which is what the aligned path below copies.
            uint inputRowBytes = (inputBufferOffsetInBits + copyWidthInBits + 7) >> 3;
            uint outputRowBytes = (outputBufferOffsetInBits + copyWidthInBits + 7) >> 3;

            if (height > 0 &&
                ((ulong)(height - 1) * outputBufferStride + outputRowBytes > outputBufferSize ||
                 (ulong)(height - 1) * inputBufferStride + inputRowBytes > inputBufferSize))
            {
                throw new ArgumentException("The pixel copy does not fit within the supplied buffers.");
            }

            // Whole bytes on both sides: every format above 8bpp, and any sub-byte copy that happens
            // to land on byte boundaries. Worth keeping separate -- it is the overwhelmingly common
            // case and a row of it is one memmove rather than a loop over bytes.
            if (outputBufferOffsetInBits == 0 && inputBufferOffsetInBits == 0 && (copyWidthInBits & 7) == 0)
            {
                uint rowBytes = copyWidthInBits >> 3;
                for (uint y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(pInputBuffer + y * inputBufferStride, (int)rowBytes)
                        .CopyTo(new Span<byte>(pOutputBuffer + y * outputBufferStride, (int)rowBytes));
                }
                return;
            }

            for (uint y = 0; y < height; y++)
            {
                CopyBits(pOutputBuffer + y * outputBufferStride, outputBufferOffsetInBits,
                         pInputBuffer + y * inputBufferStride, inputBufferOffsetInBits,
                         copyWidthInBits);
            }
        }

        /// <summary>
        /// Copy <paramref name="count"/> bits, MSB first, leaving every bit outside that range in the
        /// destination untouched -- which is the whole point: a 4bpp copy starting at an odd X shares
        /// its first byte with a pixel the caller did not ask to overwrite.
        /// </summary>
        /// <remarks>
        /// Moves up to eight bits per step, so it costs roughly one iteration per byte. A same-phase
        /// copy (source and destination misaligned by the same amount) could memmove its middle and
        /// only fiddle the two ends, but sub-byte formats are small and rare -- the aligned path above
        /// already takes every ordinary bitmap -- and a second algorithm here would be more code to be
        /// wrong in than the case justifies.
        /// </remarks>
        private static unsafe void CopyBits(byte* dst, uint dstBit, byte* src, uint srcBit, uint count)
        {
            dst += dstBit >> 3; dstBit &= 7;
            src += srcBit >> 3; srcBit &= 7;

            while (count > 0)
            {
                // Never cross a destination byte: one masked read-modify-write per byte touched.
                uint chunk = Math.Min(8 - dstBit, count);

                uint bits = ReadBits(src, srcBit, chunk);
                WriteBits(dst, dstBit, chunk, bits);

                dstBit += chunk;
                dst += dstBit >> 3; dstBit &= 7;

                srcBit += chunk;
                src += srcBit >> 3; srcBit &= 7;

                count -= chunk;
            }
        }

        /// <summary>Read <paramref name="count"/> (1..8) bits at a bit offset of 0..7, right-aligned.</summary>
        private static unsafe uint ReadBits(byte* p, uint bitOffset, uint count)
        {
            // The window can straddle two bytes; the second is only read when it is really needed, so
            // this never touches a byte past the end of the copied range.
            uint window = (uint)p[0] << 8;
            if (bitOffset + count > 8) window |= p[1];

            int shift = 16 - (int)bitOffset - (int)count;
            return (window >> shift) & ((1u << (int)count) - 1);
        }

        /// <summary>Write <paramref name="count"/> (1..8) right-aligned bits into one byte at a bit offset.</summary>
        private static unsafe void WriteBits(byte* p, uint bitOffset, uint count, uint bits)
        {
            int shift = 8 - (int)bitOffset - (int)count;
            uint mask = ((1u << (int)count) - 1) << shift;
            p[0] = (byte)((p[0] & ~mask) | ((bits << shift) & mask));
        }

        internal static Rect ProjectBounds(
            ref Matrix3D viewProjMatrix,
            ref Rect3D originalBox)
        {
            return ProjectBoundsManaged(ref viewProjMatrix, ref originalBox);
        }

        // Managed replacement for the native MIL3DCalcProjected2DBounds (wpfgfx). Projects the eight
        // corners of the 3D box through the (row-vector) view-projection matrix, does the perspective
        // divide, and returns the 2D bounding rectangle. Corners at/behind the camera (w <= 0) are
        // clamped to a tiny positive w so the resulting bound stays conservative (a superset) rather
        // than dividing by zero; for content fully in front of the camera the result is exact.
        private static Rect ProjectBoundsManaged(ref Matrix3D m, ref Rect3D box)
        {
            double x0 = box.X, y0 = box.Y, z0 = box.Z;
            double x1 = x0 + box.SizeX, y1 = y0 + box.SizeY, z1 = z0 + box.SizeZ;

            double left = double.PositiveInfinity, top = double.PositiveInfinity;
            double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            bool any = false;

            for (int c = 0; c < 8; c++)
            {
                double x = (c & 1) == 0 ? x0 : x1;
                double y = (c & 2) == 0 ? y0 : y1;
                double z = (c & 4) == 0 ? z0 : z1;

                // [x y z 1] * M   (WPF Matrix3D is row-major / row-vector).
                double px = x * m.M11 + y * m.M21 + z * m.M31 + m.OffsetX;
                double py = x * m.M12 + y * m.M22 + z * m.M32 + m.OffsetY;
                double pw = x * m.M14 + y * m.M24 + z * m.M34 + m.M44;

                if (pw < 1e-6) pw = 1e-6;
                double sx = px / pw;
                double sy = py / pw;

                if (double.IsNaN(sx) || double.IsNaN(sy)) continue;
                any = true;
                if (sx < left) left = sx;
                if (sx > right) right = sx;
                if (sy < top) top = sy;
                if (sy > bottom) bottom = sy;
            }

            if (!any || left == right || top == bottom)
            {
                return Rect.Empty;
            }
            return new Rect(left, top, right - left, bottom - top);
        }
    }
}

