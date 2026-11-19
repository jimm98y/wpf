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
    /// <summary>The tests that need what the browser has not: a GPU device of the test's, an HTTP server of its own, Windows.</summary>
    private static readonly string[] s_notHere = { "Texture", "D3D", "Device", "Http", "Probe" };

    /// <summary>The Media Foundation tests compiled in, each run (a theory once an inline data): "MF-TEST name: passed|skipped|failed ...".</summary>
    private static void RunTests(string filter)
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (var type in typeof(Program).Assembly.GetTypes().Where(t => t.Namespace == "WgpuInterop.Tests.MediaFoundation" && t.IsPublic).OrderBy(t => t.Name))
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<Xunit.FactAttribute>() != null).OrderBy(m => m.Name))
            {
                var data = m.GetCustomAttributes<Xunit.InlineDataAttribute>().Select(d => (object?[])d.GetType().GetProperty("Data")!.GetValue(d)!).ToList();
                if (data.Count == 0) data.Add(Array.Empty<object?>());
                foreach (var args in data)
                {
                    string name = $"{type.Name}.{m.Name}" + (args.Length > 0 ? $"({string.Join(", ", args)})" : "");
                    if (filter.Length > 0 && !name.Contains(filter)) continue;
                    Console.WriteLine($"MF-TEST-START {name}");
                    if (s_notHere.Any(n => m.Name.Contains(n))) { Console.WriteLine($"MF-TEST {name}: skipped (not of the browser)"); skipped++; continue; }
                    try
                    {
                        m.Invoke(Activator.CreateInstance(type), args);
                        Console.WriteLine($"MF-TEST {name}: passed");
                        passed++;
                    }
                    catch (TargetInvocationException e) when (e.InnerException?.GetType().Name.Contains("Skip") == true)
                    {
                        Console.WriteLine($"MF-TEST {name}: skipped: {e.InnerException.Message}");
                        skipped++;
                    }
                    catch (TargetInvocationException e)
                    {
                        Console.WriteLine($"MF-TEST {name}: failed: {(e.InnerException?.ToString() ?? e.ToString()).Replace('\n', ' ')}");
                        failed++;
                    }
                }
            }
        Console.WriteLine($"MF-TESTS passed {passed} failed {failed} skipped {skipped}");
    }

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
        RunTests(filter);
        if (filter.Length > 0) return 0;
        Console.WriteLine("MF-REPORT-BEGIN");
        foreach (var l in all) Console.WriteLine("MF|" + l);
        Console.WriteLine("MF-REPORT-END");
        return 0;
    }
}
