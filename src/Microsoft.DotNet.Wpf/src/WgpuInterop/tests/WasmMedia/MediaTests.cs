// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The desktop's Media Foundation tests run where xUnit's runner does not (the browser, Android, iOS): each [Fact]
// (a theory once an inline data) found by reflection and invoked, one line printed of each. Shared by the heads.
//

using System;
using System.Linq;
using System.Reflection;

internal static class MediaTests
{
    /// <summary>The tests that need what these heads have not: a GPU device of the test's, an HTTP server of its own, Windows.</summary>
    private static readonly string[] s_notHere = { "Texture", "D3D", "Device", "Http", "Probe" };

    /// <summary>The Media Foundation tests compiled in, each run (a theory once an inline data): "MF-TEST name: passed|skipped|failed ...".</summary>
    public static void Run(string filter, string head)
    {
        int passed = 0, failed = 0, skipped = 0;
        foreach (var type in typeof(MediaTests).Assembly.GetTypes().Where(t => t.Namespace == "WgpuInterop.Tests.MediaFoundation" && t.IsPublic).OrderBy(t => t.Name))
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.GetCustomAttribute<Xunit.FactAttribute>() != null).OrderBy(m => m.Name))
            {
                var data = m.GetCustomAttributes<Xunit.InlineDataAttribute>().Select(d => (object?[])d.GetType().GetProperty("Data")!.GetValue(d)!).ToList();
                if (data.Count == 0) data.Add(Array.Empty<object?>());
                foreach (var args in data)
                {
                    string name = $"{type.Name}.{m.Name}" + (args.Length > 0 ? $"({string.Join(", ", args)})" : "");
                    if (filter.Length > 0 && !name.Contains(filter)) continue;
                    Console.WriteLine($"MF-TEST-START {name}");
                    if (s_notHere.Any(n => m.Name.Contains(n))) { Console.WriteLine($"MF-TEST {name}: skipped (not of {head})"); skipped++; continue; }
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

}
