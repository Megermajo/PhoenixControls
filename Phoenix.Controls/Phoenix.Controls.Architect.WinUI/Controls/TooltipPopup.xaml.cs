using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Controls;

/// <summary>
/// B14 (audit/winui-regressions-2026-05-24) — custom mouse-anchored tooltip
/// primitive that restores the pre-T15 rich tooltip surface (title + optional
/// glyph + body, dark Coal-themed pill with drop shadow, positioned relative
/// to the current pointer rather than the anchor element).
/// <para>
/// Two ways to drive it:
/// <list type="bullet">
///   <item><description>
///     Imperative: <see cref="Show(FrameworkElement, string, string?, string?, Brush?)"/> /
///     <see cref="Hide()"/> — a single shared <see cref="Popup"/> is reused across the
///     application so successive hovers cancel any in-flight popup before showing the
///     new content (no popup leak, no double-tip).
///   </description></item>
///   <item><description>
///     Declarative attachment (S5 P0 — restores the pre-T15 WinForms
///     <c>Tooltip.Attach</c>/<c>AttachDynamic</c>/<c>Detach</c> contract): wire a
///     FrameworkElement once via <see cref="Attach(FrameworkElement, string, string?, string?, Brush?)"/>
///     (static content) or <see cref="AttachDynamic(FrameworkElement, Func{Point, ValueTuple{string, string?, string?}}?)"/>
///     (per-cursor-position resolver), and the primitive owns the pointer
///     enter/move/exit handlers, the initial-show delay, the fast-reshow
///     optimisation and the auto-hide timer. <see cref="Detach(FrameworkElement)"/>
///     unhooks everything (also fired automatically on <see cref="FrameworkElement.Unloaded"/>).
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Timing mirrors the baseline WinForms <c>Tooltip</c> tunables (Design_Orders §4.9):
/// <see cref="InitialDelayMs"/>=400 before first paint, <see cref="ReshowDelayMs"/>=200
/// fast-reshow window (skip the initial delay when re-showing within that window of the
/// last Hide), and <see cref="AutoPopMs"/>=8000 auto-dismiss so a tip never persists
/// indefinitely if a PointerExited is dropped during a rapid window/tab switch.
/// </para>
/// </summary>
public sealed partial class TooltipPopup : UserControl
{
    // ─── Tunables (Design_Orders §4.9 — mirror baseline WinForms Tooltip) ───
    /// <summary>
    /// Hover dwell before the tip first paints. S5 P1: baseline
    /// <c>Tooltip.InitialDelayMs</c> was 400; the post-T15 call site used 600.
    /// Exposed here as the recommended default so call sites can arm against
    /// it rather than re-declaring a private 600 ms constant — and so the
    /// internal delayed-show path (Attach/AttachDynamic) uses the right value.
    /// </summary>
    public const int InitialDelayMs = 400;

    /// <summary>
    /// S5 P2: fast-reshow window. When <see cref="Show"/> is called within
    /// this many ms of the last <see cref="Hide"/>, the initial delay is
    /// skipped and the tip paints immediately — so scanning across adjacent
    /// pins feels snappy instead of re-incurring the full dwell each hop.
    /// </summary>
    public const int ReshowDelayMs = 200;

    /// <summary>
    /// S5 P1 (OWNER-OVERRIDE): auto-dismiss after this idle interval. Guards
    /// against a dropped PointerExited (rapid window deactivation / tab
    /// switch) leaving a tip floating indefinitely.
    /// </summary>
    public const int AutoPopMs = 8000;

    // S5 P2 (OWNER-OVERRIDE): anchor delta. Baseline AnchorOffset was (16, 24)
    // — the +12/+12 the post-T15 port used put the tip closer to the cursor
    // and higher than the designed placement.
    private const double AnchorOffsetX = 16;
    private const double AnchorOffsetY = 24;

    // S5 P2: screen-edge clearance, mirrors the baseline ClampToScreen 4px
    // safety margin. Enforced in the XamlRoot content coordinate space the
    // popup's Horizontal/VerticalOffset actually live in (see ClampToXamlRoot).
    private const double ScreenClearance = 4;

    // ─── Static reusable popup ──────────────────────────────────────────
    // A single shared Popup + TooltipPopup instance per XamlRoot, so the
    // hover handler doesn't have to allocate a Popup on every PointerEntered
    // (which fires at 60+ Hz on cursor moves). The anchor element supplies
    // the XamlRoot, and the popup body is parented to XamlRoot.Content's
    // PopupRoot.
    private static Popup? s_sharedPopup;
    private static TooltipPopup? s_sharedTooltip;
    private static FrameworkElement? s_currentAnchor;

    // S5 P1 (OWNER-OVERRIDE): auto-dismiss timer. Armed in Show() after
    // IsOpen flips true, stopped/restarted on every Show(), and torn down in
    // Hide(). DispatcherTimer ticks on the UI thread so Hide() is safe.
    private static DispatcherTimer? s_autoPopTimer;

    // S5 P2: tracks the last Hide() instant so Show() can detect a fast
    // reshow and skip the initial delay.
    private static DateTime s_lastHideTime = DateTime.MinValue;

    /// <summary>
    /// Last pointer position observed on the anchor element. Show() updates
    /// this from the args when called from a PointerMoved / PointerEntered
    /// handler; the popup's HorizontalOffset / VerticalOffset reflect this
    /// point plus the +16 / +24 offset Design_Orders §4.9 specifies.
    /// </summary>
    private static Point s_lastPointerPosition;

    // S5 P3: explicit "a real pointer position has been seeded" flag.
    // Replaces the (0,0) coordinate guard, which mis-fired when the cursor
    // genuinely sat at the XamlRoot origin (a valid coordinate) — forcing
    // the expensive TransformToVisual fallback on every legitimate origin
    // hover. Set by UpdatePointerPosition, cleared by Hide.
    private static bool s_pointerPositionWasSeeded;

    public TooltipPopup()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Public setter used by <see cref="Show(FrameworkElement, string, string?, string?, Brush?)"/>
    /// to push the title / body / glyph (and optional glyph colour) into the
    /// shared instance. Exposed public so callers that want to extend the
    /// primitive (e.g. richer inspector tooltips with multi-line body markup)
    /// can drive it directly.
    /// </summary>
    /// <param name="glyphColor">S5 P1 (OWNER-OVERRIDE): optional override for
    /// the glyph foreground. <c>null</c> restores the default
    /// <c>CoalPaperBrush</c> resource so successive shows don't inherit a
    /// stale colour from a prior tip.</param>
    public void SetContent(string title, string? body, string? glyph, Brush? glyphColor = null)
    {
        TitleText.Text = title ?? string.Empty;
        if (string.IsNullOrEmpty(body))
        {
            BodyText.Visibility = Visibility.Collapsed;
            BodyText.Text = string.Empty;
        }
        else
        {
            BodyText.Visibility = Visibility.Visible;
            BodyText.Text = body;
        }
        if (string.IsNullOrEmpty(glyph))
        {
            GlyphIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            GlyphIcon.Visibility = Visibility.Visible;
            GlyphIcon.Glyph = glyph;
            // S5 P1: dynamic glyph colour. Fall back to the XAML default
            // (CoalPaperBrush) when no override is supplied — resolved from the
            // shared instance's resources so the brush honours the dark theme.
            GlyphIcon.Foreground = glyphColor ?? ResolveDefaultGlyphBrush();
        }
    }

    /// <summary>
    /// Resolve the default glyph brush (CoalPaperBrush) from the control's
    /// resource scope. Falls back to the app-level resource, then to a literal
    /// near-white so the glyph never renders invisibly if the token is missing
    /// from this XamlRoot.
    /// </summary>
    private Brush ResolveDefaultGlyphBrush()
    {
        if (Resources.TryGetValue("CoalPaperBrush", out var local) && local is Brush localBrush)
            return localBrush;
        if (Application.Current?.Resources is { } appResources
            && appResources.TryGetValue("CoalPaperBrush", out var app) && app is Brush appBrush)
            return appBrush;
        return new SolidColorBrush(Microsoft.UI.Colors.WhiteSmoke);
    }

    /// <summary>
    /// Show a mouse-anchored tooltip near the current pointer position.
    /// </summary>
    /// <param name="anchor">FrameworkElement whose <see cref="UIElement.XamlRoot"/>
    /// hosts the popup. Required — the popup needs a XamlRoot to mount into;
    /// passing a non-loaded element silently no-ops.</param>
    /// <param name="title">Bold 12pt header line. Mandatory; an empty
    /// string is treated as "no title shown but still take vertical space"
    /// — most callers will want at least the socket / pill name here.</param>
    /// <param name="body">Optional 10pt regular subtitle. Collapsed when
    /// null or empty so the tip degrades cleanly to title-only.</param>
    /// <param name="glyph">Optional 16×16 Segoe-Fluent-Icons glyph code
    /// rendered to the left of the title. Use the Unicode code-point
    /// string (e.g. <c>""</c> for Info). Collapsed when null or
    /// empty.</param>
    /// <param name="glyphColor">S5 P1 (OWNER-OVERRIDE): optional glyph
    /// foreground override; <c>null</c> uses the default Coal paper brush.</param>
    public static void Show(FrameworkElement anchor, string title, string? body = null, string? glyph = null, Brush? glyphColor = null)
    {
        if (anchor is null) return;
        if (anchor.XamlRoot is null) return;

        // The whole show path is wrapped so a popup-mount failure (XamlRoot
        // torn down mid-hover, a resource that didn't resolve in this
        // XamlRoot, etc.) surfaces in the rolling diagnostic log instead of
        // silently swallowing the tooltip. If hover tooltips ever go missing
        // again, this is the breadcrumb to search for.
        try
        {
            // First call → allocate the shared popup + content control. Both
            // are kept alive for the process lifetime so subsequent Shows are
            // allocation-free.
            if (s_sharedPopup is null || s_sharedTooltip is null)
            {
                s_sharedTooltip = new TooltipPopup
                {
                    // Force dark regardless of the host XamlRoot's resolved
                    // theme — the popup mounts unparented, so it can't always
                    // inherit the app theme, and these tips must read on the
                    // dark Coal canvas (Majo: "dark on canvas").
                    RequestedTheme = ElementTheme.Dark,
                };
                s_sharedPopup = new Popup
                {
                    Child = s_sharedTooltip,
                    IsLightDismissEnabled = false,
                    ShouldConstrainToRootBounds = true,
                };
            }

            // Mount into the anchor's XamlRoot. WinUI 3 Popups require the
            // XamlRoot to be set before IsOpen flips true; switching XamlRoots
            // between Show() calls works as long as IsOpen is cycled (so we
            // close first if the root changed under us).
            if (s_sharedPopup.IsOpen && !ReferenceEquals(s_sharedPopup.XamlRoot, anchor.XamlRoot))
                s_sharedPopup.IsOpen = false;
            s_sharedPopup.XamlRoot = anchor.XamlRoot;
            s_currentAnchor = anchor;

            s_sharedTooltip.SetContent(title, body, glyph, glyphColor);

            // Position relative to the last pointer position recorded on the
            // anchor. The +16 / +24 offset keeps the tooltip out from under the
            // cursor while staying visually anchored to it (S5 P2 OWNER-OVERRIDE
            // — restores the baseline AnchorOffset of (16, 24); Design_Orders
            // §4.9). We resolve the pointer position via the anchor transform
            // if the caller hasn't pushed a position into s_lastPointerPosition.
            var pos = ResolvePointerPositionForAnchor(anchor);
            double offsetX = pos.X + AnchorOffsetX;
            double offsetY = pos.Y + AnchorOffsetY;

            // S5 P2: explicit screen-edge clamp with a 4px clearance, in the
            // XamlRoot content coordinate space the offsets live in.
            (offsetX, offsetY) = ClampToXamlRoot(anchor, s_sharedTooltip, offsetX, offsetY);

            s_sharedPopup.HorizontalOffset = offsetX;
            s_sharedPopup.VerticalOffset   = offsetY;

            if (!s_sharedPopup.IsOpen) s_sharedPopup.IsOpen = true;

            // S5 P1 (OWNER-OVERRIDE): (re)arm the auto-dismiss timer so the tip
            // self-closes if no Hide() arrives within AutoPopMs.
            ArmAutoPopTimer();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Architect.TooltipPopup", "Show", ex);
        }
    }

    /// <summary>
    /// S5 P1/P2 — request a tooltip after the configured initial delay, with
    /// the fast-reshow optimisation applied. When less than
    /// <see cref="ReshowDelayMs"/> ms has elapsed since the last <see cref="Hide"/>,
    /// the tip paints immediately; otherwise it paints after
    /// <see cref="InitialDelayMs"/> ms of dwell. The delay is owned by an
    /// internal <see cref="DispatcherTimer"/> so call sites no longer have to
    /// manage their own dwell timer (though they may — see the resolver-driven
    /// <see cref="Attach"/> path which uses this). A subsequent
    /// <see cref="Hide"/> or <see cref="ShowDelayed"/> cancels a still-pending
    /// delayed show.
    /// </summary>
    public static void ShowDelayed(FrameworkElement anchor, string title, string? body = null, string? glyph = null, Brush? glyphColor = null)
    {
        if (anchor is null || anchor.XamlRoot is null) return;

        bool fastReshow = (DateTime.UtcNow - s_lastHideTime).TotalMilliseconds < ReshowDelayMs;
        if (fastReshow)
        {
            Show(anchor, title, body, glyph, glyphColor);
            return;
        }

        // Arm (or re-arm) the shared delayed-show timer. Capture the request
        // so the tick fires the most recent content even if the user moved to
        // another surface within the delay window.
        s_pendingDelayedShow = new DelayedShowRequest(anchor, title, body, glyph, glyphColor);
        if (s_showDelayTimer is null)
        {
            s_showDelayTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(InitialDelayMs),
            };
            s_showDelayTimer.Tick += OnShowDelayTick;
        }
        else
        {
            s_showDelayTimer.Stop();
            s_showDelayTimer.Interval = TimeSpan.FromMilliseconds(InitialDelayMs);
        }
        s_showDelayTimer.Start();
    }

    private static DispatcherTimer? s_showDelayTimer;
    private static DelayedShowRequest? s_pendingDelayedShow;

    private readonly record struct DelayedShowRequest(
        FrameworkElement Anchor, string Title, string? Body, string? Glyph, Brush? GlyphColor);

    private static void OnShowDelayTick(object? sender, object e)
    {
        s_showDelayTimer?.Stop();
        if (s_pendingDelayedShow is not { } req) return;
        s_pendingDelayedShow = null;
        Show(req.Anchor, req.Title, req.Body, req.Glyph, req.GlyphColor);
    }

    /// <summary>
    /// Push a pointer position into the shared static so the next
    /// <see cref="Show(FrameworkElement, string, string?, string?, Brush?)"/> uses
    /// it as the anchor point. PointerMoved / PointerEntered handlers on
    /// hover surfaces feed the args' GetCurrentPoint(null).Position into
    /// here before calling Show — the position is in the XamlRoot's
    /// content-coordinate space so the popup's HorizontalOffset /
    /// VerticalOffset (also XamlRoot-relative) lines up.
    /// </summary>
    public static void UpdatePointerPosition(Point xamlRootPosition)
    {
        s_lastPointerPosition = xamlRootPosition;
        s_pointerPositionWasSeeded = true;
    }

    /// <summary>
    /// Close the shared tooltip. Idempotent — safe to call from a
    /// PointerExited handler that fires after the popup has already
    /// closed (e.g. when the cursor leaves the canvas entirely).
    /// </summary>
    public static void Hide()
    {
        // Cancel any pending delayed show so a dwell that hasn't yet elapsed
        // doesn't pop a tip after the cursor has already left.
        s_showDelayTimer?.Stop();
        s_pendingDelayedShow = null;

        s_autoPopTimer?.Stop();

        if (s_sharedPopup is not null && s_sharedPopup.IsOpen)
        {
            s_sharedPopup.IsOpen = false;
            // Only record the hide instant when we actually closed an open
            // tip, so the fast-reshow window measures real visible-to-visible
            // gaps rather than no-op Hide() spam.
            s_lastHideTime = DateTime.UtcNow;
        }
        s_currentAnchor = null;

        // S5 P3: reset the seeded flag so the next hover session re-seeds a
        // fresh pointer position (and the (0,0)-origin case is handled by the
        // flag, not an ambiguous coordinate test).
        s_pointerPositionWasSeeded = false;
    }

    // ─── S5 P0: control-attachment API (Attach / AttachDynamic / Detach) ────
    // Restores the pre-T15 WinForms Tooltip.Attach/AttachDynamic/Detach
    // contract on the WinUI primitive. Each attached element gets one
    // AttachmentRecord tracking its hover handlers; the record drives
    // ShowDelayed/Hide internally so callers no longer hand-wire pointer
    // events + dwell timers (the duplication the post-T15 call site grew).

    private static readonly Dictionary<FrameworkElement, AttachmentRecord> s_attachments = new();

    /// <summary>
    /// S5 P0 — attach a static tooltip to an element. Idempotent: re-attaching
    /// replaces the prior content/handlers. The primitive owns the pointer
    /// enter/move/exit wiring, the initial-show delay, the fast-reshow
    /// optimisation and the auto-hide timer for the lifetime of the
    /// attachment (until <see cref="Detach"/> or the element's Unloaded).
    /// </summary>
    public static void Attach(FrameworkElement element, string title, string? body = null, string? glyph = null, Brush? glyphColor = null)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        AttachInternal(element, _ => (title, body, glyph), glyphColor);
    }

    /// <summary>
    /// S5 P0 / P1 — attach a dynamic resolver invoked on every pointer move
    /// to compute per-cursor-position content (e.g. a different tip per socket
    /// under a single canvas element). The resolver receives the pointer
    /// position in the element's local coordinate space. Returning a tuple
    /// with a null/empty title suppresses the tip at that position.
    /// </summary>
    public static void AttachDynamic(FrameworkElement element, Func<Point, (string title, string? body, string? glyph)>? resolver)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (resolver is null) throw new ArgumentNullException(nameof(resolver));
        AttachInternal(element, resolver, glyphColor: null);
    }

    /// <summary>
    /// S5 P0 — remove any tooltip attachment from <paramref name="element"/>
    /// and unhook its pointer handlers. Safe to call on an un-attached
    /// element. Hides the shared popup if it's currently anchored to this
    /// element so a detach mid-hover doesn't strand a tip.
    /// </summary>
    public static void Detach(FrameworkElement element)
    {
        if (element is null) return;
        if (!s_attachments.Remove(element, out var record)) return;
        record.Unhook();
        if (ReferenceEquals(s_currentAnchor, element)) Hide();
    }

    private static void AttachInternal(
        FrameworkElement element,
        Func<Point, (string title, string? body, string? glyph)> resolver,
        Brush? glyphColor)
    {
        // Replace any previous attachment (unhook its handlers first).
        if (s_attachments.TryGetValue(element, out var existing))
            existing.Unhook();

        var record = new AttachmentRecord(element, resolver, glyphColor);
        s_attachments[element] = record;
        record.Hook();
    }

    /// <summary>
    /// Per-element attachment bookkeeping. Holds the resolver + the pointer
    /// handler delegates so they can be unhooked on Detach / Unloaded without
    /// leaking subscriptions.
    /// </summary>
    private sealed class AttachmentRecord
    {
        private readonly FrameworkElement _element;
        private readonly Func<Point, (string title, string? body, string? glyph)> _resolver;
        private readonly Brush? _glyphColor;

        private readonly PointerEventHandler _onEntered;
        private readonly PointerEventHandler _onMoved;
        private readonly PointerEventHandler _onExited;
        private readonly RoutedEventHandler _onUnloaded;

        public AttachmentRecord(
            FrameworkElement element,
            Func<Point, (string title, string? body, string? glyph)> resolver,
            Brush? glyphColor)
        {
            _element = element;
            _resolver = resolver;
            _glyphColor = glyphColor;

            _onEntered = OnPointerEntered;
            _onMoved = OnPointerMoved;
            _onExited = OnPointerExited;
            _onUnloaded = OnUnloaded;
        }

        public void Hook()
        {
            _element.PointerEntered += _onEntered;
            _element.PointerMoved += _onMoved;
            _element.PointerExited += _onExited;
            _element.PointerCanceled += _onExited;
            _element.PointerCaptureLost += _onExited;
            // A press (drag-start / click) dismisses the tip so it doesn't
            // float under a wire-drop drag.
            _element.PointerPressed += _onExited;
            _element.Unloaded += _onUnloaded;
        }

        public void Unhook()
        {
            _element.PointerEntered -= _onEntered;
            _element.PointerMoved -= _onMoved;
            _element.PointerExited -= _onExited;
            _element.PointerCanceled -= _onExited;
            _element.PointerCaptureLost -= _onExited;
            _element.PointerPressed -= _onExited;
            _element.Unloaded -= _onUnloaded;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            SeedAndResolve(e, fromMove: false);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            SeedAndResolve(e, fromMove: true);
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            Hide();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Auto-detach so an element that leaves the tree doesn't keep its
            // handlers (and its dictionary entry) alive.
            Detach(_element);
        }

        private void SeedAndResolve(PointerRoutedEventArgs e, bool fromMove)
        {
            if (_element.XamlRoot is null) return;

            // Seed the popup's anchor point in XamlRoot content space.
            try
            {
                var rootPt = e.GetCurrentPoint(null).Position;
                UpdatePointerPosition(rootPt);
            }
            catch
            {
                // Pre-realised tree — ResolvePointerPositionForAnchor falls
                // back to the element transform.
            }

            // Resolve content from the element-local pointer position.
            (string title, string? body, string? glyph) content;
            try
            {
                var localPt = e.GetCurrentPoint(_element).Position;
                content = _resolver(localPt);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("Architect.TooltipPopup", "AttachResolver", ex);
                return;
            }

            // A null/empty title suppresses the tip at this position.
            if (string.IsNullOrEmpty(content.title))
            {
                Hide();
                return;
            }

            // While the tip is already open (dynamic resolver "trailing" the
            // cursor), update content immediately so it tracks the cursor;
            // otherwise arm the delayed-show path so a flick across the element
            // doesn't pop a transient tip.
            bool alreadyOpenForUs = s_sharedPopup is { IsOpen: true } && ReferenceEquals(s_currentAnchor, _element);
            if (fromMove && alreadyOpenForUs)
                Show(_element, content.title, content.body, content.glyph, _glyphColor);
            else
                ShowDelayed(_element, content.title, content.body, content.glyph, _glyphColor);
        }
    }

    // ─── Auto-pop timer (S5 P1 OWNER-OVERRIDE) ──────────────────────────────

    private static void ArmAutoPopTimer()
    {
        if (s_autoPopTimer is null)
        {
            s_autoPopTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(AutoPopMs),
            };
            s_autoPopTimer.Tick += OnAutoPopTick;
        }
        else
        {
            s_autoPopTimer.Stop();
        }
        s_autoPopTimer.Start();
    }

    private static void OnAutoPopTick(object? sender, object e)
    {
        Hide();
    }

    // ─── Positioning ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve the cursor position for the anchor element. Prefers the
    /// last-recorded pointer position (pushed by
    /// <see cref="UpdatePointerPosition(Point)"/>); falls back to the
    /// anchor's top-right corner translated into XamlRoot space so the
    /// popup still appears in a sensible place when callers Show() without
    /// having seeded a pointer position (e.g. keyboard-driven hover).
    /// </summary>
    private static Point ResolvePointerPositionForAnchor(FrameworkElement anchor)
    {
        // S5 P3: use the explicit seeded flag rather than an (0,0)-coordinate
        // test. A cursor genuinely at the XamlRoot origin is a valid position
        // and must not force the TransformToVisual fallback.
        if (s_pointerPositionWasSeeded)
            return s_lastPointerPosition;
        try
        {
            var transform = anchor.TransformToVisual(null);
            return transform.TransformPoint(new Point(anchor.ActualWidth, 0));
        }
        catch (Exception ex)
        {
            // S5 P3: surface the layout failure instead of silently returning
            // origin. ResolvePointerPositionForAnchor runs inside Show()'s
            // try/catch, but a TransformToVisual fault here would otherwise be
            // swallowed without a breadcrumb. Log at Warning (repeatable, not
            // fatal — the tip still shows at origin) rather than popping a modal.
            GlobalLogger.Log(
                $"TransformToVisual fallback failed for tooltip anchor; defaulting to origin. {ex.Message}",
                "Architect.TooltipPopup",
                LogLevel.Warning);
            return new Point(0, 0);
        }
    }

    /// <summary>
    /// S5 P2 — clamp the popup offsets so the tip stays inside the XamlRoot
    /// content bounds with a <see cref="ScreenClearance"/> px margin, flipping
    /// to the opposite side of the cursor when it would overrun the right /
    /// bottom edge (mirrors the baseline ClampToScreen flip behaviour).
    /// <para>
    /// This works in the XamlRoot content (DIP) coordinate space the popup's
    /// Horizontal/VerticalOffset live in — deliberately NOT the monitor
    /// working-area pixel space the baseline WinForms ClampToScreen used.
    /// WinUI Popup offsets are XamlRoot-relative DIPs; clamping in raw screen
    /// pixels would require a fragile DIP↔scale↔window-origin round-trip and
    /// fight WinUI's own ShouldConstrainToRootBounds. Clamping to the root
    /// bounds with the 4px clearance honours the readability intent of the
    /// baseline margin in the coordinate space that actually positions the tip.
    /// </para>
    /// </summary>
    private static (double X, double Y) ClampToXamlRoot(
        FrameworkElement anchor, TooltipPopup tooltip, double offsetX, double offsetY)
    {
        var root = anchor.XamlRoot;
        if (root is null) return (offsetX, offsetY);

        var bounds = root.Size; // content size in DIPs
        if (bounds.Width <= 0 || bounds.Height <= 0) return (offsetX, offsetY);

        // Measure the tip against the available content area so we know its
        // footprint before it paints. MaxWidth (320) caps the width in XAML.
        tooltip.Measure(new Size(bounds.Width, bounds.Height));
        var desired = tooltip.DesiredSize;
        double w = desired.Width > 0 ? desired.Width : 0;
        double h = desired.Height > 0 ? desired.Height : 0;

        double anchorPointX = offsetX - AnchorOffsetX; // back out to the cursor
        double anchorPointY = offsetY - AnchorOffsetY;

        double x = offsetX;
        double y = offsetY;

        // Right edge: flip to the left of the cursor if there isn't room.
        if (x + w > bounds.Width - ScreenClearance)
            x = anchorPointX - AnchorOffsetX - w;
        if (x < ScreenClearance)
            x = ScreenClearance;

        // Bottom edge: flip above the cursor if there isn't room.
        if (y + h > bounds.Height - ScreenClearance)
            y = anchorPointY - AnchorOffsetY - h;
        if (y < ScreenClearance)
            y = ScreenClearance;

        return (x, y);
    }
}
