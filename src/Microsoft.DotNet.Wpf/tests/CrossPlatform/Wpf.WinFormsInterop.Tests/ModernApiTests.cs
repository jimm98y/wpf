// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What .NET 5 to 10 added to WinForms, ported into this stack's (src/WinFormsInterop/modern): an app compiled
// against .NET's WinForms that calls one of them failed with MissingMethodException before. Each behaves as .NET's:
// the ambient DataContext, a button's and a tool strip item's ICommand, typed and JSON data on a data object and the
// clipboard, the colour mode, a form's frame attributes and ShowAsync, InvokeAsync, the IME modes of a text box.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ModernApiTests
    {
        private sealed class Command : ICommand
        {
            public bool Can = true;
            public List<object?> Executed = new();
            public event EventHandler? CanExecuteChanged;
            public bool CanExecute(object? parameter) => Can;
            public void Execute(object? parameter) => Executed.Add(parameter);
            public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        public sealed class Point3 { public int X { get; set; } public int Y { get; set; } public int Z { get; set; } }

        [Fact]
        public void DataContext_IsAmbient_AndAChildSetToItsParentsInheritsAgain()
        {
            using var parent = new SWF.Panel();
            using var child = new SWF.Button();
            parent.Controls.Add(child);
            int childChanged = 0;
            child.DataContextChanged += (s, e) => childChanged++;

            var model = new object();
            parent.DataContext = model;
            Assert.Same(model, child.DataContext);
            Assert.Equal(1, childChanged);

            var own = new object();
            child.DataContext = own;
            Assert.Same(own, child.DataContext);
            Assert.Equal(2, childChanged);

            // the parent's changes no longer reach it...
            parent.DataContext = new object();
            Assert.Same(own, child.DataContext);
            Assert.Equal(2, childChanged);

            // ...until the parent's is the child's own: inherited again, and seen as no change
            parent.DataContext = own;
            Assert.Same(own, child.DataContext);
            parent.DataContext = model;
            Assert.Same(model, child.DataContext);
        }

        [Fact]
        public void ButtonCommand_EnablesByCanExecute_ExecutesOnClick_AndGivesBackEnabled()
        {
            using var button = new SWF.Button { Enabled = true };
            var command = new Command { Can = false };
            int commandChanged = 0, canExecuteChanged = 0;
            button.CommandChanged += (s, e) => commandChanged++;
            button.CommandCanExecuteChanged += (s, e) => canExecuteChanged++;

            button.CommandParameter = "p";
            button.Command = command;
            Assert.Equal(1, commandChanged);
            Assert.False(button.Enabled);

            command.Can = true;
            command.Changed();
            Assert.Equal(1, canExecuteChanged);
            Assert.True(button.Enabled);

            button.PerformClick();
            Assert.Equal(new object?[] { "p" }, command.Executed);

            command.Can = false;
            command.Changed();
            button.Command = null;
            // the Enabled the button had before the command
            Assert.True(button.Enabled);
        }

        [Fact]
        public void ToolStripItem_HasACommand_RaisesSelectedChanged_AndIsBindable()
        {
            using var item = new SWF.ToolStripButton("Old");
            var command = new Command();
            item.Command = command;
            item.CommandParameter = 7;
            item.PerformClick();
            Assert.Equal(new object?[] { 7 }, command.Executed);

            Assert.IsAssignableFrom<SWF.BindableComponent>(item);
            var source = new Point3 { X = 1 };
            item.DataBindings.Add("Text", source, "X");
            item.BindingContext = new SWF.BindingContext();
            Assert.Equal("1", item.Text);
        }

        [Fact]
        public void DataObject_Json_RoundTrips_AndTypedDataFollowsFormats()
        {
            var data = new SWF.DataObject();
            data.SetDataAsJson("my.point", new Point3 { X = 1, Y = 2, Z = 3 });
            Assert.True(data.TryGetData("my.point", out Point3? point));
            Assert.Equal((1, 2, 3), (point!.X, point.Y, point.Z));
            // keyed by the type's full name when no format is given
            data.SetDataAsJson(new Point3 { Z = 9 });
            Assert.True(data.TryGetData(out Point3? byType));
            Assert.Equal(9, byType!.Z);

            data.SetText("hello");
            Assert.True(data.TryGetData(SWF.DataFormats.UnicodeText, out string? text));
            Assert.Equal("hello", text);
            // converting: Text is there as UnicodeText
            Assert.True(data.TryGetData(SWF.DataFormats.Text, autoConvert: true, out string? converted));
            Assert.Equal("hello", converted);
            // a text format is a string, not anything else
            Assert.Throws<NotSupportedException>(() => data.TryGetData(SWF.DataFormats.Text, out int _));
            // nor is a predefined format JSON
            Assert.Throws<ArgumentException>(() => data.SetDataAsJson(SWF.DataFormats.Text, new Point3()));
            Assert.Throws<ArgumentException>(() => data.SetDataAsJson(new SWF.DataObject()));
            Assert.False(data.TryGetData("absent", out Point3? _));
        }

        [Fact]
        public void Clipboard_Json_RoundTrips()
        {
            SWF.Clipboard.SetDataAsJson("my.point", new Point3 { X = 4 });
            Assert.True(SWF.Clipboard.TryGetData("my.point", out Point3? point));
            Assert.Equal(4, point!.X);
            Assert.False(SWF.Clipboard.TryGetData("my.point", out SWF.Form? _));
            SWF.Clipboard.Clear();
        }

        [Fact]
        public void ColorMode_Dark_SwitchesTheSystemColors()
        {
#pragma warning disable SYSLIB5002
            try
            {
                Assert.Equal(SWF.SystemColorMode.Classic, SWF.Application.ColorMode);
                SWF.Application.SetColorMode(SWF.SystemColorMode.Dark);
                Assert.True(SWF.Application.IsDarkModeEnabled);
                Assert.True(SystemColors.UseAlternativeColorSet);
                Assert.True(SystemColors.Window.GetBrightness() < 0.5f, $"Window is {SystemColors.Window}");
            }
            finally
            {
                SWF.Application.SetColorMode(SWF.SystemColorMode.Classic);
            }
            Assert.False(SystemColors.UseAlternativeColorSet);
#pragma warning restore SYSLIB5002
        }

        [Fact]
        public void FormFrameAttributes_AreKept_AndSayTheyChanged()
        {
            using var form = new SWF.Form();
            int corner = 0, caption = 0;
            form.FormCornerPreferenceChanged += (s, e) => corner++;
            form.FormCaptionBackColorChanged += (s, e) => caption++;
            form.FormCornerPreference = SWF.FormCornerPreference.RoundSmall;
            form.FormCaptionBackColor = Color.Teal;
            form.FormCaptionBackColor = Color.Teal;
            form.FormScreenCaptureMode = SWF.ScreenCaptureMode.HideWindow;
            Assert.Equal(SWF.FormCornerPreference.RoundSmall, form.FormCornerPreference);
            Assert.Equal(Color.Teal, form.FormCaptionBackColor);
            Assert.Equal(SWF.ScreenCaptureMode.HideWindow, form.FormScreenCaptureMode);
            Assert.Equal(1, corner);
            Assert.Equal(1, caption);
            Assert.Throws<ArgumentOutOfRangeException>(() => form.FormCornerPreference = (SWF.FormCornerPreference)9);
        }

        [Fact]
        public void ImeMode_OfAReadOnlyTextBox_IsDisable()
        {
            using var box = new SWF.TextBox { ImeMode = SWF.ImeMode.Hiragana };
            Assert.Equal(SWF.ImeMode.Hiragana, box.ImeMode);
            box.ReadOnly = true;
            Assert.Equal(SWF.ImeMode.Disable, box.ImeMode);
            box.ReadOnly = false;
            Assert.Equal(SWF.ImeMode.Hiragana, box.ImeMode);
        }

        [Fact]
        public void FolderBrowserDialog_SelectedPaths_FollowSelectedPath()
        {
            using var dialog = new SWF.FolderBrowserDialog { Multiselect = true, InitialDirectory = "/tmp" };
            dialog.SelectedPath = "/tmp/a";
            Assert.Equal(new[] { "/tmp/a" }, dialog.SelectedPaths);
            dialog.Reset();
            Assert.Empty(dialog.SelectedPaths);
            Assert.False(dialog.Multiselect);
            Assert.Equal(string.Empty, dialog.InitialDirectory);
        }

        [Fact]
        public void InvokeAsync_RunsOnTheControlsThread_AndShowDialogAsyncGivesItsResult()
        {
            Exception? failure = null;
            int uiThread = -1, ranOn = -2;
            string? returned = null;
            string step = "not shown", context = "?";
            int ticks = 0;
            IntPtr formHandle = IntPtr.Zero;
            SWF.DialogResult dialogResult = SWF.DialogResult.None;
            var t = new Thread(() =>
            {
                try
                {
                    using var form = new SWF.Form
                    {
                        Text = "InvokeAsyncTest", StartPosition = SWF.FormStartPosition.Manual,
                        Location = new Point(8000, 80), Size = new Size(200, 120), ShowInTaskbar = false,
                    };
                    uiThread = Environment.CurrentManagedThreadId;
                    var alive = new SWF.Timer { Interval = 100 };
                    alive.Tick += (s, e) => ticks++;
                    alive.Start();
                    form.Shown += async (s, e) =>
                    {
                        formHandle = form.Handle;
                        try
                        {
                            context = System.Threading.SynchronizationContext.Current?.GetType().Name ?? "none";
                            step = "shown";
                            await Task.Run(async () =>
                            {
                                await form.InvokeAsync(() => ranOn = Environment.CurrentManagedThreadId);
                                step = "invoked";
                                returned = await form.InvokeAsync(() => "value");
                                await form.InvokeAsync(async ct => { await Task.Yield(); });
                                step = "invoked async";
                            });
                            step = $"back on {(Environment.CurrentManagedThreadId == uiThread ? "the form's thread" : "another thread")}";
                            using var dialog = new SWF.Form
                            {
                                StartPosition = SWF.FormStartPosition.Manual, Location = new Point(8000, 300),
                                Size = new Size(120, 80), ShowInTaskbar = false,
                            };
                            dialog.Shown += (s2, e2) => { dialog.DialogResult = SWF.DialogResult.Yes; };
                            dialogResult = await dialog.ShowDialogAsync();
                            step = "dialog closed";
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    SWF.Application.Run(form);
                }
                catch (Exception ex) { failure = ex; }
            });
            t.TrySetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(TimeSpan.FromSeconds(60)), $"the form's loop did not end: {step}, context {context}, {ticks} ticks, form handle {formHandle}");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            Assert.Equal(uiThread, ranOn);
            Assert.Equal("value", returned);
            Assert.Equal(SWF.DialogResult.Yes, dialogResult);
        }
    }
}
