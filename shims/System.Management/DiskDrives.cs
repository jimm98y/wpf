// Win32_DiskDrive: the machine's physical disks, as Windows lists them -- \\.\PHYSICALDRIVEn in the system's order,
// the model as caption, an NVMe disk's interface SCSI and a SATA one's IDE (as Windows' drivers report them), USB
// for one on USB, and "Fixed hard disk media", "External hard disk media" or "Removable Media". From diskutil on
// macOS (its physical whole disks), from /sys/block on Linux (no loop, RAM, mapper or optical devices).

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace System.Management
{
    internal static class DiskDrives
    {
        private sealed record Disk(string Model, string Vendor, string Serial, string Firmware, ulong Size, uint SectorSize, string Interface, bool Removable, bool External, uint Partitions);

        public static IEnumerable<Dictionary<string, object>> All()
        {
            List<Disk> disks = OperatingSystem.IsMacOS() ? Mac() : OperatingSystem.IsLinux() ? Linux() : new List<Disk>();
            for (int i = 0; i < disks.Count; i++)
            {
                Disk d = disks[i];
                string id = @"\\.\PHYSICALDRIVE" + i.ToString(CultureInfo.InvariantCulture);
                string pnpVendor = (d.Interface == "SCSI" ? "NVME" : d.Vendor.Length > 0 ? d.Vendor : "ATA").ToUpperInvariant().Replace(' ', '_');
                yield return new Dictionary<string, object>
                {
                    ["BytesPerSector"] = d.SectorSize,
                    ["Caption"] = d.Model,
                    ["CreationClassName"] = "Win32_DiskDrive",
                    ["Description"] = "Disk drive",
                    ["DeviceID"] = id,
                    ["FirmwareRevision"] = d.Firmware,
                    ["Index"] = (uint)i,
                    ["InterfaceType"] = d.Interface,
                    ["Manufacturer"] = "(Standard disk drives)",
                    ["MediaLoaded"] = true,
                    ["MediaType"] = d.Removable ? "Removable Media" : d.External ? "External hard disk media" : "Fixed hard disk media",
                    ["Model"] = d.Model,
                    ["Name"] = id,
                    ["Partitions"] = d.Partitions,
                    ["PNPDeviceID"] = $@"{d.Interface}\DISK&VEN_{pnpVendor}&PROD_{d.Model.ToUpperInvariant().Replace(' ', '_')}\{i}",
                    ["SerialNumber"] = d.Serial,
                    ["Size"] = d.Size,
                    ["Status"] = "OK",
                    ["SystemCreationClassName"] = "Win32_ComputerSystem",
                    ["SystemName"] = Environment.MachineName.ToUpperInvariant(),
                    ["TotalSectors"] = d.SectorSize == 0 ? 0UL : d.Size / d.SectorSize,
                };
            }
        }

        // ---- macOS --------------------------------------------------------------------------------------------------

        private static List<Disk> Mac()
        {
            var disks = new List<Disk>();
            XmlElement list = Plist("list", "-plist", "physical");
            if (list == null) return disks;
            foreach (string whole in Strings(Value(list, "WholeDisks")))
            {
                XmlElement info = Plist("info", "-plist", whole);
                if (info == null) continue;
                string bus = Text(Value(info, "BusProtocol")) ?? "";
                bool usb = bus.Contains("USB", StringComparison.OrdinalIgnoreCase);
                string iface = usb ? "USB" : bus.Contains("SATA", StringComparison.OrdinalIgnoreCase) || bus.Contains("ATA", StringComparison.OrdinalIgnoreCase) ? "IDE" : "SCSI";
                string model = (Text(Value(info, "MediaName")) ?? whole).Trim();
                int partitions = Strings(Value(list, "AllDisks")).Count(d => d.StartsWith(whole + "s", StringComparison.Ordinal));
                disks.Add(new Disk(model, model.Split(' ')[0], "", "", ulong.TryParse(Text(Value(info, "TotalSize")), out ulong size) ? size : 0,
                    uint.TryParse(Text(Value(info, "DeviceBlockSize")), out uint block) ? block : 512u, iface,
                    Value(info, "Removable")?.Name == "true" || Value(info, "RemovableMedia")?.Name == "true",
                    Value(info, "Internal")?.Name == "false", (uint)partitions));
            }
            return disks;
        }

        private static XmlElement Plist(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/sbin/diskutil") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var doc = new XmlDocument { XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
                doc.Load(reader);
                return doc.DocumentElement?["dict"];
            }
            catch (Exception e) when (e is IOException or XmlException or ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        }

        // a plist dictionary's value for a key
        private static XmlElement Value(XmlElement dict, string key)
        {
            for (XmlNode n = dict?.FirstChild; n != null; n = n.NextSibling)
                if (n is XmlElement { Name: "key" } k && k.InnerText == key) return k.NextSibling as XmlElement;
            return null;
        }

        private static string Text(XmlElement e) => e?.InnerText;

        private static IEnumerable<string> Strings(XmlElement array) =>
            array?.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "string").Select(e => e.InnerText) ?? Enumerable.Empty<string>();

        // ---- Linux --------------------------------------------------------------------------------------------------

        private static List<Disk> Linux()
        {
            var disks = new List<Disk>();
            const string root = "/sys/block";
            if (!Directory.Exists(root)) return disks;
            foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal) || name.StartsWith("zram", StringComparison.Ordinal)
                    || name.StartsWith("dm-", StringComparison.Ordinal) || name.StartsWith("sr", StringComparison.Ordinal) || name.StartsWith("md", StringComparison.Ordinal)
                    || !Directory.Exists(Path.Combine(dir, "device"))) continue;
                string Read(string relative) { try { return File.ReadAllText(Path.Combine(dir, relative)).Trim(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; } }
                string real;
                try { real = new DirectoryInfo(dir).ResolveLinkTarget(true)?.FullName ?? dir; } catch (IOException) { real = dir; }
                string iface = real.Contains("/usb", StringComparison.Ordinal) ? "USB"
                    : name.StartsWith("nvme", StringComparison.Ordinal) ? "SCSI"
                    : real.Contains("/ata", StringComparison.Ordinal) ? "IDE" : "SCSI";
                string vendor = Read("device/vendor");
                string model = Read("device/model");
                if (model.Length == 0) model = name;
                if (vendor.Length > 0 && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) && iface != "IDE") model = vendor + " " + model;
                string serial = Read("device/serial");
                if (serial.Length == 0) serial = SerialFromById(name);
                ulong sectors = ulong.TryParse(Read("size"), out ulong s) ? s : 0;       // in 512-byte units, always
                uint block = uint.TryParse(Read("queue/logical_block_size"), out uint b) ? b : 512u;
                uint partitions = (uint)Directory.GetDirectories(dir).Count(d => Path.GetFileName(d).StartsWith(name, StringComparison.Ordinal));
                string firmware = Read("device/firmware_rev");
                if (firmware.Length == 0) firmware = Read("device/rev");
                disks.Add(new Disk(model, vendor, serial, firmware, sectors * 512, block, iface, Read("removable") == "1", iface == "USB", partitions));
            }
            return disks;
        }

        // /dev/disk/by-id/ata-<model>_<serial> -> the serial
        private static string SerialFromById(string device)
        {
            try
            {
                foreach (string link in Directory.GetFiles("/dev/disk/by-id"))
                {
                    string target = new FileInfo(link).ResolveLinkTarget(true)?.FullName ?? "";
                    string file = Path.GetFileName(link);
                    if (Path.GetFileName(target) == device && !file.Contains("-part", StringComparison.Ordinal) && file.StartsWith("ata-", StringComparison.Ordinal))
                        return file[(file.LastIndexOf('_') + 1)..];
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return "";
        }
    }
}
