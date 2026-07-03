using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Core;
using Windows.Foundation;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// WidgetSinglePreviewPanel — embedded per-widget preview pane. Loads
/// <c>http://127.0.0.1:18080/layer/&lt;id&gt;?widget=&lt;widgetId&gt;&amp;bg=&lt;color&gt;</c>
/// in a WebView2; compositor.js resizes its canvas to the widget's rect, so the
/// pane shows ONLY that widget filling the available area. Same renderer as the
/// OBS source.
///
/// <para>WinUI re-architecture of the pre-T15 WinForms
/// <c>WidgetSinglePreviewPanel</c>. The grip-drag width resize (persisted to
/// <see cref="VisualistUserConfig.EditorPreviewWidth"/>), the aspect letterbox,
/// and the manipulator/ATTR_CHANGED bridge are all preserved; the platform
/// changes are WinForms <c>OnMouseDown/Move/Up</c> → WinUI pointer events and
/// <c>WebMessageReceived</c> → <c>CoreWebView2.WebMessageReceived</c>.</para>
///
/// <para>Width-only resize: the owner anchors the pane top-right and lets the
/// grip set the width; the height auto-derives from the widget's pixel aspect so
/// an 800×200 widget previews as a wide strip and a 1080×1920 widget as a tall
/// slice. The owner persists the new width through <see cref="OnResized"/>.</para>
///
/// <para>This panel is Visualist-local; all failures log via
/// <see cref="GlobalLogger"/>, no modals.</para>
/// </summary>
public sealed partial class WidgetSinglePreviewPanel : UserControl
{
    public const int MinPreviewWidth = 200;
    public const int MaxPreviewWidth = 1200;
    private const int GripBandHeight = 14;

    private bool _coreInitialized;
    private bool _coreInitializing;
    private string? _pendingLayerId;
    private string? _pendingWidgetId;
    // One-shot retry arming for the detached-tree init race (see LoadAsync).
    private bool _initRetryArmed;

    // The widget this pane is currently rendering (?widget=<id>), kept past the
    // navigate so the timeline-transport bridge (PostScrub/PostPlay) can address
    // it without the caller re-supplying the id — the embedded pane owns its own
    // widget identity, unlike the full-layer popout. Set on each LoadAsync.
    private string? _widgetId;

    // The trigger the editor is currently editing. Drives which trigger graph the
    // compositor samples for SCRUB/PLAY/SET_ACTIVE_TRIGGER. Defaults to onStartup
    // so a scrub before the first SetActiveTrigger still resolves a real trigger
    // (matching compositor.js's page-load behaviour). Updated by SetActiveTrigger.
    private string _activeTriggerName = "onStartup";

    // Load-race guard. Navigate() returns BEFORE the new page's compositor.js
    // installs its window.onmessage handler, so a SET_ACTIVE_TRIGGER posted right
    // after LoadAsync is dropped (or hits the outgoing document) — the page's first
    // renderAll() then reads previewActiveTrigger === null and paints the onStartup
    // fallback instead of the trigger the editor is actually editing. We therefore
    // record the requested trigger (and the playhead the editor last scrubbed to)
    // and RE-SEND both once OnNavigationCompleted confirms the new document is live
    // (its onmessage handler is now installed; handleSetActiveTrigger pins
    // previewActiveTrigger synchronously so even an in-flight /api/layer fetch's
    // renderAll() picks it up). _pendingActiveTrigger stays armed across the whole
    // session so a Refresh()/recovery re-navigate also re-pins the trigger.
    private string _pendingActiveTrigger = "onStartup";
    private double _lastScrubMs;

    // Recover from a navigation that hit Hub's bodyless 404/5xx (e.g. racing the
    // layer's atomic-save swap). Bounded so a genuinely-missing layer settles on
    // the error page instead of reloading forever. Reset on each fresh navigate.
    private int _navFailRetries;
    private const int MaxNavFailRetries = 5;
    private string? _lastNavUrl;

    // Widget pixel aspect (width / height). When > 0 the owner sizes the pane
    // height from Width / aspect via ApplyAspectHeight so the preview keeps the
    // widget's ratio without manual resizing.
    private double _aspect;

    // Grip-drag state. Drag-left grows the pane, drag-right shrinks it; the
    // owner keeps the right edge anchored. Width is clamped to [Min,Max].
    private bool _resizing;
    private double _resizeStartPointerX;
    private int _resizeStartWidth;

    /// <summary>Fired when the user releases a grip drag, so the owner can
    /// persist the new width to <see cref="VisualistUserConfig.EditorPreviewWidth"/>
    /// and re-anchor the floating pane.</summary>
    public event Action<int>? OnResized;

    /// <summary>Fired (UI thread) when compositor.js posts an ATTR_CHANGED
    /// message after a manipulator drag completes. The editor merges the attrs
    /// into the in-memory node so undo / save / OBS catch up.</summary>
    public event Action<ManipulatorAttrChange>? OnManipulatorAttrChanged;

    /// <summary>Fired (UI thread) when compositor.js forwards a Ctrl+Z while the
    /// embedded preview WebView2 holds keyboard focus. After a manipulator
    /// drag focus lives in the WebView2, which swallows Ctrl+Z so it never reaches
    /// the document hotkey handler — the preview forwards it here instead so undo
    /// works on transform edits regardless of where focus landed.</summary>
    public event Action? OnUndoRequested;

    /// <summary>Fired (UI thread) when compositor.js forwards a Ctrl+Shift+Z / Ctrl+Y
    /// from the embedded preview (redo counterpart of OnUndoRequested).</summary>
    public event Action? OnRedoRequested;

    public WidgetSinglePreviewPanel()
    {
        InitializeComponent();

        ResizeGrip.PointerPressed  += OnGripPointerPressed;
        ResizeGrip.PointerMoved    += OnGripPointerMoved;
        ResizeGrip.PointerReleased += OnGripPointerReleased;
        ResizeGrip.PointerEntered  += (_, _) => SetGripCursor(true);
        ResizeGrip.PointerExited   += (_, _) => { if (!_resizing) SetGripCursor(false); };
    }

    private void SetGripCursor(bool resize)
    {
        try
        {
            ProtectedCursor = InputSystemCursor.Create(
                resize ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
        }
        catch { /* cursor API can be unavailable in some hosts — ignore */ }
    }

    // ── Aspect-fit ────────────────────────────────────────────────────────

    /// <summary>Set the widget's pixel aspect ratio (width / height). Pass &lt;= 0
    /// to disable aspect-fit. Recomputes the pane height from the current width
    /// and raises <see cref="OnResized"/> so the owner re-anchors.</summary>
    public void SetWidgetAspect(double aspect)
    {
        if (Math.Abs(_aspect - aspect) < 0.0001) return;
        _aspect = aspect > 0 ? aspect : 0;
        ApplyAspectHeight();
    }

    /// <summary>Recompute pane height from the current width + aspect + the grip
    /// band so the rendered widget area keeps its ratio after a width change.</summary>
    public void ApplyAspectHeight()
    {
        if (_aspect <= 0) return;
        double contentW = Math.Max(1, Width > 0 ? Width : ActualWidth);
        if (contentW <= 0) return;
        Height = (contentW / _aspect) + GripBandHeight;
    }

    // ── Load / navigate ─────────────────────────────────────────────────

    /// <summary>
    /// Build the URL for a (layerId, widgetId) pair. Public so tests can assert
    /// the wire-format without a real WebView2. Transparent bg skips the
    /// &amp;bg= param so the page stays transparent.
    /// </summary>
    public static Uri BuildUrl(string layerId, string widgetId, PreviewBgColor bg)
    {
        string url = $"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(layerId)}"
                   + $"?widget={Uri.EscapeDataString(widgetId)}";
        string token = bg.ToQueryToken();
        if (!string.IsNullOrEmpty(token)) url += $"&bg={token}";
        return new Uri(url);
    }

    /// <summary>
    /// Point the WebView2 at (layerId, widgetId). Null/empty either → placeholder.
    /// Triggers lazy WebView2 init the first time; concurrent calls collapse to
    /// the most recent params.
    /// </summary>
    public async Task LoadAsync(string? layerId, string? widgetId)
    {
        if (string.IsNullOrWhiteSpace(layerId) || string.IsNullOrWhiteSpace(widgetId))
        {
            ShowPlaceholder("Save the layer first to preview.");
            return;
        }

        if (!VisualistUserConfig.Instance.EditorEmbeddedPreviewEnabled)
        {
            ShowPlaceholder("Embedded preview is turned off in settings.");
            return;
        }

        // Detached-tree init guard (Majo's recurring "WebView2 did not
        // initialise" — intermittent). EnterWidget sets SelectedWidget (which
        // synchronously kicks this LoadAsync) BEFORE ShowWidgetEditor parents
        // the editor into MainPaneRegion. So on the canvas → enter-widget path
        // this panel is NOT yet in the live visual tree: it has no compositor
        // surface, and EnsureCoreWebView2Async would return CoreWebView2==null
        // → the placeholder. (It "sometimes works" only when the editor tab is
        // already showing — hence intermittent.) The earlier fix flipped the
        // WebView2's own Visibility, but a Visible child of a detached parent
        // still has no surface. Defer init until we're actually loaded + sized
        // + rooted, and re-run once Loaded/SizeChanged confirm it.
        if (!_coreInitialized && !IsPanelRealized())
        {
            _pendingLayerId  = layerId;
            _pendingWidgetId = widgetId;
            ShowPlaceholder("Loading preview…");
            ArmInitRetry();
            return;
        }

        if (!_coreInitialized)
        {
            if (_coreInitializing)
            {
                _pendingLayerId  = layerId;
                _pendingWidgetId = widgetId;
                return;
            }
            _coreInitializing = true;
            _pendingLayerId   = layerId;
            _pendingWidgetId  = widgetId;
            try
            {
                // A Collapsed WinUI 3 WebView2 never realises a CoreWebView2 — it
                // gets no compositor surface, so EnsureCoreWebView2Async returns
                // with CoreWebView2 == null and we fall straight to the
                // "did not initialise" placeholder (the bug Majo hit). Make the
                // control Visible BEFORE init so it's arranged in the live visual
                // tree. The Placeholder is a later sibling in the same Grid cell so
                // it stays on top (no blank flash) until ShowWebView() hides it on a
                // successful navigate. This is the inline-panel equivalent of the
                // activated-host-window trick WidgetCanvasPreviewer uses.
                PreviewWeb.Visibility = Visibility.Visible;

                // DefaultBackgroundColor lives on the WinUI WebView2 CONTROL
                // (not CoreWebView2) — transparent so the page's transparent
                // body exposes the panel background.
                try { PreviewWeb.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0); }
                catch { /* older builds — ignore */ }

                await PreviewWeb.EnsureCoreWebView2Async();
                _coreInitialized = PreviewWeb.CoreWebView2 is not null;
                if (PreviewWeb.CoreWebView2 is not null)
                {
                    PreviewWeb.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                    PreviewWeb.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("WidgetSinglePreviewPanel", "WebView2 init failed", ex);
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

        string l = _pendingLayerId  ?? layerId;
        string w = _pendingWidgetId ?? widgetId;
        _pendingLayerId  = null;
        _pendingWidgetId = null;
        // Remember the rendered widget so the timeline-transport bridge can
        // address it without the caller re-passing the id (see PostScrub etc.).
        _widgetId = w;

        try
        {
            // Fresh target → full recovery budget (see OnNavigationCompleted).
            _navFailRetries = 0;
            _lastNavUrl = BuildUrl(l, w, VisualistUserConfig.Instance.PreviewBackground).ToString();
            PreviewWeb.CoreWebView2.Navigate(_lastNavUrl);
            // Keep the loading placeholder up (covering the WebView2) until
            // NavigationCompleted confirms a healthy page, so a transient 404 never
            // shows the browser's "page can't be found". ShowWebView() is deferred to
            // the success branch of OnNavigationCompleted.
            ShowPlaceholder("Loading preview…");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetSinglePreviewPanel", $"failed to point WebView2 at widget '{w}'", ex);
            ShowPlaceholder("Preview navigation failed — check Hub is running on port 18080.");
        }
    }

    // ── Detached-tree init retry (issue: "WebView2 did not initialise") ──────

    /// <summary>True when this panel is actually live in the visual tree AND
    /// arranged with a non-zero size — the precondition for
    /// EnsureCoreWebView2Async to obtain a compositor surface and return a
    /// non-null CoreWebView2.</summary>
    private bool IsPanelRealized()
        => IsLoaded && XamlRoot is not null && ActualWidth >= 1 && ActualHeight >= 1;

    /// <summary>Arm a one-shot retry that re-runs the deferred
    /// <see cref="LoadAsync"/> the moment the panel becomes realized. Loaded
    /// fires when EnterWidget's ShowWidgetEditor finally roots the editor;
    /// SizeChanged covers the case where Loaded already fired but the first
    /// arrange (non-zero ActualWidth) hadn't completed yet.</summary>
    private void ArmInitRetry()
    {
        if (_initRetryArmed) return;
        _initRetryArmed = true;
        Loaded      += OnPanelReadyRetry;
        SizeChanged += OnPanelSizeRetry;
    }

    private void OnPanelReadyRetry(object sender, RoutedEventArgs e) => TryInitRetry();
    private void OnPanelSizeRetry(object sender, SizeChangedEventArgs e) => TryInitRetry();

    private void TryInitRetry()
    {
        if (!IsPanelRealized()) return;   // still not ready — wait for the next event
        Loaded      -= OnPanelReadyRetry;
        SizeChanged -= OnPanelSizeRetry;
        _initRetryArmed = false;
        if (!string.IsNullOrWhiteSpace(_pendingLayerId) && !string.IsNullOrWhiteSpace(_pendingWidgetId))
            _ = LoadAsync(_pendingLayerId, _pendingWidgetId);
    }

    /// <summary>Force-reload the current widget URL (after a config bg change or
    /// a manual save).</summary>
    public void Refresh()
    {
        if (!_coreInitialized) return;
        try { PreviewWeb.CoreWebView2?.Reload(); }
        catch (Exception ex) { GlobalLogger.Error("WidgetSinglePreviewPanel", "reload failed", ex); }
    }

    // ── Manipulator bridge ───────────────────────────────────────────────

    /// <summary>Push the active node into the in-page manipulator overlay so its
    /// handles render. Pass <paramref name="node"/> = null to clear.</summary>
    public void SetActiveNode(string widgetId, string triggerName, Node? node)
    {
        if (!_coreInitialized) return;
        if (node is null)
        {
            PostJson(new { type = "CLEAR_MANIPULATOR" });
            return;
        }
        var attrs = new Dictionary<string, string>(node.Attributes ?? new Dictionary<string, string>());
        PostJson(new
        {
            type        = "SET_MANIPULATOR",
            widgetId,
            triggerName,
            nodeId      = node.Id,
            nodeKind    = node.Title ?? "",
            attrs,
        });
    }

    /// <summary>Push a live rect change so the embedded preview reflects
    /// detail-panel / drag edits without a save + LayerWatcher reload.</summary>
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

    // ── Timeline-transport bridge (drives the embedded preview from the editor's
    //    timeline scrubber + play/stop transport) ──────────────────────────────
    //
    // The embedded pane renders a single ?widget=<id> page, so unlike the full-
    // layer LayerPreviewPanel (whose caller passes widgetId/triggerName per call)
    // this pane tracks its own widget (_widgetId, set on LoadAsync) and active
    // trigger (_activeTriggerName, set by SetActiveTrigger). The messages on the
    // wire are byte-identical to LayerPreviewPanel's so compositor.js handles both
    // the same way. PostJson already guards a not-yet-ready / torn-down
    // CoreWebView2 (no-op), so a transport call before init is safe.

    /// <summary>Tell the preview which trigger the editor is now editing. Tracks
    /// the name for subsequent <see cref="PostScrub"/>/<see cref="PostPlay"/> and
    /// posts SET_ACTIVE_TRIGGER so compositor.js re-renders that trigger at time 0
    /// (its idle/start state) instead of staying on onStartup.</summary>
    public void SetActiveTrigger(string triggerName)
    {
        string resolved = string.IsNullOrEmpty(triggerName) ? "onStartup" : triggerName;
        // Switching to a different trigger resets the compositor to that trigger's
        // t=0 (handleSetActiveTrigger renders timeMs=0), so drop the previous
        // trigger's last-scrub — otherwise a later re-navigate would restore a
        // playhead that belonged to the old trigger.
        if (!string.Equals(resolved, _activeTriggerName, StringComparison.Ordinal))
            _lastScrubMs = 0;
        _activeTriggerName = resolved;
        // Arm the value for the post-navigation re-send (see _pendingActiveTrigger
        // + OnNavigationCompleted). This is what makes the call robust to the load
        // race: even if the synchronous PostJson below is dropped because the page
        // isn't ready, the re-send on NavigationCompleted will pin the trigger.
        _pendingActiveTrigger = _activeTriggerName;
        if (string.IsNullOrEmpty(_widgetId)) return;
        PostJson(new { type = "SET_ACTIVE_TRIGGER", widgetId = _widgetId, triggerName = _activeTriggerName });
    }

    /// <summary>Render the active trigger at <paramref name="timeMs"/> — the
    /// timeline scrub / playhead-position consumer. No-op until the pane knows its
    /// widget (after the first <see cref="LoadAsync"/>).</summary>
    public void PostScrub(double timeMs)
    {
        // Remember the last scrubbed playhead so the post-navigation re-send
        // (OnNavigationCompleted) can restore the same frame the editor was
        // showing before a reload/recovery navigate, instead of snapping to t=0.
        _lastScrubMs = timeMs;
        if (string.IsNullOrEmpty(_widgetId)) return;
        PostJson(new { type = "SCRUB", widgetId = _widgetId, triggerName = _activeTriggerName, timeMs });
    }

    /// <summary>Start timeline playback of the active trigger in the preview.</summary>
    public void PostPlay(double durationMs, bool loop)
    {
        if (string.IsNullOrEmpty(_widgetId)) return;
        PostJson(new { type = "PLAY", widgetId = _widgetId, triggerName = _activeTriggerName, durationMs, loop });
    }

    /// <summary>Stop / reset timeline playback in the preview.</summary>
    public void PostStop()
        => PostJson(new { type = "STOP_PLAY" });

    private void PostJson(object payload)
    {
        if (!_coreInitialized) return;
        // Explicit guard against a torn-down
        // WebView2. PostJson is reachable from SetActiveNode / PostWidgetUpdate /
        // manipulator events that may fire after the pane unloaded, where
        // PreviewWeb (or its CoreWebView2) can be null. Guarding up front makes
        // the no-op intent explicit and skips the serialize when there's nothing
        // to post to, rather than dereferencing through `?.` after the work.
        if (PreviewWeb?.CoreWebView2 is null) return;
        try
        {
            string json = JsonSerializer.Serialize(payload);
            PreviewWeb.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetSinglePreviewPanel", "PostWebMessage failed", ex);
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // IsSuccess is false only for network-level failures; an HTTP 404/5xx still
        // "completes" with IsSuccess=true, so also gate on the status code (older
        // WebView2 runtimes throw on HttpStatusCode — treat that as "assume ok").
        bool httpOk;
        try { int code = e.HttpStatusCode; httpOk = code == 0 || (code >= 200 && code < 400); }
        catch { httpOk = true; }
        if (e.IsSuccess && httpOk)
        {
            // Healthy page — reveal it (the loading placeholder was covering it).
            _navFailRetries = 0;
            ShowWebView();
            // Load-race fix. The new document's compositor.js onmessage handler
            // is installed by the time NavigationCompleted fires, but its first
            // renderAll() (after the /api/layer fetch resolves) reads
            // previewActiveTrigger — which is null unless a SET_ACTIVE_TRIGGER
            // arrived AFTER the handler was wired. The SET_ACTIVE_TRIGGER posted
            // synchronously from LoadAsync raced ahead of the handler and was
            // dropped, so without this re-send the pane shows the onStartup
            // fallback. Re-pin the active trigger now (handleSetActiveTrigger sets
            // previewActiveTrigger synchronously, so even an in-flight fetch's
            // renderAll() honours it) and re-scrub to the editor's current playhead
            // so the frame matches the timeline, not t=0. Guarded by _widgetId
            // inside SetActiveTrigger/PostScrub, so it's a no-op before the first
            // LoadAsync resolves the widget id.
            if (!string.IsNullOrEmpty(_widgetId))
            {
                PostJson(new { type = "SET_ACTIVE_TRIGGER", widgetId = _widgetId, triggerName = _pendingActiveTrigger });
                if (_lastScrubMs > 0)
                    PostJson(new { type = "SCRUB", widgetId = _widgetId, triggerName = _pendingActiveTrigger, timeMs = _lastScrubMs });
            }
            return;
        }

        // Failed (Hub's bodyless 404/5xx — racing the layer's atomic-save swap, or a
        // just-saved layer Hub hasn't registered yet). Keep the placeholder covering
        // the WebView2 so the user never sees the browser's "page can't be found",
        // then re-navigate a few times so a transient miss heals on its own. Bounded —
        // a genuinely-missing layer ends on a clear message, not the browser error.
        if (_navFailRetries >= MaxNavFailRetries)
        {
            ShowPlaceholder("Preview unavailable — the layer wasn't found. Save the layer, then reselect the widget.");
            return;
        }
        _navFailRetries++;
        ShowPlaceholder("Loading preview…");
        _ = RetryNavigateAfterDelayAsync();
    }

    private async System.Threading.Tasks.Task RetryNavigateAfterDelayAsync()
    {
        try { await System.Threading.Tasks.Task.Delay(400); }
        catch { return; }
        DispatcherQueue?.TryEnqueue(() =>
        {
            // Re-navigate to the exact target (not Reload — the current document is
            // Hub's error page; Navigate re-requests the real layer URL).
            try { if (_lastNavUrl is not null) PreviewWeb?.CoreWebView2?.Navigate(_lastNavUrl); }
            catch { /* torn down */ }
        });
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try { raw = e.WebMessageAsJson; }
        catch { return; }
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
            string msgType = typeEl.GetString() ?? "";

            // Undo / redo forwarded from compositor.js when the embedded
            // preview WebView2 holds keyboard focus (e.g. right after a manipulator
            // drag). The WebView2 swallows Ctrl+Z so the document hotkey on the graph
            // canvas never sees it; the preview forwards it here so undo still works.
            if (msgType == "REQUEST_UNDO")
            {
                try { OnUndoRequested?.Invoke(); }
                catch (Exception ex) { GlobalLogger.Error("WidgetSinglePreviewPanel", "OnUndoRequested subscriber threw", ex); }
                return;
            }
            if (msgType == "REQUEST_REDO")
            {
                try { OnRedoRequested?.Invoke(); }
                catch (Exception ex) { GlobalLogger.Error("WidgetSinglePreviewPanel", "OnRedoRequested subscriber threw", ex); }
                return;
            }
            if (msgType != "ATTR_CHANGED") return;

            string widgetId    = doc.RootElement.TryGetProperty("widgetId",    out var w) ? (w.GetString() ?? "") : "";
            string triggerName = doc.RootElement.TryGetProperty("triggerName", out var t) ? (t.GetString() ?? "") : "";
            string nodeId      = doc.RootElement.TryGetProperty("nodeId",      out var n) ? (n.GetString() ?? "") : "";
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("attrs", out var attrsEl)
                && attrsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in attrsEl.EnumerateObject())
                {
                    attrs[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        _                    => prop.Value.GetRawText(),
                    };
                }
            }
            if (string.IsNullOrEmpty(nodeId) || attrs.Count == 0) return;
            var change = new ManipulatorAttrChange(widgetId, triggerName, nodeId, attrs);

            // CoreWebView2.WebMessageReceived already marshals to the thread that
            // owns the WebView2 (the UI thread), so invoke the subscriber directly.
            try { OnManipulatorAttrChanged?.Invoke(change); }
            catch (Exception ex) { GlobalLogger.Error("WidgetSinglePreviewPanel", "OnManipulatorAttrChanged subscriber threw", ex); }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetSinglePreviewPanel", "ATTR_CHANGED parse failed", ex);
        }
    }

    // ── Resize grip ──────────────────────────────────────────────────────

    private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(this);
        if (!pp.Properties.IsLeftButtonPressed) return;
        _resizing            = true;
        _resizeStartPointerX = pp.Position.X;
        _resizeStartWidth    = (int)Math.Round(Width > 0 ? Width : ActualWidth);
        ResizeGrip.CapturePointer(e.Pointer);
        SetGripCursor(true);
        e.Handled = true;
    }

    private void OnGripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        var pp = e.GetCurrentPoint(this);
        // Pointer X is relative to the panel; while the right edge stays anchored
        // (the owner re-positions on OnResized), dragging the grip left should
        // grow the pane. dx<0 (pointer moves left) → wider.
        double dx = pp.Position.X - _resizeStartPointerX;
        int newWidth = (int)Math.Clamp(_resizeStartWidth - dx, MinPreviewWidth, MaxPreviewWidth);
        Width = newWidth;
        ApplyAspectHeight();
        e.Handled = true;
    }

    private void OnGripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        try { ResizeGrip.ReleasePointerCapture(e.Pointer); } catch { }
        SetGripCursor(false);
        try { OnResized?.Invoke((int)Math.Round(Width)); } catch { }
        e.Handled = true;
    }

    // ── placeholder / webview toggle ──────────────────────────────────────

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

    /// <summary>Release the WebView2 + detach the message handler. Call from the
    /// owner's teardown.</summary>
    public void Shutdown()
    {
        try
        {
            if (PreviewWeb.CoreWebView2 is not null)
            {
                PreviewWeb.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                PreviewWeb.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            }
        }
        catch { }
        try { PreviewWeb.Close(); } catch { }
    }
}

/// <summary>
/// Payload of <see cref="WidgetSinglePreviewPanel.OnManipulatorAttrChanged"/>.
/// Carries only the attributes the manipulator actually changed during the
/// most-recent drag — the editor merges them into the node's existing
/// Attributes dict so unchanged keys aren't lost.
/// </summary>
public sealed record ManipulatorAttrChange(
    string WidgetId,
    string TriggerName,
    string NodeId,
    IReadOnlyDictionary<string, string> Attrs);
