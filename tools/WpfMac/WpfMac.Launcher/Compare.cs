// `wpfmac compare <App.dll>` (Windows): the app twice, side by side -- its own .exe on stock WPF, and
// the same folder through `wpfmac run` on the WebGPU stack -- tiled left and right in the work area at
// the same size, the WebGPU one titled "[WebGPU] ...". With --screenshot, both windows are captured
// after --seconds (PrintWindow with PW_RENDERFULLCONTENT, which includes GPU swap-chain content) and a
// diff image and score are written beside them.
//
// --isolate gives the WebGPU instance its own names for kernel objects, for apps that allow one
// instance only (see WpfMac.Detours/Isolation.cs).

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace WpfMac;

internal static partial class Compare
{
    public static int Run(Runner webgpu, IReadOnlyList<string> appArgs, string? screenshotDir, int seconds, bool close)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new UserError("compare runs on Windows, where the app's own .exe can start it on stock WPF beside the WebGPU run. On macOS use `wpfmac run`.");
        }
        AppFiles app = webgpu.App;
        if (!File.Exists(app.Exe)) throw new UserError($"{app.Exe} does not exist; compare needs the app's apphost to start the stock instance.");

        var stockInfo = new ProcessStartInfo(app.Exe) { UseShellExecute = false, WorkingDirectory = app.Directory };
        foreach (string a in appArgs) stockInfo.ArgumentList.Add(a);
        using Process stock = Process.Start(stockInfo) ?? throw new UserError($"could not start {app.Exe}");
        Console.WriteLine($"wpfmac: stock   pid {stock.Id}  {app.Exe}");

        Runner.Prepared prepared = webgpu.Prepare(appArgs);
        using Process gpu = Process.Start(prepared.StartInfo) ?? throw new UserError("could not start the WebGPU instance");
        Console.WriteLine($"wpfmac: WebGPU  pid {gpu.Id}  log {prepared.LogPath}");

        IntPtr stockWindow = WaitForWindow(stock, TimeSpan.FromSeconds(90));
        IntPtr gpuWindow = WaitForWindow(gpu, TimeSpan.FromSeconds(90));
        if (stockWindow == IntPtr.Zero || gpuWindow == IntPtr.Zero)
        {
            Console.Error.WriteLine($"wpfmac: no main window from {(stockWindow == IntPtr.Zero ? "the stock" : "the WebGPU")} instance within 90 s");
        }
        Tile(stockWindow, gpuWindow);

        if (screenshotDir != null)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, seconds)));
            Directory.CreateDirectory(screenshotDir);
            var a = Capture(stockWindow);
            var b = Capture(gpuWindow);
            if (a != null) Png.Write(Path.Combine(screenshotDir, "stock.png"), a.Value.W, a.Value.H, a.Value.Bgra);
            if (b != null) Png.Write(Path.Combine(screenshotDir, "webgpu.png"), b.Value.W, b.Value.H, b.Value.Bgra);
            if (a != null && b != null)
            {
                double same = Diff(a.Value, b.Value, Path.Combine(screenshotDir, "diff.png"));
                Console.WriteLine($"wpfmac: {same:P1} of pixels match (|delta| <= 16 per channel); images in {screenshotDir}");
            }
        }

        if (close)
        {
            stock.CloseMainWindow();
            gpu.CloseMainWindow();
            if (!stock.WaitForExit(10_000)) stock.Kill();
            if (!gpu.WaitForExit(10_000)) gpu.Kill();
            return 0;
        }

        stock.WaitForExit();
        gpu.WaitForExit();
        return gpu.ExitCode;
    }

    private static IntPtr WaitForWindow(Process p, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout && !p.HasExited)
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero && IsWindowVisible(p.MainWindowHandle)) return p.MainWindowHandle;
            Thread.Sleep(250);
        }
        return IntPtr.Zero;
    }

    private static void Tile(IntPtr left, IntPtr right)
    {
        RECT work = default;
        SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, ref work, 0);
        int w = (work.Right - work.Left) / 2, h = work.Bottom - work.Top;
        foreach (var (hwnd, x) in new[] { (left, work.Left), (right, work.Left + w) })
        {
            if (hwnd == IntPtr.Zero) continue;
            ShowWindow(hwnd, 9 /* SW_RESTORE */);
            MoveWindow(hwnd, x, work.Top, w, h, true);
        }
    }

    private readonly record struct Image(int W, int H, byte[] Bgra);

    private static unsafe Image? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            PrintWindow(hwnd, mem, 2 /* PW_RENDERFULLCONTENT */);
            byte[] pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;   // GDI leaves alpha undefined
            return new Image(w, h, pixels);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Writes a diff image (differing pixels red over a faded copy of the stock one) and returns the share that match.</summary>
    private static double Diff(Image a, Image b, string path)
    {
        int w = Math.Min(a.W, b.W), h = Math.Min(a.H, b.H);
        var diff = new byte[w * h * 4];
        long same = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int ia = (y * a.W + x) * 4, ib = (y * b.W + x) * 4, id = (y * w + x) * 4;
                bool match = Math.Abs(a.Bgra[ia] - b.Bgra[ib]) <= 16 && Math.Abs(a.Bgra[ia + 1] - b.Bgra[ib + 1]) <= 16 && Math.Abs(a.Bgra[ia + 2] - b.Bgra[ib + 2]) <= 16;
                if (match)
                {
                    same++;
                    byte g = (byte)(160 + (a.Bgra[ia] + a.Bgra[ia + 1] + a.Bgra[ia + 2]) / 3 * 95 / 255);
                    diff[id] = g; diff[id + 1] = g; diff[id + 2] = g;
                }
                else
                {
                    diff[id] = 0; diff[id + 1] = 0; diff[id + 2] = 255;
                }
                diff[id + 3] = 255;
            }
        }
        Png.Write(path, w, h, diff);
        return w * h == 0 ? 0 : (double)same / (w * h);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(IntPtr hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SystemParametersInfoW(uint action, uint param, ref RECT rect, uint ini);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
}

/// <summary>A minimal PNG writer (8-bit RGBA, no filtering): enough for screenshots, without System.Drawing.</summary>
internal static class Png
{
    private static readonly uint[] s_crc = MakeCrcTable();

    public static void Write(string path, int width, int height, byte[] bgra)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width); WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 6;   // 8 bits, RGBA
        Chunk(file, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (int y = 0; y < height; y++)
            {
                row[0] = 0;
                for (int x = 0; x < width; x++)
                {
                    int s = (y * width + x) * 4, d = 1 + x * 4;
                    row[d] = bgra[s + 2]; row[d + 1] = bgra[s + 1]; row[d + 2] = bgra[s]; row[d + 3] = bgra[s + 3];
                }
                z.Write(row);
            }
        }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in t) crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        var c = new byte[4];
        WriteBE(c, 0, crc ^ 0xFFFFFFFF);
        s.Write(c);
    }

    private static void WriteBE(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
