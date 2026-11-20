// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What winmm's wave functions do (WAVE_PROBE=<file> writes the report; Wave.windows.txt is Windows'): the devices and
// their capabilities, the formats an open takes, a header's flags through prepare, write and done, the callback's
// messages and their order, pause, restart, reset, the position in each time format, the volume, the error texts; the
// same of waveIn (the microphone on briefly: nothing is kept) and the mixers' count and capabilities. The device
// names are left out of the report, the times and the positions of their tolerances.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Winmm;
using Xunit;

namespace WgpuInterop.Tests.MediaFoundation
{
    public sealed unsafe class WaveProbe
    {
        // ---- the functions: Windows' own there, ours elsewhere ----
        private static readonly bool Win = OperatingSystem.IsWindows();
        private static class N
        {
            [DllImport("winmm")] public static extern uint waveOutGetNumDevs();
            [DllImport("winmm")] public static extern uint waveOutGetDevCapsW(nuint id, void* caps, uint cb);
            [DllImport("winmm")] public static extern uint waveOutGetDevCapsA(nuint id, void* caps, uint cb);
            [DllImport("winmm")] public static extern uint waveOutOpen(nint* h, uint id, void* fmt, nint cb, nint inst, uint flags);
            [DllImport("winmm")] public static extern uint waveOutClose(nint h);
            [DllImport("winmm")] public static extern uint waveOutPrepareHeader(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveOutUnprepareHeader(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveOutWrite(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveOutPause(nint h);
            [DllImport("winmm")] public static extern uint waveOutRestart(nint h);
            [DllImport("winmm")] public static extern uint waveOutReset(nint h);
            [DllImport("winmm")] public static extern uint waveOutBreakLoop(nint h);
            [DllImport("winmm")] public static extern uint waveOutGetPosition(nint h, void* t, uint cb);
            [DllImport("winmm")] public static extern uint waveOutGetVolume(nint h, uint* v);
            [DllImport("winmm")] public static extern uint waveOutSetVolume(nint h, uint v);
            [DllImport("winmm")] public static extern uint waveOutGetPitch(nint h, uint* v);
            [DllImport("winmm")] public static extern uint waveOutSetPitch(nint h, uint v);
            [DllImport("winmm")] public static extern uint waveOutGetPlaybackRate(nint h, uint* v);
            [DllImport("winmm")] public static extern uint waveOutSetPlaybackRate(nint h, uint v);
            [DllImport("winmm")] public static extern uint waveOutGetID(nint h, uint* id);
            [DllImport("winmm")] public static extern uint waveOutGetErrorTextW(uint e, char* t, uint n);
            [DllImport("winmm")] public static extern uint waveOutMessage(nint h, uint m, nuint a, nuint b);
            [DllImport("winmm")] public static extern uint waveInGetNumDevs();
            [DllImport("winmm")] public static extern uint waveInGetDevCapsW(nuint id, void* caps, uint cb);
            [DllImport("winmm")] public static extern uint waveInOpen(nint* h, uint id, void* fmt, nint cb, nint inst, uint flags);
            [DllImport("winmm")] public static extern uint waveInClose(nint h);
            [DllImport("winmm")] public static extern uint waveInPrepareHeader(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveInUnprepareHeader(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveInAddBuffer(nint h, void* hdr, uint cb);
            [DllImport("winmm")] public static extern uint waveInStart(nint h);
            [DllImport("winmm")] public static extern uint waveInStop(nint h);
            [DllImport("winmm")] public static extern uint waveInReset(nint h);
            [DllImport("winmm")] public static extern uint waveInGetPosition(nint h, void* t, uint cb);
            [DllImport("winmm")] public static extern uint waveInGetID(nint h, uint* id);
            [DllImport("winmm")] public static extern uint waveInGetErrorTextW(uint e, char* t, uint n);
            [DllImport("winmm")] public static extern uint waveInMessage(nint h, uint m, nuint a, nuint b);
            [DllImport("winmm")] public static extern uint mixerGetNumDevs();
            [DllImport("winmm")] public static extern uint mixerGetDevCapsW(nuint id, void* caps, uint cb);
            [DllImport("kernel32")] public static extern nint CreateEventW(nint sa, int manual, int initial, nint name);
            [DllImport("kernel32")] public static extern uint WaitForSingleObject(nint h, uint ms);
            [DllImport("kernel32")] public static extern int CloseHandle(nint h);
        }
        private static uint OutNum() => Win ? N.waveOutGetNumDevs() : WaveExports.waveOutGetNumDevs();
        private static uint OutCaps(nuint id, void* c, uint cb) => Win ? N.waveOutGetDevCapsW(id, c, cb) : WaveExports.waveOutGetDevCapsW(id, c, cb);
        private static uint OutCapsA(nuint id, void* c, uint cb) => Win ? N.waveOutGetDevCapsA(id, c, cb) : WaveExports.waveOutGetDevCapsA(id, c, cb);
        private static uint OutOpen(nint* h, uint id, void* f, nint cb, nint inst, uint fl) => Win ? N.waveOutOpen(h, id, f, cb, inst, fl) : WaveExports.waveOutOpen(h, id, f, cb, inst, fl);
        private static uint OutClose(nint h) => Win ? N.waveOutClose(h) : WaveExports.waveOutClose(h);
        private static uint OutPrepare(nint h, void* p, uint cb) => Win ? N.waveOutPrepareHeader(h, p, cb) : WaveExports.waveOutPrepareHeader(h, p, cb);
        private static uint OutUnprepare(nint h, void* p, uint cb) => Win ? N.waveOutUnprepareHeader(h, p, cb) : WaveExports.waveOutUnprepareHeader(h, p, cb);
        private static uint OutWrite(nint h, void* p, uint cb) => Win ? N.waveOutWrite(h, p, cb) : WaveExports.waveOutWrite(h, p, cb);
        private static uint OutPause(nint h) => Win ? N.waveOutPause(h) : WaveExports.waveOutPause(h);
        private static uint OutRestart(nint h) => Win ? N.waveOutRestart(h) : WaveExports.waveOutRestart(h);
        private static uint OutReset(nint h) => Win ? N.waveOutReset(h) : WaveExports.waveOutReset(h);
        private static uint OutBreakLoop(nint h) => Win ? N.waveOutBreakLoop(h) : WaveExports.waveOutBreakLoop(h);
        private static uint OutPosition(nint h, void* t, uint cb) => Win ? N.waveOutGetPosition(h, t, cb) : WaveExports.waveOutGetPosition(h, t, cb);
        private static uint OutGetVolume(nint h, uint* v) => Win ? N.waveOutGetVolume(h, v) : WaveExports.waveOutGetVolume(h, v);
        private static uint OutSetVolume(nint h, uint v) => Win ? N.waveOutSetVolume(h, v) : WaveExports.waveOutSetVolume(h, v);
        private static uint OutGetPitch(nint h, uint* v) => Win ? N.waveOutGetPitch(h, v) : WaveExports.waveOutGetPitch(h, v);
        private static uint OutSetPitch(nint h, uint v) => Win ? N.waveOutSetPitch(h, v) : WaveExports.waveOutSetPitch(h, v);
        private static uint OutGetRate(nint h, uint* v) => Win ? N.waveOutGetPlaybackRate(h, v) : WaveExports.waveOutGetPlaybackRate(h, v);
        private static uint OutSetRate(nint h, uint v) => Win ? N.waveOutSetPlaybackRate(h, v) : WaveExports.waveOutSetPlaybackRate(h, v);
        private static uint OutId(nint h, uint* id) => Win ? N.waveOutGetID(h, id) : WaveExports.waveOutGetID(h, id);
        private static uint OutText(uint e, char* t, uint n) => Win ? N.waveOutGetErrorTextW(e, t, n) : WaveExports.waveOutGetErrorTextW(e, t, n);
        private static uint OutMessage(nint h, uint m, nuint a, nuint b) => Win ? N.waveOutMessage(h, m, a, b) : WaveExports.waveOutMessage(h, m, a, b);
        private static uint InNum() => Win ? N.waveInGetNumDevs() : WaveExports.waveInGetNumDevs();
        private static uint InCaps(nuint id, void* c, uint cb) => Win ? N.waveInGetDevCapsW(id, c, cb) : WaveExports.waveInGetDevCapsW(id, c, cb);
        private static uint InOpen(nint* h, uint id, void* f, nint cb, nint inst, uint fl) => Win ? N.waveInOpen(h, id, f, cb, inst, fl) : WaveExports.waveInOpen(h, id, f, cb, inst, fl);
        private static uint InClose(nint h) => Win ? N.waveInClose(h) : WaveExports.waveInClose(h);
        private static uint InPrepare(nint h, void* p, uint cb) => Win ? N.waveInPrepareHeader(h, p, cb) : WaveExports.waveInPrepareHeader(h, p, cb);
        private static uint InUnprepare(nint h, void* p, uint cb) => Win ? N.waveInUnprepareHeader(h, p, cb) : WaveExports.waveInUnprepareHeader(h, p, cb);
        private static uint InAdd(nint h, void* p, uint cb) => Win ? N.waveInAddBuffer(h, p, cb) : WaveExports.waveInAddBuffer(h, p, cb);
        private static uint InStart(nint h) => Win ? N.waveInStart(h) : WaveExports.waveInStart(h);
        private static uint InStop(nint h) => Win ? N.waveInStop(h) : WaveExports.waveInStop(h);
        private static uint InReset(nint h) => Win ? N.waveInReset(h) : WaveExports.waveInReset(h);
        private static uint InPosition(nint h, void* t, uint cb) => Win ? N.waveInGetPosition(h, t, cb) : WaveExports.waveInGetPosition(h, t, cb);
        private static uint InId(nint h, uint* id) => Win ? N.waveInGetID(h, id) : WaveExports.waveInGetID(h, id);
        private static uint InText(uint e, char* t, uint n) => Win ? N.waveInGetErrorTextW(e, t, n) : WaveExports.waveInGetErrorTextW(e, t, n);
        private static uint InMessage(nint h, uint m, nuint a, nuint b) => Win ? N.waveInMessage(h, m, a, b) : WaveExports.waveInMessage(h, m, a, b);
        private static uint MixNum() => Win ? N.mixerGetNumDevs() : WaveExports.mixerGetNumDevs();
        private static uint MixCaps(nuint id, void* c, uint cb) => Win ? N.mixerGetDevCapsW(id, c, cb) : WaveExports.mixerGetDevCapsW(id, c, cb);

        // an event: Windows' there; ours a test's own (the shim's SetEvent hook)
        private static readonly Dictionary<nint, ManualResetEventSlim> s_events = new();
        private static nint s_nextEvent = 0x7700;
        private static nint NewEvent()
        {
            if (Win) return N.CreateEventW(0, 0, 0, 0);
            lock (s_events)
            {
                if (AppContext.GetData("WpfWebGpu.SetEvent") == null)
                    AppContext.SetData("WpfWebGpu.SetEvent", (Action<IntPtr>)(h => { lock (s_events) if (s_events.TryGetValue(h, out var e)) e.Set(); }));
                var h = s_nextEvent += 4;
                s_events[h] = new ManualResetEventSlim(false);
                return h;
            }
        }
        private static bool WaitEvent(nint h, int ms)
        {
            if (Win) return N.WaitForSingleObject(h, (uint)ms) == 0;
            ManualResetEventSlim e; lock (s_events) e = s_events[h];
            bool r = e.Wait(ms); e.Reset();             // (auto-reset, as the Windows event made)
            return r;
        }
        private static void CloseEvent(nint h) { if (Win) N.CloseHandle(h); else lock (s_events) s_events.Remove(h); }

        // ---- the callback's messages ----
        private static readonly List<(uint msg, nint h, nint inst, nint p1, bool callerThread)> s_messages = new();
        private static int s_callerThread;
        [UnmanagedCallersOnly]
        private static void Callback(nint h, uint msg, nint inst, nint p1, nint p2)
        {
            lock (s_messages) s_messages.Add((msg, h, inst, p1, Environment.CurrentManagedThreadId == s_callerThread));
        }
        private static string Msg(uint m) => m switch { 0x3BB => "WOM_OPEN", 0x3BC => "WOM_CLOSE", 0x3BD => "WOM_DONE", 0x3BE => "WIM_OPEN", 0x3BF => "WIM_CLOSE", 0x3C0 => "WIM_DATA", _ => $"0x{m:x}" };
        private static string Messages(nint h, nint hdrA = 0, nint hdrB = 0)
        {
            lock (s_messages)
            {
                var s = string.Join(" ", s_messages.Select(m => Msg(m.msg) + (m.h == h ? "" : "(other handle)") + (m.inst == 0x1234 ? "" : "(other instance)")
                    + (m.p1 == 0 ? "" : m.p1 == hdrA ? "[a]" : m.p1 == hdrB ? "[b]" : "[?]") + (m.callerThread ? "(caller's thread)" : "")));
                s_messages.Clear();
                return s.Length == 0 ? "none" : s;
            }
        }
        private static bool WaitMessages(int count, int ms)
        {
            for (int t = 0; t < ms; t += 10) { lock (s_messages) if (s_messages.Count >= count) return true; Thread.Sleep(10); }
            return false;
        }

        // ---- the structures (mmsystem.h: packed to 1 byte) ----
        private const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_FUNCTION = 0x30000, CALLBACK_EVENT = 0x50000, WAVE_FORMAT_QUERY = 1, WAVE_ALLOWSYNC = 2, WAVE_FORMAT_DIRECT = 8;
        private static string R(uint r) => r switch
        {
            0 => "OK", 1 => "MMSYSERR_ERROR", 2 => "MMSYSERR_BADDEVICEID", 4 => "MMSYSERR_ALLOCATED", 5 => "MMSYSERR_INVALHANDLE", 6 => "MMSYSERR_NODRIVER",
            7 => "MMSYSERR_NOMEM", 8 => "MMSYSERR_NOTSUPPORTED", 10 => "MMSYSERR_INVALFLAG", 11 => "MMSYSERR_INVALPARAM", 32 => "WAVERR_BADFORMAT",
            33 => "WAVERR_STILLPLAYING", 34 => "WAVERR_UNPREPARED", 35 => "WAVERR_SYNC", _ => r.ToString()
        };

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX { public ushort Tag, Channels; public uint Rate, AvgBytes; public ushort BlockAlign, Bits, CbSize; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEXTENSIBLE { public WAVEFORMATEX F; public ushort ValidBits; public uint Mask; public Guid Sub; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR { public nint Data; public uint Length, Recorded; public nint User; public uint Flags, Loops; public nint Next, Reserved; }

        private static WAVEFORMATEX Pcm(ushort tag, int ch, int rate, int bits) => new() { Tag = tag, Channels = (ushort)ch, Rate = (uint)rate, Bits = (ushort)bits, BlockAlign = (ushort)(ch * bits / 8), AvgBytes = (uint)(rate * ch * bits / 8) };

        private static string Caps(byte* c, bool input)
        {
            ushort mid = *(ushort*)c, pid = *(ushort*)(c + 2);
            uint ver = *(uint*)(c + 4);
            string name = new string((char*)(c + 8));
            uint formats = *(uint*)(c + 72);
            ushort ch = *(ushort*)(c + 76), res = *(ushort*)(c + 78);
            string tail = input ? "" : $" support 0x{*(uint*)(c + 80):x}";
            return $"mid {mid} pid {pid} version 0x{ver:x} name {(name == "Microsoft Sound Mapper" ? name : name.Length is > 0 and <= 31 ? "<device>" : name.Length + " chars")} formats 0x{formats:x} channels {ch} reserved {res}{tail}";
        }

        internal static List<string> Report()
        {
            var log = new List<string>();
            s_callerThread = Environment.CurrentManagedThreadId;
            Out(log);
            In(log);
            // the mixers: their count, the first's capabilities (MIXERCAPSW: 80 bytes)
            uint mixers = MixNum();
            log.Add($"==== mixers {(mixers > 0 ? "some" : "none")}");
            var mc = stackalloc byte[80];
            new Span<byte>(mc, 80).Fill(0xCC);
            uint mr = MixCaps(0, mc, 80);
            log.Add($"mixer 0 caps {R(mr)}" + (mr == 0 ? $" mid {*(ushort*)mc} pid {*(ushort*)(mc + 2)} version 0x{*(uint*)(mc + 4):x} support 0x{*(uint*)(mc + 72):x} destinations {*(uint*)(mc + 76)}" : ""));
            log.Add($"mixer bad caps {R(MixCaps(mixers + 7, mc, 80))}");
            log.Add("==== error texts");
            var text = stackalloc char[256];
            foreach (uint e in new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 20, 32, 33, 34, 35, 36 })
            {
                uint r = OutText(e, text, 256);
                log.Add($"out error text {e}: {R(r)} \"{(r == 0 ? new string(text) : "")}\"");
                r = InText(e, text, 256);
                log.Add($"in error text {e}: {R(r)} \"{(r == 0 ? new string(text) : "")}\"");
            }
            text[0] = 'x'; text[1] = 'y'; text[2] = 'z'; text[3] = 'w';
            log.Add($"error text of 3 chars {R(OutText(32, text, 3))} \"{new string(text, 0, 4).Replace('\0', '0')}\"");
            log.Add($"error text of no buffer {R(OutText(32, null, 0))}");
            return log;
        }

        private static void Out(List<string> log)
        {
            uint n = OutNum();
            log.Add($"==== waveOut devices {(n > 0 ? "some" : "none")}");
            var caps = stackalloc byte[84];
            foreach (var (what, id) in new[] { ("mapper", (nuint)WAVE_MAPPER), ("device 0", (nuint)0), ("bad device", (nuint)(n + 7)) })
            {
                new Span<byte>(caps, 84).Fill(0xCC);
                uint r = OutCaps(id, caps, 84);
                log.Add($"caps {what} {R(r)}" + (r == 0 ? " " + Caps(caps, false) : ""));
            }
            new Span<byte>(caps, 84).Fill(0xCC);
            log.Add($"caps of 8 bytes {R(OutCaps(0, caps, 8))}: {Convert.ToHexString(new ReadOnlySpan<byte>(caps, 12))}");
            log.Add($"caps of no buffer {R(OutCaps(0, null, 84))}");
            var capsA = stackalloc byte[52];
            uint ra = OutCapsA(0, capsA, 52);
            log.Add($"caps A {R(ra)} formats 0x{*(uint*)(capsA + 40):x} channels {*(ushort*)(capsA + 44)} support 0x{*(uint*)(capsA + 48):x}");

            Formats(log, "out", (fmt, id, fl) => { nint h; return OutOpen(&h, id, fmt, 0, 0, fl); });

            // an open of the mapper, CALLBACK_FUNCTION
            var f = Pcm(1, 2, 44100, 16);
            nint wo = 0;
            log.Add($"open null format {R(OutOpen(&wo, WAVE_MAPPER, null, 0, 0, 0))}");
            log.Add($"open bad flags {R(OutOpen(&wo, WAVE_MAPPER, &f, 0, 0, 0x70000))}");
            log.Add($"open bad device {R(OutOpen(&wo, n + 7, &f, 0, 0, 0))}");
            log.Add($"open function of no function {R(OutOpen(&wo, WAVE_MAPPER, &f, 0, 0x1234, CALLBACK_FUNCTION))}" + (wo != 0 ? " (opened)" : ""));
            if (wo != 0) { OutClose(wo); wo = 0; }
            Messages(0);
            uint or = OutOpen(&wo, WAVE_MAPPER, &f, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, void>)&Callback, 0x1234, CALLBACK_FUNCTION);
            log.Add($"open {R(or)} handle {(wo != 0 ? "set" : "none")}; messages then: {Messages(wo)}");
            if (or != 0) return;
            uint gotId;
            log.Add($"id {R(OutId(wo, &gotId))} {(gotId == WAVE_MAPPER ? "WAVE_MAPPER" : gotId.ToString())}");
            int bytes = 44100 * 4 / 10;                     // 100 ms
            var buf = (byte*)NativeMemory.AllocZeroed((nuint)(bytes * 30));
            var hdrs = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)(sizeof(WAVEHDR) * 4));
            for (int i = 0; i < 4; i++) { hdrs[i].Data = (nint)(buf + i * bytes); hdrs[i].Length = (uint)bytes; hdrs[i].User = 0x55 + i; }
            nint a = (nint)(&hdrs[0]), b = (nint)(&hdrs[1]);
            uint sz = (uint)sizeof(WAVEHDR);
            log.Add($"write unprepared {R(OutWrite(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"prepare of a short size {R(OutPrepare(wo, &hdrs[0], 16))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"prepare {R(OutPrepare(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"prepare again {R(OutPrepare(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            hdrs[1].Flags = 0x8;                        // (a flag the app left: WHDR_BEGINLOOP)
            log.Add($"prepare of flags left {R(OutPrepare(wo, &hdrs[1], sz))} flags 0x{hdrs[1].Flags:x}");
            hdrs[1].Flags = 2;
            log.Add($"write {R(OutWrite(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x} next {(hdrs[0].Next == 0 ? "null" : "set")}");
            log.Add($"write b {R(OutWrite(wo, &hdrs[1], sz))} flags 0x{hdrs[1].Flags:x}");
            log.Add($"write a again {R(OutWrite(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"unprepare queued {R(OutUnprepare(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"close queued {R(OutClose(wo))}");
            bool done = WaitMessages(2, 3000);
            log.Add($"played {done}: flags a 0x{hdrs[0].Flags:x} b 0x{hdrs[1].Flags:x} recorded {hdrs[0].Recorded} user 0x{hdrs[0].User:x}; messages {Messages(wo, a, b)}");
            Positions(log, "after both", wo, false, 2 * bytes, 4, 44100);

            // pause: a write held; restart: played
            log.Add($"pause {R(OutPause(wo))}");
            log.Add($"pause again {R(OutPause(wo))}");
            OutPrepare(wo, &hdrs[2], sz);
            log.Add($"write paused {R(OutWrite(wo, &hdrs[2], sz))}");
            Thread.Sleep(300);
            log.Add($"after 300 ms paused: flags 0x{hdrs[2].Flags:x} messages {Messages(wo)}");
            Positions(log, "paused", wo, false, 2 * bytes, 4, 44100);
            log.Add($"restart {R(OutRestart(wo))}");
            log.Add($"restart again {R(OutRestart(wo))}");
            WaitMessages(1, 3000);
            log.Add($"restarted: flags 0x{hdrs[2].Flags:x} messages {Messages(wo, (nint)(&hdrs[2]))}");
            Positions(log, "after three", wo, false, 3 * bytes, 4, 44100);

            // reset: the buffers queued done at once, the position 0
            hdrs[0].Length = hdrs[1].Length = (uint)(bytes * 10);
            OutWrite(wo, &hdrs[0], sz); OutWrite(wo, &hdrs[1], sz);
            Thread.Sleep(100);
            log.Add($"reset {R(OutReset(wo))} flags a 0x{hdrs[0].Flags:x} b 0x{hdrs[1].Flags:x}");
            WaitMessages(2, 1000);
            log.Add($"reset messages {Messages(wo, a, b)}");
            Positions(log, "after reset", wo, false, 0, 4, 44100);
            log.Add($"reset again {R(OutReset(wo))} messages {Messages(wo)}");
            // a pause, a reset: still paused after?
            OutPause(wo);
            OutReset(wo);
            hdrs[0].Length = (uint)bytes;
            OutWrite(wo, &hdrs[0], sz);
            Thread.Sleep(300);
            log.Add($"written after pause and reset: flags 0x{hdrs[0].Flags:x} (0x3 played: a reset unpauses)");
            OutRestart(wo); WaitMessages(1, 2000); Messages(wo);
            log.Add($"break loop {R(OutBreakLoop(wo))}");

            // the volume, the pitch, the rate
            uint v = 0x12345678;
            log.Add($"volume {R(OutGetVolume(wo, &v))} 0x{v:x}");
            log.Add($"set volume {R(OutSetVolume(wo, 0x40008000))}");
            v = 0; OutGetVolume(wo, &v);
            log.Add($"volume then 0x{v:x}");
            OutSetVolume(wo, 0xFFFFFFFF);
            v = 0x12345678;
            log.Add($"volume of the device id {R(OutGetVolume(0, &v))} 0x{v:x}");
            log.Add($"volume of no pointer {R(OutGetVolume(wo, null))}");
            log.Add($"pitch {R(OutGetPitch(wo, &v))} set {R(OutSetPitch(wo, 0x10000))} rate {R(OutGetRate(wo, &v))} set {R(OutSetRate(wo, 0x10000))}");
            uint pref = 0, prefFlags = 0;
            log.Add($"message preferred {R(OutMessage((nint)WAVE_MAPPER, 0x2015, (nuint)(&pref), (nuint)(&prefFlags)))} id {pref} flags {prefFlags}");
            log.Add($"message unknown {R(OutMessage(wo, 0x7777, 0, 0))}");

            // the headers unprepared, the device closed
            for (int i = 0; i < 3; i++) log.Add($"unprepare {i} {R(OutUnprepare(wo, &hdrs[i], sz))} flags 0x{hdrs[i].Flags:x}");
            log.Add($"unprepare again {R(OutUnprepare(wo, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x}");
            log.Add($"close {R(OutClose(wo))}");
            WaitMessages(1, 1000);
            log.Add($"closed: messages {Messages(wo)}");
            log.Add($"closed handle: write {R(OutWrite(wo, &hdrs[0], sz))} reset {R(OutReset(wo))} close {R(OutClose(wo))}");

            // CALLBACK_EVENT: set at the open, at each done, at the close
            nint ev = NewEvent();
            or = OutOpen(&wo, WAVE_MAPPER, &f, ev, 0, CALLBACK_EVENT);
            log.Add($"open of an event {R(or)}: set {WaitEvent(ev, 500)}");
            OutPrepare(wo, &hdrs[0], sz); OutWrite(wo, &hdrs[0], sz);
            log.Add($"event at the done {WaitEvent(ev, 2000)} flags 0x{hdrs[0].Flags:x}");
            OutUnprepare(wo, &hdrs[0], sz);
            OutClose(wo);
            log.Add($"event at the close {WaitEvent(ev, 500)}");
            CloseEvent(ev);

            // a format of 8-bit mono, 1 s: the position of its bytes and samples
            var f8 = Pcm(1, 1, 8000, 8);
            OutOpen(&wo, WAVE_MAPPER, &f8, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, void>)&Callback, 0x1234, CALLBACK_FUNCTION);
            hdrs[0].Length = 8000 / 5;
            OutPrepare(wo, &hdrs[0], sz); OutWrite(wo, &hdrs[0], sz);
            WaitMessages(2, 2000);
            Thread.Sleep(50);
            Positions(log, "8-bit mono", wo, false, 8000 / 5, 1, 8000);
            OutUnprepare(wo, &hdrs[0], sz); OutClose(wo);
            WaitMessages(1, 1000); Messages(wo);
            NativeMemory.Free(buf); NativeMemory.Free(hdrs);
        }

        private delegate uint Opener(void* fmt, uint id, uint flags);

        /// <summary>Which formats an open takes (WAVE_FORMAT_QUERY), of the mapper and of device 0.</summary>
        private static void Formats(List<string> log, string dir, Opener open)
        {
            var cases = new List<(string, byte[])>();
            void Add(string what, WAVEFORMATEX f) { var b = new byte[sizeof(WAVEFORMATEX)]; fixed (byte* p = b) *(WAVEFORMATEX*)p = f; cases.Add((what, b)); }
            void AddX(string what, WAVEFORMATEXTENSIBLE f) { var b = new byte[sizeof(WAVEFORMATEXTENSIBLE)]; fixed (byte* p = b) *(WAVEFORMATEXTENSIBLE*)p = f; cases.Add((what, b)); }
            foreach (int bits in new[] { 8, 16, 24, 32 })
                foreach (int rate in new[] { 8000, 11025, 22050, 44100, 48000, 96000, 192000 })
                    foreach (int ch in new[] { 1, 2 })
                        Add($"pcm {bits} {rate} {ch}", Pcm(1, ch, rate, bits));
            foreach (int ch in new[] { 4, 6, 8 }) Add($"pcm 16 48000 {ch}", Pcm(1, ch, 48000, 16));
            Add("pcm 16 1000 2", Pcm(1, 2, 1000, 16));
            Add("pcm 16 384000 2", Pcm(1, 2, 384000, 16));
            Add("pcm 12 44100 2", Pcm(1, 2, 44100, 12));
            Add("pcm 16 44100 0", Pcm(1, 0, 44100, 16));
            var odd = Pcm(1, 2, 44100, 16); odd.BlockAlign = 3; Add("pcm 16 of block 3", odd);
            odd = Pcm(1, 2, 44100, 16); odd.AvgBytes = 1000; Add("pcm 16 of avg 1000", odd);
            odd = Pcm(1, 2, 44100, 16); odd.CbSize = 22; Add("pcm 16 of cbSize 22", odd);
            Add("float 32 48000 2", Pcm(3, 2, 48000, 32));
            Add("float 32 44100 1", Pcm(3, 1, 44100, 32));
            Add("float 64 48000 2", Pcm(3, 2, 48000, 64));
            Add("float 16 48000 2", Pcm(3, 2, 48000, 16));
            var adpcm = Pcm(2, 2, 44100, 4); adpcm.BlockAlign = 2048; adpcm.AvgBytes = 44359; Add("adpcm", adpcm);
            Add("alaw 8000 1", Pcm(6, 1, 8000, 8));
            Add("mulaw 8000 1", Pcm(7, 1, 8000, 8));
            Add("tag 0x55 (mp3)", Pcm(0x55, 2, 44100, 0));
            var pcmSub = new Guid("00000001-0000-0010-8000-00aa00389b71"); var flSub = new Guid("00000003-0000-0010-8000-00aa00389b71");
            WAVEFORMATEXTENSIBLE X(int ch, int rate, int bits, int valid, uint mask, Guid sub) { var x = new WAVEFORMATEXTENSIBLE { F = Pcm(0xFFFE, ch, rate, bits), ValidBits = (ushort)valid, Mask = mask, Sub = sub }; x.F.CbSize = 22; return x; }
            AddX("ext pcm 16 48000 2", X(2, 48000, 16, 16, 3, pcmSub));
            AddX("ext pcm 24in32 48000 2", X(2, 48000, 32, 24, 3, pcmSub));
            AddX("ext pcm 24 48000 2", X(2, 48000, 24, 24, 3, pcmSub));
            AddX("ext float 48000 2", X(2, 48000, 32, 32, 3, flSub));
            AddX("ext float 48000 6", X(6, 48000, 32, 32, 0x3F, flSub));
            AddX("ext pcm 16 48000 2 of mask 0", X(2, 48000, 16, 16, 0, pcmSub));
            AddX("ext pcm 16 48000 2 of mask 7", X(2, 48000, 16, 16, 7, pcmSub));
            var badCb = X(2, 48000, 16, 16, 3, pcmSub); badCb.F.CbSize = 0; AddX("ext of cbSize 0", badCb);
            AddX("ext of an unknown subtype", X(2, 48000, 16, 16, 3, new Guid("11111111-2222-3333-4444-555555555555")));
            foreach (var (what, b) in cases)
                fixed (byte* p = b)
                {
                    uint m = open(p, WAVE_MAPPER, WAVE_FORMAT_QUERY), d = open(p, 0, WAVE_FORMAT_QUERY), dd = open(p, 0, WAVE_FORMAT_QUERY | WAVE_FORMAT_DIRECT);
                    log.Add($"{dir} query {what}: mapper {R(m)} device {R(d)} direct {R(dd)}");
                }
        }

        /// <summary>The position in each time format asked: the format given back, the value as of the bytes expected.</summary>
        private static void Positions(List<string> log, string when, nint h, bool input, int bytes, int block, int rate)
        {
            var t = stackalloc byte[12];
            foreach (var (name, type) in new[] { ("MS", 1u), ("SAMPLES", 2u), ("BYTES", 4u), ("SMPTE", 8u), ("MIDI", 0x10u), ("TICKS", 0x20u) })
            {
                new Span<byte>(t, 12).Clear();
                *(uint*)t = type;
                uint r = input ? InPosition(h, t, 12) : OutPosition(h, t, 12);
                uint got = *(uint*)t, value = *(uint*)(t + 4);
                long expect = got switch { 1 => (long)bytes / block * 1000 / rate, 2 => bytes / block, 4 => bytes, _ => -1 };
                string v = expect < 0 ? $"0x{value:x}" : value == expect ? "as expected" : Math.Abs(value - expect) <= Math.Max(expect / 50, 2) ? "within 2%" : $"{value} of {expect}";
                log.Add($"position {when} {name}: {R(r)} as {got switch { 1 => "MS", 2 => "SAMPLES", 4 => "BYTES", 8 => "SMPTE", 0x10 => "MIDI", 0x20 => "TICKS", _ => got.ToString() }} {v}");
            }
            new Span<byte>(t, 12).Clear(); *(uint*)t = 4;
            log.Add($"position {when} of 8 bytes: {R(input ? InPosition(h, t, 8) : OutPosition(h, t, 8))}");
        }

        private static void In(List<string> log)
        {
            uint n = InNum();
            log.Add($"==== waveIn devices {(n > 0 ? "some" : "none")}");
            var caps = stackalloc byte[80];
            foreach (var (what, id) in new[] { ("mapper", (nuint)WAVE_MAPPER), ("device 0", (nuint)0), ("bad device", (nuint)(n + 7)) })
            {
                new Span<byte>(caps, 80).Fill(0xCC);
                uint r = InCaps(id, caps, 80);
                log.Add($"caps {what} {R(r)}" + (r == 0 ? " " + Caps(caps, true) : ""));
            }
            if (n == 0) return;
            Formats(log, "in", (fmt, id, fl) => { nint h; return InOpen(&h, id, fmt, 0, 0, fl); });
            var f = Pcm(1, 1, 44100, 16);
            nint wi = 0;
            Messages(0);
            uint or = InOpen(&wi, WAVE_MAPPER, &f, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, void>)&Callback, 0x1234, CALLBACK_FUNCTION);
            log.Add($"in open {R(or)}; messages then: {Messages(wi)}");
            if (or != 0) return;
            uint gotId;
            log.Add($"in id {R(InId(wi, &gotId))} {(gotId == WAVE_MAPPER ? "WAVE_MAPPER" : gotId.ToString())}");
            int bytes = 44100 * 2 / 10;
            var buf = (byte*)NativeMemory.AllocZeroed((nuint)(bytes * 12));
            var hdrs = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)(sizeof(WAVEHDR) * 4));
            for (int i = 0; i < 4; i++) { hdrs[i].Data = (nint)(buf + i * bytes * 3); hdrs[i].Length = (uint)bytes; hdrs[i].Recorded = 77; }
            nint a = (nint)(&hdrs[0]), b = (nint)(&hdrs[1]);
            uint sz = (uint)sizeof(WAVEHDR);
            log.Add($"in add unprepared {R(InAdd(wi, &hdrs[0], sz))}");
            for (int i = 0; i < 4; i++) InPrepare(wi, &hdrs[i], sz);
            log.Add($"in prepared flags 0x{hdrs[0].Flags:x} recorded {hdrs[0].Recorded}");
            log.Add($"in add {R(InAdd(wi, &hdrs[0], sz))} flags 0x{hdrs[0].Flags:x} recorded {hdrs[0].Recorded}");
            log.Add($"in add b {R(InAdd(wi, &hdrs[1], sz))}");
            log.Add($"in add a again {R(InAdd(wi, &hdrs[0], sz))}");
            Thread.Sleep(250);
            log.Add($"not started after 250 ms: flags 0x{hdrs[0].Flags:x} messages {Messages(wi)}");
            Positions(log, "in before start", wi, true, 0, 2, 44100);
            log.Add($"in start {R(InStart(wi))}");
            log.Add($"in start again {R(InStart(wi))}");
            WaitMessages(2, 3000);
            log.Add($"in recorded: flags a 0x{hdrs[0].Flags:x} b 0x{hdrs[1].Flags:x} recorded a {(hdrs[0].Recorded == bytes ? "full" : hdrs[0].Recorded.ToString())} b {(hdrs[1].Recorded == bytes ? "full" : hdrs[1].Recorded.ToString())}; messages {Messages(wi, a, b)}");
            Thread.Sleep(200);
            Positions(log, "in running", wi, true, 2 * bytes, 2, 44100);
            log.Add($"in stop {R(InStop(wi))}");
            log.Add($"in stop again {R(InStop(wi))}");
            // a buffer added while stopped: kept; stop of a buffer begun: it returned partial
            InAdd(wi, &hdrs[2], sz);
            Thread.Sleep(200);
            log.Add($"in stopped after 200 ms: flags 0x{hdrs[2].Flags:x} messages {Messages(wi)}");
            hdrs[2].Length = (uint)(bytes * 3);
            InStart(wi);
            Thread.Sleep(150);
            log.Add($"in stop with one begun {R(InStop(wi))}");
            WaitMessages(1, 1000);
            log.Add($"in stopped: flags 0x{hdrs[2].Flags:x} recorded {(hdrs[2].Recorded > 0 && hdrs[2].Recorded < bytes * 3 ? "part" : hdrs[2].Recorded.ToString())} of whole frames {hdrs[2].Recorded % 2 == 0}; messages {Messages(wi, (nint)(&hdrs[2]))}");
            // reset: queued ones back of nothing recorded, the position 0
            hdrs[3].Recorded = 77;
            InAdd(wi, &hdrs[3], sz);
            log.Add($"in reset {R(InReset(wi))} flags 0x{hdrs[3].Flags:x} recorded {hdrs[3].Recorded}");
            WaitMessages(1, 1000);
            log.Add($"in reset messages {Messages(wi, (nint)(&hdrs[3]))}");
            Positions(log, "in after reset", wi, true, 0, 2, 44100);
            for (int i = 0; i < 4; i++) InUnprepare(wi, &hdrs[i], sz);
            log.Add($"in unprepared flags 0x{hdrs[0].Flags:x}");
            log.Add($"in message unknown {R(InMessage(wi, 0x7777, 0, 0))}");
            log.Add($"in close {R(InClose(wi))}");
            WaitMessages(1, 1000);
            log.Add($"in closed: messages {Messages(wi)}");
            log.Add($"in closed handle: start {R(InStart(wi))}");
            NativeMemory.Free(buf); NativeMemory.Free(hdrs);
        }

        [Fact]
        public void Probe()
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("WAVE_PROBE") is not { Length: > 0 }, "WAVE_PROBE=<file> writes the report");
            System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("WAVE_PROBE")!, Report());
        }

        /// <summary>Ours as Windows' (Wave.windows.txt): where there is an output.</summary>
        [Fact]
        public void TheWaveFunctions_BehaveAsWindowsDo()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' winmm is the fixture");
            var ours = Report();
            Assert.SkipWhen(ours.Contains("==== waveOut devices none"), "no audio output here");
            var windows = System.IO.File.ReadAllLines(System.IO.Path.Combine(TestFiles.Root, "MediaFoundation", "Wave.windows.txt")).ToList();
            if (ours.Contains("==== waveIn devices none"))
            {
                // no microphone: the waveIn part of Windows' left out, ours of no device
                int from = windows.FindIndex(x => x.StartsWith("==== waveIn")), to = windows.FindIndex(x => x.StartsWith("==== mixers"));
                windows.RemoveRange(from, to - from);
                windows.InsertRange(from, new[] { "==== waveIn devices none", "caps mapper MMSYSERR_BADDEVICEID", "caps device 0 MMSYSERR_BADDEVICEID", "caps bad device MMSYSERR_BADDEVICEID" });
            }
            for (int i = 0; i < Math.Min(windows.Count, ours.Count); i++)
                Assert.True(windows[i] == ours[i], $"line {i}: Windows '{windows[i]}', ours '{ours[i]}'");
            Assert.Equal(windows.Count, ours.Count);
        }
    }
}
