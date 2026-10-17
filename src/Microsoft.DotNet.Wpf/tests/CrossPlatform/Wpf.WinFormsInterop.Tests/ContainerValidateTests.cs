// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ContainerControl.Validate validates the control that has the focus, as .NET's does: Validating and
// Validated on it (and the containers between it and the form), stopping at a cancel. Mono's was a stub
// that printed "not yet implemented" and returned true; GraphWorX's configurator validates on close.
//

using System.Drawing;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ContainerValidateTests
    {
        [Theory]
        [InlineData(false, true, 1, 1)]
        [InlineData(true, false, 1, 0)]
        public void Validate_RunsTheFocusedControlsValidation(bool cancel, bool expected, int validating, int validated)
        {
            using var form = new SWF.Form { ClientSize = new Size(200, 100) };
            var panel = new SWF.Panel { Bounds = new Rectangle(0, 0, 200, 100) };
            var box = new SWF.TextBox { Left = 5, Top = 5 };
            panel.Controls.Add(box);
            form.Controls.Add(panel);
            form.CreateControl();
            int ing = 0, ed = 0;
            box.Validating += (s, e) => { ing++; e.Cancel = cancel; };
            box.Validated += (s, e) => ed++;
            form.ActiveControl = box;
            Assert.Equal(expected, form.Validate());
            Assert.Equal(validating, ing);
            Assert.Equal(validated, ed);
        }

        [Fact]
        public void Validate_SkipsAControlThatDoesNotCauseValidation()
        {
            using var form = new SWF.Form();
            var box = new SWF.TextBox { CausesValidation = false };
            form.Controls.Add(box);
            form.CreateControl();
            int ing = 0;
            box.Validating += (s, e) => ing++;
            form.ActiveControl = box;
            Assert.True(form.Validate());
            Assert.Equal(0, ing);
        }
    }
}
