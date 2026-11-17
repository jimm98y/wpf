// Speech synthesis as SAPI does it, on the system's voices (Voices.cs): prompts queued and spoken one after another
// on the synthesizer's own thread, paused and resumed (the speaking process stopped and continued), cancelled one by
// one or all; output to the audio device, to nothing, to a WAVE file or stream, or to a raw audio stream; and its
// events (SpeakStarted, SpeakCompleted, StateChanged) raised on the synchronization context it was created on, as
// System.Speech posts them.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Speech.AudioFormat;
using System.Text;
using System.Threading;
using System.Xml;

namespace System.Speech.Synthesis
{
    public sealed partial class SpeechSynthesizer : IDisposable
    {
        private readonly object _gate = new object();
        private readonly List<Prompt> _queue = new List<Prompt>();
        private readonly SynchronizationContext _context;
        private Thread _worker;
        private Prompt _current;
        private Process _speaking;
        private SynthesizerState _state = SynthesizerState.Ready;
        private bool _paused, _disposed;
        private int _rate, _volume = 100;
        private VoiceInfo _voice;
        private Output _output = Output.Device;

        public SpeechSynthesizer()
        {
            // AsyncOperationManager's: the creator's context, else one that runs callbacks on the thread pool
            _context = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        public event EventHandler<BookmarkReachedEventArgs> BookmarkReached;
        public event EventHandler<PhonemeReachedEventArgs> PhonemeReached;
        public event EventHandler<SpeakCompletedEventArgs> SpeakCompleted;
        public event EventHandler<SpeakProgressEventArgs> SpeakProgress;
        public event EventHandler<SpeakStartedEventArgs> SpeakStarted;
        public event EventHandler<StateChangedEventArgs> StateChanged;
        public event EventHandler<VisemeReachedEventArgs> VisemeReached;
        public event EventHandler<VoiceChangeEventArgs> VoiceChange;

        private void Check()
        {
            if (_disposed) throw new ObjectDisposedException("SpeechSynthesizer", "Object has been disposed.");
        }

        public int Rate
        {
            get { Check(); return _rate; }
            set
            {
                Check();
                if (value is < -10 or > 10) throw new ArgumentOutOfRangeException(nameof(value), "Value must be in the range -10 to 10.");
                _rate = value;
            }
        }

        public int Volume
        {
            get { Check(); return _volume; }
            set
            {
                Check();
                if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "Value must be in the range 0 to 100.");
                _volume = value;
            }
        }

        public SynthesizerState State { get { Check(); return _state; } }

        public VoiceInfo Voice { get { Check(); return _voice ?? Voices.Default; } }

        // ---- voices ------------------------------------------------------------------------------------------------

        public ReadOnlyCollection<InstalledVoice> GetInstalledVoices()
        {
            Check();
            return new ReadOnlyCollection<InstalledVoice>(Voices.All.Select(v => new InstalledVoice(v)).ToList());
        }

        public ReadOnlyCollection<InstalledVoice> GetInstalledVoices(CultureInfo culture)
        {
            Check();
            ArgumentNullException.ThrowIfNull(culture);
            if (culture.Equals(CultureInfo.InvariantCulture)) throw new ArgumentException("Invariant culture is not supported.", nameof(culture));
            return new ReadOnlyCollection<InstalledVoice>(Voices.All.Where(v => v.Culture.Equals(culture)).Select(v => new InstalledVoice(v)).ToList());
        }

        public void SelectVoice(string name)
        {
            Check();
            ArgumentException.ThrowIfNullOrEmpty(name);
            _voice = Voices.All.FirstOrDefault(v => v.Name == name)
                ?? throw new ArgumentException("Cannot set voice. No matching voice is installed or the voice was disabled.", nameof(name));
        }

        public void SelectVoiceByHints(VoiceGender gender) => SelectVoiceByHints(gender, VoiceAge.NotSet, 1, CultureInfo.CurrentUICulture);
        public void SelectVoiceByHints(VoiceGender gender, VoiceAge age) => SelectVoiceByHints(gender, age, 1, CultureInfo.CurrentUICulture);
        public void SelectVoiceByHints(VoiceGender gender, VoiceAge age, int voiceAlternate) => SelectVoiceByHints(gender, age, voiceAlternate, CultureInfo.CurrentUICulture);

        // the hints narrow the choice where the voices say (the system's do not give gender or age); the culture's
        // voices first, the alternate'th of them
        public void SelectVoiceByHints(VoiceGender gender, VoiceAge age, int voiceAlternate, CultureInfo culture)
        {
            Check();
            if (voiceAlternate < 0) throw new ArgumentOutOfRangeException(nameof(voiceAlternate), "Value must be greater than or equal to zero.");
            var candidates = Voices.All.Where(v => culture == null || v.Culture.Equals(culture)).ToList();
            if (candidates.Count == 0) candidates = Voices.All.Where(v => culture == null || v.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName).ToList();
            if (candidates.Count == 0) return;
            _voice = candidates[Math.Max(0, voiceAlternate - 1) % candidates.Count];
        }

        public void AddLexicon(Uri uri, string mediaType) => throw new PlatformNotSupportedException("Lexicons are SAPI's; the system's voices do not take them.");

        public void RemoveLexicon(Uri uri) => throw new PlatformNotSupportedException("Lexicons are SAPI's; the system's voices do not take them.");

        // ---- output -----------------------------------------------------------------------------------------------

        private sealed class Output : IDisposable
        {
            public static readonly Output Device = new Output(), Null = new Output { Silent = true };
            public bool Silent;
            public Stream Stream;           // where the sound goes, a WAVE file's or raw
            public bool Wave, OwnsStream;
            public SpeechAudioFormat Format = SpeechAudioFormat.Default;
            private long _dataStart = -1, _dataLength;

            public void Append(ReadOnlySpan<byte> pcm)
            {
                if (Wave && _dataStart < 0) { WriteHeader(); _dataStart = Stream.Position; }
                Stream.Write(pcm);
                _dataLength += pcm.Length;
                if (Wave && Stream.CanSeek) PatchHeader();
                Stream.Flush();
            }

            private void WriteHeader()
            {
                int block = Format.Channels * Format.Bits / 8;
                using var w = new BinaryWriter(Stream, Encoding.ASCII, leaveOpen: true);
                w.Write("RIFF"u8); w.Write(36); w.Write("WAVE"u8);
                w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)Format.Channels); w.Write(Format.Rate);
                w.Write(Format.Rate * block); w.Write((short)block); w.Write((short)Format.Bits);
                w.Write("data"u8); w.Write(0);
            }

            private void PatchHeader()
            {
                long end = Stream.Position;
                Stream.Position = _dataStart - 40;
                Stream.Write(BitConverter.GetBytes((int)(36 + _dataLength)));
                Stream.Position = _dataStart - 4;
                Stream.Write(BitConverter.GetBytes((int)_dataLength));
                Stream.Position = end;
            }

            public void Dispose()
            {
                if (Wave && _dataStart < 0 && Stream != null) { WriteHeader(); _dataStart = Stream.Position; }
                if (OwnsStream) Stream?.Dispose();
            }
        }

        private void SetOutput(Output output)
        {
            Check();
            lock (_gate)
            {
                Output old = _output;
                _output = output;
                if (old != Output.Device && old != Output.Null) old.Dispose();
            }
        }

        private static SpeechAudioFormat FormatOf(SpeechAudioFormatInfo info) =>
            info == null ? SpeechAudioFormat.Default
            : info.EncodingFormat != EncodingFormat.Pcm ? throw new ArgumentException("Only PCM audio is supported by the system's voices.", nameof(info))
            : new SpeechAudioFormat(info.SamplesPerSecond, info.BitsPerSample, info.ChannelCount);

        public void SetOutputToDefaultAudioDevice() => SetOutput(Output.Device);

        public void SetOutputToNull() => SetOutput(Output.Null);

        public void SetOutputToWaveFile(string path) => SetOutputToWaveFile(path, null);

        public void SetOutputToWaveFile(string path, SpeechAudioFormatInfo formatInfo)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            SetOutput(new Output { Stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite), Wave = true, OwnsStream = true, Format = FormatOf(formatInfo) });
        }

        public void SetOutputToWaveStream(Stream audioDestination)
        {
            ArgumentNullException.ThrowIfNull(audioDestination);
            SetOutput(new Output { Stream = audioDestination, Wave = true });
        }

        public void SetOutputToAudioStream(Stream audioDestination, SpeechAudioFormatInfo formatInfo)
        {
            ArgumentNullException.ThrowIfNull(audioDestination);
            ArgumentNullException.ThrowIfNull(formatInfo);
            SetOutput(new Output { Stream = audioDestination, Format = FormatOf(formatInfo) });
        }

        // ---- speaking ---------------------------------------------------------------------------------------------

        public void Speak(string textToSpeak) => Speak(new Prompt(textToSpeak ?? throw new ArgumentNullException(nameof(textToSpeak))));

        public void Speak(PromptBuilder promptBuilder) => Speak(new Prompt(promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder))));

        public void Speak(Prompt prompt)
        {
            ArgumentNullException.ThrowIfNull(prompt);
            SpeakAsync(prompt);
            prompt.Done.Wait();
            if (prompt.Error != null) Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(prompt.Error).Throw();
        }

        public void SpeakSsml(string textToSpeak) => Speak(new Prompt(textToSpeak ?? throw new ArgumentNullException(nameof(textToSpeak)), SynthesisTextFormat.Ssml));

        public Prompt SpeakAsync(string textToSpeak)
        {
            var prompt = new Prompt(textToSpeak ?? throw new ArgumentNullException(nameof(textToSpeak)));
            SpeakAsync(prompt);
            return prompt;
        }

        public Prompt SpeakAsync(PromptBuilder promptBuilder)
        {
            var prompt = new Prompt(promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder)));
            SpeakAsync(prompt);
            return prompt;
        }

        public Prompt SpeakSsmlAsync(string textToSpeak)
        {
            var prompt = new Prompt(textToSpeak ?? throw new ArgumentNullException(nameof(textToSpeak)), SynthesisTextFormat.Ssml);
            SpeakAsync(prompt);
            return prompt;
        }

        public void SpeakAsync(Prompt prompt)
        {
            Check();
            ArgumentNullException.ThrowIfNull(prompt);
            if (prompt.Synthesizer != null && !prompt.IsCompleted) throw new ArgumentException("Prompt is already queued or being spoken.", nameof(prompt));
            prompt.Reset(this);
            lock (_gate)
            {
                _queue.Add(prompt);
                if (_worker == null)
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "SpeechSynthesizer" };
                    _worker.Start();
                }
                Monitor.PulseAll(_gate);
            }
        }

        public Prompt GetCurrentlySpokenPrompt()
        {
            Check();
            lock (_gate) return _current;
        }

        public void SpeakAsyncCancel(Prompt prompt)
        {
            Check();
            ArgumentNullException.ThrowIfNull(prompt);
            lock (_gate)
            {
                if (_queue.Remove(prompt)) { Complete(prompt, null, cancelled: true); return; }
                if (_current == prompt) { prompt.Cancelled = true; Stop(_speaking); }
            }
        }

        public void SpeakAsyncCancelAll()
        {
            Check();
            lock (_gate)
            {
                foreach (Prompt p in _queue.ToList()) { _queue.Remove(p); Complete(p, null, cancelled: true); }
                if (_current != null) { _current.Cancelled = true; Stop(_speaking); }
            }
        }

        public void Pause()
        {
            Check();
            lock (_gate)
            {
                if (_paused) return;
                _paused = true;
                if (_speaking != null) Signal(_speaking, stop: true);
                SetState(SynthesizerState.Paused);
            }
        }

        public void Resume()
        {
            Check();
            lock (_gate)
            {
                if (!_paused) return;
                _paused = false;
                if (_speaking != null) Signal(_speaking, stop: false);
                SetState(_current != null ? SynthesizerState.Speaking : SynthesizerState.Ready);
                Monitor.PulseAll(_gate);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        private static void Signal(Process p, bool stop)
        {
            // SIGSTOP and SIGCONT: 17 and 19 on macOS, 19 and 18 on Linux
            int signal = OperatingSystem.IsMacOS() ? (stop ? 17 : 19) : (stop ? 19 : 18);
            try { kill(p.Id, signal); } catch (InvalidOperationException) { }
        }

        private static void Stop(Process p)
        {
            if (p == null) return;
            try { Signal(p, stop: false); p.Kill(); } catch (InvalidOperationException) { }
        }

        private void SetState(SynthesizerState state)
        {
            if (_state == state) return;
            SynthesizerState previous = _state;
            _state = state;
            Raise(() => StateChanged?.Invoke(this, new StateChangedEventArgs(state, previous)));
        }

        private void Raise(Action raise) => _context.Post(_ => raise(), null);

        private void Complete(Prompt prompt, Exception error, bool cancelled)
        {
            prompt.Finish(error);
            Raise(() => SpeakCompleted?.Invoke(this, new SpeakCompletedEventArgs(prompt, error, cancelled)));
        }

        private void Work()
        {
            while (true)
            {
                Prompt prompt;
                lock (_gate)
                {
                    while (!_disposed && (_queue.Count == 0 || _paused))
                    {
                        if (_queue.Count == 0 && !_paused) SetState(SynthesizerState.Ready);
                        Monitor.Wait(_gate);
                    }
                    if (_disposed) return;
                    prompt = _queue[0];
                    _queue.RemoveAt(0);
                    _current = prompt;
                    SetState(SynthesizerState.Speaking);
                }
                Raise(() => SpeakStarted?.Invoke(this, new SpeakStartedEventArgs(prompt)));
                Exception error = null;
                try { Utter(prompt); }
                catch (Exception e) { error = e; }
                lock (_gate)
                {
                    _current = null;
                    _speaking = null;
                    Complete(prompt, prompt.Cancelled ? null : error, prompt.Cancelled);
                }
            }
        }

        private void Utter(Prompt prompt)
        {
            Output output;
            VoiceInfo voice;
            int rate, volume;
            lock (_gate) { output = _output; voice = _voice; rate = _rate; volume = _volume; }
            if (Voices.All.Count == 0) throw Voices.NoVoice();
            if (output.Silent) return;
            string text = prompt.SpokenText(Voices.Say != null);
            if (text.Trim().Length == 0) return;
            string wave = output.Stream != null ? Path.Combine(Path.GetTempPath(), "speech-" + Guid.NewGuid().ToString("N") + ".wav") : null;
            try
            {
                var (start, input) = Voices.Speaker(text, voice, rate, volume, wave, output.Format);
                Process p;
                lock (_gate)
                {
                    if (prompt.Cancelled) return;
                    p = Process.Start(start)!;
                    _speaking = p;
                    if (_paused) Signal(p, stop: true);
                }
                var errors = p.StandardError.ReadToEndAsync();
                p.StandardInput.Write(input);
                p.StandardInput.Close();
                p.WaitForExit();
                if (prompt.Cancelled) return;
                if (p.ExitCode != 0) throw new InvalidOperationException($"Speech synthesis failed: {errors.Result.Trim()}");
                if (wave != null && File.Exists(wave))
                    lock (_gate) output.Append(PcmOf(File.ReadAllBytes(wave)));
            }
            finally
            {
                if (wave != null) try { File.Delete(wave); } catch (IOException) { }
            }
        }

        // a WAVE file's samples: its data chunk
        private static ReadOnlySpan<byte> PcmOf(byte[] wave)
        {
            int at = 12;
            while (at + 8 <= wave.Length)
            {
                int size = BitConverter.ToInt32(wave, at + 4);
                if (wave[at] == 'd' && wave[at + 1] == 'a' && wave[at + 2] == 't' && wave[at + 3] == 'a')
                    return wave.AsSpan(at + 8, Math.Min(size, wave.Length - at - 8));
                at += 8 + size + (size & 1);
            }
            return ReadOnlySpan<byte>.Empty;
        }

        public void Dispose()
        {
            if (_disposed) return;
            lock (_gate)
            {
                foreach (Prompt p in _queue) p.Finish(null);
                _queue.Clear();
                if (_current != null) { _current.Cancelled = true; Stop(_speaking); }
                _disposed = true;
                Monitor.PulseAll(_gate);
                if (_output != Output.Device && _output != Output.Null) _output.Dispose();
            }
        }
    }

    // ---- prompts -----------------------------------------------------------------------------------------------------

    public partial class Prompt
    {
        private readonly PromptBuilder _builder;
        internal SpeechSynthesizer Synthesizer;
        internal volatile bool Cancelled;
        internal Exception Error;
        internal ManualResetEventSlim Done = new ManualResetEventSlim(true);
        private volatile bool _completed;

        public Prompt(PromptBuilder promptBuilder)
        {
            ArgumentNullException.ThrowIfNull(promptBuilder);
            _builder = promptBuilder;
        }

        public Prompt(string textToSpeak) : this(textToSpeak, SynthesisTextFormat.Text) { }

        public Prompt(string textToSpeak, SynthesisTextFormat media)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            _builder = new PromptBuilder();
            if (media == SynthesisTextFormat.Ssml) _builder.AppendSsmlMarkupDocument(textToSpeak);
            else _builder.AppendText(textToSpeak);
        }

        public bool IsCompleted => _completed;

        internal string SpokenText(bool embeddedCommands) => _builder.Spoken(embeddedCommands);

        internal void Reset(SpeechSynthesizer synthesizer)
        {
            Synthesizer = synthesizer;
            Cancelled = false;
            Error = null;
            _completed = false;
            Done = new ManualResetEventSlim(false);
        }

        internal void Finish(Exception error)
        {
            Error = error;
            _completed = true;
            Done.Set();
        }
    }

    public partial class FilePrompt : Prompt
    {
        public FilePrompt(string path, SynthesisMediaType media) : this(new Uri(Path.GetFullPath(path)), media) { }

        public FilePrompt(Uri promptFile, SynthesisMediaType media) : base(FromFile(promptFile, media)) { }

        private static PromptBuilder FromFile(Uri file, SynthesisMediaType media)
        {
            ArgumentNullException.ThrowIfNull(file);
            var builder = new PromptBuilder();
            switch (media)
            {
                case SynthesisMediaType.Ssml: builder.AppendSsml(file); break;
                case SynthesisMediaType.Text: builder.AppendText(File.ReadAllText(file.LocalPath)); break;
                default: builder.AppendAudio(file); break;
            }
            return builder;
        }
    }

    public partial class PromptStyle
    {
        public PromptStyle() { }
        public PromptStyle(PromptEmphasis emphasis) => Emphasis = emphasis;
        public PromptStyle(PromptRate rate) => Rate = rate;
        public PromptStyle(PromptVolume volume) => Volume = volume;
        public PromptEmphasis Emphasis { get; set; }
        public PromptRate Rate { get; set; }
        public PromptVolume Volume { get; set; }
    }

    /// <summary>A prompt's content, kept as its SSML (ToXml) and as what the voice reads, a break a pause in it.</summary>
    [Serializable]
    public partial class PromptBuilder
    {
        // the spoken text with pauses marked (\u0001ms\u0001), turned into the voice's own pause where it has one
        private readonly StringBuilder _spoken = new StringBuilder();
        private readonly StringBuilder _ssml = new StringBuilder();
        private readonly Stack<string> _open = new Stack<string>();
        private CultureInfo _culture;

        public PromptBuilder() : this(CultureInfo.CurrentUICulture) { }

        public PromptBuilder(CultureInfo culture)
        {
            ArgumentNullException.ThrowIfNull(culture);
            _culture = culture;
        }

        public CultureInfo Culture
        {
            get => _culture;
            set => _culture = value ?? throw new ArgumentNullException(nameof(value));
        }

        public bool IsEmpty => _ssml.Length == 0;

        internal string Spoken(bool embeddedCommands)
        {
            string s = _spoken.ToString();
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\u0001') { sb.Append(s[i]); continue; }
                int end = s.IndexOf('\u0001', i + 1);
                if (end < 0) break;
                string ms = s[(i + 1)..end];
                sb.Append(embeddedCommands ? $" [[slnc {ms}]] " : " ... ");
                i = end;
            }
            return sb.ToString();
        }

        private static string Escape(string s) => SecurityElement.Escape(s) ?? "";

        private void Text(string spoken, string ssml)
        {
            if (_spoken.Length > 0 && !char.IsWhiteSpace(_spoken[^1])) _spoken.Append(' ');
            _spoken.Append(spoken);
            _ssml.Append(ssml);
        }

        private void Pause(int ms, string ssml)
        {
            _spoken.Append('\u0001').Append(ms.ToString(CultureInfo.InvariantCulture)).Append('\u0001');
            _ssml.Append(ssml);
        }

        public void AppendText(string textToSpeak)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            Text(textToSpeak, Escape(textToSpeak));
        }

        public void AppendText(string textToSpeak, PromptEmphasis emphasis)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            Text(textToSpeak, $"<emphasis level=\"{emphasis.ToString().ToLowerInvariant()}\">{Escape(textToSpeak)}</emphasis>");
        }

        public void AppendText(string textToSpeak, PromptRate rate)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            Text(textToSpeak, $"<prosody rate=\"{RateName(rate)}\">{Escape(textToSpeak)}</prosody>");
        }

        public void AppendText(string textToSpeak, PromptVolume volume)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            Text(textToSpeak, $"<prosody volume=\"{VolumeName(volume)}\">{Escape(textToSpeak)}</prosody>");
        }

        private static string RateName(PromptRate rate) => rate switch
        {
            PromptRate.ExtraFast => "x-fast", PromptRate.Fast => "fast", PromptRate.Medium => "medium", PromptRate.Slow => "slow", PromptRate.ExtraSlow => "x-slow", _ => "default",
        };

        private static string VolumeName(PromptVolume volume) => volume switch
        {
            PromptVolume.Silent => "silent", PromptVolume.ExtraSoft => "x-soft", PromptVolume.Soft => "soft", PromptVolume.Medium => "medium",
            PromptVolume.Loud => "loud", PromptVolume.ExtraLoud => "x-loud", _ => "default",
        };

        public void AppendTextWithAlias(string textToSpeak, string substitute)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            ArgumentNullException.ThrowIfNull(substitute);
            Text(substitute, $"<sub alias=\"{Escape(substitute)}\">{Escape(textToSpeak)}</sub>");
        }

        public void AppendTextWithHint(string textToSpeak, SayAs sayAs)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            Text(textToSpeak, $"<say-as interpret-as=\"{sayAs.ToString().ToLowerInvariant()}\">{Escape(textToSpeak)}</say-as>");
        }

        public void AppendTextWithHint(string textToSpeak, string sayAs)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            ArgumentException.ThrowIfNullOrEmpty(sayAs);
            Text(textToSpeak, $"<say-as interpret-as=\"{Escape(sayAs)}\">{Escape(textToSpeak)}</say-as>");
        }

        public void AppendTextWithPronunciation(string textToSpeak, string pronunciation)
        {
            ArgumentNullException.ThrowIfNull(textToSpeak);
            ArgumentException.ThrowIfNullOrEmpty(pronunciation);
            Text(textToSpeak, $"<phoneme ph=\"{Escape(pronunciation)}\">{Escape(textToSpeak)}</phoneme>");
        }

        public void AppendBookmark(string bookmarkName)
        {
            ArgumentNullException.ThrowIfNull(bookmarkName);
            _ssml.Append($"<mark name=\"{Escape(bookmarkName)}\"/>");
        }

        public void AppendBreak() => Pause(400, "<break/>");

        public void AppendBreak(PromptBreak strength)
        {
            int ms = strength switch
            {
                PromptBreak.None => 0, PromptBreak.ExtraSmall => 100, PromptBreak.Small => 200, PromptBreak.Medium => 400, PromptBreak.Large => 700, _ => 1000,
            };
            string name = strength switch
            {
                PromptBreak.None => "none", PromptBreak.ExtraSmall => "x-weak", PromptBreak.Small => "weak", PromptBreak.Medium => "medium", PromptBreak.Large => "strong", _ => "x-strong",
            };
            Pause(ms, $"<break strength=\"{name}\"/>");
        }

        public void AppendBreak(TimeSpan duration)
        {
            if (duration.Ticks < 0) throw new ArgumentOutOfRangeException(nameof(duration));
            int ms = (int)Math.Min(int.MaxValue, duration.TotalMilliseconds);
            Pause(ms, $"<break time=\"{ms.ToString(CultureInfo.InvariantCulture)}ms\"/>");
        }

        public void AppendAudio(string path) => AppendAudio(new Uri(path, UriKind.RelativeOrAbsolute));

        public void AppendAudio(Uri audioFile) => AppendAudio(audioFile, "");

        // the system's voices read text only: an audio file is its alternate text
        public void AppendAudio(Uri audioFile, string alternateText)
        {
            ArgumentNullException.ThrowIfNull(audioFile);
            ArgumentNullException.ThrowIfNull(alternateText);
            _spoken.Append(alternateText.Length > 0 ? " " + alternateText : "");
            _ssml.Append($"<audio src=\"{Escape(audioFile.ToString())}\">{Escape(alternateText)}</audio>");
        }

        public void AppendPromptBuilder(PromptBuilder promptBuilder)
        {
            ArgumentNullException.ThrowIfNull(promptBuilder);
            if (_spoken.Length > 0) _spoken.Append(' ');
            _spoken.Append(promptBuilder._spoken);
            _ssml.Append(promptBuilder._ssml);
        }

        public void AppendSsml(string path) => AppendSsml(new Uri(Path.GetFullPath(path)));

        public void AppendSsml(Uri ssmlFile)
        {
            ArgumentNullException.ThrowIfNull(ssmlFile);
            using var reader = XmlReader.Create(ssmlFile.IsFile ? ssmlFile.LocalPath : ssmlFile.ToString());
            AppendSsml(reader);
        }

        public void AppendSsml(XmlReader ssmlFile)
        {
            ArgumentNullException.ThrowIfNull(ssmlFile);
            var doc = new XmlDocument();
            doc.Load(ssmlFile);
            AppendSsmlNodes(doc.DocumentElement);
        }

        public void AppendSsmlMarkup(string ssmlMarkup)
        {
            ArgumentNullException.ThrowIfNull(ssmlMarkup);
            var doc = new XmlDocument();
            doc.LoadXml("<speak xmlns=\"http://www.w3.org/2001/10/synthesis\">" + ssmlMarkup + "</speak>");
            AppendSsmlNodes(doc.DocumentElement);
        }

        internal void AppendSsmlMarkupDocument(string ssml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(ssml);
            AppendSsmlNodes(doc.DocumentElement);
        }

        private void AppendSsmlNodes(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
            {
                switch (child)
                {
                    case XmlText or XmlCDataSection or XmlWhitespace or XmlSignificantWhitespace:
                        if (child.Value is { } v && v.Trim().Length > 0) Text(v.Trim(), Escape(v));
                        break;
                    case XmlElement e when e.LocalName == "break":
                        if (e.GetAttribute("time") is { Length: > 0 } time)
                        {
                            double ms = time.EndsWith("ms", StringComparison.Ordinal) ? double.Parse(time[..^2], CultureInfo.InvariantCulture)
                                : time.EndsWith('s') ? double.Parse(time[..^1], CultureInfo.InvariantCulture) * 1000 : 400;
                            AppendBreak(TimeSpan.FromMilliseconds(ms));
                        }
                        else AppendBreak();
                        break;
                    case XmlElement e when e.LocalName == "sub" && e.HasAttribute("alias"):
                        AppendTextWithAlias(e.InnerText, e.GetAttribute("alias"));
                        break;
                    case XmlElement e when e.LocalName == "audio":
                        AppendAudio(new Uri(e.GetAttribute("src"), UriKind.RelativeOrAbsolute), e.InnerText);
                        break;
                    case XmlElement e when e.LocalName == "mark":
                        AppendBookmark(e.GetAttribute("name"));
                        break;
                    case XmlElement e:
                        AppendSsmlNodes(e);
                        break;
                }
            }
        }

        public void ClearContent()
        {
            _spoken.Clear();
            _ssml.Clear();
            _open.Clear();
        }

        private void Start(string element, string ssml)
        {
            _open.Push(element);
            _ssml.Append(ssml);
        }

        private void End(string element, string name)
        {
            if (_open.Count == 0 || _open.Peek() != element) throw new InvalidOperationException($"Cannot end {name}: none is open.");
            _open.Pop();
            _ssml.Append("</").Append(element).Append('>');
        }

        public void StartParagraph() => Start("p", "<p>");
        public void StartParagraph(CultureInfo culture) => Start("p", culture == null ? "<p>" : $"<p xml:lang=\"{culture.Name}\">");
        public void EndParagraph() { End("p", "paragraph"); Pause(700, ""); }
        public void StartSentence() => Start("s", "<s>");
        public void StartSentence(CultureInfo culture) => Start("s", culture == null ? "<s>" : $"<s xml:lang=\"{culture.Name}\">");
        public void EndSentence() { End("s", "sentence"); Pause(400, ""); }

        public void StartStyle(PromptStyle style)
        {
            ArgumentNullException.ThrowIfNull(style);
            Start("prosody", $"<prosody rate=\"{RateName(style.Rate)}\" volume=\"{VolumeName(style.Volume)}\">");
        }

        public void EndStyle() => End("prosody", "style");

        public void StartVoice(CultureInfo culture) => Start("voice", $"<voice xml:lang=\"{culture?.Name}\">");
        public void StartVoice(VoiceGender gender) => Start("voice", $"<voice gender=\"{gender.ToString().ToLowerInvariant()}\">");
        public void StartVoice(VoiceGender gender, VoiceAge age) => Start("voice", $"<voice gender=\"{gender.ToString().ToLowerInvariant()}\" age=\"{(int)age}\">");
        public void StartVoice(VoiceGender gender, VoiceAge age, int voiceAlternate) => Start("voice", $"<voice gender=\"{gender.ToString().ToLowerInvariant()}\" age=\"{(int)age}\" variant=\"{voiceAlternate}\">");
        public void StartVoice(VoiceInfo voice) => Start("voice", $"<voice name=\"{Escape(voice?.Name ?? "")}\">");
        public void StartVoice(string name) => Start("voice", $"<voice name=\"{Escape(name ?? "")}\">");
        public void EndVoice() => End("voice", "voice");

        public string ToXml() =>
            $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{_culture.Name}\">{_ssml}{string.Concat(_open.Select(e => "</" + e + ">"))}</speak>";
    }

    // ---- voices --------------------------------------------------------------------------------------------------

    public partial class InstalledVoice
    {
        internal InstalledVoice(VoiceInfo voice) => VoiceInfo = voice;
        public bool Enabled { get; set; } = true;
        public VoiceInfo VoiceInfo { get; }
        public override bool Equals(object obj) => obj is InstalledVoice v && v.VoiceInfo.Equals(VoiceInfo);
        public override int GetHashCode() => VoiceInfo.GetHashCode();
    }

    [Serializable]
    public partial class VoiceInfo
    {
        internal VoiceInfo(string name, string id, CultureInfo culture, VoiceGender gender, VoiceAge age, string description)
        {
            Name = name;
            Id = id;
            Culture = culture;
            Gender = gender;
            Age = age;
            Description = description;
        }

        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public IDictionary<string, string> AdditionalInfo { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        public VoiceAge Age { get; }
        public CultureInfo Culture { get; }
        public string Description { get; }
        public VoiceGender Gender { get; }
        public string Id { get; }
        public string Name { get; }
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public ReadOnlyCollection<SpeechAudioFormatInfo> SupportedAudioFormats { get; } = new ReadOnlyCollection<SpeechAudioFormatInfo>(new[] { new SpeechAudioFormatInfo(22050, AudioBitsPerSample.Sixteen, AudioChannel.Mono) });
        public override bool Equals(object obj) => obj is VoiceInfo v && v.Name == Name && v.Id == Id;
        public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
    }

    // ---- events --------------------------------------------------------------------------------------------------

    public abstract partial class PromptEventArgs : AsyncCompletedEventArgs
    {
        private readonly Prompt _prompt;
        internal PromptEventArgs(Prompt prompt) : base(null, false, prompt) => _prompt = prompt;
        internal PromptEventArgs(Prompt prompt, Exception error, bool cancelled) : base(error, cancelled, prompt) => _prompt = prompt;
        public Prompt Prompt => _prompt;
    }

    public partial class SpeakStartedEventArgs : PromptEventArgs
    {
        internal SpeakStartedEventArgs(Prompt prompt) : base(prompt) { }
    }

    public partial class SpeakCompletedEventArgs : PromptEventArgs
    {
        internal SpeakCompletedEventArgs(Prompt prompt, Exception error, bool cancelled) : base(prompt, error, cancelled) { }
    }

    public partial class SpeakProgressEventArgs : PromptEventArgs
    {
        internal SpeakProgressEventArgs(Prompt prompt, TimeSpan position, int characterPosition, int characterCount, string text) : base(prompt)
        {
            AudioPosition = position;
            CharacterPosition = characterPosition;
            CharacterCount = characterCount;
            Text = text;
        }

        public TimeSpan AudioPosition { get; }
        public int CharacterCount { get; }
        public int CharacterPosition { get; }
        public string Text { get; }
    }

    public partial class StateChangedEventArgs : EventArgs
    {
        internal StateChangedEventArgs(SynthesizerState state, SynthesizerState previousState)
        {
            State = state;
            PreviousState = previousState;
        }

        public SynthesizerState PreviousState { get; }
        public SynthesizerState State { get; }
    }

    public partial class VoiceChangeEventArgs : PromptEventArgs
    {
        internal VoiceChangeEventArgs(Prompt prompt, VoiceInfo voice) : base(prompt) => Voice = voice;
        public VoiceInfo Voice { get; }
    }

    public partial class BookmarkReachedEventArgs : PromptEventArgs
    {
        internal BookmarkReachedEventArgs(Prompt prompt, string bookmark, TimeSpan position) : base(prompt) { Bookmark = bookmark; AudioPosition = position; }
        public TimeSpan AudioPosition { get; }
        public string Bookmark { get; }
    }

    public partial class PhonemeReachedEventArgs : PromptEventArgs
    {
        internal PhonemeReachedEventArgs(Prompt prompt) : base(prompt) { }
        public TimeSpan AudioPosition { get; }
        public TimeSpan Duration { get; }
        public SynthesizerEmphasis Emphasis { get; }
        public string NextPhoneme { get; }
        public string Phoneme { get; }
    }

    public partial class VisemeReachedEventArgs : PromptEventArgs
    {
        internal VisemeReachedEventArgs(Prompt prompt) : base(prompt) { }
        public TimeSpan AudioPosition { get; }
        public TimeSpan Duration { get; }
        public SynthesizerEmphasis Emphasis { get; }
        public int NextViseme { get; }
        public int Viseme { get; }
    }
}

namespace System.Speech.AudioFormat
{
    [Serializable]
    public partial class SpeechAudioFormatInfo
    {
        private readonly byte[] _formatSpecificData;

        public SpeechAudioFormatInfo(int samplesPerSecond, AudioBitsPerSample bitsPerSample, AudioChannel channel)
            : this(EncodingFormat.Pcm, samplesPerSecond, (int)bitsPerSample, (int)channel, samplesPerSecond * (int)bitsPerSample / 8 * (int)channel, (int)bitsPerSample / 8 * (int)channel, null) { }

        public SpeechAudioFormatInfo(EncodingFormat encodingFormat, int samplesPerSecond, int bitsPerSample, int channelCount, int averageBytesPerSecond, int blockAlign, byte[] formatSpecificData)
        {
            if (encodingFormat == 0) throw new ArgumentException("Cannot create a SpeechAudioFormatInfo with an unknown encoding format.", nameof(encodingFormat));
            if (samplesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerSecond));
            if (bitsPerSample <= 0) throw new ArgumentOutOfRangeException(nameof(bitsPerSample));
            if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
            if (averageBytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(averageBytesPerSecond));
            if (blockAlign <= 0) throw new ArgumentOutOfRangeException(nameof(blockAlign));
            EncodingFormat = encodingFormat;
            SamplesPerSecond = samplesPerSecond;
            BitsPerSample = (short)bitsPerSample;
            ChannelCount = (short)channelCount;
            AverageBytesPerSecond = averageBytesPerSecond;
            BlockAlign = (short)blockAlign;
            _formatSpecificData = formatSpecificData == null ? Array.Empty<byte>() : (byte[])formatSpecificData.Clone();
        }

        [EditorBrowsable(EditorBrowsableState.Advanced)] public int AverageBytesPerSecond { get; }
        [EditorBrowsable(EditorBrowsableState.Advanced)] public int BitsPerSample { get; }
        [EditorBrowsable(EditorBrowsableState.Advanced)] public int BlockAlign { get; }
        public int ChannelCount { get; }
        public EncodingFormat EncodingFormat { get; }
        public int SamplesPerSecond { get; }

        public byte[] FormatSpecificData() => (byte[])_formatSpecificData.Clone();

        public override bool Equals(object obj) => obj is SpeechAudioFormatInfo f && f.EncodingFormat == EncodingFormat && f.SamplesPerSecond == SamplesPerSecond
            && f.BitsPerSample == BitsPerSample && f.ChannelCount == ChannelCount && f.AverageBytesPerSecond == AverageBytesPerSecond && f.BlockAlign == BlockAlign
            && f._formatSpecificData.AsSpan().SequenceEqual(_formatSpecificData);

        public override int GetHashCode() => HashCode.Combine(EncodingFormat, SamplesPerSecond, BitsPerSample, ChannelCount);
    }
}
