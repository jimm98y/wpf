# winmm's wave functions off Windows

An app that plays or records sound through winmm (`waveOutOpen`, `waveInOpen` and the rest, directly or through a
library such as NAudio's `WaveOutEvent` and `WaveInEvent`) reaches `Winmm/WaveExports.cs` in the Direct3D shim's
assembly, by the P/Invoke rewriter. On Windows the real winmm is used.

The behaviour is Windows', probed in the test VM (`WaveProbe`; the report kept is `Wave.windows.txt`, with no
device's name). `TheWaveFunctions_BehaveAsWindowsDo` compares ours with it line by line on every head; where there is no
microphone, only the waveIn part's first lines are compared.

## The devices

- **waveOut:** the system's output, as device 0 (none where the system has no output). **waveIn:** the microphones of
  `MFEnumDeviceSources` (`Capture/AudioCapture.cs`), in its order. `WAVE_MAPPER` is the first of each.
- The capabilities Windows gives:
  - the mapper: MM_MICROSOFT, product 2, version 5.0, "Microsoft Sound Mapper";
  - a device: product 0xFFFF, version 0, its name (31 characters at most);
  - every `WAVE_FORMAT_` bit (0xFFFFF), two channels;
  - waveOut support 0x2E: PLAYBACKRATE, VOLUME, LRVOLUME and SAMPLEACCURATE.
- A structure smaller than the capabilities' is MMSYSERR_INVALPARAM, with nothing written. A device id out of range
  is MMSYSERR_BADDEVICEID.
- **The mixers:** one of each output, then of each input, with one destination each.
- **The error texts** are Windows' own, for every MMSYSERR_ and WAVERR_ code, cut to the buffer. Another code is
  MMSYSERR_BADERRNUM.

## The formats an open takes

As Windows' takes them, of the mapper and of a device alike:
- **PCM:** 8, 16, 24 or 32 bits, one or two channels, at any rate. The block alignment and byte rate are not checked.
- **IEEE float:** 16, 32 or 64 bits.
- **WAVE_FORMAT_EXTENSIBLE:** PCM or float, of any channels and mask. A cbSize under 22 is the mapper's
  WAVERR_BADFORMAT and a device's MMSYSERR_INVALPARAM.
- **A-law and mu-law:** only through the mapper, where Windows' ACM converts them. A device directly, or
  `WAVE_FORMAT_DIRECT`, refuses them.
- **Anything else**, such as ADPCM or MP3, is WAVERR_BADFORMAT, as are 0 channels, more than two in a WAVEFORMATEX, and
  12-bit PCM.

`WAVE_FORMAT_QUERY` answers and opens nothing. A null format is MMSYSERR_INVALPARAM. The callback flag's
unknown values open with no callback, as Windows' do.

## The callback

A function (`CALLBACK_FUNCTION`), a window's message (`CALLBACK_WINDOW`: MM_WOM_* and MM_WIM_*, of the handle and the
header), a thread's message (`CALLBACK_THREAD`), an event (`CALLBACK_EVENT`, set at each message), or none. The window
and thread messages go through the Win32 shim's PostMessage and PostThreadMessage. An event may be the Win32 shim's
(`CreateEvent`) or the runtime's own, a .NET `EventWaitHandle`'s handle, as NAudio passes.

The messages arrive on these threads, as Windows sends them:
- **The caller's thread:** the open (WOM_OPEN, WIM_OPEN), the close (WOM_CLOSE, WIM_CLOSE), and waveInStop's return of
  the buffer it had begun.
- **Another thread:** each buffer done, and those a reset returns.

## waveOut

- **Headers:**
  - Prepare sets WHDR_PREPARED and keeps the app's other flags. A header smaller than WAVEHDR is MMSYSERR_INVALPARAM.
  - A write of an unprepared header is WAVERR_UNPREPARED (checked before the handle). A header already queued is
    WAVERR_STILLPLAYING.
  - Written, it is WHDR_PREPARED | WHDR_INQUEUE. Done, it is WHDR_PREPARED | WHDR_DONE, with dwUser kept.
  - Unprepare or close while queued is WAVERR_STILLPLAYING.
- **Playing:** the samples go to the stack's output (`MediaFoundation/Session/AudioOutput.cs`): AudioQueue on macOS and
  iOS, PulseAudio on Linux, AAudio on Android, Web Audio in the browser. They are converted to its 48 kHz stereo float,
  with the handle's left and right volume and its playback rate. More than two channels are mixed down to two.
- **Done:** a header is done when the device has taken its last sample from the ring.
- **Loops:** headers from one of `WHDR_BEGINLOOP` to one of `WHDR_ENDLOOP` play `dwLoops` times. `waveOutBreakLoop`
  ends the loop after the pass playing.
- **Pause** holds the output, and a header written meanwhile waits. **Restart** goes on; either again is no error.
- **Reset** returns every queued header at once, done, and tells them later. It sets the position back to 0 and
  unpauses.
- **The position** is the bytes the device has taken since the open or the last reset: in TIME_SAMPLES if asked,
  otherwise TIME_BYTES, which also stands for any other format asked. An MMTIME smaller than 12 bytes is
  MMSYSERR_ERROR.
- **Volume:** each handle has its own, and the devices share one; it reads back as set.
- **Pitch** is MMSYSERR_NOTSUPPORTED. The **playback rate** is applied.
- **Messages:** `waveOutGetID` gives the id opened (WAVE_MAPPER of the mapper). `waveOutMessage` answers:
  - DRVM_MAPPER_PREFERRED_GET: device 0, no flags;
  - DRV_QUERYDEVICEINTERFACE(SIZE): an MMDEVAPI path;
  - anything else: MMSYSERR_NOTSUPPORTED.
- **A closed handle** is MMSYSERR_INVALHANDLE.

## waveIn

- **Headers:** an added header is queued with dwBytesRecorded 0. Headers added before the start wait.
- **Start** opens the microphone (`AudioCapture`) and fills the headers in turn. Its blocks are converted to the
  format: the rate (linear interpolation), the channels (mono of the average), and the sample type.
- **Done:** a header is done, of its length, when full. Samples that come with no header queued are dropped.
- **Stop** keeps the microphone open. It returns the header begun, of its whole frames, on the caller's thread.
  Headers added while stopped wait for the next start.
- **Reset** stops, returns every header (those not begun of 0 bytes) and tells them later. It sets the position back
  to 0.
- **The position** is the bytes recorded into the headers.

## Where it differs

- The device names are the system's, not Windows' endpoint names.
- Done comes when the device takes the samples, at most a buffer of the device's before they are heard.
- Pitch is unsupported, as on Windows.
