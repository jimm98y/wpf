// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The MediaElement exercise the mobile media heads show, in one file both of them <Compile Include>
// rather than copy -- the same payload arrangement AndroidHost.cs uses.
//
// It exists because the phone heads cannot be driven the way samples/media-linux is. That one is a
// console app: it takes a file argument, prints a state line per second and returns an exit code, and
// the harness reads all three. An iOS or Android app has no argument vector, no console the shell can
// read and no exit code anyone sees -- so the transport exercise has to run INSIDE the app and write
// its verdict to the platform log (logcat on Android, the simulator's stdout on iOS).
//
// The sequence is deliberately the same one media-linux drives, so a regression that shows up on one
// platform is reproducible on the others: play -> seek -> 2x -> 1x -> pause -> volume -> close ->
// reopen, with a RESULT line at the end. What differs is only how the source file is found.
//

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// The Android head compiles with ImplicitUsings, which pulls in Android.Widget and makes these two
// names ambiguous. Aliasing here keeps the page itself identical on both heads.
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace WpfMediaTest
{
    internal static class MediaTestUi
    {
        private static MediaElement s_media;
        private static TextBlock s_status;
        private static bool s_opened, s_ended, s_failed, s_sawFrame;
        private static int s_seconds;
        private static int s_autoSeconds;
        private static Action<string> s_log;

        /// <summary>
        /// Builds the page. <paramref name="autoSeconds"/> > 0 also drives the unattended transport
        /// exercise and prints RESULT pass/fail, which is what makes this checkable without a human
        /// watching the screen.
        /// </summary>
        internal static UIElement Create(string source, int autoSeconds, Action<string> log)
        {
            s_log = log ?? (m => Console.WriteLine(m));
            s_opened = s_ended = s_failed = s_sawFrame = false;
            s_seconds = 0;
            s_autoSeconds = autoSeconds;

            s_media = new MediaElement
            {
                Source = new Uri(source, UriKind.RelativeOrAbsolute),
                LoadedBehavior = MediaState.Manual,
                Stretch = Stretch.Uniform,
            };
            s_media.MediaOpened += (s, e) =>
            {
                s_opened = true;
                s_log($"MEDIA opened {s_media.NaturalVideoWidth}x{s_media.NaturalVideoHeight} " +
                      $"duration={(s_media.NaturalDuration.HasTimeSpan ? s_media.NaturalDuration.TimeSpan.ToString() : "unknown")} " +
                      $"audio={s_media.HasAudio} video={s_media.HasVideo}");
            };
            s_media.MediaFailed += (s, e) => { s_failed = true; s_log("MEDIA failed: " + e.ErrorException?.Message); };
            s_media.MediaEnded += (s, e) =>
            {
                s_ended = true;
                s_log("MEDIA ended");

                // Hand-driven mode loops. A short clip otherwise plays once and freezes on its last
                // frame, which is correct end-of-media behaviour and looks exactly like a stall to
                // anyone watching the screen. Looping also exercises the restart-after-Ended path,
                // which the unattended sequence never reaches. The unattended run must NOT loop: its
                // verdict depends on the transport script owning the position.
                if (s_autoSeconds <= 0)
                {
                    s_media.Position = TimeSpan.Zero;
                    s_media.Play();
                }
            };

            s_status = new TextBlock
            {
                Margin = new Thickness(8, 4, 8, 4),
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Text = source,
            };

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            bar.Children.Add(MakeButton("Play", () => s_media.Play()));
            bar.Children.Add(MakeButton("Pause", () => s_media.Pause()));
            bar.Children.Add(MakeButton("Stop", () => s_media.Stop()));
            bar.Children.Add(MakeButton("-5s", () => s_media.Position -= TimeSpan.FromSeconds(5)));
            bar.Children.Add(MakeButton("+5s", () => s_media.Position += TimeSpan.FromSeconds(5)));
            bar.Children.Add(MakeButton("2x", () => s_media.SpeedRatio = 2.0));
            bar.Children.Add(MakeButton("1x", () => s_media.SpeedRatio = 1.0));

            var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(s_media, 0); grid.Children.Add(s_media);
            Grid.SetRow(bar, 1); grid.Children.Add(bar);
            Grid.SetRow(s_status, 2); grid.Children.Add(s_status);

            s_media.Volume = 0.5;
            s_media.Play();

            StartTicking(source, autoSeconds);
            return grid;
        }

        private static void StartTicking(string source, int autoSeconds)
        {
            var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (s, e) =>
            {
                s_seconds++;

                // Position advancing past zero is the only in-process evidence that decoded frames are
                // actually being presented: the clock only moves once the backend is running.
                if (s_media.Position > TimeSpan.Zero) s_sawFrame = true;

                string line = $"t={s_seconds}s pos={s_media.Position.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s " +
                              $"{s_media.NaturalVideoWidth}x{s_media.NaturalVideoHeight} " +
                              $"opened={s_opened} ended={s_ended} failed={s_failed}";
                s_status.Text = line;
                s_log("STATE " + line);

                if (autoSeconds <= 0) return;

                // The same transport exercise media-linux drives, so every IMediaBackend leaf is hit
                // before the verdict.
                switch (s_seconds)
                {
                    case 3: s_log("AUTO seek 1s"); s_media.Position = TimeSpan.FromSeconds(1); break;
                    case 4: s_log("AUTO rate 2x"); s_media.SpeedRatio = 2.0; break;
                    case 5: s_log("AUTO rate 1x"); s_media.SpeedRatio = 1.0; break;
                    case 6: s_log("AUTO pause"); s_media.Pause(); break;
                    case 7: s_log("AUTO volume 0.2"); s_media.Volume = 0.2; s_media.Play(); break;
                    case 8: s_log("AUTO volume 0.7"); s_media.Volume = 0.7; break;
                    case 9:
                        s_log("AUTO close");
                        s_media.Close();
                        s_opened = s_ended = false;
                        break;
                    case 10:
                        s_log("AUTO reopen");
                        s_media.Source = new Uri(source, UriKind.RelativeOrAbsolute);
                        s_media.Play();
                        break;
                }

                if (s_seconds >= autoSeconds)
                {
                    tick.Stop();
                    // Reopen has to have taken effect for a run long enough to reach it, and the clock
                    // has to have moved -- an Opened event on its own would pass with a frozen picture.
                    bool ok = s_opened && s_sawFrame && !s_failed;
                    s_log(ok ? "RESULT pass" : "RESULT fail");
                }
            };
            tick.Start();
        }

        private static Button MakeButton(string text, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }
    }
}
