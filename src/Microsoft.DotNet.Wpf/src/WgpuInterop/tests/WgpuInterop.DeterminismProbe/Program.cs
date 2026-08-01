// Is rendering bit-reproducible? Decides whether image baselines can use exact
// comparison or need a tolerance. Renders the same scene repeatedly in one process
// and reports whether the bytes match; run it twice to also cover across-process.
using System;
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Wpf.Interop.WebGpu.Composition;

internal static class Program
{
    private static int Main()
    {
        const int W = 240, H = 180;
        string? first = null;
        for (int i = 0; i < 5; i++)
        {
            using var ctx = WgpuContext.Create();
            var r = new WgpuSceneRenderer(ctx);
            byte[] px = r.RenderToRgba(Scene(), W, H, RgbaColor.FromBytes(255, 255, 255, 255));
            string h = Convert.ToHexString(SHA256.HashData(px))[..16];
            Console.WriteLine($"  run {i}: {h}");
            first ??= h;
            if (h != first) { Console.WriteLine("NON-DETERMINISTIC within process"); return 1; }
        }
        Console.WriteLine($"deterministic within process: {first}");
        return 0;
    }

    private static SceneVisual Scene()
    {
        var root = new SceneVisual();
        root.Content.Add(new GeometryFill(new EllipseGeometry(new Vector2(60, 60), 40, 30),
            RgbaColor.FromBytes(200, 60, 60, 255)));
        root.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(110, 20, 100, 70), 14, 14),
            RgbaColor.FromBytes(60, 120, 200, 200)));
        var stops = new[] { new GradientStop(0f, RgbaColor.FromBytes(255, 240, 0, 255)),
                            new GradientStop(1f, RgbaColor.FromBytes(0, 160, 90, 255)) };
        root.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(20, 110, 190, 50)),
            new LinearGradientBrush(new Vector2(20, 110), new Vector2(210, 160), stops, GradientSpreadMethod.Pad)));
        return root;
    }
}
