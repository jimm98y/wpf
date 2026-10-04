// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

namespace System.Drawing.Text
{
    /// <summary>
    /// When inherited, enumerates the FontFamily objects in a collection of fonts. Managed: the
    /// families come from the font files (WebGpuBackend.Gdip.GpFontFamily), never from GDI+.
    /// </summary>
    public abstract class FontCollection : IDisposable
    {
        internal readonly List<string> _families = new List<string>();

        internal FontCollection() { }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) { }

        public FontFamily[] Families
        {
            get
            {
                var families = new FontFamily[_families.Count];
                for (int f = 0; f < families.Length; f++)
                    families[f] = new FontFamily(_families[f], this);
                return families;
            }
        }

        internal bool Contains(string name, out string canonical)
        {
            foreach (string f in _families)
                if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) { canonical = f; return true; }
            canonical = null;
            return false;
        }

        ~FontCollection() => Dispose(false);
    }
}
