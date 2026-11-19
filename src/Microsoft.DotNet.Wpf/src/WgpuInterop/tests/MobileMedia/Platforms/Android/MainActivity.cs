// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The tests on Android: the assets copied to the app's files, MF_TEST_ROOT set to them, the tests run on a thread of
// their own (MediaCodec waits) and each line logged under the tag MFTEST. The filter is the intent's "filter" extra:
//     adb shell am start -n net.dot.wpf.mobilemedia/mobilemedia.MainActivity -e filter MFDecoderTests
//

using System;
using System.IO;
using Android.App;
using Android.OS;

// the cameras' tests (MFCaptureProbe): granted by the test run (adb shell pm grant)
[assembly: UsesPermission(Android.Manifest.Permission.Camera)]

namespace MobileMedia
{
    [Activity(Label = "MF tests", MainLauncher = true, Name = "mobilemedia.MainActivity")]
    public class MainActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            string root = FilesDir!.AbsolutePath;
            Copy("MediaFoundation", Path.Combine(root, "MediaFoundation"));
            System.Environment.SetEnvironmentVariable("MF_TEST_ROOT", root);
            string filter = Intent?.GetStringExtra("filter") ?? "";
            Console.SetOut(new LogWriter());
            var t = new System.Threading.Thread(() =>
            {
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                MediaTests.Run(filter, "Android");
            }, 16 << 20);
            t.Start();
        }

        /// <summary>An asset folder, recursively, to files.</summary>
        private void Copy(string asset, string to)
        {
            var names = Assets!.List(asset) ?? Array.Empty<string>();
            Directory.CreateDirectory(to);
            foreach (var n in names)
            {
                string a = asset + "/" + n, f = Path.Combine(to, n);
                if (Assets.List(a) is { Length: > 0 }) { Copy(a, f); continue; }
                using var src = Assets.Open(a);
                using var dst = File.Create(f);
                src.CopyTo(dst);
            }
        }

        /// <summary>Console's lines to logcat.</summary>
        private sealed class LogWriter : TextWriter
        {
            private readonly System.Text.StringBuilder _line = new();
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
            public override void Write(char c)
            {
                if (c == '\n') { Flush(); return; }
                _line.Append(c);
            }
            public override void Flush()
            {
                if (_line.Length == 0) return;
                string s = _line.ToString();
                _line.Clear();
                for (int i = 0; i < s.Length; i += 3000) Android.Util.Log.Info("MFTEST", s.Substring(i, Math.Min(3000, s.Length - i)));
            }
        }
    }
}
