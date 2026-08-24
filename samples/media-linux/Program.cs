// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media test for the Linux/Wayland head: a MediaElement plus transport controls, exercising
// LinuxMediaBackend (GStreamer playbin -> appsink BGRA) and the compositor's MilDrawVideo path.
//
//   dotnet run --project samples/media-linux -- <file-or-uri>
//   dotnet run --project samples/media-linux -- <file> --auto 12
//
// --auto N runs unattended: it drives play -> seek -> rate -> pause -> play off a timer, prints a
// state line every second and exits after N seconds with a non-zero code if no frame ever arrived.
// That is what makes this gate checkable without a human watching the window.
//

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class Program
{
    private static MediaElement s_media;
    private static TextBlock s_status;
    private static bool s_opened, s_ended, s_failed;
    private static int s_seconds;
    private static int s_exitCode;

    internal static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION") == null)
            Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        string source = null;
        int autoSeconds = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--auto" && i + 1 < args.Length) autoSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (!args[i].StartsWith("--", StringComparison.Ordinal)) source ??= args[i];
        }

        if (source == null)
        {
            Console.Error.WriteLine("usage: MediaLinux <file-or-uri> [--auto <seconds>]");
            return 2;
        }

        var app = new Application();

        s_media = new MediaElement
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute),
            LoadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform,
        };
        s_media.MediaOpened += (s, e) =>
        {
            s_opened = true;
            Console.WriteLine($"MEDIA opened {s_media.NaturalVideoWidth}x{s_media.NaturalVideoHeight} " +
                              $"duration={(s_media.NaturalDuration.HasTimeSpan ? s_media.NaturalDuration.TimeSpan.ToString() : "unknown")} " +
                              $"audio={s_media.HasAudio} video={s_media.HasVideo}");
        };
        s_media.MediaFailed += (s, e) => { s_failed = true; Console.WriteLine("MEDIA failed: " + e.ErrorException?.Message); };
        s_media.MediaEnded += (s, e) => { s_ended = true; Console.WriteLine("MEDIA ended"); };

        s_status = new TextBlock { Margin = new Thickness(8, 4, 8, 4), Foreground = Brushes.White };

        var volume = new Slider { Minimum = 0, Maximum = 1, Value = 0.5, Width = 100, VerticalAlignment = VerticalAlignment.Center };
        volume.ValueChanged += (s, e) => s_media.Volume = volume.Value;

        var position = new Slider { Minimum = 0, Maximum = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        position.PreviewMouseUp += (s, e) =>
        {
            if (s_media.NaturalDuration.HasTimeSpan)
                s_media.Position = TimeSpan.FromSeconds(position.Value * s_media.NaturalDuration.TimeSpan.TotalSeconds);
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        bar.Children.Add(Button("Play", () => s_media.Play()));
        bar.Children.Add(Button("Pause", () => s_media.Pause()));
        bar.Children.Add(Button("Stop", () => s_media.Stop()));
        bar.Children.Add(Button("-5s", () => s_media.Position -= TimeSpan.FromSeconds(5)));
        bar.Children.Add(Button("+5s", () => s_media.Position += TimeSpan.FromSeconds(5)));
        bar.Children.Add(Button("2x", () => s_media.SpeedRatio = 2.0));
        bar.Children.Add(Button("1x", () => s_media.SpeedRatio = 1.0));
        bar.Children.Add(new TextBlock { Text = "vol", Margin = new Thickness(12, 0, 4, 0), Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(volume);

        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(s_media, 0); grid.Children.Add(s_media);
        Grid.SetRow(position, 1); grid.Children.Add(position);
        Grid.SetRow(bar, 2); grid.Children.Add(bar);
        Grid.SetRow(s_status, 3); grid.Children.Add(s_status);

        var window = new Window { Title = "WPF media (Linux)", Width = 720, Height = 560, Content = grid };
        window.Show();

        s_media.Volume = 0.5;
        s_media.Play();

        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (s, e) =>
        {
            s_seconds++;
            string line = $"t={s_seconds}s pos={s_media.Position.TotalSeconds:F1}s " +
                          $"{s_media.NaturalVideoWidth}x{s_media.NaturalVideoHeight} " +
                          $"opened={s_opened} ended={s_ended} failed={s_failed}";
            s_status.Text = line;
            Console.WriteLine("STATE " + line);

            if (s_media.NaturalDuration.HasTimeSpan && s_media.NaturalDuration.TimeSpan.TotalSeconds > 0)
                position.Value = s_media.Position.TotalSeconds / s_media.NaturalDuration.TimeSpan.TotalSeconds;

            if (autoSeconds <= 0) return;

            // Unattended transport exercise: every leaf of IMediaBackend gets hit before the exit check.
            switch (s_seconds)
            {
                case 3: Console.WriteLine("AUTO seek 1s"); s_media.Position = TimeSpan.FromSeconds(1); break;
                case 4: Console.WriteLine("AUTO rate 2x"); s_media.SpeedRatio = 2.0; break;
                case 5: Console.WriteLine("AUTO rate 1x"); s_media.SpeedRatio = 1.0; break;
                case 6: Console.WriteLine("AUTO pause"); s_media.Pause(); break;
                case 7: Console.WriteLine("AUTO volume 0.2"); s_media.Volume = 0.2; s_media.Play(); break;
                // Tear the pipeline all the way down and build it again. A GStreamer state change to NULL
                // can block, so this is the step that would expose a hang on Close.
                // A second, distinct volume change: with WPF_MEDIA_LOG the backend prints the value it
                // read back BEFORE setting, which is the only way to tell whether the previous change
                // actually stuck on the same pipeline.
                case 8: Console.WriteLine("AUTO volume 0.7"); s_media.Volume = 0.7; break;
                case 9:
                    Console.WriteLine("AUTO close");
                    s_media.Close();
                    s_opened = s_ended = false;
                    break;
                case 10:
                    Console.WriteLine("AUTO reopen");
                    s_media.Source = new Uri(source, UriKind.RelativeOrAbsolute);
                    s_media.Play();
                    break;
            }

            if (s_seconds >= autoSeconds)
            {
                // Reopen has to have taken effect for a run long enough to reach it.
                bool ok = s_opened && !s_failed;
                Console.WriteLine(ok ? "RESULT pass" : "RESULT fail");
                s_exitCode = ok ? 0 : 1;
                tick.Stop();
                // Close the window rather than Environment.Exit, so shutdown runs the backend's Dispose
                // path. A hang here shows up as the harness timing out instead of a false pass.
                window.Close();
            }
        };
        tick.Start();

        app.Run();
        return s_exitCode;
    }

    private static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        b.Click += (s, e) => onClick();
        return b;
    }
}
