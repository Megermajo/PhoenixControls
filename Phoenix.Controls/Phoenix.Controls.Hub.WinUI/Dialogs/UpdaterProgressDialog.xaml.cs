using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// In-app progress surface for Force Download (and any future Hub-initiated
/// update flows). Polls the Updater's
/// <c>%AppData%/PhoenixControls/Hub/updater-progress.json</c> file every
/// 250 ms via a <see cref="DispatcherTimer"/> and renders phase + percent
/// + a free-form status line.
///
/// Lifecycle:
///   1. <see cref="OnForceDownloadClicked"/> in <c>SettingsDialog</c> creates
///      the dialog, awaits its <c>ShowAsync</c>, and spawns the Updater
///      AFTER the dialog appears (otherwise Hub.Exit fires before the user
///      ever sees a window).
///   2. Updater writes progress every ~500 ms via the atomic .tmp+Move
///      pattern. We poll at 250 ms — never half-read because Move is atomic.
///   3. Once Updater enters <c>await_hub_exit</c>, Hub itself is told to
///      exit by the caller. The dialog never observes <c>swap</c> /
///      <c>complete</c> in practice — Hub is gone before they're written.
///   4. Cancel: click writes <c>cancel.signal</c>; Updater respects it
///      pre-swap. Cancel disables itself once <c>await_hub_exit</c> fires
///      (Updater is past the abort window by then).
/// </summary>
public sealed partial class UpdaterProgressDialog : ContentDialog
{
    private DispatcherTimer? _pollTimer;
    private string? _lastPhase;
    // 0 = no cancel in flight, 1 = cancel already initiated. Swapped
    // via Interlocked.Exchange so spamming ESC during the cancel window can't
    // race past the guard and fire OnCancelClicked twice (which would write
    // cancel.signal twice, log two "Updater cancel requested" lines, and
    // briefly re-enable the Cancel button between writes). The old `bool`
    // flag had a TOCTOU between the read in OnDialogClosing/OnCancelClicked
    // and the write that set it true.
    private int _userCancelled;

    // Watchdog state. Once the dialog observes any "swap" phase
    // entry (or progress effectively halts in await_hub_exit), we stamp
    // _swapWatchdogStart. The Closing handler consults
    // <see cref="WedgedDurationSeconds"/> below to decide whether to treat
    // an ESC dismissal as a recovery action instead of a hard block.
    private DateTime? _swapWatchdogStart;
    private int _lastPercent = -1;
    private string? _lastText;
    private DateTime _lastProgressChangedUtc = DateTime.UtcNow;
    private bool _wedgedConfirmShown;

    /// <summary>
    /// Threshold (in seconds) after which a swap-phase progress
    /// stall is treated as "the Updater appears wedged" and ESC is allowed
    /// to dismiss the dialog (with confirmation). 30s comfortably exceeds
    /// the worst observed swap duration on a healthy machine; anything
    /// past it means the swap is hung and the user needs an out.
    /// </summary>
    private const int WedgedDurationSeconds = 30;

    /// <summary>
    /// Threshold (in seconds) after which an <c>await_hub_exit</c>
    /// phase stall is treated as "the Updater is unresponsive / was killed
    /// externally". At this point the wedge-confirm path may have
    /// fired already, but if the user dismissed it (or didn't try ESC at
    /// all) they are otherwise trapped — Hub is being held alive by a dead
    /// Updater waiting on a sentinel that will never clear. 60s is double
    /// the wedge-confirm window so genuine slow swaps don't surface the
    /// banner; once we cross it we re-enable a plain Close button and
    /// surface an inline "Updater appears unresponsive" message instead of
    /// any modal confirmation (per the no-modals-for-repeatable rule, this
    /// is also better UX than the confirm-dialog ladder).
    /// </summary>
    private const int UnresponsiveDurationSeconds = 60;

    /// <summary>
    /// DispatcherTimer used to re-evaluate the unresponsive
    /// threshold once we've entered <c>await_hub_exit</c>. Separate from
    /// the polling timer (which is driven by progress-file IO and stops
    /// firing if the Updater is dead) so the watchdog clock keeps
    /// ticking regardless of Updater health.
    /// </summary>
    private DispatcherTimer? _unresponsiveWatchdog;
    private bool _unresponsiveSurfaced;

    // OnPollTick is async void (the file read is offloaded to a thread-pool
    // thread). A slow read could otherwise let the 250 ms timer queue a second
    // overlapping tick; this gate drops re-entrant ticks while one is in flight.
    private bool _pollInFlight;

    public UpdaterProgressDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyLocalizedChrome();
            // Initial state — show the eyebrow as "querying" so the dialog
            // doesn't flash empty before the first progress write lands.
            ShowPhase("query", percent: -1, text: Localizer.T(
                "dialog.updater_progress.phase.query", "Querying GitHub for release info…"));
            StartPolling();
        };
        Closed += (_, _) => { StopPolling(); StopUnresponsiveWatchdog(); };
        // ContentDialog's DefaultButton="None" means ESC dismisses the
        // dialog without routing through OnCancelClicked, so cancel.signal
        // never got written and Updater proceeded with the swap. Hook
        // Closing to route ESC / window-close through the cancel path
        // when we're still in a cancellable phase, or block close
        // entirely once the swap is in flight.
        Closing += OnDialogClosing;
    }

    private async void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // Normal terminal route — OnPollTick observed complete/failed
        // and called Hide(). Let it through. Volatile.Read so
        // a cancel-in-flight on another tick isn't observed pre-write.
        if (Volatile.Read(ref _userCancelled) == 1 || _lastPhase is "complete" or "failed") return;

        // Unresponsive escape — once the watchdog has surfaced
        // the "Updater appears unresponsive" banner the user has been
        // explicitly told the install is unchanged and they can dismiss.
        // ESC / close-button / OnCloseClicked should all just exit; the
        // sentinel is left in place so the next Hub launch's
        // ClearStaleSentinel will clean it up via the timestamp
        // fallback once the recorded write-time ages out.
        if (_unresponsiveSurfaced)
        {
            GlobalLogger.Log(
                "Updater appears unresponsive — user dismissed dialog via watchdog escape.",
                "UpdaterProgressDialog", LogLevel.System);
            StopPolling();
            StopUnresponsiveWatchdog();
            return;
        }

        if (CancelButton.IsEnabled)
        {
            // User-initiated dismissal during a cancellable phase. Write
            // cancel.signal and keep the dialog up; OnPollTick will close
            // us once Updater acknowledges with a "failed" entry.
            args.Cancel = true;
            OnCancelClicked(this, new RoutedEventArgs());
            return;
        }

        // Past the cancellable window — by default the swap is
        // about to start or is in flight and dismissing leaves a half-
        // applied install, so block close. BUT: if no progress has been
        // observed for >WedgedDurationSeconds we treat the dialog as
        // wedged and offer a one-shot ESC dismissal with confirmation.
        // The standing rule against modal ContentDialogs only covers
        // repeatable rejection guardrails; this is a one-shot recovery
        // affordance the user actively asked for (by pressing ESC), so
        // a modal confirmation is appropriate.
        if (IsSwapWedged() && !_wedgedConfirmShown)
        {
            args.Cancel = true;
            _wedgedConfirmShown = true;
            try
            {
                // RequestedTheme=Dark pins the
                // dialog to the coal/ember chrome; without it WinUI's
                // ContentDialog defaults to ElementTheme.Default which
                // pulls Fluent's light-grey title-bar and button chrome.
                var confirm = new ContentDialog
                {
                    XamlRoot           = this.XamlRoot,
                    RequestedTheme     = ElementTheme.Dark,
                    Title              = Localizer.T(
                        "dialog.updater_progress.wedged.title",
                        "Updater appears wedged"),
                    Content            = Localizer.T(
                        "dialog.updater_progress.wedged.body",
                        "No progress has been reported for over 30 seconds. The Updater may be stuck. Cancel and return to Hub? The current install is unchanged."),
                    PrimaryButtonText  = Localizer.T(
                        "dialog.updater_progress.wedged.confirm",
                        "Cancel updater"),
                    CloseButtonText    = Localizer.T(
                        "dialog.updater_progress.wedged.keep_waiting",
                        "Keep waiting"),
                    DefaultButton      = ContentDialogButton.Close,
                };
                var result = await confirm.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    GlobalLogger.Log(
                        "Updater progress wedged — user dismissed via watchdog confirmation.",
                        "UpdaterProgressDialog", LogLevel.System);
                    // Mark as a user-cancel-style exit so the Closing rerun
                    // takes the "return" early-out branch above instead of
                    // blocking us again. Best-effort cancel.signal write,
                    // mirroring OnCancelClicked, so a slow-but-not-stuck
                    // Updater can still abort cleanly if it catches up.
                    OnCancelClicked(this, new RoutedEventArgs());
                    // Force-close the dialog regardless of cancel.signal
                    // observation — the watchdog premise is that the
                    // Updater isn't responding, so we can't wait on it.
                    StopPolling();
                    Hide();
                }
                else
                {
                    // User chose to keep waiting — reset the confirm gate
                    // so they can be offered the same out again after the
                    // next stall window.
                    _wedgedConfirmShown = false;
                    _lastProgressChangedUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("UpdaterProgressDialog", "wedged-confirm failed", ex);
                // Re-arm so a follow-up ESC can try the recovery path
                // again rather than silently swallowing the user's intent.
                _wedgedConfirmShown = false;
            }
            return;
        }

        // Past the cancellable window and not wedged — block close so the
        // user can't dismiss a window that's about to take Hub down anyway.
        args.Cancel = true;
    }

    /// <summary>
    /// Returns true when the Updater has been parked on an
    /// uncancellable phase (<c>await_hub_exit</c> / <c>swap</c>) for at
    /// least <see cref="WedgedDurationSeconds"/> with no progress change.
    /// The progress-changed clock advances whenever phase / percent / text
    /// differs from the previous tick, so a slow-but-progressing swap
    /// (large install, slow disk) won't trip the watchdog.
    /// </summary>
    private bool IsSwapWedged()
    {
        if (_lastPhase is not ("await_hub_exit" or "swap")) return false;
        if (_swapWatchdogStart is null) return false;
        var stallSeconds = (DateTime.UtcNow - _lastProgressChangedUtc).TotalSeconds;
        return stallSeconds >= WedgedDurationSeconds;
    }

    /// <summary>
    /// Sets every user-visible string from <see cref="Localizer"/>. The
    /// English fallback passed to <see cref="Localizer.T"/> is the canonical
    /// EN string and matches the value in <c>lang/en.json</c> verbatim.
    /// </summary>
    private void ApplyLocalizedChrome()
    {
        Title              = Localizer.T("dialog.updater_progress.title", "Updating Phoenix Controls…");
        CancelButton.Content = Localizer.T("dialog.updater_progress.button.cancel", "Cancel");
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= OnPollTick;
            _pollTimer = null;
        }
    }

    private async void OnPollTick(object? sender, object e)
    {
        // Drop overlapping ticks: ReadProgress now runs on a thread-pool
        // thread, so a slow read mustn't let the 250 ms timer stack a second
        // in-flight tick that races the UI mutations below.
        if (_pollInFlight) return;
        _pollInFlight = true;
        try
        {
            // ReadProgress() does File.Exists + File.ReadAllText +
            // JsonDocument.Parse. Run it off the dispatcher so the 250 ms poll
            // never blocks the UI thread on disk IO. ConfigureAwait(true)
            // resumes on the UI thread so every mutation below stays UI-bound.
            var snap = await Task.Run(UpdateChecker.ReadProgress).ConfigureAwait(true);
            if (snap is null) return;
            ShowPhase(snap.Phase, snap.Percent, snap.Text);

            // Once the Updater enters await_hub_exit, the Cancel button is
            // off-limits — the swap is about to start and aborting after
            // would leave a half-applied install. Same for swap / complete /
            // failed (terminal states the dialog rarely sees because Hub
            // exits first, but covered for unit-test rigs).
            if (snap.Phase is "await_hub_exit" or "swap" or "complete" or "failed")
            {
                CancelButton.IsEnabled = false;
            }

            // Terminal-state surface — only reachable in test rigs or the
            // (rare) case where Hub didn't exit before Updater finished.
            if (snap.Phase is "complete" or "failed")
            {
                StopPolling();
                Hide();
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: progress file might be mid-rotation. Try again next tick.
            GlobalLogger.Error("UpdaterProgressDialog", "OnPollTick", ex);
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void ShowPhase(string phase, int percent, string text)
    {
        // Eyebrow caption — phase-specific, falls back to the raw phase name
        // for unknown phases (forward-compat: a future Updater introducing
        // a new phase shouldn't crash the dialog).
        string eyebrow = phase switch
        {
            "query"          => Localizer.T("dialog.updater_progress.phase.query",          "QUERYING GITHUB"),
            "download"       => Localizer.T("dialog.updater_progress.phase.download",       "DOWNLOADING"),
            "verify"         => Localizer.T("dialog.updater_progress.phase.verify",         "VERIFYING"),
            "prepare"        => Localizer.T("dialog.updater_progress.phase.prepare",        "PREPARING SWAP"),
            "await_hub_exit" => Localizer.T("dialog.updater_progress.phase.await_hub_exit", "WAITING FOR HUB TO EXIT"),
            "failed"         => Localizer.T("dialog.updater_progress.phase.failed",         "FAILED"),
            "complete"       => Localizer.T("dialog.updater_progress.phase.complete",       "COMPLETE"),
            _                => phase.ToUpperInvariant(),
        };
        PhaseEyebrowText.Text = eyebrow;
        ApplyTerminalChrome(phase);

        // Bar — indeterminate when percent < 0; otherwise show the value.
        if (percent < 0 || percent > 100)
        {
            ProgressBarControl.IsIndeterminate = true;
            PercentText.Text = "";
        }
        else
        {
            ProgressBarControl.IsIndeterminate = false;
            ProgressBarControl.Value = percent;
            string fmt = Localizer.T("dialog.updater_progress.percent_format", "{0}%");
            PercentText.Text = string.Format(CultureInfo.InvariantCulture, fmt, percent);
        }

        StatusText.Text = text ?? "";

        // Watchdog bookkeeping. Treat any (phase, percent, text)
        // change as forward progress and stamp _lastProgressChangedUtc.
        // Once we first cross into await_hub_exit / swap, snapshot
        // _swapWatchdogStart so IsSwapWedged() can apply its threshold.
        bool changed =
            !string.Equals(phase, _lastPhase, StringComparison.Ordinal) ||
            percent != _lastPercent ||
            !string.Equals(text ?? string.Empty, _lastText ?? string.Empty, StringComparison.Ordinal);
        if (changed)
        {
            _lastProgressChangedUtc = DateTime.UtcNow;
            // Re-arm the wedged-confirm gate on real progress — a wedged
            // dialog that recovers shouldn't carry a stale "already
            // confirmed" flag into a fresh stall window.
            _wedgedConfirmShown = false;
        }
        if (phase is "await_hub_exit" or "swap" && _swapWatchdogStart is null)
            _swapWatchdogStart = DateTime.UtcNow;

        // Start the unresponsive watchdog the first time we
        // observe await_hub_exit. swap is excluded — a slow swap is best
        // handled by the wedge-confirm path because aborting
        // mid-swap leaves a half-applied install, whereas await_hub_exit
        // is pre-swap and dismissing is safe (install is untouched).
        if (phase == "await_hub_exit" && _unresponsiveWatchdog is null)
            StartUnresponsiveWatchdog();

        _lastPhase = phase;
        _lastPercent = percent;
        _lastText = text;
    }

    /// <summary>
    /// Spins up a 1 s DispatcherTimer that re-evaluates
    /// <see cref="IsUpdaterUnresponsive"/> on every tick. Once the
    /// threshold trips, the timer surfaces the inline banner + Close
    /// button and self-stops — it's a one-shot escalation, not a
    /// continuous poller.
    /// </summary>
    private void StartUnresponsiveWatchdog()
    {
        _unresponsiveWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _unresponsiveWatchdog.Tick += OnUnresponsiveWatchdogTick;
        _unresponsiveWatchdog.Start();
    }

    private void StopUnresponsiveWatchdog()
    {
        if (_unresponsiveWatchdog is not null)
        {
            _unresponsiveWatchdog.Stop();
            _unresponsiveWatchdog.Tick -= OnUnresponsiveWatchdogTick;
            _unresponsiveWatchdog = null;
        }
    }

    private void OnUnresponsiveWatchdogTick(object? sender, object e)
    {
        try
        {
            if (!IsUpdaterUnresponsive()) return;
            SurfaceUnresponsiveBanner();
            StopUnresponsiveWatchdog();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UpdaterProgressDialog", "OnUnresponsiveWatchdogTick", ex);
        }
    }

    /// <summary>
    /// Returns true when the dialog has been parked on
    /// <c>await_hub_exit</c> for ≥<see cref="UnresponsiveDurationSeconds"/>
    /// without any progress write. Distinct from
    /// <see cref="IsSwapWedged"/> — that path covers the swap phase and
    /// goes through a modal confirmation; this path covers await_hub_exit
    /// and surfaces an inline escape with no confirm step.
    /// </summary>
    private bool IsUpdaterUnresponsive()
    {
        if (_lastPhase != "await_hub_exit") return false;
        if (_unresponsiveSurfaced) return false;
        var stallSeconds = (DateTime.UtcNow - _lastProgressChangedUtc).TotalSeconds;
        return stallSeconds >= UnresponsiveDurationSeconds;
    }

    /// <summary>
    /// Reveals the unresponsive banner + Close button and
    /// hides the now-stale Cancel button. Logging is at System level so
    /// the SystemLog panel shows the user-visible recovery affordance
    /// without spamming Debug.
    /// </summary>
    private void SurfaceUnresponsiveBanner()
    {
        if (_unresponsiveSurfaced) return;
        _unresponsiveSurfaced = true;

        UnresponsiveBannerText.Text = Localizer.T(
            "dialog.updater_progress.unresponsive.body",
            "Updater appears unresponsive — click Close to dismiss. Your install is unchanged.");
        UnresponsiveBanner.Visibility = Visibility.Visible;

        CloseButton.Content = Localizer.T(
            "dialog.updater_progress.button.close", "Close");
        CloseButton.Visibility = Visibility.Visible;
        CloseButton.IsEnabled  = true;

        // Cancel is meaningless once we've decided the Updater is dead —
        // cancel.signal would never be read. Hide it so the user has
        // exactly one obvious action.
        CancelButton.Visibility = Visibility.Collapsed;

        GlobalLogger.Log(
            $"Updater progress stalled in await_hub_exit for ≥{UnresponsiveDurationSeconds}s — surfacing unresponsive banner.",
            "UpdaterProgressDialog", LogLevel.System);
    }

    /// <summary>
    /// Handler for the watchdog-only Close button. Hide() triggers
    /// the Closing handler, which observes <see cref="_unresponsiveSurfaced"/>
    /// and lets the dismissal proceed without confirmation.
    /// </summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    /// <summary>
    /// Paints the dialog into a failure state on terminal
    /// phases. On "failed" we tint the progress bar red, surface the
    /// warning glyph next to the eyebrow, and flip the dialog frame
    /// border to ErrBrush so the whole window reads as "this didn't
    /// work". Non-terminal phases revert to the steady-state chrome
    /// (idempotent, in case the Updater bounces between phases). The
    /// dialog also auto-closes within ~250 ms on "failed" via
    /// OnPollTick, but the chrome flip is what the user actually sees.
    /// </summary>
    private void ApplyTerminalChrome(string phase)
    {
        bool failed = phase == "failed";
        var res = Application.Current?.Resources;

        if (failed)
        {
            if (res is { } r1
                && r1.TryGetValue("ErrBrush", out var errResource)
                && errResource is Brush errBrush)
            {
                ProgressBarControl.Foreground = errBrush;
                DialogFrame.BorderBrush = errBrush;
            }
            FailureIcon.Visibility = Visibility.Visible;
        }
        else
        {
            // Steady-state chrome. The XAML default for the progress
            // bar is Ember200Brush; reload from Application.Resources
            // so a theme reload doesn't leave us on a stale colour.
            if (res is { } r2
                && r2.TryGetValue("Ember200Brush", out var emberResource)
                && emberResource is Brush emberBrush)
            {
                ProgressBarControl.Foreground = emberBrush;
            }
            DialogFrame.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            FailureIcon.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Writes <c>cancel.signal</c> next to the progress file. Updater polls
    /// for it during pre-swap phases and aborts cleanly. Once the swap is
    /// in flight the file is ignored — the button has been disabled at that
    /// point. The dialog stays open until Updater observes the cancel and
    /// writes a <c>failed</c> progress entry, at which point <see cref="OnPollTick"/>
    /// closes us.
    /// </summary>
    private async void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        // Single-shot guard. Two cancel paths can collide here:
        // direct button click, ESC → OnDialogClosing → re-invocation, and
        // the watchdog-confirm path. Interlocked.Exchange returns the
        // PREVIOUS value, so the first caller sees 0 (proceed) and every
        // subsequent caller sees 1 (already in flight) and bails early —
        // even if they arrive nanoseconds apart on the dispatcher queue.
        if (Interlocked.Exchange(ref _userCancelled, 1) == 1) return;
        // Dim the button up-front so the user doesn't try to click again —
        // Updater takes a tick or two to react. Doing this BEFORE the
        // Task.Run keeps the visual ack synchronous with the click while
        // the actual file I/O moves off the UI thread.
        CancelButton.IsEnabled = false;
        CancelButton.Content = Localizer.T("dialog.updater_progress.button.cancelling",
                                           "Cancelling…");
        try
        {
            string path = UpdateChecker.CancelSignalFilePath;
            // cancel.signal is tiny (a single ISO-8601 string),
            // but File.WriteAllText still blocks the dispatcher. Task.Run is
            // the minimal-touch fix — it keeps OnCancelClicked's signature
            // compatible with the two direct-invocation call sites that
            // synthesise a RoutedEventArgs without awaiting.
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
            }).ConfigureAwait(true);
            GlobalLogger.Log("Updater cancel requested by user.", "UpdaterProgressDialog", LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("UpdaterProgressDialog", "OnCancelClicked", ex);
        }
    }
}
