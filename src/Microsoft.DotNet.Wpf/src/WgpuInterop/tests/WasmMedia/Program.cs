// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The source reader's report of the test media in the browser (MFSourceReaderProbe's, as WgpuInterop.Tests prints it
// on the desktop): each line to the console between the markers, for MFSourceReaderTests to compare with Windows'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WgpuInterop.Tests.MediaFoundation;

internal static class Program
{
    private static int Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var all = new List<string>();
        void Run(string head, Func<List<string>> report)
        {
            all.Add(head);
            Console.WriteLine("MF-PROGRESS " + head);
            try { all.AddRange(report()); }
            catch (Exception e) { all.Add("failed: " + e); }
        }
        foreach (var (name, f) in MFSourceReaderProbe.Files()) Run($"==== {name}", () => MFSourceReaderProbe.Report(f, name));
        foreach (var m in MFSourceReaderProbe.AudioFiles) Run($"==== {m}", () => MFSourceReaderProbe.AudioReport(MFSourceReaderProbe.AudioPath(m)));
        foreach (var m in MFSourceReaderProbe.VideoFiles) Run($"==== {m}", () => MFSourceReaderProbe.VideoReport(MFSourceReaderProbe.AudioPath(m)));
        foreach (var m in MFSourceReaderProbe.AvFiles)
        {
            Run($"==== {m}", () => MFSourceReaderProbe.VideoReport(MFSourceReaderProbe.AudioPath(m)));
            Run("---- audio", () => MFSourceReaderProbe.AudioReport(MFSourceReaderProbe.AudioPath(m)));
        }
        string filter = args.Length > 0 ? args[0] : "";
        MediaTests.Run(filter, "the browser");
        if (filter.Length > 0) return 0;
        Console.WriteLine("MF-REPORT-BEGIN");
        foreach (var l in all) Console.WriteLine("MF|" + l);
        Console.WriteLine("MF-REPORT-END");
        return 0;
    }
}
