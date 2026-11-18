// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a printer can do, as CUPS knows it -- on Linux and on macOS, whose print system is CUPS too -- for what Windows
// answers from the driver (DeviceCapabilities, GetPrintCapabilities): its paper sizes with their margins (the
// destination's media list), whether it prints both sides and in colour, its resolutions and how many copies it makes
// (the IPP *-supported attributes). Asked once per printer and kept.
//
// libcups is loaded here by its file names and called through function pointers: this file is link-compiled into
// more than one assembly (WindowsBase, System.Drawing), each with a DllImport resolver of its own or none, and must
// not depend on either. A machine without CUPS, or a printer CUPS cannot describe, has no capabilities known: the
// callers keep their standard answer then.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MS.Internal.Interop
{
    /// <summary>A printer's capabilities. Sizes in WPF units (96ths of an inch); the default paper first.</summary>
    internal sealed class PrinterCapabilities
    {
        internal readonly List<PaperMedia> Media = new List<PaperMedia>();
        internal bool Duplex;
        internal bool Color;
        internal readonly List<(int X, int Y)> Resolutions = new List<(int, int)>();
        internal int MaxCopies = 1;
    }

    /// <summary>One paper size: its PWG self-describing name ("iso_a4_210x297mm"), size and margins.</summary>
    internal readonly record struct PaperMedia(string Name, double Width, double Height, double Left, double Top, double Right, double Bottom);

    internal static unsafe class CupsCapabilities
    {
        private const double WpfUnitsPerHundredthMillimetre = 96.0 / 2540.0;

        private static readonly Dictionary<string, PrinterCapabilities> s_cache = new Dictionary<string, PrinterCapabilities>(StringComparer.Ordinal);
        private static bool s_loaded, s_available;

        private static delegate* unmanaged<IntPtr, byte*, byte*, IntPtr> s_cupsGetNamedDest;
        private static delegate* unmanaged<int, IntPtr, void> s_cupsFreeDests;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr> s_cupsCopyDestInfo;
        private static delegate* unmanaged<IntPtr, void> s_cupsFreeDestInfo;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, int> s_cupsGetDestMediaCount;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, uint, CupsSize*, int> s_cupsGetDestMediaByIndex;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte*, IntPtr> s_cupsFindDestSupported;
        private static delegate* unmanaged<IntPtr, int> s_ippGetCount;
        private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, CupsSize*, int> s_cupsGetDestMediaDefault;
        private static delegate* unmanaged<IntPtr*, int> s_cupsGetDests;
        private static delegate* unmanaged<byte*, int, IntPtr, IntPtr> s_cupsGetOption;
        private static delegate* unmanaged<IntPtr, int, IntPtr*, int> s_cupsCopyDest;
        private static delegate* unmanaged<IntPtr, int, IntPtr, IntPtr> s_ippGetString;
        private static delegate* unmanaged<IntPtr, int, int*, int> s_ippGetRange;
        private static delegate* unmanaged<IntPtr, int, int*, int*, int> s_ippGetResolution;

        // cups_size_t
        [StructLayout(LayoutKind.Sequential)]
        private struct CupsSize
        {
            public fixed byte media[128];
            public int width, length, bottom, left, right, top;
        }

        private static bool Load()
        {
            if (s_loaded) return s_available;
            s_loaded = true;
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
            if (OperatingSystem.IsAndroid()) return false;
            IntPtr lib = IntPtr.Zero;
            foreach (string name in OperatingSystem.IsMacOS() ? new[] { "/usr/lib/libcups.2.dylib" } : new[] { "libcups.so.2", "libcups.so" })
                if (NativeLibrary.TryLoad(name, out lib)) break;
            if (lib == IntPtr.Zero) return false;
            IntPtr F(string n) => NativeLibrary.TryGetExport(lib, n, out IntPtr p) ? p : IntPtr.Zero;
            IntPtr[] fns =
            {
                F("cupsGetNamedDest"), F("cupsFreeDests"), F("cupsCopyDestInfo"), F("cupsFreeDestInfo"), F("cupsGetDestMediaCount"),
                F("cupsGetDestMediaByIndex"), F("cupsFindDestSupported"), F("ippGetCount"), F("ippGetString"), F("ippGetRange"),
                F("ippGetResolution"), F("cupsGetDests"), F("cupsGetOption"), F("cupsCopyDest"), F("cupsGetDestMediaDefault"),
            };
            if (Array.IndexOf(fns, IntPtr.Zero) >= 0) return false;
            s_cupsGetNamedDest = (delegate* unmanaged<IntPtr, byte*, byte*, IntPtr>)fns[0];
            s_cupsFreeDests = (delegate* unmanaged<int, IntPtr, void>)fns[1];
            s_cupsCopyDestInfo = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)fns[2];
            s_cupsFreeDestInfo = (delegate* unmanaged<IntPtr, void>)fns[3];
            s_cupsGetDestMediaCount = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, int>)fns[4];
            s_cupsGetDestMediaByIndex = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, uint, CupsSize*, int>)fns[5];
            s_cupsFindDestSupported = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte*, IntPtr>)fns[6];
            s_ippGetCount = (delegate* unmanaged<IntPtr, int>)fns[7];
            s_ippGetString = (delegate* unmanaged<IntPtr, int, IntPtr, IntPtr>)fns[8];
            s_ippGetRange = (delegate* unmanaged<IntPtr, int, int*, int>)fns[9];
            s_ippGetResolution = (delegate* unmanaged<IntPtr, int, int*, int*, int>)fns[10];
            s_cupsGetDests = (delegate* unmanaged<IntPtr*, int>)fns[11];
            s_cupsGetOption = (delegate* unmanaged<byte*, int, IntPtr, IntPtr>)fns[12];
            s_cupsCopyDest = (delegate* unmanaged<IntPtr, int, IntPtr*, int>)fns[13];
            s_cupsGetDestMediaDefault = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, CupsSize*, int>)fns[14];
            return s_available = true;
        }

        private static byte[] Z(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

        /// <summary>The printer's capabilities, or null where CUPS cannot say.</summary>
        internal static PrinterCapabilities Query(string printer)
        {
            if (string.IsNullOrEmpty(printer)) return null;
            lock (s_cache)
            {
                if (s_cache.TryGetValue(printer, out PrinterCapabilities known)) return known;
                PrinterCapabilities caps = null;
                try { caps = Load() ? Read(printer) : null; }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or AccessViolationException) { caps = null; }
                s_cache[printer] = caps;
                return caps;
            }
        }

        private static PrinterCapabilities Read(string printer)
        {
            // a queue's instance (office/duplex) is the queue's name and the instance's
            string name = printer, instance = null;
            int slash = printer.IndexOf('/');
            if (slash > 0) { name = printer[..slash]; instance = printer[(slash + 1)..]; }
            IntPtr dest;
            fixed (byte* n = Z(name))
            fixed (byte* i = instance == null ? null : Z(instance))
                dest = s_cupsGetNamedDest(IntPtr.Zero, n, i);
            // a printer named as the system's printing UI names it (macOS: "192.168.4.2" for the queue
            // _192_168_4_2): the destination whose description is that name
            if (dest == IntPtr.Zero) dest = ByDescription(printer);
            if (dest == IntPtr.Zero) return null;
            IntPtr info = IntPtr.Zero;
            try
            {
                info = s_cupsCopyDestInfo(IntPtr.Zero, dest);
                if (info == IntPtr.Zero) return null;
                var caps = new PrinterCapabilities();

                int count = s_cupsGetDestMediaCount(IntPtr.Zero, dest, info, 0);
                for (int k = 0; k < count; k++)
                {
                    CupsSize size;
                    if (s_cupsGetDestMediaByIndex(IntPtr.Zero, dest, info, k, 0, &size) == 0 || size.width <= 0 || size.length <= 0) continue;
                    string media = Marshal.PtrToStringUTF8((IntPtr)size.media) ?? "";
                    if (caps.Media.Exists(m => m.Name == media)) continue;
                    double u = WpfUnitsPerHundredthMillimetre;
                    caps.Media.Add(new PaperMedia(media, size.width * u, size.length * u, size.left * u, size.top * u, size.right * u, size.bottom * u));
                }

                // the printer's default paper first, as a driver's default form leads its list
                CupsSize def;
                if (s_cupsGetDestMediaDefault(IntPtr.Zero, dest, info, 0, &def) != 0)
                {
                    string defName = Marshal.PtrToStringUTF8((IntPtr)def.media) ?? "";
                    int at = caps.Media.FindIndex(m => m.Name == defName);
                    if (at > 0) { PaperMedia d = caps.Media[at]; caps.Media.RemoveAt(at); caps.Media.Insert(0, d); }
                }

                foreach (string side in Strings(dest, info, "sides"))
                    if (side.StartsWith("two-sided", StringComparison.Ordinal)) caps.Duplex = true;
                List<string> colour = Strings(dest, info, "print-color-mode");
                // a printer that names no colour modes is taken at its word for colour, as Windows' drivers default
                caps.Color = colour.Count == 0 || colour.Exists(c => c == "color" || c == "auto");

                IntPtr res = Supported(dest, info, "printer-resolution");
                if (res != IntPtr.Zero)
                    for (int k = 0, n = s_ippGetCount(res); k < n; k++)
                    {
                        int y, units;
                        int x = s_ippGetResolution(res, k, &y, &units);
                        // IPP_RES_PER_CM (4): dots per centimetre
                        if (units == 4) { x = (int)Math.Round(x * 2.54); y = (int)Math.Round(y * 2.54); }
                        if (x > 0 && y > 0 && !caps.Resolutions.Contains((x, y))) caps.Resolutions.Add((x, y));
                    }

                IntPtr copies = Supported(dest, info, "copies");
                if (copies != IntPtr.Zero && s_ippGetCount(copies) > 0)
                {
                    int upper;
                    s_ippGetRange(copies, 0, &upper);
                    caps.MaxCopies = Math.Max(1, upper);
                }
                return caps;
            }
            finally
            {
                if (info != IntPtr.Zero) s_cupsFreeDestInfo(info);
                s_cupsFreeDests(1, dest);
            }
        }

        // cups_dest_t: name, instance, is_default, num_options, options
        [StructLayout(LayoutKind.Sequential)]
        private struct CupsDest { public IntPtr Name, Instance; public int IsDefault, NumOptions; public IntPtr Options; }

        private static IntPtr ByDescription(string description)
        {
            IntPtr dests = IntPtr.Zero;
            int count = s_cupsGetDests(&dests);
            if (count <= 0 || dests == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                for (int k = 0; k < count; k++)
                {
                    CupsDest* d = (CupsDest*)dests + k;
                    IntPtr info;
                    fixed (byte* key = Z("printer-info")) info = s_cupsGetOption(key, d->NumOptions, d->Options);
                    if (!string.Equals(Marshal.PtrToStringUTF8(info), description, StringComparison.Ordinal)) continue;
                    // a copy of its own, freed with the one-destination cupsFreeDests the caller makes
                    IntPtr copy = IntPtr.Zero;
                    s_cupsCopyDest((IntPtr)d, 0, &copy);      // cupsCopyDest(dest, num_dests, &dests): a new one-entry array
                    return copy;
                }
                return IntPtr.Zero;
            }
            finally { s_cupsFreeDests(count, dests); }
        }

        private static IntPtr Supported(IntPtr dest, IntPtr info, string option)
        {
            fixed (byte* o = Z(option)) return s_cupsFindDestSupported(IntPtr.Zero, dest, info, o);
        }

        private static List<string> Strings(IntPtr dest, IntPtr info, string option)
        {
            var result = new List<string>();
            IntPtr attr = Supported(dest, info, option);
            if (attr == IntPtr.Zero) return result;
            for (int k = 0, n = s_ippGetCount(attr); k < n; k++)
                if (Marshal.PtrToStringUTF8(s_ippGetString(attr, k, IntPtr.Zero)) is { } s) result.Add(s);
            return result;
        }
    }
}
