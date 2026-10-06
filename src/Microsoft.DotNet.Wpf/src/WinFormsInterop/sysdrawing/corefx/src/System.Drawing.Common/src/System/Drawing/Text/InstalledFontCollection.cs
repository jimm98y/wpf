// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Drawing.Text
{
    /// <summary>
    /// Represents the fonts installed on the system: the managed font stack's scan of them, sorted.
    /// </summary>
    public sealed class InstalledFontCollection : FontCollection
    {
        public InstalledFontCollection() : base()
        {
            foreach (string name in System.Drawing.WebGpuBackend.Gdip.GpFontFamily.Installed())
                if (!PrivateFontCollection.IsPrivateOnly(name))
                    _families.Add(name);
        }
    }
}
