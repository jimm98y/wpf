// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// THE RENDER THREAD.
//
// WPF's own compositor runs on a thread of its own: the UI thread hands a batch of commands over and
// goes back to layout while the batch is composited and presented. This sink used to do all of that
// synchronously inside Channel.Commit, on the UI thread, so a frame cost WPF's work PLUS ours. On a
// 100Hz display that is 10ms for both together -- measured in the gallery at ~4ms WPF + ~8ms here,
// which missed every other vsync and ran at ~50fps with the UI thread idle a third of the time.
//
// With WPF_RENDER_THREAD=1 every IMilCompositionSink call is RECORDED on the UI thread and replayed,
// in order, on a dedicated render thread; Commit hands the recorded batch over and returns. The engine,
// the renderer and every GPU object are then touched by the render thread alone, so nothing needs a
// lock or a second copy of the scene. The only state the UI thread keeps is what it must answer
// synchronously:
//
//   * resource handles, which the UI thread allocates (the allocator never touched the engine), and
//   * reference counts, mirrored exactly (MilcoreEngine.CreateOrAddRef / Release), because
//     ReleaseOnChannel has to say NOW whether the resource left the channel.
//
// A Commit never waits: calls made while the render thread is busy join the batch it has not taken
// yet, and it draws once for both (see HandOver for why waiting deadlocked). SyncFlush and
// ReadbackTarget (RenderTargetBitmap) do wait for their batch, pumping sent messages as they do.
//

using System;
using System.Collections.Generic;
using System.Threading;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed unsafe partial class WpfCompositionSink
    {
        /// <summary>Whether to composite on a dedicated thread (see the file header). On by default on
        /// Windows, where it has been measured and exercised (popups, resize, shutdown, the WPF
        /// suites); WPF_RENDER_THREAD=0 turns it off there and =1 turns it on elsewhere. Never in the
        /// browser, which has one thread.</summary>
        private static readonly bool s_renderThread =
            !OperatingSystem.IsBrowser()
            && Environment.GetEnvironmentVariable("WPF_RENDER_THREAD") switch
            {
                "1" => true,
                "0" => false,
                _ => OperatingSystem.IsWindows(),
            };

        private readonly object _rtLock = new();
        private Thread? _rtThread;
        private List<Action> _rtRecording = new();     // UI thread: calls since the last hand-over
        private List<Action>? _rtHanded;               // handed over, not yet taken by the render thread
        private bool _rtRender;                        // ...and whether a frame was committed with them
        private long _rtHandedSeq, _rtDoneSeq;         // hand-overs made, and executed
        private readonly Dictionary<uint, int> _uiRefCounts = new();

        [ThreadStatic] private static bool t_onRenderThread;

        /// <summary>Whether this sink composites on a render thread. WPF_RENDER_THREAD decides it for the
        /// sink WPF creates; tests set it per instance, before the first call.</summary>
        internal bool UseRenderThread { get; set; } = s_renderThread;

        /// <summary>True when calls on this thread are to be recorded for the render thread.</summary>
        private bool Recording => UseRenderThread && !t_onRenderThread && !_rtStopping;

        private bool _rtStopping;

        private void Record(Action call)
        {
            EnsureRenderThread();
            _rtRecording.Add(call);
        }

        private void EnsureRenderThread()
        {
            if (_rtThread != null) return;
            _rtThread = new Thread(RenderThreadMain)
            {
                IsBackground = true,
                Name = "WPF WebGPU render thread",
            };
            _rtThread.Start();
            Log("render thread started");
        }

        /// <summary>Hands everything recorded so far to the render thread.
        /// <para>A COMMIT NEVER WAITS. If the render thread is still on the previous frame, these
        /// calls join the batch it has not taken yet and it draws once for both -- WPF paces the UI
        /// thread to the display, so this coalesces the odd slow frame rather than letting anything
        /// run away. Blocking here instead deadlocked: the GL driver, compiling a pipeline on the
        /// render thread, sends a message to the window's thread, and a WPF UI thread waiting on a
        /// lock does not process sent messages.</para>
        /// <para>With <paramref name="wait"/> (SyncFlush, RenderTargetBitmap) it returns once this
        /// batch has executed; see <see cref="WaitForRenderThread"/>.</para></summary>
        private void HandOver(bool render, bool wait)
        {
            EnsureRenderThread();
            long seq;
            lock (_rtLock)
            {
                if (_rtHanded == null) _rtHanded = _rtRecording;
                else _rtHanded.AddRange(_rtRecording);
                _rtRecording = new List<Action>();
                _rtRender |= render;
                seq = ++_rtHandedSeq;
                Monitor.PulseAll(_rtLock);
            }
            if (wait) WaitForRenderThread(seq);
        }

        /// <summary>Waits for hand-over <paramref name="seq"/> to execute, processing messages sent to
        /// this thread meanwhile (Platform.NativePlatform.PumpSentMessages) so that a render thread
        /// that needs the window's thread cannot deadlock against it.</summary>
        private void WaitForRenderThread(long seq)
        {
            while (true)
            {
                lock (_rtLock)
                {
                    if (_rtDoneSeq >= seq) return;
                    Monitor.Wait(_rtLock, 2);
                    if (_rtDoneSeq >= seq) return;
                }
                Platform.NativePlatform.PumpSentMessages();
            }
        }

        private void RenderThreadMain()
        {
            t_onRenderThread = true;
            while (true)
            {
                List<Action> batch;
                bool render;
                long seq;
                lock (_rtLock)
                {
                    // Idle, but not deaf: wgpu's GL backend gives this thread a hidden window for its
                    // context, and a thread that owns a window has to process its messages or every
                    // sender -- a broadcast, the driver -- waits on it.
                    while (_rtHanded == null)
                    {
                        if (_rtStopping) return;
                        if (Monitor.Wait(_rtLock, 4)) continue;
                        Monitor.Exit(_rtLock);
                        try { Platform.NativePlatform.PumpThreadMessages(); }
                        finally { Monitor.Enter(_rtLock); }
                    }
                    batch = _rtHanded;
                    render = _rtRender;
                    seq = _rtHandedSeq;
                    _rtHanded = null;
                    _rtRender = false;
                }
                foreach (Action call in batch) Run(call);
                if (render) Run(RenderTargets);
                lock (_rtLock)
                {
                    _rtDoneSeq = seq;
                    Monitor.PulseAll(_rtLock);
                }
            }
        }

        private static void Run(Action call)
        {
            try { call(); }
            catch (Exception ex)
            {
                // The synchronous sink let these reach WPF's commit; here there is no caller to throw
                // to, and a render thread that dies takes every later frame with it.
                Log($"render thread: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            }
        }

        /// <summary>Runs whatever has been recorded, then ends the render thread. Later calls on this
        /// sink run synchronously on the calling thread.</summary>
        private void StopRenderThread()
        {
            if (_rtThread == null) return;
            if (_rtRecording.Count > 0) HandOver(render: false, wait: true);
            lock (_rtLock)
            {
                _rtStopping = true;
                Monitor.PulseAll(_rtLock);
            }
            _rtThread.Join(TimeSpan.FromSeconds(5));
            _rtThread = null;
        }

        // ---- the synchronous answers the UI thread keeps -----------------------------------

        private void MirrorAddRef(uint handle)
            => _uiRefCounts[handle] = _uiRefCounts.TryGetValue(handle, out int rc) ? rc + 1 : 1;

        /// <summary>MilcoreEngine.Release's answer, computed from the mirror.</summary>
        private bool MirrorRelease(uint handle)
        {
            if (_uiRefCounts.TryGetValue(handle, out int rc) && rc > 1)
            {
                _uiRefCounts[handle] = rc - 1;
                return false;
            }
            _uiRefCounts.Remove(handle);
            return true;
        }
    }
}
