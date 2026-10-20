// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//

using MS.Internal;
using System.Windows.Media.Imaging;

namespace System.Windows.Interop
{
    /// <summary>
    /// Managed/Unmanaged Interop for Imaging.
    /// </summary>
    public static class Imaging
    {
        /// <summary>
        /// Construct an Bitmap from a HBITMAP.
        /// </summary>
        /// <param name="bitmap"></param>
        /// <param name="palette"></param>
        /// <param name="sourceRect"></param>
        /// <param name="sizeOptions"></param>
        /// <remarks>
        ///     Callers must have UnmanagedCode permission to call this API.
        /// </remarks>
        public static unsafe BitmapSource CreateBitmapSourceFromHBitmap(
            IntPtr bitmap,
            IntPtr palette,
            Int32Rect sourceRect,
            BitmapSizeOptions sizeOptions)
        {

            return CriticalCreateBitmapSourceFromHBitmap(bitmap, palette, sourceRect, sizeOptions, WICBitmapAlphaChannelOption.WICBitmapUseAlpha);
        }

        /// <summary>
        /// Construct an Bitmap from a HBITMAP.
        /// </summary>
        /// <param name="bitmap"></param>
        /// <param name="palette"></param>
        /// <param name="sourceRect"></param>
        /// <param name="sizeOptions"></param>
        /// <param name="alphaOptions"></param>
        internal static unsafe BitmapSource CriticalCreateBitmapSourceFromHBitmap(
            IntPtr bitmap,
            IntPtr palette,
            Int32Rect sourceRect,
            BitmapSizeOptions sizeOptions,
            WICBitmapAlphaChannelOption alphaOptions)
        {
            if (bitmap == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(bitmap));
            }

            if (!OperatingSystem.IsWindows() && FromManagedHandle(bitmap, sourceRect, sizeOptions) is BitmapSource managed)
            {
                return managed;
            }

            return new InteropBitmap(bitmap, palette, sourceRect, sizeOptions, alphaOptions); // use the critical version
        }
        
        /// <summary>
        /// Construct an Bitmap from a HICON.
        /// </summary>
        /// <param name="icon"></param>
        /// <param name="sourceRect"></param>
        /// <param name="sizeOptions"></param>
        /// <remarks>
        ///     Callers must have UnmanagedCode permission to call this API.
        /// </remarks>
        public static unsafe BitmapSource CreateBitmapSourceFromHIcon(
            IntPtr icon,
            Int32Rect sourceRect,
            BitmapSizeOptions sizeOptions)
        {

            if (icon == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(icon));
            }

            if (!OperatingSystem.IsWindows() && FromManagedHandle(icon, sourceRect, sizeOptions) is BitmapSource managed)
            {
                return managed;
            }

            return new InteropBitmap(icon, sourceRect, sizeOptions);
        }

        /// <summary>
        /// Off Windows: the picture behind an HBITMAP or HICON, which there is no GDI object but a token
        /// System.Drawing hands out for a copy of a bitmap's pixels (Bitmap.GetHbitmap, GetHicon) and
        /// publishes how to read back (AppContext "System.Drawing.ManagedHbitmapImage"). WPF's own route,
        /// InteropBitmap, asks WIC (WindowsCodecs.dll) to read the GDI object, and neither exists here:
        /// a TrendWorX toolbar made its buttons' images this way and could not be created at all.
        /// Null when the handle is not such a token (the caller goes on as on Windows).
        /// </summary>
        private static BitmapSource FromManagedHandle(IntPtr handle, Int32Rect sourceRect, BitmapSizeOptions sizeOptions)
        {
            if (AppContext.GetData("System.Drawing.ManagedHbitmapImage") is not Delegate resolve)
            {
                return null;
            }

            object image = resolve.DynamicInvoke(handle);
            if (image == null)
            {
                return null;
            }

            try
            {
                // Bitmap.LockBits(Rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb), by
                // reflection: PresentationCore does not reference System.Drawing.
                Type type = image.GetType();
                int width = (int)type.GetProperty("Width").GetValue(image);
                int height = (int)type.GetProperty("Height").GetValue(image);
                if (width <= 0 || height <= 0)
                {
                    return null;
                }

                // the parameter types from the method: Rectangle lives in System.Drawing.Primitives, not
                // beside Bitmap
                System.Reflection.MethodInfo lockBits = Array.Find(type.GetMethods(), m => m.Name == "LockBits" && m.GetParameters().Length == 3);
                if (lockBits == null)
                {
                    return null;
                }
                System.Reflection.ParameterInfo[] parameters = lockBits.GetParameters();
                object rect = Activator.CreateInstance(parameters[0].ParameterType, 0, 0, width, height);
                object readOnly = Enum.ToObject(parameters[1].ParameterType, 1);
                object argb32 = Enum.ToObject(parameters[2].ParameterType, 0x0026200A);      // Format32bppArgb
                object data = lockBits.Invoke(image, new[] { rect, readOnly, argb32 });
                byte[] pixels;
                int stride;
                try
                {
                    Type dataType = data.GetType();
                    IntPtr scan0 = (IntPtr)dataType.GetProperty("Scan0").GetValue(data);
                    stride = (int)dataType.GetProperty("Stride").GetValue(data);
                    pixels = new byte[Math.Abs(stride) * height];
                    System.Runtime.InteropServices.Marshal.Copy(scan0, pixels, 0, pixels.Length);
                }
                finally
                {
                    type.GetMethod("UnlockBits").Invoke(image, new[] { data });
                }

                // Format32bppArgb is BGRA in memory, straight alpha: WPF's Bgra32
                BitmapSource source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, Math.Abs(stride));
                if (!sourceRect.IsEmpty)
                {
                    source = new CroppedBitmap(source, sourceRect);
                }
                return ApplySizeOptions(source, sizeOptions);
            }
            finally
            {
                (image as IDisposable)?.Dispose();
            }
        }

        /// <summary>What InteropBitmap's size options do: a scale to the asked pixel size (keeping the
        /// aspect ratio when asked) and a rotation.</summary>
        private static BitmapSource ApplySizeOptions(BitmapSource source, BitmapSizeOptions sizeOptions)
        {
            if (sizeOptions == null)
            {
                return source;
            }

            double sx = 1, sy = 1;
            int pw = sizeOptions.PixelWidth, ph = sizeOptions.PixelHeight;
            if (pw > 0 || ph > 0)
            {
                sx = pw > 0 ? (double)pw / source.PixelWidth : 0;
                sy = ph > 0 ? (double)ph / source.PixelHeight : 0;
                if (sx == 0) sx = sy;
                if (sy == 0) sy = sx;
                if (sizeOptions.PreservesAspectRatio && pw > 0 && ph > 0)
                {
                    sx = sy = Math.Min(sx, sy);
                }
            }

            double angle = sizeOptions.Rotation switch
            {
                Rotation.Rotate90 => 90,
                Rotation.Rotate180 => 180,
                Rotation.Rotate270 => 270,
                _ => 0,
            };
            if (sx == 1 && sy == 1 && angle == 0)
            {
                return source;
            }

            var transform = new System.Windows.Media.TransformGroup();
            if (sx != 1 || sy != 1) transform.Children.Add(new System.Windows.Media.ScaleTransform(sx, sy));
            if (angle != 0) transform.Children.Add(new System.Windows.Media.RotateTransform(angle));
            return new TransformedBitmap(source, transform);
        }

        /// <summary>
        /// Construct an Bitmap from a section handle.
        /// </summary>
        /// <param name="section"></param>
        /// <param name="pixelWidth"></param>
        /// <param name="pixelHeight"></param>
        /// <param name="format"></param>
        /// <param name="stride"></param>
        /// <param name="offset"></param>
        /// <remarks>
        ///     Callers must have UnmanagedCode permission to call this API.
        /// </remarks>
        public static unsafe BitmapSource CreateBitmapSourceFromMemorySection(
            IntPtr section,
            int pixelWidth,
            int pixelHeight,
            Media.PixelFormat format,
            int stride,
            int offset)
        {

            if (section == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(section));
            }

            return new InteropBitmap(section, pixelWidth, pixelHeight, format, stride, offset);
        }
}
}

