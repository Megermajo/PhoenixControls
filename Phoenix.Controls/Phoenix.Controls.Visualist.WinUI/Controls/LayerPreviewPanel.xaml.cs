using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// LayerPreviewPanel — design-time WebView2 host that loads
/// <c>http://127.0.0.1:18080/layer/&lt;id&gt;</c> from a running Hub. The same
/// compositor.js path OBS uses, so what shows here is what OBS sees.
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
            ShowPlaceholder("Save the layer first to preview.");
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
                ShowPlaceholder("Preview unavailable — install the Microsoft Edge WebView2 runtime.");
                _coreInitializing = false;
                return;
            }
            _coreInitializing = false;
        }

        if (!_coreInitialized || PreviewWeb.CoreWebView2 is null)
        {
            ShowPlaceholder("Preview unavailable — WebView2 did not initialise.");
            return;
        }

        string targetId = _pendingLayerId ?? layerId;
        _pendingLayerId = null;

        try
        {
            PreviewWeb.CoreWebView2.Navigate(
                $"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(targetId)}");
            ShowWebView();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewPanel", $"failed to point WebView2 at layer '{targetId}'", ex);
            ShowPlaceholder("Preview navigation failed — check Hub is running on port 18080.");
        }
    }

    /// <summary>Force-refresh the current layer (reloads the WebView2 source).</summary>
    public void RefreshLayer()
    {
        if (!_coreInitialized) return;
        try { PreviewWeb.CoreWebView2?.Reload(); }
        catch (Exception ex) { GlobalLogger.Error("LayerPreviewPanel", "reload failed", ex); }
    }

    // ── Timeline scrub bridge ────────────────────────────────────────────
    // TimelinePlayback emits scrub/play/stop directly into the WebView2 via
    // PostWebMessageAsJson rather than the Hub bus — a 30Hz bus round-trip
    // would saturate the envelope queue, and scrub is design-time-only.
    // compositor.js listens via chrome.webview.addEventListener('message').

    public void PostScrub(string widgetId, string triggerName, double timeMs)
        => PostJson(new { type = "SCRUB", widgetId, triggerName, timeMs });

    public void PostPlay(string widgetId, string triggerName, double durationMs, bool loop)
        => PostJson(new { type = "PLAY", widgetId, triggerName, durationMs, loop });

    public void PostStop()
        => PostJson(new { type = "STOP_PLAY" });

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

    private void PostJson(object payload)
    {
        if (!_coreInitialized) return;
        // Explicit guard against a torn-down
        // WebView2. PostJson is invoked from event handlers (timeline scrub /
        // play, widget-update) that may have been queued before the control
        // unloaded — at which point PreviewWeb (or its CoreWebView2) can be null.
        // Relying solely on the `?.` below would silently no-op, but reads the
        // null member first; checking up front makes the intent explicit and
        // skips the serialize work when there's no surface to post to.
        if (PreviewWeb?.CoreWebView2 is null) return;
        try
        {
            string json = JsonSerializer.Serialize(payload);
            PreviewWeb.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewPanel", "PostWebMessage failed", ex);
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
