// wpfrun bundle --visionos: the --ios bundle, as a visionOS app (the simulator's) rather than an iPad app
// run in its compatibility window.
//
// .NET ships no visionOS runtime, but needs none: visionOS is iOS's arm64 Darwin, with the same UIKit and
// Metal the iOS head uses, and its loader takes an iOS binary as its own once the binary says it is one.
// So the iOS build is taken as it is and each Mach-O in it -- the host, Mono, the runtime's native
// libraries -- is marked for visionOS (LC_BUILD_VERSION platform 12, the simulator's, from the iOS
// simulator build; 11, a device's, from the iOS device build), the bundle's Info.plist for Vision (device
// family 7, the XRSimulator or XROS platform), and every binary signed again, since the mark changes what
// was signed (or left for the Packager to sign: visionos.pack, `wpfrun pack --visionos`). A visionOS app is what can later open an immersive space for stereo
// content, which an iPad app in its compatibility window cannot.

using System.Diagnostics;

namespace WpfWebGpu;

internal static partial class Bundle
{
    private const int VisionOsPlatform = 11;            // PLATFORM_XROS
    private const int VisionOsSimulatorPlatform = 12;   // PLATFORM_XROS_SIMULATOR
    private const int VisionDeviceFamily = 7;

    /// <summary>An iOS .app (the simulator's, or with <paramref name="device"/> a device's) made a visionOS one; signed
    /// ad hoc unless <paramref name="sign"/> is false (the Packager's template, which it signs as it fills).</summary>
    internal static int RetagForVisionOs(string appDir, bool device = false, bool sign = true)
    {
        string sdkName = device ? "xros" : "xrsimulator";
        string sdk = Tool("xcrun", "--sdk", sdkName, "--show-sdk-version").Trim();
        if (string.IsNullOrEmpty(sdk)) throw new UserError($"no visionOS SDK (xcrun --sdk {sdkName}); install the visionOS platform in Xcode.");
        string minimum = sdk.Split('.')[0] + ".0";

        var binaries = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
            if (IsMachO(file)) binaries.Add(file);
        foreach (string bin in binaries)
        {
            string retagged = bin + ".visionos";
            int code = Run("xcrun", "vtool", "-set-build-version", (device ? VisionOsPlatform : VisionOsSimulatorPlatform).ToString(), minimum, sdk, "-replace", "-output", retagged, bin);
            if (code != 0) return code;
            File.Move(retagged, bin, overwrite: true);
        }

        // the immersive space a 3D view fills the eyes in (WpfImmersive.swift below), before the binaries are listed
        int shim = BuildImmersiveShim(appDir, sdkName, minimum, device);
        if (shim != 0) return shim;
        binaries.Add(Path.Combine(appDir, "Frameworks", ImmersiveShim));

        string plist = Path.Combine(appDir, "Info.plist");
        foreach (string command in new[]
        {
            "Delete :UIDeviceFamily", "Add :UIDeviceFamily array", $"Add :UIDeviceFamily:0 integer {VisionDeviceFamily}",
            "Delete :CFBundleSupportedPlatforms", "Add :CFBundleSupportedPlatforms array", "Add :CFBundleSupportedPlatforms:0 string " + (device ? "XROS" : "XRSimulator"),
            "Delete :MinimumOSVersion", $"Add :MinimumOSVersion string {minimum}",
            "Delete :DTPlatformName", "Add :DTPlatformName string " + sdkName,
            // an immersive space is a scene of its own, beside the window's
            "Set :UIApplicationSceneManifest:UIApplicationSupportsMultipleScenes true",
        })
            Run("/usr/libexec/PlistBuddy", "-c", command, plist);   // a Delete of what is not there fails, and is meant to

        if (!sign) return 0;
        // the libraries first, the bundle (its executable and seal) last
        foreach (string bin in binaries.OrderByDescending(b => b.Length))
        {
            if (Path.GetDirectoryName(bin) == appDir && Path.GetExtension(bin) == "") continue;
            int code = Run("codesign", "--force", "--sign", "-", bin);
            if (code != 0) return code;
        }
        return Run("codesign", "--force", "--sign", "-", appDir);
    }

    // ---- the immersive space ---------------------------------------------------------------------------------
    //
    // A 3D view filling the eyes (as the Quest's host has it, AndroidXrHost) is drawn in an immersive space, into
    // the textures visionOS's compositor gives (CompositorServices: a layer renderer). Only SwiftUI makes a layer
    // renderer -- a CompositorLayer in an ImmersiveSpace -- and a UIKit app opens a SwiftUI scene through a hosting
    // scene delegate (UIHostingSceneDelegate, visionOS 26). So a small Swift library does that much and hands the
    // renderer on (wpf_immersive_open's callback); the app's side (VisionXrHost) runs the frames with the
    // compositor's C API. Built here, with the SDK the bundle is for; Apple's toolchain, nothing else.

    private const string ImmersiveShim = "libWpfImmersive.dylib";

    private static int BuildImmersiveShim(string appDir, string sdkName, string minimum, bool device)
    {
        string frameworks = Path.Combine(appDir, "Frameworks");
        Directory.CreateDirectory(frameworks);
        string work = Path.Combine(Path.GetTempPath(), "wpfrun-immersive-" + Environment.ProcessId);
        Directory.CreateDirectory(work);
        string source = Path.Combine(work, "WpfImmersive.swift");
        File.WriteAllText(source, ImmersiveSwift);
        string sdkPath = Tool("xcrun", "--sdk", sdkName, "--show-sdk-path").Trim();
        int code = Run("xcrun", "--sdk", sdkName, "swiftc", "-parse-as-library", "-emit-library",
            "-target", $"arm64-apple-xros{minimum}{(device ? "" : "-simulator")}", "-sdk", sdkPath, "-O",
            "-module-name", "WpfImmersive", "-Xlinker", "-install_name", "-Xlinker", "@rpath/" + ImmersiveShim,
            "-o", Path.Combine(frameworks, ImmersiveShim), source);
        try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        return code;
    }

    private const string ImmersiveSwift = """"
        // The immersive space a 3D view of the app fills the eyes in (VisionXrHost, the app's side). A CompositorLayer's
        // frames run here: each locates the eyes (ARKit's device anchor, the drawable's views), which the app draws
        // the view for (wpf_xr_eyes); the latest picture the app has given (wpf_xr_submit) is put into the eyes'
        // textures and presented with the anchor it was drawn for, so that the compositor moves it to where the eyes
        // are when it shows it -- as an OpenXR host submits a picture with the views it was drawn for.
        import SwiftUI
        import CompositorServices
        import ARKit
        import Metal
        import UIKit

        struct WpfLayerConfiguration: CompositorLayerConfiguration {
            func makeConfiguration(capabilities: LayerRenderer.Capabilities, configuration: inout LayerRenderer.Configuration) {
                // a texture an eye
                if capabilities.supportedLayouts(options: []).contains(.dedicated) { configuration.layout = .dedicated }
                configuration.depthFormat = .depth32Float
                configuration.colorFormat = .bgra8Unorm_srgb
                configuration.isFoveationEnabled = false
            }
        }

        final class WpfImmersiveSceneDelegate: NSObject, UIHostingSceneDelegate {
            static var rootScene: some Scene {
                ImmersiveSpace(id: "wpf-immersive") {
                    CompositorLayer(configuration: WpfLayerConfiguration()) { renderer in
                        WpfXr.shared.start(renderer)
                    }
                }
                // the room around the view, as the headsets' own apps show it
                .immersionStyle(selection: .constant(.mixed), in: .mixed)
            }
        }

        extension Duration {
            var seconds: TimeInterval { TimeInterval(components.seconds) + TimeInterval(components.attoseconds) / 1e18 }
        }

        final class WpfXr {
            static let shared = WpfXr()
            var closed: (@convention(c) () -> Void)?
            private let lock = NSLock()
            private var renderer: LayerRenderer?
            private let session = ARKitSession()
            private let world = WorldTrackingProvider()
            private var device: MTLDevice?
            private var queue: MTLCommandQueue?
            private var pipeline: MTLRenderPipelineState?
            // the eyes as the latest frame located them: position, turn, field of view's angles (left, right, up,
            // down) for each, 22 floats; and their number, which keeps the anchor they were located with
            private var eyes = [Float](repeating: 0, count: 22)
            private var eyesNumber: Int64 = 0
            private var anchors = [Int64: DeviceAnchor]()
            // the app's latest picture: both eyes side by side, straight colour; and the eyes' number it was drawn for
            private var picture: MTLTexture?
            private var pictureNumber: Int64 = -1
            private var frames = 0
            // the depth it was drawn with: each eye's cells' nearest (0 near .. 1 far, side by side as the picture), the
            // eyes' near and far planes, and the picture's number it is of
            private var depthCells: MTLTexture?
            private var depthNumber: Int64 = -1
            private var clip = [Float](repeating: 0, count: 4)
            private var depthPipeline: MTLRenderPipelineState?

            // the hands' pinches in the space (each its id, active or not, where the hand is), for the app to turn,
            // scale and move the view by (VisionXrHost)
            var spatial: (@convention(c) (Int64, Int32, Float, Float, Float) -> Void)?

            func start(_ renderer: LayerRenderer) {
                NSLog("[xr] immersive: the layer renderer is there")
                self.renderer = renderer
                DispatchQueue.main.async {
                    renderer.onSpatialEvent = { events in
                        for event in events {
                            let p = event.inputDevicePose?.pose3D.position ?? event.location3D
                            let active: Int32 = event.phase == .active ? 1 : 0
                            WpfXr.shared.spatial?(Int64(truncatingIfNeeded: event.id.hashValue), active, Float(p.x), Float(p.y), Float(p.z))
                        }
                    }
                }
                device = renderer.device
                queue = device?.makeCommandQueue()
                pipeline = makePipeline()
                depthPipeline = makePipeline(depth: true)
                Task { do { try await session.run([world]) } catch { print("[xr] world tracking: \\(error)") } }
                let thread = Thread { self.loop(renderer) }
                thread.name = "Immersive"
                thread.start()
            }

            private func loop(_ renderer: LayerRenderer) {
                while true {
                    switch renderer.state {
                    case .invalidated:
                        lock.lock(); self.renderer = nil; picture = nil; pictureNumber = -1; depthCells = nil; depthNumber = -1; anchors.removeAll(); lock.unlock()
                        closed?()
                        return
                    case .paused:
                        renderer.waitUntilRunning()
                    default:
                        autoreleasepool { frame(renderer) }
                    }
                }
            }

            private func frame(_ renderer: LayerRenderer) {
                guard let frame = renderer.queryNextFrame() else { return }
                frame.startUpdate()
                frame.endUpdate()
                guard let timing = frame.predictTiming() else { return }
                LayerRenderer.Clock().wait(until: timing.optimalInputTime)
                frame.startSubmission()
                defer { frame.endSubmission() }
                guard let drawable = frame.queryDrawables().first, let queue, let commands = queue.makeCommandBuffer() else { return }
                let time = LayerRenderer.Clock.Instant.epoch.duration(to: drawable.frameTiming.presentationTime).seconds
                let anchor = world.queryDeviceAnchor(atTimestamp: time)
                frames += 1
                if frames == 1 || frames % 600 == 0 { NSLog("[xr] immersive: frame \(frames), head \(anchor == nil ? "not tracked" : "tracked"), \(drawable.views.count) views, \(drawable.colorTextures.count) textures, picture \(picture == nil ? "none" : "\(pictureNumber)")") }
                lock.lock()
                if let anchor, !drawable.views.isEmpty {
                    // the eyes in the room: the anchor's pose, then each view's from it (one view, a simulator's: both)
                    eyesNumber += 1
                    for i in 0..<2 {
                        let index = min(i, drawable.views.count - 1)
                        let view = drawable.views[index]
                        let pose = anchor.originFromAnchorTransform * view.transform
                        let q = simd_quatf(pose)
                        // the field of view from the projection the compositor wants (in mixed immersion a view's
                        // tangents are not to be read): its sides' tangents from its x and y terms
                        let p = drawable.computeProjection(convention: .rightUpBack, viewIndex: index)
                        let left = (p.columns.2.x - 1) / p.columns.0.x, right = (p.columns.2.x + 1) / p.columns.0.x
                        let down = (p.columns.2.y - 1) / p.columns.1.y, up = (p.columns.2.y + 1) / p.columns.1.y
                        let at = i * 11
                        eyes[at] = pose.columns.3.x; eyes[at + 1] = pose.columns.3.y; eyes[at + 2] = pose.columns.3.z
                        eyes[at + 3] = q.imag.x; eyes[at + 4] = q.imag.y; eyes[at + 5] = q.imag.z; eyes[at + 6] = q.real
                        eyes[at + 7] = atan(left); eyes[at + 8] = atan(right); eyes[at + 9] = atan(up); eyes[at + 10] = atan(down)
                    }
                    anchors[eyesNumber] = anchor
                    anchors = anchors.filter { $0.key > eyesNumber - 16 }
                }
                let shown = picture
                let drawnFor = anchors[pictureNumber]
                let shownDepth = depthNumber == pictureNumber ? depthCells : nil
                let planes = clip
                lock.unlock()
                // the picture with the anchor it was drawn for; with none yet, nothing, at this frame's
                drawable.deviceAnchor = shown != nil ? (drawnFor ?? anchor) : anchor
                // a texture an eye, its half of the picture (one, a simulator's single view: the left eye's)
                for i in 0..<min(2, drawable.colorTextures.count) {
                    let projection = drawable.views.isEmpty ? matrix_identity_float4x4 : drawable.computeProjection(convention: .rightUpBack, viewIndex: min(i, drawable.views.count - 1))
                    draw(commands, picture: shown, cells: shownDepth, near: planes[2 * i], far: planes[2 * i + 1], projection: projection,
                         eye: i, into: drawable.colorTextures[i], depth: i < drawable.depthTextures.count ? drawable.depthTextures[i] : nil)
                }
                drawable.encodePresent(commandBuffer: commands)
                commands.commit()
            }

            // an eye's half of the picture into its texture (premultiplied: the compositor blends it over the room)
            private func draw(_ commands: MTLCommandBuffer, picture: MTLTexture?, cells: MTLTexture?, near: Float, far: Float, projection: simd_float4x4,
                              eye: Int, into target: MTLTexture, depth: MTLTexture?) {
                let pass = MTLRenderPassDescriptor()
                pass.colorAttachments[0].texture = target
                pass.colorAttachments[0].loadAction = .clear
                pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
                pass.colorAttachments[0].storeAction = .store
                if let depth {
                    pass.depthAttachment.texture = depth
                    pass.depthAttachment.loadAction = .clear
                    pass.depthAttachment.clearDepth = 0     // reverse depth: nothing near
                    pass.depthAttachment.storeAction = .store
                }
                guard let encoder = commands.makeRenderCommandEncoder(descriptor: pass) else { return }
                if let picture, let pipeline {
                    var half: SIMD2<Float> = eye < 0 ? SIMD2(0, 1) : SIMD2(Float(eye) * 0.5, 0.5)
                    encoder.setFragmentBytes(&half, length: MemoryLayout<SIMD2<Float>>.size, index: 0)
                    encoder.setFragmentTexture(picture, index: 0)
                    if let cells, let depthPipeline, depth != nil, far > near, near > 0 {
                        // each texel at its distance, as the compositor's projection for this eye puts it (reverse depth):
                        // moved by it to where the eyes are, and among what else is in the room
                        encoder.setRenderPipelineState(depthPipeline)
                        let depthState = MTLDepthStencilDescriptor()
                        depthState.depthCompareFunction = .always
                        depthState.isDepthWriteEnabled = true
                        if let state = device?.makeDepthStencilState(descriptor: depthState) { encoder.setDepthStencilState(state) }
                        var planes = SIMD2<Float>(near, far)
                        var p = projection
                        encoder.setFragmentBytes(&planes, length: MemoryLayout<SIMD2<Float>>.size, index: 1)
                        encoder.setFragmentBytes(&p, length: MemoryLayout<simd_float4x4>.size, index: 2)
                        encoder.setFragmentTexture(cells, index: 1)
                    } else {
                        encoder.setRenderPipelineState(pipeline)
                    }
                    encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 3)
                }
                encoder.endEncoding()
            }

            private func makePipeline(depth: Bool = false) -> MTLRenderPipelineState? {
                let source = """
                #include <metal_stdlib>
                using namespace metal;
                struct V { float4 position [[position]]; float2 uv; };
                vertex V vs(uint i [[vertex_id]]) {
                    float2 p = float2((i << 1) & 2, i & 2);
                    V o; o.position = float4(p * 2.0 - 1.0, 0.0, 1.0); o.uv = float2(p.x, 1.0 - p.y); return o;
                }
                fragment float4 fs(V v [[stage_in]], constant float2& half_ [[buffer(0)]], texture2d<float> picture [[texture(0)]]) {
                    constexpr sampler s(filter::linear, address::clamp_to_edge);
                    float4 c = picture.sample(s, float2(half_.x + v.uv.x * half_.y, v.uv.y));
                    return float4(c.rgb * c.a, c.a);
                }
                struct Out { float4 color [[color(0)]]; float depth [[depth(any)]]; };
                // the picture with its depth: a cell's nearest (0 near .. 1 far between the planes it was drawn with) back
                // to its distance, then through the compositor's projection for the eye (reverse: 0 is far)
                fragment Out fsDepth(V v [[stage_in]], constant float2& half_ [[buffer(0)]], constant float2& planes [[buffer(1)]],
                                     constant float4x4& projection [[buffer(2)]], texture2d<float> picture [[texture(0)]], texture2d<float> cells [[texture(1)]]) {
                    constexpr sampler s(filter::linear, address::clamp_to_edge);
                    constexpr sampler nearest(filter::nearest, address::clamp_to_edge);
                    float2 uv = float2(half_.x + v.uv.x * half_.y, v.uv.y);
                    float4 c = picture.sample(s, uv);
                    float d = cells.sample(nearest, uv).r;
                    Out o;
                    o.color = float4(c.rgb * c.a, c.a);
                    if (d >= 0.99999 || c.a <= 0.0) { o.depth = 0.0; return o; }
                    float n = planes.x, f = planes.y;
                    float z = n * f / (f - d * (f - n));
                    float4 at = projection * float4(0.0, 0.0, -z, 1.0);
                    o.depth = clamp(at.z / at.w, 0.0, 1.0);
                    return o;
                }
                """
                guard let device, let library = try? device.makeLibrary(source: source, options: nil) else { return nil }
                let d = MTLRenderPipelineDescriptor()
                d.vertexFunction = library.makeFunction(name: "vs")
                d.fragmentFunction = library.makeFunction(name: depth ? "fsDepth" : "fs")
                d.colorAttachments[0].pixelFormat = .bgra8Unorm_srgb
                d.depthAttachmentPixelFormat = .depth32Float
                return try? device.makeRenderPipelineState(descriptor: d)
            }

            func latestEyes(_ out: UnsafeMutablePointer<Float>) -> Int64 {
                lock.lock(); defer { lock.unlock() }
                guard renderer != nil, eyesNumber > 0 else { return -1 }
                for i in 0..<22 { out[i] = eyes[i] }
                return eyesNumber
            }

            func submit(_ pixels: UnsafeRawPointer, _ width: Int, _ height: Int, _ bytesPerRow: Int, _ number: Int64) {
                guard let device else { return }
                lock.lock(); defer { lock.unlock() }
                if picture == nil || picture!.width != width || picture!.height != height {
                    // the picture's bytes are sRGB-encoded: read as such, they are written to the sRGB target as they are
                    let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm_srgb, width: width, height: height, mipmapped: false)
                    d.usage = .shaderRead
                    d.storageMode = .shared
                    picture = device.makeTexture(descriptor: d)
                }
                picture?.replace(region: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0, withBytes: pixels, bytesPerRow: bytesPerRow)
                pictureNumber = number
            }

            func submitDepth(_ cells: UnsafeRawPointer, _ width: Int, _ height: Int, _ bytesPerRow: Int, _ planes: [Float], _ number: Int64) {
                guard let device else { return }
                lock.lock(); defer { lock.unlock() }
                if depthCells == nil || depthCells!.width != width || depthCells!.height != height {
                    let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .r32Float, width: width, height: height, mipmapped: false)
                    d.usage = .shaderRead
                    d.storageMode = .shared
                    depthCells = device.makeTexture(descriptor: d)
                }
                depthCells?.replace(region: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0, withBytes: cells, bytesPerRow: bytesPerRow)
                if depthNumber < 0 { NSLog("[xr] immersive: the picture's depth, \(width)x\(height) cells, planes \(planes[0])..\(planes[1]) m") }
                clip = planes
                depthNumber = number
            }
        }

        /// Opens the immersive space; `closed` is called when it has gone (the app put it away, or the person did).
        @_cdecl("wpf_immersive_open")
        public func wpf_immersive_open(_ closed: @escaping @convention(c) () -> Void) {
            WpfXr.shared.closed = closed
            DispatchQueue.main.async {
                guard let request = UISceneSessionActivationRequest(hostingDelegateClass: WpfImmersiveSceneDelegate.self, id: "wpf-immersive") else {
                    print("[xr] immersive: no activation request")
                    return
                }
                UIApplication.shared.activateSceneSession(for: request) { error in print("[xr] immersive: \\(error)") }
            }
        }

        /// Where the hands' pinches are to go: `callback` gets each (id, 1 active or 0 ended, x, y, z in the space).
        @_cdecl("wpf_xr_on_spatial")
        public func wpf_xr_on_spatial(_ callback: @escaping @convention(c) (Int64, Int32, Float, Float, Float) -> Void) {
            WpfXr.shared.spatial = callback
        }

        /// Closes the immersive space.
        @_cdecl("wpf_immersive_close")
        public func wpf_immersive_close() {
            DispatchQueue.main.async {
                for session in UIApplication.shared.openSessions where session.role == .immersiveSpaceApplication {
                    UIApplication.shared.requestSceneSessionDestruction(session, options: nil) { error in print("[xr] immersive close: \\(error)") }
                }
            }
        }

        /// The eyes as the latest frame located them (22 floats: each eye's position, turn, angles); their number, or -1.
        @_cdecl("wpf_xr_eyes")
        public func wpf_xr_eyes(_ out: UnsafeMutablePointer<Float>) -> Int64 { WpfXr.shared.latestEyes(out) }

        /// The app's picture (both eyes side by side, RGBA, straight colour), drawn for the eyes of `number`.
        @_cdecl("wpf_xr_submit")
        public func wpf_xr_submit(_ pixels: UnsafeRawPointer, _ width: Int32, _ height: Int32, _ bytesPerRow: Int32, _ number: Int64) {
            WpfXr.shared.submit(pixels, Int(width), Int(height), Int(bytesPerRow), number)
        }

        /// The depth the picture of `number` was drawn with: each eye's cells' nearest (R32F, 0 near .. 1 far), and each
        /// eye's near and far planes (metres).
        @_cdecl("wpf_xr_submit_depth")
        public func wpf_xr_submit_depth(_ cells: UnsafeRawPointer, _ width: Int32, _ height: Int32, _ bytesPerRow: Int32,
                                        _ nearLeft: Float, _ farLeft: Float, _ nearRight: Float, _ farRight: Float, _ number: Int64) {
            WpfXr.shared.submitDepth(cells, Int(width), Int(height), Int(bytesPerRow), [nearLeft, farLeft, nearRight, farRight], number)
        }
        """";

    private static bool IsMachO(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            Span<byte> magic = stackalloc byte[4];
            if (s.Read(magic) != 4) return false;
            uint m = BitConverter.ToUInt32(magic);
            return m is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;   // 64-bit Mach-O, either order; fat
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int Run(string tool, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && tool != "/usr/libexec/PlistBuddy") Console.Error.WriteLine($"wpfrun bundle: {tool} {string.Join(' ', args)}: {err.Trim()}");
        return tool == "/usr/libexec/PlistBuddy" ? 0 : p.ExitCode;
    }

    private static string Tool(string tool, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : "";
    }
}
