// WpfWebGpu.ComInterop's WindowsPaths: the stand-ins an app's calls of the members that take a path are
// rewritten onto off Windows. The XML loaders take a URI as well as a path, and a URI stays one.

using System.Xml;
using System.Xml.Linq;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class WindowsPathsTests
{
    [Fact]
    public void XmlLoaders_TakeAFileUri_APath_AndAWindowsPath()
    {
        string dir = TestEnvironment.NewDirectory("xmlpaths");
        string file = Path.Combine(dir, "AGConfigurationMode.xml");
        File.WriteAllText(file, "<mode name=\"ag\"/>");
        // app's way: the file's URI next to its assemblies
        string uri = new Uri(new Uri(dir + "/"), "AGConfigurationMode.xml").AbsoluteUri;
        using (XmlReader r = WindowsPaths.XmlReader_Create(uri)) { r.MoveToContent(); Assert.Equal("mode", r.Name); }
        Assert.Equal("ag", WindowsPaths.XDocument_Load(uri).Root!.Attribute("name")!.Value);
        Assert.Equal("ag", WindowsPaths.XElement_Load(file).Attribute("name")!.Value);
        var doc = new XmlDocument();
        WindowsPaths.XmlDocument_Load(doc, OperatingSystem.IsWindows() ? file : file.Replace('/', '\\'));
        Assert.Equal("mode", doc.DocumentElement!.Name);
        using var text = WindowsPaths.XmlTextReader_new(uri);
        text.MoveToContent();
        Assert.Equal("mode", text.Name);
    }

    [Fact]
    public void DiskOrUri_LeavesOtherUrisAndDrivesAlone()
    {
        Assert.Equal("http://host/a.xml", WindowsPaths.DiskOrUri("http://host/a.xml"));
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("C:/data/a.xml", WindowsPaths.DiskOrUri(@"C:\data\a.xml"));       // a drive is a path's, read as Windows reads it
        Assert.Equal("/tmp/a b.xml", WindowsPaths.DiskOrUri("file:///tmp/a%20b.xml"));
    }
}
