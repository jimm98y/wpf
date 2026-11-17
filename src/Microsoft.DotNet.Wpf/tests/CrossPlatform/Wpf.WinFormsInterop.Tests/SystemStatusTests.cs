// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What WinForms asks the system for: SystemInformation.PowerStatus (it threw), Control.IsKeyLocked (always
// false) and the beep (nothing). On a Mac the power status is the one pmset reports.
//

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public partial class SystemStatusTests
    {
        [GeneratedRegex(@"(\d+)%")]
        private static partial Regex Percent();

        [Fact]
        public void PowerStatus_IsTheSystems()
        {
            SWF.PowerStatus p = SWF.SystemInformation.PowerStatus;
            Assert.InRange(p.BatteryLifePercent, 0f, 1f);
            if (!OperatingSystem.IsMacOS()) return;

            var psi = new ProcessStartInfo("pmset", "-g batt") { RedirectStandardOutput = true };
            string text = Process.Start(psi)!.StandardOutput.ReadToEnd();
            bool onMains = text.Contains("'AC Power'");
            Assert.Equal(onMains ? SWF.PowerLineStatus.Online : SWF.PowerLineStatus.Offline, p.PowerLineStatus);
            Match m = Percent().Match(text);
            if (!m.Success) { Assert.Equal(SWF.BatteryChargeStatus.NoSystemBattery, p.BatteryChargeStatus); return; }
            Assert.InRange(p.BatteryLifePercent * 100, int.Parse(m.Groups[1].Value) - 1, int.Parse(m.Groups[1].Value) + 1);
            Assert.Equal(text.Contains("; charging"), (p.BatteryChargeStatus & SWF.BatteryChargeStatus.Charging) != 0);
        }

        [Fact]
        public void IsKeyLocked_AnswersForTheLockKeysAndRefusesOthers()
        {
            _ = SWF.Control.IsKeyLocked(SWF.Keys.CapsLock);
            _ = SWF.Control.IsKeyLocked(SWF.Keys.NumLock);
            _ = SWF.Control.IsKeyLocked(SWF.Keys.Scroll);
            _ = SWF.Control.IsKeyLocked(SWF.Keys.Insert);
            Assert.Throws<NotSupportedException>(() => SWF.Control.IsKeyLocked(SWF.Keys.A));
        }
    }
}
