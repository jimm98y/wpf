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
  - `MFCreateSourceReaderFromURL`, `MFCreateSourceReaderFromByteStream`, `MFCreateSourceReaderFromMediaSource`, `MFCreateFile` (mfreadwrite's and mfplat's);
  - `MFCreateMFByteStreamOnStream` (over a COM IStream, its writability from STATSTG's `grfMode`, as Windows'), `MFCreateTempFile`;
  - `MFCreateSourceResolver`, `MFCreateEventQueue`, `MFCreateMediaEvent`, `MFCreateStreamDescriptor`, `MFCreatePresentationDescriptor`;
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
  - **WebCodecs in the browser** (`Codecs/WebCodecs.cs`, `Browser/browser-codecs.js`, the `wpfBrowserCodecs`
    module that `main.js` registers). It needs the multithreaded runtime: the shim runs on a worker and the
    decoders on the page's thread. Each JS call is a round trip to the page, and the shim waits for what the
    decoders make by polling.
    - Video: `VideoDecoder` for H.264, HEVC, VP9 and AV1.
      - The codec string is made from the stream: H.264's and HEVC's first SPS, VP9's first frame, AV1's
        sequence header.
      - Annex B goes in as it is. Parameter sets that come alone are held for the next chunk. Key frames
        are found in the chunks, and nothing is decoded before the first.
      - Each frame is copied out as NV12: of NV12 and I420 directly, of other formats through RGBX.
    - Audio: `AudioDecoder` for AAC, MP3, FLAC and Opus, with each channel read as f32-planar and written as
      the stream's PCM.
    - Times go over in microseconds and come back in 100 ns as they went in.
    - A drain waits for the decoder's flush and for every copy.
    - A page without the module (an older `main.js`) has no decoders.
    - Where the input type has no frame size, the H.264 decoder is configured with the coded size of the
      stream's SPS. Chrome's decoder fails a cropped stream configured without one.
    - Encoders: `VideoEncoder` for H.264 and HEVC (Annex B out, the codec string from the profile and the
      picture's size), with NV12 frames in. `AudioEncoder` for AAC, with s16 in. Behind them are the same
      H.264 and AAC encoder MFTs and the same sink writer as on the desktop.
      - Windows' AAC encoder has a packet once 2048 frames have come after it. The browser's encoder
        answers through the page, so the MFT waits up to 3 s for it (200 ms elsewhere).
      - At the drain, the MFT hands out the packets of the input and of the encoder's 2112 frames of
        priming. It drops what an encoder pads after those (WebCodecs' does) and Windows' does not have.
    - The source reader's report of every test file in headless Chrome (`tests/WasmMedia`, its lines checked by
      `TheBrowsersReader_DoesWithEachFileWhatWindowsReaderDoes`) is Windows', under the desktop's
      tolerances.
    - `tests/WasmMedia` also runs the desktop's Media Foundation tests in the page: it compiles them in and
      runs each by reflection (`index.html?filter=<name>`). It prints `MF-TEST <name>: passed|skipped|failed`
      for each and `MF-TESTS` with the totals. Tests of textures, devices, HTTP and the probes are skipped.
- WebAssembly is 32-bit, so the shim and the tests find `MFT_OUTPUT_DATA_BUFFER`'s fields at offsets of the
  pointer's size, never at the 64-bit 8 and 16.
- Calls through native function pointers (the vtables' `calli`) need an interp-to-native trampoline of
  their signature in the browser. Mono's build makes one only for the signatures of P/Invokes, so
  `Browser/CalliSignatures.cs` declares one P/Invoke of each signature, never called. `CalliSignatureTests`
  reads every `calli` of the built browser assembly and checks that the file holds each signature;
  `MF_WRITE_CALLI=1` writes it anew.
  - WebAssembly's `call_indirect` checks a signature exactly, so each vtable slot has a stub of its own
    signature: `ICodecAPI`'s slots, for example, through `CodecApiSlots`.
  - The interpreter finds no `UnmanagedCallersOnly` callback of a nested class, so the callback classes
    are at the namespace's level.
- **Apple Lossless in managed code** (`Codecs/AlacDecoder.cs`): a port of the algorithm of Apple's open-source ALAC
  decoder (Apache 2.0), used where the system has no ALAC decoder of its own (Chrome's WebCodecs has none). Mono
  and stereo, 16-bit and 24-bit tones decode to the bit what AudioToolbox and GStreamer decode
  (`AlacDecoderTests`).
- In the browser, the GUID that `IMFMediaType::GetRepresentation` and `FreeRepresentation` take by value
  arrives by reference, as WebAssembly's C ABI passes it; the interpreter makes no wrapper for a struct
  passed by value.
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
  - Over HTTP(S) (`Source/HttpByteSource.cs`), as Windows' network byte stream reads them: a GET of
    `bytes=0-` first, then ranges, in cached blocks of 256 KB (a server without ranges is read through
    once into a temporary file). The reader's report is that of the same file on disk, as Windows'
    is. A 404 gives NS_E_FILE_NOT_FOUND (0xC00D001A).
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

### Transforms of the application's

`IMFSourceReaderEx::AddTransformForStream` takes an effect MFT (or its activation object) as Windows'
reader does (`Source/ReaderEffects.cs`). This was probed with `SpyEffect`, a pass-through effect that logs
every call made of it (`MFReaderTransformProbe`, the report in `ReaderTransform.windows.txt`); the shim's
reader makes the same calls in the same order.
- **Where the effect goes:** after the decoder (or the file's own PCM), before the reader's own conversion
  to the current type. The current type stays as it was.
- **Its input:**
  - Audio: the decoder's type is tried three times. If it is refused, a float type of the same rate and
    channels is tested, then set, with the reader's converter before the effect.
  - Video: the effect's first input type is asked for. If its subtype is the decoder's, the decoder's
    type is set. If not, the effect's first type is set: the decoder's output changes to it where the
    decoder gives it (YUY2), else the video processor goes before the effect (RGB32), after the effect's
    types are asked for again.
  - A compressed stream is offered as it is: three tries, no decoding added.
- **Its output:** the effect's types are asked for until one has the current subtype. If none does, its
  first is set, with a converter (the video processor) after it.
- **Refusals:** an effect that takes nothing it can be given, or whose output nothing converts back
  (an encoder), gets MF_E_INVALIDMEDIATYPE. It is let go as Windows lets one go: flush, end of stream,
  end streaming, its types cleared.
- **Streaming:**
  - Begun at the first read, drained at the end, flushed by a seek.
  - For audio from an effect that doesn't provide its samples, the reader asks the output size before
    each output.
  - `RemoveAllTransformsForStream` ends each effect. It also drops the reader's own converters and
    keeps the decoder. An audio stream's current type becomes its WAVEFORMATEX's type, as
    `MFInitMediaTypeFromWaveFormatEx` makes it.
- **A new current type with effects:** the effect's output is set as it is where it has that subtype,
  else its types are searched.
- **`GetTransformForStream`:** the decoder, then each effect (`MFT_CATEGORY_AUDIO_EFFECT` /
  `MFT_CATEGORY_VIDEO_EFFECT`), with the reader's converters around it as stand-ins
  (`MFT_CATEGORY_AUDIO_EFFECT` / `MFT_CATEGORY_VIDEO_PROCESSOR`). The PCM-to-float converter is listed
  even with no effect, as Windows lists it.
- **Not compared:** the attributes of decoded video frames, which come from each system's decoder.

## Media sources

**The objects they are described and talk by** (`MFSourceObjects.cs`), for the shim's own sources and the
application's:
- `MFCreateEventQueue` (`IMFMediaEventQueue`): events in order, waited for or handed to an
  `IMFAsyncCallback` as they come, one BeginGetEvent at a time, MF_E_SHUTDOWN after Shutdown;
- `MFCreateMediaEvent`;
- `MFCreateStreamDescriptor`, with its `IMFMediaTypeHandler`: the stream's types, a current type among them;
- `MFCreatePresentationDescriptor`: the streams, selected or not; a clone of its own selection.

These behave as Media Foundation documents (`MFSourceObjectsProbe`). Windows' own could not be probed from
the test process: its `QueueEventParamVar` and `QueueEventParamUnk`, and an unmatched `IsMediaTypeSupported`,
fault when called so on ARM64.

**The source resolver and the sources of files** (`Source/MFMediaSource.cs`). Windows' were probed
(`MFMediaSourceProbe`, the report in `MediaSource.windows.txt`); the shim's give the same report.
- `MFCreateSourceResolver`: a media source of a URL (a file, http(s)) or of a byte stream; the byte stream
  itself where only that is asked for (`MF_RESOLUTION_BYTESTREAM`). Failures are Windows' (0x80070002,
  MF_E_UNSUPPORTED_BYTESTREAM_TYPE, MF_E_UNSUPPORTED_SCHEME), the object type left as it was.
- **The presentation descriptor:** a copy each time it is asked for.
  - MPEG-4: the audio streams first, numbered 1, 2, … in that order. Matroska: the file's order and
    track numbers. FLAC's one stream is 0.
  - Each stream has the reader's native type, current; ADTS's and WAV's have none current. ALAC has
    its type again with the `WAVE_FORMAT_ALAC` subtype.
  - The stream descriptors' attributes: an AAC file's iTunSMPB encoder delay and padding (0 of other
    audio with iTunSMPB, and of WAV and FLAC), `MF_SD_LANGUAGE` of mdhd (a Macintosh code of English is
    "en", "und" none), and an unnamed attribute of Matroska's.
  - The presentation's attributes: those the reader gives, the MP3 file's time of last writing,
    and an unnamed attribute of a fragmented MPEG-4 file's.
- **Start:**
  - On the source: MENewStream (or MEUpdatedStream) of each selected stream, then MESourceStarted.
    It is MESourceSeeked when the source has been started already and Start gives a position.
  - On each stream: MEStreamStarted (or MEStreamSeeked). Requests held while paused are answered then.
  - The events carry the position, except MP3's source events and FLAC's events.
- **RequestSample:**
  - MEMediaSample with the sample; its token goes in `MFSampleExtension_Token`, which is set (empty) on
    every sample except Matroska's.
  - MPEG-4's H.264 and HEVC come in a buffer of the parameter sets, then a buffer for each NAL unit's
    start code and one for its rest. The reader gives them as one buffer, as Windows' does.
  - MEEndOfStream answers the request after the last sample. MP3 and FLAC send it with the last sample
    and refuse requests beyond it. MEEndOfPresentation comes once every selected stream has ended.
  - When a source restarts at the end, ADTS, MP3 and FLAC send MEEndOfStream again.
- **Seeking:**
  - MPEG-4 goes to the sync sample decoded at or before the position (by decode time, which matters
    with B-frames).
  - MP3 goes by bytes at the bitrate: the frame at or before that byte, its time taken from its offset.
  - ADTS goes to the frame at or after the position, timed from the position.
  - Matroska goes to the video's key frame at or before the position; the other streams resume from
    their first sample at or after it, and FLAC's header comes again first.
  - FLAC's source starts only at the beginning (MF_E_INVALIDREQUEST).
- **Stop and Shutdown:** a request while stopped is MF_E_MEDIA_SOURCE_WRONGSTATE (MF_E_END_OF_STREAM
  for MP3 and Matroska), and a deselected stream's is MF_E_SHUTDOWN (Matroska: MF_E_END_OF_STREAM). After
  Shutdown everything gives MF_E_SHUTDOWN; a second Shutdown succeeds, except FLAC's and Matroska's.

**The reader of a media source** (`MFCreateSourceReaderFromMediaSource`): the source's streams in its
presentation descriptor's order. Windows' reader keeps that order, so the reader of a URL has it too; a
Matroska file's video comes first. The reader shuts the source down when it is released, unless
`MF_SOURCE_READER_DISCONNECT_MEDIASOURCE_ON_SHUTDOWN` is set. `GetServiceForStream` of
MF_SOURCE_READER_MEDIASOURCE gives the source.
- A source of the shim's own is read through its file.
- An application's source is driven as Windows' reader drives one (`Source/ForeignSource.cs`):
  - The handler's current type of each stream is its native type.
  - Start (VT_EMPTY) comes at the first read, with the source's events up to MESourceStarted. A seek is
    a Start at the position; a change of selection, a Start where the source is.
  - Each sample is a RequestSample and its MEMediaSample. MEEndOfStream is the end.
  - Stream ticks and format changes are not passed on yet.
- The reader's report of every file is Windows' report the three ways: of the URL, of the resolver's
  source, and of that source driven as an application's (`TheReader_DoesWithEachFileWhatWindowsReaderDoes`).

## The encoders

`MFTEnumEx` lists Windows' "H264 Encoder MFT" (`6ca50344-…`) and "Microsoft AAC Audio Encoder MFT"
(`93af0c51-…`) wherever the system encodes the codec (`MFEncoders.cs`, over `Codecs/PlatformEncoders.cs`).
Windows' were probed (`MFEncoderProbe`'s Details and Dumps, the report in `Encoder.windows.txt`). The
shim's give the same report, except for what each system's encoder makes itself (below). Both are
synchronous with one stream each way and write the application's output samples.

- **H.264:**
  - The output type comes first. It needs a size of even dimensions (MF_E_ATTRIBUTENOTFOUND without
    one, even to a test), a frame rate and an interlace mode (MF_E_ATTRIBUTENOTFOUND), and a bitrate
    (E_FAIL). `MF_MT_MPEG2_PROFILE` is 66, 77 or 100; Main is the default.
  - Then the input: IYUV, YV12, NV12 or YUY2 of the output's size and rate, as the four available input
    types give them.
  - The current output type carries `MF_MT_MPEG_SEQUENCE_HEADER`. The output buffer is 1.875 bytes a
    pixel, at least 32 KB.
  - Each output is an access unit led by an access unit delimiter, with the parameter sets before each
    key frame.
  - Attributes: `MFSampleExtension_DecodeTimestamp` and the picture type, `MFSampleExtension_CleanPoint`
    of 1 on key frames and 0 otherwise, and two more of Windows'.
  - As Windows' encoder, it holds five frames before its first output (four of Baseline). Of profiles
    with B pictures, each frame is timed one frame after its decode time.
  - `ICodecAPI` has Windows' properties and their defaults: rate control, mean bitrate, quality, GOP
    size, B pictures, low latency, QP and others.
- **AAC:**
  - Input: 16-bit PCM of 1, 2, 6 or 8 channels at 11025 to 96000 Hz. The 64 available input types list
    float as well, as Windows' do, though float is refused.
  - Output: one of Windows' 279 types, raw or ADTS, by bytes a second and profile level (LC, HE, HE v2).
    It has to match the input's channels and rate; either may be set first.
  - Like Windows', the encoder fills the application's own type objects in with the rest of the
    attributes (`MF_MT_USER_DATA`'s HEAACWAVEINFO tail with the AudioSpecificConfig among them) and
    keeps them.
  - Packets come once 2048 frames have followed them, or at the drain. Each is a clean point, timed
    from the first input.
  - No `ICodecAPI` property, and no attribute store, as Windows'.
- **What differs is each system's encoder's own:**
  - the bytes, and so the parameter sets;
  - Windows' B pictures: the shim's encoders make I and P pictures only, at the same times;
  - SEI;
  - each frame's QP attribute;
  - AAC's priming: AudioToolbox's makes up to two packets more at the drain.
- The test VM's H.264 decoder MFT decodes nothing when made on its own, so the round trip of encoding
  and decoding (`TheH264Encoder_EncodesWhatTheDecoderDecodesBack`) is checked off Windows.

## The sink writer

`MFCreateSinkWriterFromURL` gives an `IMFSinkWriterEx` writing MP4 (`Sink/`). It writes to a path or to a
byte stream: the shim's own, or the application's (`Sink/ByteStreamWriter.cs`). It was probed by writing
H.264 of NV12 and AAC of PCM.

- **Byte streams:** as Windows' sink writer treats them (probed with `SpyByteStream`, the report in
  `SinkByteStream.windows.txt`).
  - Without a URL, the container is `MF_TRANSCODE_CONTAINERTYPE`'s; with neither, the answer is
    E_INVALIDARG. A stream that can't be written and sought fails `BeginWriting` with E_INVALIDARG, and
    later calls give MF_E_INVALIDREQUEST.
  - Nothing is written before there are samples. Then the header comes in one write; writes are
    BeginWrite/EndWrite, each followed by GetCurrentPosition. At the end the sink goes back to mdat's
    start to rewrite its header, writes moov, and closes the stream.
  - Windows writes a sample at a time and the shim a chunk at a time. Windows writes ASF to a `.wmv`
    URL, which the shim does not.

- **Streams:**
  - A stream's target type is H.264, HEVC or AAC.
  - Its input is that type itself (written as it comes), or what the system's encoder is given.
  - Video converts from NV12, I420, IYUV, YV12, YUY2, RGB32 and ARGB32; audio converts float and other
    bit depths to 16-bit PCM.
  - The encoders are the system's (`Codecs/PlatformEncoders.cs`, SharpMediaFoundationInterop's,
    adapted): VideoToolbox and AudioToolbox, GStreamer, MediaCodec. H.264 is Baseline where the type
    names no profile, as Windows' encoder makes it.
- **The file is laid out as Windows' MPEG-4 sink lays it out** (`Sink/Mp4Muxer.cs`; the box tree is
  `SinkWriter.windows.txt`):
  - `ftyp` mp42 (mp41, isom); a `uuid` box of Media Foundation's version; a 64-bit `mdat`; then
    `moov`.
  - The movie's timescale is the audio's rate; video is at its frame rate × 1000.
  - Chunks of 0.32 s, written in the order of their start, video first on ties.
  - Durations are the last sample's time and duration in 100 ns units, rounded down, then rounded
    down again in the track's units.
  - The parameter sets go to avcC / hvcC and out of the samples. The entry is named "AVC Coding".
  - The esds' descriptors have four-byte lengths (the SLConfig one byte). The audio has a ctts of
    zeros. `udta` holds an empty iTunes `meta` and `Xtra`.
- Reading such a file, the reader puts the parameter sets after a leading access unit delimiter, as
  Windows' reader does.

What differs is the encoders' own output: the sample sizes, the AudioSpecificConfig (GStreamer's
signals SBR), and AudioToolbox's priming, which makes two AAC packets more than Windows' encoder.
`MFSinkWriterProbe.TheSinkWriter_WritesTheFileWindowsWrites` compares the box tree with Windows' (all
but the encoders' bytes) on macOS and Linux and reads the file back. Windows' own reader decodes a file
the shim wrote to the same frames.

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

- Android's decoders are in but not yet run on a device or emulator.
- The sink writer through the encoder MFTs (it uses the system's encoders directly).
- More of the source reader:
  - more containers (ASF);
  - stream ticks and format changes of an application's media source;
  - the RGB conversion of textures on the GPU (the shim's video processor).
- The media session, and camera capture (`MFEnumDeviceSources`).
- Running Windows' Media Foundation on the shim's `ID3D11VideoDecoder` on a GPU with a decoder (the test VM has none).
