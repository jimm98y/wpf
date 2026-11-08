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

    /// <summary>A '/' string that may be relative or absolute: a file URI where it is a path on this disk (a
    /// display template's, from the stack's own file listing), and relative, as on Windows, where it is a
    /// reference to a resource -- Telerik's RadRibbonWindow loads its template with one, and made absolute
    /// it threw "Cannot use absolute URI" and Workbench did not start.</summary>
    [Fact]
    public void ASlashUri_IsAFileOnlyWhereThereIsOne()
    {
        if (OperatingSystem.IsWindows()) return;
        string dir = TestEnvironment.NewDirectory("uri-paths");
        string file = Path.Combine(dir, "Template.gdfx");
        File.WriteAllText(file, "");

        Assert.True(WindowsPaths.Uri_new(file, UriKind.RelativeOrAbsolute).IsFile);
        Assert.True(WindowsPaths.Uri_new(Path.Combine(dir, "NotYet.gdfx"), UriKind.RelativeOrAbsolute).IsAbsoluteUri);   // in a folder that is
        Assert.False(WindowsPaths.Uri_new("/Telerik.Windows.Controls.RibbonView;component/themes/RibbonWindow.xaml", UriKind.RelativeOrAbsolute).IsAbsoluteUri);
        // tried rather than made: the same answers
        Assert.True(WindowsPaths.Uri_TryCreate(Path.Combine(dir, "NotYet.gdfx"), UriKind.RelativeOrAbsolute, out Uri? tried) && tried!.IsFile);
        Assert.Equal(Path.Combine(dir, "NotYet.gdfx"), tried.LocalPath);
        Assert.True(WindowsPaths.Uri_TryCreate("Displays/Plant.gdfx", UriKind.RelativeOrAbsolute, out Uri? relative) && !relative!.IsAbsoluteUri);
        Assert.False(WindowsPaths.Uri_new("/Images/a.png", UriKind.RelativeOrAbsolute).IsAbsoluteUri);
        Assert.False(WindowsPaths.Uri_new("/a.png", UriKind.RelativeOrAbsolute).IsAbsoluteUri);
    }
}

// The OS questions an assembly asks, rewritten onto WindowsPaths (and so answered as Windows under
// WPFWEBGPU_OS_IDENTITY=windows), told which systems the assembly knows: one that knows the system it runs on
// gets that system's answers; the unit is the method.
public sealed class OSIdentityRewriteTests
{
    [Theory]
    [InlineData(false, -1)]     // a program written for Windows: the plain stand-in
    [InlineData(true, 1)]       // a library that asks about macOS: the variant told it knows macOS (bit 1)
    public void An_assembly_s_OS_checks_are_stood_in_for_with_the_systems_it_knows(bool asksAboutMacOS, int known)
    {
        var subs = WpfWebGpu.ComRewrite.PathSubstitutions.For(new[] { AppContext.BaseDirectory })!;
        var module = Mono.Cecil.ModuleDefinition.CreateModule("OsChecks", Mono.Cecil.ModuleKind.Dll);
        var type = new Mono.Cecil.TypeDefinition("Fixture", "Checks", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(type);
        var method = new Mono.Cecil.MethodDefinition("Check", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Boolean);
        type.Methods.Add(method);
        var il = method.Body.GetILProcessor();
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, module.ImportReference(typeof(OperatingSystem).GetMethod(nameof(OperatingSystem.IsWindows))));
        if (asksAboutMacOS)
        {
            il.Emit(Mono.Cecil.Cil.OpCodes.Call, module.ImportReference(typeof(OperatingSystem).GetMethod(nameof(OperatingSystem.IsMacOS))));
            il.Emit(Mono.Cecil.Cil.OpCodes.Or);
        }
        il.Emit(Mono.Cecil.Cil.OpCodes.Ret);

        subs.Apply(module);
        var ins = method.Body.Instructions;
        var first = ins.First(i => i.Operand is Mono.Cecil.MethodReference r && r.Name == "OperatingSystem_IsWindows");
        var stand = (Mono.Cecil.MethodReference)first.Operand;
        Assert.Equal("WindowsPaths", stand.DeclaringType.Name);
        if (known < 0) Assert.Empty(stand.Parameters);
        else
        {
            Assert.Equal("System.Int32", Assert.Single(stand.Parameters).ParameterType.FullName);
            Assert.Equal(Mono.Cecil.Cil.OpCodes.Ldc_I4, first.Previous.OpCode);
            Assert.Equal(known, first.Previous.Operand);
        }
    }
}
