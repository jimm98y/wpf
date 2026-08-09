// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Internal;
using MS.Internal.PresentationCore;
using MS.Win32;
using System.IO.Packaging;
using System.Windows.Media.Composition;
using System.Windows.Threading;
using System.Windows.Navigation;

using UnsafeNativeMethods = MS.Win32.PresentationCore.UnsafeNativeMethods;

namespace System.Windows.Media
{
    #region MediaPlayerState

    /// <summary>
    /// MediaPlayerState
    /// Holds all of the local state that is required for playing media. This is
    /// separated out into a separate class because MediaPlayer needs to be
    /// Animatable, but then that means it needs to be Freezable. However, media
    /// state cannot really be frozan (media piplines progress according to time
    /// and are very expensive), so instead, we make the "Frozen" object copy the
    /// state object around. Doing this will also help in the remote case where
    /// we need to handle MediaPlayer quite differently on the channel in the
    /// remote and local cases.
    /// </summary>
    internal class MediaPlayerState
    {
        #region Constructors and Finalizers

        /// <summary>
        /// Constructor
        /// </summary>
        internal
        MediaPlayerState(
            MediaPlayer     mediaPlayer
            )
        {
            _dispatcher = mediaPlayer.Dispatcher;

            Init();

            CreateMedia(mediaPlayer);

            //
            // We need to know about new frames when they are sent so that we can
            // capture the image data in the synchronous case.
            //
            _mediaEventsHelper.NewFrame += new EventHandler(OnNewFrame);

            //
            // Opened is actually fired when the media is prerolled.
            //
            _mediaEventsHelper.MediaPrerolled += new EventHandler(OnMediaOpened);
        }

        // Need to add support for base uri.

        /// <summary>
        /// Initialize local variables to their default state. After a close we want to restore this too, same as
        /// after construction.
        /// </summary>
        private
        void
        Init()
        {
            _volume = DEFAULT_VOLUME;
            _balance = DEFAULT_BALANCE;
            _speedRatio = 1.0;
            _paused = false;
            _muted = false;
            _sourceUri = null;
            _scrubbingEnabled = false;
        }

        /// <summary>
        /// Finalizer to remove the event handler from AppDomain.ProcessExit
        /// </summary>
        ~MediaPlayerState()
        {
            if (_helper != null)
            {
                AppDomain.CurrentDomain.ProcessExit -= _helper.ProcessExitHandler;
            }
            _backend?.Dispose();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Internal IsBuffering
        /// </summary>
        internal bool IsBuffering
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.IsBuffering; }
                bool isBuffering = false;
                HRESULT.Check(MILMedia.IsBuffering(_nativeMedia, ref isBuffering));
                return isBuffering;
            }
        }

        /// <summary>
        /// Internal CanPause
        /// </summary>
        internal bool CanPause
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.CanPause; }
                bool canPause = false;
                HRESULT.Check(MILMedia.CanPause(_nativeMedia, ref canPause));
                return canPause;
            }
        }

        /// <summary>
        /// Internal DownloadProgress
        /// </summary>
        internal double DownloadProgress
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.DownloadProgress; }
                double downloadProgress = 0;
                HRESULT.Check(MILMedia.GetDownloadProgress(_nativeMedia, ref downloadProgress));
                return downloadProgress;
            }
        }

        /// <summary>
        /// Internal BufferingProgress
        /// </summary>
        internal double BufferingProgress
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.BufferingProgress; }
                double bufferingProgress = 0;
                HRESULT.Check(MILMedia.GetBufferingProgress(_nativeMedia, ref bufferingProgress));
                return bufferingProgress;
            }
        }

        /// <summary>
        /// Returns the Height
        /// </summary>
        internal Int32 NaturalVideoHeight
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.NaturalVideoHeight; }

                UInt32 height = 0;

                HRESULT.Check(MILMedia.GetNaturalHeight(_nativeMedia, ref height));
                return (Int32)height;
            }
        }

        /// <summary>
        /// Returns the Width
        /// </summary>
        internal Int32 NaturalVideoWidth
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.NaturalVideoWidth; }

                UInt32 width = 0;

                HRESULT.Check(MILMedia.GetNaturalWidth(_nativeMedia, ref width));
                return (Int32)width;
            }
        }

        /// <summary>
        /// If media has audio content
        /// </summary>
        internal bool HasAudio
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.HasAudio; }

                bool hasAudio = true;

                HRESULT.Check(MILMedia.HasAudio(_nativeMedia, ref hasAudio));
                return hasAudio;
            }
        }

        /// <summary>
        /// If the media has video content
        /// </summary>
        internal bool HasVideo
        {
            get
            {
                VerifyAPI();
                if (_backend != null) { return _backend.HasVideo; }

                bool hasVideo = false;

                HRESULT.Check(MILMedia.HasVideo(_nativeMedia, ref hasVideo));
                return hasVideo;
            }
        }

        /// <summary>
        /// Location of the media to play. Open opens the media, this property
        /// allows the source that is currently playing to be retrieved.
        /// </summary>
        internal Uri Source
        {
            get
            {
                VerifyAPI();

                return _sourceUri;
            }
        }

        /// <summary>
        /// Internal Get Volume
        /// </summary>
        internal double Volume
        {
            get
            {
                VerifyAPI();

                return _volume;
            }
            set
            {
                VerifyAPI();

                ArgumentOutOfRangeException.ThrowIfEqual(value, double.NaN);

                if (DoubleUtil.GreaterThanOrClose(value, 1))
                {
                    value = 1;
                }
                else if (DoubleUtil.LessThanOrClose(value, 0))
                {
                    value = 0;
                }

                // We only want to set the volume if the current cached volume is not the same
                // No need to do extra work.
                if (!DoubleUtil.AreClose(_volume, value))
                {
                    if (!_muted)
                    {
                        if (_backend != null)
                        {
                            _backend.SetVolume(value);
                        }
                        else
                        {
                            HRESULT.Check(MILMedia.SetVolume(_nativeMedia, value));
                        }

                        // value is changing
                        _volume = value;
                    }
                    else
                    {
                        // If we are muted, cache the volume
                        _volume = value;
                    }
                }
            }
        }

        /// <summary>
        /// Internal Get Balance
        /// </summary>
        internal double Balance
        {
            get
            {
                VerifyAPI();

                return _balance;
            }
            set
            {
                VerifyAPI();

                ArgumentOutOfRangeException.ThrowIfEqual(value, double.NaN);

                if (DoubleUtil.GreaterThanOrClose(value, 1))
                {
                    value = 1;
                }
                else if (DoubleUtil.LessThanOrClose(value, -1))
                {
                    value = -1;
                }

                // We only want to set the balance if the current cached balance
                // is not the same. No need to do extra work.
                if (!DoubleUtil.AreClose(_balance, value))
                {
                    if (_backend != null)
                    {
                        _backend.SetBalance(value);
                    }
                    else
                    {
                        HRESULT.Check(MILMedia.SetBalance(_nativeMedia, value));
                    }

                    // value is changing
                    _balance = value;
                }
            }
        }

        /// <summary>
        /// Whether or not scrubbing is enabled
        /// </summary>
        internal bool ScrubbingEnabled
        {
            get
            {
                VerifyAPI();
                return _scrubbingEnabled;
            }
            set
            {
                VerifyAPI();
                if (value != _scrubbingEnabled)
                {
                    if (_backend != null)
                    {
                        _backend.SetScrubbingEnabled(value);
                    }
                    else
                    {
                        HRESULT.Check(MILMedia.SetIsScrubbingEnabled(_nativeMedia, value));
                    }
                    _scrubbingEnabled = value;
                }
            }
        }

        /// <summary>
        /// Internal Get Mute
        /// </summary>
        internal bool IsMuted
        {
            get
            {
                VerifyAPI();
                return _muted;
            }
            set
            {
                VerifyAPI();

                // we need to store the volume since this.Volume will change the cached value
                double volume = _volume;

                if (value && !_muted)
                {
                    // Going from Unmuted -> Muted

                    // Set the volume to 0
                    this.Volume = 0;
                    _muted = true;

                    // make sure cached volume is previous value
                    _volume = volume;
                }
                else if (!value && _muted)
                {
                    // Going from Muted -> Unmuted

                    _muted = false;

                    // set cached volume to 0 since this. Volume will only change volume
                    // if cached volume and new volume differ
                    _volume = 0;

                    // set volume to old cached value, which will also update our current cached value
                    this.Volume = volume;
                }
            }
        }

        internal Duration NaturalDuration
        {
            get
            {
                VerifyAPI();

                long mediaLength = 0;
                if (_backend != null)
                {
                    mediaLength = _backend.MediaLengthTicks;
                }
                else
                {
                    HRESULT.Check(MILMedia.GetMediaLength(_nativeMedia, ref mediaLength));
                }
                if (mediaLength == 0)
                {
                    return Duration.Automatic;
                }
                else
                {
                    return new Duration(TimeSpan.FromTicks(mediaLength));
                }
            }
        }

        /// <summary>
        /// Seek to specified position
        /// </summary>
        internal TimeSpan Position
        {
            set
            {
                VerifyAPI();

                VerifyNotControlledByClock();

                SetPosition(value);
            }
            get
            {
                VerifyAPI();

                return GetPosition();
            }
        }

        /// <summary>
        /// The current speed. This cannot be changed if a clock is controlling this player
        /// </summary>
        internal double SpeedRatio
        {
            get
            {
                VerifyAPI();

                return _speedRatio;
            }
            set
            {
                VerifyAPI();
                VerifyNotControlledByClock();

                if (value < 0)
                {
                    value = 0; // we clamp negative values to 0
                }

                SetSpeedRatio(value);
            }
        }

        /// <summary>
        /// The dispatcher, this is actually derived from the media player
        /// on construction.
        /// </summary>
        internal Dispatcher Dispatcher
        {
            get
            {
                return _dispatcher;
            }
        }


        #endregion

        #region EventHandlers

        /// <summary>
        /// Raised when there is an error opening or playing media
        /// </summary>
        internal event EventHandler<ExceptionEventArgs> MediaFailed
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.MediaFailed += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.MediaFailed -= value;
            }
        }

        /// <summary>
        /// Raised when the media has been opened.
        /// </summary>
        internal event EventHandler MediaOpened
        {
            add
            {
                VerifyAPI();

                _mediaOpenedHelper.AddEvent(value);
            }
            remove
            {
                VerifyAPI();

                _mediaOpenedHelper.RemoveEvent(value);
            }
        }


        /// <summary>
        /// Raised when the media has finished.
        /// </summary>
        internal event EventHandler MediaEnded
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.MediaEnded += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.MediaEnded -= value;
            }
        }


        /// <summary>
        /// Raised when media begins buffering.
        /// </summary>
        internal event EventHandler BufferingStarted
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.BufferingStarted += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.BufferingStarted -= value;
            }
        }


        /// <summary>
        /// Raised when media finishes buffering.
        /// </summary>
        internal event EventHandler BufferingEnded
        {
            add
            {
                VerifyAPI();

                _mediaEventsHelper.BufferingEnded += value;
            }
            remove
            {
                VerifyAPI();

                _mediaEventsHelper.BufferingEnded -= value;
            }
        }

        /// <summary>
        /// Raised when a script command embedded in the media is encountered.
        /// </summary>
        internal event EventHandler<MediaScriptCommandEventArgs> ScriptCommand
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.ScriptCommand += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.ScriptCommand -= value;
            }
        }

        /// <summary>
        /// Raised when a new frame in the media is encountered, we only
        /// send one new frame per AddRefOnChannel in synchronous mode only.
        /// </summary>
        internal event EventHandler NewFrame
        {
            add
            {
                VerifyAPI();

                _newFrameHelper.AddEvent(value);
            }

            remove
            {
                VerifyAPI();

                _newFrameHelper.RemoveEvent(value);
            }
        }

        #endregion

        #region Clock dependent properties and methods

        /// <summary>
        /// The clock driving this instance of media
        /// </summary>
        internal MediaClock Clock
        {
            get
            {
                VerifyAPI();
                return _mediaClock;
            }
        }

        internal
        void
        SetClock(
            MediaClock  clock,
            MediaPlayer player
            )
        {
            VerifyAPI();
            MediaClock oldClock = _mediaClock;
            MediaClock newClock = clock;

            // Avoid infinite loops
            if (oldClock != newClock)
            {
                _mediaClock = newClock;

                // Disassociate the old clock
                oldClock?.Player = null;

                // Associate the new clock;
                newClock?.Player = player;

                // According to the spec, setting the Clock to null
                // should set the Source to null
                if (newClock == null)
                {
                    Open(null);
                }
            }
        }

        /// <summary>
        /// Open the media, at this point the underlying native resources are
        /// created. The media player cannot be controlled when it isn't opened.
        /// </summary>
        internal
        void
        Open(
            Uri      source
            )
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            SetSource(source);

            // Media fails to resume playback after setting source property to the same value.
			// We workaround this by resuing one instance of MediaElement and
            // calling play() wont result in seek to zero, Media Freezes.  Ensure
            // we set Media to play from the beginning.
            SetPosition(TimeSpan.Zero);
        }

        /// <summary>
        /// Begin playback. This operation is not allowed if a clock is
        /// controlling this player
        /// </summary>
        internal void Play()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            _paused = false;
            PrivateSpeedRatio = SpeedRatio;
        }

        /// <summary>
        /// Halt playback at current position. This operation is not allowed if
        /// a clock is controlling this player
        /// </summary>
        internal void Pause()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            _paused = true;
            PrivateSpeedRatio = 0;
        }

        /// <summary>
        /// Halt playback and seek to the beginning of media. This operation is
        /// not allowed if a clock is controlling this player
        /// </summary>
        internal void Stop()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            Pause();
            Position = TimeSpan.FromTicks(0);
        }

        /// <summary>
        /// Closes the underlying media. This de-allocates all of the native resources in
        /// the media. The mediaplayer can be opened again by calling the Open method.
        /// </summary>
        internal
        void
        Close()
        {
            VerifyAPI();

            VerifyNotControlledByClock();

            if (_backend != null)
            {
                _backend.Close();
            }
            else
            {
                HRESULT.Check(MILMedia.Close(_nativeMedia));
            }

            //
            // Once we successfully close, we don't have a clock anymore.
            // Assign the property so that the clock is disconnected from the
            // player as well as the player from the clock.
            //
            SetClock(null, null);

            Init();
        }

        /// <summary>
        /// Sends a command to play the given media.
        /// </summary>
        internal
        void
        SendCommandMedia(
            DUCE.Channel            channel,
            DUCE.ResourceHandle     handle,
            bool                    notifyUceDirectly
            )
        {
            SendMediaPlayerCommand(
                channel,
                handle,
                notifyUceDirectly
                );

            //
            // Independently, tell the native media that we need to update the UI, the
            // reason we do this directly through the player is that effects can immediately
            // remove the channel on us and hence media might not get a chance to see
            // the media player resource.
            //
            if (!notifyUceDirectly)
            {
                NeedUIFrameUpdate();
            }
        }

        /// <summary>
        /// Sends a request to the media player to reserve a UI frame for notification.
        /// </summary>
        private
        void
        NeedUIFrameUpdate()
        {
            VerifyAPI();

            if (_backend != null)
            {
                _backend.NeedUIFrameUpdate();
            }
            else
            {
                HRESULT.Check(MILMedia.NeedUIFrameUpdate(_nativeMedia));
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Create the unmanaged media resources
        /// </summary>
        private void CreateMedia(MediaPlayer mediaPlayer)
        {
            MediaEventsHelper.CreateMediaEventsHelper(mediaPlayer, out _mediaEventsHelper, out _);

            // There is no native media object on any platform any more. The milcore player
            // (wpfgfx_cor3.dll) decoded into milcore's own compositor, which the managed WebGPU
            // compositor replaced, so even on Windows it played sound into a video that never appeared.
            // A platform IMediaBackend does the whole job instead and its frames reach the compositor
            // through SendVideoFrame. The media handle stays INVALID; a zero handle is IsInvalid, so
            // SafeMediaHandle.ReleaseHandle never runs native code, and the MILMedia.* leaf calls that
            // remain for backend-less platforms are inert stubs.
            _nativeMedia = new SafeMediaHandle();
            _helper = new Helper(_nativeMedia);
            AppDomain.CurrentDomain.ProcessExit += _helper.ProcessExitHandler;

            // May be null where no backend exists yet (Android, iOS) -> the element stays blank.
            _backend = MediaBackendFactory.Create(mediaPlayer);
            if (_backend != null)
            {
                _backend.Opened += () => _mediaEventsHelper.RaiseMediaOpened();
                _backend.Ended += () => _mediaEventsHelper.RaiseMediaEnded();
                _backend.Failed += (ex) => _mediaEventsHelper.RaiseMediaFailed(ex);
                _backend.BufferingStarted += () => _mediaEventsHelper.RaiseBufferingStarted();
                _backend.BufferingEnded += () => _mediaEventsHelper.RaiseBufferingEnded();
                _backend.FrameAvailable += () => _mediaEventsHelper.RaiseNewFrame();
            }
        }

        /// <summary>
        /// Open Media
        /// </summary>
        private void OpenMedia(Uri source)
        {
            if (source != null && source.IsAbsoluteUri && source.Scheme == PackUriHelper.UriSchemePack)
            {
                try
                {
                    source = BaseUriHelper.ConvertPackUriToAbsoluteExternallyVisibleUri(source);
                }
                catch (InvalidOperationException)
                {
                    source = null;
                    _mediaEventsHelper.RaiseMediaFailed(new System.NotSupportedException(SR.Format(SR.Media_PackURIsAreNotSupported, null)));
                }
            }

            // Setting a null source effectively disconects the MediaElement.
            string toOpen = null;
            if (source != null)
            {
                // get the base directory of the application; never expose this
                Uri appBase = SecurityHelper.GetBaseDirectory(AppDomain.CurrentDomain);
                // this extracts the URI to open
                Uri uriToOpen = ResolveUri(source, appBase);

                // The security-zone demand maps the URL through urlmon, which exists only on Windows.
                // Elsewhere the backend is handed a plain file path or http(s) URL directly.
                toOpen = OperatingSystem.IsWindows()
                    ? DemandPermissions(uriToOpen)
                    : (uriToOpen.IsFile ? uriToOpen.LocalPath : uriToOpen.AbsoluteUri);
            }

            // We pass in exact same URI for which we demanded permissions so that we can be sure
            // there is no discrepancy between the two.
            if (_backend != null)
            {
                if (toOpen != null) { _backend.Open(toOpen); }
            }
            else
            {
                HRESULT.Check(MILMedia.Open(_nativeMedia, toOpen));
            }
        }

        private Uri ResolveUri(Uri uri, Uri appBase)
        {
            if (uri.IsAbsoluteUri)
            {
                return uri;
            }
            else
            {
                return new Uri(appBase, uri);
            }
        }

        // returns the exact string on which we demanded permissions

        private string DemandPermissions(Uri absoluteUri)
        {
            Debug.Assert(absoluteUri.IsAbsoluteUri);
            string toOpen = BindUriHelper.UriToString(absoluteUri);
            int targetZone = SecurityHelper.MapUrlToZoneWrapper(absoluteUri);

            if (targetZone == NativeMethods.URLZONE_LOCAL_MACHINE)
            {
                // go here only for files and not for UNC
                if (absoluteUri.IsFile)
                {
                    toOpen = absoluteUri.LocalPath;
                }
            }

            return toOpen;
        }

        /// <summary>
        /// Seek to specified position (in 100 nanosecond ticks)
        /// </summary>
        internal void SetPosition(TimeSpan value)
        {
            VerifyAPI();

            if (_backend != null)
            {
                _backend.SetPositionTicks(value.Ticks);
            }
            else
            {
                HRESULT.Check(MILMedia.SetPosition(_nativeMedia, value.Ticks));
            }
        }

        /// <summary>
        /// get the current position (in 100 nanosecond ticks)
        /// </summary>
        private TimeSpan GetPosition()
        {
            VerifyAPI();

            if (_backend != null)
            {
                return TimeSpan.FromTicks(_backend.GetPositionTicks());
            }
            long position = 0;
            HRESULT.Check(MILMedia.GetPosition(_nativeMedia, ref position));
            return TimeSpan.FromTicks(position);
        }

        private double PrivateSpeedRatio
        {
            set
            {
                VerifyAPI();

                ArgumentOutOfRangeException.ThrowIfEqual(value, double.NaN);

                if (_backend != null)
                {
                    _backend.SetRate(value);
                }
                else
                {
                    HRESULT.Check(MILMedia.SetRate(_nativeMedia, value));
                }
            }
        }

        //
        // Set the current speed.
        //
        internal void SetSpeedRatio(double value)
        {
            _speedRatio = value;

            //
            // We don't change the speed if we are paused, unless we are in
            // clock mode, which overrides paused mode.
            //
            if (!_paused || _mediaClock != null)
            {
                PrivateSpeedRatio = _speedRatio;
            }
        }

        /// <summary>
        /// Sets the source of the media (and opens it), without checking whether
        /// we are under clock control. This is called by the clock.
        /// </summary>
        internal
        void
        SetSource(
            Uri      source
            )
        {
            if (source != _sourceUri)
            {
                OpenMedia(source);

                //
                // Only assign the source uri if the OpenMedia succeeds.
                //
                _sourceUri = source;
            }
        }


        /// <summary>
        /// Verifies this object is in an accessible state, and that we
        /// are being called from the correct thread. This method should
        /// be the first thing called from any internal method.
        /// </summary>
        private void VerifyAPI()
        {
            //
            // We create _nativeMedia in the constructor, so it should always
            // be initialized.
            //
            Debug.Assert(_nativeMedia != null);

            //
            // We only allow calls to any media object on the UI thread. That is all this check does
            // now: the media handle is intentionally INVALID on every platform (playback is owned by
            // the IMediaBackend, and MILMedia operations are inert stubs), so an invalid handle is no
            // longer the version/DLL error it used to signal.
            //
            _dispatcher.VerifyAccess();
        }

        /// <summary>
        /// Verifies that this player is not currently controlled by a clock. Some actions are
        /// invalid while we are under clock control.
        /// </summary>
        private
        void
        VerifyNotControlledByClock()
        {
            if (Clock != null)
            {
                throw new InvalidOperationException(SR.Media_NotAllowedWhileTimingEngineInControl);
            }
        }

        /// <summary>
        /// SendMediaPlayerCommand
        /// </summary>              SecurityNote
        private
        void
        SendMediaPlayerCommand(
            DUCE.Channel            channel,
            DUCE.ResourceHandle     handle,
            bool                    notifyUceDirectly
            )
        {
            // There is no native media object to hand to the compositor. Pull the current decoded frame
            // from the platform backend instead and ship its BGRA pixels over the SendVideoFrame seam,
            // keyed by this MediaPlayer resource handle -- the MilDrawVideo record (still emitted by
            // MediaElement) samples it. A fresh array each pass makes the frame texture re-upload.
            if (_backend == null)
            {
                return;
            }

            if (_backend.TryLockFrame(out IntPtr baseAddr, out int fw, out int fh, out int rowBytes) &&
                baseAddr != IntPtr.Zero && fw > 0 && fh > 0)
            {
                try
                {
                    byte[] bgra = new byte[(long)rowBytes * fh];
                    System.Runtime.InteropServices.Marshal.Copy(baseAddr, bgra, 0, bgra.Length);
                    channel.SendVideoFrame(handle, fw, fh, rowBytes, bgra);
                }
                finally
                {
                    _backend.UnlockFrame();
                }
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// When a new frame is received, we need to check if it is remote and then acquire the new frame
        /// from the composition synchronously before passing the NewFrame event up to the
        /// Media player.
        /// </summary>
        private
        void
        OnNewFrame(
            object          sender,
            EventArgs       args
            )
        {
            _newFrameHelper.InvokeEvents(sender, args);
        }

        /// <summary>
        /// Fired when the media is opened, we can't open our capture resources until the media is opened
        /// because we won't know the size of the media.
        /// </summary>
        private
        void
        OnMediaOpened(
            object          sender,
            EventArgs       args
            )
        {
            _mediaOpenedHelper.InvokeEvents(sender, args);
        }

        #endregion


        #region Data Members

        /// <summary>
        /// Current volume (ranges from 0 to 1)
        /// </summary>
        private double _volume;

        /// <summary>
        /// Current balance (ranges from -1 (left) to 1 (right) )
        /// </summary>
        private double _balance;

        /// <summary>
        /// Current state of mute
        /// </summary>
        private bool _muted;

        /// <summary>
        /// Whether or not scrubbin is enabled
        /// </summary>
        private bool _scrubbingEnabled;

        /// <summary>
        /// Unamanaged Media object
        /// </summary>
        private SafeMediaHandle _nativeMedia;

        // Off-Windows platform media engine (AVFoundation on macOS). Non-null only off-Windows where a backend
        // exists; every MILMedia.* leaf call in this class routes to it instead of the (no-op'd) native media.
        private IMediaBackend _backend;

        private MediaEventsHelper _mediaEventsHelper;

        /// <summary>
        /// Default volume
        /// </summary>
        private const double DEFAULT_VOLUME = 0.5;

        /// <summary>
        /// Default balance
        /// </summary>
        private const double DEFAULT_BALANCE = 0;

        private double _speedRatio;
        private bool _paused;

        private Uri _sourceUri;

        private MediaClock _mediaClock = null;

        private Dispatcher _dispatcher = null;

        private UniqueEventHelper _newFrameHelper = new UniqueEventHelper();
        private UniqueEventHelper _mediaOpenedHelper = new UniqueEventHelper();

        private const float _defaultDevicePixelsPerInch = 96.0F;

        private Helper _helper;

        /// <summary>
        /// A separate class is needed to register for the ProcessExit event
        /// because MediaPlayerState holds a strong reference to _nativeMedia.
        /// If a MediaPlayerState method was registered for the ProcessExit
        /// event then MediaPlayerState would not be garbage collected until
        /// ProcessExit time.
        /// </summary>
        private class Helper
        {
            private WeakReference _nativeMedia;

            internal
            Helper(
                SafeMediaHandle nativeMedia
                )
            {
                _nativeMedia = new WeakReference(nativeMedia);
            }

            internal
            void
            ProcessExitHandler(
                object sender,
                EventArgs args
                )
            {
                SafeMediaHandle nativeMedia = (SafeMediaHandle)_nativeMedia.Target;
                if (nativeMedia != null)
                {
                    MILMedia.ProcessExitHandler(nativeMedia);
                }
            }
        };
        #endregion
    }

    #endregion
};

