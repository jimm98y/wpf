using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WpfFixture;

// Reports where it is running from and closes itself after --seconds N, so it can run unattended.
// Exit code 0 means every check passed; each failed check is printed and makes it 1.
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var failures = new List<string>();
        string dir = Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!;

        Console.WriteLine($"fixture: WPF {typeof(Window).Assembly.FullName} from {typeof(Window).Assembly.Location}");
        Console.WriteLine($"fixture: BaseDirectory {AppContext.BaseDirectory}");
        Console.WriteLine($"fixture: CurrentDirectory {Environment.CurrentDirectory}");

        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != dir)
            failures.Add($"BaseDirectory is {AppContext.BaseDirectory}, expected {dir}");
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "WpfFixture.dll")))
            failures.Add("WpfFixture.dll is not in BaseDirectory");
        if (Heading.ActualWidth <= 0)
            failures.Add("the heading was not measured");

        foreach (string f in failures) Console.WriteLine($"fixture: FAIL {f}");
        Console.WriteLine(failures.Count == 0 ? "fixture: PASS" : "fixture: FAILED");
        Application.Current.Properties["exit"] = failures.Count == 0 ? 0 : 1;

        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--seconds");
        if (i > 0 && i + 1 < args.Length && double.TryParse(args[i + 1], out double seconds))
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); Application.Current.Shutdown((int)Application.Current.Properties["exit"]!); };
            timer.Start();
        }
    }
}
