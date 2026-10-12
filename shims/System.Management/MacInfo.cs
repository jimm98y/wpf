// What the Mac says about itself: sysctl, the IOKit registry, CoreGraphics. Every read is allowed
// to fail (another Unix, a sandbox) and then answers null, which WMI reports as an unset property.

using System.Runtime.InteropServices;
using System.Text;

namespace System.Management
{
    internal static unsafe class MacInfo
    {
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const uint kCFStringEncodingUTF8 = 0x08000100;

        [DllImport(LibSystem)]
        private static extern int sysctlbyname(string name, void* oldp, nint* oldlenp, void* newp, nint newlen);

        [DllImport(IOKit)]
        private static extern IntPtr IOServiceMatching(string name);

        [DllImport(IOKit)]
        private static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);

        [DllImport(IOKit)]
        private static extern uint IORegistryEntryFromPath(uint mainPort, string path);

        [DllImport(IOKit)]
        private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

        [DllImport(IOKit)]
        private static extern int IOObjectRelease(uint obj);

        [DllImport(IOKit)]
        private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

        [DllImport(IOKit)]
        private static extern uint IOIteratorNext(uint iterator);

        [DllImport(IOKit)]
        private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, string plane, IntPtr key, IntPtr allocator, uint options);

        [DllImport(CoreFoundation)]
        private static extern nuint CFBooleanGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern byte CFBooleanGetValue(IntPtr boolean);

        private const uint kIORegistryIterateRecursively = 1, kIORegistryIterateParents = 2;

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cStr, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern nuint CFGetTypeID(IntPtr cf);

        [DllImport(CoreFoundation)]
        private static extern nuint CFStringGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern nuint CFDataGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern nint CFStringGetLength(IntPtr s);

        [DllImport(CoreFoundation)]
        private static extern byte CFStringGetCString(IntPtr s, byte* buffer, nint size, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern nint CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern byte* CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundation)]
        private static extern void CFRelease(IntPtr cf);

        [DllImport(CoreGraphics)]
        private static extern uint CGMainDisplayID();

        [DllImport(CoreGraphics)]
        private static extern nuint CGDisplayPixelsWide(uint display);

        [DllImport(CoreGraphics)]
        private static extern nuint CGDisplayPixelsHigh(uint display);

        [DllImport(CoreGraphics)]
        private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

        [DllImport(CoreGraphics)]
        private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

        [DllImport(CoreGraphics)]
        private static extern void CGDisplayModeRelease(IntPtr mode);

        public static bool IsMac => OperatingSystem.IsMacOS();

        public static string SysctlString(string name)
        {
            byte[] b = SysctlBytes(name);
            if (b == null) return null;
            int len = Array.IndexOf(b, (byte)0);
            return Encoding.UTF8.GetString(b, 0, len < 0 ? b.Length : len).Trim();
        }

        public static long? SysctlLong(string name)
        {
            byte[] b = SysctlBytes(name);
            if (b == null) return null;
            return b.Length switch
            {
                8 => BitConverter.ToInt64(b, 0),
                4 => BitConverter.ToInt32(b, 0),
                _ => null,
            };
        }

        public static byte[] SysctlBytes(string name)
        {
            if (!IsMac) return null;
            try
            {
                nint len = 0;
                if (sysctlbyname(name, null, &len, null, 0) != 0 || len <= 0) return null;
                var buffer = new byte[len];
                fixed (byte* p = buffer)
                {
                    if (sysctlbyname(name, p, &len, null, 0) != 0) return null;
                }
                if (len < buffer.Length) Array.Resize(ref buffer, (int)len);
                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A property of IOPlatformExpertDevice (the machine itself) as text.</summary>
        public static string PlatformProperty(string key)
        {
            if (!IsMac) return null;
            try
            {
                uint entry = IOServiceGetMatchingService(0, IOServiceMatching("IOPlatformExpertDevice"));
                return entry == 0 ? null : EntryProperty(entry, key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether an IOKit service of this class exists (a battery, say).</summary>
        public static bool HasService(string ioClass)
        {
            if (!IsMac) return false;
            try
            {
                uint entry = IOServiceGetMatchingService(0, IOServiceMatching(ioClass));
                if (entry == 0) return false;
                IOObjectRelease(entry);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A property of the IORegistry entry at <paramref name="path"/> as text.</summary>
        public static string RegistryProperty(string path, string key)
        {
            if (!IsMac) return null;
            try
            {
                uint entry = IORegistryEntryFromPath(0, path);
                return entry == 0 ? null : EntryProperty(entry, key);
            }
            catch
            {
                return null;
            }
        }

        private static string EntryProperty(uint entry, string key)
        {
            try
            {
                return AsText(EntryValue(entry, key, search: false));
            }
            finally
            {
                IOObjectRelease(entry);
            }
        }

        // Data properties ("model", "board-id") are NUL-terminated text.
        private static string AsText(object value) => value switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b, 0, Array.IndexOf(b, (byte)0) is int n and >= 0 ? n : b.Length),
            _ => null,
        };

        /// <summary>A property of an entry (or, with <paramref name="search"/>, of the nearest parent that has it): string, byte[] or bool.</summary>
        private static object EntryValue(uint entry, string key, bool search)
        {
            IntPtr cfKey = CFStringCreateWithCString(IntPtr.Zero, key, kCFStringEncodingUTF8);
            try
            {
                IntPtr value = search
                    ? IORegistryEntrySearchCFProperty(entry, "IOService", cfKey, IntPtr.Zero, kIORegistryIterateRecursively | kIORegistryIterateParents)
                    : IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0);
                if (value == IntPtr.Zero) return null;
                try
                {
                    nuint type = CFGetTypeID(value);
                    if (type == CFStringGetTypeID())
                    {
                        nint size = CFStringGetLength(value) * 4 + 1;
                        var buffer = new byte[size];
                        fixed (byte* p = buffer)
                        {
                            if (CFStringGetCString(value, p, size, kCFStringEncodingUTF8) == 0) return null;
                        }
                        int len = Array.IndexOf(buffer, (byte)0);
                        return Encoding.UTF8.GetString(buffer, 0, len < 0 ? buffer.Length : len);
                    }
                    if (type == CFDataGetTypeID())
                    {
                        return new ReadOnlySpan<byte>(CFDataGetBytePtr(value), (int)CFDataGetLength(value)).ToArray();
                    }
                    if (type == CFBooleanGetTypeID())
                    {
                        return CFBooleanGetValue(value) != 0;
                    }
                    return null;
                }
                finally
                {
                    CFRelease(value);
                }
            }
            finally
            {
                CFRelease(cfKey);
            }
        }

        /// <summary>
        /// The Ethernet interfaces IOKit knows (BSD name to hardware address and whether built in).
        /// The address comes from the controller: the one a process reads from the interface itself
        /// may be withheld by the system (02:00:00:00:00:00).
        /// </summary>
        public static Collections.Generic.Dictionary<string, (byte[] Mac, bool Builtin)> EthernetInterfaces()
        {
            var result = new Collections.Generic.Dictionary<string, (byte[], bool)>(StringComparer.Ordinal);
            if (!IsMac) return result;
            try
            {
                if (IOServiceGetMatchingServices(0, IOServiceMatching("IOEthernetInterface"), out uint iterator) != 0) return result;
                try
                {
                    uint entry;
                    while ((entry = IOIteratorNext(iterator)) != 0)
                    {
                        try
                        {
                            if (EntryValue(entry, "BSD Name", search: false) is string bsd)
                            {
                                byte[] mac = EntryValue(entry, "IOMACAddress", search: true) as byte[];
                                bool builtin = EntryValue(entry, "IOBuiltin", search: false) is true;
                                result[bsd] = (mac is { Length: 6 } ? mac : null, builtin);
                            }
                        }
                        finally
                        {
                            IOObjectRelease(entry);
                        }
                    }
                }
                finally
                {
                    IOObjectRelease(iterator);
                }
            }
            catch
            {
            }
            return result;
        }

        public static (uint Width, uint Height, uint RefreshRate)? MainDisplay()
        {
            if (!IsMac) return null;
            try
            {
                uint display = CGMainDisplayID();
                uint w = (uint)CGDisplayPixelsWide(display), h = (uint)CGDisplayPixelsHigh(display);
                uint hz = 60;
                IntPtr mode = CGDisplayCopyDisplayMode(display);
                if (mode != IntPtr.Zero)
                {
                    double rate = CGDisplayModeGetRefreshRate(mode);
                    if (rate > 0) hz = (uint)Math.Round(rate);
                    CGDisplayModeRelease(mode);
                }
                return (w, h, hz);
            }
            catch
            {
                return null;
            }
        }

        public static DateTime? BootTime()
        {
            byte[] tv = SysctlBytes("kern.boottime");
            if (tv == null || tv.Length < 12) return null;
            long seconds = BitConverter.ToInt64(tv, 0);
            int micro = BitConverter.ToInt32(tv, 8);
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddTicks(micro * 10L).ToLocalTime();
        }
    }
}
