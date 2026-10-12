namespace WinFormsFixture;

// The .NET WinForms template, plus checks. ApplicationConfiguration.Initialize is source-generated
// from the project's settings and calls Application.SetHighDpiMode, SetDefaultFont and friends: the
// .NET-only surface every template app touches before its first window.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm(args);
        Application.Run(form);
        return form.ExitCode;
    }
}
