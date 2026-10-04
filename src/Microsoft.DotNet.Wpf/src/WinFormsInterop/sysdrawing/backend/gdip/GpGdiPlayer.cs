// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer : IDisposable
    {
        readonly GpMetafilePlayer.Session _s;
        public GpGdiPlayer(GpMetafilePlayer.Session s) { _s = s; }
        public void BeginEmf(GpMetafileData d) { }
        public void BeginWmf(GpMetafileData d) { }
        public void PlayEmf(int type, byte[] b, int o, int n) { }
        public void PlayWmf(int function, byte[] b, int o, int n) { }
        public void Dispose() { }
    }
}
