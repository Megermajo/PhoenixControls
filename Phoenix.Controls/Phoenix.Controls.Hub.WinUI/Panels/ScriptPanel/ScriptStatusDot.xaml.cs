using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;

public sealed partial class ScriptStatusDot : UserControl
{
    // Perf-review H8 (2026-05-14): UpdatePulse() fires on every Status setter
    // raise (i.e. every script metric tick). Begin() restarts from frame 0
    // each call, producing a visible "jump" in the dot per tick. Track which
    // pulse is active so we can skip re-Start on unchanged state.
    // `_activePulse` distinguishes amber (Queued/Running) from red (Errored)
    // so the two pulses never run simultaneously.
    private enum ActivePulse { None, Amber, Red }
    private ActivePulse _activePulse;

    // M17 (2026-05-14): activation gate. When the host Window (Hub MainWindow
    // or the pop-out Window via PopOutWindowFactory) goes Deactivated we
    // Pause() the active animation and Resume() on re-activation.
    private IDisposable? _activationSubscription;
    private bool _windowActive = true;

    // M36 (2026-05-14): Composition-API pulse. Replaces the two XAML
    // Storyboards (amber + red) with Compositor.CreateScalarKeyFrameAnimation
    // so both pulses run on the DComp render thread. AnimationController
    // (TryGetAnimationController) drives M17 Pause/Resume without restarting
    // the Composition clock.
    private Visual? _dotVisual;
    private static readonly TimeSpan AmberCycleDuration = TimeSpan.FromMilliseconds(2400); // 1.2 s rise + 1.2 s fall
    private const float AmberPeakScale = 1.25f;
    private static readonly TimeSpan RedCycleDuration = TimeSpan.FromMilliseconds(1800);   // 0.9 s rise + 0.9 s fall — slightly punchier
    private const float RedPeakScale = 1.30f;

    public ScriptStatusDot()
    {
        InitializeComponent();
        // Seed StateText + DotBrush for the default ScriptState.Idle so the
        // tooltip / Narrator name / dot colour are correct before the first
        // OnStateChanged fires (the DP change handler only runs when the
        // value changes away from the default). Without RecomputeBrush() at
        // ctor time the brush stays null and Fill renders nothing.
        RecomputeStateText();
        RecomputeBrush();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureDotVisual();
        if (XamlRoot is { } xr)
        {
            _activationSubscription = WindowActivationTracker.SubscribeForXamlRoot(
                xr,
                OnWindowActivationChanged,
                out _windowActive);
        }
        UpdatePulse();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _activationSubscription?.Dispose();
        _activationSubscription = null;
        StopAllPulseAnimations();
        _activePulse = ActivePulse.None;
    }

    private void OnWindowActivationChanged(bool active)
    {
        _windowActive = active;
        if (active)
        {
            UpdatePulse();
        }
        else
        {
            // Pause the active pulse via its AnimationController. Pause on a
            // never-started property is a no-op (the controller is null) so
            // checking _activePulse first isn't strictly required, but it
            // saves the TryGetAnimationController round-trip.
            PausePulseAnimations();
        }
    }

    private void EnsureDotVisual()
    {
        if (_dotVisual is not null) return;
        try
        {
            _dotVisual = ElementCompositionPreview.GetElementVisual(DotEllipse);
            _dotVisual.CenterPoint = new Vector3(5.0f, 5.0f, 0f); // 10×10 ellipse mid-point
        }
        catch { }
    }

    public ScriptState State
    {
        get => (ScriptState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(ScriptState), typeof(ScriptStatusDot),
            new PropertyMetadata(ScriptState.Idle, OnStateChanged));

    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        private set => SetValue(DotBrushProperty, value);
    }
    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(ScriptStatusDot),
            new PropertyMetadata(null));

    /// <summary>
    /// Human-readable state phrase — drives the tooltip and Narrator
    /// announcement. Without it the dot was color-only at 6×6 px and
    /// the meaning was opaque to first-time users and screen readers.
    /// </summary>
    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        private set => SetValue(StateTextProperty, value);
    }
    public static readonly DependencyProperty StateTextProperty =
        DependencyProperty.Register(nameof(StateText), typeof(string), typeof(ScriptStatusDot),
            new PropertyMetadata(string.Empty));

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScriptStatusDot self)
        {
            self.RecomputeBrush();
            self.RecomputeStateText();
            self.UpdatePulse();
        }
    }

    private void RecomputeStateText()
    {
        StateText = State switch
        {
            ScriptState.Errored => "Errored — last run faulted",
            ScriptState.Queued  => "Queued — waiting to run",
            ScriptState.Running => "Running — executing now",
            _                   => "Idle — ready / last run clean",
        };
    }

    private void RecomputeBrush()
    {
        DotBrush = ResolveStateBrush(State);
    }

    // ── Shared traffic-light resolver (QC12-02) ──────────────────────────
    // Single source of truth for ScriptState → brush so the dot and the row
    // text in ScriptRowVm cannot drift apart. Keys come from PhoenixDark.xaml's
    // Status* tokens (Design_Orders §2 Accents + §4.6 traffic lights).
    //
    // Maps:
    //   Running → StatusGreenBrush  (90,200,120 — connected / active)
    //   Queued  → StatusYellowBrush (230,200,90 — queued / executing wait)
    //   Errored → StatusRedBrush    (230,100,100 — error)
    //   Idle    → CoalSecondaryTextBrush (quiet, no traffic-light color)
    internal static string ResolveStateBrushKey(ScriptState state) => state switch
    {
        ScriptState.Running => "StatusGreenBrush",
        ScriptState.Queued  => "StatusYellowBrush",
        ScriptState.Errored => "StatusRedBrush",
        _                   => "CoalSecondaryTextBrush",
    };

    internal static Brush ResolveStateBrush(ScriptState state)
    {
        string key = ResolveStateBrushKey(state);
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush b)
            return b;
        return new SolidColorBrush(Colors.Gray);
    }

    private void UpdatePulse()
    {
        // Traffic-light spec (project_script_window_redesign.md): amber
        // pulses for Queued and Running (script is in flight), red pulses
        // for Errored (last run faulted), green stays solid for Idle.
        // Solid-green Idle is unchanged.
        ActivePulse target = State switch
        {
            ScriptState.Errored                       => ActivePulse.Red,
            ScriptState.Queued or ScriptState.Running => ActivePulse.Amber,
            _                                         => ActivePulse.None,
        };

        // M17 — if the host window is deactivated, hold the current pulse
        // paused regardless of target. The Resume path re-enters UpdatePulse
        // on re-activation and the dot picks up its current state.
        if (!_windowActive)
        {
            PausePulseAnimations();
            _activePulse = target;
            return;
        }

        EnsureDotVisual();
        if (_dotVisual is null)
        {
            _activePulse = target;
            return;
        }

        // If the target didn't change AND a previous state's animation is
        // still in flight, just Resume (handles "was paused, now active"
        // without restarting from frame 0).
        if (target == _activePulse)
        {
            ResumePulseAnimations();
            return;
        }

        // Stop the previously-running animation before starting the new one.
        // Without the explicit stop, switching Running → Errored would
        // briefly double-pulse before the override settles.
        StopAllPulseAnimations();

        switch (target)
        {
            case ActivePulse.Amber: StartScalePulse(AmberCycleDuration, AmberPeakScale); break;
            case ActivePulse.Red:   StartScalePulse(RedCycleDuration,   RedPeakScale);   break;
            // None: both stopped, no Start call — dot rests at scale 1.
        }
        _activePulse = target;
    }

    private void StartScalePulse(TimeSpan cycleDuration, float peakScale)
    {
        if (_dotVisual is null) return;
        var compositor = _dotVisual.Compositor;
        var sineEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0f), new Vector2(0.58f, 1f));
        // M36 — ScalarKeyFrameAnimation per the spec call-out. Scale.X and
        // Scale.Y get the same animation; both run as independent scalars
        // off the Composition clock.
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0.0f, 1.0f,       sineEase);
        scaleAnim.InsertKeyFrame(0.5f, peakScale,  sineEase);
        scaleAnim.InsertKeyFrame(1.0f, 1.0f,       sineEase);
        scaleAnim.Duration = cycleDuration;
        scaleAnim.IterationBehavior = AnimationIterationBehavior.Forever;
        try
        {
            _dotVisual.StartAnimation("Scale.X", scaleAnim);
            _dotVisual.StartAnimation("Scale.Y", scaleAnim);
        }
        catch { /* Composition StartAnimation can fail on legacy DWM paths */ }
    }

    private void StopAllPulseAnimations()
    {
        if (_dotVisual is null) return;
        try
        {
            _dotVisual.StopAnimation("Scale.X");
            _dotVisual.StopAnimation("Scale.Y");
            // Reset to identity so the dot doesn't freeze mid-pulse when
            // Stop is the terminal call.
            _dotVisual.Scale = new Vector3(1, 1, 1);
        }
        catch { }
    }

    private void PausePulseAnimations()
    {
        if (_dotVisual is null) return;
        try
        {
            _dotVisual.TryGetAnimationController("Scale.X")?.Pause();
            _dotVisual.TryGetAnimationController("Scale.Y")?.Pause();
        }
        catch { }
    }

    private void ResumePulseAnimations()
    {
        if (_dotVisual is null) return;
        try
        {
            _dotVisual.TryGetAnimationController("Scale.X")?.Resume();
            _dotVisual.TryGetAnimationController("Scale.Y")?.Resume();
        }
        catch { }
    }
}
