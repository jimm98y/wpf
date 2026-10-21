// What an XBAP asked of its host, asked here: whether it is browser hosted, where it was launched
// from, and the page's script through BrowserInteropHelper.HostScript -- a member read, a member
// write (the page title, as GraphWorX's XBAP helper sets it), and calls of page functions.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

public static class Program
{
    private static readonly List<string> s_lines = new();

    [STAThread]
    public static void Main()
    {
        var app = new Application();
        var text = new TextBlock { Margin = new Thickness(12) };
        var window = new Window { Title = "Browser host check", Width = 560, Height = 300, Content = text };
        window.Loaded += (_, _) =>
        {
            Check();
            text.Text = string.Join("\n", s_lines);
        };
        app.Run(window);
    }

    private static void Check()
    {
        Report("IsBrowserHosted", () => BrowserInteropHelper.IsBrowserHosted.ToString());
        Report("Source", () => BrowserInteropHelper.Source?.ToString() ?? "null");
        dynamic script = BrowserInteropHelper.HostScript;
        Report("HostScript", () => script == null ? "null" : "the page's window");
        if (script == null) return;
        Report("HostScript.document.title = ...; read back", () => { script.document.title = "Set from WPF"; return (string)script.document.title; });
        Report("HostScript.navigator.language", () => (string)script.navigator.language);
        Report("HostScript.Math.max(3, 7)", () => ((double)script.Math.max(3, 7)).ToString());
        Report("HostScript.encodeURIComponent(\"a b\")", () => (string)script.encodeURIComponent("a b"));
        Report("HostScript.document.body.tagName", () => (string)script.document.body.tagName);
        Report("HostScript.location[\"protocol\"]", () => (string)script.location["protocol"]);
    }

    private static void Report(string what, Func<string> answer)
    {
        string result;
        try { result = answer(); }
        catch (Exception e) { result = e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Replace("\n", " | "); }
        string line = $"host: {what} = {result}";
        s_lines.Add(line);
        Console.WriteLine(line);
    }
}
