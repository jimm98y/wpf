// A clock for the things that move.
//
// Windows 11 does not draw a static control and leave it there: a scroll bar's thumb widens when the
// pointer arrives and narrows when it leaves, a progress bar carries a highlight along itself, a
// button's face settles into its hover colour. Every one of those needs the control repainted over
// and over for a while, and nothing else is going to ask for it.
//
// One timer serves the whole application. A control registers an animation under a key, reads a
// value between 0 and 1 while painting, and does nothing else; the timer invalidates whatever has a
// live animation and stops itself once nothing does, so an idle application is idle.
//
// The theme is the natural caller: it knows a control's state at paint time, so it can compare that
// state to where the animation is heading and turn it round when they disagree. That keeps the
// controls themselves free of animation code.

using System;
using System.Collections.Generic;
using System.Drawing;

namespace System.Windows.Forms
{
    internal static class Animation
    {
        /// <summary>How long a hover fade takes. Windows is quick about it -- long enough to read as
        /// a transition, short enough never to feel in the way.</summary>
        internal const int HoverMilliseconds = 150;

        private sealed class Track
        {
            internal Control Owner;
            internal double From, To;
            internal int Start, Duration;
            internal bool Looping;
            internal int Period;
            /// <summary>The part of the control this animation actually changes, or null for
            /// all of it. A progress bar's highlight is a narrow band travelling along a wide
            /// control; repainting the whole thing sixty times a second to move it would be
            /// most of the work for none of the result.</summary>
            internal Func<Rectangle> Region;
        }

        private static readonly Dictionary<Control, Dictionary<object, Track>> s_tracks =
            new Dictionary<Control, Dictionary<object, Track>>();
        private static Timer s_timer;

        private static int Now => Environment.TickCount;

        private static Track Find(Control owner, object key, bool create)
        {
            Dictionary<object, Track> byKey;
            if (!s_tracks.TryGetValue(owner, out byKey))
            {
                if (!create)
                    return null;
                s_tracks[owner] = byKey = new Dictionary<object, Track>();
            }
            Track track;
            if (!byKey.TryGetValue(key, out track) && create)
                byKey[key] = track = new Track { Owner = owner };
            return track;
        }

        /// <summary>Head for <paramref name="target"/> (0 or 1) over <paramref name="milliseconds"/>,
        /// starting from wherever the animation has got to. Calling it again with the same target
        /// changes nothing, so a theme can say what it wants on every paint without restarting the
        /// fade each time.</summary>
        internal static void To(Control owner, object key, double target, int milliseconds)
        {
            To(owner, key, target, milliseconds, null);
        }

        internal static void To(Control owner, object key, double target, int milliseconds,
            Func<Rectangle> region)
        {
            if (owner == null || owner.IsDisposed)
                return;
            lock (s_tracks)
            {
                Track track = Find(owner, key, true);
                if (track.Duration > 0 && track.To == target)
                    return;
                double current = ValueOf(track);
                if (current == target && track.Duration > 0)
                    return;
                track.From = current;
                track.To = target;
                track.Start = Now;
                track.Duration = Math.Max(1, milliseconds);
                track.Looping = false;
                track.Region = region;
                Wake();
            }
        }

        /// <summary>Run round and round with the given period, for something that never settles --
        /// a progress bar's travelling highlight. The value is the phase, 0 to 1.</summary>
        internal static void Loop(Control owner, object key, int period)
        {
            Loop(owner, key, period, null);
        }

        internal static void Loop(Control owner, object key, int period, Func<Rectangle> region)
        {
            if (owner == null || owner.IsDisposed)
                return;
            lock (s_tracks)
            {
                Track track = Find(owner, key, true);
                track.Looping = true;
                track.Period = Math.Max(1, period);
                track.Region = region;
                Wake();
            }
        }

        internal static void Stop(Control owner, object key)
        {
            lock (s_tracks)
            {
                Dictionary<object, Track> byKey;
                if (s_tracks.TryGetValue(owner, out byKey))
                {
                    byKey.Remove(key);
                    if (byKey.Count == 0)
                        s_tracks.Remove(owner);
                }
            }
        }

        /// <summary>Forget everything a control had running, when it goes away.</summary>
        internal static void Forget(Control owner)
        {
            lock (s_tracks)
                s_tracks.Remove(owner);
        }

        /// <summary>Where the animation has got to: 0 to 1, eased, or the phase when looping. Zero
        /// for an animation that was never started, which is what an unhovered control wants.</summary>
        internal static double Value(Control owner, object key)
        {
            lock (s_tracks)
            {
                Track track = Find(owner, key, false);
                return track == null ? 0.0 : ValueOf(track);
            }
        }

        private static double ValueOf(Track track)
        {
            if (track.Looping)
                return (Now % track.Period) / (double)track.Period;
            if (track.Duration <= 0)
                return track.To;
            double t = (Now - track.Start) / (double)track.Duration;
            if (t >= 1.0)
                return track.To;
            if (t <= 0.0)
                return track.From;
            // Smooth at both ends, so the fade neither jumps into motion nor stops dead.
            double eased = t * t * (3.0 - 2.0 * t);
            return track.From + (track.To - track.From) * eased;
        }

        private static bool Live(Track track)
        {
            return track.Looping || Now - track.Start < track.Duration;
        }

        private static void Wake()
        {
            if (s_timer != null)
                return;
            s_timer = new Timer { Interval = 16 };      // about sixty frames a second
            s_timer.Tick += delegate { Tick(); };
            s_timer.Start();
        }

        private static void Tick()
        {
            // Control, and the part of it to repaint -- an empty rectangle meaning all of it.
            var repaint = new List<KeyValuePair<Control, Rectangle>>();
            bool any = false;
            lock (s_tracks)
            {
                var finished = new List<Control>();
                foreach (var pair in s_tracks)
                {
                    Control owner = pair.Key;
                    if (owner.IsDisposed)
                    {
                        finished.Add(owner);
                        continue;
                    }
                    bool live = false;
                    bool whole = false;
                    Rectangle area = Rectangle.Empty;
                    foreach (Track track in pair.Value.Values)
                    {
                        if (!Live(track))
                            continue;
                        live = true;
                        if (track.Region == null)
                        {
                            whole = true;
                            continue;
                        }
                        try
                        {
                            Rectangle part = track.Region();
                            area = area.IsEmpty ? part : Rectangle.Union(area, part);
                        }
                        catch (Exception) { whole = true; }
                    }
                    if (live)
                    {
                        any = true;
                        if (owner.IsHandleCreated && owner.Visible)
                            repaint.Add(new KeyValuePair<Control, Rectangle>(
                                owner, whole || area.IsEmpty ? Rectangle.Empty : area));
                    }
                }
                foreach (Control gone in finished)
                    s_tracks.Remove(gone);

                // Nothing moving: stop the timer rather than burn a frame doing nothing. The next
                // To or Loop starts it again.
                if (!any && s_timer != null)
                {
                    s_timer.Stop();
                    s_timer.Dispose();
                    s_timer = null;
                }
            }

            foreach (var pair in repaint)
            {
                try
                {
                    if (pair.Value.IsEmpty)
                        pair.Key.Invalidate();
                    else
                        pair.Key.Invalidate(pair.Value);
                }
                catch (Exception) { }
            }
        }
    }
}
