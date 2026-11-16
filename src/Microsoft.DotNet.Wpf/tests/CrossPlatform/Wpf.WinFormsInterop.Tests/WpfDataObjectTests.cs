// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF's DataObject is a COM composition, registered in the global interface table so any apartment
// can reach it. Off Windows there is no OLE to hold that table: making a DataObject -- what an
// application does to start a drag -- threw a TypeInitializationException for want of OLE32 and took
// the application down. The table is the process's own there, as there are no apartments to cross.
//

using System;
using Xunit;
using WPF = System.Windows;

namespace Wpf.WinFormsInterop.Tests
{
    public class WpfDataObjectTests
    {
        [Fact]
        public void ADataObjectIsMadeAndKeepsItsData()
        {
            var data = new WPF.DataObject();
            data.SetData(WPF.DataFormats.Text, "tag path");
            data.SetData("Custom.Items", new[] { "a", "b" });
            Assert.Equal("tag path", data.GetData(WPF.DataFormats.Text));
            Assert.True(data.GetDataPresent("Custom.Items"));
            Assert.Equal(new[] { "a", "b" }, (string[])data.GetData("Custom.Items"));

            var second = new WPF.DataObject(WPF.DataFormats.UnicodeText, "second");
            Assert.Equal("second", second.GetData(WPF.DataFormats.UnicodeText));
        }
    }
}
