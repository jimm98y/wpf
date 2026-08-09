// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The seam WPF's printing pipeline has always had, and the only part of it that was ever
// Windows-specific.
//
// ReachFramework's visual-tree walker, drawing normalizer and alpha flattener all reduce a Visual
// to calls on this interface. Every one of its members takes WPF types -- Geometry, Matrix, Brush,
// Pen, GlyphRun, BitmapSource -- and not one takes a device handle. It is a device-independent 2-D
// sink by construction; the reason printing did not work off Windows is simply that the sole
// implementation ever written for it was C++/CLI over GDI.
//
// The declaration must exist in this assembly as well as in the reference assembly ReachFramework
// compiles against, and must match it member for member: they are the same type at run time.
//

using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// ReachFramework implements ILegacyDevice, and it is internal, so it needs friend access -- exactly
// as the reference assembly this one replaces already grants. Without it the type loads and then
// fails with "attempting to implement an inaccessible interface", which happens at the moment the
// first page is printed rather than at build time.
[assembly: InternalsVisibleTo(Microsoft.Internal.BuildInfo.ReachFramework)]

namespace System.Printing
{
    internal interface ILegacyDevice
    {
        int StartDocument(string printerName, string jobName, string filename, byte[] deviceMode);

        void StartDocumentWithoutCreatingDC(string printerName, string jobName, string filename);

        void EndDocument();

        void CreateDeviceContext(string printerName, string jobName, byte[] deviceMode);

        void DeleteDeviceContext();

        string ExtEscGetName();

        bool ExtEscMXDWPassThru();

        void StartPage(byte[] deviceMode, int rasterizationDPI);

        void EndPage();

        void PopTransform();

        void PopClip();

        void PushClip(Geometry clipGeometry);

        void PushTransform(Matrix transform);

        void DrawGeometry(Brush brush, Pen pen, Brush strokeBrush, Geometry geometry);

        void DrawImage(BitmapSource source, byte[] buffer, Rect rect);

        void DrawGlyphRun(Brush brush, GlyphRun glyphRun);

        void Comment(string message);
    }
}
