// Serial ports by their Windows names. An app's System.IO.Ports carries its own Unix build and native library, which
// open a port by its device (/dev/ttyUSB0, /dev/cu.usbserial-1410) and list devices; the app knows its ports as
// Windows names them -- COM3 in its configuration, COMn in the list it shows. So the ports are named as Windows names
// them: each device the COM number the COM Name Arbiter gives it, kept for it from then on
// (HKLM\SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices: COMn = the device), the ports present listed as
// SERIALCOMM lists them (HKLM\HARDWARE\DEVICEMAP\SERIALCOMM: the device = COMn), GetPortNames answering from that,
// and Open taking COMn for its device. A device path is still taken as it is; a COM name no device has fails as
// Windows fails it ("The port 'COM9' does not exist.").
//
// A device is known by what stays the same for it: on Linux its /dev/serial/by-id name (a USB adapter's vendor,
// product and serial number), whatever ttyUSBn it is this time.

using System.Reflection;
using Microsoft.Win32;

namespace WpfWebGpu.Detours;

public static class SerialPorts
{
    private const string ArbiterKey = @"SYSTEM\CurrentControlSet\Control\COM Name Arbiter\Devices";
    private const string SerialCommKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";
    private static readonly object s_gate = new();

    // ---- naming ----------------------------------------------------------------------------------------------

    /// <summary>The COM number <paramref name="name"/> is (COM1..COM256, any case), or 0.</summary>
    public static int ComNumber(string? name) =>
        name is { Length: > 3 } && name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
        && n is >= 1 and <= 256 ? n : 0;

    /// <summary>What a device is known by: its /dev/serial/by-id name where it has one.</summary>
    public static string IdentityOf(string device)
    {
        const string byId = "/dev/serial/by-id";
        try
        {
            if (Directory.Exists(byId))
            {
                string real = Path.GetFullPath(new FileInfo(device).ResolveLinkTarget(true)?.FullName ?? device);
                foreach (string link in Directory.GetFiles(byId))
                {
                    var target = new FileInfo(link).ResolveLinkTarget(true);
                    if (target != null && Path.GetFullPath(target.FullName) == real) return link;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return device;
    }

    /// <summary>The device an identity is now: a by-id link's target, else the path itself.</summary>
    private static string DeviceOf(string identity)
    {
        try { return identity.StartsWith("/dev/serial/", StringComparison.Ordinal) ? new FileInfo(identity).ResolveLinkTarget(true)?.FullName ?? identity : identity; }
        catch (IOException) { return identity; }
    }

    /// <summary>The COM names of <paramref name="devices"/> (each given one the first time it is seen, the lowest
    /// free), in COM order, and SERIALCOMM written for them: GetPortNames' answer.</summary>
    public static string[] Assign(IEnumerable<string> devices)
    {
        lock (s_gate)
        {
            var arbiter = ReadArbiter();
            var taken = new HashSet<int>(arbiter.Keys);
            var present = new SortedDictionary<int, string>();
            foreach (string device in devices.Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal))
            {
                string identity = IdentityOf(device);
                int n = arbiter.FirstOrDefault(kv => kv.Value == identity).Key;
                if (n == 0)
                {
                    n = 1;
                    while (taken.Contains(n)) n++;
                    taken.Add(n);
                    arbiter[n] = identity;
                    Write(ArbiterKey, "COM" + n, identity);
                }
                present[n] = device;
            }
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(SerialCommKey, writable: true);
                foreach (string stale in key.GetValueNames()) key.DeleteValue(stale, throwOnMissingValue: false);
                foreach (var (n, device) in present) key.SetValue(device, "COM" + n);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException) { }
            return present.Keys.Select(n => "COM" + n).ToArray();
        }
    }

    /// <summary>The device <paramref name="portName"/> opens: a COM name's (null where no device has it), anything
    /// else as given.</summary>
    public static string? Resolve(string portName)
    {
        int n = ComNumber(portName);
        if (n == 0) return portName;
        lock (s_gate)
        {
            if (!ReadArbiter().TryGetValue(n, out string? identity)) return null;
            string device = DeviceOf(identity);
            return File.Exists(device) ? device : null;
        }
    }

    private static Dictionary<int, string> ReadArbiter()
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ArbiterKey);
            if (key == null) return result;
            foreach (string name in key.GetValueNames())
                if (ComNumber(name) is int n and > 0 && key.GetValue(name) is string identity && identity.Length > 0) result[n] = identity;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException) { }
        return result;
    }

    private static void Write(string path, string name, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            key.SetValue(name, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException) { }
    }

    // ---- the detours -------------------------------------------------------------------------------------------

    private static int s_hooked;

    /// <summary>Hooks the app's System.IO.Ports when it loads (or now, where it has).</summary>
    internal static void Install(Action<MethodBase, Delegate> add)
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => TryHook(e.LoadedAssembly, add);
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) TryHook(a, add);
    }

    private static void TryHook(Assembly asm, Action<MethodBase, Delegate> add)
    {
        if (asm.GetName().Name != "System.IO.Ports" || Interlocked.Exchange(ref s_hooked, 1) != 0) return;
        Type port = asm.GetType("System.IO.Ports.SerialPort", throwOnError: true)!;
        FieldInfo name = port.GetField("_portName", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingFieldException(port.FullName, "_portName");

        add(port.GetMethod("GetPortNames", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)!,
            (Func<Func<string[]>, string[]>)(orig => Assign(orig())));

        // the stream opened on the device; the port keeps the name it was given (PortName is COM3 still)
        add(port.GetMethod("Open", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes)!,
            (Action<Action<object>, object>)((orig, self) =>
            {
                string given = (string)name.GetValue(self)!;
                if (ComNumber(given) == 0) { orig(self); return; }
                // a port open already is refused as the stream would refuse it, before any lookup
                if (port.GetProperty("IsOpen")!.GetValue(self) is true) { orig(self); return; }
                string device = Resolve(given) ?? throw new IOException($"The port '{given}' does not exist.");
                name.SetValue(self, device);
                try { orig(self); }
                finally { name.SetValue(self, given); }
            }));
    }
}
