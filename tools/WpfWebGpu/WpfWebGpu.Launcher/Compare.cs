// `wpfrun compare <App.dll>` (Windows): the app twice, side by side -- its own .exe on stock WPF, and
// the same folder through `wpfrun run` on the WebGPU stack -- placed left and right at the same size
// (--size, or half the work area each). The captions are left alone unless --title-prefix asks:
// they are part of what is compared.
//
// With --screenshot, after --seconds each window is brought to the front, then both are made inactive
// (the taskbar is activated) and the cursor is parked off them, so that neither shows focus, hover or
// an active caption; then each is captured FROM THE SCREEN, by its visible frame (DWM's extended frame
// bounds), from a per-monitor DPI aware thread so the pixels are the physical ones. PrintWindow is not
// used: it renders the window again rather than showing what is on screen, and gives nothing for a GPU
// swap chain on some drivers. The diff counts every pixel whose colour differs in any channel, for the
// whole window and for its client area; --tolerance <n> also reports the count beyond n per channel.
//
// --isolate gives the WebGPU instance its own names for kernel objects, for apps that allow one
// instance only (see WpfWebGpu.Detours/Isolation.cs).

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace WpfWebGpu;

internal static partial class Compare
{
    public readonly record struct Options(int Tolerance, int Width, int Height);

    public static int Run(Runner webgpu, IReadOnlyList<string> appArgs, string? screenshotDir, int seconds, bool close, Options options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new UserError("compare runs on Windows, where the app's own .exe can start it on stock WPF beside the WebGPU run. On macOS use `wpfrun run`.");
        }
        SetThreadDpiAwarenessContext(-4);    // PER_MONITOR_AWARE_V2: physical pixels everywhere
        AppFiles app = webgpu.App;
        if (!File.Exists(app.Exe)) throw new UserError($"{app.Exe} does not exist; compare needs the app's apphost to start the stock instance.");

        var stockInfo = new ProcessStartInfo(app.Exe) { UseShellExecute = false, WorkingDirectory = app.Directory };
        foreach (string a in appArgs) stockInfo.ArgumentList.Add(a);
        using Process stock = Process.Start(stockInfo) ?? throw new UserError($"could not start {app.Exe}");
        Console.WriteLine($"wpfrun: stock   pid {stock.Id}  {app.Exe}");

        Runner.Prepared prepared = webgpu.Prepare(appArgs);
        using Process gpu = Process.Start(prepared.StartInfo) ?? throw new UserError("could not start the WebGPU instance");
        Console.WriteLine($"wpfrun: WebGPU  pid {gpu.Id}  log {prepared.LogPath}");

        IntPtr stockWindow = WaitForWindow(stock, TimeSpan.FromSeconds(180));
        IntPtr gpuWindow = WaitForWindow(gpu, TimeSpan.FromSeconds(180));
        if (stockWindow == IntPtr.Zero || gpuWindow == IntPtr.Zero)
        {
            Console.Error.WriteLine($"wpfrun: no main window from {(stockWindow == IntPtr.Zero ? "the stock" : "the WebGPU")} instance within 180 s");
        }
        Tile(stockWindow, gpuWindow, options);

        if (screenshotDir != null)
        {
            Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, seconds)));
            // The main window then, not the first one: a splash screen is gone by now.
            stockWindow = CurrentWindow(stock, stockWindow);
            gpuWindow = CurrentWindow(gpu, gpuWindow);
            Tile(stockWindow, gpuWindow, options);   // an app may have moved itself while it started
            Directory.CreateDirectory(screenshotDir);
            BringToFront(stockWindow);
            BringToFront(gpuWindow);
            Deactivate();
            Thread.Sleep(1500);
            var a = Capture(stockWindow);
            var b = Capture(gpuWindow);
            foreach (var (shot, name) in new[] { (a, "stock"), (b, "webgpu") })
            {
                if (shot == null) continue;
                Png.Write(Path.Combine(screenshotDir, name + ".png"), shot.Value.Window.W, shot.Value.Window.H, shot.Value.Window.Bgra);
                Png.Write(Path.Combine(screenshotDir, name + "-client.png"), shot.Value.Client.W, shot.Value.Client.H, shot.Value.Client.Bgra);
            }
            if (a != null && b != null)
            {
                Report("window", Diff(a.Value.Window, b.Value.Window, Path.Combine(screenshotDir, "diff.png"), options.Tolerance), options.Tolerance);
                Report("client", Diff(a.Value.Client, b.Value.Client, Path.Combine(screenshotDir, "diff-client.png"), options.Tolerance), options.Tolerance);
                Console.WriteLine($"wpfrun: images in {screenshotDir}");
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

    private static IntPtr CurrentWindow(Process p, IntPtr previous)
    {
        p.Refresh();
        return !p.HasExited && p.MainWindowHandle != IntPtr.Zero ? p.MainWindowHandle : previous;
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

    /// <summary>Both windows restored and at the same size: --size, or half the work area each,
    /// left and right on the primary monitor.</summary>
    private static void Tile(IntPtr left, IntPtr right, Options options)
    {
        RECT work = default;
        SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, ref work, 0);
        int half = (work.Right - work.Left) / 2;
        int w = options.Width > 0 ? options.Width : half, h = options.Height > 0 ? options.Height : work.Bottom - work.Top;
        foreach (var (hwnd, x) in new[] { (left, work.Left), (right, work.Left + Math.Max(half, w + 20)) })
        {
            if (hwnd == IntPtr.Zero) continue;
            if (IsZoomed(hwnd) || IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
            MoveWindow(hwnd, x, work.Top, w, h, true);
        }
    }

    /// <summary>To the front of the z-order and active. Windows lets a background process take the
    /// foreground only while it handles input, so the thread joins the foreground thread's input.</summary>
    private static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        uint me = GetCurrentThreadId(), fg = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        AttachThreadInput(me, fg, true);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);   // NOSIZE | NOMOVE | SHOWWINDOW
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        AttachThreadInput(me, fg, false);
        Thread.Sleep(300);
    }

    /// <summary>Neither window active (the taskbar is), the cursor off both: an active caption, a
    /// focus rectangle, a hover or a blinking caret is state, not rendering, and only one window could
    /// have it.</summary>
    private static void Deactivate()
    {
        BringToFront(FindWindowW("Shell_TrayWnd", null));
        RECT work = default;
        SystemParametersInfoW(0x0030, 0, ref work, 0);
        SetCursorPos(work.Right - 1, work.Bottom + 8);
    }

    private readonly record struct Image(int W, int H, byte[] Bgra);
    private readonly record struct Shot(Image Window, Image Client);

    private static Shot? Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, 16) != 0 && !GetWindowRect(hwnd, out r)) return null;
        GetClientRect(hwnd, out RECT c);
        POINT origin = default;
        ClientToScreen(hwnd, ref origin);
        Image? window = Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        Image? client = Grab(origin.X, origin.Y, c.Right - c.Left, c.Bottom - c.Top);
        return window == null || client == null ? null : new Shot(window.Value, client.Value);
    }

    /// <summary>What the screen shows in a rectangle.</summary>
    private static Image? Grab(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
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

    private readonly record struct DiffResult(long Pixels, long Differing, long BeyondTolerance, bool SameSize);

    private static void Report(string what, DiffResult d, int tolerance)
    {
        string size = d.SameSize ? "" : " (the two differ in size; compared over the smaller)";
        string beyond = tolerance > 0 ? $", {d.BeyondTolerance} by more than {tolerance}" : "";
        Console.WriteLine($"wpfrun: {what}: {d.Differing} of {d.Pixels} pixels differ{beyond}{size}");
    }

    /// <summary>Writes a diff image (differing pixels red over a faded copy of the stock one) and
    /// counts the pixels that differ at all, and by more than <paramref name="tolerance"/>.</summary>
    private static DiffResult Diff(Image a, Image b, string path, int tolerance)
    {
        int w = Math.Min(a.W, b.W), h = Math.Min(a.H, b.H);
        var diff = new byte[w * h * 4];
        long differing = 0, beyond = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int ia = (y * a.W + x) * 4, ib = (y * b.W + x) * 4, id = (y * w + x) * 4;
                int m = Math.Max(Math.Abs(a.Bgra[ia] - b.Bgra[ib]), Math.Max(Math.Abs(a.Bgra[ia + 1] - b.Bgra[ib + 1]), Math.Abs(a.Bgra[ia + 2] - b.Bgra[ib + 2])));
                if (m == 0)
                {
                    byte g = (byte)(160 + (a.Bgra[ia] + a.Bgra[ia + 1] + a.Bgra[ia + 2]) / 3 * 95 / 255);
                    diff[id] = g; diff[id + 1] = g; diff[id + 2] = g;
                }
                else
                {
                    differing++;
                    if (m > tolerance) beyond++;
                    diff[id] = 0; diff[id + 1] = 0; diff[id + 2] = 255;
                }
                diff[id + 3] = 255;
            }
        }
        Png.Write(path, w, h, diff);
        return new DiffResult((long)w * h, differing, beyond, a.W == b.W && a.H == b.H);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [LibraryImport("user32.dll")] private static partial IntPtr SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsZoomed(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(IntPtr hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BringWindowToTop(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AttachThreadInput(uint a, uint b, [MarshalAs(UnmanagedType.Bool)] bool attach);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial IntPtr FindWindowW(string? cls, string? title);
    [LibraryImport("dwmapi.dll")] private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

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
