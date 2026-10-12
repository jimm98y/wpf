using System.Reflection;

namespace WpfMac.Bootstrap.Win32
{
    /// <summary>
    /// The managed implementations of Windows exports live in WpfMac.Win32, which references WPF and
    /// WinForms; the bootstrap does not, so it reaches them by name. A missing assembly costs the
    /// implementations, never the app: every export still has its logged failure path.
    /// </summary>
    internal static class Implementations
    {
        public static int RegisterAll(IntPtr shimHandle, Func<string, bool> coversModule)
        {
            try
            {
                Assembly asm = Assembly.Load(new AssemblyName("WpfMac.Win32"));
                Type? exports = asm.GetType("WpfMac.Win32.Exports");
                if (exports == null) return 0;
                exports.GetProperty("ShimHandle")?.SetValue(null, shimHandle);
                exports.GetProperty("CoversModule")?.SetValue(null, coversModule);
                var register = exports.GetMethod("RegisterAll");
                if (register == null) return 0;
                Func<string, IntPtr, bool> slot = (name, fn) => { unsafe { return ShimLibrary.Register(name, (void*)fn); } };
                Action<string, string> logOnce = CompatLog.Once;
                return (int)register.Invoke(null, new object[] { slot, logOnce })!;
            }
            catch (Exception e)
            {
                CompatLog.Info($"win32 shim: the managed implementations did not load: {e.GetBaseException().Message}");
                return 0;
            }
        }
    }
}
