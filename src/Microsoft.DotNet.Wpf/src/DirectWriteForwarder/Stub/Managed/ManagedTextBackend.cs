// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Public entry point PresentationCore's Factory delegates to off-Windows. The stub's managed
// font-object constructors are internal, so PresentationCore (a separate assembly) reaches the
// managed graph through these public factory methods instead of constructing the types directly.
//

using System;

namespace MS.Internal.Text.TextInterface.Managed
{
    public static class ManagedTextBackend
    {
        public static FontCollection GetSystemFontCollection()
            => FontFactoryState.SystemCollection;

        public static FontCollection GetFontCollection(Uri uri)
        {
            // Custom font collections (app font directories) are uncommon; resolve the system
            // collection for the standard fonts folder, otherwise fall back to system too
            // (the gallery uses only installed fonts). A directory-scoped catalog can be added
            // later if an app ships private fonts.
            return FontFactoryState.SystemCollection;
        }

        public static FontFile CreateFontFile(Uri filePathUri)
            => new FontFile(filePathUri);

        public static FontFace CreateFontFace(Uri filePathUri, uint faceIndex, FontSimulations sims)
        {
            string path = filePathUri.IsFile ? filePathUri.LocalPath : filePathUri.AbsoluteUri;
            FaceRecord rec = FaceRecord.FromFile(path, (int)faceIndex);
            return new FontFace(rec, sims);
        }

        public static TextAnalyzer CreateTextAnalyzer()
            => TextAnalyzer.CreateManaged();
    }
}
