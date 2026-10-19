// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>The method name of an interface's vtable slot, for the not-implemented log.</summary>
    internal static class SlotNames
    {
        private static readonly Dictionary<string, string[]> s_cache = new();

        public static string Name(string iface, int slot)
        {
            lock (s_cache)
            {
                if (!s_cache.TryGetValue(iface, out var names))
                {
                    names = Array.Empty<string>();
                    var t = typeof(Vtbl).GetNestedType(iface, BindingFlags.NonPublic | BindingFlags.Public);
                    if (t != null)
                    {
                        var list = new List<(int, string)>();
                        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                            if (f.IsLiteral && f.FieldType == typeof(int) && f.Name != "SlotCount") list.Add(((int)f.GetRawConstantValue()!, f.Name));
                        names = new string[list.Count == 0 ? 0 : list.ConvertAll(x => x.Item1).Max() + 1];
                        foreach (var (i, n) in list) names[i] = n;
                    }
                    s_cache[iface] = names;
                }
                return (uint)slot < (uint)names.Length ? names[slot] ?? "?" : "?";
            }
        }
    }
}
