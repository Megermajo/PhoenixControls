using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;            // WPF MediaPlayer (UseWPF=true)
using System.Windows.Threading;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// P3 — Local audio output for Script (audio.play / audio.play_tts /
    /// audio.set_volume). Owns:
    ///
    ///   * a single STA worker thread + Dispatcher so WPF MediaPlayer instances can
    ///     be constructed and driven without depending on Application.Current
    ///     (Hub is WinForms — there is no WPF Application). MediaPlayer requires a
    ///     thread with a Dispatcher; the worker thread runs Dispatcher.Run() for
    ///     the lifetime of the process.
    ///   * a global base-volume multiplier set by audio.set_volume; applied on top
    ///     of each per-call Volume so streamers can duck everything at once.
    ///   * an active-players list so MediaPlayer / SpeechSynthesizer instances stay
    ///     rooted until playback finishes (otherwise GC may dispose mid-play).
    ///
    /// Design choices:
    ///   * audio.play is fire-and-forget: PlayAsync resolves once playback has been
    ///     dispatched to begin, NOT on MediaEnded. Streamers chain alerts and
    ///     scripts shouldn't stall on a 30s sting.
    ///   * audio.play_tts is fire-and-forget for the same reason — multiple TTS
    ///     calls overlap; we never auto-stop a previous synthesizer.
    ///   * audio.set_volume persists in process memory only (no DB write per spec).
    /// </summary>
    internal static class AudioService
    {
        private static readonly object _lock = new();
        private static double _baseVolume = 1.0;

        // Active players/synths — kept rooted so they aren't GC'd while playing.
        // Cleaned up by the per-instance MediaEnded/MediaFailed/SpeakCompleted handlers.
        //
        // MediaPlayer is stored alongside the original per-call volume
        // (the 0..1 multiplier the script passed in, BEFORE base-volume application).
        // SetBaseVolume needs both to re-derive effective volume on the fly when the
        // streamer ducks mid-playback — without the per-call value we'd lose the
        // "make this one alert quieter" intent on every base-volume change.
        private static readonly Dictionary<MediaPlayer, double> _activePlayers = new();
        private static readonly HashSet<SpeechSynthesizer>      _activeSpeakers = new();

        // Lazy STA dispatcher worker. WPF MediaPlayer needs a Dispatcher thread.
        // Hub is WinForms-first; there is no WPF Application.Current, so we own one.
        private static Dispatcher? _dispatcher;
        private static Thread?     _staThread;
        private static readonly object _dispatcherLock = new();

        private static Dispatcher GetDispatcher()
        {
            var d = _dispatcher;
            if (d != null) return d;
            lock (_dispatcherLock)
            {
                if (_dispatcher != null) return _dispatcher;

                using var ready = new ManualResetEventSlim(false);
                Dispatcher? created = null;
                var t = new Thread(() =>
                {
                    created = Dispatcher.CurrentDispatcher; // forces creation on this thread

                    // Keep the dispatcher loop alive after a faulting handler.
                    // Without UnhandledException = Handled, any throw from a MediaEnded /
                    // MediaFailed / dispatch lambda would tear down Dispatcher.Run() and
                    // brick every subsequent audio.play / audio.play_tts call until Hub
                    // restarts. Logging is best-effort; even if it throws we still mark
                    // Handled so the loop keeps pumping.
                    created.UnhandledException += (s, e) =>
                    {
                        try
                        {
                            GlobalLogger.Error("Audio",
                                "AudioService dispatcher handler threw — loop preserved", e.Exception);
                        }
                        catch { /* never let a logger fault break the dispatcher */ }
                        e.Handled = true;
                    };

                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "PhoenixAudioDispatcher",
                };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                ready.Wait();
                _dispatcher = created!;
                _staThread  = t;
                return _dispatcher;
            }
        }

        /// <summary>
        /// Graceful teardown for the STA dispatcher thread. Safe to call
        /// from any thread; idempotent. Posts <see cref="Dispatcher.InvokeShutdown"/>
        /// onto the dispatcher itself so the worker thread exits its
        /// <see cref="Dispatcher.Run"/> loop without a forced abort.
        ///
        /// Called from HubBootstrapper shutdown (slice 17). Until then the worker
        /// thread is IsBackground = true so process exit will reap it.
        /// </summary>
        public static void Shutdown()
        {
            Dispatcher? d;
            Thread?     t;
            lock (_dispatcherLock)
            {
                d = _dispatcher;
                t = _staThread;
                _dispatcher = null;
                _staThread  = null;
            }
            if (d is null) return;
            try { d.InvokeShutdown(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("Audio", "AudioService.Shutdown failed", ex);
            }

            // Bounded join so a wedged Dispatcher thread can't keep the
            // test host (or a slow Hub teardown) alive indefinitely. 2s is well
            // beyond a healthy InvokeShutdown drain; anything longer than that is
            // a wedge that we'd rather abandon than wait on.
            if (t is not null && t.IsAlive)
            {
                try
                {
                    if (!t.Join(TimeSpan.FromSeconds(2)))
                    {
                        GlobalLogger.Log(
                            "AudioService dispatcher thread did not exit within 2s; abandoning.",
                            "Audio", LogLevel.Communication);
                    }
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("Audio", "AudioService.Shutdown join failed", ex);
                }
            }
        }

        private static double Clamp01(double v) =>
            double.IsNaN(v) ? 0.0 : Math.Max(0.0, Math.Min(1.0, v));

        public static void SetBaseVolume(double v)
        {
            double newBase;
            int activeCount;
            lock (_lock)
            {
                _baseVolume = Clamp01(v);
                newBase     = _baseVolume;
                activeCount = _activePlayers.Count;
            }

            // Duck currently-playing audio in addition to future plays.
            // The class XML doc-comment promises "duck everything at once"; before
            // this fix SetBaseVolume only affected the volume snapshot taken at
            // the next PlayAsync call, leaving any in-flight sting / TTS at its
            // original level. Walk each rooted MediaPlayer and reapply per-call
            // * newBase. Marshal onto the audio Dispatcher because MediaPlayer
            // properties are thread-affined to the dispatcher that created it.
            //
            // Re-read _activePlayers under _lock from INSIDE the
            // dispatcher callback rather than sending a snapshot captured on the
            // caller's thread. BeginInvoke runs asynchronously, so a player present
            // in a caller-side snapshot can be disposed by DisposePlayer (which
            // removes it from _activePlayers) before the callback runs — touching
            // its Volume would then race a closed MediaPlayer. Reading the live
            // dictionary at apply-time means disposed players are already gone and
            // the per-call value matches the still-rooted player.
            //
            // NOTE: SAPI SpeechSynthesizer.Volume is set at construction time and
            // mid-utterance volume changes are not supported by SAPI itself, so
            // active TTS keeps its original ducking. Future calls pick up the new
            // base via PlayTtsAsync's read of GetBaseVolume(). Same "duck future"
            // semantics as before for TTS, on-the-fly ducking for MediaPlayer.
            if (activeCount > 0)
            {
                Dispatcher? d;
                lock (_dispatcherLock) { d = _dispatcher; }
                if (d is not null && !d.HasShutdownStarted)
                {
                    d.BeginInvoke(() =>
                    {
                        List<KeyValuePair<MediaPlayer, double>> live;
                        lock (_lock)
                        {
                            live = new List<KeyValuePair<MediaPlayer, double>>(_activePlayers);
                        }
                        foreach (var kv in live)
                        {
                            try { kv.Key.Volume = Clamp01(kv.Value) * newBase; }
                            catch { /* player may have ended/closed mid-iteration */ }
                        }
                    });
                }
            }
        }

        public static double GetBaseVolume()
        {
            lock (_lock) { return _baseVolume; }
        }

        /// <summary>
        /// Fire-and-forget: returns once playback has been dispatched to begin.
        /// MediaPlayer is rooted in <see cref="_activePlayers"/> until MediaEnded /
        /// MediaFailed clean it up.
        /// </summary>
        public static Task PlayAsync(string path, double volume, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is empty.", nameof(path));

            ct.ThrowIfCancellationRequested();

            double perCall  = Clamp01(volume);
            double effective = perCall * GetBaseVolume();
            var dispatcher = GetDispatcher();

            // Defensive watchdog. If the dispatcher has been shut down
            // (Hub teardown raced ahead of a fire-and-forget audio.play call), the
            // legacy code would queue a work item that never ran AND return a Task
            // that never completed, hanging the script forever. Fail fast with a
            // clear message instead — the caller surfaces it as result.audio_error.
            if (dispatcher.HasShutdownStarted)
                throw new InvalidOperationException("AudioService dispatcher is shut down");

            // Kick off on the STA dispatcher; we wait only for construction +
            // Open + Play to be queued — not for MediaEnded.
            //
            // The HasShutdownStarted check above is a TOCTOU best-effort:
            // Shutdown() can win the race between the check and this InvokeAsync,
            // in which case InvokeAsync itself throws InvalidOperationException.
            // Convert that into the same controlled fast-fail message the explicit
            // shutdown guard emits so the caller surfaces a clean result.audio_error
            // instead of a raw dispatcher exception.
            DispatcherOperation op;
            try
            {
                op = dispatcher.InvokeAsync(() =>
                {
                    MediaPlayer? player = null;
                    try
                    {
                        player = new MediaPlayer { Volume = effective };
                        var uri = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute);

                        player.MediaEnded  += (_, __) => DisposePlayer(player);
                        player.MediaFailed += (_, ev) =>
                        {
                            GlobalLogger.Log($"audio.play({path}) failed: {ev.ErrorException?.Message ?? "unknown"}",
                                "Audio", LogLevel.CriticalError);
                            DisposePlayer(player);
                        };

                        // Root the player AND its per-call volume so a later
                        // SetBaseVolume can re-derive effective volume without losing
                        // the script's original ducking intent.
                        lock (_lock) { _activePlayers[player] = perCall; }
                        player.Open(uri);
                        player.Play();
                    }
                    catch (Exception)
                    {
                        if (player != null) DisposePlayer(player);
                        throw;
                    }
                });
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("AudioService dispatcher is shut down");
            }

            // Surface synchronous construction errors (bad path / unsupported uri
            // schemes) back to the caller so result.audio_error gets a real message.
            return op.Task;
        }

        private static void DisposePlayer(MediaPlayer player)
        {
            try
            {
                lock (_lock) { _activePlayers.Remove(player); }
                player.Close();
            }
            catch { /* swallow — disposal is best-effort */ }
        }

        // Test/diagnostic surface. Returns the count of currently rooted
        // MediaPlayer instances so unit tests can verify SetBaseVolume's "duck
        // everything at once" iteration without subscribing to internal events.
        internal static int ActivePlayerCount
        {
            get { lock (_lock) { return _activePlayers.Count; } }
        }

        /// <summary>
        /// Fire-and-forget Windows TTS. New calls do NOT stop prior synthesizers;
        /// chained alerts overlap by design. Voice selection failures fall back to
        /// the system default and are logged, not raised.
        /// </summary>
        public static Task PlayTtsAsync(string text, string? voice, int rate, double volume, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(text))
                throw new ArgumentException("Text is empty.", nameof(text));

            ct.ThrowIfCancellationRequested();

            int clampedRate = Math.Max(-10, Math.Min(10, rate));
            int clampedVol  = (int)Math.Round(Clamp01(volume) * Clamp01(GetBaseVolume()) * 100.0);
            // SpeechSynthesizer.Volume is 0..100; multiplying the per-call factor
            // by the global base preserves the same "global multiplier" semantics
            // as audio.play (MediaPlayer.Volume is 0..1, math is identical).

            // SpeechSynthesizer construction is cheap and not Dispatcher-bound, so we
            // run it on the threadpool. SpeakAsync queues internally; we attach a
            // cleanup handler and return immediately.
            return Task.Run(() =>
            {
                SpeechSynthesizer?       synth = null;
                CancellationTokenRegistration ctReg = default;
                bool registrationHandedOff = false;
                try
                {
                    synth = new SpeechSynthesizer
                    {
                        Rate   = clampedRate,
                        Volume = clampedVol,
                    };

                    if (!string.IsNullOrWhiteSpace(voice))
                    {
                        try { synth.SelectVoice(voice); }
                        catch (Exception vex)
                        {
                            GlobalLogger.Log(
                                $"audio.play_tts: voice '{voice}' not installed ({vex.Message}); using default.",
                                "Audio", LogLevel.Communication);
                        }
                    }

                    synth.SetOutputToDefaultAudioDevice();

                    var localSynth = synth;
                    localSynth.SpeakCompleted += (_, __) => DisposeSynth(localSynth);

                    // Cooperative cancellation. Speech.Synthesis.SpeechSynthesizer
                    // doesn't accept a CancellationToken on SpeakAsync; the only way to
                    // abort an in-flight TTS is SpeakAsyncCancelAll. Register a callback
                    // on ct so a cancelled script actually stops talking instead of
                    // continuing past the abort. The registration is disposed on
                    // SpeakCompleted (happy path) AND on the exception path below
                    // (sad path — synth.SetOutput / SpeakAsync throws after registration).
                    ctReg = ct.Register(() =>
                    {
                        try { localSynth.SpeakAsyncCancelAll(); }
                        catch { /* synth may already be disposed */ }
                    });
                    var localReg = ctReg;
                    localSynth.SpeakCompleted += (_, __) =>
                    {
                        try { localReg.Dispose(); } catch { }
                    };

                    lock (_lock) { _activeSpeakers.Add(localSynth); }
                    localSynth.SpeakAsync(text);
                    // From here on SpeakCompleted owns the registration; do NOT
                    // dispose in the finally block (would race the cancel callback).
                    registrationHandedOff = true;
                }
                catch (Exception)
                {
                    if (synth != null) DisposeSynth(synth);
                    throw;
                }
                finally
                {
                    // If we registered the CT callback but never reached
                    // the SpeakCompleted hand-off (synth.SpeakAsync threw), dispose
                    // the registration here so the closure doesn't leak for the
                    // lifetime of the script's execution token.
                    if (!registrationHandedOff)
                    {
                        try { ctReg.Dispose(); } catch { }
                    }
                }
            }, ct);
        }

        private static void DisposeSynth(SpeechSynthesizer synth)
        {
            try
            {
                lock (_lock) { _activeSpeakers.Remove(synth); }
                synth.Dispose();
            }
            catch { /* swallow */ }
        }
    }
}
