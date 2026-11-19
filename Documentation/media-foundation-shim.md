# Media Foundation off Windows

An unmodified Windows app that decodes video itself calls Media Foundation: mfplat's functions, and
the decoder MFTs it finds with `MFTEnumEx`. Off Windows there is no Media Foundation, so the stack
provides one, in `Microsoft.Wpf.Interop.WebGpu` (`src/Microsoft.DotNet.Wpf/src/WgpuInterop/MediaFoundation`).
Its exports are `[Win32Export]` methods that the P/Invoke rewriter reaches (pinvoke-rewriter.md). Its
objects are COM objects built the way the Direct3D shim's are: a vtable per interface, the slots in
the order the Windows metadata gives.

On Windows the app keeps Windows' own Media Foundation. That Media Foundation is handed the shim's
Direct3D 11 device, and its hardware decoders reach the GPU through the shim's `ID3D11VideoDecoder`,
which forwards to the GPU's decoder through Windows' Direct3D 11 (d3d11-webgpu-shim.md, section 8, video).

## What there is

- **Exports:**
  - `MFStartup`, `MFShutdown`, `MFLockPlatform`, `MFUnlockPlatform`;
  - `MFCreateAttributes`, `MFCreateMediaType`, `MFCreateSample`;
  - `MFCreateMemoryBuffer`, `MFCreateAlignedMemoryBuffer`, `MFCreateDXGISurfaceBuffer`;
  - `MFCreateDXGIDeviceManager`, `MFTEnumEx`;
  - `MFCreateSourceReaderFromURL`, `MFCreateSourceReaderFromByteStream`, `MFCreateFile` (mfreadwrite's and mfplat's);
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

## The audio decoders

`IMFTransform`s under Windows' names and CLSIDs (`MFAudioTransform.cs`) over the system's decoder
(`Codecs/PlatformAudioDecoder.cs`): AudioToolbox on Apple's systems, GStreamer on Linux, MediaCodec on
Android. The decoders are SharpMediaFoundationInterop's (MIT, by the same author), adapted.

- "Microsoft AAC Audio Decoder MFT" (AAC, raw AAC, ADTS), "MP3 Decoder MFT", "Microsoft FLAC Audio
  Decoder MFT" and "Microsoft Opus Audio Decoder MFT" are listed by `MFTEnumEx` in
  `MFT_CATEGORY_AUDIO_DECODER`. The ALAC decoder is not listed, as on Windows, where `MFTEnumEx` finds
  none for its ALAC subtype, though its source reader decodes ALAC.
- **Output types, in Windows' order:**
  - AAC and MP3: 32-bit float, then 16-bit PCM.
  - FLAC: those and the stream's bits.
  - ALAC: the stream's PCM alone. The reader converts it to float and to other bit depths, with the
    durations rounded, as Windows' resampler does.
- **Samples:** one per packet. The time is the first input's, then counted in frames. AAC's durations
  are rounded; the others' are rounded down. AAC's samples carry no discontinuity; the others pass on
  the input's.
- Nothing of the priming is cut: an MP4's iTunSMPB is not read, as on Windows.
- **Byte streams:** ADTS, MP3 and FLAC input that comes as a byte stream (from Windows' sources of
  them) is cut into frames in the MFT (`Codecs/Packetizer.cs`). FLAC frames are found by their sync code
  and header CRC-8.
- **MP3:**
  - The output types are float, 16-bit and 8-bit PCM, of the stream's channels and then of one.
  - A Xing/Info frame decodes as a frame of silence. It is synthesized here, because GStreamer's
    parser drops the frame.
  - The decoder's delay of 528 frames is left out at the start (where the system's decoder keeps it)
    and is not flushed at the end, as Windows' decoder does.
- **FLAC:** the stream's PCM only, with its channel mask and valid bits. The input's clean point is
  passed on. A frame of packets carries the next packet's time, as Windows' decoder stamps it.
- **Opus:** float only. The pre-skip is left out (less what the system's decoder drops itself).
- A decoded chunk carries the time of the input packet it comes from (by the packets' durations),
  falling back to counting frames from the first input.
- `MFTEnumEx` gives the output types Windows' decoders list: AAC's float and PCM, the others' PCM.

## The source reader

`IMFSourceReaderEx` over a demuxer of the shim's own (`Source/`). Windows' reader was probed for
ground truth (`MFSourceReaderProbe`). Its report on Windows is checked in as
`tests/.../MediaFoundation/SourceReader.windows.txt`. The shim's reader gives the same report line for
line, with one exception: the order in which a read of `MF_SOURCE_READER_ANY_STREAM` interleaves the
streams. The shim gives the earliest sample first; Windows' order follows its pipeline's queues.

- **Files:** each read as Windows' source of it hands its samples out (`Source/Containers.cs`):
  - ISO base media (.mp4, .m4v, .m4a, .mov), plain with moov before or after mdat, and fragmented;
  - WAV: PCM in tenths of a second, each a clean point;
  - MP3: whole frames up to 8 KB a sample, the first a clean point. The duration is the file's size
    over the first frame's bitrate, as Windows estimates it. `MF_MT_USER_DATA` is
    MPEGLAYER3WAVEFORMAT's tail.
  - ADTS AAC and FLAC: the file's bytes as they come, 64 KB a sample, with the decoder finding the
    frames. FLAC has no duration or size (characteristics 6), and its metadata goes in Windows' unnamed
    attribute `3eadb782-…`.
  - Known difference: Windows estimates an ADTS file's duration and bitrate from its start, by a rule
    one file does not show; the shim's are exact.
  - Matroska and WebM (`Source/MatroskaContainer.cs`), as Windows' Matroska source reads them:
    - the blocks in storage order at their timecodes, with DefaultDuration in 100 ns units, rounded
      down;
    - Annex B H.264 and HEVC as from MP4;
    - FLAC's CodecPrivate as a sample of its own before the first frame (the decoder reads its
      configuration in band);
    - native types with the codec ID (Windows' unnamed attribute `9668dde5-…`), compressed, no bitrate,
      and the frame rate of twice the DefaultDuration (Windows reads it as a field's);
    - characteristics 6, MIME `video/x-matroska`; lacing (Xiph, EBML, fixed) is read.

    Known differences: Windows' FLAC bytes per second in Matroska follow a rule one file does not
    show. Windows' Opus decoder hands the sound out 274 frames after where the pre-skip puts it; the
    frame count is the same. Only box headers and sample tables are read at open; samples are read from the file as
  they are asked for. The source is a file (a path or a `file:` URL), the shim's `MFCreateFile` byte
  stream, or any application `IMFByteStream`, read through its vtable.
- **Streams:** the audio tracks first, then the video ones, whatever the order of the traks or their
  ids, as Windows orders them. The first stream of each kind is selected.
- **Native types are those of Windows' MPEG-4 source.**
  - Video: H.264, HEVC, VP9, AV1, MPEG-4 Part 2, H.263, MJPEG. Each codec's native and decoded types are
    Windows' own (probed with ffmpeg-made clips, whose decoding is exact):
    - H.264 is interlace mode 7, with 3-byte start codes in its sequence header.
    - HEVC is progressive: the picture in `MF_MT_MINIMUM_DISPLAY_APERTURE`, 4-byte start codes, the hvc1
      entry (not the stsd) as its sample description. Its decoder reports the apertures on the first frame
      with `CURRENTMEDIATYPECHANGED`.
    - VP9 has no AM format type. Its transfer function, chroma siting and range come from vpcC. Its
      decoded type has the apertures and BT.709 primaries from the start.
    - AV1's sequence header is av1C's configuration OBUs. Its decoded type is its native type as NV12.
  - The edit list is not applied, as Windows' MPEG-4 source applies none: the first presentation time
    of a stream with B-frames is its composition offset. `MFSampleExtension_DecodeTimestamp` is given
    where the decode time is not the presentation time.
    - The frame size is the SPS's cropped picture, not the tkhd's.
    - The frame rate is the timescale over the first sample's duration.
    - `MF_MT_VIDEO_ROTATION` comes from the tkhd matrix.
    - The sequence header uses 3-byte start codes.
    - `MF_MT_MPEG4_SAMPLE_DESCRIPTION` is the stsd box.
    - The bitrate is that of the samples known at open. In a fragmented file that is only the first
      fragment, over the movie's duration.
  - Audio: AAC with `MF_MT_USER_DATA` (HEAACWAVEINFO's tail and the AudioSpecificConfig), MP3, PCM,
    FLAC, ALAC, Opus, AC-3 and E-AC-3.
- **Samples:**
  - H.264 and H.265 come in Annex B: the parameter sets before every sync sample, then each NAL unit
    after a 4-byte start code, with `MFSampleExtension_NALULengthInfo`.
  - Times are in 100 ns units, rounded down. In a fragmented file the tfdt goes to 100 ns units and back
    to ticks, each rounded down, so the audio can be a tick early, as on Windows.
  - Clean points are marked, and a discontinuity on the first sample and after a seek.
  - The end of a stream is a null sample with `MF_SOURCE_READERF_ENDOFSTREAM`.
- **Decoding** (`SetCurrentMediaType`):
  - NV12, YV12, IYUV, I420 and YUY2 come through the shim's decoder MFTs.
  - With `MF_SOURCE_READER_ENABLE_(ADVANCED_)VIDEO_PROCESSING`, RGB32, ARGB32 and RGB24 are also
    available. They are converted from NV12 as Windows' video processor does it: BT.601 (BT.709 from 720
    lines), limited range, one chroma sample per 2x2 pixels, in 8-bit fixed point (298·C ± k·D ± k'·E +
    128) >> 8. RGB32's fourth byte is 0. Without video processing, an RGB type gives
    MF_E_INVALIDMEDIATYPE.
  - The decoded type has the picture's size at first. When the decoder reports the coded size, the next
    sample carries `MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED`, and the new type has the picture in its
    apertures and `MF_MT_VIDEO_NOMINAL_RANGE`.
  - With `MF_SOURCE_READER_D3D_MANAGER`, the frames are textures of the manager's device: NV12, and for
    RGB a B8G8R8X8 / B8G8R8A8 texture. The conversion to RGB still runs on the CPU.
  - Audio is decoded to PCM (8, 16, 24 or 32 bits) or float, of any rate and channel count. Like
    Windows' resampler, the reader converts after the decoder (`AudioConverter.cs`):
    - to mono (L + R) / 2;
    - mono to stereo duplicated, mono to more to the centre;
    - stereo to more to the front left and right; more to fewer by ITU's coefficients;
    - `MF_MT_AUDIO_CHANNEL_MASK` set where the count of channels changes;
    - the rate by a windowed sinc (32 taps, Kaiser) with its delay taken out, so the sound stays where it
      was in time.

    The packets differ from Windows' (its filter's look-ahead is longer), while the types are the same;
    the frames are within two and the level within 0.0005.
  - ALAC's native type is Windows' own: its subtype `616c6163-767a-…`, the bytes per second of its
    configuration, and no WAVEFORMATEX attributes.
  - Known difference: an esds whose descriptor lengths are four bytes long (as Apple's tools write
    them) gives Windows a bitrate of its own making. The shim takes the esds' bitrate.
- **Seeking** (`SetCurrentPosition`) goes to the sync sample at or before the position. In a fragmented
  file it goes to the first sync sample of the fragment, as Windows does.
- **Presentation attributes:** `MF_PD_DURATION` (the mvhd's), `MF_PD_TOTAL_FILE_SIZE`,
  `MF_PD_MIME_TYPE`, `MF_SOURCE_READER_MEDIASOURCE_CHARACTERISTICS` (0x86).
- **Asynchronous mode:** with `MF_SOURCE_READER_ASYNC_CALLBACK`, reads are answered in order on a
  thread of the reader's own through `OnReadSample`, and a flush drops the waiting reads and then calls
  `OnFlush`.
- `IMFSourceReaderEx.AddTransformForStream` is not implemented.

## Tests

They pass on macOS (VideoToolbox) and in the Ubuntu 26.04 VM (GStreamer: libav's and OpenH264's decoders). `MFDecoderTests` makes an H.264 stream whose pictures are all I_PCM macroblocks (raw samples), so a
decoder's output is known to the byte. It finds the H.264 decoder with `MFTEnumEx` and drives it
through its vtables, as an app does:
- **The app's samples:** six frames, one stream change, each frame identical.
- **The coded size:** a 56x40 picture in 64x48 of macroblocks gives an output type of 64x48, the picture in its aperture.
- **The device manager's device:** each frame an NV12 texture of the device behind `IMFDXGIBuffer`, each identical when read back.

`MFSourceReaderTests` compares the reader's report with Windows', including two audio files made with
macOS's afconvert (AAC and ALAC tones). Decoded, they have the same frames as Windows' decoding, with the
sound beginning within two frames and its level within 0.002. It also decodes the I_PCM files to NV12
and to RGB32 (exact to the byte), through the callback, into the device manager's textures, and from a
byte stream. On Windows the same tests drive Windows' own reader, and they pass there too. They also pass
on macOS and in the Ubuntu VM.

## To come

- The browser's decoder (WebCodecs). Android's is in but not yet run on a device or emulator.
- The encoders.
- More of the source reader:
  - more containers (WAV, MP3, ADTS, WebM/Matroska, ASF);
  - reading from a media source (`MFCreateSourceReaderFromMediaSource`) and from network URLs;
  - the application's transforms;
  - the RGB conversion of textures on the GPU (the shim's video processor).
- The media session, and camera capture (`MFEnumDeviceSources`).
- Running Windows' Media Foundation on the shim's `ID3D11VideoDecoder` on a GPU with a decoder (the test VM has none).
