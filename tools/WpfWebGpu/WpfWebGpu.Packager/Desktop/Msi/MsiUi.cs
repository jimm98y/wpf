// The package's own dialogs: the wizard Windows Installer shows when the MSI is opened (msiexec's full UI), in the
// tables it draws them from (Dialog, Control, ControlEvent, ControlCondition, EventMapping, TextStyle, UIText, Error),
// the standard set an installer has:
//
//   first install    Welcome -> License Agreement (when the manifest has one) -> Destination Folder (Change...: the
//                    folder browser) -> Ready to Install -> progress -> Completed
//   upgrade          Welcome (it says it updates) -> License Agreement -> Ready to Install: the folder stays the one
//                    installed to, the installation's data and settings pointing there
//   installed        Repair or Remove -> Ready to Repair / Ready to Remove (with "Keep the settings and data",
//                    KEEPDATA) -> progress -> Completed
//   any time         Cancel (are you sure?), the error box, out of disk space, interrupted, ended by an error
//
// Laid out in dialog units, as Windows Installer's own samples are (370 x 270). The images are drawn here: the
// banner, white with the product's icon at its right, and the welcome's band, the icon on it.

using System.Text;

namespace WpfWebGpu.Packager.Desktop.Msi;

internal static class MsiUi
{
    // control attributes
    private const int Visible = 1, Enabled = 2, Sunken = 4, Indirect = 8, Transparent = 0x10000, NoPrefix = 0x20000,
        Progress95 = 0x10000, Shield = 0x800000,
        FixedVolume = 0x20000, RemoteVolume = 0x40000;
    private const int Std = Visible | Enabled, Label = Std | NoPrefix, Over = Std | Transparent | NoPrefix;
    private const int W = 370, H = 270;

    // what the wizard does where, by mode: an install (neither), a repair, a removal (also msiexec /x)
    private const string Installing = "NOT WPFMODE AND REMOVE<>\"ALL\"", Repairing = "WPFMODE=\"Repair\"", Removing = "WPFMODE=\"Remove\" OR REMOVE=\"ALL\"";

    /// <param name="licenseRtf">The license agreement (RTF), or null for no license page.</param>
    /// <param name="icon">The product's icon, or null.</param>
    public static void Add(MsiDatabase db, string? licenseRtf, (byte[] Rgba, int Width, int Height)? icon)
    {
        db["TextStyle"].Add("WpfFont", "Tahoma", 8, null, 0);
        db["TextStyle"].Add("WpfBold", "Tahoma", 8, null, 1);
        db["TextStyle"].Add("WpfTitle", "Tahoma", 9, null, 1);
        db["TextStyle"].Add("WpfBig", "Tahoma", 12, null, 1);
        db["Property"].Add("DefaultUIFont", "WpfFont");
        db["Property"].Add("ErrorDialog", "ErrorDlg");
        db["Property"].Add("_BrowseProperty", "INSTALLDIR");
        // drawn for 150% (a dialog unit 1.5 x 1.625 pixels at 100%): Windows Installer stretches them to their controls
        db["Binary"].Add("WpfWhite", Bitmap(1, 1, White, null, 0, 0, 0));
        db["Binary"].Add("WpfBanner", Bitmap(833, 107, White, icon, 72, 833 - 72 - 18, (107 - 72) / 2));
        db["Binary"].Add("WpfSide", Bitmap(270, 570, (0x1E, 0x5A, 0xA8), icon, 96, (270 - 96) / 2, 60));
        bool license = licenseRtf != null;

        // ---- Welcome ----
        var d = new Dlg(db, "WelcomeDlg");
        Side(d, "Welcome to the [ProductName] Setup Wizard");
        d.Control("Description", "Text", 135, 80, 220, 60, Over, null, "The Setup Wizard will install [ProductName] on your computer. Click Next to continue or Cancel to exit.");
        d.Condition("Description", "Hide", "WPFOLDER");
        d.Control("Upgrade", "Text", 135, 80, 220, 60, Over & ~Visible, null, "The Setup Wizard will update [ProductName] to version [ProductVersion]. Its settings and data are kept. Click Next to continue or Cancel to exit.");
        d.Condition("Upgrade", "Show", "WPFOLDER");
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("Back", 180, "&Back", enabled: false);
        d.Button("Next", 236, "&Next");
        if (license) d.Event("Next", "NewDialog", "LicenseAgreementDlg");
        else AfterLicense(d, "Next");
        CancelButton(d);
        d.End(first: "Next", def: "Next", cancel: "Cancel");

        // ---- License Agreement ----
        if (license)
        {
            d = new Dlg(db, "LicenseAgreementDlg");
            Banner(d, "End-User License Agreement", "Please read the following license agreement carefully");
            d.Control("LicenseText", "ScrollableText", 20, 60, 330, 140, Std | Sunken, null, licenseRtf, tab: true);
            d.Control("LicenseAcceptedCheckBox", "CheckBox", 20, 207, 330, 18, Std, "LicenseAccepted", "I &accept the terms in the License Agreement", tab: true);
            db["CheckBox"].Add("LicenseAccepted", "1");
            d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
            d.Button("Back", 180, "&Back");
            d.Event("Back", "NewDialog", "WelcomeDlg");
            d.Button("Next", 236, "&Next");
            d.Condition("Next", "Disable", "LicenseAccepted<>\"1\"");
            d.Condition("Next", "Enable", "LicenseAccepted=\"1\"");
            AfterLicense(d, "Next");
            CancelButton(d);
            d.End(first: "LicenseAcceptedCheckBox", def: "Next", cancel: "Cancel");
        }

        // ---- Destination Folder ----
        d = new Dlg(db, "InstallDirDlg");
        Banner(d, "Destination Folder", "Click Next to install to the default folder or click Change to choose another.");
        d.Control("FolderLabel", "Text", 20, 60, 290, 30, Label, null, "Install [ProductName] to:");
        d.Control("Folder", "PathEdit", 20, 100, 320, 18, Std | Indirect, "_BrowseProperty", null, tab: true);
        d.Control("ChangeFolder", "PushButton", 20, 120, 56, 17, Std, null, "&Change...", tab: true);
        d.Event("ChangeFolder", "SpawnDialog", "BrowseDlg");
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("Back", 180, "&Back");
        d.Event("Back", "NewDialog", license ? "LicenseAgreementDlg" : "WelcomeDlg");
        d.Button("Next", 236, "&Next");
        d.Event("Next", "SetTargetPath", "[_BrowseProperty]", order: 1);
        d.Event("Next", "NewDialog", "VerifyReadyDlg", order: 2);
        CancelButton(d);
        d.End(first: "Folder", def: "Next", cancel: "Cancel");

        // ---- the folder browser ----
        d = new Dlg(db, "BrowseDlg");
        Banner(d, "Change destination folder", "Browse to the destination folder");
        d.Control("ComboLabel", "Text", 25, 58, 44, 10, Std, null, "&Look in:");
        d.Control("DirectoryCombo", "DirectoryCombo", 70, 55, 208, 80, Std | Indirect | FixedVolume | RemoteVolume, "_BrowseProperty", null, tab: true);
        d.Control("Up", "PushButton", 284, 55, 28, 17, Std, null, "&Up", tab: true);
        d.Event("Up", "DirectoryListUp", "0");
        d.Control("NewFolder", "PushButton", 316, 55, 34, 17, Std, null, "Ne&w", tab: true);
        d.Event("NewFolder", "DirectoryListNew", "0");
        d.Control("DirectoryList", "DirectoryList", 25, 83, 325, 98, Std | Sunken | Indirect, "_BrowseProperty", null, tab: true);
        d.Control("PathLabel", "Text", 25, 190, 320, 10, Std, null, "&Folder name:");
        d.Control("PathEdit", "PathEdit", 25, 202, 325, 18, Std | Indirect, "_BrowseProperty", null, tab: true);
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("OK", 240, "OK");
        d.Event("OK", "SetTargetPath", "[_BrowseProperty]", order: 1);
        d.Event("OK", "EndDialog", "Return", order: 2);
        d.Button("Cancel", 304, "Cancel");
        d.Event("Cancel", "Reset", "0", order: 1);
        d.Event("Cancel", "EndDialog", "Return", order: 2);
        d.End(first: "DirectoryCombo", def: "OK", cancel: "Cancel");

        // ---- Ready to Install / Repair / Remove ----
        d = new Dlg(db, "VerifyReadyDlg");
        d.Control("BannerBitmap", "Bitmap", 0, 0, W, 44, Visible, null, "WpfBanner");
        d.Control("BannerLine", "Line", 0, 44, W, 0, Visible, null, null);
        foreach (var (mode, when, title, text) in new[]
        {
            ("Install", Installing, "Ready to install [ProductName]", "Click Install to begin the installation. Click Back to review or change any of your installation settings. Click Cancel to exit the wizard."),
            ("Repair", Repairing, "Ready to repair [ProductName]", "Click Repair to repair the installation of [ProductName]: its missing and damaged files, shortcuts and registry entries. Its settings are kept. Click Cancel to exit the wizard."),
            ("Remove", Removing, "Ready to remove [ProductName]", "Click Remove to remove [ProductName] from your computer: its programs, services and shortcuts, and, unless you keep them, its settings and data. Click Cancel to exit the wizard."),
        })
        {
            d.Control(mode + "Title", "Text", 15, 15, 300, 15, Over & ~Visible, null, @"{\WpfTitle}" + title);
            d.Control(mode + "Text", "Text", 25, 70, 320, 60, Label & ~Visible, null, text);
            d.Condition(mode + "Title", "Show", when);
            d.Condition(mode + "Text", "Show", when);
        }
        d.Control("KeepData", "CheckBox", 25, 140, 320, 18, Std & ~Visible, "KEEPDATA", "&Keep the settings and data, for a later installation", tab: true);
        db["CheckBox"].Add("KEEPDATA", "1");
        d.Condition("KeepData", "Show", Removing);
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("Back", 180, "&Back");
        d.Event("Back", "NewDialog", "MaintenanceTypeDlg", "WPFMODE");
        d.Event("Back", "NewDialog", "InstallDirDlg", "NOT WPFMODE AND NOT WPFOLDER");
        d.Event("Back", "NewDialog", license ? "LicenseAgreementDlg" : "WelcomeDlg", "NOT WPFMODE AND WPFOLDER");
        d.Control("Install", "PushButton", 236, 243, 56, 17, (Std | Shield) & ~Visible, null, "&Install", tab: true);
        d.Condition("Install", "Show", Installing);
        d.Event("Install", "SpawnDialog", "OutOfDiskDlg", "OutOfDiskSpace = 1", order: 1);
        d.Event("Install", "EndDialog", "Return", "OutOfDiskSpace <> 1", order: 2);
        d.Control("Repair", "PushButton", 236, 243, 56, 17, (Std | Shield) & ~Visible, null, "&Repair", tab: true);
        d.Condition("Repair", "Show", Repairing);
        d.Event("Repair", "ReinstallMode", "ecmus", order: 1);
        d.Event("Repair", "Reinstall", "ALL", order: 2);
        d.Event("Repair", "EndDialog", "Return", order: 3);
        d.Control("Remove", "PushButton", 236, 243, 56, 17, (Std | Shield) & ~Visible, null, "&Remove", tab: true);
        d.Condition("Remove", "Show", Removing);
        d.Event("Remove", "Remove", "ALL", order: 1);
        d.Event("Remove", "EndDialog", "Return", order: 2);
        CancelButton(d);
        d.End(first: "Install", def: "Install", cancel: "Cancel");

        // ---- Repair or Remove ----
        d = new Dlg(db, "MaintenanceTypeDlg");
        Banner(d, "Repair or remove installation", "Select the operation you wish to perform.");
        d.Control("RepairButton", "PushButton", 40, 65, 80, 17, Std, null, "&Repair", tab: true);
        d.Event("RepairButton", "[WPFMODE]", "Repair", order: 1);
        d.Event("RepairButton", "NewDialog", "VerifyReadyDlg", order: 2);
        d.Control("RepairText", "Text", 60, 85, 280, 30, Label, null, "Repairs errors in the installation: its missing and damaged files, shortcuts and registry entries.");
        d.Control("RemoveButton", "PushButton", 40, 118, 80, 17, Std, null, "Re&move", tab: true);
        d.Event("RemoveButton", "[WPFMODE]", "Remove", order: 1);
        d.Event("RemoveButton", "NewDialog", "VerifyReadyDlg", order: 2);
        d.Control("RemoveText", "Text", 60, 138, 280, 20, Label, null, "Removes [ProductName] from your computer.");
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("Back", 180, "&Back", enabled: false);
        d.Button("Next", 236, "&Next", enabled: false);
        CancelButton(d);
        d.End(first: "RepairButton", def: "RepairButton", cancel: "Cancel");

        // ---- progress (modeless) ----
        d = new Dlg(db, "ProgressDlg");
        d.Control("BannerBitmap", "Bitmap", 0, 0, W, 44, Visible, null, "WpfBanner");
        d.Control("BannerLine", "Line", 0, 44, W, 0, Visible, null, null);
        foreach (var (mode, when, title, text) in new[]
        {
            ("Install", Installing, "Installing [ProductName]", "Please wait while the Setup Wizard installs [ProductName]."),
            ("Repair", Repairing, "Repairing [ProductName]", "Please wait while the Setup Wizard repairs [ProductName]."),
            ("Remove", Removing, "Removing [ProductName]", "Please wait while the Setup Wizard removes [ProductName]."),
        })
        {
            d.Control(mode + "Title", "Text", 15, 15, 300, 15, Over & ~Visible, null, @"{\WpfTitle}" + title);
            d.Control(mode + "Text", "Text", 20, 65, 330, 35, Label & ~Visible, null, text);
            d.Condition(mode + "Title", "Show", when);
            d.Condition(mode + "Text", "Show", when);
        }
        d.Control("StatusLabel", "Text", 20, 100, 50, 10, Std, null, "Status:");
        d.Control("ActionText", "Text", 70, 100, 280, 10, Label, null, null);
        d.Map("ActionText", "ActionText", "Text");
        d.Control("ProgressBar", "ProgressBar", 20, 115, 330, 10, Visible | Progress95, null, "Progress done");
        d.Map("ProgressBar", "SetProgress", "Progress");
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("Back", 180, "&Back", enabled: false);
        d.Button("Next", 236, "&Next", enabled: false);
        CancelButton(d);
        d.End(first: "Cancel", def: "Cancel", cancel: "Cancel", attributes: Visible);

        // ---- the end: completed, cancelled, failed ----
        foreach (var (name, title, text, end) in new[]
        {
            ("ExitDialog", "Completed the [ProductName] Setup Wizard", "Click the Finish button to exit the Setup Wizard.", "Return"),
            ("UserExit", "[ProductName] Setup Wizard was interrupted", "[ProductName] setup was interrupted. Your system has not been modified. To install this program at a later time, run the installation again. Click the Finish button to exit the Setup Wizard.", "Exit"),
            ("FatalError", "[ProductName] Setup Wizard ended prematurely", "[ProductName] Setup Wizard ended prematurely because of an error. Your system has not been modified. To install this program at a later time, run the installation again. Click the Finish button to exit the Setup Wizard.", "Exit"),
        })
        {
            d = new Dlg(db, name);
            Side(d, title);
            d.Control("Description", "Text", 135, 80, 220, 100, Over, null, text);
            d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
            d.Button("Back", 180, "&Back", enabled: false);
            d.Button("Finish", 236, "&Finish");
            d.Event("Finish", "EndDialog", end);
            d.Button("Cancel", 304, "Cancel", enabled: false);
            d.End(first: "Finish", def: "Finish", cancel: "Finish");
        }

        // ---- are you sure ----
        d = new Dlg(db, "CancelDlg");
        d.Control("Text", "Text", 20, 15, 220, 30, Label, null, "Are you sure you want to cancel [ProductName] installation?");
        d.Control("Yes", "PushButton", 72, 57, 56, 17, Std, null, "&Yes", tab: true);
        d.Event("Yes", "EndDialog", "Exit");
        d.Control("No", "PushButton", 132, 57, 56, 17, Std, null, "&No", tab: true);
        d.Event("No", "EndDialog", "Return");
        d.End(first: "No", def: "No", cancel: "No", width: 260, height: 85);

        // ---- the error box (ErrorDialog): Windows Installer shows the buttons the message needs ----
        d = new Dlg(db, "ErrorDlg");
        d.Control("ErrorText", "Text", 20, 15, 230, 60, Label, null, null);
        foreach (var (control, text, end) in new[]
        {
            ("O", "OK", "ErrorOk"), ("C", "Cancel", "ErrorCancel"), ("A", "&Abort", "ErrorAbort"), ("R", "&Retry", "ErrorRetry"),
            ("I", "&Ignore", "ErrorIgnore"), ("Y", "&Yes", "ErrorYes"), ("N", "&No", "ErrorNo"),
        })
        {
            d.Control(control, "PushButton", 100, 80, 56, 17, Std, null, text, tab: true);
            d.Event(control, "EndDialog", end);
        }
        d.End(first: "O", def: null, cancel: null, width: 270, height: 105, attributes: 0x10000 | Std);

        // ---- out of disk space ----
        d = new Dlg(db, "OutOfDiskDlg");
        Banner(d, "Out of Disk Space", "The required disk space exceeds the available disk space.");
        d.Control("Text", "Text", 20, 53, 330, 40, Label, null, "The highlighted volumes do not have enough disk space available. Free some space on them, or go back and choose another destination folder.");
        d.Control("VolumeList", "VolumeCostList", 20, 100, 330, 120, Visible | Sunken | FixedVolume | RemoteVolume, null, "{120}{70}{70}{70}{70}", tab: true);
        d.Control("BottomLine", "Line", 0, 234, W, 0, Visible, null, null);
        d.Button("OK", 304, "OK");
        d.Event("OK", "EndDialog", "Return");
        d.End(first: "OK", def: "OK", cancel: "OK", attributes: Std | 0x20);

        // ---- when ----
        foreach (var (action, condition, sequence) in new (string, string?, int)[]
        {
            ("FatalError", null, -3), ("UserExit", null, -2), ("ExitDialog", null, -1),
            ("WelcomeDlg", "NOT Installed", 1297),
            ("MaintenanceTypeDlg", "Installed AND NOT RESUME AND NOT Preselected", 1298),
            ("ProgressDlg", null, 1299),
        })
            db["InstallUISequence"].Add(action, condition, sequence);

        // ---- texts ----
        foreach (var (key, text) in new[]
        {
            ("AbsentPath", (string?)null), ("bytes", "bytes"), ("KB", "KB"), ("MB", "MB"), ("GB", "GB"), ("NewFolder", "Folder|New Folder"),
            ("SelCostPending", "Compiling cost for this feature..."), ("TimeRemaining", "Time remaining: {[1] minutes }{[2] seconds}"),
            ("VolumeCostAvailable", "Available"), ("VolumeCostDifference", "Difference"), ("VolumeCostRequired", "Required"),
            ("VolumeCostSize", "Disk Size"), ("VolumeCostVolume", "Volume"),
        })
            db["UIText"].Add(key, text);
        foreach (var (action, description, template) in new[]
        {
            ("InstallValidate", "Validating install", (string?)null),
            ("RemoveExistingProducts", "Removing the earlier version", "Product: [1]"),
            ("InstallFiles", "Copying new files", "File: [1],  Directory: [9],  Size: [6]"),
            ("RemoveFiles", "Removing files", "File: [1], Directory: [9]"),
            ("CreateFolders", "Creating folders", "Folder: [1]"),
            ("RemoveFolders", "Removing folders", "Folder: [1]"),
            ("WriteRegistryValues", "Writing system registry values", "Key: [1], Name: [2], Value: [3]"),
            ("RemoveRegistryValues", "Removing system registry values", "Key: [1], Name: [2]"),
            ("CreateShortcuts", "Creating shortcuts", "Shortcut: [1]"),
            ("RemoveShortcuts", "Removing shortcuts", "Shortcut: [1]"),
            ("RegisterProduct", "Registering product", "[1]"),
            ("PublishProduct", "Publishing product information", null),
            ("WpfRecord", "Noting the settings and data new to this computer", null),
            ("WpfInstall", "Registering and starting the services", null),
            ("WpfStop", "Stopping the services and the programs running", null),
            ("WpfCleanup", "Removing the caches, logs and data", null),
        })
            db["ActionText"].Add(action, description, template);
        foreach (var (error, message) in new[]
        {
            (0, "{{Fatal error: }}"), (1, "{{Error [1]. }}"), (2, "Warning [1]. "), (4, "Info [1]. "),
            (5, "The installer has encountered an unexpected error installing this package. This may indicate a problem with this package. The error code is [1]. {{The arguments are: [2], [3], [4]}}"),
            (7, "{{Disk full: }}"), (8, "Action [Time]: [1]. [2]"), (9, "[ProductName]"), (10, "{[2]}{, [3]}{, [4]}"),
            (11, "Message type: [1], Argument: [2]"), (12, "=== Logging started: [Date]  [Time] ==="), (13, "=== Logging stopped: [Date]  [Time] ==="),
            (14, "Action start [Time]: [1]."), (15, "Action ended [Time]: [1]. Return value [2]."), (16, "Time remaining: {[1] minutes }{[2] seconds}"),
            (17, "Out of memory. Shut down other applications before retrying."), (18, "Installer is no longer responding."),
            (19, "Installer stopped prematurely."), (20, "Please wait while Windows configures [ProductName]"), (21, "Gathering required information..."),
            (22, "Removing older versions of this application..."), (23, "Preparing to remove older versions of this application..."),
            (32, "{[ProductName] }Setup completed successfully."), (33, "{[ProductName] }Setup failed."),
        })
            db["Error"].Add(error, message);
    }

    // Welcome to Next: the license, else the folder (the folder installed to, on an upgrade: Ready)
    private static void AfterLicense(Dlg d, string control)
    {
        d.Event(control, "NewDialog", "InstallDirDlg", "NOT WPFOLDER");
        d.Event(control, "NewDialog", "VerifyReadyDlg", "WPFOLDER");
    }

    private static void CancelButton(Dlg d)
    {
        d.Button("Cancel", 304, "Cancel");
        d.Event("Cancel", "SpawnDialog", "CancelDlg");
    }

    // the welcome's and the end's: the band at the side, the icon on it, the title beside
    private static void Side(Dlg d, string title)
    {
        d.Control("Band", "Bitmap", 0, 0, 120, 234, Visible, null, "WpfSide");
        d.Control("White", "Bitmap", 120, 0, W - 120, 234, Visible, null, "WpfWhite");
        d.Control("Title", "Text", 135, 20, 220, 60, Over, null, @"{\WpfBig}" + title);
    }

    // a page's top: white, its title and what it is for, the icon at the right
    private static void Banner(Dlg d, string title, string description)
    {
        d.Control("BannerBitmap", "Bitmap", 0, 0, W, 44, Visible, null, "WpfBanner");
        d.Control("BannerLine", "Line", 0, 44, W, 0, Visible, null, null);
        d.Control("Title", "Text", 15, 6, 300, 15, Over, null, @"{\WpfTitle}" + title);
        d.Control("Description", "Text", 25, 23, 300, 15, Over, null, description);
    }

    /// <summary>A dialog's rows: its controls in tab order (each one that takes the focus to the next, the last to the
    /// first), its events and conditions.</summary>
    private sealed class Dlg(MsiDatabase db, string name)
    {
        private readonly List<object?[]> _controls = new();
        private readonly List<int> _tab = new();

        public void Control(string control, string type, int x, int y, int w, int h, int attributes, string? property, string? text, bool tab = false)
        {
            if (tab) _tab.Add(_controls.Count);
            _controls.Add(new object?[] { name, control, type, x, y, w, h, attributes, property, text, null, null });
        }

        public void Button(string control, int x, string text, bool enabled = true) =>
            Control(control, "PushButton", x, 243, 56, 17, enabled ? Std : Visible, null, text, tab: true);

        public void Event(string control, string ev, string argument, string condition = "1", int order = 1) =>
            db["ControlEvent"].Add(name, control, ev, argument, condition, order);

        public void Condition(string control, string action, string condition) => db["ControlCondition"].Add(name, control, action, condition);

        public void Map(string control, string ev, string attribute) => db["EventMapping"].Add(name, control, ev, attribute);

        public void End(string first, string? def, string? cancel, int width = W, int height = H, int attributes = Std)
        {
            for (int i = 0; i < _tab.Count; i++)
                _controls[_tab[i]][10] = _controls[_tab[(i + 1) % _tab.Count]][1];
            foreach (var row in _controls) db["Control"].Add(row);
            db["Dialog"].Add(name, 50, 50, width, height, attributes, "[ProductName] Setup", first, def, cancel);
        }
    }

    private static readonly (int R, int G, int B) White = (0xFF, 0xFF, 0xFF);

    /// <summary>A 24-bit BMP of the color, the icon drawn on it (blended by its alpha) at the size and place given.</summary>
    private static byte[] Bitmap(int width, int height, (int R, int G, int B) back, (byte[] Rgba, int Width, int Height)? icon, int size, int left, int top)
    {
        byte[]? image = icon is { } i ? Icons.Scale(i.Rgba, i.Width, i.Height, size, size) : null;
        int row = (width * 3 + 3) & ~3;
        var b = new byte[54 + row * height];
        void I4(int at, int v) => BitConverter.TryWriteBytes(b.AsSpan(at, 4), v);
        b[0] = (byte)'B'; b[1] = (byte)'M';
        I4(2, b.Length); I4(10, 54); I4(14, 40); I4(18, width); I4(22, height);
        b[26] = 1; b[28] = 24;
        I4(34, row * height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int r = back.R, g = back.G, bl = back.B;
                if (image != null && x >= left && x < left + size && y >= top && y < top + size)
                {
                    int at = ((y - top) * size + (x - left)) * 4, a = image[at + 3];
                    r = (image[at] * a + r * (255 - a)) / 255;
                    g = (image[at + 1] * a + g * (255 - a)) / 255;
                    bl = (image[at + 2] * a + bl * (255 - a)) / 255;
                }
                int o = 54 + (height - 1 - y) * row + x * 3;     // the rows bottom up
                b[o] = (byte)bl; b[o + 1] = (byte)g; b[o + 2] = (byte)r;
            }
        return b;
    }
}
