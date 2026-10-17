// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WinForms members that were Mono stubs throwing NotImplementedException, ported from dotnet/winforms:
// BindingManagerBase.GetItemProperties (list accessor chains), ControlPaint's HBITMAP helpers and
// DrawSelectionFrame, PropertyGrid's IComPropertyBrowser, DataObject's ComTypes.IDataObject, DataGridView's
// editing panel and accessibility objects, RichTextBox.CreateRichEditOleCallback and FileDialog.HookProc.
// Expectations are .NET 10's.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using SWF = System.Windows.Forms;
using CT = System.Runtime.InteropServices.ComTypes;

namespace Wpf.WinFormsInterop.Tests
{
    public class WinFormsStubPortTests
    {
        public sealed class Order
        {
            public string Name { get; set; } = "";
            public List<Line> Lines { get; } = new();
        }

        public sealed class Line
        {
            public string Product { get; set; } = "";
            public int Quantity { get; set; }
        }

        [Fact]
        public void GetItemProperties_FollowsAListAccessorChain()
        {
            var orders = new List<Order> { new Order { Name = "a" } };
            var manager = (SWF.BindingManagerBase)new SWF.BindingContext()[orders];
            PropertyDescriptor lines = TypeDescriptor.GetProperties(typeof(Order))["Lines"]!;
            MethodInfo get = typeof(SWF.BindingManagerBase).GetMethod("GetItemProperties", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(ArrayList), typeof(ArrayList) }, null)!;
            var props = (PropertyDescriptorCollection?)get.Invoke(manager, new object[] { new ArrayList { orders }, new ArrayList { lines } });
            Assert.NotNull(props);
            Assert.NotNull(props!["Product"]);
            Assert.NotNull(props["Quantity"]);
            // No accessors: the items' own properties.
            var own = (PropertyDescriptorCollection?)get.Invoke(manager, new object[] { new ArrayList(), new ArrayList() });
            Assert.NotNull(own!["Name"]);
            Assert.NotNull(manager.GetItemProperties()["Name"]);
        }

        [Fact]
        public void APropertyManager_ListsItsObjectsProperties()
        {
            var order = new Order { Name = "x" };
            var manager = new SWF.BindingContext()[order];
            Assert.IsType<SWF.PropertyManager>(manager);
            Assert.NotNull(manager.GetItemProperties()["Name"]);
        }

        [Fact]
        public void ControlPaint_ContrastControlDark_IsControlDarkOutsideHighContrast()
        {
            Assert.Equal(SWF.SystemInformation.HighContrast ? SystemColors.WindowFrame : SystemColors.ControlDark, SWF.ControlPaint.ContrastControlDark);
        }

        [Fact]
        public void ControlPaint_TheTransparencyMask_IsSetWhereThePictureIsTransparent_AndTheColorMaskBlackThere()
        {
            using var bmp = new Bitmap(10, 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 10; x++)
                    bmp.SetPixel(x, y, x < 5 ? Color.Transparent : Color.FromArgb(255, 200, 100, 50));
            IntPtr mask = SWF.ControlPaint.CreateHBitmapTransparencyMask(bmp);
            Assert.NotEqual(IntPtr.Zero, mask);
            using (Image m = Image.FromHbitmap(mask))
            {
                var mb = (Bitmap)m;
                Assert.Equal(Color.White.ToArgb() & 0xffffff, mb.GetPixel(1, 1).ToArgb() & 0xffffff);
                Assert.Equal(0, mb.GetPixel(7, 1).ToArgb() & 0xffffff);
            }
            IntPtr color = SWF.ControlPaint.CreateHBitmapColorMask(bmp, mask);
            using (Image c = Image.FromHbitmap(color))
            {
                var cb = (Bitmap)c;
                Assert.Equal(0, cb.GetPixel(1, 1).ToArgb() & 0xffffff);
                Assert.Equal(Color.FromArgb(200, 100, 50).ToArgb() & 0xffffff, cb.GetPixel(7, 1).ToArgb() & 0xffffff);
            }
            Assert.Throws<ArgumentNullException>(() => SWF.ControlPaint.CreateHBitmapTransparencyMask(null!));
        }

        [Fact]
        public void ControlPaint_The16BitBitmap_IsComposedOverTheBackground_InFiveBitsAChannel()
        {
            using var bmp = new Bitmap(4, 4, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bmp.SetPixel(0, 0, Color.FromArgb(255, 201, 103, 57));
            bmp.SetPixel(1, 0, Color.Transparent);
            IntPtr h = SWF.ControlPaint.CreateHBitmap16Bit(bmp, Color.FromArgb(10, 20, 30));
            using Image img = Image.FromHbitmap(h);
            var b = (Bitmap)img;
            static int Five(int v) => (v >> 3) << 3 | (v >> 3) >> 2;
            Assert.Equal(Color.FromArgb(Five(201), Five(103), Five(57)).ToArgb() & 0xffffff, b.GetPixel(0, 0).ToArgb() & 0xffffff);
            Assert.Equal(Color.FromArgb(Five(10), Five(20), Five(30)).ToArgb() & 0xffffff, b.GetPixel(1, 0).ToArgb() & 0xffffff);
        }

        [Fact]
        public void ControlPaint_DrawSelectionFrame_PaintsOnlyBetweenTheTwoRectangles()
        {
            using var bmp = new Bitmap(40, 40);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                SWF.ControlPaint.DrawSelectionFrame(g, active: false, new Rectangle(0, 0, 40, 40), new Rectangle(8, 8, 24, 24), Color.White);
            }
            bool frameInked = false;
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 40; x++)
                    if (bmp.GetPixel(x, y).ToArgb() != Color.White.ToArgb()) frameInked = true;
            Assert.True(frameInked, "nothing was drawn in the frame");
            for (int y = 8; y < 32; y++)
                for (int x = 8; x < 32; x++)
                    Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(x, y).ToArgb());
            Assert.Throws<ArgumentNullException>(() => SWF.ControlPaint.DrawSelectionFrame(null!, true, Rectangle.Empty, Rectangle.Empty, Color.White));
        }

        [Fact]
        public void PropertyGrid_AnswersIComPropertyBrowser()
        {
            using var grid = new SWF.PropertyGrid();
            var browser = (SWF.ComponentModel.Com2Interop.IComPropertyBrowser)grid;
            Assert.False(browser.InPropertySet);
            Assert.True(browser.EnsurePendingChangesCommitted());
            browser.DropDownDone();
            grid.PropertySort = SWF.PropertySort.Alphabetical;
            grid.HelpVisible = false;
            browser.LoadState(null);
            Assert.Equal(SWF.PropertySort.Categorized | SWF.PropertySort.Alphabetical, grid.PropertySort);
            Assert.True(grid.HelpVisible);
            Assert.False(grid.CommandsVisibleIfAvailable);
            browser.SaveState(null);   // no key: nothing to do
        }

        [Fact]
        public void DataObject_ComTypes_HandsOutHGlobals_AndAnswersQueries()
        {
            var data = new SWF.DataObject();
            data.SetData(SWF.DataFormats.UnicodeText, "héllo");
            data.SetData(SWF.DataFormats.FileDrop, new[] { @"C:\a.txt", @"C:\b.txt" });
            var com = (CT.IDataObject)data;

            var fmt = new CT.FORMATETC { cfFormat = 13, dwAspect = CT.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = CT.TYMED.TYMED_HGLOBAL };
            Assert.Equal(0, com.QueryGetData(ref fmt));
            com.GetData(ref fmt, out CT.STGMEDIUM medium);
            Assert.Equal(CT.TYMED.TYMED_HGLOBAL, medium.tymed);
            Assert.Equal("héllo", Marshal.PtrToStringUni(medium.unionmember));
            Marshal.FreeHGlobal(medium.unionmember);

            var drop = new CT.FORMATETC { cfFormat = 15, dwAspect = CT.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = CT.TYMED.TYMED_HGLOBAL };
            com.GetData(ref drop, out CT.STGMEDIUM files);
            Assert.Equal(20, Marshal.ReadInt32(files.unionmember));          // DROPFILES.pFiles
            Assert.Equal(1, Marshal.ReadInt32(files.unionmember, 16));       // fWide
            Assert.Equal(@"C:\a.txt", Marshal.PtrToStringUni(files.unionmember + 20));
            Marshal.FreeHGlobal(files.unionmember);

            var missing = new CT.FORMATETC { cfFormat = 2, dwAspect = CT.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = CT.TYMED.TYMED_HGLOBAL };
            Assert.Equal(unchecked((int)0x80040064), com.QueryGetData(ref missing));   // DV_E_FORMATETC
            var thumbnail = fmt; thumbnail.dwAspect = CT.DVASPECT.DVASPECT_THUMBNAIL;
            Assert.Equal(unchecked((int)0x8004006B), com.QueryGetData(ref thumbnail)); // DV_E_DVASPECT
            Assert.Throws<COMException>(() => com.GetData(ref missing, out _));

            CT.IEnumFORMATETC formats = com.EnumFormatEtc(CT.DATADIR.DATADIR_GET);
            var one = new CT.FORMATETC[1];
            var fetched = new int[1];
            var seen = new List<short>();
            while (formats.Next(1, one, fetched) == 0) seen.Add(one[0].cfFormat);
            Assert.Contains((short)13, seen);
            Assert.Contains((short)15, seen);

            Assert.Equal(unchecked((int)0x80004001), com.DAdvise(ref fmt, CT.ADVF.ADVF_NODATA, null!, out int connection));   // E_NOTIMPL
            Assert.Equal(0, connection);
            Assert.Equal(unchecked((int)0x80040003), com.EnumDAdvise(out CT.IEnumSTATDATA? advise));                           // OLE_E_ADVISENOTSUPPORTED
            Assert.Null(advise);
            Assert.Equal(0x00040130, com.GetCanonicalFormatEtc(ref fmt, out _));                                                // DATA_S_SAMEFORMATETC
        }

        [Fact]
        public void DataGridView_HasAnEditingPanel_AndAccessibleRowsAndCells()
        {
            using var grid = new SWF.DataGridView { RowHeadersVisible = true };
            grid.Columns.Add("a", "A");
            grid.Columns.Add("b", "B");
            grid.Columns[1].Visible = false;
            grid.Rows.Add("1", "2");
            Assert.NotNull(grid.EditingPanel);
            Assert.Same(grid.EditingPanel, grid.EditingPanel);

            SWF.DataGridViewRow row = grid.Rows[0];
            // The row header, then the one visible cell.
            Assert.Equal(2, row.AccessibilityObject.GetChildCount());
            Assert.Same(row.HeaderCell.AccessibilityObject, row.AccessibilityObject.GetChild(0));
            Assert.Same(row.Cells[0].AccessibilityObject, row.AccessibilityObject.GetChild(1));
            Assert.Null(row.AccessibilityObject.GetChild(2));
            // A cell not being edited has no child.
            Assert.Null(row.Cells[0].AccessibilityObject.GetChild(0));

            SWF.DataGridViewCell cell = row.Cells[0];
            Rectangle editing = cell.PositionEditingPanel(new Rectangle(10, 10, 100, 20), new Rectangle(10, 10, 100, 20), cell.InheritedStyle, false, false, true, true);
            Assert.True(editing.Width > 0 && editing.Height > 0);
            Assert.Equal(grid.EditingPanel.Width, editing.Width);
        }

        [Fact]
        public void DataGridViewCellStyleConverter_SerializesTheParameterlessConstructor()
        {
            var converter = new SWF.DataGridViewCellStyleConverter();
            Type descriptorType = typeof(System.ComponentModel.Design.Serialization.InstanceDescriptor);
            Assert.True(converter.CanConvertTo(null, descriptorType));
            var descriptor = (System.ComponentModel.Design.Serialization.InstanceDescriptor)converter.ConvertTo(null, null, new SWF.DataGridViewCellStyle(), descriptorType)!;
            Assert.Equal(typeof(SWF.DataGridViewCellStyle).GetConstructor(Type.EmptyTypes), descriptor.MemberInfo);
            Assert.Empty(descriptor.Arguments);
            Assert.False(descriptor.IsComplete);
        }

        [Fact]
        public void TheResXTypeResolution_LooksInTheListedAssemblies()
        {
            Type t = typeof(SWF.Form).Assembly.GetType("System.Resources.AssemblyNamesTypeResolutionService")!;
            var names = new[] { typeof(WinFormsStubPortTests).Assembly.GetName() };
            var svc = (System.ComponentModel.Design.ITypeResolutionService)Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { names }, null)!;
            Assert.Same(typeof(Order), svc.GetType(typeof(Order).FullName + ", SomeOtherAssembly", false));
            Assert.Same(typeof(WinFormsStubPortTests).Assembly, svc.GetAssembly(names[0]));
            Assert.Null(svc.GetType("No.Such.Type", false));
            Assert.Throws<ArgumentException>(() => svc.GetType("No.Such.Type", true));
            Assert.Throws<NotSupportedException>(() => svc.ReferenceAssembly(names[0]));
        }

        private sealed class RtbWithCallback : SWF.RichTextBox
        {
            public object Callback() => CreateRichEditOleCallback();
        }

        [Fact]
        public void RichTextBox_CreateRichEditOleCallback_MakesOne()
        {
            using var rtb = new RtbWithCallback();
            Assert.NotNull(rtb.Callback());
        }

        [Fact]
        public void FileDialog_HookProc_AnswersZero()
        {
            using var dlg = new SWF.OpenFileDialog();
            MethodInfo hook = typeof(SWF.FileDialog).GetMethod("HookProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(IntPtr.Zero, (IntPtr)hook.Invoke(dlg, new object[] { IntPtr.Zero, 0x004E, IntPtr.Zero, IntPtr.Zero })!);
            Assert.Equal(IntPtr.Zero, (IntPtr)hook.Invoke(dlg, new object[] { IntPtr.Zero, 0x0110, IntPtr.Zero, IntPtr.Zero })!);
        }
    }
}
