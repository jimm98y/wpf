// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The data objects drag-and-drop uses off Windows.
//
// Both types here run on every head, this one included: neither touches an operating system. That is
// what makes them testable at all -- the transports around them (wl_data_device, NSDragging, the
// browser's DataTransfer) need a machine none of the automation has, but the decisions ABOUT a drag
// -- which formats it offers, what a MIME type means, what the bytes decode to -- are ordinary
// managed code and are where the bugs live.
//

using System.Text;

namespace System.Windows;

public class InMemoryDataObjectTests
{
    // Stands in for DataObject off Windows, where that type cannot be constructed at all.
    private static InMemoryDataObject Empty() => new();

    [Fact]
    public void UnsetFormatIsAbsent()
    {
        InMemoryDataObject data = Empty();

        data.GetDataPresent("nothing here").Should().BeFalse();
        data.GetData("nothing here").Should().BeNull();
        data.GetFormats().Should().BeEmpty();
    }

    [Fact]
    public void StoredFormatRoundTrips()
    {
        InMemoryDataObject data = Empty();
        data.SetData("private/format", "payload");

        data.GetDataPresent("private/format").Should().BeTrue();
        data.GetData("private/format").Should().Be("payload");
        data.GetFormats().Should().Contain("private/format");
    }

    [Fact]
    public void SetDataReplacesRatherThanAccumulates()
    {
        InMemoryDataObject data = Empty();
        data.SetData(DataFormats.UnicodeText, "first");
        data.SetData(DataFormats.UnicodeText, "second");

        data.GetData(DataFormats.UnicodeText).Should().Be("second");
    }

    /// <summary>
    ///  Text written under one name reads back under the others, as DataObject's autoConvert does on
    ///  Windows. A paste handler asking for Text after a copy that wrote UnicodeText is the case.
    /// </summary>
    [Fact]
    public void TextAliasesReadEachOther()
    {
        // A loop rather than [InlineData]: DataFormats' members are static readonly, not const, so
        // they cannot be attribute arguments.
        string[] aliases = { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat, DataFormats.OemText };

        foreach (string written in aliases)
        {
            InMemoryDataObject data = Empty();
            data.SetData(written, "aliased");

            foreach (string read in aliases)
            {
                data.GetData(read).Should().Be("aliased", $"{read} should alias {written}");
            }
        }
    }

    /// <summary>
    ///  Anything GetDataPresent answers true for must also be listed by GetFormats. Clipboard's
    ///  off-Windows arm copies a data object by enumerating formats, so a format that is present but
    ///  unlisted is a format that silently does not survive a copy.
    /// </summary>
    [Fact]
    public void EveryPresentFormatIsListed()
    {
        InMemoryDataObject data = Empty();
        data.SetData(DataFormats.UnicodeText, "text");
        data.SetData("private/format", "payload");

        foreach (string format in new[]
                 {
                     DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat,
                     DataFormats.OemText, "private/format"
                 })
        {
            data.GetDataPresent(format).Should().BeTrue($"{format} should be present");
            data.GetFormats().Should().Contain(format);
        }
    }

    /// <summary>Aliases are a convenience of autoConvert, and must not appear without it.</summary>
    [Fact]
    public void AliasesAreNotAppliedWhenAutoConvertIsOff()
    {
        InMemoryDataObject data = Empty();
        data.SetData(DataFormats.UnicodeText, "text");

        data.GetDataPresent(DataFormats.Text, autoConvert: false).Should().BeFalse();
        data.GetData(DataFormats.Text, autoConvert: false).Should().BeNull();
        data.GetFormats(autoConvert: false).Should().ContainSingle().Which.Should().Be(DataFormats.UnicodeText);
    }

    [Fact]
    public void BareStringBecomesText()
    {
        var data = new InMemoryDataObject("bare");

        data.GetData(DataFormats.UnicodeText).Should().Be("bare");
        data.GetData(DataFormats.Text).Should().Be("bare");
    }

    /// <summary>
    ///  A string[] is the only shape a file drag arrives in, and the platform sources decide what to
    ///  advertise by asking for FileDrop.
    /// </summary>
    [Fact]
    public void BareStringArrayBecomesFileDrop()
    {
        var data = new InMemoryDataObject(new[] { @"/tmp/one.txt", @"/tmp/two.txt" });

        data.GetDataPresent(DataFormats.FileDrop).Should().BeTrue();
        data.GetData(DataFormats.FileDrop).Should().BeEquivalentTo(new[] { @"/tmp/one.txt", @"/tmp/two.txt" });
    }

    [Fact]
    public void BareObjectIsStoredUnderItsTypeName()
    {
        var payload = new Uri("https://example.invalid/");
        var data = new InMemoryDataObject(payload);

        data.GetDataPresent(typeof(Uri).FullName!).Should().BeTrue();
        data.GetData(typeof(Uri)).Should().BeSameAs(payload);
    }
}

public class DataObjectFactoryTests
{
    /// <summary>
    ///  On Windows the factory must still produce the OLE-backed DataObject; the substitute exists
    ///  only because that type cannot be constructed elsewhere.
    /// </summary>
    [WpfFact]
    public void CreateProducesADataObjectOnWindows()
    {
        DataObjectFactory.Create().Should().BeOfType<DataObject>();
        DataObjectFactory.Create("text").Should().BeOfType<DataObject>();
    }

    /// <summary>
    ///  A DataObject is passed through rather than wrapped, so an app that built one keeps it.
    /// </summary>
    [WpfFact]
    public void CreateKeepsAnExistingDataObject()
    {
        DataObject original = new();

        DataObjectFactory.Create(original).Should().BeSameAs(original);
    }

    /// <summary>
    ///  A foreign IDataObject is WRAPPED on Windows, not passed through: OLE needs an IComDataObject,
    ///  which only DataObject implements. Handing the drag its own object would fail the cast.
    /// </summary>
    [WpfFact]
    public void CreateWrapsAForeignDataObjectOnWindows()
    {
        var foreign = new InMemoryDataObject("text");

        IDataObject created = DataObjectFactory.Create(foreign);

        created.Should().NotBeSameAs(foreign);
        created.Should().BeOfType<DataObject>();
    }
}

/// <summary>
///  Turning WPF's Html format into the bare markup other toolkits expect on the wire.
/// </summary>
/// <remarks>
///  WPF's Html format is CF_HTML: a header of "Key:value" lines, one of which gives the BYTE offset
///  at which the markup starts. Everything here is about cutting at that offset without trusting it,
///  since a wrong cut silently ships a fragment of header as if it were markup.
/// </remarks>
public class CfHtmlHeaderTests
{
    private static string WithHeader(string markup, int? startOverride = null)
    {
        // A minimal but real CF_HTML preamble. StartHTML is filled in with the true offset unless a
        // test is deliberately lying about it.
        string prefix = "Version:0.9\r\nStartHTML:";
        string rest = "\r\nEndHTML:00000000\r\n";
        int start = prefix.Length + 8 + rest.Length;
        return prefix + (startOverride ?? start).ToString("D8") + rest + markup;
    }

    [Fact]
    public void HeaderIsRemoved()
    {
        PlatformDragSource.StripCfHtmlHeader(WithHeader("<p>markup</p>")).Should().Be("<p>markup</p>");
    }

    [Fact]
    public void BareMarkupIsUntouched()
    {
        PlatformDragSource.StripCfHtmlHeader("<p>no header</p>").Should().Be("<p>no header</p>");
    }

    [Fact]
    public void EmptyStringIsUntouched()
    {
        PlatformDragSource.StripCfHtmlHeader(string.Empty).Should().BeEmpty();
    }

    /// <summary>
    ///  An offset past the end of the string, or at zero, is not usable. Handing the whole thing
    ///  back keeps the markup intact -- with a header on it, which is visible and recoverable --
    ///  rather than throwing or returning nothing.
    /// </summary>
    [Fact]
    public void ImplausibleOffsetsLeaveTheStringAlone()
    {
        string tooFar = WithHeader("<p>markup</p>", startOverride: 99999999);
        PlatformDragSource.StripCfHtmlHeader(tooFar).Should().Be(tooFar);

        string zero = WithHeader("<p>markup</p>", startOverride: 0);
        PlatformDragSource.StripCfHtmlHeader(zero).Should().Be(zero);
    }

    [Fact]
    public void MarkerWithNoDigitsLeavesTheStringAlone()
    {
        const string malformed = "Version:0.9\r\nStartHTML:\r\n<p>markup</p>";

        PlatformDragSource.StripCfHtmlHeader(malformed).Should().Be(malformed);
    }
}

public class DragDataObjectTests
{
    private const string Utf8Text = "text/plain;charset=utf-8";

    /// <summary>Records which MIME types were actually read, to prove reads are lazy and cached.</summary>
    private sealed class Offer
    {
        private readonly Dictionary<string, byte[]> _data;

        public Offer(Dictionary<string, byte[]> data) => _data = data;

        public List<string> Reads { get; } = new();

        public string[] Types
        {
            get
            {
                var types = new string[_data.Count];
                _data.Keys.CopyTo(types, 0);
                return types;
            }
        }

        public byte[]? Read(string mime)
        {
            Reads.Add(mime);
            return _data.TryGetValue(mime, out byte[]? bytes) ? bytes : null;
        }

        public DragDataObject Object() => new(Types, Read);
    }

    private static Offer Offering(string mime, byte[] bytes) =>
        new(new Dictionary<string, byte[]>(StringComparer.Ordinal) { [mime] = bytes });

    private static Offer OfferingText(string mime, string text) =>
        Offering(mime, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void UnofferedFormatIsAbsent()
    {
        DragDataObject data = OfferingText(Utf8Text, "hello").Object();

        data.GetDataPresent(DataFormats.FileDrop).Should().BeFalse();
        data.GetData(DataFormats.FileDrop).Should().BeNull();
    }

    [Fact]
    public void PlainTextIsOfferedUnderEveryTextFormat()
    {
        DragDataObject data = OfferingText(Utf8Text, "hello").Object();

        data.GetData(DataFormats.UnicodeText).Should().Be("hello");
        data.GetData(DataFormats.Text).Should().Be("hello");
        data.GetData(DataFormats.StringFormat).Should().Be("hello");
        data.GetFormats().Should().Contain(DataFormats.UnicodeText);
    }

    /// <summary>
    ///  text/uri-list to the local paths FileDrop promises. Comments and non-file schemes are not
    ///  paths and must not be handed over as if they were.
    /// </summary>
    [Fact]
    public void UriListBecomesLocalPaths()
    {
        const string list = "#comment\r\nfile:///tmp/one.txt\r\nhttps://example.invalid/page\r\nfile:///tmp/two.txt\r\n";
        DragDataObject data = OfferingText("text/uri-list", list).Object();

        data.GetDataPresent(DataFormats.FileDrop).Should().BeTrue();
        string[] paths = data.GetData(DataFormats.FileDrop).Should().BeOfType<string[]>().Subject;

        paths.Should().HaveCount(2);
        paths[0].Should().EndWith("one.txt");
        paths[1].Should().EndWith("two.txt");
    }

    [Fact]
    public void UriListWithNoFilesYieldsNoPaths()
    {
        DragDataObject data = OfferingText("text/uri-list", "https://example.invalid/only\r\n").Object();

        data.GetData(DataFormats.FileDrop).Should().BeOfType<string[]>().Which.Should().BeEmpty();
    }

    /// <summary>
    ///  RTF is an ASCII wire format; a stray high byte is likelier to be mislabelled Latin-1 than
    ///  UTF-8, and decoding it as UTF-8 would replace it with U+FFFD.
    /// </summary>
    [Fact]
    public void RtfDecodesAsLatin1()
    {
        // A raw 0xE9 byte in the middle of otherwise-ASCII RTF. As Latin-1 that is 'é'; decoded as
        // UTF-8 it is an invalid sequence and would come back as U+FFFD.
        byte[] rtf = Encoding.Latin1.GetBytes("{\\rtf1 caf\\'e9 é}");
        rtf.Should().Contain((byte)0xE9);

        DragDataObject data = Offering("text/rtf", rtf).Object();

        data.GetData(DataFormats.Rtf).Should().Be("{\\rtf1 caf\\'e9 é}");
    }

    [Fact]
    public void HtmlDecodesAsText()
    {
        DragDataObject data = OfferingText("text/html", "<p>markup</p>").Object();

        data.GetData(DataFormats.Html).Should().Be("<p>markup</p>");
    }

    /// <summary>
    ///  A type WPF has no mapping for is still offered verbatim, so an application that dragged its
    ///  own private format can find it again.
    /// </summary>
    [Fact]
    public void UnmappedTypeIsOfferedUnderItsOwnName()
    {
        DragDataObject data = Offering("application/x-example", new byte[] { 1, 2, 3 }).Object();

        data.GetDataPresent("application/x-example").Should().BeTrue();
        data.GetData("application/x-example").Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        data.GetFormats().Should().Contain("application/x-example");
    }

    /// <summary>
    ///  Formats are not read until asked for, and then only once: each read is a pipe round trip to
    ///  the source process, and a drag advertises types nobody ever asks about.
    /// </summary>
    [Fact]
    public void ReadsAreLazyAndCached()
    {
        Offer offer = OfferingText(Utf8Text, "hello");
        DragDataObject data = offer.Object();

        offer.Reads.Should().BeEmpty("nothing has been asked for yet");

        data.GetDataPresent(DataFormats.UnicodeText).Should().BeTrue();
        offer.Reads.Should().BeEmpty("presence is answered from the type list alone");

        data.GetData(DataFormats.UnicodeText).Should().Be("hello");
        data.GetData(DataFormats.UnicodeText).Should().Be("hello");
        offer.Reads.Should().ContainSingle();
    }

    /// <summary>A type that is offered but unreadable must not be retried on every access.</summary>
    [Fact]
    public void AFailedReadIsCachedToo()
    {
        var offer = new Offer(new Dictionary<string, byte[]>(StringComparer.Ordinal));
        var data = new DragDataObject(new[] { Utf8Text }, offer.Read);

        data.GetData(DataFormats.UnicodeText).Should().BeNull();
        data.GetData(DataFormats.UnicodeText).Should().BeNull();

        offer.Reads.Should().ContainSingle();
    }

    /// <summary>
    ///  A drag offer describes what the SOURCE holds, so it is read-only. Silently accepting a write
    ///  would let a drop handler believe it had changed data that lives in another process.
    /// </summary>
    [Fact]
    public void WritingToADragOfferThrows()
    {
        DragDataObject data = OfferingText(Utf8Text, "hello").Object();

        data.Invoking(d => d.SetData("x")).Should().Throw<NotSupportedException>();
        data.Invoking(d => d.SetData("format", "x")).Should().Throw<NotSupportedException>();
        data.Invoking(d => d.SetData(typeof(string), "x")).Should().Throw<NotSupportedException>();
        data.Invoking(d => d.SetData("format", "x", autoConvert: true)).Should().Throw<NotSupportedException>();
    }
}
