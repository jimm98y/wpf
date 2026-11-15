// System.Media's SoundPlayer and SystemSounds, as the package's Windows build has them, on every OS: a .wav loaded
// from a file, a URL, a stream or a resource, and played once, looping or to its end -- through winmm's PlaySound on
// Windows (what the package calls), AppKit's NSSound on macOS, and the desktop's sound player on Linux (pw-play,
// paplay or aplay, whichever is there). A system sound is the one the user set: MessageBeep's on Windows, the alert
// sound on macOS (NSBeep), the freedesktop sound theme's on Linux (canberra-gtk-play), else the terminal bell.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace System.Media
{
	[Serializable]
	[ToolboxItem (false)]
	public class SoundPlayer : Component, ISerializable
	{
		private const int DefaultLoadTimeout = 10000;
		private static readonly object s_loadCompleted = new object ();
		private static readonly object s_soundLocationChanged = new object ();
		private static readonly object s_streamChanged = new object ();

		private string _soundLocation = string.Empty;
		private Stream _stream;
		private byte [] _wave;
		private bool _isLoadCompleted;
		private int _loadTimeout = DefaultLoadTimeout;
		private Task _loading;
		private SoundOutput _playing;

		public SoundPlayer ()
		{
		}

		public SoundPlayer (string soundLocation) => SetLocation (soundLocation ?? string.Empty);

		public SoundPlayer (Stream stream) => _stream = stream;

		[EditorBrowsable (EditorBrowsableState.Never)]
		[Obsolete ("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
		protected SoundPlayer (SerializationInfo serializationInfo, StreamingContext context)
		{
			foreach (SerializationEntry entry in serializationInfo) {
				switch (entry.Name) {
				case "SoundLocation": SetLocation ((string) entry.Value); break;
				case "Stream": _stream = entry.Value as Stream; break;
				case "LoadTimeout": _loadTimeout = (int) entry.Value; break;
				}
			}
		}

		/// <summary>Whether the sound has been loaded.</summary>
		public bool IsLoadCompleted => _isLoadCompleted;

		/// <summary>How long a synchronous load may take, in milliseconds.</summary>
		public int LoadTimeout {
			get => _loadTimeout;
			set {
				if (value < 0)
					throw new ArgumentOutOfRangeException (nameof (value), value, "The value must be zero or greater.");
				_loadTimeout = value;
			}
		}

		/// <summary>The file path or URL of the .wav.</summary>
		public string SoundLocation {
			get => _soundLocation;
			set {
				value ??= string.Empty;
				if (_soundLocation == value)
					return;
				SetLocation (value);
				OnSoundLocationChanged (EventArgs.Empty);
			}
		}

		/// <summary>The stream the .wav is read from.</summary>
		public Stream Stream {
			get => _soundLocation.Length != 0 ? null : _stream;
			set {
				if (ReferenceEquals (_stream, value))
					return;
				_stream = value;
				_soundLocation = string.Empty;
				_wave = null;
				_isLoadCompleted = false;
				OnStreamChanged (EventArgs.Empty);
			}
		}

		public object Tag { get; set; }

		private void SetLocation (string location)
		{
			_soundLocation = location;
			_stream = null;
			_wave = null;
			_isLoadCompleted = false;
		}

		/// <summary>Loads the sound, waiting up to <see cref="LoadTimeout"/>.</summary>
		public void Load ()
		{
			if (_isLoadCompleted)
				return;
			Task load = _loading ??= Task.Run (LoadCore);
			try {
				if (!load.Wait (_loadTimeout))
					throw new TimeoutException ("The request to load the sound timed out.");
			} catch (AggregateException e) when (e.InnerExceptions.Count == 1) {
				_loading = null;
				throw e.InnerException;
			}
		}

		/// <summary>Loads the sound on a thread of its own; LoadCompleted says when it is done.</summary>
		public void LoadAsync ()
		{
			if (_isLoadCompleted) {
				OnLoadCompleted (new AsyncCompletedEventArgs (null, false, null));
				return;
			}
			SynchronizationContext context = SynchronizationContext.Current;
			Task load = _loading ??= Task.Run (LoadCore);
			load.ContinueWith (t => {
				var args = new AsyncCompletedEventArgs (t.Exception?.InnerException, false, null);
				if (t.IsFaulted)
					_loading = null;
				if (context != null)
					context.Post (_ => OnLoadCompleted (args), null);
				else
					OnLoadCompleted (args);
			}, TaskScheduler.Default);
		}

		private void LoadCore ()
		{
			byte [] data;
			if (_soundLocation.Length != 0) {
				if (Uri.TryCreate (_soundLocation, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) {
					using var http = new HttpClient ();
					data = http.GetByteArrayAsync (uri).GetAwaiter ().GetResult ();
				} else {
					string path = uri != null && uri.IsFile ? uri.LocalPath : _soundLocation;
					if (!File.Exists (path))
						throw new FileNotFoundException ("Please be sure a sound file exists at the specified location.", _soundLocation);
					data = File.ReadAllBytes (path);
				}
			} else if (_stream != null) {
				using var copy = new MemoryStream ();
				if (_stream.CanSeek)
					_stream.Position = 0;
				_stream.CopyTo (copy);
				data = copy.ToArray ();
			} else {
				data = null;
			}
			if (data != null)
				ValidateWave (data);
			_wave = data;
			_isLoadCompleted = true;
		}

		// a RIFF/WAVE with a PCM (or other) format chunk and a data chunk, as winmm takes it
		private void ValidateWave (byte [] data)
		{
			bool riff = data.Length >= 12 && data [0] == 'R' && data [1] == 'I' && data [2] == 'F' && data [3] == 'F'
				&& data [8] == 'W' && data [9] == 'A' && data [10] == 'V' && data [11] == 'E';
			if (!riff)
				throw new InvalidOperationException (_soundLocation.Length != 0
					? $"The file located at {_soundLocation} is not a valid wave file."
					: "The wave header is corrupt.");
		}

		/// <summary>Plays the sound on another thread, loading it first if need be.</summary>
		public void Play () => PlayCore (loop: false, wait: false);

		/// <summary>Plays the sound over and over until <see cref="Stop"/>.</summary>
		public void PlayLooping () => PlayCore (loop: true, wait: false);

		/// <summary>Plays the sound to its end on this thread.</summary>
		public void PlaySync () => PlayCore (loop: false, wait: true);

		private void PlayCore (bool loop, bool wait)
		{
			if (_soundLocation.Length == 0 && _stream == null) {
				// nothing to play: the system's default sound, as PlaySound with no sound does
				SystemSounds.Beep.Play ();
				return;
			}
			Load ();
			Stop ();
			if (_wave == null)
				return;
			SoundOutput output = SoundOutput.Create (_wave, loop);
			_playing = output;
			if (wait)
				output.Wait ();
		}

		/// <summary>Stops the sound playing.</summary>
		public void Stop ()
		{
			SoundOutput playing = Interlocked.Exchange (ref _playing, null);
			playing?.Stop ();
		}

		public event AsyncCompletedEventHandler LoadCompleted {
			add { Events.AddHandler (s_loadCompleted, value); }
			remove { Events.RemoveHandler (s_loadCompleted, value); }
		}

		public event EventHandler SoundLocationChanged {
			add { Events.AddHandler (s_soundLocationChanged, value); }
			remove { Events.RemoveHandler (s_soundLocationChanged, value); }
		}

		public event EventHandler StreamChanged {
			add { Events.AddHandler (s_streamChanged, value); }
			remove { Events.RemoveHandler (s_streamChanged, value); }
		}

		protected virtual void OnLoadCompleted (AsyncCompletedEventArgs e) => (Events [s_loadCompleted] as AsyncCompletedEventHandler)?.Invoke (this, e);

		protected virtual void OnSoundLocationChanged (EventArgs e) => (Events [s_soundLocationChanged] as EventHandler)?.Invoke (this, e);

		protected virtual void OnStreamChanged (EventArgs e) => (Events [s_streamChanged] as EventHandler)?.Invoke (this, e);

		void ISerializable.GetObjectData (SerializationInfo info, StreamingContext context)
		{
			if (_soundLocation.Length != 0)
				info.AddValue ("SoundLocation", _soundLocation);
			if (_stream != null)
				info.AddValue ("Stream", _stream);
			info.AddValue ("LoadTimeout", _loadTimeout);
		}
	}

	public class SystemSound
	{
		// MessageBeep's types
		private readonly uint _type;

		internal SystemSound (uint type) => _type = type;

		/// <summary>Plays the sound the user set for this event.</summary>
		public void Play () => SoundOutput.PlaySystem (_type);
	}

	public static class SystemSounds
	{
		private static SystemSound s_asterisk, s_beep, s_exclamation, s_hand, s_question;

		public static SystemSound Asterisk => s_asterisk ??= new SystemSound (0x40);      // MB_ICONASTERISK
		public static SystemSound Beep => s_beep ??= new SystemSound (0);                 // MB_OK
		public static SystemSound Exclamation => s_exclamation ??= new SystemSound (0x30); // MB_ICONEXCLAMATION
		public static SystemSound Hand => s_hand ??= new SystemSound (0x10);              // MB_ICONHAND
		public static SystemSound Question => s_question ??= new SystemSound (0x20);      // MB_ICONQUESTION
	}

	/// <summary>A sound playing, on the OS's audio.</summary>
	internal abstract class SoundOutput
	{
		public abstract void Wait ();
		public abstract void Stop ();

		/// <summary>How long a .wav plays: its data chunk over its format's bytes a second.</summary>
		internal static TimeSpan Duration (byte [] wave)
		{
			int byteRate = 0, dataSize = 0;
			for (int at = 12; at + 8 <= wave.Length;) {
				string id = System.Text.Encoding.ASCII.GetString (wave, at, 4);
				int size = BitConverter.ToInt32 (wave, at + 4);
				if (id == "fmt " && at + 16 <= wave.Length)
					byteRate = BitConverter.ToInt32 (wave, at + 16);
				else if (id == "data")
					dataSize = Math.Min (size, wave.Length - at - 8);
				if (size < 0)
					break;
				at += 8 + size + (size & 1);
			}
			return byteRate > 0 ? TimeSpan.FromSeconds ((double) dataSize / byteRate) : TimeSpan.Zero;
		}

		public static SoundOutput Create (byte [] wave, bool loop)
		{
			if (OperatingSystem.IsWindows ())
				return new WinmmOutput (wave, loop);
			if (OperatingSystem.IsMacOS ())
				return new AppKitOutput (wave, loop);
			if (OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid ())
				return new PlayerProcessOutput (wave, loop);
			throw new PlatformNotSupportedException ("This platform has no sound output for SoundPlayer.");
		}

		public static void PlaySystem (uint type)
		{
			if (OperatingSystem.IsWindows ()) {
				MessageBeep (type);
				return;
			}
			if (OperatingSystem.IsMacOS ()) {
				// macOS has one alert sound, the user's: what every system sound is there
				AppKitOutput.Beep ();
				return;
			}
			if (OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid ()) {
				string id = type switch { 0x40 => "dialog-information", 0x30 => "dialog-warning", 0x10 => "dialog-error", 0x20 => "dialog-question", _ => "bell" };
				if (PlayerProcessOutput.TryRun ("canberra-gtk-play", "-i " + id, wait: false) == null)
					Console.Out.Write ('\a');
			}
		}

		[DllImport ("user32")]
		private static extern bool MessageBeep (uint type);
	}

	/// <summary>winmm's PlaySound from memory, the buffer pinned while it plays.</summary>
	internal sealed class WinmmOutput : SoundOutput
	{
		private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4, SND_LOOP = 0x8, SND_SYNC = 0x0;
		private static readonly object s_lock = new object ();
		private static GCHandle s_pinned;
		private readonly byte [] _wave;
		private readonly bool _loop;
		private bool _waited;

		public WinmmOutput (byte [] wave, bool loop)
		{
			_wave = wave;
			_loop = loop;
			lock (s_lock) {
				Release ();
				s_pinned = GCHandle.Alloc (wave, GCHandleType.Pinned);
				PlaySound (s_pinned.AddrOfPinnedObject (), IntPtr.Zero, SND_MEMORY | SND_NODEFAULT | SND_ASYNC | (loop ? SND_LOOP : 0));
			}
		}

		public override void Wait ()
		{
			if (_waited || _loop)
				return;
			_waited = true;
			lock (s_lock) {
				// to its end: played again synchronously from the start, as PlaySync plays (SND_SYNC)
				PlaySound (s_pinned.AddrOfPinnedObject (), IntPtr.Zero, SND_MEMORY | SND_NODEFAULT | SND_SYNC);
			}
		}

		public override void Stop ()
		{
			lock (s_lock) {
				PlaySound (IntPtr.Zero, IntPtr.Zero, 0);
				Release ();
			}
		}

		private static void Release ()
		{
			if (s_pinned.IsAllocated)
				s_pinned.Free ();
		}

		[DllImport ("winmm", EntryPoint = "PlaySoundW")]
		private static extern bool PlaySound (IntPtr sound, IntPtr module, uint flags);
	}

	/// <summary>AppKit's NSSound, made from the .wav's bytes.</summary>
	internal sealed class AppKitOutput : SoundOutput
	{
		private IntPtr _sound;
		private readonly TimeSpan _duration;
		private readonly Stopwatch _started = Stopwatch.StartNew ();

		public AppKitOutput (byte [] wave, bool loop)
		{
			_duration = SoundOutput.Duration (wave);
			EnsureAppKit ();
			IntPtr data;
			unsafe {
				fixed (byte* p = wave)
					data = SendPtrUlong (objc_getClass ("NSData"), Sel ("dataWithBytes:length:"), (IntPtr) p, (ulong) wave.Length);
			}
			_sound = SendPtr (Send (objc_getClass ("NSSound"), Sel ("alloc")), Sel ("initWithData:"), data);
			if (_sound == IntPtr.Zero)
				throw new InvalidOperationException ("The wave header is corrupt.");
			SendVoidBool (_sound, Sel ("setLoops:"), loop);
			SendBool (_sound, Sel ("play"));
		}

		// to the sound's end: its length, as the header gives it (isPlaying is kept by the run loop, which a thread
		// playing synchronously need not have)
		public override void Wait ()
		{
			while (_sound != IntPtr.Zero && _started.Elapsed < _duration)
				Thread.Sleep (10);
		}

		public override void Stop ()
		{
			IntPtr sound = Interlocked.Exchange (ref _sound, IntPtr.Zero);
			if (sound == IntPtr.Zero)
				return;
			SendBool (sound, Sel ("stop"));
			Send (sound, Sel ("release"));
		}

		public static void Beep ()
		{
			EnsureAppKit ();
			NSBeep ();
		}

		private static void EnsureAppKit () => dlopen ("/System/Library/Frameworks/AppKit.framework/AppKit", 2);

		private static IntPtr Sel (string name) => sel_registerName (name);

		private const string ObjC = "/usr/lib/libobjc.A.dylib";

		[DllImport ("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen (string path, int mode);
		[DllImport ("/System/Library/Frameworks/AppKit.framework/AppKit")] private static extern void NSBeep ();
		[DllImport (ObjC)] private static extern IntPtr objc_getClass (string name);
		[DllImport (ObjC)] private static extern IntPtr sel_registerName (string name);
		[DllImport (ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send (IntPtr receiver, IntPtr selector);
		[DllImport (ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtr (IntPtr receiver, IntPtr selector, IntPtr a);
		[DllImport (ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrUlong (IntPtr receiver, IntPtr selector, IntPtr a, ulong b);
		[DllImport (ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs (UnmanagedType.I1)] private static extern bool SendBool (IntPtr receiver, IntPtr selector);
		[DllImport (ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool (IntPtr receiver, IntPtr selector, [MarshalAs (UnmanagedType.I1)] bool a);
	}

	/// <summary>The desktop's own player (PipeWire's, PulseAudio's or ALSA's), given the .wav as a file.</summary>
	internal sealed class PlayerProcessOutput : SoundOutput
	{
		private static readonly string [] s_players = { "pw-play", "paplay", "aplay" };
		private readonly string _file;
		private readonly bool _loop;
		private Process _process;
		private volatile bool _stopped;
		private readonly Thread _loopThread;

		public PlayerProcessOutput (byte [] wave, bool loop)
		{
			_file = Path.Combine (Path.GetTempPath (), "soundplayer-" + Guid.NewGuid ().ToString ("N") + ".wav");
			File.WriteAllBytes (_file, wave);
			_loop = loop;
			_process = Start ();
			if (loop) {
				_loopThread = new Thread (() => {
					while (!_stopped) {
						_process?.WaitForExit ();
						if (!_stopped)
							_process = Start ();
					}
				}) { IsBackground = true, Name = "SoundPlayer loop" };
				_loopThread.Start ();
			}
		}

		private Process Start ()
		{
			foreach (string player in s_players) {
				Process p = TryRun (player, "\"" + _file + "\"", wait: false);
				if (p != null)
					return p;
			}
			throw new PlatformNotSupportedException ("No sound player (pw-play, paplay or aplay) was found.");
		}

		public override void Wait ()
		{
			_process?.WaitForExit ();
			if (!_loop)
				Delete ();
		}

		public override void Stop ()
		{
			_stopped = true;
			try {
				if (_process != null && !_process.HasExited)
					_process.Kill ();
			} catch (InvalidOperationException) {
			}
			Delete ();
		}

		private void Delete ()
		{
			try {
				File.Delete (_file);
			} catch (IOException) {
			}
		}

		internal static Process TryRun (string program, string arguments, bool wait)
		{
			try {
				var p = Process.Start (new ProcessStartInfo (program, arguments) {
					UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
				});
				if (wait)
					p?.WaitForExit ();
				return p;
			} catch (System.ComponentModel.Win32Exception) {
				return null;
			}
		}
	}
}
