// wpfrun sqlbrowser (SqlBrowser.cs): the answers [MC-SQLR] gives, of the instances the folder names.

using System.Text;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed class SqlBrowserTests
{
    private static readonly List<(string, int)> Two = new() { ("SQLEXPRESS2025", 1433), ("OTHER", 14330) };

    private static string? Text(byte[]? answer)
    {
        if (answer == null) return null;
        Assert.Equal(5, answer[0]);                                         // SVR_RESP
        Assert.Equal(answer.Length - 3, answer[1] | answer[2] << 8);        // its size
        return Encoding.ASCII.GetString(answer, 3, answer.Length - 3);
    }

    [Fact]
    public void An_instance_named_is_answered_with_its_port_and_an_unknown_one_not_at_all()
    {
        string? one = Text(SqlBrowser.Answer(Encoding.ASCII.GetBytes("\u0004sqlexpress2025\0"), Two));
        Assert.Matches(@"^ServerName;[^;]+;InstanceName;SQLEXPRESS2025;IsClustered;No;Version;[^;]+;tcp;1433;;$", one);
        Assert.Null(SqlBrowser.Answer(Encoding.ASCII.GetBytes("\u0004NONE\0"), Two));
        Assert.Null(SqlBrowser.Answer(new byte[] { 0x0F, 1, (byte)'X', 0 }, Two));       // DAC: no answer
        Assert.Null(SqlBrowser.Answer(Array.Empty<byte>(), Two));
    }

    [Fact]
    public void A_broadcast_is_answered_with_every_instance_and_the_folder_s_files_name_them()
    {
        string? all = Text(SqlBrowser.Answer(new byte[] { 3 }, Two));
        Assert.Contains("InstanceName;SQLEXPRESS2025;", all);
        Assert.Contains("InstanceName;OTHER;IsClustered;No;Version;17.0.1000.7;tcp;14330;;", all);
        string folder = TestEnvironment.NewDirectory("sqlbrowser");
        File.WriteAllText(Path.Combine(folder, "com.example.a"), "SQLEXPRESS 1433\n");
        File.WriteAllText(Path.Combine(folder, "com.example.b"), "B 2000\nbroken line here\nC notaport\n");
        Assert.Equal(new[] { ("SQLEXPRESS", 1433), ("B", 2000) }, SqlBrowser.Instances(folder));
        Assert.Empty(SqlBrowser.Instances(Path.Combine(folder, "missing")));
    }
}
