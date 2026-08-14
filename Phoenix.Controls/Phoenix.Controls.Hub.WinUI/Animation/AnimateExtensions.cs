using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Phoenix.Controls.Hub.WinUI.Animation;

/// <summary>
/// T11-10 — Hub.WinUI-local motion vocabulary for the in-tool animation pass.
///
/// Deliberately small and <b>composited-only</b>: every routine animates
/// <see cref="UIElement.Opacity"/>, a <see cref="TranslateTransform"/>, or a
/// <see cref="ScaleTransform"/> assigned to <see cref="UIElement.RenderTransform"/>.
/// Nothing here touches Width / Height / Margin / Grid definitions, so the
/// motion stays off the layout path (no layout thrash). Durations + CubicEase
/// easing match the pre-existing house idioms so additions read as native:
///   • <c>PopOutWindowFactory.ApplyOpenFadeIn</c> — 120 ms opacity ease-out.
///   • <c>HubWorkspaceView.AnimatePanelRegionIn</c> — 170 ms opacity + 8 px slide.
///
/// Lives in Hub.WinUI (NOT <c>Phoenix.Controls.Shared/UI</c>) per the pillar
/// chrome-independence rule — Architect / Visualist keep their own motion. Every
/// routine is decorative and defensively wrapped: on any failure it lands the
/// element in its resting state (Opacity 1, no transform) and returns, so a
/// motion glitch can never leave a control invisible or un-clickable.
/// </summary>
internal static class AnimateExtensions
{
    /// <summary>Tab / section content entrance (opacity + short upward slide).</summary>
    public const double EntranceMs = 160;
    /// <summary>Value-changed emphasis (centred scale pulse).</summary>
    public const double FlashMs = 220;
    /// <summary>Marquee reveal (scale-up + fade).</summary>
    public const double RevealMs = 200;

    // EaseOut is the house curve for every routine here. There is no EaseIn
    // helper: the one caller that needed an ease-in return leg (the pulse)
    // now gets it from AutoReverse replaying EaseOut backwards.
    private static CubicEase EaseOut() => new() { EasingMode = EasingMode.EaseOut };

    // Per-element animation generation. A superseded storyboard's Completed
    // still fires at its scheduled end; without this guard it would Land the
    // element (Opacity=1, transform cleared) mid-way through a NEWER animation
    // on the same element (rapid tab re-entrance, staggered pulses) — a visible
    // snap. Each start bumps the element's generation; completion handlers only
    // touch the element when their generation is still current. UI-thread only,
    // so the bare increment needs no synchronization.
    private sealed class Generation { public int Value; }
    private static readonly ConditionalWeakTable<FrameworkElement, Generation> s_generations = new();

    private static int Bump(FrameworkElement el)
    {
        var g = s_generations.GetOrCreateValue(el);
        return ++g.Value;
    }

    private static bool IsCurrent(FrameworkElement el, int gen)
        => s_generations.TryGetValue(el, out var g) && g.Value == gen;

    // ── Entrance: fade + short upward slide ──────────────────────────────
    /// <summary>
    /// Fades <paramref name="el"/> in from 0 while sliding it up from
    /// <paramref name="slideY"/> px — the shared tab/section entrance idiom
    /// (mirrors <c>HubWorkspaceView.AnimatePanelRegionIn</c>). Clears the
    /// transform on completion so it can't interfere with later layout.
    /// </summary>
    public static void FadeSlideIn(FrameworkElement el, double slideY = 8, double durationMs = EntranceMs, double delayMs = 0)
    {
        if (el is null) return;
        int gen = Bump(el);
        try
        {
            var translate = new TranslateTransform { Y = slideY };
            el.RenderTransform = translate;
            el.Opacity = 0;

            var begin = TimeSpan.FromMilliseconds(delayMs);
            var dur = TimeSpan.FromMilliseconds(durationMs);

            var fade = new DoubleAnimation { From = 0, To = 1, BeginTime = begin, Duration = dur, EasingFunction = EaseOut() };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var slide = new DoubleAnimation { From = slideY, To = 0, BeginTime = begin, Duration = dur, EasingFunction = EaseOut() };
            Storyboard.SetTarget(slide, translate);
            Storyboard.SetTargetProperty(slide, "Y");

            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Children.Add(slide);
            sb.Completed += (_, _) => { if (IsCurrent(el, gen)) Land(el, clearTransform: true); };
            sb.Begin();
        }
        catch { Land(el, clearTransform: true); }
    }

    // ── Crossfade: opacity only ──────────────────────────────────────────
    /// <summary>
    /// Fades <paramref name="el"/> in from 0 (no slide) — for whole-region
    /// crossfades (empty-state → populated) where a slide would drag the
    /// whole column.
    /// </summary>
    public static void FadeIn(FrameworkElement el, double durationMs = EntranceMs)
    {
        if (el is null) return;
        int gen = Bump(el);
        try
        {
            el.Opacity = 0;
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(durationMs), EasingFunction = EaseOut() };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Completed += (_, _) => { if (IsCurrent(el, gen)) Land(el, clearTransform: false); };
            sb.Begin();
        }
        catch { Land(el, clearTransform: false); }
    }

    // ── Value flash: brief centred scale pulse ───────────────────────────
    /// <summary>
    /// A brief centred scale pulse (1 → <paramref name="peak"/> → 1) — the
    /// value-changed emphasis on a stat-tile value. Leaves the element
    /// visually identical once complete. <paramref name="delayMs"/> lets a
    /// caller stagger a follow-on pulse.
    /// </summary>
    public static void PulseScale(FrameworkElement el, double peak = 1.12, double durationMs = FlashMs, double delayMs = 0)
    {
        if (el is null) return;
        int gen = Bump(el);
        try
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform();
            el.RenderTransform = scale;

            var begin = TimeSpan.FromMilliseconds(delayMs);
            // Half the budget out, half back — AutoReverse plays the return
            // leg, so one From/To track covers the whole 1 → peak → 1 beat
            // inside durationMs (see AddPulseTrack for why that shape).
            var half = TimeSpan.FromMilliseconds(Math.Max(1, durationMs * 0.5));

            var sb = new Storyboard();
            AddPulseTrack(sb, scale, "ScaleX", peak, begin, half);
            AddPulseTrack(sb, scale, "ScaleY", peak, begin, half);
            sb.Completed += (_, _) => { if (IsCurrent(el, gen)) { try { el.RenderTransform = null; } catch { /* detached */ } } };
            sb.Begin();
        }
        catch { try { el.RenderTransform = null; } catch { /* detached */ } }
    }

    private static void AddPulseTrack(Storyboard sb, ScaleTransform scale, string prop, double peak, TimeSpan begin, TimeSpan half)
    {
        // A plain From/To DoubleAnimation on a RenderTransform sub-property,
        // deliberately WITHOUT EnableDependentAnimation — the flag is what a
        // dependent (UI-thread, per-frame) animation needs, and this one must
        // never become that. PulseScale is the value-changed flash on every
        // tool's stat tiles, so it fires on ordinary traffic (a 1 Hz timer
        // tick counts), and this app already fights UI-thread stalls; booking
        // 220 ms of per-frame UI-thread work per value change is the exact
        // pressure the file's own header promises to stay off. The previous
        // key-frame track carried the flag "defensively", contradicting that.
        //
        // AutoReverse replays the leg — and its easing — backwards, which
        // reproduces the old three-key shape (ease-out up, ease-in back to 1)
        // with a single timeline per axis, so nothing races another animation
        // for the same property. Peak lands mid-beat rather than at 40%; the
        // total duration is unchanged.
        var anim = new DoubleAnimation
        {
            From           = 1.0,
            To             = peak,
            BeginTime      = begin,
            Duration       = half,
            AutoReverse    = true,
            EasingFunction = EaseOut(),
        };
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, prop);
        sb.Children.Add(anim);
    }

    // ── Reveal: scale-up from <1 + fade ──────────────────────────────────
    /// <summary>
    /// Reveals <paramref name="el"/> by scaling it up from
    /// <paramref name="fromScale"/> to 1 while fading in — the marquee
    /// entrance for the Giveaway winner card. Clears the transform on
    /// completion.
    /// </summary>
    public static void RevealScaleFade(FrameworkElement el, double fromScale = 0.96, double durationMs = RevealMs)
    {
        if (el is null) return;
        int gen = Bump(el);
        try
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform { ScaleX = fromScale, ScaleY = fromScale };
            el.RenderTransform = scale;
            el.Opacity = 0;

            var dur = TimeSpan.FromMilliseconds(durationMs);
            var sb = new Storyboard();

            var fade = new DoubleAnimation { From = 0, To = 1, Duration = dur, EasingFunction = EaseOut() };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            AddScaleTo(sb, scale, "ScaleX", fromScale, dur);
            AddScaleTo(sb, scale, "ScaleY", fromScale, dur);

            sb.Completed += (_, _) => { if (IsCurrent(el, gen)) Land(el, clearTransform: true); };
            sb.Begin();
        }
        catch { Land(el, clearTransform: true); }
    }

    private static void AddScaleTo(Storyboard sb, ScaleTransform scale, string prop, double from, TimeSpan dur)
    {
        var anim = new DoubleAnimation { From = from, To = 1.0, Duration = dur, EnableDependentAnimation = true, EasingFunction = EaseOut() };
        Storyboard.SetTarget(anim, scale);
        Storyboard.SetTargetProperty(anim, prop);
        sb.Children.Add(anim);
    }

    // Pin the element to its resting state — full opacity, and (optionally)
    // no transform — so an interrupted or failed storyboard can never strand
    // it invisible or offset. Guarded because a torn-down element (rapid
    // tab / pop-out churn) may already be detached when this fires.
    private static void Land(FrameworkElement el, bool clearTransform)
    {
        try
        {
            el.Opacity = 1;
            if (clearTransform) el.RenderTransform = null;
        }
        catch { /* element detached — nothing to restore */ }
    }
}
