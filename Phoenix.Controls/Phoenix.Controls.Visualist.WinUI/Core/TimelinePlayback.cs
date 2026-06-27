using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.WinUI.Core
{
    /// <summary>
    /// TimelinePlayback — owns design-time playback state for one
    /// <see cref="WidgetTimeline"/>. Drives a ~30Hz tick that advances the
    /// playhead and fires <see cref="OnTimeChanged"/>; consumers
    /// (the timeline transport in <c>WidgetEditorView</c> and any preview
    /// scrub bridge) react.
    ///
    /// Real elapsed time (<see cref="Stopwatch"/>) drives advancement so the
    /// playhead doesn't drift if a timer tick is delayed by a busy UI thread.
    ///
    /// Production rendering is browser-side (compositor.js); this class never
    /// renders. It just owns the time cursor and the play/pause state.
    ///
    /// (Ported from the pre-T15 WinForms baseline
    /// <c>Phoenix.Controls.Visualist/Core/TimelinePlayback.cs</c> during the
    /// Visualist WinUI parity restoration — regression audit 2026-05-31,
    /// Lane A. The only platform change is the ticker:
    /// <c>System.Windows.Forms.Timer</c> → WinUI <see cref="DispatcherTimer"/>.
    /// Like the WinForms timer, <see cref="DispatcherTimer"/> must be created
    /// on a thread with a dispatcher — construct this on the UI thread.)
    /// </summary>
    public sealed class TimelinePlayback : IDisposable
    {
        private readonly DispatcherTimer _ticker;
        private readonly Stopwatch _watch = new();
        private long _lastElapsedMs;

        private double _timeMs;
        private bool _isPlaying;
        private bool _disposed;

        public WidgetTimeline? Timeline { get; set; }
        public bool Loop { get; set; }

        public double TimeMs
        {
            get => _timeMs;
            private set
            {
                if (Math.Abs(_timeMs - value) < 0.0001) return;
                _timeMs = Math.Max(0, value);
                OnTimeChanged?.Invoke(_timeMs);
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                OnPlayingChanged?.Invoke(_isPlaying);
            }
        }

        public event Action<double>? OnTimeChanged;
        public event Action<bool>?   OnPlayingChanged;

        public TimelinePlayback()
        {
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30Hz
            _ticker.Tick += OnTick;
        }

        public void Play()
        {
            if (_disposed) return;
            if (Timeline is null || Timeline.DurationMs <= 0) return;
            // From a stopped position past the end, restart from 0 so Play means "go".
            if (_timeMs >= Timeline.DurationMs) TimeMs = 0;
            _watch.Restart();
            _lastElapsedMs = 0;
            IsPlaying = true;
            _ticker.Start();
        }

        public void Pause()
        {
            if (_disposed) return;
            _ticker.Stop();
            _watch.Stop();
            IsPlaying = false;
        }

        public void Stop()
        {
            if (_disposed) return;
            _ticker.Stop();
            _watch.Stop();
            IsPlaying = false;
            TimeMs = 0;
        }

        public void ScrubTo(double timeMs)
        {
            if (_disposed) return;
            // Scrub-while-playing is allowed: snap and keep playing from the new
            // position. Restart the watch so subsequent ticks compute deltas
            // against the new origin instead of the pre-scrub one.
            TimeMs = Math.Max(0, timeMs);
            if (_isPlaying)
            {
                _watch.Restart();
                _lastElapsedMs = 0;
            }
        }

        // DispatcherTimer.Tick is EventHandler<object> (sender, object e) — unlike
        // the WinForms Timer's (sender, EventArgs e). Only the event-arg type
        // differs; the body is unchanged from the baseline.
        private void OnTick(object? sender, object e)
        {
            if (Timeline is null || Timeline.DurationMs <= 0) { Pause(); return; }

            long now = _watch.ElapsedMilliseconds;
            long delta = now - _lastElapsedMs;
            _lastElapsedMs = now;

            double next = _timeMs + delta;
            double dur = Timeline.DurationMs;

            if (next >= dur)
            {
                if (Loop)
                {
                    // Wrap by modulo so a long-deferred tick doesn't skip past a frame's worth.
                    next = next % dur;
                    TimeMs = next;
                }
                else
                {
                    TimeMs = dur;
                    Pause();
                }
            }
            else
            {
                TimeMs = next;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            // Clear the playing flag (and notify subscribers) before flipping the
            // disposed gate so a Pause/Stop subscriber can react before listeners
            // detach. Time stays where it is — disposing isn't equivalent to Stop.
            IsPlaying = false;
            _disposed = true;
            // DispatcherTimer is not IDisposable (unlike WinForms.Timer); stop it and
            // detach the handler so it can't fire after disposal and the instance is
            // free to collect.
            try { _ticker.Stop(); } catch { }
            try { _ticker.Tick -= OnTick; } catch { }
            _watch.Stop();
        }
    }
}
