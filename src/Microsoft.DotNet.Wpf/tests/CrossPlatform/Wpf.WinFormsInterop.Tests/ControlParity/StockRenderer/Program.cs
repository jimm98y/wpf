// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Windows.Forms;
using WinFormsControlParity;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: StockRenderer <output-directory>");
            return 2;
        }

        // Visual styles on, because that is what an application gets: the classic theme is a
        // different look and comparing against it would be comparing against nothing anybody sees.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int done = SpecimenRenderer.RenderAll(args[0]);
        Console.WriteLine($"rendered {done} specimens to {args[0]}");
        return done > 0 ? 0 : 3;
    }
}
