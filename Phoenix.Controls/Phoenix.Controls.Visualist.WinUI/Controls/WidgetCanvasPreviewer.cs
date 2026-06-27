using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Core;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// WidgetCanvasPreviewer — WinUI re-architecture of the pre-T15 WinForms capture
/// engine (<c>Phoenix.Controls.Visualist/Controls/WidgetCanvasPreviewer.cs</c>).
/// A single hidden WebView2 loads <c>http://127.0.0.1:18080/layer/&lt;id&gt;?capture=1</c>
/// from a running Hub HUDServer and periodically captures the rendered overlay
/// as a PNG. <see cref="Canvas.LayerCanvasView"/> crops each widget's
/// <c>Rect</c> region from the captured frame so the design-time canvas matches
/// what OBS sees, with no per-widget WebView2 instances and no C#-side
/// compositor implementation.
///
/// <para><b>The two WinForms→WinUI re-architecture problems</b></para>
///
/// <para><b>1. Off-screen host.</b> WinForms parked a borderless off-screen
/// <c>Form</c> hosting <c>Microsoft.Web.WebView2.WinForms.WebView2</c>. WinUI 3's
/// <c>Microsoft.UI.Xaml.Controls.WebView2</c> must be in a live visual tree to
/// initialise + render. We therefore host it inside a dedicated borderless WinUI
/// <see cref="Window"/>, size that window to the layer resolution, move it to
/// (-32000,-32000) via <c>AppWindow.Move</c>, and <c>Activate()</c> it exactly
/// once so the WebView2 gets a compositor surface — it is never shown to the
/// user. <c>EnsureCoreWebView2Async</c> then yields a <c>CoreWebView2</c> whose
/// <c>CapturePreviewAsync(Png, stream)</c> is the SAME shared API as the
/// WinForms baseline (ported verbatim) plus the FNV-1a idle-skip and the 4Hz
/// <see cref="DispatcherTimer"/> cadence.</para>
///
/// <para><b>2. Per-widget blit (no GDI+).</b> The baseline cropped each widget's
/// rect from a <c>System.Drawing.Bitmap</c> via <c>Graphics.DrawImage</c>. WinUI
/// has no GDI+: we keep the latest captured PNG bytes, and <see cref="CropWidgetAsync"/>
/// decodes them with <see cref="BitmapDecoder"/> and crops the widget's rect via
/// a <see cref="BitmapTransform"/> <see cref="BitmapBounds"/> into a
/// <see cref="SoftwareBitmap"/>. The canvas wraps that in a SoftwareBitmapSource
/// and overlays it on the widget's <c>WidgetView</c>.</para>
///
/// <para>Disabled (null frames, no throw) when
/// <see cref="VisualistUserConfig.CanvasWidgetPreviewEnabled"/> is off, when the
/// layer is unsaved (no layerId for Hub to serve), or when WebView2 init fails.
/// Every failure path logs via <see cref="GlobalLogger"/> and degrades to the
/// existing labeled-rectangle rendering — per
/// feedback_no_modal_dialogs_for_repeatable_rejections, no modals.</para>
///
/// <para>Per feedback_visualist_architect_chrome_independence this capture
/// engine is Visualist-local; Architect has no analogue.</para>
///
/// <para>Lifecycle: one previewer per <see cref="Canvas.LayerCanvasView"/>.
/// <see cref="Dispose"/> closes the hidden window + WebView2 and stops the timer.
/// All public methods must be called on the UI thread that owns the host window
/// (the <see cref="DispatcherTimer"/> and WebView2 are dispatcher-affined).</para>
/// </summary>
public sealed class WidgetCanvasPreviewer : IDisposable
{
    // 250ms cadence (4 Hz) — enough to make moving keyframe animations feel live
    // on the canvas without saturating the WebView2 render thread or burning CPU
    // on a static layer. A LAYER_RELOADED message forces an off-cadence capture.
    private const int CaptureIntervalMs = 250;

    // Park the host window far off any reasonable monitor configuration so it
    // never flashes on-screen, matching the WinForms baseline's (-32000,-32000).
    private const int OffscreenX = -32000;
    private const int OffscreenY = -32000;

    private Window? _hostWindow;
    private Microsoft.UI.Xaml.Controls.WebView2? _webView;
    private AppWindow? _appWindow;
    private readonly DispatcherTimer _timer;

    private string? _layerId;
    private int  _layerWidth  = 1920;
    private int  _layerHeight = 1080;
    private bool _coreInitialized;
    private bool _coreInitializing;
    private bool _captureInFlight;
    private bool _disposed;

    // Tracks whether the last WebView2 navigation produced a healthy page. False
    // after a network failure (Hub momentarily unreachable) or an HTTP error (a
    // transient 404 served while Hub was still registering a freshly-saved layer).
    // Such a page is Hub's bodyless error response — no compositor.js, so it can
    // never self-reload on the /hud LAYER_RELOADED the way a healthy capture page
    // does. RecoverIfFailed re-navigates off it when the layer next reloads.
    private bool _lastNavOk;

    // K (audit live-preview lane) — single in-flight init Task so concurrent
    // AttachAsync / ResumeAsync calls (rapid layer switches) COALESCE onto one
    // WebView2 bring-up instead of the second caller bailing out while the first
    // is still initialising. Held only for the duration of EnsureCoreAsync.
    private Task? _coreInitTask;

    // K (audit live-preview lane) — monotonic attach token. Each AttachAsync
    // bumps this and captures its value; after awaiting init it only drives the
    // navigate/timer if it's still the latest attach. A stale attach that was
    // superseded mid-init (user clicked through several layers) no-ops instead
    // of navigating the WebView2 to an out-of-date layer.
    private int _attachGeneration;

    // Latest captured PNG bytes. Held so CropWidgetAsync can decode + crop any
    // widget rect on demand without re-capturing. Replaced wholesale on each
    // fresh frame; the object reference swap is atomic so the lock only guards
    // the read/write pair, not the decode.
    private byte[]? _currentPng;
    private readonly object _frameLock = new();

    /// <summary>
    /// Fired (on the UI thread) after a fresh frame is published. The layer
    /// canvas subscribes and re-crops every widget overlay from the new PNG.
    /// </summary>
    public event Action? OnFrameUpdated;

    public WidgetCanvasPreviewer()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CaptureIntervalMs) };
        _timer.Tick += async (_, _) => await CaptureAsync();
    }

    /// <summary>Latest captured PNG bytes (Hub overlay frame). Null before the
    /// first successful capture, when disabled, or after a failed init. Callers
    /// must not mutate the returned array — the previewer owns it.</summary>
    public byte[]? CurrentPng
    {
        get { lock (_frameLock) return _currentPng; }
    }

    /// <summary>True once the WebView2 core initialised and a layer is attached
    /// — the canvas uses this to decide whether to attempt live crops at all.</summary>
    public bool IsActive => !_disposed && _coreInitialized && _layerId is not null && _timer.IsEnabled;

    /// <summary>
    /// Repoint the previewer at a layer. Pass null/empty <paramref name="layerId"/>
    /// to detach (the canvas falls back to placeholder rendering). Must be called
    /// from the UI thread. Never throws — init / navigation failures log + degrade.
    /// </summary>
    public async Task AttachAsync(string? layerId, int layerWidth, int layerHeight)
    {
        if (_disposed) return;

        // K (audit live-preview lane) — claim this attach. The token lets a
        // superseded attach (a newer AttachAsync arrived while we awaited init)
        // bail before navigating the WebView2 to a now-stale layer.
        int myGeneration = ++_attachGeneration;

        _layerId     = string.IsNullOrWhiteSpace(layerId) ? null : layerId;
        _layerWidth  = Math.Max(1, layerWidth);
        _layerHeight = Math.Max(1, layerHeight);

        // Size the off-screen window to the layer's pixel size so the WebView2
        // renders the canvas at native resolution — capturing a 1920×1080 layer
        // through an 800×600 WebView2 would scale it down then re-scale up,
        // killing the alignment-grade crispness the per-widget crop needs.
        ResizeHostWindow();

        if (!VisualistUserConfig.Instance.CanvasWidgetPreviewEnabled || _layerId is null)
        {
            StopTimer();
            return;
        }

        // Coalesced init: concurrent attaches all await the SAME bring-up Task
        // rather than the second caller short-circuiting on _coreInitializing.
        await EnsureCoreAsync();
        if (_disposed || !_coreInitialized || _webView?.CoreWebView2 is null) return;

        // A newer attach superseded us while init ran — let that one drive the
        // navigate/timer so we don't point the WebView2 at an out-of-date layer.
        if (myGeneration != _attachGeneration) return;

        // Re-read _layerId: it may have been re-pointed by a later attach that
        // shares our generation race window. Navigate to the current target.
        string? target = _layerId;
        if (target is null) { StopTimer(); return; }

        try
        {
            // Assume not-ok until OnNavigationCompleted confirms a healthy page, so a
            // capture-timer tick firing during the load window can't grab the in-flight
            // or error page — CaptureAsync skips while !_lastNavOk. Without this reset,
            // a re-navigate to a 404 left the PREVIOUS load's _lastNavOk==true, so the
            // very first tick captured the browser error page and pinned it.
            _lastNavOk = false;
            _webView.CoreWebView2.Navigate(BuildUrl(target).ToString());
            StartTimer();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", $"navigate failed for layer '{target}'", ex);
            StopTimer();
        }
    }

    /// <summary>Re-navigate to the same layer — called when the preview backdrop
    /// or layer file changes so the captured frames pick up the new state.</summary>
    public void Refresh()
    {
        if (_disposed || _layerId is null || !_coreInitialized) return;
        // Gate captures until the reload's NavigationCompleted reports healthy.
        try { _lastNavOk = false; _webView?.CoreWebView2?.Reload(); }
        catch (Exception ex) { GlobalLogger.Error("WidgetCanvasPreviewer", "refresh failed", ex); }
    }

    /// <summary>Trigger an immediate capture (e.g. after LAYER_RELOADED) so the
    /// canvas reflects a fresh save within one render frame instead of waiting
    /// up to the 250ms timer interval.</summary>
    public Task CaptureNowAsync() => CaptureAsync();

    /// <summary>
    /// Re-navigate the hidden capture WebView2 when its last navigation did NOT
    /// produce a healthy page — a network failure or an HTTP error (a transient
    /// 404 served while Hub was still registering a freshly-saved layer). Hub's
    /// error response has no body and no <c>compositor.js</c>, so it can never
    /// self-reload on the next <c>/hud</c> LAYER_RELOADED the way a healthy capture
    /// page does, and would otherwise stay stuck on Hub's error page until the user
    /// switched layers. A healthy page self-reloads via its own WS handler, so we
    /// leave it alone (no double reload). Called from
    /// <c>LayerCanvasView.OnBusLayerReloaded</c> when a layer reloads.
    /// </summary>
    public void RecoverIfFailed()
    {
        if (_disposed || !_coreInitialized || _layerId is null) return;
        if (_lastNavOk) return;
        try { _webView?.CoreWebView2?.Navigate(BuildUrl(_layerId).ToString()); }
        catch (Exception ex) { GlobalLogger.Error("WidgetCanvasPreviewer", "RecoverIfFailed", ex); }
    }

    /// <summary>
    /// Push a live rect change for a widget into the hidden WebView2 so the
    /// captured frame reflects detail-panel / drag-resize edits without waiting
    /// for the auto-save → LayerWatcher → reload round-trip. compositor.js applies
    /// the new rect to its in-memory widget and re-renders. No-op when WebView2
    /// hasn't initialised yet — the next capture still reflects the change.
    /// </summary>
    public void PostWidgetUpdate(LayerWidget widget)
    {
        if (_disposed || widget is null || !_coreInitialized) return;
        try
        {
            string json = System.Text.Json.JsonSerializer.Serialize(new
            {
                type     = "WIDGET_UPDATE",
                widgetId = widget.Id,
                name     = widget.Name,
                zIndex   = widget.ZIndex,
                rect     = new { x = widget.Rect.X, y = widget.Rect.Y, width = widget.Rect.Width, height = widget.Rect.Height },
            });
            _webView?.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", "PostWidgetUpdate failed", ex);
        }
    }

    /// <summary>Stop the capture loop and discard the current frame. Used when
    /// the user flips the canvas-preview toggle off.</summary>
    public void Pause()
    {
        if (_disposed) return;
        StopTimer();
        lock (_frameLock) _currentPng = null;
        _lastFrameHash = 0;
        try { OnFrameUpdated?.Invoke(); } catch { /* subscriber faults shouldn't crash the toggle */ }
    }

    /// <summary>Resume capture. Re-navigates the WebView2 (so the page re-runs
    /// onStartup) before restarting the 4 Hz timer.</summary>
    public async Task ResumeAsync()
    {
        if (_disposed || _layerId is null) return;
        await EnsureCoreAsync();
        if (!_coreInitialized || _webView?.CoreWebView2 is null) return;
        try { _lastNavOk = false; _webView.CoreWebView2.Navigate(BuildUrl(_layerId).ToString()); }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", "resume failed", ex);
            return;
        }
        StartTimer();
    }

    /// <summary>
    /// Crop one widget's <c>Rect</c> region out of the latest captured frame.
    /// Returns a <see cref="SoftwareBitmap"/> (Bgra8 / Premultiplied — the format
    /// a <c>SoftwareBitmapSource</c> accepts) the canvas overlays on the widget,
    /// or null when there is no frame yet, the rect is degenerate, or decode
    /// fails. The caller owns the returned bitmap and disposes it.
    ///
    /// This is the WinUI replacement for the baseline's GDI+ <c>Graphics.DrawImage</c>
    /// crop: <see cref="BitmapDecoder"/> + a <see cref="BitmapTransform"/> with
    /// <see cref="BitmapBounds"/> set to the widget rect, clamped to the decoded
    /// frame's pixel bounds so an out-of-range rect can't throw.
    /// </summary>
    public async Task<SoftwareBitmap?> CropWidgetAsync(WidgetRect rect)
    {
        if (_disposed) return null;
        if (rect is null || rect.Width <= 0 || rect.Height <= 0) return null;

        byte[]? png;
        lock (_frameLock) png = _currentPng;
        if (png is null || png.Length == 0) return null;

        try
        {
            using var ms = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(ms))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            ms.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ms);
            uint frameW = decoder.PixelWidth;
            uint frameH = decoder.PixelHeight;
            if (frameW == 0 || frameH == 0) return null;

            // Clamp the requested rect into the decoded frame's pixel bounds.
            // The Hub overlay renders at the layer resolution, so a widget rect
            // expressed in layer coordinates maps 1:1 onto frame pixels — but a
            // mid-resize rect (or a frame captured a tick behind a resolution
            // change) could fall partly outside, which BitmapBounds rejects.
            uint x = (uint)Math.Clamp(rect.X, 0, (int)frameW - 1);
            uint y = (uint)Math.Clamp(rect.Y, 0, (int)frameH - 1);
            uint w = (uint)Math.Clamp(rect.Width,  1, (int)(frameW - x));
            uint h = (uint)Math.Clamp(rect.Height, 1, (int)(frameH - y));

            var transform = new BitmapTransform
            {
                Bounds = new BitmapBounds { X = x, Y = y, Width = w, Height = h },
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = pixelData.DetachPixelData();
            var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, (int)w, (int)h, BitmapAlphaMode.Premultiplied);
            bmp.CopyFromBuffer(pixels.AsBuffer());
            return bmp;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", "CropWidgetAsync", ex);
            return null;
        }
    }

    // ── Private ──────────────────────────────────────────────────────────

    private void OnNavigationCompleted(
        Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        // IsSuccess is false only for network-level failures (Hub unreachable). An
        // HTTP 404/5xx still "completes" with IsSuccess=true, so also gate on the
        // status code: an error page can't host compositor.js and so can't recover
        // itself. HttpStatusCode may throw NotImplementedException on an older
        // WebView2 runtime — treat that as "status unknown / assume ok" and fall
        // back to IsSuccess alone.
        bool httpOk;
        try
        {
            int code = e.HttpStatusCode;
            httpOk = code == 0 || (code >= 200 && code < 400);
        }
        catch { httpOk = true; }
        _lastNavOk = e.IsSuccess && httpOk;

        if (!_lastNavOk)
        {
            // Hub served its bodyless 404/5xx — the layer file isn't in a scanned
            // directory yet, or we raced its atomic-save swap. That page has no
            // compositor.js, so a captured frame of it is the browser's "this page
            // can't be displayed". Cropping each widget's rect out of it paints
            // slices of that error behind every widget — the reported "404 showing
            // as the background, with the correct images above it" (the widgets'
            // stored thumbnails sit on top of the stale error crop). Evict any
            // stale/transitional frame and notify so RefreshWidgetOverlaysAsync
            // clears the live overlays; the widgets fall back to their stored
            // thumbnails / labelled outline until a healthy capture lands.
            lock (_frameLock) _currentPng = null;
            _lastFrameHash = 0;
            try { OnFrameUpdated?.Invoke(); }
            catch { /* a subscriber fault must not break nav handling */ }
        }
    }

    private void StartTimer()
    {
        if (_disposed) return;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer.IsEnabled) _timer.Stop();
    }

    private Uri BuildUrl(string layerId)
        // No ?bg= here — the canvas paints its own backdrop and blits widget
        // pixels onto it. Letting compositor.js paint a backdrop too would
        // obscure the canvas backdrop the user actually wants to see (same
        // reasoning as the WinForms baseline's capture URL).
        => new($"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(layerId)}?capture=1");

    private void ResizeHostWindow()
    {
        if (_appWindow is null) return;
        try
        {
            _appWindow.Resize(new SizeInt32(_layerWidth, _layerHeight));
            // Re-assert the off-screen position after a resize — some
            // AppWindow.Resize paths re-clamp position into the work area.
            _appWindow.Move(new PointInt32(OffscreenX, OffscreenY));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", "ResizeHostWindow", ex);
        }
    }

    // K (audit live-preview lane) — coalescing wrapper. The first caller starts
    // the real bring-up and parks the Task in _coreInitTask; any caller that
    // arrives while init is in flight awaits that SAME Task instead of returning
    // early (the old _coreInitializing guard let the racing AttachAsync fall
    // through to its `!_coreInitialized` check and silently drop the layer
    // switch). All calls are on the UI thread, so the _coreInitTask read/assign
    // pair needs no lock — there is no concurrent thread, only interleaved
    // continuations, and the field is only mutated at synchronous points
    // (before the first await, and in the finally after completion).
    private Task EnsureCoreAsync()
    {
        if (_coreInitialized) return Task.CompletedTask;
        if (_coreInitTask is not null) return _coreInitTask;

        var task = EnsureCoreCoreAsync();
        // If init completed fully synchronously (no real awaits hit), the field
        // was already cleared in the finally; only park a still-running Task.
        _coreInitTask = task.IsCompleted ? null : task;
        return task;
    }

    private async Task EnsureCoreCoreAsync()
    {
        if (_coreInitialized || _coreInitializing) return;
        _coreInitializing = true;
        try
        {
            // Build the dedicated hidden host window + WebView2 the first time.
            // The WebView2 needs a live, activated visual tree to obtain a
            // compositor surface — a collapsed / never-activated window yields a
            // CoreWebView2 that captures blank frames.
            if (_hostWindow is null)
            {
                _hostWindow = new Window { Title = "Phoenix Visualist — hidden preview host" };
                _webView    = new Microsoft.UI.Xaml.Controls.WebView2();
                // Transparent capture (bug #1 — layer-canvas widget preview showed a
                // WHITE background instead of the overlay's transparency). WebView2's
                // DefaultBackgroundColor defaults to opaque WHITE, so CapturePreviewAsync
                // flattens every transparent pixel of the compositor overlay onto white;
                // the cropped per-widget PNG then paints a white box behind each widget
                // on the layer canvas. Setting it to a fully-transparent colour makes the
                // PNG capture carry the alpha channel through, so the widget's transparent
                // regions show the stage backdrop (the BG swatch) like OBS does. dpr/OBS
                // path is untouched — this WebView2 only feeds the design-time canvas.
                _webView.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
                _hostWindow.Content = _webView;

                var hwnd     = WinRT.Interop.WindowNative.GetWindowHandle(_hostWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow   = AppWindow.GetFromWindowId(windowId);

                if (_appWindow is not null)
                {
                    // Borderless: no title bar / border so nothing of the host
                    // chrome could ever flash on a multi-monitor edge case.
                    if (_appWindow.Presenter is OverlappedPresenter op)
                    {
                        op.SetBorderAndTitleBar(false, false);
                        op.IsAlwaysOnTop = false;
                        op.IsResizable   = false;
                    }
                    _appWindow.IsShownInSwitchers = false;
                    ResizeHostWindow();
                }

                // Activate ONCE so the compositor surface is created. The window
                // stays off-screen + out of switchers, so the user never sees it.
                _hostWindow.Activate();
            }

            if (_webView is null) { _coreInitialized = false; return; }

            await _webView.EnsureCoreWebView2Async();

            if (_webView.CoreWebView2 is not null)
            {
                try { _webView.CoreWebView2.Settings.AreDevToolsEnabled = false; } catch { /* older builds */ }
                _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                _coreInitialized = true;
            }
            else
            {
                _coreInitialized = false;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("WidgetCanvasPreviewer", "WebView2 init failed — canvas preview disabled", ex);
            _coreInitialized = false;
        }
        finally
        {
            _coreInitializing = false;
            // Clear the coalescing handle so a future EnsureCoreAsync can retry
            // after a FAILED init (e.g. WebView2 runtime installed later). A
            // successful init is short-circuited earlier by the _coreInitialized
            // check, so this is only load-bearing on the failure path.
            _coreInitTask = null;
        }
    }

    private async Task CaptureAsync()
    {
        if (_disposed || !_coreInitialized || _captureInFlight) return;
        if (_layerId is null || _webView?.CoreWebView2 is null) return;
        // Don't blit an error page onto the canvas. When the last navigation hit
        // Hub's 404/5xx (e.g. racing the layer's atomic-save swap, or a not-yet-
        // registered fresh save), the WebView2 shows the browser's "this page can't
        // be found" page — capturing it would paint that error across every widget
        // thumbnail (the "error in the back"). Skip until RecoverIfFailed (on
        // LAYER_RELOADED) re-navigates to a healthy page; the last good frame — or
        // the labelled-outline fallback when there's no frame yet — stays meanwhile.
        if (!_lastNavOk) return;
        if (!VisualistUserConfig.Instance.CanvasWidgetPreviewEnabled)
        {
            StopTimer();
            return;
        }

        _captureInFlight = true;
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            // SAME shared CoreWebView2 API as the WinForms baseline — ported
            // verbatim apart from the WinForms MemoryStream → WinRT
            // InMemoryRandomAccessStream swap the WinUI WebView2 overload requires.
            await _webView.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, ms);

            uint size = (uint)ms.Size;
            if (size == 0) return;

            ms.Seek(0);
            byte[] png = new byte[size];
            using (var reader = new DataReader(ms.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(png);
            }

            // FNV-1a idle-skip (#16 from the baseline). If the captured PNG is
            // byte-identical to the previous tick (static layer, idle widget),
            // skip the publish + decode churn entirely. The 250ms timer keeps
            // ticking for change-detection but the per-frame allocation drops to
            // near zero on an idle layer.
            ulong hash = Fnv1a64(png);
            if (hash == _lastFrameHash) return;
            _lastFrameHash = hash;

            lock (_frameLock) _currentPng = png;

            try { OnFrameUpdated?.Invoke(); }
            catch (Exception ex)
            {
                GlobalLogger.Error("WidgetCanvasPreviewer", "OnFrameUpdated subscriber threw", ex);
            }
        }
        catch (Exception ex)
        {
            // Capture fails transiently (page navigating, Hub down, etc). Don't
            // spam — log at most once per minute through a simple time gate.
            LogCaptureFault(ex);
        }
        finally
        {
            _captureInFlight = false;
        }
    }

    // #16 — last captured PNG bytes' FNV-1a hash, compared each tick before the
    // publish/decode step so unchanged frames don't churn.
    private ulong _lastFrameHash;
    private static ulong Fnv1a64(ReadOnlySpan<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime  = 1099511628211UL;
        ulong h = offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            h ^= bytes[i];
            h *= prime;
        }
        return h;
    }

    private DateTime _lastCaptureFaultLog = DateTime.MinValue;
    private void LogCaptureFault(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCaptureFaultLog).TotalSeconds < 60) return;
        _lastCaptureFaultLog = now;
        GlobalLogger.Error("WidgetCanvasPreviewer", "capture failed (Hub overlay unreachable?)", ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer.Stop(); } catch { }
        lock (_frameLock) _currentPng = null;
        try { _webView?.Close(); } catch { }
        try { _hostWindow?.Close(); } catch { }
        _webView    = null;
        _hostWindow = null;
        _appWindow  = null;
    }
}
