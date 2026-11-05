namespace XrFixture;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow()
    {
        InitializeComponent();
        // XR_FIXTURE_DIALOG=1: a dialog over the window once it is up, as an app shows one (a framed window's
        // title bar, edges and close button on a head that draws them)
        if (System.Environment.GetEnvironmentVariable("XR_FIXTURE_DIALOG") == "1")
            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                var button = new System.Windows.Controls.Button { Content = "OK", Width = 80, Margin = new System.Windows.Thickness(12), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
                var dialog = new System.Windows.Window
                {
                    Title = "A dialog", Width = 360, Height = 160, Owner = this, ShowInTaskbar = false,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner, ResizeMode = System.Windows.ResizeMode.CanResize,
                    Content = new System.Windows.Controls.DockPanel { Children = { button, new System.Windows.Controls.TextBlock { Text = "Its title bar is the head's.", Margin = new System.Windows.Thickness(12) } } },
                };
                button.Click += (_, _) => dialog.Close();
                dialog.Show();
            });
    }
}
