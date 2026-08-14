using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// LayerPreviewPanel — design-time WebView2 host that loads
/// <c>http://127.0.0.1:18080/layer/&lt;id&gt;?client=editor</c> from a running Hub.
/// The same compositor.js path OBS uses, so what shows here is what OBS sees —
/// with the one deliberate difference the query param declares: this surface is
/// design time, so a widget bound to a live-channel key that has no value yet may
/// show its <c>PreviewText</c> mock instead of the production blank. See the
/// Navigate call in <see cref="LoadLayerAsync"/>.
///
/// <para>WinUI re-architecture of the pre-T15 WinForms
/// <c>Phoenix.Controls.Visualist/Controls/LayerPreviewPanel.cs</c>. The
/// platform changes: the off-screen-host trick is unnecessary (this panel is a
/// visible control in a live tree, so the embedded
/// <c>Microsoft.UI.Xaml.Controls.WebView2</c> initialises normally) and the
/// <c>DefaultBackgroundColor</c> transparency is set through
/// <c>CoreWebView2.DefaultBackgroundColor</c> after init.</para>
///
/// <para>Lazy init: the WebView2 control is built in XAML but
/// <c>EnsureCoreWebView2Async</c> is only awaited the first time
/// <see cref="LoadLayerAsync"/> runs, so opening an editor that never shows the
/// preview pays no WebView2 startup cost. All failures log via
/// <see cref="GlobalLogger"/> and degrade to the placeholder — no modals.</para>
///
/// <para>This panel is Visualist-local.</para>
/// </summary>
public sealed partial class LayerPreviewPanel : UserControl
{
    private bool _coreInitialized;
    private bool _coreInitializing;
    private string? _pendingLayerId;

    public LayerPreviewPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Point the WebView2 at <c>/layer/{layerId}</c>. Triggers lazy WebView2 init
    /// on the first call. A null/empty <paramref name="layerId"/> shows the
    /// "save first" placeholder.
    /// </summary>
    public async Task LoadLayerAsync(string? layerId)
    {
        if (string.IsNullOrWhiteSpace(layerId))
        {
            ShowPlaceholder(Localizer.T("visualist.preview.layer.save_first",
                "Save the layer first to preview."));
            return;
        }

        if (!_coreInitialized)
        {
            if (_coreInitializing)
            {
                // Another LoadLayerAsync is racing init — record the latest
                // requested layer and let the in-flight call finish.
                _pendingLayerId = layerId;
                return;
            }
            _coreInitializing = true;
            _pendingLayerId = layerId;
            try
            {
                // Make the surface transparent so compositor.js's transparent
                // body exposes the panel background instead of WebView2's
                // default opaque white. DefaultBackgroundColor lives on the
                // WinUI WebView2 CONTROL (not CoreWebView2) and must be set
                // before EnsureCoreWebView2Async takes effect.
                try { PreviewWeb.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); }
                catch { /* older builds may not expose this — ignore */ }

                await PreviewWeb.EnsureCoreWebView2Async();
                _coreInitialized = PreviewWeb.CoreWebView2 is not null;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LayerPreviewPanel", "WebView2 init failed", ex);
                ShowPlaceholder(Localizer.T("visualist.preview.layer.webview_missing",
                    "Preview unavailable — install the Microsoft Edge WebView2 runtime."));
                _coreInitializing = false;
                return;
            }
            _coreInitializing = false;
        }

        if (!_coreInitialized || PreviewWeb.CoreWebView2 is null)
        {
            ShowPlaceholder(Localizer.T("visualist.preview.layer.webview_failed",
                "Preview unavailable — WebView2 did not initialise."));
            return;
        }

        string targetId = _pendingLayerId ?? layerId;
        _pendingLayerId = null;

        try
        {
            // ?client=editor marks this surface as DESIGN-TIME to compositor.js
            // (IS_DESIGN_TIME, alongside ?widget=<id> and ?capture=1). Without it a
            // bare /layer/<id> is byte-identical to what OBS loads, so the page
            // applies the production honest-data rule: a live-channel key with no
            // value paints NOTHING. Every channel-reading widget then rendered empty
            // in the one preview that shows a whole layer at once, which is exactly
            // where layout work happens — so laying out a countdown or a leaderboard
            // meant arranging invisible boxes.
            //
            // V6 — the SAME param now also carries a WIRE meaning, which is why the two
            // sibling surfaces (WidgetSinglePreviewPanel, WidgetCanvasPreviewer) gained it
            // too even though ?widget= / ?capture=1 already implied design-time page-side.
            // compositor.js forwards it onto its /hud/<id> WebSocket URL, and Hub reads it
            // to classify the socket: an editor socket does not count as live layer
            // presence, gets its own connection budget (so preview panes can no longer lock
            // a real OBS source out of the shared cap), and has its VISUAL_COMPLETE / FPS
            // frames refused — because a preview acking a widget it never drew is what
            // completed a script's wait_for_visual against a pane.
            //
            // The Inspector's Copy-OBS-URL affordance stays a clean production URL by
            // intent — that string is pasted into a Browser Source, where a design-time
            // flag would both re-enable the PreviewText mocks on a live stream AND make the
            // real overlay invisible to presence, so scripts would stop dispatching to it.
            PreviewWeb.CoreWebView2.Navigate(
                $"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(targetId)}?client=editor");
            ShowWebView();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewPanel", $"failed to point WebView2 at layer '{targetId}'", ex);
            ShowPlaceholder(Localizer.T("visualist.preview.layer.nav_failed",
                "Preview navigation failed — check Hub is running on port 18080."));
        }
    }

    // V14 removed the singular RefreshLayer() (a bare CoreWebView2.Reload()) —
    // nothing called it. Refreshes reach this panel through ShowLayer(), which
    // re-navigates with the current ?client=editor URL; a blind Reload() would
    // have replayed whatever URL happened to be loaded, including a stale
    // pre-V6 one without the editor-socket classification.
    // ★ Do not confuse it with the plural VisualistViewModel.RefreshLayers(),
    // which re-enumerates data/layers/ and is live in nine places.

    // ── Timeline scrub bridge ────────────────────────────────────────────
    // TimelinePlayback emits scrub/play/stop directly into the WebView2 via
    // PostWebMessageAsJson rather than the Hub bus — a 30Hz bus round-trip
    // would saturate the envelope queue, and scrub is design-time-only.
    // compositor.js listens via chrome.webview.addEventListener('message').

    /// <summary>
    /// Render <paramref name="widgetId"/>'s <paramref name="triggerName"/> at
    /// <paramref name="timeMs"/>.
    ///
    /// <para>Returns TRUE only when the message actually reached the page. The WebView2
    /// bridge is lazily initialised (and torn down with the popout), so
    /// <see cref="PostJson"/> silently no-ops whenever <c>CoreWebView2</c> is not there
    /// yet — the documented intermittent case. Callers that REPORT a reached surface to
    /// the author (Test Run's status line) must gate on this result: counting a pane as
    /// reached from a non-null reference plus a Visibility check claims a target that took
    /// nothing, which is the exact class of false confirmation that affordance exists to
    /// remove. Fire-and-forget callers (the 30 Hz timeline scrub) can keep ignoring it.</para>
    /// </summary>
    public bool PostScrub(string widgetId, string triggerName, double timeMs)
        => PostJson(new { type = "SCRUB", widgetId, triggerName, timeMs });

    /// <summary>Start timeline playback. Returns true only when the message reached the
    /// page — see <see cref="PostScrub"/> for why the result is load-bearing.</summary>
    public bool PostPlay(string widgetId, string triggerName, double durationMs, bool loop)
        => PostJson(new { type = "PLAY", widgetId, triggerName, durationMs, loop });

    public void PostStop()
        => PostJson(new { type = "STOP_PLAY" });

    /// <summary>
    /// Release every design-time clock pin on the page, handing the widgets back to the
    /// ambient production clock.
    ///
    /// <para>Distinct from <see cref="PostStop"/> because pause and stop mean different
    /// things: STOP_PLAY holds the frame the transport stopped on, which is what an author
    /// pausing wants, while this is the "I am done scrubbing" gesture. Without it the
    /// whole-layer preview had no reachable release at all — a bare playhead drag pinned a
    /// widget for the page's life and killed its ambient animation.</para>
    /// </summary>
    public void PostReleaseTimeCursor()
        => PostJson(new { type = "RELEASE_TIME_CURSOR" });

    /// <summary>
    /// Push a live rect change for a widget into the WebView2 so the preview
    /// reflects detail-panel / drag edits without a save + LayerWatcher reload.
    /// </summary>
    public void PostWidgetUpdate(LayerWidget widget)
    {
        if (widget is null) return;
        PostJson(new
        {
            type     = "WIDGET_UPDATE",
            widgetId = widget.Id,
            name     = widget.Name,
            zIndex   = widget.ZIndex,
            rect     = new { x = widget.Rect.X, y = widget.Rect.Y, width = widget.Rect.Width, height = widget.Rect.Height },
        });
    }

    /// <summary>
    /// Post one design-time message into the page. Returns TRUE only when
    /// <c>PostWebMessageAsJson</c> actually ran — every early-out and every throw returns
    /// false so a caller can tell "delivered" from "silently dropped" (see
    /// <see cref="PostScrub"/>).
    /// </summary>
    private bool PostJson(object payload)
    {
        if (!_coreInitialized) return false;
        // Explicit guard against a torn-down
        // WebView2. PostJson is invoked from event handlers (timeline scrub /
        // play, widget-update) that may have been queued before the control
        // unloaded — at which point PreviewWeb (or its CoreWebView2) can be null.
        // Relying solely on the `?.` below would silently no-op, but reads the
        // null member first; checking up front makes the intent explicit and
        // skips the serialize work when there's no surface to post to.
        if (PreviewWeb?.CoreWebView2 is null) return false;
        try
        {
            string json = JsonSerializer.Serialize(payload);
            PreviewWeb.CoreWebView2.PostWebMessageAsJson(json);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewPanel", "PostWebMessage failed", ex);
            return false;
        }
    }

    private void ShowPlaceholder(string text)
    {
        Placeholder.Text       = text;
        Placeholder.Visibility = Visibility.Visible;
        PreviewWeb.Visibility  = Visibility.Collapsed;
    }

    private void ShowWebView()
    {
        Placeholder.Visibility = Visibility.Collapsed;
        PreviewWeb.Visibility  = Visibility.Visible;
    }

    /// <summary>Release the WebView2 — call from the host window's Closed handler
    /// so the browser process tears down with the popout.</summary>
    public void Shutdown()
    {
        try { PreviewWeb.Close(); } catch { }
    }
}
