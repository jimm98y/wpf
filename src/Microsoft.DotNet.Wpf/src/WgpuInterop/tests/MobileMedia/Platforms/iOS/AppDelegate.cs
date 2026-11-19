// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The tests on iOS: the fixtures and media are the bundle's resources (beside the app, as on the desktop), the tests run
// on a thread of their own and each line goes to stdout -- `xcrun simctl launch --console <device> net.dot.wpf.mobilemedia
// [filter]` prints them. The app exits when they are done.
//

using System;
using System.Linq;
using Foundation;
using UIKit;

namespace MobileMedia
{
    [Register("AppDelegate")]
    public class AppDelegate : UIApplicationDelegate
    {
        public override UIWindow? Window { get; set; }

        public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
        {
            Window = new UIWindow(UIScreen.MainScreen.Bounds) { RootViewController = new UIViewController() };
            Window.MakeKeyAndVisible();
            string filter = NSProcessInfo.ProcessInfo.Arguments.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "";
            var t = new System.Threading.Thread(() =>
            {
                System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
                MediaTests.Run(filter, "iOS");
                Console.Out.Flush();
                Environment.Exit(0);
            }, 16 << 20);
            t.Start();
            return true;
        }
    }

    public static class Program
    {
        private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
    }
}
