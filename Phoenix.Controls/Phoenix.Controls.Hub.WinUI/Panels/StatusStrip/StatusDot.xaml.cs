using System;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Phoenix.Controls.Hub.WinUI.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Phoenix.Controls.Hub.WinUI.Panels.StatusStrip;

public sealed partial class StatusDot : UserControl
{
    // Activation gate. When the host Window goes
    // Deactivated we Pause() the pulse; on re-activation we Resume() if the
    // dot is still in a state that wants pulsing. _windowActive mirrors the
    // tracker's last broadcast so UpdatePulse can decide between Begin/Stop
    // (state change) and Pause/Resume (activation flip) without ambiguity.
    private IDisposable? _activationSubscription;
    private bool _windowActive = true;

    // Composition-API pulse. Replaced the XAML Storyboard
    // (scale + opacity DoubleAnimations on the Ellipse's ScaleTransform) with
    // a Composition ScalarKeyFrameAnimation pair so the pulse runs on the
    // DComp render thread instead of the UI dispatcher. _dotVisual is the
    // hand-off Visual on the Ellipse; _activePulse tracks which animation
    // (if any) is in flight so UpdatePulse can decide between
    // Stop+Start (state change), no-op+Resume (re-arm after an activation pause), or
    // straight Stop (target state has no pulse).
    private Visual? _dotVisual;
    // Two-pulse mode mirrors ScriptStatusDot — the
    // chrome dot used to only pulse Connected and StopPulseAnimations() on
    // Errored, making Errored visually identical to Disconnected. Errored
    // now gets a distinct red attention pulse so the at-a-glance signal
    // matches ScriptStatusDot's traffic-light semantics.
    private enum ActivePulse { None, Connected, Errored }
    private ActivePulse _activePulse;
    // Connected pulse — 3 cycles × 2 s, matches the prior Storyboard's
    // RepeatBehavior="3x" * (1 s rise + 1 s fall AutoReverse) cadence so the
    // existing visual identity ("dot just connected") is preserved.
    private const int ConnectedPulseIterations = 3;
    private static readonly TimeSpan ConnectedPulseCycleDuration = TimeSpan.FromSeconds(2);
    private const float ConnectedPeakScale = 1.35f;
    // Errored pulse — forever, slightly punchier (1.8 s cycle, 1.30 peak)
    // so it reads as ongoing attention rather than a one-shot greeting.
    // Mirrors ScriptStatusDot.RedCycleDuration / RedPeakScale exactly.
    private static readonly TimeSpan ErroredPulseCycleDuration = TimeSpan.FromMilliseconds(1800);
    private const float ErroredPeakScale = 1.30f;
    /// <summary>
    /// Raised when the user left-clicks (or taps) the dot. The hosting
    /// <see cref="StatusStripView"/> uses this to open
    /// <c>SettingsDialog</c> on the relevant Pivot tab.
    /// </summary>
    public event EventHandler? Clicked;

    // Shared hand cursor. Previous code called
    // InputSystemCursor.Create per PointerEntered — a real native CreateCursor
    // round-trip plus a leaked IDisposable on every hover. Three dots in the
    // strip and casual mousing across the bottom bar made this audible in
    // GDI handle counts during long sessions.
    private static readonly Microsoft.UI.Input.InputCursor? s_handCursor = TryCreateHandCursor();

    private static Microsoft.UI.Input.InputCursor? TryCreateHandCursor()
    {
        try { return Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand); }
        catch { return null; }
    }

    public StatusDot()
    {
        InitializeComponent();
        // Seed DotBrush + TooltipText before Loaded so the dot is rendered
        // with a valid brush and an accessible name at its initial State
        // (Disconnected) — otherwise both stay at the DependencyProperty
        // defaults (null, "") until the first State change.
        RecomputeBrush();
        RecomputeTooltip();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Bind a Composition Visual to the Ellipse so we can run the
        // pulse off the UI dispatcher. The Visual handle is stable for the
        // Ellipse's lifetime; CenterPoint anchors the Scale around the
        // 8×8 ellipse's centre so the pulse scales symmetrically.
        EnsureDotVisual();

        // Subscribe to host-Window activation. The tracker primes
        // _windowActive to the current state so a dot loaded into an
        // already-active window starts pulsing immediately if its state
        // says it should.
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
        // Tear down the activation subscription before stopping the pulse
        // so a tracker callback firing during teardown can't restart it.
        _activationSubscription?.Dispose();
        _activationSubscription = null;
        StopPulseAnimations();
        _activePulse = ActivePulse.None;
    }

    private void EnsureDotVisual()
    {
        if (_dotVisual is not null) return;
        try
        {
            _dotVisual = ElementCompositionPreview.GetElementVisual(DotEllipse);
            _dotVisual.CenterPoint = new Vector3(4.0f, 4.0f, 0f); // 8×8 ellipse mid-point
        }
        catch
        {
            // Composition handles can fail to materialise on rare driver
            // paths (RDP fallback, very old WARP). Without the Visual, the
            // pulse silently degrades to "no pulse" — the dot still renders
            // at its solid color/state which preserves the meaning.
        }
    }

    private void OnWindowActivationChanged(bool active)
    {
        _windowActive = active;
        // Pause/Resume go through the per-property AnimationController
        // so the Composition pulse picks up mid-cycle on re-activation
        // instead of restarting from frame 0.
        if (active)
        {
            UpdatePulse();
        }
        else
        {
            PausePulseAnimations();
        }
    }

    private void OnDotTapped(object sender, TappedRoutedEventArgs e)
    {
        Clicked?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    // Keyboard activation — Tab now reaches the dot (IsTabStop=True on
    // the UserControl); Enter/Space fire the same Clicked event that a
    // mouse tap does. Pre-fix the dot was mouse-only. This is wired
    // via KeyDown="OnRootKeyDown" on the UserControl root in the XAML.
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDotPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Cursor hint so the user knows the dot is interactive — without it
        // the click target is invisible until you actually try clicking.
        // Shared singleton; null-safe if InputSystemCursor.Create failed at
        // class init (rare, but possible on shutdown / RPC teardown).
        if (s_handCursor is not null) ProtectedCursor = s_handCursor;
    }

    private void OnDotPointerExited(object sender, PointerRoutedEventArgs e)
    {
        try { ProtectedCursor = null; }
        catch { }
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatusDot),
            new PropertyMetadata(string.Empty, OnLabelChanged));

    public string Sub
    {
        get => (string)GetValue(SubProperty);
        set => SetValue(SubProperty, value);
    }
    public static readonly DependencyProperty SubProperty =
        DependencyProperty.Register(nameof(Sub), typeof(string), typeof(StatusDot),
            new PropertyMetadata(string.Empty, (d, _) =>
            {
                if (d is StatusDot self) self.RecomputeTooltip();
            }));

    public ConnectionState State
    {
        get => (ConnectionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(StatusDot),
            new PropertyMetadata(ConnectionState.Disconnected, OnStateChanged));

    // Computed via x:Bind off State — but x:Bind needs the source updated to re-evaluate.
    // We update both DotBrush and LabelUpper through dependency-prop change handlers.
    public Brush DotBrush
    {
        get => (Brush)GetValue(DotBrushProperty);
        private set => SetValue(DotBrushProperty, value);
    }
    public static readonly DependencyProperty DotBrushProperty =
        DependencyProperty.Register(nameof(DotBrush), typeof(Brush), typeof(StatusDot),
            new PropertyMetadata(null));

    public string LabelUpper
    {
        get => (string)GetValue(LabelUpperProperty);
        private set => SetValue(LabelUpperProperty, value);
    }
    public static readonly DependencyProperty LabelUpperProperty =
        DependencyProperty.Register(nameof(LabelUpper), typeof(string), typeof(StatusDot),
            new PropertyMetadata(string.Empty));

    /// <summary>
    /// Tooltip surface combining label, sub-text, and current state. Bound
    /// by the wrapper Border so hovering a dot tells the user *why* it's
    /// red/yellow without requiring them to dig into the System Log
    /// ("status strip dots have no tooltips").
    /// </summary>
    public string TooltipText
    {
        get => (string)GetValue(TooltipTextProperty);
        private set => SetValue(TooltipTextProperty, value);
    }
    public static readonly DependencyProperty TooltipTextProperty =
        DependencyProperty.Register(nameof(TooltipText), typeof(string), typeof(StatusDot),
            new PropertyMetadata(string.Empty));

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusDot self)
        {
            self.RecomputeBrush();
            self.UpdatePulse();
            self.RecomputeTooltip();
        }
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is StatusDot self)
        {
            self.LabelUpper = (e.NewValue as string)?.ToUpperInvariant() ?? string.Empty;
            self.RecomputeTooltip();
        }
    }

    private void RecomputeTooltip()
    {
        string stateLabel = State switch
        {
            ConnectionState.Connected    => "connected",
            ConnectionState.Connecting   => "connecting…",
            ConnectionState.Degraded     => "degraded",
            ConnectionState.Errored      => "error",
            _                            => "disconnected",
        };
        string subText = string.IsNullOrEmpty(Sub) ? "" : $"\n{Sub}";
        TooltipText = $"{Label} — {stateLabel}{subText}";
    }

    private void RecomputeBrush()
    {
        Color target = ResolveStateColor(State);
        // First call: seed a fresh mutable SolidColorBrush so subsequent
        // state changes can animate its Color rather than reassign the
        // entire DotBrush DP. Reassigning would let x:Bind swap to a new
        // brush instance mid-animation and cancel the transition.
        if (DotBrush is not SolidColorBrush owned)
        {
            DotBrush = new SolidColorBrush(target);
            return;
        }
        if (owned.Color == target) return;
        // ~180ms ease-out ColorAnimation — fast enough to feel responsive
        // but slow enough that a green→red transition reads as a state
        // change rather than a glitch. Pre-fix this was an instant brush
        // swap and a brief blip went unnoticed.
        var anim = new ColorAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, owned);
        Storyboard.SetTargetProperty(anim, "Color");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    private static Color ResolveStateColor(ConnectionState s)
    {
        string key = s switch
        {
            ConnectionState.Connected  => "OkBrush",
            ConnectionState.Connecting => "WarnBrush",
            ConnectionState.Degraded   => "WarnBrush",
            ConnectionState.Errored    => "ErrBrush",
            _                          => "CoalDividerBrush",
        };
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is SolidColorBrush b) return b.Color;
        return Colors.Gray;
    }

    private void UpdatePulse()
    {
        // Errored gets its own attention pulse instead of being
        // silently collapsed onto StopPulseAnimations(). Connecting and
        // Degraded stay solid (the colour change via RecomputeBrush is the
        // signal; a pulse on every yellow state would be noise).
        ActivePulse target = State switch
        {
            ConnectionState.Connected => ActivePulse.Connected,
            ConnectionState.Errored   => ActivePulse.Errored,
            _                         => ActivePulse.None,
        };

        // While the host window is deactivated, hold the active pulse
        // paused regardless of target. The Resume path in
        // OnWindowActivationChanged re-enters UpdatePulse on re-activation
        // and the dot picks up its current state.
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

        // Same pulse still in flight — Resume handles "was paused, now
        // active" without restarting from frame 0.
        if (target == _activePulse)
        {
            if (target != ActivePulse.None) ResumePulseAnimations();
            return;
        }

        // Different target — stop whatever's running before starting the new
        // one. Without the explicit Stop, switching Connected → Errored
        // would briefly double-pulse before the new animation settles.
        StopPulseAnimations();

        switch (target)
        {
            case ActivePulse.Connected:
                StartScalePulse(ConnectedPulseCycleDuration, ConnectedPeakScale,
                                AnimationIterationBehavior.Count, ConnectedPulseIterations,
                                animateOpacity: true);
                break;
            case ActivePulse.Errored:
                // Forever-iterating red pulse so the dot keeps demanding
                // attention until the underlying state clears. Opacity is
                // held at 1.0 — ErrBrush is already saturated red, so
                // fading it would soften the signal we want to amplify.
                StartScalePulse(ErroredPulseCycleDuration, ErroredPeakScale,
                                AnimationIterationBehavior.Forever, iterationCount: 0,
                                animateOpacity: false);
                break;
            // None: dot rests at scale 1, solid colour from RecomputeBrush.
        }
        _activePulse = target;
    }

    private void StartScalePulse(
        TimeSpan cycleDuration,
        float peakScale,
        AnimationIterationBehavior iterationBehavior,
        int iterationCount,
        bool animateOpacity)
    {
        if (_dotVisual is null) return;
        var compositor = _dotVisual.Compositor;

        // Closer match to the prior Storyboard's SineEase — Composition's
        // CubicBezierEasingFunction with sine-approx control points.
        var sineEase = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0f), new Vector2(0.58f, 1f));

        // Scale.X / Scale.Y — 1.0 → peakScale → 1.0 over cycleDuration. We
        // animate Scale.X and Scale.Y independently as scalars rather than
        // packing into a Vector3 so the eased rise/fall is symmetric on
        // both axes.
        var scaleAnim = compositor.CreateScalarKeyFrameAnimation();
        scaleAnim.InsertKeyFrame(0.0f, 1.0f,      sineEase);
        scaleAnim.InsertKeyFrame(0.5f, peakScale, sineEase);
        scaleAnim.InsertKeyFrame(1.0f, 1.0f,      sineEase);
        scaleAnim.Duration = cycleDuration;
        scaleAnim.IterationBehavior = iterationBehavior;
        if (iterationBehavior == AnimationIterationBehavior.Count && iterationCount > 0)
            scaleAnim.IterationCount = iterationCount;

        try
        {
            _dotVisual.StartAnimation("Scale.X", scaleAnim);
            _dotVisual.StartAnimation("Scale.Y", scaleAnim);

            if (animateOpacity)
            {
                // Opacity — 1.0 → 0.55 → 1.0 (mirrors the original DotEllipse
                // Opacity pulse). Separate animation so the visual stays at
                // 1.0 when the pulse stops, instead of inheriting a stale
                // partial opacity.
                var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                opacityAnim.InsertKeyFrame(0.0f, 1.0f,  sineEase);
                opacityAnim.InsertKeyFrame(0.5f, 0.55f, sineEase);
                opacityAnim.InsertKeyFrame(1.0f, 1.0f,  sineEase);
                opacityAnim.Duration = cycleDuration;
                opacityAnim.IterationBehavior = iterationBehavior;
                if (iterationBehavior == AnimationIterationBehavior.Count && iterationCount > 0)
                    opacityAnim.IterationCount = iterationCount;
                _dotVisual.StartAnimation("Opacity", opacityAnim);
            }
        }
        catch
        {
            // Composition.StartAnimation can fail with E_NOINTERFACE on
            // legacy DWM fallbacks. Leave the dot solid (still semantically
            // correct — the colour change already carries the state).
        }
    }

    private void StopPulseAnimations()
    {
        if (_dotVisual is null) return;
        try
        {
            _dotVisual.StopAnimation("Scale.X");
            _dotVisual.StopAnimation("Scale.Y");
            _dotVisual.StopAnimation("Opacity");
            // Reset to identity so the dot doesn't freeze in the middle of a
            // half-played pulse when Stop is the terminal call.
            _dotVisual.Scale = new Vector3(1, 1, 1);
            _dotVisual.Opacity = 1.0f;
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
            _dotVisual.TryGetAnimationController("Opacity")?.Pause();
        }
        catch { /* controller can be null after IterationCount completion */ }
    }

    private void ResumePulseAnimations()
    {
        if (_dotVisual is null) return;
        try
        {
            _dotVisual.TryGetAnimationController("Scale.X")?.Resume();
            _dotVisual.TryGetAnimationController("Scale.Y")?.Resume();
            _dotVisual.TryGetAnimationController("Opacity")?.Resume();
        }
        catch { }
    }
}
