# Media Foundation off Windows

An unmodified Windows app that decodes video itself calls Media Foundation: mfplat's functions, and
the decoder MFTs it finds with `MFTEnumEx`. Off Windows there is no Media Foundation, so the stack
provides one, in `Microsoft.Wpf.Interop.WebGpu` (`src/Microsoft.DotNet.Wpf/src/WgpuInterop/MediaFoundation`).
Its exports are `[Win32Export]` methods that the P/Invoke rewriter reaches (pinvoke-rewriter.md). Its
objects are COM objects built the way the Direct3D shim's are: a vtable per interface, the slots in
the order the Windows metadata gives.

On Windows the app keeps Windows' own Media Foundation. That Media Foundation is handed the shim's
Direct3D 11 device, and its hardware decoders reach the GPU through the shim's `ID3D11VideoDecoder`
(section "Decoding on Windows", to come).

## What there is

- **Exports:**
  - `MFStartup`, `MFShutdown`, `MFLockPlatform`, `MFUnlockPlatform`;
  - `MFCreateAttributes`, `MFCreateMediaType`, `MFCreateSample`;
  - `MFCreateMemoryBuffer`, `MFCreateAlignedMemoryBuffer`, `MFCreateDXGISurfaceBuffer`;
  - `MFCreateDXGIDeviceManager`, `MFTEnumEx`;
  - `MFFrameRateToAverageTimePerFrame`, `MFAverageTimePerFrameToFrameRate`;
  - `MFCalculateImageSize`, `MFGetStrideForBitmapInfoHeader`, `MFCopyImage`.
- **Objects:**
  - `IMFAttributes`: keys in the order they were set, the values as PROPVARIANTs at the ABI.
  - `IMFMediaType`, including `IsEqual`'s flags.
  - `IMFSample`.
  - `IMFMediaBuffer`, of memory.
  - A buffer over a shim D3D11 texture, which answers `IMFMediaBuffer`, `IMF2DBuffer2` and `IMFDXGIBuffer`. Locked, it holds the texture's bytes (of NV12: the luma rows, then the chroma rows), read back through a staging copy and written back where the lock was for writing.
  - `IMFDXGIDeviceManager`, `IMFActivate`.
- **The video decoders:** H.264, HEVC, VP9, AV1, MPEG-2, MPEG-4 Part 2 and H.263. Each is listed under Windows' name and CLSID wherever the system decodes the codec.
  - They are `IMFTransform`s that behave as Windows' decoders do:
    - synchronous, with one stream each way;
    - output types NV12, YV12, IYUV, I420 and YUY2, of the coded size (macroblocks of 16 for H.264, 8 for HEVC), with the picture in `MF_MT_MINIMUM_DISPLAY_APERTURE` and `MF_MT_GEOMETRIC_APERTURE` where it is smaller;
    - `MF_E_TRANSFORM_STREAM_CHANGE` on the first frame and on any change of size;
    - `MF_E_NOTACCEPTING` while a frame is ready, `MF_E_TRANSFORM_NEED_MORE_INPUT` while none is;
    - flush and drain.
  - Without a device manager they write the app's sample. Given one (`MFT_MESSAGE_SET_D3D_MANAGER`), they hand out samples of their own: NV12 textures of the manager's device, from a pool that each sample returns to when the app releases it.
  - `MF_LOW_LATENCY` and `ICodecAPI`'s `CODECAPI_AVLowLatencyMode` make them hand frames out as soon as they are decoded.
- **The system's decoders** (`PlatformVideoDecoder`):
  - VideoToolbox on Apple's systems, the GPU's decoder where there is one.
  - GStreamer on Linux: decodebin picks the best decoder installed, a GPU's (VA-API, V4L2) before the software ones. It is the system's library, loaded at run time and never bundled. The codecs come from the distribution's plugins (`gstreamer1.0-libav`, `-plugins-bad`, `-ugly`).
  - MediaCodec on Android, through the NDK, with frames through an image reader sized to the input type.
  - The decoders are SharpMediaFoundationInterop's (MIT, by the same author), adapted.
  - VideoToolbox's decoder is made from the stream's own configuration: H.264's and HEVC's parameter sets, the first VP9 key frame, the AV1 sequence header, the headers before the first MPEG-4 picture.
  - Frames are handed out in display order.

## Tests

They pass on macOS (VideoToolbox) and in the Ubuntu 26.04 VM (GStreamer: libav's and OpenH264's decoders). `MFDecoderTests` makes an H.264 stream whose pictures are all I_PCM macroblocks (raw samples), so a
decoder's output is known to the byte. It finds the H.264 decoder with `MFTEnumEx` and drives it
through its vtables, as an app does:
- **The app's samples:** six frames, one stream change, each frame identical.
- **The coded size:** a 56x40 picture in 64x48 of macroblocks gives an output type of 64x48, the picture in its aperture.
- **The device manager's device:** each frame an NV12 texture of the device behind `IMFDXGIBuffer`, each identical when read back.

## To come

- The browser's decoder (WebCodecs). Android's is in but not yet run on a device or emulator.
- The audio decoders (AAC, MP3, FLAC, ALAC, Opus) and the encoders.
- The source reader (`MFCreateSourceReaderFromURL`, from a byte stream, from a media source) with an MP4 demuxer of its own, the media session, and camera capture (`MFEnumDeviceSources`).
- The shim's `ID3D11VideoDecoder` for Windows' Media Foundation.
