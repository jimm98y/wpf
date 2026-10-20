// Uses another assembly's COM interface with an RCW it makes itself: on Windows a built-in RCW unless this
// assembly is rewritten with ComImportFixture (then Marshal.GetObjectForIUnknown is wpfmac's, a ComObject).
using System;
using System.Runtime.InteropServices;

namespace ComUserFixture
{
    public static class User
    {
        public static int Add(IntPtr p, int a, int b) => ((ComImportFixture.ICalc)Marshal.GetObjectForIUnknown(p)).Add(a, b);
    }
}
