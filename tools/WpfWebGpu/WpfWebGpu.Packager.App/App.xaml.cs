// The Packager's entry: with arguments, the command line (`wpfrun package`'s: scan, stage, build), its output on the
// console; without, the window.

using System.Windows;

namespace WpfWebGpu.Packager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AotPages.Install();
        if (e.Args.Length > 0)
        {
            int code;
            try { code = PackagerCli.Run(e.Args, Console.Out); }
            catch (PackagerError error) { Console.Error.WriteLine("packager: " + error.Message); code = 1; }
            Shutdown(code);
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
