// Releases another assembly's COM objects: built-in Marshal.ReleaseComObject throws on a rewritten one (not a
// __ComObject) unless this assembly's call sites are rewritten with ComImportFixture.
using System.Runtime.InteropServices;

namespace ComReleaserFixture
{
    public static class Releaser
    {
        public static string AddAndRelease(object o)
        {
            var calc = (ComImportFixture.ICalc)o;
            int sum = calc.Add(2, 3);
            return $"{sum}|{Marshal.IsComObject(calc)}|{Marshal.ReleaseComObject(calc)}";
        }
    }
}
