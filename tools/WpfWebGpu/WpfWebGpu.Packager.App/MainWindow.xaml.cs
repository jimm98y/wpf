// The Packager's window: a manifest (*.wpfpkg.json) made by scanning an installation, checked and edited page by
// page -- the installation, the product, its apps and services, SQL Server, signing -- and built for the targets
// ticked, the build's log as it goes. The engine is the command line's (Scanner, Builder), run off the window's
// thread.

using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace WpfWebGpu.Packager.App;

public partial class MainWindow : Window
{
    private PackageManifest _manifest = new();
    private string? _path;
    private readonly Dictionary<Target, CheckBox> _targets = new();
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        KindColumn.ItemsSource = Enum.GetValues<AppKind>();
        StartColumn.ItemsSource = Enum.GetValues<StartType>();
        AccountColumn.ItemsSource = Enum.GetValues<ServiceAccount>();
        foreach (Target target in Enum.GetValues<Target>())
        {
            var box = new CheckBox { Content = TargetName(target), Margin = new Thickness(0, 4, 20, 4) };
            // a Mac's package is made by productbuild, which only a Mac has
            if (target == Target.MacOS && !OperatingSystem.IsMacOS())
            {
                box.IsEnabled = false;
                box.ToolTip = "made on a Mac (productbuild)";
            }
            _targets[target] = box;
            TargetsPanel.Children.Add(box);
        }
        PacksFolder.Text = PackFile.DefaultFolder;
        Show(new PackageManifest(), null);
        _ = CheckAotAsync();
    }

    // the ahead-of-time pages offered only where this machine can build them; why not, where it cannot
    private async Task CheckAotAsync()
    {
        string? why = await Task.Run(AotPages.Unavailable);
        AotBuild.IsEnabled = why == null;
        AotBuild.ToolTip = why == null ? "Faster pages, built with this machine's .NET SDK and its wasm-tools workload (slow to build)" : "Not available: " + why;
        if (why != null) AotBuild.IsChecked = false;
    }

    private static string TargetName(Target target) => target switch
    {
        Target.MacOS => "macOS", Target.IOS => "iOS / iPadOS", Target.VisionOS => "Apple Vision Pro", Target.Quest => "Meta Quest",
        _ => target.ToString(),
    };

    // ---- the manifest ---------------------------------------------------------------------------------------

    private void Show(PackageManifest manifest, string? path)
    {
        _manifest = manifest;
        _path = path;
        DataContext = null;
        DataContext = manifest;
        ManifestPath.Text = path ?? "A new manifest: scan an installation, or open one";
        Title = (path != null ? Path.GetFileName(path) + " – " : "") + "Packager";
        Refresh();
        foreach (var (target, box) in _targets) box.IsChecked = manifest.Output.Targets.Contains(target);
        FastBuild.IsChecked = manifest.Output.Fast;
        AotBuild.IsChecked = manifest.Output.Aot;
        OutputFolder.Text = manifest.Output.Directory;
    }

    // the lists the window shows that are not bound to a notifying collection, shown again
    private void Refresh()
    {
        ProgramDataList.ItemsSource = null;
        ProgramDataList.ItemsSource = _manifest.Inputs.ProgramData;
        RegistryList.ItemsSource = null;
        RegistryList.ItemsSource = _manifest.Inputs.Registry;
        AppsGrid.ItemsSource = null;
        AppsGrid.ItemsSource = _manifest.Apps;
        ServicesGrid.ItemsSource = null;
        ServicesGrid.ItemsSource = _manifest.Services;
        MappingsGrid.ItemsSource = null;
        MappingsGrid.ItemsSource = _manifest.PathMappings;
    }

    // what the Build page holds, into the manifest
    private void Collect()
    {
        _manifest.Output.Targets = _targets.Where(t => t.Value.IsChecked == true).Select(t => t.Key).ToList();
        _manifest.Output.Fast = FastBuild.IsChecked == true;
        _manifest.Output.Aot = AotBuild.IsChecked == true;
        _manifest.Output.Directory = OutputFolder.Text.Trim();
    }

    private void OnNew(object sender, RoutedEventArgs e) => Show(new PackageManifest(), null);

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Packager manifests (*.wpfpkg.json)|*.wpfpkg.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { Show(PackageManifest.Load(dialog.FileName), dialog.FileName); }
        catch (Exception ex) when (ex is PackagerError or System.Text.Json.JsonException or IOException) { Error(ex.Message); }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_path == null) { OnSaveAs(sender, e); return; }
        Save(_path);
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Packager manifests (*.wpfpkg.json)|*.wpfpkg.json",
            FileName = (_manifest.Product.Name.Length > 0 ? _manifest.Product.Name : "installation") + PackageManifest.Extension,
        };
        if (dialog.ShowDialog(this) != true) return;
        Save(dialog.FileName);
    }

    private void Save(string path)
    {
        Collect();
        try
        {
            _manifest.Save(path);
            _path = path;
            ManifestPath.Text = path;
            Title = Path.GetFileName(path) + " – Packager";
        }
        catch (IOException ex) { Error(ex.Message); }
        catch (UnauthorizedAccessException ex) { Error(ex.Message); }
    }

    // ---- the installation -----------------------------------------------------------------------------------

    private void OnBrowseInstall(object sender, RoutedEventArgs e)
    {
        if (Folder("The installation's Program Files folder") is { } folder) { _manifest.Inputs.InstallDir = folder; Rebind(); }
    }

    private void OnBrowseIis(object sender, RoutedEventArgs e)
    {
        if (PickFile("IIS's applicationHost.config", "IIS configuration (*.config)|*.config|All files|*.*") is { } file) { _manifest.Inputs.IisConfig = file; Rebind(); }
    }

    private void OnAddProgramData(object sender, RoutedEventArgs e)
    {
        if (Folder("A ProgramData folder of the installation") is { } folder && !_manifest.Inputs.ProgramData.Contains(folder))
        {
            _manifest.Inputs.ProgramData.Add(folder);
            Refresh();
        }
    }

    private void OnRemoveProgramData(object sender, RoutedEventArgs e)
    {
        if (ProgramDataList.SelectedItem is string item) { _manifest.Inputs.ProgramData.Remove(item); Refresh(); }
    }

    private void OnAddRegistry(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Registry exports (*.reg)|*.reg|All files|*.*", Multiselect = true, Title = "The installation's registry, exported by regedit" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (string file in dialog.FileNames)
            if (!_manifest.Inputs.Registry.Contains(file)) _manifest.Inputs.Registry.Add(file);
        Refresh();
    }

    private void OnRemoveRegistry(object sender, RoutedEventArgs e)
    {
        if (RegistryList.SelectedItem is string item) { _manifest.Inputs.Registry.Remove(item); Refresh(); }
    }

    // the scanner's manifest of the installation, what the inputs page holds kept (and the signing, the targets)
    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var inputs = _manifest.Inputs;
        if (inputs.InstallDir.Trim().Length == 0) { Error("Choose the installation's Program Files folder first."); return; }
        Collect();
        var keep = _manifest;
        Busy(true);
        ScanStatus.Text = "Scanning…";
        try
        {
            var lines = new List<string>();
            var scanned = await Task.Run(() => Scanner.Scan(inputs.InstallDir.Trim(), inputs.ProgramData, inputs.Registry, lines.Add));
            scanned.Inputs.IisConfig = inputs.IisConfig;
            scanned.Signing = keep.Signing;
            scanned.Output = keep.Output;
            Show(scanned, _path);
            int gui = scanned.Apps.Count(a => a.Kind == AppKind.Gui);
            ScanStatus.Text = $"{scanned.Apps.Count} apps ({gui} with windows), {scanned.Services.Count} services, {scanned.Apps.Count(a => a.Warnings.Count > 0)} with warnings"
                + (scanned.SqlServer.Include ? $"; SQL Server instance {scanned.SqlServer.Instance}" : "");
            Pages.SelectedIndex = 2;
        }
        catch (Exception ex) when (ex is PackagerError or IOException or UnauthorizedAccessException)
        {
            ScanStatus.Text = "";
            Error(ex.Message);
        }
        finally { Busy(false); }
    }

    // ---- path mappings --------------------------------------------------------------------------------------

    private void OnAddMapping(object sender, RoutedEventArgs e)
    {
        MappingsGrid.CommitEdit();
        _manifest.PathMappings.Add(new PathMapping { From = @"C:\", To = PathTokens.InstallDir });
        Refresh();
        MappingsGrid.SelectedIndex = _manifest.PathMappings.Count - 1;
    }

    private void OnRemoveMapping(object sender, RoutedEventArgs e)
    {
        if (MappingsGrid.SelectedItem is PathMapping mapping) { _manifest.PathMappings.Remove(mapping); Refresh(); }
    }

    // ---- signing, output ------------------------------------------------------------------------------------

    private void OnBrowseSigningFile(object sender, RoutedEventArgs e)
    {
        string field = (string)((Button)sender).Tag;
        string filter = field switch
        {
            "AuthenticodeCertificate" => "Certificates with their key (*.pfx;*.p12)|*.pfx;*.p12",
            "NotaryKey" => "App Store Connect API keys (*.p8)|*.p8",
            "AppleProvisioningProfile" => "Provisioning profiles (*.mobileprovision;*.provisionprofile)|*.mobileprovision;*.provisionprofile",
            _ => "Keystores (*.jks;*.keystore;*.p12;*.pfx)|*.jks;*.keystore;*.p12;*.pfx",
        };
        if (PickFile("Choose the file", filter + "|All files|*.*") is not { } file) return;
        typeof(SigningSection).GetProperty(field)!.SetValue(_manifest.Signing, file);
        Rebind();
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        if (Folder("Where the packages are written") is { } folder) OutputFolder.Text = folder;
    }

    private void OnBrowsePacks(object sender, RoutedEventArgs e)
    {
        if (Folder("The packs folder (the templates: android.pack, desktop-<os>.pack, ...)") is { } folder) PacksFolder.Text = folder;
    }

    // ---- the build ------------------------------------------------------------------------------------------

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Collect();
        var manifest = _manifest;
        var targets = manifest.Output.Targets.ToList();
        if (manifest.Inputs.InstallDir.Length == 0 || manifest.Apps.Count == 0) { Error("Scan an installation, or open a manifest, first."); return; }
        if (targets.Count == 0) { Error("Tick the targets to build for."); return; }
        string output = manifest.Output.Directory.Length > 0 ? manifest.Output.Directory
            : Path.Combine(_path != null ? Path.GetDirectoryName(_path)! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                (manifest.Product.Name.Length > 0 ? manifest.Product.Name : "installation") + ".packages");
        string packs = PacksFolder.Text.Trim();

        Busy(true);
        Log.Clear();
        BuildStatus.Text = "Building…";
        var started = DateTime.Now;
        try
        {
            var made = await Task.Run(() => Builder.Build(manifest, targets, output, packs, line => Dispatcher.BeginInvoke(() => Append(line))));
            Append("");
            Append($"Made in {(DateTime.Now - started):h\\:mm\\:ss}, in {output}:");
            foreach (string m in made) Append("  " + m);
            BuildStatus.Text = $"{made.Count} made";
        }
        catch (Exception ex) when (ex is PackagerError or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Append("");
            Append("Failed: " + ex.Message);
            BuildStatus.Text = "Failed";
        }
        finally { Busy(false); }
    }

    private void Append(string line)
    {
        Log.AppendText(line + Environment.NewLine);
        Log.ScrollToEnd();
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private void Busy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = BuildButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    // the bindings read the manifest again (a field set from code)
    private void Rebind()
    {
        DataContext = null;
        DataContext = _manifest;
    }

    private string? Folder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void Error(string message) => MessageBox.Show(this, message, "Packager", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>A list of strings as one line ("a; b").</summary>
public sealed class JoinConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable items and not string ? string.Join("; ", items.Cast<object>()) : value ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
