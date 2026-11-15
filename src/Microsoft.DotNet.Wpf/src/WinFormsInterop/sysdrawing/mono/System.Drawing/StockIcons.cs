// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET 8's stock icons (SystemIcons.GetStockIcon: the shell's SHGetStockIconInfo) and its icon extraction
// (Icon.ExtractIcon: SHDefExtractIcon). On Windows both are the shell's own. Elsewhere a stock icon is the
// platform's own drawing of the same thing -- macOS's CoreTypes artwork, the freedesktop icon theme on Linux --
// and the bundled set where there is neither; an icon is extracted from an executable's resources (or an .ico)
// by reading the file, as the shell does.

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace System.Drawing
{
	public enum StockIconId
	{
		DocumentNoAssociation = 0, DocumentWithAssociation = 1, Application = 2, Folder = 3, FolderOpen = 4,
		Drive525 = 5, Drive35 = 6, DriveRemovable = 7, DriveFixed = 8, DriveNet = 9, DriveNetDisabled = 10,
		DriveCD = 11, DriveRam = 12, World = 13, Server = 15, Printer = 16, MyNetwork = 17, Find = 22, Help = 23,
		Share = 28, Link = 29, SlowFile = 30, Recycler = 31, RecyclerFull = 32, MediaCDAudio = 40, Lock = 47,
		AutoList = 49, PrinterNet = 50, ServerShare = 51, PrinterFax = 52, PrinterFaxNet = 53, PrinterFile = 54,
		Stack = 55, MediaSVCD = 56, StuffedFolder = 57, DriveUnknown = 58, DriveDVD = 59, MediaDVD = 60,
		MediaDVDRAM = 61, MediaDVDRW = 62, MediaDVDR = 63, MediaDVDROM = 64, MediaCDAudioPlus = 65, MediaCDRW = 66,
		MediaCDR = 67, MediaCDBurn = 68, MediaBlankCD = 69, MediaCDROM = 70, AudioFiles = 71, ImageFiles = 72,
		VideoFiles = 73, MixedFiles = 74, FolderBack = 75, FolderFront = 76, Shield = 77, Warning = 78, Info = 79,
		Error = 80, Key = 81, Software = 82, Rename = 83, Delete = 84, MediaAudioDVD = 85, MediaMovieDVD = 86,
		MediaEnhancedCD = 87, MediaEnhancedDVD = 88, MediaHDDVD = 89, MediaBluRay = 90, MediaVCD = 91,
		MediaDVDPlusR = 92, MediaDVDPlusRW = 93, DesktopPC = 94, MobilePC = 95, Users = 96, MediaSmartMedia = 97,
		MediaCompactFlash = 98, DeviceCellPhone = 99, DeviceCamera = 100, DeviceVideoCamera = 101,
		DeviceAudioPlayer = 102, NetworkConnect = 103, Internet = 104, ZipFile = 105, Settings = 106,
		DriveHDDVD = 132, DriveBD = 133, MediaHDDVDROM = 134, MediaHDDVDR = 135, MediaHDDVDRAM = 136,
		MediaBDROM = 137, MediaBDR = 138, MediaBDRE = 139, ClusteredDrive = 140,
	}

	[Flags]
	public enum StockIconOptions
	{
		Default = 0,
		SmallIcon = 1,
		ShellIconSize = 4,
		LinkOverlay = 32768,
		Selected = 65536,
	}

	public sealed partial class SystemIcons
	{
		const int E_INVALIDARG = unchecked ((int) 0x80070057);

		public static Icon GetStockIcon (StockIconId stockIcon, StockIconOptions options = StockIconOptions.Default)
		{
			if (OperatingSystem.IsWindows ()) {
				var info = new SHSTOCKICONINFO { cbSize = (uint) Marshal.SizeOf<SHSTOCKICONINFO> () };
				Marshal.ThrowExceptionForHR (SHGetStockIconInfo ((int) stockIcon, (uint) options | SHGSI_ICON, ref info));
				return Owned (info.hIcon);
			}
			// SHGSI_LARGEICON is the system's large icon (SM_CXICON), SHGSI_SMALLICON its small one; the shell's
			// sizes are the same two here.
			int size = (options & StockIconOptions.SmallIcon) != 0 ? 16 : 32;
			return StockIcons.Make (stockIcon, size, options);
		}

		public static Icon GetStockIcon (StockIconId stockIcon, int size)
		{
			if (OperatingSystem.IsWindows ()) {
				var info = new SHSTOCKICONINFO { cbSize = (uint) Marshal.SizeOf<SHSTOCKICONINFO> () };
				Marshal.ThrowExceptionForHR (SHGetStockIconInfo ((int) stockIcon, SHGSI_ICONLOCATION, ref info));
				Marshal.ThrowExceptionForHR (Icon.SHDefExtractIcon (info.szPath, info.iIcon, 0, out IntPtr hicon, IntPtr.Zero, (uint) (ushort) size << 16 | (ushort) size));
				return Owned (hicon);
			}
			return StockIcons.Make (stockIcon, size <= 0 ? 32 : size, StockIconOptions.Default);
		}

		internal static Icon Owned (IntPtr hicon)
		{
			Icon icon = Icon.FromHandle (hicon);
			icon.ownHandle = true;
			return icon;
		}

		internal static void Invalid () => Marshal.ThrowExceptionForHR (E_INVALIDARG);

		const uint SHGSI_ICONLOCATION = 0, SHGSI_ICON = 0x100;

		[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct SHSTOCKICONINFO
		{
			internal uint cbSize;
			internal IntPtr hIcon;
			internal int iSysImageIndex;
			internal int iIcon;
			[MarshalAs (UnmanagedType.ByValTStr, SizeConst = 260)]
			internal string szPath;
		}

		[DllImport ("shell32.dll", CharSet = CharSet.Unicode)]
		static extern int SHGetStockIconInfo (int siid, uint flags, ref SHSTOCKICONINFO info);
	}

	public sealed partial class Icon
	{
		public static Icon ExtractIcon (string filePath, int id, int size)
		{
			if (size <= 0 || size > ushort.MaxValue)
				throw new ArgumentOutOfRangeException (nameof (size));
			return ExtractIcon (filePath, id, size, smallIcon: false);
		}

		public static Icon ExtractIcon (string filePath, int id, bool smallIcon = false) => ExtractIcon (filePath, id, 0, smallIcon);

		/// <summary>SHDefExtractIcon: the icon at the index (a negative id is a resource id), at the size (the
		/// system's large or small one for none); none there is null, a file that cannot be read an IOException.</summary>
		static Icon ExtractIcon (string filePath, int id, int size, bool smallIcon)
		{
			ArgumentNullException.ThrowIfNull (filePath);
			if (OperatingSystem.IsWindows ()) {
				IntPtr hicon;
				int hr = smallIcon
					? SHDefExtractIcon (filePath, id, 0, IntPtr.Zero, out hicon, (uint) size << 16 | (uint) size)
					: SHDefExtractIcon (filePath, id, 0, out hicon, IntPtr.Zero, (uint) size << 16 | (uint) size);
				if (hr == 1)
					return null;
				if (hr < 0)
					throw new IOException ("The icon could not be extracted from the file.", new COMException (null, hr));
				return SystemIcons.Owned (hicon);
			}
			byte [] ico;
			try {
				ico = StockIcons.ReadIcon (filePath, id);
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) {
				throw new IOException ("The icon could not be extracted from the file.", e);
			}
			if (ico == null)
				return null;
			int want = size > 0 ? size : smallIcon ? 16 : 32;
			var icon = new Icon (new MemoryStream (ico), want, want);
			// the shell resamples the nearest size it has to the one asked for
			return icon.Width == want && icon.Height == want ? icon : StockIcons.Resized (icon, want);
		}

		[DllImport ("shell32.dll", CharSet = CharSet.Unicode)]
		internal static extern int SHDefExtractIcon (string iconFile, int index, uint flags, out IntPtr large, IntPtr small, uint size);

		[DllImport ("shell32.dll", CharSet = CharSet.Unicode)]
		static extern int SHDefExtractIcon (string iconFile, int index, uint flags, IntPtr large, out IntPtr small, uint size);
	}

	static class StockIcons
	{
		// ---- the artwork, per platform ------------------------------------------------------------------

		const string CT = "/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/";
		const string KX = "/System/Library/Extensions/";
		const string PC = "/System/Library/Frameworks/ApplicationServices.framework/Versions/A/Frameworks/PrintCore.framework/Versions/A/Resources/";

		/// <summary>What each platform draws for a stock icon: macOS's .icns, the freedesktop names (the first
		/// the theme has), and the bundled icon where neither is there.</summary>
		static readonly Dictionary<StockIconId, (string Mac, string Linux, string Bundled)> s_art = new () {
			[StockIconId.DocumentNoAssociation] = (CT + "GenericDocumentIcon.icns", "text-x-generic,unknown", null),
			[StockIconId.DocumentWithAssociation] = (CT + "GenericDocumentIcon.icns", "text-x-generic,unknown", null),
			[StockIconId.Application] = (CT + "GenericApplicationIcon.icns", "application-x-executable,application-x-generic", "Mono.ico"),
			[StockIconId.Folder] = (CT + "GenericFolderIcon.icns", "folder", null),
			[StockIconId.FolderOpen] = (CT + "OpenFolderIcon.icns", "folder-open,folder", null),
			[StockIconId.Drive525] = (KX + "IOStorageFamily.kext/Contents/Resources/Removable.icns", "media-floppy,drive-removable-media", null),
			[StockIconId.Drive35] = (KX + "IOStorageFamily.kext/Contents/Resources/Removable.icns", "media-floppy,drive-removable-media", null),
			[StockIconId.DriveRemovable] = (KX + "IOStorageFamily.kext/Contents/Resources/Removable.icns", "drive-removable-media", null),
			[StockIconId.DriveFixed] = (KX + "IOStorageFamily.kext/Contents/Resources/Internal.icns", "drive-harddisk", null),
			[StockIconId.DriveNet] = (CT + "SidebarServerDrive.icns", "folder-remote,network-server", null),
			[StockIconId.DriveNetDisabled] = (CT + "SidebarServerDrive.icns", "folder-remote,network-server", null),
			[StockIconId.DriveCD] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD.icns", "drive-optical", null),
			[StockIconId.DriveRam] = (KX + "IOStorageFamily.kext/Contents/Resources/Internal.icns", "drive-harddisk-solidstate,drive-harddisk", null),
			[StockIconId.World] = (CT + "GenericNetworkIcon.icns", "applications-internet,network-workgroup", null),
			[StockIconId.Server] = (CT + "SidebarServerDrive.icns", "network-server", null),
			[StockIconId.Printer] = (PC + "GenericInkjetPrinter.icns", "printer", null),
			[StockIconId.MyNetwork] = (CT + "GenericNetworkIcon.icns", "network-workgroup", null),
			[StockIconId.Find] = (CT + "MagnifyingGlassIcon.icns", "system-search,edit-find", null),
			[StockIconId.Help] = (CT + "HelpIcon.icns", "help-browser,help-contents", "Question.ico"),
			[StockIconId.Share] = (CT + "PublicFolderIcon.icns", "emblem-shared,folder-publicshare", null),
			[StockIconId.Link] = (CT + "AliasBadgeIcon.icns", "emblem-symbolic-link", null),
			[StockIconId.SlowFile] = (CT + "Clock.icns", "document-open-recent,text-x-generic", null),
			[StockIconId.Recycler] = (CT + "TrashIcon.icns", "user-trash", null),
			[StockIconId.RecyclerFull] = (CT + "FullTrashIcon.icns", "user-trash-full", null),
			[StockIconId.MediaCDAudio] = (CT + "CDAudioVolumeIcon.icns", "media-optical-audio,media-optical", null),
			[StockIconId.Lock] = (CT + "LockedIcon.icns", "changes-prevent,system-lock-screen", null),
			[StockIconId.AutoList] = (CT + "GenericDocumentIcon.icns", "view-list,text-x-generic", null),
			[StockIconId.PrinterNet] = (PC + "PrinterPool.icns", "printer-network,printer", null),
			[StockIconId.ServerShare] = (CT + "SidebarServerDrive.icns", "folder-remote,network-server", null),
			[StockIconId.PrinterFax] = (PC + "GenericFax.icns", "printer,fax", null),
			[StockIconId.PrinterFaxNet] = (PC + "GenericFax.icns", "printer-network,printer", null),
			[StockIconId.PrinterFile] = (PC + "GenericPostscriptPrinter.icns", "printer", null),
			[StockIconId.Stack] = (CT + "MultipleItemsIcon.icns", "folder", null),
			[StockIconId.MediaSVCD] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD.icns", "media-optical-video,media-optical", null),
			[StockIconId.StuffedFolder] = (CT + "GenericFolderIcon.icns", "folder", null),
			[StockIconId.DriveUnknown] = (KX + "IOStorageFamily.kext/Contents/Resources/External.icns", "drive-harddisk", null),
			[StockIconId.DriveDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "drive-optical", null),
			[StockIconId.MediaDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaDVDRAM] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD-RAM.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaDVDRW] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD-RW.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaDVDR] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD-R.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaDVDROM] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaCDAudioPlus] = (CT + "CDAudioVolumeIcon.icns", "media-optical-audio,media-optical", null),
			[StockIconId.MediaCDRW] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD-RW.icns", "media-optical-cd,media-optical", null),
			[StockIconId.MediaCDR] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD-R.icns", "media-optical-cd,media-optical", null),
			[StockIconId.MediaCDBurn] = (CT + "BurningIcon.icns", "media-optical-burn,media-optical", null),
			[StockIconId.MediaBlankCD] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD-R.icns", "media-optical-cd,media-optical", null),
			[StockIconId.MediaCDROM] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD.icns", "media-optical-cd,media-optical", null),
			[StockIconId.AudioFiles] = (CT + "ClippingSound.icns", "audio-x-generic", null),
			[StockIconId.ImageFiles] = (CT + "ClippingPicture.icns", "image-x-generic", null),
			[StockIconId.VideoFiles] = (CT + "SidebarMoviesFolder.icns", "video-x-generic", null),
			[StockIconId.MixedFiles] = (CT + "MultipleItemsIcon.icns", "folder", null),
			[StockIconId.FolderBack] = (CT + "GenericFolderIcon.icns", "folder", null),
			[StockIconId.FolderFront] = (CT + "GenericFolderIcon.icns", "folder", null),
			[StockIconId.Shield] = (null, "security-high,emblem-important", "Shield.ico"),
			[StockIconId.Warning] = (CT + "AlertCautionBadgeIcon.icns", "dialog-warning", "Warning.ico"),
			[StockIconId.Info] = (CT + "AlertNoteIcon.icns", "dialog-information", "Information.ico"),
			[StockIconId.Error] = (CT + "AlertStopIcon.icns", "dialog-error", "Error.ico"),
			[StockIconId.Key] = (CT + "LockedIcon.icns", "dialog-password,channel-secure", null),
			[StockIconId.Software] = (CT + "GenericApplicationIcon.icns", "system-software-install,applications-other", null),
			[StockIconId.Rename] = (CT + "GenericDocumentIcon.icns", "edit-rename,document-edit", null),
			[StockIconId.Delete] = (CT + "ToolbarDeleteIcon.icns", "edit-delete,user-trash", null),
			[StockIconId.MediaAudioDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-audio,media-optical", null),
			[StockIconId.MediaMovieDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-video,media-optical", null),
			[StockIconId.MediaEnhancedCD] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD.icns", "media-optical-cd,media-optical", null),
			[StockIconId.MediaEnhancedDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaHDDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaBluRay] = (KX + "IOBDStorageFamily.kext/Contents/Resources/BD.icns", "media-optical-bd,media-optical", null),
			[StockIconId.MediaVCD] = (KX + "IOCDStorageFamily.kext/Contents/Resources/CD.icns", "media-optical-video,media-optical", null),
			[StockIconId.MediaDVDPlusR] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD+R.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaDVDPlusRW] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD+RW.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.DesktopPC] = (CT + "public.generic-pc.icns", "computer", null),
			[StockIconId.MobilePC] = (CT + "SidebarLaptop.icns", "computer-laptop,computer", null),
			[StockIconId.Users] = (CT + "UsersFolderIcon.icns", "system-users,avatar-default", null),
			[StockIconId.MediaSmartMedia] = (KX + "IOStorageFamily.kext/Contents/Resources/Removable.icns", "media-flash,drive-removable-media", null),
			[StockIconId.MediaCompactFlash] = (KX + "IOStorageFamily.kext/Contents/Resources/Removable.icns", "media-flash,drive-removable-media", null),
			[StockIconId.DeviceCellPhone] = (CT + "SidebariPhone.icns", "phone,smartphone", null),
			[StockIconId.DeviceCamera] = (null, "camera-photo", null),
			[StockIconId.DeviceVideoCamera] = (null, "camera-video,camera-web", null),
			[StockIconId.DeviceAudioPlayer] = (CT + "SidebariPodTouch.icns", "multimedia-player,audio-x-generic", null),
			[StockIconId.NetworkConnect] = (CT + "GenericNetworkIcon.icns", "network-wired,network-workgroup", null),
			[StockIconId.Internet] = (CT + "InternetLocation.icns", "applications-internet,web-browser", null),
			[StockIconId.ZipFile] = ("/System/Library/CoreServices/Applications/Archive Utility.app/Contents/Resources/ArchiveUtility.icns", "package-x-generic,application-zip", null),
			[StockIconId.Settings] = ("/System/Applications/System Settings.app/Contents/Resources/Settings.icns", "preferences-system,applications-system", null),
			[StockIconId.DriveHDDVD] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "drive-optical", null),
			[StockIconId.DriveBD] = (KX + "IOBDStorageFamily.kext/Contents/Resources/BD.icns", "drive-optical", null),
			[StockIconId.MediaHDDVDROM] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaHDDVDR] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD-R.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaHDDVDRAM] = (KX + "IODVDStorageFamily.kext/Contents/Resources/DVD-RAM.icns", "media-optical-dvd,media-optical", null),
			[StockIconId.MediaBDROM] = (KX + "IOBDStorageFamily.kext/Contents/Resources/BD.icns", "media-optical-bd,media-optical", null),
			[StockIconId.MediaBDR] = (KX + "IOBDStorageFamily.kext/Contents/Resources/BD-R.icns", "media-optical-bd,media-optical", null),
			[StockIconId.MediaBDRE] = (KX + "IOBDStorageFamily.kext/Contents/Resources/BD-RE.icns", "media-optical-bd,media-optical", null),
			[StockIconId.ClusteredDrive] = (CT + "SidebarServerDrive.icns", "network-server,drive-harddisk", null),
		};

		internal static Icon Make (StockIconId id, int size, StockIconOptions options)
		{
			// the shell refuses an id it does not know (E_INVALIDARG)
			if (!s_art.TryGetValue (id, out var art))
				SystemIcons.Invalid ();
			using Bitmap picture = Picture (art, size) ?? Bundled (art.Bundled ?? "Mono.ico", size);
			using var icon = new Bitmap (size, size, PixelFormat.Format32bppArgb);
			using (Graphics g = Graphics.FromImage (icon)) {
				g.InterpolationMode = InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = PixelOffsetMode.HighQuality;
				g.DrawImage (picture, new Rectangle (0, 0, size, size));
				if ((options & StockIconOptions.LinkOverlay) != 0 && id != StockIconId.Link)
					DrawLinkOverlay (g, size);
			}
			if ((options & StockIconOptions.Selected) != 0)
				Select (icon);
			return FromBitmap (icon);
		}

		static Bitmap Picture ((string Mac, string Linux, string Bundled) art, int size)
		{
			try {
				if (OperatingSystem.IsMacOS () && art.Mac != null && File.Exists (art.Mac))
					return FromIcns (File.ReadAllBytes (art.Mac), size);
				if (OperatingSystem.IsLinux () && art.Linux != null)
					foreach (string name in art.Linux.Split (','))
						if (IconTheme.Find (name, size) is string png)
							return new Bitmap (new MemoryStream (File.ReadAllBytes (png)));
			} catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException) {
				// the platform's file unreadable: the bundled icon
			}
			return null;
		}

		static Bitmap Bundled (string resource, int size)
		{
			using Stream s = typeof (SystemIcons).Assembly.GetManifestResourceStream (resource);
			using var icon = new Icon (s, size, size);
			return icon.ToBitmap ();
		}

		/// <summary>The shortcut overlay: the platform's link badge, the arrow drawn where there is none.</summary>
		static void DrawLinkOverlay (Graphics g, int size)
		{
			string badge = null;
			if (OperatingSystem.IsMacOS () && File.Exists (CT + "AliasBadgeIcon.icns")) {
				using Bitmap b = FromIcns (File.ReadAllBytes (CT + "AliasBadgeIcon.icns"), size);
				if (b != null) {
					// macOS draws its alias badge over the whole icon
					g.DrawImage (b, new Rectangle (0, 0, size, size));
					return;
				}
			}
			if (OperatingSystem.IsLinux ())
				badge = IconTheme.Find ("emblem-symbolic-link", size / 2);
			int s = Math.Max (8, size / 2);
			var box = new Rectangle (0, size - s, s, s);
			if (badge != null) {
				using var b = new Bitmap (new MemoryStream (File.ReadAllBytes (badge)));
				g.DrawImage (b, box);
				return;
			}
			// Windows' shortcut arrow: white box, a curved blue arrow up and to the right
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (var back = new SolidBrush (Color.White))
				g.FillRectangle (back, box.X, box.Y, box.Width - 1, box.Height - 1);
			using (var edge = new Pen (Color.FromArgb (160, 160, 160)))
				g.DrawRectangle (edge, box.X, box.Y, box.Width - 1, box.Height - 1);
			using var arrow = new Pen (Color.FromArgb (0, 84, 166), Math.Max (1.5f, s / 8f)) { EndCap = LineCap.ArrowAnchor };
			g.DrawBezier (arrow, box.X + s * 0.25f, box.Bottom - s * 0.2f, box.X + s * 0.25f, box.Y + s * 0.45f,
				box.X + s * 0.45f, box.Y + s * 0.3f, box.Right - s * 0.25f, box.Y + s * 0.3f);
		}

		/// <summary>SHGSI_SELECTED: the icon blended with the selection colour.</summary>
		static void Select (Bitmap icon)
		{
			Color h = SystemColors.Highlight;
			for (int y = 0; y < icon.Height; y++)
				for (int x = 0; x < icon.Width; x++) {
					Color c = icon.GetPixel (x, y);
					if (c.A == 0)
						continue;
					icon.SetPixel (x, y, Color.FromArgb (c.A, (c.R + h.R) / 2, (c.G + h.G) / 2, (c.B + h.B) / 2));
				}
		}

		internal static Icon Resized (Icon icon, int size)
		{
			using (icon) {
				using Bitmap b = icon.ToBitmap ();
				using var scaled = new Bitmap (size, size, PixelFormat.Format32bppArgb);
				using (Graphics g = Graphics.FromImage (scaled)) {
					g.InterpolationMode = InterpolationMode.HighQualityBicubic;
					g.PixelOffsetMode = PixelOffsetMode.HighQuality;
					g.DrawImage (b, new Rectangle (0, 0, size, size));
				}
				return FromBitmap (scaled);
			}
		}

		/// <summary>An icon of one PNG image: an .ico of one entry.</summary>
		static Icon FromBitmap (Bitmap image)
		{
			var png = new MemoryStream ();
			image.Save (png, ImageFormat.Png);
			var ico = new MemoryStream ();
			var w = new BinaryWriter (ico);
			w.Write ((ushort) 0); w.Write ((ushort) 1); w.Write ((ushort) 1);
			w.Write ((byte) (image.Width >= 256 ? 0 : image.Width));
			w.Write ((byte) (image.Height >= 256 ? 0 : image.Height));
			w.Write ((byte) 0); w.Write ((byte) 0);
			w.Write ((ushort) 1); w.Write ((ushort) 32);
			w.Write ((int) png.Length); w.Write (22);
			png.WriteTo (ico);
			ico.Position = 0;
			return new Icon (ico);
		}

		// ---- macOS's .icns: the PNG entry nearest above the size --------------------------------------

		static readonly Dictionary<string, int> s_icnsSizes = new () {
			["icp4"] = 16, ["icp5"] = 32, ["icp6"] = 64, ["ic11"] = 32, ["ic12"] = 64, ["ic07"] = 128,
			["ic08"] = 256, ["ic13"] = 256, ["ic09"] = 512, ["ic14"] = 512, ["ic10"] = 1024,
		};

		static Bitmap FromIcns (byte [] d, int size)
		{
			if (d.Length < 8 || d [0] != 'i' || d [1] != 'c' || d [2] != 'n' || d [3] != 's')
				return null;
			int best = -1, bestSize = 0, bestLength = 0;
			for (int i = 8; i + 8 <= d.Length;) {
				string type = System.Text.Encoding.ASCII.GetString (d, i, 4);
				int length = (d [i + 4] << 24) | (d [i + 5] << 16) | (d [i + 6] << 8) | d [i + 7];
				if (length < 8 || i + length > d.Length)
					break;
				bool png = length > 16 && d [i + 8] == 0x89 && d [i + 9] == (byte) 'P';
				if (png && s_icnsSizes.TryGetValue (type, out int px)) {
					// the smallest at or above the size; else the largest there is
					bool better = best < 0 || (px >= size ? bestSize < size || px < bestSize : bestSize < size && px > bestSize);
					if (better) {
						best = i + 8; bestSize = px; bestLength = length - 8;
					}
				}
				i += length;
			}
			return best < 0 ? null : new Bitmap (new MemoryStream (d, best, bestLength));
		}

		// ---- the freedesktop icon theme ------------------------------------------------------------------

		static class IconTheme
		{
			static List<string> s_roots;
			static List<string> s_themes;

			/// <summary>A PNG of the name in the user's theme, the themes it inherits, then hicolor: the size
			/// nearest at or above the one asked for.</summary>
			internal static string Find (string name, int size)
			{
				s_roots ??= Roots ();
				s_themes ??= Themes ();
				foreach (string theme in s_themes) {
					string best = null;
					int bestSize = 0;
					foreach (string root in s_roots) {
						string dir = Path.Combine (root, theme);
						foreach ((string sub, int px) in Directories (dir)) {
							string file = Path.Combine (dir, sub, name + ".png");
							if (!File.Exists (file))
								continue;
							bool better = best == null || (px >= size ? bestSize < size || px < bestSize : bestSize < size && px > bestSize);
							if (better) {
								best = file; bestSize = px;
							}
						}
					}
					if (best != null)
						return best;
				}
				foreach (string root in new [] { "/usr/share/pixmaps" })
					if (File.Exists (Path.Combine (root, name + ".png")))
						return Path.Combine (root, name + ".png");
				return null;
			}

			static List<string> Roots ()
			{
				var roots = new List<string> ();
				string home = Environment.GetEnvironmentVariable ("HOME");
				if (!string.IsNullOrEmpty (home))
					roots.Add (Path.Combine (home, ".icons"));
				string dataHome = Environment.GetEnvironmentVariable ("XDG_DATA_HOME");
				if (string.IsNullOrEmpty (dataHome) && !string.IsNullOrEmpty (home))
					dataHome = Path.Combine (home, ".local", "share");
				if (!string.IsNullOrEmpty (dataHome))
					roots.Add (Path.Combine (dataHome, "icons"));
				string dirs = Environment.GetEnvironmentVariable ("XDG_DATA_DIRS");
				foreach (string d in (string.IsNullOrEmpty (dirs) ? "/usr/local/share:/usr/share" : dirs).Split (':'))
					if (d.Length > 0)
						roots.Add (Path.Combine (d, "icons"));
				return roots;
			}

			static List<string> Themes ()
			{
				var themes = new List<string> ();
				void Add (string theme)
				{
					if (string.IsNullOrEmpty (theme) || themes.Contains (theme))
						return;
					themes.Add (theme);
					foreach (string root in s_roots) {
						string index = Path.Combine (root, theme, "index.theme");
						if (!File.Exists (index))
							continue;
						foreach (string line in File.ReadLines (index))
							if (line.StartsWith ("Inherits=", StringComparison.Ordinal)) {
								foreach (string parent in line.Substring (9).Split (','))
									Add (parent.Trim ());
								return;
							}
						return;
					}
				}
				Add (UserTheme ());
				Add ("Adwaita");
				Add ("hicolor");
				return themes;
			}

			static string UserTheme ()
			{
				try {
					using var p = System.Diagnostics.Process.Start (new System.Diagnostics.ProcessStartInfo ("gsettings", "get org.gnome.desktop.interface icon-theme") {
						RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
					});
					if (p == null || !p.WaitForExit (2000))
						return null;
					return p.StandardOutput.ReadToEnd ().Trim ().Trim ('\'', '"');
				} catch {
					return null;
				}
			}

			static readonly Dictionary<string, List<(string, int)>> s_dirs = new ();

			/// <summary>A theme's directories of fixed-size images, from its index (Directories=, each one's Size and
			/// Scale).</summary>
			static List<(string, int)> Directories (string theme)
			{
				if (s_dirs.TryGetValue (theme, out var known))
					return known;
				var dirs = new List<(string, int)> ();
				string index = Path.Combine (theme, "index.theme");
				if (File.Exists (index)) {
					string section = null;
					int sz = 0, scale = 1;
					string type = null;
					var list = new HashSet<string> ();
					void End ()
					{
						if (section != null && list.Contains (section) && sz > 0 && type != "Scalable")
							dirs.Add ((section, sz * scale));
					}
					foreach (string raw in File.ReadLines (index)) {
						string line = raw.Trim ();
						if (line.StartsWith ('[') && line.EndsWith (']')) {
							End ();
							section = line [1..^1];
							sz = 0; scale = 1; type = null;
						} else if (section == "Icon Theme" && line.StartsWith ("Directories=", StringComparison.Ordinal)) {
							foreach (string d in line.Substring (12).Split (','))
								list.Add (d.Trim ());
						} else if (line.StartsWith ("Size=", StringComparison.Ordinal))
							int.TryParse (line.AsSpan (5), out sz);
						else if (line.StartsWith ("Scale=", StringComparison.Ordinal))
							int.TryParse (line.AsSpan (6), out scale);
						else if (line.StartsWith ("Type=", StringComparison.Ordinal))
							type = line.Substring (5);
					}
					End ();
				}
				s_dirs [theme] = dirs;
				return dirs;
			}
		}

		// ---- an executable's icons ------------------------------------------------------------------------

		const int RtIcon = 3, RtGroupIcon = 14;

		/// <summary>The icon an index (or, negative, a resource id) names in an executable's resources, as an .ico;
		/// an .ico file is its own one icon. Null when there is no such icon.</summary>
		internal static byte [] ReadIcon (string path, int id)
		{
			byte [] head = new byte [4];
			using (FileStream f = File.OpenRead (path))
				if (f.Read (head, 0, 4) < 4)
					return null;
			if (head [0] == 0 && head [1] == 0 && head [2] == 1 && head [3] == 0)
				return id == 0 ? File.ReadAllBytes (path) : null;
			if (head [0] != 'M' || head [1] != 'Z')
				return null;
			try {
				using FileStream stream = File.OpenRead (path);
				using var pe = new PEReader (stream);
				var dir = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
				if (dir is not { RelativeVirtualAddress: > 0 } d)
					return null;
				byte [] root = pe.GetSectionData (d.RelativeVirtualAddress).GetContent ().ToArray ();

				byte [] Leaf (int nameDir)
				{
					int leaf = First (root, nameDir);   // the first language
					if (leaf < 0 || leaf + 8 > root.Length)
						return null;
					int rva = BitConverter.ToInt32 (root, leaf), size = BitConverter.ToInt32 (root, leaf + 4);
					var data = pe.GetSectionData (rva);
					return size <= 0 || data.Length < size ? null : data.GetContent (0, size).ToArray ();
				}

				int groups = Find (root, 0, RtGroupIcon), icons = Find (root, 0, RtIcon);
				if (groups < 0 || icons < 0)
					return null;
				int groupDir = id < 0 ? Find (root, groups, -id) : Nth (root, groups, id);
				if (groupDir < 0)
					return null;
				byte [] group = Leaf (groupDir);
				if (group == null || group.Length < 6)
					return null;
				int count = BitConverter.ToUInt16 (group, 4);
				var images = new List<(byte [] Entry, byte [] Image)> ();
				for (int n = 0; n < count && 6 + n * 14 + 14 <= group.Length; n++) {
					int at = 6 + n * 14;
					int imageDir = Find (root, icons, BitConverter.ToUInt16 (group, at + 12));
					byte [] image = imageDir < 0 ? null : Leaf (imageDir);
					if (image != null)
						images.Add ((group [at..(at + 12)], image));
				}
				if (images.Count == 0)
					return null;
				using var ico = new MemoryStream ();
				using var w = new BinaryWriter (ico);
				w.Write ((ushort) 0); w.Write ((ushort) 1); w.Write ((ushort) images.Count);
				int offset = 6 + 16 * images.Count;
				foreach (var (entry, image) in images) {
					w.Write (entry, 0, 8);
					w.Write (image.Length);
					w.Write (offset);
					offset += image.Length;
				}
				foreach (var (_, image) in images)
					w.Write (image);
				return ico.ToArray ();
			} catch (BadImageFormatException) {
				return null;
			}

			// IMAGE_RESOURCE_DIRECTORY: 16 bytes, its named entries, then its id entries (8 bytes each); an
			// entry's offset has its top bit set when it is another directory.
			static int Find (byte [] r, int at, int id)
			{
				if (at + 16 > r.Length)
					return -1;
				int named = BitConverter.ToUInt16 (r, at + 12), ids = BitConverter.ToUInt16 (r, at + 14);
				for (int e = named; e < named + ids; e++) {
					int p = at + 16 + e * 8;
					if (p + 8 > r.Length)
						return -1;
					if (BitConverter.ToInt32 (r, p) == id)
						return BitConverter.ToInt32 (r, p + 4) & 0x7fffffff;
				}
				return -1;
			}

			// the index-th entry, named ones first, as the shell counts them
			static int Nth (byte [] r, int at, int index)
			{
				if (at + 16 > r.Length)
					return -1;
				int total = BitConverter.ToUInt16 (r, at + 12) + BitConverter.ToUInt16 (r, at + 14);
				int p = at + 16 + index * 8;
				return index >= total || p + 8 > r.Length ? -1 : BitConverter.ToInt32 (r, p + 4) & 0x7fffffff;
			}

			static int First (byte [] r, int at) => Nth (r, at, 0);
		}
	}
}
