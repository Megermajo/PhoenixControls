using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;

namespace Phoenix.Controls.Visualist.WinUI.Canvas;

public sealed partial class LayerCanvasView : UserControl
{
    private VisualistViewModel? _vm;

    // Single-widget drag state — populated on PointerPressed, cleared on
    // PointerReleased / capture-lost / cancel. _dragMoved suppresses MarkDirty on a
    // click that never actually moved the widget. _dragPointerId is the id of the
    // pointer that armed the drag: PointerMoved/Released/Canceled all guard on it so
    // a stale event from a previous, incompletely-torn-down gesture can never drive
    // a move (the "widget jumps to where I click" / "sticks to the cursor" bugs).
    private LayerWidget? _dragWidget;
    private WidgetView?  _dragView;
    private Point        _dragStartLocal;
    private int          _dragOriginX, _dragOriginY;
    private bool         _dragMoved;
    private uint         _dragPointerId;

    // Multi-select state. _multiSelected is the model-side set;
    // _viewByWidget maps each widget to its current WidgetView so Render
    // rebuilds and group-drag can touch the visuals without an O(n) lookup.
    // Group-drag snapshots every selected widget's origin at press-time so
    // PointerMoved can re-apply the delta to all of them.
    private readonly HashSet<LayerWidget> _multiSelected = new();
    private readonly Dictionary<LayerWidget, WidgetView> _viewByWidget = new();
    private readonly Dictionary<LayerWidget, (int X, int Y)> _groupDragOrigins = new();
    private bool _isGroupDragging;

    // Lasso (rubber-band) state. _lassoRect is added to WidgetSurface when
    // a press lands on empty canvas and removed on release. Coordinates are
    // canvas-local so the rect lines up with widget rects directly.
    private bool _isLassoing;
    private bool _lassoExtend;          // true when the press was shift-held
    private Point _lassoStart;
    private Rectangle? _lassoRect;

    // ─── Resize handle (restored from pre-T15 LayerCanvas) ───
    //
    // The pre-T15 WinForms LayerCanvas painted a 12×12 resize handle on the
    // bottom-right of the primary-selected widget and ran a _resizing state
    // machine (handle hit-test → Width/Height mutation, snap, PushUndo on first
    // motion, MarkDirty on release). The WinUI canvas dropped it entirely. We restore it as a single
    // shared Rectangle child of WidgetSurface re-positioned over the primary
    // widget on every Render; the handle owns its own pointer handlers so
    // grabbing it never triggers the widget-body move drag.
    //
    // HandleSize matches the baseline (12px in canvas space — the outer Viewbox
    // scales it uniformly with the rest of the stage). MinWidgetSize is the
    // resize floor; the brief asks for >= 8px (the baseline used 20px, but the
    // inline w/h pills already allow down to 1px, so 8px is a sane middle that
    // keeps a grabbable footprint without fighting the pill minimum).
    private const int HandleSize = 12;
    private const int MinWidgetSize = 8;

    private Rectangle? _resizeHandle;
    private LayerWidget? _resizeWidget;
    private WidgetView?  _resizeView;
    private Point        _resizeStartLocal;
    private int          _resizeOriginW, _resizeOriginH;
    private bool         _resizing;
    private bool         _resizeMoved;

    // Physical [ / ] keytops (the two keys right of P on a US layout) drive
    // single-step z-order regardless of the VirtualKey the layout reports, the
    // same scancode-anchor pattern as Undo/Redo above.
    private const uint ScanCode_BracketLeft  = 0x1A;  // [  → send backward
    private const uint ScanCode_BracketRight = 0x1B;  // ]  → bring forward

    // ─── Snap-to-grid + alignment guides ───────────────────────────
    //
    // Visualist-local copy of the snap-guide semantics from Architect's
    // LogicCanvasView.Pointer.cs. The PAINT helpers are NOT lifted —
    // Visualist owns
    // its own implementation, identical behaviour but distinct code.
    //
    // Grid step is 8px (vs Architect's wildcard zoom-based grid) so widget
    // placement on a layer canvas reads cleanly against typical OBS-source
    // resolutions like 1920×1080 (240 columns × 135 rows).
    //
    // The edge/centre ALIGNMENT snap (and its guides) is
    // delegated to the shared, pure-data WidgetSnapCalculator in
    // Phoenix.Controls.Visualist.Engine, which owns the tolerance
    // (SnapThreshold = 6px) and the snap targets (canvas 0 / mid / max edges
    // PLUS every sibling's edges/centre). The prior inline 4px cascade +
    // separately-computed guides were removed so snap position and guide
    // geometry can no longer disagree, and so the canvas edges/centre snap too.
    //
    // Guides live in WidgetSurface as Lines + translucent Rects added to the
    // canvas at canvas-space coordinates (no host-space projection needed —
    // WidgetSurface IS the canvas-space surface, scaled uniformly by the
    // outer Viewbox).

    private const int SnapGridStepPx = 8;

    // Alignment guides span the union of the dragged-widget extent and the
    // matching sibling extent, padded by this margin on each end, instead of the
    // whole canvas. A localized line reads as "this widget is about to snap to
    // that one" rather than a full-stage line that implies a snap is active
    // everywhere along the column/row. Clamped to the canvas bounds in the guide
    // helpers so the padding never paints off-surface.
    private const int AlignGuideSpanMarginPx = 24;

    private readonly List<Shape> _snapGuideShapes = new();

    // Engine-snap wiring — the authoritative alignment-snap engine is the pure
    // data WidgetSnapCalculator in Phoenix.Controls.Visualist.Engine. It snaps
    // the dragged rect against the layer-canvas edges/centre AND every sibling's
    // edges/centre, and returns the guide segments that explain the snap. We
    // capture those guides each time ResolveAlignmentSnap runs so the very same
    // PointerMoved pass can render them — guide geometry and snap position are
    // then guaranteed consistent (the pre-wiring code computed snap and guides
    // separately and could disagree, and neither knew about the canvas edges).
    // SnapThreshold (6px) lives in the calculator; the inline AlignGuideThreshold
    // (4px) above is now used only as the render gate for the secondary
    // collision overlay, not for the alignment math.
    private IReadOnlyList<WidgetSnapCalculator.Guide> _engineSnapGuides =
        System.Array.Empty<WidgetSnapCalculator.Guide>();

    // Perf — the alignment-guide overlay is rebuilt on every PointerMoved
    // during a drag/resize. The overlay is a pure function of the anchor's
    // rect (siblings don't move mid-gesture), so when the anchor's geometry
    // hasn't changed since the last rebuild the freshly-built overlay would be
    // byte-for-byte identical. We cache the last anchor rect the overlay was
    // built against and short-circuit the clear+rebuild when it's unchanged,
    // turning a per-frame O(N) child-mutation storm into a no-op between actual
    // pixel moves. Reset to the sentinel in ClearAlignmentGuides so every new
    // gesture (and any return to a previously-visited position) rebuilds.
    private (int X, int Y, int W, int H)? _lastGuideAnchorRect;

    // Perf — the snap passes fed WidgetSnapCalculator a freshly-built
    // List<Rectangle> of sibling rects on every PointerMoved. Siblings are
    // static for the whole drag/resize gesture (only the anchor moves — the
    // same invariant _lastGuideAnchorRect relies on), so the list is
    // snapshotted once at gesture start (armed in the drag / resize-handle
    // PointerPressed, cleared in EndWidgetDrag / FinishResize) and reused per
    // move. The per-call rebuild remains as a fallback for any call outside
    // an armed gesture.
    private readonly List<System.Drawing.Rectangle> _gestureSiblingRects = new();
    private bool _gestureSiblingRectsValid;

    // Reusable moving-member set for the guide-overlay rebuild — cleared and
    // refilled per rebuild rather than allocated per rebuild.
    private readonly HashSet<LayerWidget> _guideDragSet = new();

    // ─── Live preview (canvas widget live preview) ──
    //
    // The pre-T15 WinForms LayerCanvas blit-rendered each widget from a hidden
    // WebView2 (WidgetCanvasPreviewer). This restores that pipeline on WinUI:
    // a single off-screen WebView2 (hosted in a hidden Window — see
    // WidgetCanvasPreviewer) loads the Hub overlay for the saved layer, captures
    // it at 4Hz, and we crop each widget's rect out of the captured frame and
    // overlay it on that widget's WidgetView. Gated on
    // VisualistUserConfig.CanvasWidgetPreviewEnabled — when off, every overlay is
    // cleared and the existing labeled-rectangle rendering shows through (the
    // fallback is never removed).
    //
    // The previewer is created lazily on first attach so opening a layer with
    // the toggle off (or with no Hub running) costs nothing. The whole pipeline is
    // Visualist-local; Architect has no analogue.
    private Phoenix.Controls.Visualist.WinUI.Controls.WidgetCanvasPreviewer? _previewer;
    private Action? _onConfigChanged;
    private Action<string>? _onLayerReloaded;
    // Guards re-entrant overlay refreshes — OnFrameUpdated can fire while a prior
    // async crop batch is still in flight (4Hz timer + a LAYER_RELOADED forced
    // capture can overlap). We coalesce to "one batch at a time, re-run if a
    // frame arrived during the batch" so crops never stack up unbounded.
    private bool _previewRefreshInFlight;
    private bool _previewRefreshQueued;

    // Overlay-refresh batch guard. Bumped on every Render() (which rebuilds
    // _viewByWidget). RefreshWidgetOverlaysAsync snapshots this at the start of a
    // crop batch and, after each CropWidgetAsync await, checks the live value:
    // if a Render() landed mid-batch the snapshot is stale, so we abandon the
    // rest of the batch rather than push crops onto a half-rebuilt view set
    // (which would flicker). The coalesce loop / 4Hz timer re-runs the refresh
    // against the fresh views, so no frame is lost — only the doomed tail of an
    // interrupted batch is dropped.
    private int _renderVersion;

    // Attach-call coalescing. SyncPreviewer fires AttachPreviewerSafeAsync
    // fire-and-forget on every Render(); rapidly switching layers (each
    // selection re-renders) would otherwise launch a stack of overlapping
    // AttachAsync calls, each awaiting the slow WebView2 EnsureCoreAsync and
    // racing to navigate. We funnel them through a single in-flight gate that
    // always settles on the LATEST requested (layerId,w,h): a request that
    // arrives while an attach is running just updates the pending tuple, and the
    // running loop re-attaches to the newest target before exiting. This is the
    // _captureInFlight pattern from WidgetCanvasPreviewer applied to attach, and
    // it stays entirely in this owned file (no edit to WidgetCanvasPreviewer).
    private bool _attachInFlight;
    private (string LayerId, int W, int H)? _pendingAttach;

    /// <summary>
    /// Persistent snap toggle. Defaults to <c>true</c>.
    /// In-memory only; no settings round-trip. Toggled at runtime via the public surface.
    /// </summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>
    /// Fires when the user drops a .phxlayer file from Explorer onto the
    /// canvas. Visualist.MainView subscribes and routes the path through
    /// VisualistViewModel.OpenLayer (TODO: drag-drop into editor windows not wired).
    /// </summary>
    public event System.EventHandler<string>? FileOpenRequested;

    /// <summary>
    /// Enter-widget restore — raised when the user enters a widget for
    /// editing: double-tap its body, press Enter on the current selection, or
    /// pick "Edit Widget" from the per-widget context menu. MainView subscribes
    /// (mirroring the <see cref="FileOpenRequested"/> bubble pattern) and swaps
    /// to the Widget Editor sub-tab bound to this widget. Restores the pre-WinUI
    /// double-click-rectangle-to-enter gesture (WidgetEditorForm) the rework dropped.
    /// </summary>
    public event System.EventHandler<LayerWidget>? WidgetEnterRequested;

    public LayerCanvasView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        // Canvas-level handlers — fire on empty-space presses (per-widget
        // PointerPressed handlers consume the event with args.Handled = true
        // before bubbling, so this only sees clicks that miss every widget).
        WidgetSurface.PointerPressed  += OnSurfacePointerPressed;
        WidgetSurface.PointerMoved    += OnSurfacePointerMoved;
        WidgetSurface.PointerReleased += OnSurfacePointerReleased;
        WidgetSurface.PointerCanceled    += OnSurfacePointerCaptureLost;
        WidgetSurface.PointerCaptureLost += OnSurfacePointerCaptureLost;

        // Empty-canvas right-click → authoring menu (Add Widget + presets).
        // Restores the WinForms LayerCanvas.BuildContextMenu gesture the rework
        // dropped — the only WinUI context menu left was the per-widget one, so
        // a fresh/empty layer had no reachable way to create a widget.
        WidgetSurface.RightTapped += OnSurfaceRightTapped;

        // Explorer file-drop — accept .phxlayer and bubble through
        // FileOpenRequested. AllowDrop sits on
        // the outer Grid (not WidgetSurface) so drops on the resolution
        // badge / outside the stage frame still register.
        AllowDrop = true;
        DragOver += OnHostDragOver;
        Drop     += OnHostDrop;

        // Cut/Copy/Paste/Select-All/Delete keyboard
        // shortcuts. The UserControl itself acts as the focus host; PointerPressed
        // (both on widgets and on empty surface) calls Focus so the next
        // keystroke lands here rather than bubbling to the embedding pillar
        // shell. WidgetGraphCanvas already does the same on its HostGrid.
        IsTabStop = true;
        KeyDown += OnKeyDownForClipboardShortcuts;
        WidgetSurface.PointerPressed += (_, _) => TryFocus();

        // Live-preview wiring. Config flips (toggle / bg) re-evaluate
        // whether the previewer should be running; a Hub LAYER_RELOADED forces
        // an immediate re-capture so a save round-trip refreshes the canvas
        // within one frame. Both handlers are detached on Unloaded.
        _onConfigChanged = OnPreviewConfigChanged;
        Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.OnChanged += _onConfigChanged;

        _onLayerReloaded = OnBusLayerReloaded;
        Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance.OnLayerReloaded += _onLayerReloaded;

        // Build the backdrop swatches + seed the command-bar toggles
        // from the persisted prefs. The same config OnChanged fan-out the
        // previewer uses keeps these in step if a setting is flipped elsewhere.
        BuildBackdropSwatches();
        SyncPrefsUi();

        // Zoom/pan on the stage host. Ctrl+wheel zooms about the cursor;
        // middle-mouse drag pans. Handlers live on StageHost (the letterboxed
        // grid around the Viewbox) so they fire over the empty margin too.
        // PointerWheelChanged needs handledEventsToo=false (default) — Ctrl+wheel
        // isn't consumed elsewhere on this surface.
        StageHost.PointerWheelChanged += OnStagePointerWheel;
        StageHost.PointerPressed      += OnStagePanPointerPressed;
        StageHost.PointerMoved        += OnStagePanPointerMoved;
        StageHost.PointerReleased     += OnStagePanPointerReleased;
        StageHost.PointerCaptureLost  += OnStagePanPointerCaptureLost;
    }

    private void TryFocus()
    {
        try { Focus(FocusState.Programmatic); } catch { /* designer / pre-app */ }
    }

    /// <summary>
    /// True when a pointer press originated on an inline editable pill (geometry /
    /// preset / name) — those carry <c>Tag="editpill"</c> on their container, or a
    /// live edit <see cref="TextBox"/>. Walks up from the press's OriginalSource.
    /// The canvas + widget gesture handlers consult this so a pill press is left
    /// entirely to the pill's own Tapped→TextBox edit, instead of stealing focus or
    /// capturing the pointer (which opens the edit box but blocks all typing).
    /// </summary>
    private static bool IsEditAffordancePress(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is TextBox) return true;
            if (d is FrameworkElement fe && fe.Tag is string tag && tag == "editpill") return true;
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // Theme-key + literal-ARGB fallback for tinted brushes. Per-pillar copy
    // of the same helper in WidgetGraphCanvas.ResolveBrush — Visualist owns
    // its own paint helpers, never lifted to Shared. Returns a SolidColorBrush at the requested alpha
    // using the resolved RGB triple (theme lookup wins, literal ARGB is the
    // fallback for designer / pre-app / missing-key paths).
    private static Brush ResolveBrushTinted(string key, byte a, byte r, byte g, byte b)
    {
        try
        {
            if (Application.Current?.Resources is { } res
                && res.TryGetValue(key, out var found)
                && found is SolidColorBrush sb)
            {
                return new SolidColorBrush(Color.FromArgb(a, sb.Color.R, sb.Color.G, sb.Color.B));
            }
        }
        catch { /* designer / pre-app — fall through to literal */ }
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    // Accepted extensions at the DragOver gate. .phxlayer opens a layer
    // file; the media set spawns Image.Load / Video.Load widgets at the drop
    // point. Anything else gets rejected at DragOver so the gold halo
    // never lights up for an unsupported type. Visualist owns its own filter
    // table per the per-pillar paint isolation rule — Architect's
    // LogicCanvasView.Pointer DragOver lives on its own table; not lifted.
    // The canonical accepted extensions are:
    // .png / .jpg / .jpeg / .webp / .gif for images, .mp4 / .webm / .mov for
    // video. .bmp is kept for back-compat with the prior table.
    private static readonly string[] s_imageExtensions =
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
    private static readonly string[] s_videoExtensions =
        { ".mp4", ".webm", ".mov" };

    private static bool HasExt(string path, string[] table)
    {
        foreach (var ext in table)
        {
            if (path.EndsWith(ext, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsAcceptedDropPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.EndsWith(".phxlayer", System.StringComparison.OrdinalIgnoreCase)) return true;
        if (HasExt(path, s_imageExtensions)) return true;
        if (HasExt(path, s_videoExtensions)) return true;
        return false;
    }

    private static string DescribeAcceptedDrop(IReadOnlyList<Windows.Storage.IStorageItem> items)
    {
        bool anyLayer = false, anyImage = false, anyVideo = false;
        foreach (var item in items)
        {
            if (item is not Windows.Storage.StorageFile f) continue;
            var p = f.Path;
            if (p.EndsWith(".phxlayer", System.StringComparison.OrdinalIgnoreCase)) anyLayer = true;
            else if (HasExt(p, s_imageExtensions)) anyImage = true;
            else if (HasExt(p, s_videoExtensions)) anyVideo = true;
        }
        // Captions: "Open layer" /
        // "Spawn image widget" / "Spawn video widget". A mixed media drop
        // falls through to a generic "Spawn media widget" so the user still
        // sees something coherent; the rejection caption ("Unsupported file
        // type") is set in OnHostDragOver below — this method only renders
        // accepted drops.
        if (anyLayer && !anyImage && !anyVideo) return Localizer.T("visualist.canvas.drop.open_layer", "Open layer");
        if (anyImage && !anyVideo && !anyLayer) return Localizer.T("visualist.canvas.drop.spawn_image", "Spawn image widget");
        if (anyVideo && !anyImage && !anyLayer) return Localizer.T("visualist.canvas.drop.spawn_video", "Spawn video widget");
        if (anyImage || anyVideo) return Localizer.T("visualist.canvas.drop.spawn_media", "Spawn media widget");
        return Localizer.T("visualist.canvas.drop.open_layer", "Open layer");
    }

    private async void OnHostDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            // Non-StorageItems payloads (text drags from other apps, etc.) —
            // reject so the gold halo doesn't light up.
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        // Peek at the dragged items before lighting up the halo. Resolve
        // the deferral so the StorageItems read completes before we set the
        // accepted operation. The per-Architect-pattern reference is
        // LogicCanvasView.Pointer.cs's DragOver but Visualist owns its own
        // filter logic.
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            bool anyAccepted = false;
            foreach (var item in items)
            {
                if (item is Windows.Storage.StorageFile f && IsAcceptedDropPath(f.Path))
                {
                    anyAccepted = true;
                    break;
                }
            }

            if (!anyAccepted)
            {
                // Emit an explicit "Unsupported file type" caption
                // on the override so the gold halo doesn't appear, but the
                // user still gets feedback explaining why their drop is being
                // refused. This stays purely visual chrome — no popup.
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
                if (e.DragUIOverride is not null)
                {
                    e.DragUIOverride.Caption          = Localizer.T("visualist.canvas.drop.unsupported", "Unsupported file type");
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.IsContentVisible = false;
                    e.DragUIOverride.IsGlyphVisible   = false;
                }
                return;
            }

            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = DescribeAcceptedDrop(items);
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsContentVisible = false;
            }
        }
        catch (System.Exception ex)
        {
            // GetStorageItemsAsync can throw on shell extensions / VFS drags —
            // be conservative and reject rather than light up a misleading halo.
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "LayerCanvasView", "OnHostDragOver StorageItems", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnHostDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();

            // Resolve the drop position into WidgetSurface-local coords
            // so spawned widgets land where the cursor was. Failures fall back
            // to (0,0) — same anchor an "Add Widget" toolbar click uses.
            int dropX = 0, dropY = 0;
            try
            {
                Point hostPoint = e.GetPosition(WidgetSurface);
                dropX = (int)System.Math.Round(hostPoint.X);
                dropY = (int)System.Math.Round(hostPoint.Y);
            }
            catch (System.Exception posEx)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerCanvasView", "OnHostDrop position", posEx);
            }

            int mediaSpawnedCount = 0;
            foreach (var item in items)
            {
                if (item is not Windows.Storage.StorageFile file) continue;

                if (file.Path.EndsWith(".phxlayer", System.StringComparison.OrdinalIgnoreCase))
                {
                    FileOpenRequested?.Invoke(this, file.Path);
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                    return;
                }

                bool isImage = HasExt(file.Path, s_imageExtensions);
                bool isVideo = HasExt(file.Path, s_videoExtensions);
                if (!isImage && !isVideo) continue;

                if (SpawnMediaWidget(file.Path, isVideo, dropX + mediaSpawnedCount * 20, dropY + mediaSpawnedCount * 20))
                {
                    mediaSpawnedCount++;
                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                }
            }
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "LayerCanvasView", "OnHostDrop StorageItems", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Spawn an Image.Load (or Video.Load) widget at (dropX, dropY) with
    /// the dragged file path stamped onto the source node's Path attribute.
    /// Returns true on a successful spawn so the caller can offset subsequent
    /// drops in the same batch. Failures are logged via GlobalLogger; no modal.
    /// </summary>
    private bool SpawnMediaWidget(string path, bool isVideo, int dropX, int dropY)
    {
        if (_vm is null) return false;
        if (_vm.SelectedLayer is null)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                "LayerCanvasView: media drop ignored — no layer is open.",
                "LayerCanvasView", LogLevel.System);
            return false;
        }
        try
        {
            // Use VisualistViewModel.AddWidget — same path New Widget button
            // takes, with its undo / dirty bookkeeping intact. AddWidget anchors
            // the widget at (100,100); we override Rect to put it under the
            // cursor and resize to a sensible 480×270 default (16:9 thumb).
            LayerWidget? widget = _vm.AddWidget();
            if (widget is null) return false;

            // Default 480×270 — 16:9 frame that scales sanely for images and
            // matches the most common video aspect. Author can resize.
            const int defaultW = 480;
            const int defaultH = 270;

            int canvasW = (int)WidgetSurface.Width;
            int canvasH = (int)WidgetSurface.Height;
            int x = System.Math.Max(0, System.Math.Min(dropX - defaultW / 2, System.Math.Max(0, canvasW - defaultW)));
            int y = System.Math.Max(0, System.Math.Min(dropY - defaultH / 2, System.Math.Max(0, canvasH - defaultH)));
            widget.Rect = new WidgetRect { X = x, Y = y, Width = defaultW, Height = defaultH };

            // Tag the widget preset so the resolver / inspector knows what
            // kind it is; the visual is mirrored in BuildView via
            // ResolveThumbKind. AddWidget() pre-seeded an onStartup trigger
            // with the Display sink — we need to inject the source node
            // (Image.Load / Video.Load) and wire it to the sink.
            widget.Preset = isVideo ? WidgetPreset.Video : WidgetPreset.Image;
            widget.Name = isVideo
                ? Localizer.T("visualist.canvas.widget.video", "Video")
                : Localizer.T("visualist.canvas.widget.image", "Image");

            string sourceTitle = isVideo ? "Video.Load" : "Image.Load";
            WidgetTrigger? trigger = widget.Triggers.Count > 0 ? widget.Triggers[0] : null;
            if (trigger?.Graph is Graph graph)
            {
                // The dropped file path becomes the Path attribute. JSON-quote
                // it to match the convention WidgetNodeRegistry's defaults use
                // for string attributes ("Path" defaults to "\"\"").
                Node source = Phoenix.Controls.Visualist.Core.WidgetNodeRegistry.Instantiate(
                    sourceTitle,
                    new System.Drawing.Point(120, 120));
                source.Attributes["Path"] = System.Text.Json.JsonSerializer.Serialize(path);
                graph.Nodes.Add(source);

                // Wire the new source's Image output into the Display sink's
                // first input — same shape WidgetPresets.GetStartingGraph
                // produces for Image / Video preset widgets.
                Node? sink = graph.Nodes.Find(n =>
                    Phoenix.Controls.Visualist.Core.DisplaySinkNode.Is(n));
                if (sink is not null)
                {
                    Socket? srcOut = source.Sockets.Find(s =>
                        s.Type == SocketType.Output && s.Name == "Image");
                    Socket? sinkIn = sink.Sockets.Find(s => s.Type == SocketType.Input);
                    if (srcOut is not null && sinkIn is not null)
                    {
                        graph.Links.Add(new Link
                        {
                            FromNodeId   = source.Id,
                            FromSocketId = srcOut.Id,
                            ToNodeId     = sink.Id,
                            ToSocketId   = sinkIn.Id,
                        });
                    }
                }
            }

            _vm.Document?.MarkDirty();
            _vm.RaiseSelectedLayerChanged();
            _vm.SelectedWidget = widget;

            Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                $"LayerCanvasView: spawned {(isVideo ? "Video" : "Image")} widget from '{System.IO.Path.GetFileName(path)}' at ({x},{y}).",
                "LayerCanvasView", LogLevel.System);
            return true;
        }
        catch (System.Exception ex)
        {
            Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                "LayerCanvasView", $"SpawnMediaWidget('{path}')", ex);
            return false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Without this, a recycled view leaves _vm.PropertyChanged still pinning
        // the old view via the closure — slow but real listener accretion.
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            _vm = null;
        }

        // Tear down the live-preview pipeline. Detach the global
        // config + bus subscriptions (both fan out across surfaces; a dangling
        // delegate would resurrect a disposed canvas) and dispose the previewer
        // so its hidden Window + WebView2 + 4Hz timer are released.
        if (_onConfigChanged is not null)
        {
            try { Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.OnChanged -= _onConfigChanged; } catch { }
            _onConfigChanged = null;
        }
        if (_onLayerReloaded is not null)
        {
            try { Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance.OnLayerReloaded -= _onLayerReloaded; } catch { }
            _onLayerReloaded = null;
        }
        if (_previewer is not null)
        {
            try { _previewer.OnFrameUpdated -= OnPreviewFrameUpdated; } catch { }
            try { _previewer.Dispose(); } catch { }
            _previewer = null;
        }
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is { } old) old.PropertyChanged -= OnVmPropertyChanged;
        _vm = args.NewValue as VisualistViewModel;
        if (_vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            Render(vm);
            PublishMultiSelection(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VisualistViewModel vm) return;
        if (e.PropertyName == nameof(VisualistViewModel.SelectedWidget))
        {
            // A single-select that originated OUTSIDE the canvas (inspector
            // roster row, context-menu single fallback) never touched
            // _multiSelected — collapse any stale multi-set onto the new anchor
            // so the canvas highlight and the inspector's mode agree. Canvas ops
            // always add the anchor to _multiSelected before assigning it, so
            // this is a no-op for them.
            if (vm.SelectedWidget is { } sw && !_multiSelected.Contains(sw))
            {
                ClearMultiSelect();
                _multiSelected.Add(sw);
            }
            Render(vm);
            RecomputeHotkeyContext();
            PublishMultiSelection(vm);
        }
        else if (e.PropertyName == nameof(VisualistViewModel.SelectedLayer))
        {
            Render(vm);
            RecomputeHotkeyContext();
            PublishMultiSelection(vm);
        }
    }

    // Capture each mounted
    // WidgetView's rendered bitmap and stamp it into the corresponding
    // LayerWidget.Thumbnail field. Called from MainView's save flow before
    // LayerSerializer.Write so the persisted .phxlayer carries a live thumb.
    // Throttle is intentionally simple: we capture every widget on every
    // save (saves are explicit and human-paced), not on every edit — that
    // would burn render-thread budget without payoff. A dirty-since-last-
    // save flag would help but would also need its own lifetime (a widget
    // edit that doesn't touch the canvas — e.g. a graph commit — should
    // still invalidate the thumb), so we keep the rule "capture on save
    // = source of truth" for now and let future polish refine it.
    public async System.Threading.Tasks.Task CaptureAllWidgetThumbnailsAsync()
    {
        if (_viewByWidget.Count == 0) return;
        // Snapshot the pairs so a mid-capture Render() rebuild (which would
        // clear _viewByWidget under our feet) doesn't surface an
        // InvalidOperationException on enumeration.
        var pairs = _viewByWidget.ToArray();
        foreach (var kvp in pairs)
        {
            // Break the baked-thumbnail feedback loop. BeginThumbnailCapture hides
            // the stale ThumbnailHost (which shows the PREVIOUSLY baked image) and
            // the editor chrome, so we capture ONLY the current live render — never
            // re-baking the last baked frame. It returns false when there's no
            // valid live frame (preview off / Hub down / not navigated): we then
            // SKIP the bake and keep the existing thumbnail instead of baking the
            // stub or perpetuating an old error frame. This is the fix for the
            // persistent "404 behind the widget" (an error frame baked once used to
            // re-bake on every save and survive even after the bus came back).
            var view = kvp.Value;
            if (!view.BeginThumbnailCapture()) continue;
            try
            {
                string? b64 = await Phoenix.Controls.Visualist.WinUI.Core
                    .WidgetThumbnailCapture.CaptureBase64Async(view, 256);
                if (!string.IsNullOrEmpty(b64))
                {
                    kvp.Key.Thumbnail = b64;
                }
            }
            catch (System.Exception ex)
            {
                // Per-widget capture failure shouldn't abort the rest of the
                // save — log + continue. The widget keeps whatever Thumbnail
                // it had before.
                GlobalLogger.Error("LayerCanvasView",
                    $"CaptureAllWidgetThumbnailsAsync widget '{kvp.Key.Name}'", ex);
            }
            finally
            {
                view.EndThumbnailCapture();
            }
        }

        // A save just changed the .phxlayer on disk. Hub's LayerWatcher
        // will broadcast LAYER_RELOADED (which forces a re-capture via the bus
        // handler), but nudge a local re-capture too so the canvas reflects the
        // saved state immediately even when no Hub is connected. No-op when the
        // previewer isn't running.
        if (_previewer is not null)
        {
            try { await _previewer.CaptureNowAsync(); }
            catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "post-save re-capture", ex); }
        }
    }

    private void Render(VisualistViewModel vm)
    {
        // Mark a new view generation so any in-flight overlay-refresh batch
        // can detect it's rebuilding against stale views and bail (see
        // RefreshWidgetOverlaysAsync). unchecked so the counter wraps harmlessly
        // rather than overflowing on a very long session.
        unchecked { _renderVersion++; }
        WidgetSurface.Children.Clear();
        _viewByWidget.Clear();
        // The shared resize handle is a WidgetSurface child, so the Clear above
        // detached it — drop the stale reference (and any in-flight resize that
        // a re-render interrupted) so PositionResizeHandle rebuilds cleanly.
        _resizeHandle = null;
        if (_resizing)
        {
            _resizing = false;
            _resizeMoved = false;
            _resizeWidget = null;
            _resizeView = null;
        }
        Layer? layer = vm.SelectedLayer;

        // Only reset zoom/pan when the LAYER itself changes (not on the
        // SelectedWidget re-renders), so clicking a widget never zaps the user's
        // zoom but switching layers always recentres on the fresh stage.
        bool layerChanged = !ReferenceEquals(layer, _lastRenderedLayer);
        _lastRenderedLayer = layer;
        if (layerChanged) ResetStageZoomPan();

        if (layer is null || layer.Resolution.Width <= 0 || layer.Resolution.Height <= 0)
        {
            ResolutionLabel.Text = Localizer.T("visualist.canvas.resolution.no_layer", "(no layer loaded)");
            // Layer change invalidates any prior multi-select.
            _multiSelected.Clear();
            // Surface the New/Open empty-state when nothing is loaded.
            if (EmptyStateOverlay is not null) EmptyStateOverlay.Visibility = Visibility.Visible;
            return;
        }

        if (EmptyStateOverlay is not null) EmptyStateOverlay.Visibility = Visibility.Collapsed;
        WidgetSurface.Width  = layer.Resolution.Width;
        WidgetSurface.Height = layer.Resolution.Height;
        ResolutionLabel.Text = $"{layer.Name} · {layer.Resolution.Width} × {layer.Resolution.Height}";

        // Drop multi-select entries whose widgets are no longer in the layer
        // (deleted via context menu, etc.) before re-rendering.
        _multiSelected.RemoveWhere(w => !layer.Widgets.Contains(w));

        foreach (LayerWidget w in layer.Widgets)
        {
            WidgetView view = BuildView(w, vm);
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, w.Rect.X);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(view, w.Rect.Y);
            // Paint the preview in z-order so it matches the OBS compositor
            // (which sorts by zIndex). Pre-fix the editor painted in list order,
            // so Bring-to-Front / Send-to-Back changed the value but never moved
            // the widget in the preview. The resize handle uses SetZIndex(10000)
            // so it still stays on top of any real widget z.
            Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(view, w.ZIndex);
            WidgetSurface.Children.Add(view);
            _viewByWidget[w] = view;
        }

        // Place the resize handle over the primary-selected widget
        // (added last so it sits on top of every widget view).
        PositionResizeHandle(vm);

        // (Re)point the live previewer at this layer and refresh every
        // widget's overlay from the latest captured frame. SyncPreviewer reads
        // the layerId from the document's file-stem (matching Hub's LayerRegistry
        // key); an unsaved layer has no id, so the previewer detaches and the
        // overlays clear back to the stub.
        SyncPreviewer(vm, layer);
    }

    // ─── Live-preview pipeline ──────────────────────────────────────────

    // Hub serves overlays under /layer/<file-stem>; LayerRegistry keys each entry
    // by the same stem. Layer.Name is the DISPLAY name and is NOT what Hub looks
    // up, so we derive the id from the saved file path's stem (matching the
    // WinForms baseline's WidgetEditorForm.GetLayerIdForPreview). Returns null
    // for an unsaved scratch document — the previewer then detaches.
    private string? ResolveLayerIdForPreview()
    {
        string? path = _vm?.Document?.FilePath;
        if (string.IsNullOrEmpty(path)) return null;
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Fan a committed rect edit out to the live surfaces BEYOND the in-canvas
    /// previewer (which each caller already posts to). Two targets:
    ///
    ///   1. An open full-layer popout preview window — ALWAYS updated: it's a
    ///      design surface the author explicitly opened, so there's no reason to
    ///      make them re-save to see the move. No-op when no popout is open.
    ///   2. The live production OBS browser source, via the Hub bus
    ///      (<see cref="Core.VisualistBusClient.SendWidgetLiveUpdateAsync"/>) —
    ///      but ONLY when the user opted into AutoSyncOnEdit. Pushing rect updates
    ///      to the on-stream overlay is exactly the "sync my edits live" the flag
    ///      already promises; gating it there keeps a streamer who never asked for
    ///      live sync from seeing their overlay shift while they design. Skipped for
    ///      an unsaved scratch layer (no layerId ⇒ no live browser source).
    ///
    /// Both legs are ephemeral (compositor.js applies WIDGET_UPDATE in memory; a
    /// browser refresh reverts to the saved layer) and independently guarded so a
    /// publish fault can't break the gesture teardown. This retires the two
    /// dead live-update paths (popout PostWidgetUpdate + SendWidgetLiveUpdateAsync).
    /// </summary>
    private void PublishLiveWidgetEdit(LayerWidget widget)
    {
        if (widget is null) return;

        try { FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview?.PostWidgetUpdate(widget); }
        catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "popout live update", ex); }

        try
        {
            if (Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.AutoSyncOnEdit)
            {
                string? layerId = ResolveLayerIdForPreview();
                if (!string.IsNullOrEmpty(layerId))
                    _ = Phoenix.Controls.Visualist.WinUI.Core.VisualistBusClient.Instance
                            .SendWidgetLiveUpdateAsync(layerId!, widget);
            }
        }
        catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "live OBS widget update", ex); }
    }

    private void SyncPreviewer(VisualistViewModel vm, Layer layer)
    {
        bool enabled = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.CanvasWidgetPreviewEnabled;
        string? layerId = ResolveLayerIdForPreview();

        if (!enabled || layerId is null)
        {
            // Toggle off or unsaved layer — pause capture (if running) and clear
            // every overlay so the labeled-rectangle fallback shows through.
            _previewer?.Pause();
            ClearAllWidgetOverlays();
            return;
        }

        if (_previewer is null)
        {
            _previewer = new Phoenix.Controls.Visualist.WinUI.Controls.WidgetCanvasPreviewer();
            _previewer.OnFrameUpdated += OnPreviewFrameUpdated;
        }

        // AttachAsync is idempotent for an unchanged (layerId,resolution) — it
        // re-navigates, which is harmless. Fire-and-forget through the error
        // boundary: a faulted attach must not break the canvas render.
        _ = AttachPreviewerSafeAsync(layerId, layer.Resolution.Width, layer.Resolution.Height);
    }

    private async System.Threading.Tasks.Task AttachPreviewerSafeAsync(string layerId, int w, int h)
    {
        if (_previewer is null) return;

        // Record this as the desired target. If an attach is already running, it
        // will pick this up before it exits — so we don't start a second one.
        _pendingAttach = (layerId, w, h);
        if (_attachInFlight) return;

        _attachInFlight = true;
        try
        {
            // Drain to the latest pending target. A request arriving mid-await
            // overwrites _pendingAttach, so the loop re-runs against the newest
            // (layerId,w,h) and the previewer ends up pointed at the layer the
            // user actually settled on — never a stale intermediate one.
            while (_pendingAttach is { } target)
            {
                _pendingAttach = null;
                if (_previewer is null) return;
                try
                {
                    await _previewer.AttachAsync(target.LayerId, target.W, target.H);
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error("LayerCanvasView", "AttachPreviewerSafeAsync", ex);
                }
            }
        }
        finally
        {
            _attachInFlight = false;
        }
    }

    // Re-evaluate the previewer when the user flips CanvasWidgetPreviewEnabled
    // (or the bg colour) in settings. Marshalled onto the UI thread because
    // VisualistUserConfig.OnChanged can fire from any thread that calls Update.
    private void OnPreviewConfigChanged()
    {
        try
        {
            if (DispatcherQueue is null) { return; }
            DispatcherQueue.TryEnqueue(() =>
            {
                // Keep the command-bar toggles + active backdrop swatch in
                // step with an external pref flip.
                SyncPrefsUi();
                if (_vm is { } vm && vm.SelectedLayer is { } layer
                    && layer.Resolution.Width > 0 && layer.Resolution.Height > 0)
                {
                    SyncPreviewer(vm, layer);
                }
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "OnPreviewConfigChanged", ex);
        }
    }

    // ─── Command-bar commands + prefs strip ──────────────────────────────

    private bool _suppressPrefsEcho;

    // Transparent was dropped (it never produced a usable design-time backdrop);
    // Hex replaces it as the first swatch — a hex pasteboard pattern that frames the
    // layer bounds ("hex outside, flat inside"). The swatch fill is dark so the hex
    // glyph reads on it; the actual pasteboard pattern is the HexPatternOverlay.
    // TipKey rides alongside the English Tip so the swatch tooltip can be
    // translated without splitting the table — Tip stays the fallback the
    // Localizer degrades to when a bundle has no entry.
    private static readonly (Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor Color, string Tip, string TipKey, byte R, byte G, byte B, bool Hex)[] s_backdropSpecs =
    {
        (Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Hex,   "Hex pattern — frames the layer bounds", "visualist.canvas.backdrop.hex", 0x16, 0x13, 0x10, true),
        (Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Black, "Black", "visualist.canvas.backdrop.black", 0x00, 0x00, 0x00, false),
        (Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Gray,  "Gray",  "visualist.canvas.backdrop.gray",  0x80, 0x80, 0x80, false),
        (Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.White, "White", "visualist.canvas.backdrop.white", 0xFF, 0xFF, 0xFF, false),
    };
    private readonly Dictionary<Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor, Border> _backdropSwatches = new();

    private void BuildBackdropSwatches()
    {
        if (BackdropSwatches is null) return;
        BackdropSwatches.Children.Clear();
        _backdropSwatches.Clear();
        foreach (var spec in s_backdropSpecs)
        {
            var swatch = new Border
            {
                Width = 18, Height = 18,
                CornerRadius    = new CornerRadius(3),
                Background      = new SolidColorBrush(Color.FromArgb(0xFF, spec.R, spec.G, spec.B)),
                BorderBrush     = ResolveBrushTinted("CoalCardBrush", 0xFF, 0x2A, 0x26, 0x20),
                BorderThickness = new Thickness(1),
            };
            if (spec.Hex)
            {
                // Mini hexagon outline marks the hex-pattern backdrop swatch.
                swatch.Child = BuildHexGlyph();
            }
            ToolTipService.SetToolTip(swatch, string.Format(
                Localizer.T("visualist.canvas.backdrop.tip_format", "Preview backdrop: {0}"),
                Localizer.T(spec.TipKey, spec.Tip)));
            var captured = spec.Color;
            swatch.Tapped += (_, e) => { OnBackdropSwatchClicked(captured); e.Handled = true; };
            _backdropSwatches[spec.Color] = swatch;
            BackdropSwatches.Children.Add(swatch);
        }
    }

    private void SyncPrefsUi()
    {
        var cfg = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance;
        _suppressPrefsEcho = true;
        try
        {
            // CanvasPreviewToggle was removed as an abandoned affordance; its setting
            // (cfg.CanvasWidgetPreviewEnabled) still gates WidgetCanvasPreviewer and is
            // simply no longer user-toggleable from the command bar.
            if (AutoSyncToggle     is not null) AutoSyncToggle.IsChecked     = cfg.AutoSyncOnEdit;
            // Reflect the in-memory snap state (defaults true). Not config-
            // backed, so this seeds from the live field rather than cfg.
            if (SnapToggle         is not null) SnapToggle.IsChecked         = SnapEnabled;
        }
        finally { _suppressPrefsEcho = false; }

        foreach (var (color, border) in _backdropSwatches)
        {
            bool active = color == cfg.PreviewBackground;
            border.BorderBrush = active
                ? ResolveBrushTinted("SelectionBrush", 0xFF, 0xFF, 0xD7, 0x00)
                : ResolveBrushTinted("CoalCardBrush",  0xFF, 0x2A, 0x26, 0x20);
            border.BorderThickness = new Thickness(active ? 2 : 1);
        }

        // Pre-fix the swatch click only updated the config + this highlight; the
        // canvas backdrop itself was never repainted, so the buttons looked dead.
        ApplyCanvasBackdrop();
    }

    private void OnBackdropSwatchClicked(Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor color)
        => Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.Update(c => c.PreviewBackground = color);

    // Paints the selected preview backdrop onto the canvas. The layer rect
    // (StageFrame) gets an OPAQUE flat fill so widget previews sit on a known
    // background, and — for the Hex variant — the HexPatternOverlay pasteboard is
    // shown around the layer with a brighter layer border, giving the "hex outside,
    // flat inside" framing that makes the layer bounds obvious. Called from
    // SyncPrefsUi (initial bind + every config change).
    private void ApplyCanvasBackdrop()
    {
        if (StageFrame is null) return;
        var bg = Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.PreviewBackground;
        bool hex = bg == Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Hex;

        if (HexBackdrop is not null)
            HexBackdrop.Visibility = hex ? Visibility.Visible : Visibility.Collapsed;

        // Inside-the-layer flat fill (opaque so the hex never bleeds through). The
        // retired Transparent option maps to Black for any config still carrying it.
        Color inside = bg switch
        {
            Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.White => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Gray  => Color.FromArgb(0xFF, 0x80, 0x80, 0x80),
            Phoenix.Controls.Visualist.WinUI.Core.PreviewBgColor.Hex   => Color.FromArgb(0xFF, 0x16, 0x13, 0x10),
            _                                                          => Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        };
        StageFrame.Background = new SolidColorBrush(inside);

        // Brighter, thicker border in Hex mode so the layer edge pops against the
        // textured pasteboard; the normal coal divider otherwise.
        StageFrame.BorderBrush = hex
            ? ResolveBrushTinted("SelectionBrush",   0xFF, 0xFF, 0xD7, 0x00)
            : ResolveBrushTinted("CoalDividerBrush", 0xFF, 0x3A, 0x34, 0x2C);
        StageFrame.BorderThickness = new Thickness(hex ? 2 : 1);
    }

    // Small flat-top hexagon outline for the Hex backdrop swatch — mirrors the
    // HexPatternOverlay's flat-top math at swatch scale (per-pillar paint stays
    // local; this is a deliberate tiny duplicate, not a shared helper).
    private static Microsoft.UI.Xaml.Shapes.Path BuildHexGlyph()
    {
        const double cx = 9, cy = 9, r = 6;
        double half = r * 0.5;
        double yEdge = Math.Sqrt(3.0) * 0.5 * r;

        var figure = new Microsoft.UI.Xaml.Media.PathFigure
        {
            StartPoint = new Point(cx - r, cy),
            IsClosed = true,
            IsFilled = false,
        };
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(cx - half, cy - yEdge) });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(cx + half, cy - yEdge) });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(cx + r,    cy) });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(cx + half, cy + yEdge) });
        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = new Point(cx - half, cy + yEdge) });

        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
        geometry.Figures.Add(figure);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data                = geometry,
            Stroke              = new SolidColorBrush(Color.FromArgb(0xFF, 0xC9, 0xC2, 0xB6)),
            StrokeThickness     = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
    }

    private void OnAddWidgetCommand(object sender, RoutedEventArgs e)
    {
        TryFocus();
        if (_vm?.SelectedLayer is null) { FindPillarMainView()?.NewLayer(); return; }
        _vm.AddWidget();
    }

    private void OnPreviewLayerCommand(object sender, RoutedEventArgs e)
        => FindPillarMainView()?.PreviewLayer();

    // OnCanvasPreviewToggle was removed with the "Preview" canvas-thumbnail ToggleButton
    // (abandoned affordance). VisualistUserConfig.CanvasWidgetPreviewEnabled survives at
    // its default (true) and still gates WidgetCanvasPreviewer — the thumbnails render as
    // before, there is simply no command-bar switch for them any more.

    private void OnAutoSyncToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressPrefsEcho) return;
        bool on = AutoSyncToggle?.IsChecked == true;
        Phoenix.Controls.Visualist.WinUI.Core.VisualistUserConfig.Instance.Update(c => c.AutoSyncOnEdit = on);
    }

    // Snap toggle. SnapEnabled is in-memory only (no config round-trip yet,
    // see its doc comment), so this just flips the field and re-anchors any live
    // alignment overlay; a subsequent drag/resize reads the new state via
    // ShouldSnap(). No PushUndo — snap mode isn't part of the undo history.
    private void OnSnapToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressPrefsEcho) return;
        SnapEnabled = SnapToggle?.IsChecked == true;
        // A guide overlay built under the old snap mode would now be stale; drop
        // it so the next move/resize rebuilds against the current state.
        ClearAlignmentGuides();
    }

    private void OnEmptyNewLayer(object sender, RoutedEventArgs e)  => FindPillarMainView()?.NewLayer();
    private void OnEmptyOpenLayer(object sender, RoutedEventArgs e) => FindPillarMainView()?.OpenLayerDialog();

    // Hub broadcast: a .phxlayer was reloaded on disk. Force an off-cadence
    // capture so the canvas reflects the new file within one frame. The payload
    // is the layer id; we only react when it matches the layer we're previewing.
    private void OnBusLayerReloaded(string reloadedLayerId)
    {
        try
        {
            if (_previewer is null || DispatcherQueue is null) return;
            string? mine = ResolveLayerIdForPreview();
            if (mine is null) return;
            if (!string.IsNullOrEmpty(reloadedLayerId)
                && !string.Equals(reloadedLayerId, mine, StringComparison.OrdinalIgnoreCase))
                return;
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (_previewer is null) return;
                try
                {
                    // Re-navigate first if the previous load failed (a stuck error
                    // page can't self-reload), then force an off-cadence capture so
                    // the canvas reflects the new file within one frame.
                    _previewer.RecoverIfFailed();
                    await _previewer.CaptureNowAsync();
                }
                catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "OnBusLayerReloaded capture", ex); }
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "OnBusLayerReloaded", ex);
        }
    }

    // A fresh capture landed. Re-crop every mounted widget's rect out of the new
    // frame and push it onto the WidgetView overlay. Coalesced so overlapping
    // captures (4Hz timer + a forced reload capture) never stack crop batches.
    private void OnPreviewFrameUpdated()
    {
        if (DispatcherQueue is null) return;
        DispatcherQueue.TryEnqueue(async () => await RefreshWidgetOverlaysAsync());
    }

    // True while <paramref name="w"/> is the widget (or one of the widgets)
    // being actively dragged or resized on the canvas. Its rect on-screen is
    // ahead of the compositor's in-memory rect until the gesture commits, so its
    // captured crop is unreliable mid-gesture — see the skip in
    // RefreshWidgetOverlaysAsync.
    private bool IsWidgetUnderActiveGesture(LayerWidget w)
    {
        if (_dragWidget is not null)
        {
            if (_isGroupDragging) { if (_groupDragOrigins.ContainsKey(w)) return true; }
            else if (ReferenceEquals(_dragWidget, w)) return true;
        }
        if (_resizing && ReferenceEquals(_resizeWidget, w)) return true;
        return false;
    }

    private async System.Threading.Tasks.Task RefreshWidgetOverlaysAsync()
    {
        if (_previewRefreshInFlight) { _previewRefreshQueued = true; return; }
        _previewRefreshInFlight = true;
        try
        {
            do
            {
                _previewRefreshQueued = false;
                var previewer = _previewer;
                if (previewer is null) return;

                // Pin this batch to the current view generation. A Render()
                // landing mid-batch bumps _renderVersion; we then abandon the
                // rest of THIS batch and let the queued/timer re-run rebuild
                // against the fresh views instead of pushing crops onto a
                // half-rebuilt set.
                int batchVersion = _renderVersion;

                // No frame yet (or the toggle went off between enqueue + run) →
                // clear overlays and bail. Snapshot the pairs so a mid-batch
                // Render() rebuild doesn't surface a collection-modified throw.
                if (previewer.CurrentPng is null)
                {
                    ClearAllWidgetOverlays();
                    return;
                }

                var pairs = _viewByWidget.ToArray();
                foreach (var kvp in pairs)
                {
                    LayerWidget w = kvp.Key;
                    WidgetView  v = kvp.Value;

                    // Skip the widget under an active drag/resize gesture: the
                    // compositor still holds its PRE-gesture rect (WIDGET_UPDATE is
                    // posted on commit), so cropping it at its live (moved/resized)
                    // Rect samples the wrong region of the frame and flashes a
                    // neighbour's pixels. Its outline / last-good crop stays until
                    // the commit-time CaptureAfterCommit refreshes it correctly.
                    if (IsWidgetUnderActiveGesture(w)) continue;

                    try
                    {
                        var crop = await previewer.CropWidgetAsync(w.Rect);
                        // A Render() rebuilt the view set while this crop was
                        // in flight: the whole batch is now stale. Drop this crop
                        // and re-queue a fresh batch (so the just-rendered views
                        // still get filled) rather than continuing to push onto
                        // views that may be mid-rebuild.
                        if (batchVersion != _renderVersion)
                        {
                            crop?.Dispose();
                            _previewRefreshQueued = true;
                            break;
                        }
                        // The view may have been detached by a Render() rebuild
                        // mid-await — re-check membership before touching it.
                        if (!_viewByWidget.TryGetValue(w, out var live) || !ReferenceEquals(live, v))
                        {
                            crop?.Dispose();
                            continue;
                        }
                        if (crop is null) v.ClearLivePreview();
                        else v.SetLivePreview(crop); // takes ownership of crop
                    }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("LayerCanvasView", $"overlay refresh widget '{w.Name}'", ex);
                    }
                }
            }
            while (_previewRefreshQueued);
        }
        finally
        {
            _previewRefreshInFlight = false;
        }
    }

    private void ClearAllWidgetOverlays()
    {
        foreach (var v in _viewByWidget.Values)
        {
            try { v.ClearLivePreview(); } catch { /* view torn down — best effort */ }
        }
    }

    // ─── Resize handle ───────────────────────────────────────────────────

    /// <summary>
    /// Creates / repositions the single shared resize handle over the primary
    /// selected widget (<see cref="VisualistViewModel.SelectedWidget"/>), or
    /// removes it when there is no single primary target. Mirrors the baseline
    /// "primary only" rule — secondary multi-select members keep their gold
    /// border but expose no handle, so dragging the handle is unambiguous.
    /// </summary>
    private void PositionResizeHandle(VisualistViewModel vm)
    {
        LayerWidget? primary = vm.SelectedWidget;
        if (primary is null
            || vm.SelectedLayer is not Layer layer
            || !layer.Widgets.Contains(primary)
            || !_viewByWidget.TryGetValue(primary, out var primaryView))
        {
            RemoveResizeHandle();
            return;
        }

        if (_resizeHandle is null)
        {
            _resizeHandle = new Rectangle
            {
                Width  = HandleSize,
                Height = HandleSize,
                // §2 gold (SelectionBrush #FFD700) is the canonical
                // selection accent (Design_Orders §2: gold=selection); the
                // pre-fix code painted brass EmberPrimary and mislabelled it
                // "canonical". Fill at reduced alpha so the handle reads as a
                // grab affordance; a lighter gold stroke rims it. ResolveBrushTinted
                // falls back to the literal ARGB on a missing key (designer / pre-app).
                Fill = ResolveBrushTinted("SelectionBrush", 0xB0, 0xFF, 0xD7, 0x00),
                Stroke = ResolveBrushTinted("SelectionBrush", 0xFF, 0xFF, 0xE9, 0x66),
                StrokeThickness = 1,
                IsHitTestVisible = true,
            };
            ToolTipService.SetToolTip(_resizeHandle, Localizer.T("visualist.canvas.resize.tip", "Drag to resize"));
            WireResizeHandle(_resizeHandle);
        }

        // Always keep the handle on top of the freshly added widget views.
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(_resizeHandle, 10_000);
        if (!WidgetSurface.Children.Contains(_resizeHandle))
            WidgetSurface.Children.Add(_resizeHandle);

        UpdateResizeHandlePosition(primary);
        _resizeView = primaryView;
    }

    private void UpdateResizeHandlePosition(LayerWidget w)
    {
        if (_resizeHandle is null) return;
        // Anchor the handle so its centre sits on the widget's bottom-right
        // corner — half the handle overhangs the widget like the baseline's
        // corner grip, keeping it grabbable even on a flush-to-edge widget.
        double left = w.Rect.X + w.Rect.Width  - HandleSize / 2.0;
        double top  = w.Rect.Y + w.Rect.Height - HandleSize / 2.0;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_resizeHandle, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_resizeHandle,  top);
    }

    private void RemoveResizeHandle()
    {
        if (_resizeHandle is null) return;
        try { WidgetSurface.Children.Remove(_resizeHandle); }
        catch { /* surface torn down — best effort */ }
        _resizeHandle = null;
        _resizeView   = null;
    }

    private void WireResizeHandle(Rectangle handle)
    {
        handle.PointerPressed += (s, args) =>
        {
            PointerPoint p = args.GetCurrentPoint(WidgetSurface);
            if (!p.Properties.IsLeftButtonPressed) return;
            if (_vm?.SelectedWidget is not LayerWidget primary) return;

            TryFocus();
            _resizing        = true;
            _resizeMoved     = false;
            _resizeWidget    = primary;
            _viewByWidget.TryGetValue(primary, out _resizeView);
            _resizeStartLocal = p.Position;
            _resizeOriginW   = primary.Rect.Width;
            _resizeOriginH   = primary.Rect.Height;
            // One sibling-rect snapshot for the whole resize gesture (see
            // SnapshotGestureSiblingRects) — siblings can't move mid-resize.
            SnapshotGestureSiblingRects(primary);
            handle.CapturePointer(args.Pointer);
            args.Handled = true;
        };

        handle.PointerMoved += (s, args) =>
        {
            if (!_resizing || _resizeWidget is null) return;
            PointerPoint p = args.GetCurrentPoint(WidgetSurface);

            // Same stale-gesture guard as the widget-body drag: a resize only
            // continues while the left button is held. A swallowed activation
            // release (alt-tab back onto the handle) would otherwise leave the
            // handle stuck to the cursor. Settle like a release and stop.
            if (!p.Properties.IsLeftButtonPressed)
            {
                try { handle.ReleasePointerCapture(args.Pointer); } catch { /* already lost */ }
                FinishResize();
                return;
            }

            double dx = p.Position.X - _resizeStartLocal.X;
            double dy = p.Position.Y - _resizeStartLocal.Y;

            // Tiny-jitter dead zone so a click on the handle doesn't register
            // as a 1px resize (matches the move-drag dead zone above).
            if (!_resizeMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;

            if (!_resizeMoved)
            {
                // PushUndo ONCE, on the first real motion of the gesture.
                _vm?.Document?.PushUndo();
                _resizeMoved = true;
            }

            int nw = _resizeOriginW + (int)Math.Round(dx);
            int nh = _resizeOriginH + (int)Math.Round(dy);
            ClampAndApplyResize(_resizeWidget, _resizeView, nw, nh);

            // Reuse the move-drag alignment-guide overlay so resizing snaps /
            // shows dashed guides against sibling edges the same way moving does.
            UpdateAlignmentGuides();
        };

        handle.PointerReleased += (s, args) =>
        {
            if (!_resizing) return;
            handle.ReleasePointerCapture(args.Pointer);
            FinishResize();
        };

        handle.PointerCaptureLost += (s, args) =>
        {
            // Capture can be lost mid-resize (window deactivation, etc.) — settle
            // the same way a release would so the next grab re-arms cleanly.
            if (!_resizing) return;
            FinishResize();
        };
    }

    private void FinishResize()
    {
        bool moved = _resizeMoved;
        // Capture the resized widget before we null the gesture state so the
        // live-preview push below has its rect.
        LayerWidget? resized = _resizeWidget;

        // Settle gesture state + guides BEFORE any re-render so Render's
        // mid-resize guard doesn't have to clean up after us.
        ClearAlignmentGuides();
        ClearGestureSiblingRects();
        _guideDragSet.Clear();
        _resizing     = false;
        _resizeMoved  = false;
        _resizeWidget = null;
        // _resizeView stays pointed at the primary view (Render owns its
        // lifetime); only the in-flight gesture state is cleared here.

        if (moved)
        {
            _vm?.Document?.MarkDirty();
            // Push the committed geometry to the live WebView2 so the
            // captured frame reflects the new rect before the next save round-
            // trip. No-op when the previewer isn't running.
            if (resized is not null) { _previewer?.PostWidgetUpdate(resized); PublishLiveWidgetEdit(resized); }
            _previewer?.CaptureAfterCommit();
            // Re-sync the inspector mirror with the committed geometry; this
            // also re-renders the canvas and re-anchors the handle.
            _vm?.RaiseSelectedLayerChanged();
        }
    }

    /// <summary>
    /// Apply a candidate (width, height) to the widget being resized: snap to
    /// the 8px grid when <see cref="SnapEnabled"/>, snap the bottom-right corner
    /// to sibling alignment guides, then clamp to a sensible minimum and to the
    /// canvas bounds (the widget's top-left is fixed, so it stays on-canvas).
    /// Mutates the model + live WidgetView size + handle position together.
    /// </summary>
    private void ClampAndApplyResize(LayerWidget widget, WidgetView? view, int nw, int nh)
    {
        if (ShouldSnap())   // Alt suppresses grid snap mid-resize
        {
            nw = SnapToGrid(nw);
            nh = SnapToGrid(nh);
        }

        // Alignment-guide snap on the bottom-right corner. Convert the corner's
        // candidate position back into a width/height so the resize lands ON the
        // guide the user is seeing (matches the move-drop-snaps-to-guide rule).
        int right  = widget.Rect.X + nw;
        int bottom = widget.Rect.Y + nh;
        ResolveResizeAlignmentSnap(widget, ref right, ref bottom);
        nw = right  - widget.Rect.X;
        nh = bottom - widget.Rect.Y;

        // Minimum footprint so a widget can't collapse to an unrecoverable size.
        if (nw < MinWidgetSize) nw = MinWidgetSize;
        if (nh < MinWidgetSize) nh = MinWidgetSize;

        // Clamp to canvas bounds — the top-left is fixed during a resize, so the
        // width/height can grow only until the right/bottom edge hits the stage.
        int maxW = (int)Math.Max(MinWidgetSize, WidgetSurface.Width  - widget.Rect.X);
        int maxH = (int)Math.Max(MinWidgetSize, WidgetSurface.Height - widget.Rect.Y);
        if (nw > maxW) nw = maxW;
        if (nh > maxH) nh = maxH;

        widget.Rect.Width  = nw;
        widget.Rect.Height = nh;

        if (view is not null)
        {
            view.Width  = nw;
            view.Height = nh;
            view.RefreshPills();   // keep the inline w/h pills live during resize
        }

        UpdateResizeHandlePosition(widget);
    }

    /// <summary>
    /// Snap the resized widget's right / bottom edge to the canvas edges/centre
    /// or any sibling's matching edge / centre, via the shared
    /// <see cref="WidgetSnapCalculator"/> (resizing:true — top-left pinned, only
    /// the right + bottom edges snap, with the calculator's MinResizeDim floor).
    /// Distinct from <see cref="ResolveAlignmentSnap"/> (which moves the whole
    /// rect). Returned guides are stashed in <see cref="_engineSnapGuides"/> so
    /// the resize gesture renders the same lines its snap latched onto.
    /// </summary>
    private void ResolveResizeAlignmentSnap(LayerWidget resized, ref int right, ref int bottom)
    {
        bool bypass = IsAltDown();   // Alt suppresses alignment-guide snap

        if (_vm?.SelectedLayer is not Layer layer)
        {
            _engineSnapGuides = System.Array.Empty<WidgetSnapCalculator.Guide>();
            return;
        }

        // Candidate rect with the top-left pinned and the proposed right/bottom.
        var candidate = new System.Drawing.Rectangle(
            resized.Rect.X, resized.Rect.Y,
            right  - resized.Rect.X,
            bottom - resized.Rect.Y);

        // Sibling rects snapshotted once at resize start (siblings are static
        // mid-gesture); the inline rebuild covers any call outside an armed
        // gesture.
        List<System.Drawing.Rectangle> others;
        if (_gestureSiblingRectsValid)
        {
            others = _gestureSiblingRects;
        }
        else
        {
            others = new List<System.Drawing.Rectangle>();
            foreach (LayerWidget w in layer.Widgets)
            {
                if (ReferenceEquals(w, resized)) continue;
                others.Add(new System.Drawing.Rectangle(
                    w.Rect.X, w.Rect.Y, w.Rect.Width, w.Rect.Height));
            }
        }

        WidgetSnapCalculator.SnapResult result = WidgetSnapCalculator.Snap(
            candidate,
            layer.Resolution.Width,
            layer.Resolution.Height,
            others,
            bypass: bypass,
            resizing: true);

        // Convert the snapped width/height back to absolute right/bottom for the
        // caller (which derives nw/nh from them).
        right  = result.Rect.X + result.Rect.Width;
        bottom = result.Rect.Y + result.Rect.Height;
        _engineSnapGuides = result.Guides;
    }

    private WidgetView BuildView(LayerWidget widget, VisualistViewModel vm)
    {
        LayerWidget capture = widget;
        bool isInMultiSelect = _multiSelected.Contains(widget);
        var view = new WidgetView
        {
            Width      = widget.Rect.Width,
            Height     = widget.Rect.Height,
            WidgetName = string.IsNullOrEmpty(widget.Name)
                ? Localizer.T("visualist.canvas.widget.unnamed", "(unnamed)")
                : widget.Name,
            ThumbKind  = ResolveThumbKind(widget.Preset),
            // Round-trip the captured thumbnail. WidgetView falls back
            // to the preset stub when this is null/empty so an unsaved widget
            // (or one whose capture failed) still gets a usable visual.
            ThumbnailB64 = widget.Thumbnail,
            // A widget shows as selected whether it's the VM's primary
            // SelectedWidget or part of the view-side multi-select set.
            IsSelected = ReferenceEquals(widget, vm.SelectedWidget) || isInMultiSelect,
            ContextFlyout = BuildWidgetContextMenu(vm, widget),
        };

        // Wire the inline value pills. Each commit pushes
        // undo + marks dirty + invalidates the layer so the canvas re-renders
        // at the new geometry. Same shape the drag handlers below use; the
        // pill is a discrete-edit alternative to dragging. The inspector becomes a
        // read-only mirror — see InspectorPanel.xaml for the disabled controls.
        view.SetEditTarget(widget, committed =>
        {
            try
            {
                vm.Document?.PushUndo();
                vm.Document?.MarkDirty();
                // SelectedWidget = same instance short-circuits on equality so
                // RaiseSelectedLayerChanged is the path the inspector also
                // uses for inline rect mutations (see InspectorPanel.UpdateRect).
                vm.RaiseSelectedLayerChanged();
            }
            catch (System.Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerCanvasView", "WidgetView pill commit", ex);
            }
        });

        // Inline rename commit. WidgetView hands back the new name
        // WITHOUT mutating the model, so we push undo against the pre-change
        // state, apply the name, mark dirty, then re-render (which also refreshes
        // the inspector mirror via RaiseSelectedLayerChanged).
        view.SetNameCommit((target, newName) =>
        {
            try
            {
                vm.Document?.PushUndo();
                target.Name = newName;
                vm.Document?.MarkDirty();
                vm.RaiseSelectedLayerChanged();
            }
            catch (System.Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Error(
                    "LayerCanvasView", "WidgetView rename commit", ex);
            }
        });

        // Double-tap the widget body enters its node editor. WidgetView
        // raises EnterRequested from a body double-tap (the name footer renames,
        // the inline pills edit geometry — both own their own gestures), which
        // we bubble to MainView via WidgetEnterRequested to swap into the editor.
        view.EnterRequested += w => RequestEnter(w);

        view.RightTapped += (s, args) =>
        {
            // Right-click selects + opens the menu — UX consistency with the
            // Tapped/PointerPressed left-click flow.
            vm.SelectedWidget = capture;
            // Re-anchor keyboard focus on this canvas so a subsequent
            // Ctrl+C/X/V/Del lands here instead of bubbling to the Hub shell.
            // PointerPressed already does this on left-clicks; mirroring on
            // RightTapped fixes the gap where a context-menu-driven workflow
            // (right-click → Copy) left focus stranded on whatever surface had
            // it before the right-click landed.
            TryFocus();
            RecomputeHotkeyContext();
        };

        view.PointerPressed += (s, args) =>
        {
            // A press that lands on an inline geometry / preset / name pill belongs
            // to the pill's own edit gesture (Tapped → inline TextBox). TryFocus()
            // here yanks keyboard focus to the canvas, and CapturePointer() below
            // steals the pointer — either one leaves the pill's edit box open but
            // unable to receive a single keystroke (the reported "can't modify the
            // values" bug). Consume the press so it also doesn't bubble to the
            // surface lasso / focus-steal, then let the pill's Tapped run.
            if (IsEditAffordancePress(args.OriginalSource))
            {
                args.Handled = true;
                return;
            }

            // Make sure subsequent Ctrl+A/X/C/V/Del land on this canvas, not
            // the embedding pillar shell.
            TryFocus();

            // Only left-click drags; right-click reserved for context menu.
            PointerPoint p = args.GetCurrentPoint(WidgetSurface);
            if (!p.Properties.IsLeftButtonPressed) return;

            // Shift-click toggles in/out of the multi-select set without
            // starting a drag — mirrors the widget-graph pattern.
            if (IsShiftDown())
            {
                if (_multiSelected.Contains(capture))
                {
                    _multiSelected.Remove(capture);
                    view.IsSelected = false;
                    // Don't leave the just-removed widget as the primary anchor —
                    // re-anchor onto a still-selected widget (or null when the set
                    // is now empty) so OnVmPropertyChanged doesn't treat the next
                    // anchor as an external select and collapse the remaining set.
                    if (ReferenceEquals(vm.SelectedWidget, capture))
                        vm.SelectedWidget = _multiSelected.LastOrDefault();
                }
                else
                {
                    _multiSelected.Add(capture);
                    view.IsSelected = true;
                    vm.SelectedWidget = capture;  // anchor on the most-recently-added
                }
                PublishMultiSelection(vm);
                args.Handled = true;
                RecomputeHotkeyContext();
                return;
            }

            // No shift. If the clicked widget is already part of the
            // multi-select set we group-drag everything; otherwise the click
            // collapses the multi-select to just this widget.
            if (_multiSelected.Contains(capture))
            {
                _isGroupDragging = true;
                _groupDragOrigins.Clear();
                foreach (var w in _multiSelected)
                    _groupDragOrigins[w] = (w.Rect.X, w.Rect.Y);
            }
            else
            {
                ClearMultiSelect();
                _multiSelected.Add(capture);
                view.IsSelected = true;
                _isGroupDragging = false;
            }

            vm.SelectedWidget = capture;
            PublishMultiSelection(vm);   // reflect the collapse-to-one / group keep
            _dragWidget   = capture;
            _dragView     = view;
            _dragStartLocal = p.Position;
            _dragOriginX  = capture.Rect.X;
            _dragOriginY  = capture.Rect.Y;
            _dragMoved    = false;
            _dragPointerId = args.Pointer.PointerId;
            // Arm the drag ONLY if we actually capture the pointer. If CapturePointer
            // fails, clear the armed state so a later move can't drag a widget the
            // user merely clicked, and no half-armed gesture lingers to fire later.
            if (!view.CapturePointer(args.Pointer))
            {
                _dragWidget = null;
                _dragView   = null;
                _dragPointerId = 0;
            }
            // Siblings can't move mid-gesture — snapshot their rects once for
            // the whole drag instead of per pointer-move (see
            // SnapshotGestureSiblingRects). Only when the drag actually armed.
            if (_dragWidget is not null) SnapshotGestureSiblingRects(capture);
            args.Handled  = true;
            RecomputeHotkeyContext();
        };

        view.PointerMoved += (s, args) =>
        {
            // Guard on BOTH the active widget and the arming pointer id: a stale
            // move from a previous, incompletely-released gesture (different pointer,
            // or this widget after teardown) must never reach the move math.
            if (!ReferenceEquals(_dragWidget, capture) || args.Pointer.PointerId != _dragPointerId) return;
            PointerPoint p = args.GetCurrentPoint(WidgetSurface);

            // A drag is only valid while its initiating (left) button stays held.
            // When the window is re-activated by a click (alt-tab back in), WinUI can
            // deliver the activating PointerPressed — which arms the drag and captures
            // the pointer — but NOT the matching PointerReleased, leaving the widget
            // "stuck to the cursor" on the next button-up move. PointerReleased /
            // PointerCaptureLost are the primary teardown, but we can't trust them to
            // fire after a swallowed activation release, so we re-assert the invariant
            // here: a move seen while armed but with the button up is a stale gesture —
            // settle it like a release and stop before any move math runs.
            if (!p.Properties.IsLeftButtonPressed)
            {
                EndWidgetDrag(args.Pointer);
                return;
            }

            double dx = p.Position.X - _dragStartLocal.X;
            double dy = p.Position.Y - _dragStartLocal.Y;

            // Tiny-jitter dead zone so a click doesn't register as a 1px nudge.
            if (!_dragMoved && Math.Abs(dx) < 2 && Math.Abs(dy) < 2) return;

            if (!_dragMoved)
            {
                vm.Document?.PushUndo();
                _dragMoved = true;
            }

            if (_isGroupDragging)
            {
                ApplyGroupDragDelta((int)Math.Round(dx), (int)Math.Round(dy));
            }
            else
            {
                int nx = _dragOriginX + (int)Math.Round(dx);
                int ny = _dragOriginY + (int)Math.Round(dy);
                ClampAndApplyMove(capture, view, nx, ny);
            }

            // Alignment-guide overlay is rebuilt every move so the
            // dashed lines track the dragged widget's edges/centre against
            // every sibling in the layer. Cleared on release / cancel.
            UpdateAlignmentGuides();
        };

        view.PointerReleased += (s, args) =>
        {
            if (!ReferenceEquals(_dragWidget, capture) || args.Pointer.PointerId != _dragPointerId) return;
            EndWidgetDrag(args.Pointer);
        };

        view.PointerCaptureLost += (s, args) =>
        {
            // Capture can be lost mid-drag (window deactivation, a layout pass
            // stealing it, etc.). Tear down identically so future presses re-arm
            // cleanly — leaving _dragWidget set + capture held is what made a widget
            // keep following the cursor after the gesture ended.
            if (!ReferenceEquals(_dragWidget, capture)) return;
            EndWidgetDrag(args.Pointer);
        };

        view.PointerCanceled += (s, args) =>
        {
            // System-cancelled gesture (palm rejection, pointer device removed). The
            // widget's live position was already applied during PointerMoved, so we
            // commit it like a normal release rather than leaving the drag half-armed.
            if (!ReferenceEquals(_dragWidget, capture)) return;
            EndWidgetDrag(args.Pointer);
        };

        return view;
    }

    /// <summary>
    /// Idempotent teardown for a single-widget / group drag, shared by
    /// PointerReleased / PointerCaptureLost / PointerCanceled. Releasing the pointer
    /// capture is wrapped so a throw (e.g. capture already lost) can NEVER skip the
    /// state reset below: a leaked <see cref="_dragWidget"/> + still-held capture is
    /// exactly what made a widget stick to the cursor or jump to the next click.
    /// Operates on the current drag-state fields, so each caller only guards that it
    /// owns the active gesture before invoking.
    /// </summary>
    private void EndWidgetDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        WidgetView? view = _dragView;
        if (pointer is not null && view is not null)
        {
            try { view.ReleasePointerCapture(pointer); } catch { /* already lost — harmless */ }
        }

        if (_dragMoved)
        {
            _vm?.Document?.MarkDirty();
            // Push the committed position(s) to the live WebView2 so the
            // captured frame tracks the move without a save round-trip.
            if (_isGroupDragging)
            {
                foreach (var w in _groupDragOrigins.Keys) { _previewer?.PostWidgetUpdate(w); PublishLiveWidgetEdit(w); }
            }
            else if (_dragWidget is not null)
            {
                _previewer?.PostWidgetUpdate(_dragWidget);
                PublishLiveWidgetEdit(_dragWidget);
            }
            // Re-render the moved widget's content now instead of waiting up to
            // the 250ms capture timer — the posted WIDGET_UPDATE paints a frame
            // later, so CaptureAfterCommit grabs it staggered.
            _previewer?.CaptureAfterCommit();
        }

        ClearAlignmentGuides();
        ClearGestureSiblingRects();
        _guideDragSet.Clear();
        _dragWidget      = null;
        _dragView        = null;
        _dragMoved       = false;
        _dragPointerId   = 0;
        _isGroupDragging = false;
        _groupDragOrigins.Clear();
    }

    // ─── Lasso (rubber-band) selection ────────────────────────────────────
    // Empty-canvas press → start a rubber-band rectangle. Per-widget
    // PointerPressed handlers consume their events first, so this only
    // fires when the click misses every widget.

    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(WidgetSurface);
        if (!p.Properties.IsLeftButtonPressed) return;

        _isLassoing  = true;
        _lassoExtend = IsShiftDown();
        _lassoStart  = p.Position;

        _lassoRect = new Rectangle
        {
            Width = 0,
            Height = 0,
            // §2 gold marquee (SelectionBrush #FFD700). The pre-fix code
            // painted brass EmberPrimary and falsely called it the §2 accent;
            // gold is the canonical selection colour. Routed through
            // ResolveBrushTinted so a missing key (designer / pre-app) falls back
            // to the literal ARGB rather than throwing on a direct lookup.
            Stroke = ResolveBrushTinted("SelectionBrush", 0xFF, 0xFF, 0xD7, 0x00),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            // Gold marquee fill at α=0x22 (matches SelectionDimBrush's hue).
            Fill = ResolveBrushTinted("SelectionBrush", 0x22, 0xFF, 0xD7, 0x00),
            IsHitTestVisible = false,
        };
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, p.Position.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_lassoRect,  p.Position.Y);
        WidgetSurface.Children.Add(_lassoRect);

        WidgetSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLassoing || _lassoRect is null) return;
        var pp = e.GetCurrentPoint(WidgetSurface);

        // Stale-gesture guard (same class as the widget-drag / resize fix): a lasso
        // only continues while the left button is held. A swallowed activation
        // release (alt-tab back onto empty canvas) would otherwise leave the
        // rubber-band rect tracking the cursor with no button down. Tear it down
        // like a capture-lost rather than continuing to stretch it.
        if (!pp.Properties.IsLeftButtonPressed)
        {
            if (_lassoRect is not null) WidgetSurface.Children.Remove(_lassoRect);
            _lassoRect  = null;
            _isLassoing = false;
            try { WidgetSurface.ReleasePointerCapture(e.Pointer); } catch { /* already lost */ }
            RecomputeHotkeyContext();
            return;
        }

        var p = pp.Position;
        Rect r = NormalizeRect(_lassoStart, p);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(_lassoRect, r.X);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(_lassoRect,  r.Y);
        _lassoRect.Width  = r.Width;
        _lassoRect.Height = r.Height;
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLassoing) return;
        var p = e.GetCurrentPoint(WidgetSurface).Position;
        Rect lasso = NormalizeRect(_lassoStart, p);
        FinaliseLasso(lasso);
        WidgetSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        RecomputeHotkeyContext();
    }

    private void OnSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isLassoing) return;
        // Best-effort cleanup; don't rebuild the selection from a cancelled lasso.
        if (_lassoRect is not null) WidgetSurface.Children.Remove(_lassoRect);
        _lassoRect = null;
        _isLassoing = false;
        RecomputeHotkeyContext();
    }

    private void FinaliseLasso(Rect lasso)
    {
        if (_lassoRect is not null) WidgetSurface.Children.Remove(_lassoRect);
        _lassoRect = null;
        _isLassoing = false;

        // Click-without-drag (lasso area is effectively zero) — treat as a
        // canvas click. Without shift, clear selection. With shift, no-op.
        bool clickOnly = lasso.Width < 2 && lasso.Height < 2;
        if (clickOnly)
        {
            if (!_lassoExtend) ClearMultiSelectAndAnchor();
            return;
        }

        if (!_lassoExtend) ClearMultiSelect();

        Layer? layer = _vm?.SelectedLayer;
        if (layer is null) return;

        LayerWidget? lastHit = null;
        foreach (LayerWidget w in layer.Widgets)
        {
            var widgetRect = new Rect(w.Rect.X, w.Rect.Y, w.Rect.Width, w.Rect.Height);
            if (RectsIntersect(lasso, widgetRect))
            {
                _multiSelected.Add(w);
                if (_viewByWidget.TryGetValue(w, out var view)) view.IsSelected = true;
                lastHit = w;
            }
        }

        if (lastHit is not null && _vm is not null)
            _vm.SelectedWidget = lastHit;
        if (_vm is not null) PublishMultiSelection(_vm);
    }

    private void ApplyGroupDragDelta(int dx, int dy)
    {
        // Compute the maximum allowed delta so no widget in the group
        // breaches the canvas bounds — keeps the relative formation intact
        // (clamping each widget independently would shear the group).
        int maxDxNeg = int.MinValue, maxDxPos = int.MaxValue;
        int maxDyNeg = int.MinValue, maxDyPos = int.MaxValue;
        int canvasW = (int)WidgetSurface.Width;
        int canvasH = (int)WidgetSurface.Height;
        foreach (var (w, origin) in _groupDragOrigins)
        {
            // -origin.X bounds the leftward delta; (canvasW - w.Rect.Width - origin.X) bounds the rightward.
            int leftLimit  = -origin.X;
            int rightLimit = Math.Max(0, canvasW - w.Rect.Width - origin.X);
            int upLimit    = -origin.Y;
            int downLimit  = Math.Max(0, canvasH - w.Rect.Height - origin.Y);
            if (leftLimit  > maxDxNeg) maxDxNeg = leftLimit;
            if (rightLimit < maxDxPos) maxDxPos = rightLimit;
            if (upLimit    > maxDyNeg) maxDyNeg = upLimit;
            if (downLimit  < maxDyPos) maxDyPos = downLimit;
        }

        if (dx < maxDxNeg) dx = maxDxNeg;
        if (dx > maxDxPos) dx = maxDxPos;
        if (dy < maxDyNeg) dy = maxDyNeg;
        if (dy > maxDyPos) dy = maxDyPos;

        // Snap the group delta to the 8px grid rather than each member's
        // resulting position. Snapping the per-widget positions would shear
        // the formation: two widgets 4px apart on X would collapse onto the
        // same column the moment the cursor crossed a grid line. Snapping the
        // delta preserves relative offsets and just moves the group in 8px hops.
        if (ShouldSnap())   // Alt suppresses grid snap mid-group-drag
        {
            dx = SnapDelta(dx);
            dy = SnapDelta(dy);
        }

        foreach (var (w, origin) in _groupDragOrigins)
        {
            int nx = origin.X + dx;
            int ny = origin.Y + dy;
            w.Rect.X = nx;
            w.Rect.Y = ny;
            if (_viewByWidget.TryGetValue(w, out var view))
            {
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, nx);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(view, ny);
            }
        }
    }

    private void ClampAndApplyMove(LayerWidget widget, WidgetView view, int nx, int ny)
    {
        // Snap the dragged origin to the 8px grid (when the persistent
        // toggle is on) BEFORE clamping. Then, if the resulting position
        // aligns within the 4px alignment-guide tolerance of any sibling
        // edge/centre, snap-to-guide overrides the grid snap so the drop
        // commits exactly on the guide the user is seeing. Matches the
        // commit-drop-snaps-to-guide behaviour.
        if (ShouldSnap())   // Alt suppresses grid snap mid-move
        {
            nx = SnapToGrid(nx);
            ny = SnapToGrid(ny);
        }

        // Alignment-guide snap — fires regardless of SnapEnabled, since the
        // dashed guides themselves only render when the threshold is crossed.
        // If the user dragged close enough to a sibling edge for a guide to
        // appear, the commit lands ON that edge rather than 1-3px off it.
        ResolveAlignmentSnap(widget, ref nx, ref ny);

        int maxX = (int)Math.Max(0, WidgetSurface.Width  - widget.Rect.Width);
        int maxY = (int)Math.Max(0, WidgetSurface.Height - widget.Rect.Height);
        if (nx < 0)    nx = 0;
        if (ny < 0)    ny = 0;
        if (nx > maxX) nx = maxX;
        if (ny > maxY) ny = maxY;

        widget.Rect.X = nx;
        widget.Rect.Y = ny;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, nx);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(view, ny);
    }

    // ─── Snap + alignment guide overlay ──────────────────────────────────

    private static int SnapToGrid(int v) =>
        (int)Math.Round(v / (double)SnapGridStepPx) * SnapGridStepPx;

    private static int SnapDelta(int v) =>
        (int)Math.Round(v / (double)SnapGridStepPx) * SnapGridStepPx;

    /// <summary>
    /// Snapshot every non-moving sibling's rect once at gesture start (drag
    /// or resize). Siblings can't move mid-gesture — the invariant the
    /// <see cref="_lastGuideAnchorRect"/> overlay cache already relies on — so
    /// the per-move snap passes reuse this list instead of rebuilding it (plus
    /// a rect per sibling) on every PointerMoved. Excludes the anchor and,
    /// during a group drag, every other moving member — the same exclusions
    /// the inline fallbacks in the snap passes apply.
    /// </summary>
    private void SnapshotGestureSiblingRects(LayerWidget anchor)
    {
        _gestureSiblingRects.Clear();
        _gestureSiblingRectsValid = false;
        if (_vm?.SelectedLayer is not Layer layer) return;
        foreach (LayerWidget w in layer.Widgets)
        {
            if (ReferenceEquals(w, anchor)) continue;
            if (_isGroupDragging && _groupDragOrigins.ContainsKey(w)) continue;
            _gestureSiblingRects.Add(new System.Drawing.Rectangle(
                w.Rect.X, w.Rect.Y, w.Rect.Width, w.Rect.Height));
        }
        _gestureSiblingRectsValid = true;
    }

    /// <summary>Symmetric teardown for <see cref="SnapshotGestureSiblingRects"/>.</summary>
    private void ClearGestureSiblingRects()
    {
        _gestureSiblingRects.Clear();
        _gestureSiblingRectsValid = false;
    }

    /// <summary>
    /// Snap the candidate move position by delegating to
    /// the pure-data <see cref="WidgetSnapCalculator"/> in the Visualist Engine.
    /// The calculator snaps the dragged rect against the layer-canvas
    /// edges/centre AND every sibling's edges/centre (within its 6px threshold)
    /// and returns the alignment guides that explain the snap; we stash those in
    /// <see cref="_engineSnapGuides"/> so the very same PointerMoved pass renders
    /// exactly the lines the snap latched onto. Signature (ref x/y) is unchanged
    /// so <see cref="ClampAndApplyMove"/> still calls it the same way; the inline
    /// per-sibling cascade it replaced computed snap and guides independently and
    /// knew nothing about the canvas edges.
    /// </summary>
    private void ResolveAlignmentSnap(LayerWidget dragged, ref int x, ref int y)
    {
        // Alt bypasses the alignment snap entirely. We still pass the
        // bypass flag to the calculator (rather than early-returning) so it
        // clears any stale guides for an honest "no snap" frame.
        bool bypass = IsAltDown();

        if (_vm?.SelectedLayer is not Layer layer)
        {
            _engineSnapGuides = System.Array.Empty<WidgetSnapCalculator.Guide>();
            return;
        }

        // Candidate rect at the (grid-snapped) position the caller proposes.
        var candidate = new System.Drawing.Rectangle(
            x, y, dragged.Rect.Width, dragged.Rect.Height);

        // Other-widget rects = every sibling except the dragged widget and,
        // during a group drag, every other moving member (so the formation
        // never snaps to its own tail). Snapshotted once at gesture start
        // (siblings are static mid-gesture); the inline rebuild below covers
        // any call outside an armed gesture.
        List<System.Drawing.Rectangle> others;
        if (_gestureSiblingRectsValid)
        {
            others = _gestureSiblingRects;
        }
        else
        {
            others = new List<System.Drawing.Rectangle>();
            foreach (LayerWidget w in layer.Widgets)
            {
                if (ReferenceEquals(w, dragged)) continue;
                if (_isGroupDragging && _groupDragOrigins.ContainsKey(w)) continue;
                others.Add(new System.Drawing.Rectangle(
                    w.Rect.X, w.Rect.Y, w.Rect.Width, w.Rect.Height));
            }
        }

        // resizing:false — this is a MOVE (top-left tracks the cursor). The
        // resize gesture has its own snap path (SnapWidgetCorner). Canvas bounds
        // come from the layer resolution, matching how the calculator derives
        // its 0 / mid / max edge targets.
        WidgetSnapCalculator.SnapResult result = WidgetSnapCalculator.Snap(
            candidate,
            layer.Resolution.Width,
            layer.Resolution.Height,
            others,
            bypass: bypass,
            resizing: false);

        x = result.Rect.X;
        y = result.Rect.Y;
        _engineSnapGuides = result.Guides;
    }

    /// <summary>
    /// Rebuild the alignment-guide overlay (dashed gold lines) against
    /// every non-selected sibling's matching edge / centre. Called every
    /// PointerMoved during a drag; cleared on release / cancel.
    /// </summary>
    private void UpdateAlignmentGuides()
    {
        // Alt suppresses snapping, so don't draw guides that wouldn't snap.
        if (IsAltDown())
        {
            ClearAlignmentGuides();
            return;
        }
        if (_vm?.SelectedLayer is not Layer layer)
        {
            ClearAlignmentGuides();
            return;
        }

        // Anchor = single-drag target, group-drag anchor, or resize target.
        var anchor = _dragWidget ?? (_resizing ? _resizeWidget : null);
        if (anchor is null)
        {
            ClearAlignmentGuides();
            return;
        }

        int dragX = anchor.Rect.X;
        int dragY = anchor.Rect.Y;
        int dragW = anchor.Rect.Width;
        int dragH = anchor.Rect.Height;

        // Perf — short-circuit when the anchor hasn't moved/resized since the
        // last rebuild. The overlay depends only on the anchor rect (siblings
        // are static mid-gesture), so an unchanged rect ⇒ identical overlay
        // (including the "no guide matched" empty overlay — caching that too
        // means a drag that stays off-grid costs nothing per frame).
        // ClearAlignmentGuides resets the sentinel, so the next genuine move (or
        // a fresh gesture after release) rebuilds. The cached shapes survive this
        // no-op frame because we return before touching them.
        var curAnchorRect = (dragX, dragY, dragW, dragH);
        if (_lastGuideAnchorRect == curAnchorRect)
            return;

        ClearAlignmentGuides();
        _lastGuideAnchorRect = curAnchorRect;

        // Drag set — for a single drag this is just _dragWidget; for a group
        // drag every member moves together so all of them are excluded from
        // the alignment cascade (a member shouldn't draw a guide against
        // another member). During a resize the anchor is the widget being
        // resized — same guide overlay, different gesture. Reuses one cleared
        // set per rebuild instead of allocating a fresh HashSet per move.
        var dragSet = _guideDragSet;
        dragSet.Clear();
        if (_isGroupDragging)
        {
            foreach (var kv in _groupDragOrigins) dragSet.Add(kv.Key);
        }
        else if (_dragWidget is not null)
        {
            dragSet.Add(_dragWidget);
        }
        else if (_resizing && _resizeWidget is not null)
        {
            dragSet.Add(_resizeWidget);
        }

        int dragRight = dragX + dragW;
        int dragBottom = dragY + dragH;

        // §2 gold (SelectionBrush #FFD700) is the canonical selection
        // accent (Design_Orders §2: gold=selection). The pre-fix stroke used
        // brass EmberPrimary, so guides read in a different colour than the rest
        // of the selection chrome (selected-widget border, resize handle, lasso
        // marquee). Same opacity (0xCC = 80%), routed through ResolveBrushTinted
        // so a missing key (designer / pre-app) falls back to the literal ARGB.
        Brush stroke = ResolveBrushTinted("SelectionBrush", 0xCC, 0xFF, 0xD7, 0x00);

        // Render the EXACT guide segments the snap engine
        // (WidgetSnapCalculator) returned, captured into _engineSnapGuides by
        // the matching ResolveAlignmentSnap / ResolveResizeAlignmentSnap pass.
        // The engine emits canvas-spanning axis-aligned guides (vertical:
        // X1==X2, horizontal: Y1==Y2); we keep the localized-span aesthetic by
        // bounding each guide to the anchor's extent ± margin (clamped to the
        // canvas) so the dashed line reads as a snap zone around the moving
        // widget rather than a full-stage line. Position (the snapped axis) comes
        // straight from the engine, so guide and snap are always consistent.
        foreach (WidgetSnapCalculator.Guide g in _engineSnapGuides)
        {
            bool vertical = g.X1 == g.X2;
            if (vertical)
            {
                int spanTop    = dragY - AlignGuideSpanMarginPx;
                int spanBottom = dragBottom + AlignGuideSpanMarginPx;
                AddEngineGuideLine(g.X1, spanTop, g.X1, spanBottom, stroke);
            }
            else // horizontal (Y1 == Y2)
            {
                int spanLeft  = dragX - AlignGuideSpanMarginPx;
                int spanRight = dragRight + AlignGuideSpanMarginPx;
                AddEngineGuideLine(spanLeft, g.Y1, spanRight, g.Y1, stroke);
            }
        }

        // Overlap detection — independent of the snap engine (the calculator
        // doesn't model collisions). If the dragged widget's rect intersects a
        // non-moving sibling's rect, paint a translucent red overlay so the drop
        // collision is visible at a glance. Same colour key the wire-preview uses
        // for incompatible drops (kept palette-coherent).
        foreach (LayerWidget w in layer.Widgets)
        {
            if (dragSet.Contains(w)) continue;

            int nx = w.Rect.X;
            int ny = w.Rect.Y;
            int nr = nx + w.Rect.Width;
            int nb = ny + w.Rect.Height;

            bool overlap = dragX < nr && dragRight > nx
                        && dragY < nb && dragBottom > ny;
            if (overlap)
            {
                var collision = new Rectangle
                {
                    Width  = w.Rect.Width,
                    Height = w.Rect.Height,
                    Fill   = ResolveBrushTinted("ErrBrush", 0x40, 0xC9, 0x53, 0x3C),
                    IsHitTestVisible = false,
                };
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(collision, nx);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop (collision, ny);
                WidgetSurface.Children.Add(collision);
                _snapGuideShapes.Add(collision);
            }
        }
    }

    // Draw one dashed gold guide line for a snap the engine
    // already decided on (no threshold re-gate here; the engine's 6px
    // SnapThreshold owns that). Endpoints are clamped to the canvas bounds so the
    // localized ± margin never paints off-surface. Matches the dash/opacity of
    // the TryAdd*Guide helpers so engine guides and any future guides read alike.
    private void AddEngineGuideLine(int x1, int y1, int x2, int y2, Brush stroke)
    {
        double width  = WidgetSurface.Width;
        double height = WidgetSurface.Height;
        if (width <= 0 || height <= 0) return;
        double cx1 = Math.Clamp(x1, 0.0, width);
        double cy1 = Math.Clamp(y1, 0.0, height);
        double cx2 = Math.Clamp(x2, 0.0, width);
        double cy2 = Math.Clamp(y2, 0.0, height);
        // Degenerate after clamping (guide axis off-canvas, or span collapsed).
        if (cx1 == cx2 && cy1 == cy2) return;
        var line = new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = cx1, Y1 = cy1,
            X2 = cx2, Y2 = cy2,
            Stroke = stroke,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
            Opacity = 0.9,
        };
        WidgetSurface.Children.Add(line);
        _snapGuideShapes.Add(line);
    }

    private void ClearAlignmentGuides()
    {
        // Perf — drop the rebuild-skip cache so the next UpdateAlignmentGuides
        // (a fresh gesture, or a return to a previously-visited anchor rect)
        // always rebuilds rather than short-circuiting against a stale rect whose
        // guides we just removed.
        _lastGuideAnchorRect = null;
        if (_snapGuideShapes.Count == 0) return;
        foreach (var s in _snapGuideShapes)
        {
            try { WidgetSurface.Children.Remove(s); }
            catch { /* surface torn down — best effort */ }
        }
        _snapGuideShapes.Clear();
    }

    private void ClearMultiSelect()
    {
        foreach (var w in _multiSelected)
        {
            if (_viewByWidget.TryGetValue(w, out var view)) view.IsSelected = false;
        }
        _multiSelected.Clear();
    }

    private void ClearMultiSelectAndAnchor()
    {
        ClearMultiSelect();
        if (_vm is not null)
        {
            _vm.SelectedWidget = null;
            PublishMultiSelection(_vm);
        }
    }

    /// <summary>
    /// Publish the current visually-selected widgets to the VM so the inspector
    /// can drive mixed-value / apply-to-all editing. Mirrors BuildView's
    /// IsSelected rule exactly (the multi-select set ∪ the primary anchor),
    /// ordered by layer Z so the inspector's roster and the published set agree.
    /// Idempotent on the VM side (sequence-equal = no-op), so it's safe to call
    /// liberally — every selection op funnels through here.
    /// </summary>
    private void PublishMultiSelection(VisualistViewModel vm)
    {
        if (vm.SelectedLayer is not Layer layer)
        {
            vm.SetSelectedWidgets(System.Array.Empty<LayerWidget>());
            return;
        }
        var set = new HashSet<LayerWidget>(_multiSelected);
        if (vm.SelectedWidget is { } anchor && layer.Widgets.Contains(anchor))
            set.Add(anchor);
        if (set.Count == 0)
        {
            vm.SetSelectedWidgets(System.Array.Empty<LayerWidget>());
            return;
        }
        vm.SetSelectedWidgets(layer.Widgets.Where(set.Contains).ToList());
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private static bool RectsIntersect(Rect a, Rect b)
        => a.X < b.X + b.Width
        && a.X + a.Width > b.X
        && a.Y < b.Y + b.Height
        && a.Y + a.Height > b.Y;

    // Visualist owns its own copy of the keyboard-state probe. Same helper
    // lives in WidgetGraphCanvas; not lifted to a shared utility.
    private static bool IsShiftDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    // Alt-held momentary snap-disable, matching the widget-graph canvas
    // (WidgetGraphCanvas.IsAltDown / ShouldSnap) and the Photoshop / Fusion
    // convention where the modifier SUPPRESSES the active grid/guide constraint
    // for the duration of the drag. VirtualKey.Menu == Alt in Win32-speak.
    private static bool IsAltDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private bool ShouldSnap() => SnapEnabled && !IsAltDown();

    // ─── Stage zoom / pan ────────────────────────────────────────────────
    //
    // Restores the pre-WinForms LayerCanvas zoom/pan the rework dropped, layered
    // on top of the aspect-locking Viewbox so the existing canvas-space pointer
    // math stays intact (GetPosition(WidgetSurface) already accounts for the
    // ancestor RenderTransform). Ctrl+wheel zooms about the cursor; middle-mouse
    // drag pans. State is view-local and reset whenever a fresh layer renders.
    // Min zoom is deliberately low (0.1) so the layer frame can shrink well
    // inside the stage and float in "pasteboard" space with the backdrop visible
    // all around it — DaVinci-style zoom-out so off-frame content and scaling are
    // visible while composing cut-outs. Fit (scale 1.0) fills the view; the user
    // zooms out from there.
    private const double StageMinZoom = 0.1;
    private const double StageMaxZoom = 8.0;
    private const double StageZoomStep = 1.15;   // per wheel notch

    private bool _isPanning;
    private Point _panStartPointer;       // pointer pos in StageHost space at grab
    private double _panStartX, _panStartY; // TranslateTransform values at grab

    // Last layer Render() painted, so we only reset zoom/pan on an actual
    // layer switch, not on the SelectedWidget-driven re-renders (which would
    // otherwise zap the user's zoom every time they clicked a widget).
    private Layer? _lastRenderedLayer;

    private void OnStagePointerWheel(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl+wheel only — a bare wheel scrolls any embedding scroll host and
        // shouldn't hijack into zoom (matches the common editor convention).
        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                    & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (!ctrl) return;
        if (StageScale is null || StagePan is null) return;

        int delta = e.GetCurrentPoint(StageHost).Properties.MouseWheelDelta;
        if (delta == 0) return;

        double oldZoom = StageScale.ScaleX;
        double factor = delta > 0 ? StageZoomStep : 1.0 / StageZoomStep;
        double newZoom = Math.Clamp(oldZoom * factor, StageMinZoom, StageMaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001) { e.Handled = true; return; }

        // Zoom about the cursor, computed via TransformToVisual so the maths are
        // exact regardless of the Viewbox's centring offset / uniform-fit scale:
        //   1. find the content point (WidgetSurface space) currently under the
        //      cursor, BEFORE changing the scale,
        //   2. apply the new scale,
        //   3. find where that same content point now sits in StageHost space,
        //   4. add the screen-space drift to the pan so the point lands back
        //      under the cursor.
        Point cursor = e.GetCurrentPoint(StageHost).Position;
        Point contentPt;
        try
        {
            contentPt = StageHost.TransformToVisual(WidgetSurface).TransformPoint(cursor);
        }
        catch
        {
            // TransformToVisual can throw if the tree isn't fully realised — fall
            // back to a plain centre-anchored zoom (no cursor tracking).
            StageScale.ScaleX = newZoom;
            StageScale.ScaleY = newZoom;
            UpdateZoomReadout();
            e.Handled = true;
            return;
        }

        StageScale.ScaleX = newZoom;
        StageScale.ScaleY = newZoom;

        try
        {
            Point afterPt = WidgetSurface.TransformToVisual(StageHost).TransformPoint(contentPt);
            StagePan.X += cursor.X - afterPt.X;
            StagePan.Y += cursor.Y - afterPt.Y;
        }
        catch { /* leave pan as-is on a transform failure */ }
        UpdateZoomReadout();
        e.Handled = true;
    }

    private void OnStagePanPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Middle button starts a pan. (Left is reserved for widget drag / lasso,
        // right for the context menu.)
        var pp = e.GetCurrentPoint(StageHost);
        if (!pp.Properties.IsMiddleButtonPressed) return;
        if (StagePan is null) return;

        _isPanning = true;
        _panStartPointer = pp.Position;
        _panStartX = StagePan.X;
        _panStartY = StagePan.Y;
        StageHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnStagePanPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning || StagePan is null) return;
        var pp = e.GetCurrentPoint(StageHost);

        // Stale-gesture guard: a pan only continues while the middle button is held.
        // A swallowed activation release would otherwise leave the stage panning with
        // the cursor and no button down. Settle like a release.
        if (!pp.Properties.IsMiddleButtonPressed)
        {
            _isPanning = false;
            try { StageHost.ReleasePointerCapture(e.Pointer); } catch { /* already lost */ }
            e.Handled = true;
            return;
        }

        Point cur = pp.Position;
        StagePan.X = _panStartX + (cur.X - _panStartPointer.X);
        StagePan.Y = _panStartY + (cur.Y - _panStartPointer.Y);
        e.Handled = true;
    }

    private void OnStagePanPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        StageHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnStagePanPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isPanning = false;
    }

    /// <summary>Reset zoom (1×) and pan (0,0) back to the Viewbox's
    /// default uniform fit. Called on every fresh layer render so switching
    /// layers doesn't strand the new one off-screen at an old zoom.</summary>
    private void ResetStageZoomPan()
    {
        if (StageScale is not null) { StageScale.ScaleX = 1.0; StageScale.ScaleY = 1.0; }
        if (StagePan   is not null) { StagePan.X = 0.0; StagePan.Y = 0.0; }
        _isPanning = false;
        UpdateZoomReadout();
    }

    // Fixed design-surface width — the WidgetSurface Canvas is a constant
    // 1920×1080 in XAML; the Viewbox uniform-fits it into the stage, so the
    // Viewbox's rendered width ÷ 1920 is the fit scale.
    private const double StageDesignWidth = 1920.0;

    /// <summary>Refresh the command-bar zoom readout. Shows "Fit" at the natural
    /// uniform-fit (StageScale == 1) and the effective on-screen percentage
    /// otherwise (StageScale × the Viewbox fit scale).</summary>
    private void UpdateZoomReadout()
    {
        if (ZoomReadout is null || StageScale is null) return;
        double s = StageScale.ScaleX;
        if (Math.Abs(s - 1.0) < 0.001) { ZoomReadout.Text = Localizer.T("visualist.canvas.zoom.fit", "Fit"); return; }
        double fit = StageViewbox is { ActualWidth: > 0 } ? StageViewbox.ActualWidth / StageDesignWidth : 1.0;
        ZoomReadout.Text = $"{Math.Round(s * fit * 100)}%";
    }

    /// <summary>Set the stage zoom, anchored about the stage-host centre, and
    /// refresh the readout. Clamped to [StageMinZoom, StageMaxZoom].</summary>
    private void SetStageZoom(double newZoom)
    {
        if (StageScale is null || StagePan is null) return;
        newZoom = Math.Clamp(newZoom, StageMinZoom, StageMaxZoom);
        double oldZoom = StageScale.ScaleX;
        if (Math.Abs(newZoom - oldZoom) < 0.0001) { UpdateZoomReadout(); return; }

        Point center = new(StageHost.ActualWidth / 2.0, StageHost.ActualHeight / 2.0);
        Point contentPt;
        try { contentPt = StageHost.TransformToVisual(WidgetSurface).TransformPoint(center); }
        catch
        {
            StageScale.ScaleX = StageScale.ScaleY = newZoom;
            UpdateZoomReadout();
            return;
        }
        StageScale.ScaleX = StageScale.ScaleY = newZoom;
        try
        {
            Point afterPt = WidgetSurface.TransformToVisual(StageHost).TransformPoint(contentPt);
            StagePan.X += center.X - afterPt.X;
            StagePan.Y += center.Y - afterPt.Y;
        }
        catch { /* leave pan as-is on a transform failure */ }
        UpdateZoomReadout();
    }

    private void OnZoomInCommand(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    { if (StageScale is not null) SetStageZoom(StageScale.ScaleX * StageZoomStep); }

    private void OnZoomOutCommand(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    { if (StageScale is not null) SetStageZoom(StageScale.ScaleX / StageZoomStep); }

    private void OnZoomFitCommand(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => ResetStageZoomPan();

    private void OnZoom100Command(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Actual size = 1 device px per layer px = invert the Viewbox fit scale.
        double fit = StageViewbox is { ActualWidth: > 0 } ? StageViewbox.ActualWidth / StageDesignWidth : 1.0;
        if (fit > 0) SetStageZoom(1.0 / fit);
    }

    private void OnGuidesToggle(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (GuideOverlay is null) return;
        GuideOverlay.Visibility = GuidesToggle?.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Empty-canvas authoring menu (Add Widget + presets) ──────────────
    // WinForms parity: LayerCanvas.BuildContextMenu offered Add Widget +
    // preset entries on a right-click of empty canvas (and was context-aware
    // about whether a widget was under the cursor). The WinUI rework kept only
    // the per-widget menu, leaving a fresh layer with no reachable create path.

    private void OnSurfaceRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // A right-tap on a widget is handled by that WidgetView's own
        // ContextFlyout (Duplicate / Front / Back / Delete). WidgetView.RightTapped
        // doesn't set Handled, so the event bubbles to the surface — bail when the
        // source sits inside a widget so we don't pop two menus at once.
        if (IsWithinWidgetView(e.OriginalSource)) return;

        TryFocus();
        // WidgetSurface's coordinate space IS the layer's world space (the
        // Viewbox scale is applied above it), so GetPosition(WidgetSurface)
        // yields the spawn point directly.
        Point worldPt = e.GetPosition(WidgetSurface);
        MenuFlyout flyout = BuildCanvasContextMenu(worldPt);
        // Anchor the flyout at the cursor relative to this UserControl (unscaled).
        flyout.ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition(this),
        });
        e.Handled = true;
    }

    private MenuFlyout BuildCanvasContextMenu(Point worldPt)
    {
        var flyout = new MenuFlyout();

        var add = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.canvas.context.add_widget", "Add Widget"),
        };
        add.Click += (_, _) => SpawnWidget(null, worldPt);
        flyout.Items.Add(add);

        var presets = new MenuFlyoutSubItem
        {
            Text = Localizer.T("visualist.canvas.context.add_from_preset", "Add Widget from Preset"),
        };
        foreach (WidgetPreset preset in Enum.GetValues<WidgetPreset>())
        {
            WidgetPreset captured = preset;
            // Labels come from the ONE shared helper (VisualistViewModel.cs's
            // WidgetPresetLabels) — this menu, the Inspector picker and the preset gallery used
            // to spell the same preset three different ways.
            var item = new MenuFlyoutItem { Text = WidgetPresetLabels.For(preset) };
            item.Click += (_, _) => SpawnWidget(captured, worldPt);
            presets.Items.Add(item);
        }
        flyout.Items.Add(presets);

        return flyout;
    }

    // Spawn a widget (optionally preset-seeded) at the cursor. Auto-creates an
    // untitled layer when the canvas is empty so the gesture works from a cold
    // open. Mirrors WinForms LayerCanvas.AddWidgetAtCenter, but anchored at the
    // click point rather than canvas centre.
    private void SpawnWidget(WidgetPreset? preset, Point worldPt)
    {
        if (_vm is null) return;
        try
        {
            if (_vm.Document is null) _vm.NewLayer();

            LayerWidget? w = preset is null
                ? _vm.AddWidget()
                : _vm.ApplyPreset(preset.Value);
            if (w is null) return;

            // The VM seeds a default 400×200 rect at (100,100); re-anchor it
            // under the cursor, clamped so the whole widget stays inside the
            // layer bounds.
            if (_vm.SelectedLayer is { } layer)
            {
                int maxX = Math.Max(0, layer.Resolution.Width  - w.Rect.Width);
                int maxY = Math.Max(0, layer.Resolution.Height - w.Rect.Height);
                w.Rect.X = (int)Math.Clamp(worldPt.X, 0, maxX);
                w.Rect.Y = (int)Math.Clamp(worldPt.Y, 0, maxY);
                _vm.Document?.MarkDirty();
                _vm.RaiseSelectedLayerChanged(); // re-render at the new position
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "SpawnWidget", ex);
        }
    }

    private static bool IsWithinWidgetView(object? originalSource)
    {
        DependencyObject? d = originalSource as DependencyObject;
        while (d is not null)
        {
            if (d is WidgetView) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // Instance (was static) so the "Edit Widget" item can reach the
    // enter-editor bubble (RequestEnter / WidgetEnterRequested). The pre-T15
    // shell let you reach the editor without a precise double-click (canvas
    // double-click OR widget-tree double-click); "Edit Widget" is the
    // discoverable right-click equivalent the WinUI rework left out.
    private MenuFlyout BuildWidgetContextMenu(VisualistViewModel vm, LayerWidget widget)
    {
        LayerWidget capture = widget;
        var flyout = new MenuFlyout();

        var edit = new MenuFlyoutItem { Text = Localizer.T("visualist.canvas.context.edit_widget", "Edit Widget") };
        edit.Click += (s, e) => RequestEnter(capture);
        flyout.Items.Add(edit);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var dup = new MenuFlyoutItem { Text = Localizer.T("common.context.duplicate", "Duplicate") };
        dup.Click += (s, e) =>
        {
            // Group-aware: when several widgets are multi-selected, duplicate the
            // whole set (canvas-level DuplicateSelectedWidgets) so the action
            // matches To-Front / To-Back / Delete which already honour the
            // multi-select. Pre-fix this only duplicated the right-clicked
            // widget. The VM has no plural duplicate, so the batch path lives on
            // the canvas (mirrors the in-file Paste deep-clone).
            if (_multiSelected.Count > 1) DuplicateSelectedWidgets();
            else { vm.SelectedWidget = capture; vm.DuplicateSelectedWidget(); }
        };
        flyout.Items.Add(dup);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Group-aware: when several widgets are multi-selected, To-Front /
        // To-Back restacks the whole set (preserving relative order); otherwise
        // it acts on the right-clicked widget.
        var front = new MenuFlyoutItem { Text = Localizer.T("visualist.canvas.context.bring_to_front", "Bring to Front") };
        front.Click += (s, e) =>
        {
            if (_multiSelected.Count > 1) vm.BringWidgetsToFront(_multiSelected.ToList());
            else { vm.SelectedWidget = capture; vm.BringSelectedToFront(); }
        };
        flyout.Items.Add(front);

        var back = new MenuFlyoutItem { Text = Localizer.T("visualist.canvas.context.send_to_back", "Send to Back") };
        back.Click += (s, e) =>
        {
            if (_multiSelected.Count > 1) vm.SendWidgetsToBack(_multiSelected.ToList());
            else { vm.SelectedWidget = capture; vm.SendSelectedToBack(); }
        };
        flyout.Items.Add(back);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Mirror the baseline ContextMenuStrip grouping. The
        // pre-T15 LayerCanvas laid the menu out as three separator-delimited
        // groups (action → z-order → destructive Delete in Theme.StatusRed).
        // The WinUI flyout already matched the
        // 3-group / 2-separator structure; this restores the destructive-
        // command emphasis the baseline carried by tinting Delete with the
        // ErrBrush token (theme-resolved, literal-ARGB fallback for
        // designer / pre-app) instead of leaving it body-coloured.
        var del = new MenuFlyoutItem
        {
            Text = Localizer.T("common.context.delete", "Delete"),
            Foreground = ResolveBrushTinted("ErrBrush", 0xFF, 0xC9, 0x53, 0x3C),
        };
        del.Click += (s, e) =>
        {
            // Group-aware, matching To-Front / To-Back / Duplicate above: when
            // several widgets are multi-selected, delete the whole set in one
            // undo entry (canvas-level DeleteSelectedWidgets); otherwise delete
            // just the right-clicked widget.
            if (_multiSelected.Count > 1) DeleteSelectedWidgets();
            else { vm.SelectedWidget = capture; vm.DeleteSelectedWidget(); }
        };
        flyout.Items.Add(del);

        return flyout;
    }

    private static WidgetThumbKind ResolveThumbKind(WidgetPreset? preset) => preset switch
    {
        WidgetPreset.Image     => WidgetThumbKind.Gradient,
        WidgetPreset.Video     => WidgetThumbKind.Gradient,
        WidgetPreset.Particles => WidgetThumbKind.Gradient,
        WidgetPreset.WebSource => WidgetThumbKind.Gradient,
        WidgetPreset.Text      => WidgetThumbKind.Text,
        WidgetPreset.Chat      => WidgetThumbKind.Text,
        WidgetPreset.CC        => WidgetThumbKind.Text,
        WidgetPreset.Audio     => WidgetThumbKind.Label,
        // V8 — Gradient, not the default Label. An Alert Box is idle-blank by design
        // (its onStartup is a bare Display sink), so on the layer canvas it would otherwise
        // read as an empty box with a word in it; the gradient thumb is the convention for
        // "this widget paints an image at runtime", which is what it does.
        WidgetPreset.AlertBox  => WidgetThumbKind.Gradient,
        // V15 — Gradient for the same reason AlertBox is. A Player widget's canvas
        // content is permanently nothing: the iframe lives on the DOM-overlay track and
        // is drawn by the browser in OBS, never on this design surface, so a Label thumb
        // would read as a broken empty box. Gradient is the established "this paints at
        // runtime" thumb.
        WidgetPreset.Player    => WidgetThumbKind.Gradient,
        _                      => WidgetThumbKind.Label,
    };

    // ─── Cut / Copy / Paste / Select-All / Delete keyboard shortcuts ─────
    // Operates on the multi-select set
    // (_multiSelected). The clipboard buffer is per-app-process — Visualist
    // owns its own copy per the per-pillar paint isolation rule; not lifted to
    // Phoenix.Controls.Shared.

    private void OnKeyDownForClipboardShortcuts(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null) return;

        // When an inline value-pill or rename TextBox owns focus, none of the
        // canvas shortcuts may fire — the focused editor owns its keys (Ctrl+Z
        // must undo the TEXT, Delete must not destroy the widget, arrows move the
        // caret). Mirrors WidgetGraphCanvas.OnHostKeyDown's guard; without it a
        // Ctrl+Z while renaming a widget hijacked the document undo stack.
        if (IsTextInputFocused()) return;

        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                     & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        // Undo / Redo — keyed on the layout-mapped VirtualKey (the LABEL on the
        // keytop), NOT the physical scancode. The old scancode anchor (0x2C/0x15)
        // tracked the PHYSICAL key position, which on a German QWERTZ keyboard has
        // the Y/Z keytops INVERTED from QWERTY — so Strg+Z hit the redo scancode
        // and undo never fired (Majo's "Ctrl+Z doesn't work"). VirtualKey.Z
        // resolves to whatever key produces 'Z' on the active layout, so the
        // visible Z keytop always undoes. Matches WidgetGraphCanvas.OnHostKeyDown
        // so both canvases behave identically on every layout.
        if (ctrl)
        {
            if (e.Key == Windows.System.VirtualKey.Z)
            {
                var mv = FindPillarMainView();
                if (mv is not null)
                {
                    try { if (IsShiftDown()) mv.Redo(); else mv.Undo(); }
                    catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "Save/Undo chord", ex); }
                    e.Handled = true;
                }
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Y)
            {
                var mv = FindPillarMainView();
                if (mv is not null)
                {
                    try { mv.Redo(); }
                    catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "Save/Undo chord", ex); }
                    e.Handled = true;
                }
                return;
            }
            uint sc = e.KeyStatus.ScanCode;
            // Ctrl+] / Ctrl+[ step the selection's z-order; Shift makes it
            // To-Front / To-Back (group-aware when several widgets are selected).
            if (sc == ScanCode_BracketRight || sc == ScanCode_BracketLeft)
            {
                int dir = sc == ScanCode_BracketRight ? +1 : -1;
                bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                              & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                ApplyZOrderChord(dir, toExtreme: shift);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                // Esc clears the widget selection at canvas scope.
                if (_multiSelected.Count > 0 || _vm.SelectedWidget is not null)
                {
                    ClearMultiSelectAndAnchor();
                    _vm.SelectedWidget = null;
                    e.Handled = true;
                }
                break;
            case Windows.System.VirtualKey.Delete:
                if (DeleteSelectedWidgets()) e.Handled = true;
                break;
            // ── Arrow-key nudging ─────────────────────────────────────────
            // Move the selection by 1px (or SnapGridStepPx with Shift held,
            // mirroring Architect's LogicCanvasView.Keyboard.cs shift?10:1 step
            // pattern, here aligned to the canvas's own 8px grid). Group-aware:
            // nudges the whole multi-select when 2+ are selected, else the
            // primary SelectedWidget. Ctrl is reserved for chords (Z-order /
            // undo) and Alt is the snap-suppress modifier, so both are excluded
            // — bare/Shift arrows only.
            case Windows.System.VirtualKey.Left   when !ctrl && !IsAltDown():
                if (NudgeSelection(IsShiftDown() ? -SnapGridStepPx : -1, 0)) e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right  when !ctrl && !IsAltDown():
                if (NudgeSelection(IsShiftDown() ?  SnapGridStepPx :  1, 0)) e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up     when !ctrl && !IsAltDown():
                if (NudgeSelection(0, IsShiftDown() ? -SnapGridStepPx : -1)) e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down   when !ctrl && !IsAltDown():
                if (NudgeSelection(0, IsShiftDown() ?  SnapGridStepPx :  1)) e.Handled = true;
                break;
            case Windows.System.VirtualKey.Enter when !ctrl:
                // Enter on the current selection enters its editor (parity
                // gesture alongside body double-tap + "Edit Widget" menu). Inline
                // rename / pill TextBoxes consume Enter with e.Handled=true, so
                // this only fires when the canvas itself owns focus.
                if (_vm.SelectedWidget is { } toEnter
                    && _vm.SelectedLayer?.Widgets.Contains(toEnter) == true)
                {
                    RequestEnter(toEnter);
                    e.Handled = true;
                }
                break;
            case Windows.System.VirtualKey.A when ctrl:
                SelectAllWidgets();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.C when ctrl:
                CopySelectedWidgets();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.X when ctrl:
                CutSelectedWidgets();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.V when ctrl:
                PasteWidgetsFromClipboard();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.S when ctrl:
            {
                // S is in the same row across QWERTY/QWERTZ/AZERTY — no
                // scancode anchoring needed. Shift toggles Save → Save As.
                bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                              & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                var mv = FindPillarMainView();
                if (mv is not null)
                {
                    try { if (shift) mv.SaveLayerAs(); else mv.SaveLayer(); }
                    catch (Exception ex) { GlobalLogger.Error("LayerCanvasView", "Save/Undo chord", ex); }
                    e.Handled = true;
                }
                break;
            }
        }
    }

    // True when a TextBox / NumberBox owns focus anywhere in the tree — the
    // guard that lets a focused inline editor keep its own keys (text undo,
    // caret arrows, Delete) instead of the canvas hijacking them. Mirrors
    // WidgetGraphCanvas.IsTextInputFocused so both canvases behave the same.
    private bool IsTextInputFocused()
    {
        try
        {
            DependencyObject? fe = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (fe is not null)
            {
                if (fe is TextBox || fe is NumberBox) return true;
                fe = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(fe);
            }
        }
        catch { /* XamlRoot not ready — treat as not-focused */ }
        return false;
    }

    // Apply a z-order chord. <paramref name="dir"/> +1 = toward front,
    // -1 = toward back. <paramref name="toExtreme"/> brings/sends all the way
    // (group-aware when 2+ widgets are multi-selected); otherwise single-step.
    private void ApplyZOrderChord(int dir, bool toExtreme)
    {
        if (_vm is null) return;
        var group = _multiSelected.Count > 1 ? _multiSelected.ToList() : null;
        if (toExtreme)
        {
            if (group is not null)
            {
                if (dir > 0) _vm.BringWidgetsToFront(group);
                else         _vm.SendWidgetsToBack(group);
            }
            else if (dir > 0) _vm.BringSelectedToFront();
            else              _vm.SendSelectedToBack();
        }
        else
        {
            // Single-step nudges the anchor widget (the most-recently selected).
            _vm.NudgeSelectedZOrder(dir);
        }
    }

    /// <summary>
    /// Nudge the current selection by (dx, dy) canvas pixels in a single
    /// undo entry. Group-aware (every multi-selected widget moves; otherwise the
    /// primary SelectedWidget). Each widget is clamped independently to the
    /// canvas bounds — unlike a group DRAG, a keyboard nudge has no shared
    /// formation to preserve, so a widget already flush against an edge simply
    /// stops while the rest still move. Pushes the canvas / live-preview update
    /// the same way a finished drag does. Returns true when something moved.
    /// </summary>
    private bool NudgeSelection(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return false;
        if (_vm?.Document is not LayerDocument doc) return false;
        var picked = ResolvePickedWidgets();
        if (picked.Count == 0) return false;

        int canvasW = (int)WidgetSurface.Width;
        int canvasH = (int)WidgetSurface.Height;

        // Compute clamped positions first so a no-op (everything already against
        // the edge it's nudging into) doesn't push a wasted undo entry.
        var moves = new List<(LayerWidget W, int X, int Y)>(picked.Count);
        bool anyChanged = false;
        foreach (var w in picked)
        {
            int maxX = Math.Max(0, canvasW - w.Rect.Width);
            int maxY = Math.Max(0, canvasH - w.Rect.Height);
            int nx = Math.Clamp(w.Rect.X + dx, 0, maxX);
            int ny = Math.Clamp(w.Rect.Y + dy, 0, maxY);
            if (nx != w.Rect.X || ny != w.Rect.Y) anyChanged = true;
            moves.Add((w, nx, ny));
        }
        if (!anyChanged) return false;

        doc.PushUndo();
        foreach (var (w, nx, ny) in moves)
        {
            w.Rect.X = nx;
            w.Rect.Y = ny;
            if (_viewByWidget.TryGetValue(w, out var view))
            {
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(view, nx);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(view, ny);
            }
            // Keep the live WebView2 frame in step (no-op when the
            // previewer isn't running), matching the finished-drag path.
            _previewer?.PostWidgetUpdate(w);
            PublishLiveWidgetEdit(w);
        }
        _previewer?.CaptureAfterCommit();
        doc.MarkDirty();
        // Re-anchor the resize handle over the (possibly moved) primary without a
        // full rebuild — RaiseSelectedLayerChanged would re-render the whole
        // surface every keystroke, which fights smooth held-arrow repeat.
        if (_vm is { } vm) UpdateResizeHandlePosition(vm.SelectedWidget ?? moves[^1].W);
        return true;
    }

    // Walk the visual tree up to the embedding MainView. The chrome-level
    // KeyboardAccelerators fire when the chrome
    // window has focus; this handler covers the case where the canvas itself
    // owns focus (e.g. after a click on the layer surface). Keep both paths.
    private Phoenix.Controls.Visualist.WinUI.MainView? FindPillarMainView()
    {
        DependencyObject? cur = this;
        while (cur is not null)
        {
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
            if (cur is Phoenix.Controls.Visualist.WinUI.MainView mv) return mv;
        }
        return null;
    }

    // Select the widget and ask the shell to open its editor. Selection is
    // set here too (not only in MainView.EnterWidget) so the VM reflects the
    // intent even on a host that doesn't subscribe; MainView.EnterWidget re-sets
    // it idempotently. Single funnel for all three enter gestures (body
    // double-tap, Enter key, "Edit Widget" menu).
    private void RequestEnter(LayerWidget widget)
    {
        if (widget is null || _vm is null) return;
        _vm.SelectedWidget = widget;
        WidgetEnterRequested?.Invoke(this, widget);
    }

    private void SelectAllWidgets()
    {
        if (_vm?.SelectedLayer is not Layer layer) return;
        if (layer.Widgets.Count == 0) return;

        // Replace the multi-select set with every widget in the layer; flip
        // each WidgetView's IsSelected so the existing rebuild contract
        // doesn't fight us. No undo entry — selection state isn't part of
        // the undo history.
        _multiSelected.Clear();
        foreach (var w in layer.Widgets)
        {
            _multiSelected.Add(w);
            if (_viewByWidget.TryGetValue(w, out var view)) view.IsSelected = true;
        }
        _vm.SelectedWidget = layer.Widgets[^1]; // last as anchor (matches lasso convention)
        PublishMultiSelection(_vm);
        RecomputeHotkeyContext();
    }

    /// <summary>
    /// Snapshot the current multi-selection into <see cref="WidgetClipboard"/>.
    /// Round-trips through LayerSerializer so every converter (Color,
    /// KeyframeCurve, …) replays identically on Paste — same shape
    /// VisualistViewModel.DuplicateSelectedWidget already uses for in-process
    /// deep clone. Returns true when the buffer was populated.
    /// </summary>
    private bool CopySelectedWidgets()
    {
        var picked = ResolvePickedWidgets();
        if (picked.Count == 0) return false;
        try
        {
            var stub = new Layer
            {
                Name       = "_clip_stub",
                Resolution = new LayerResolution { Width = 1, Height = 1 },
            };
            foreach (var w in picked) stub.Widgets.Add(w);
            WidgetClipboard.Json = LayerSerializer.Serialize(stub);
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "CopySelectedWidgets", ex);
            return false;
        }
    }

    /// <summary>
    /// Cut = Copy + Delete in a SINGLE undo entry — the copy doesn't push undo
    /// itself, the delete owns the snapshot.
    /// </summary>
    private void CutSelectedWidgets()
    {
        if (!CopySelectedWidgets()) return;
        DeleteSelectedWidgets();
    }

    /// <summary>
    /// Deserialize the WidgetClipboard buffer into fresh widgets, regenerate
    /// Ids, offset by +20px, append to the layer in a single undo entry, and
    /// promote the new widgets to the multi-selection.
    /// </summary>
    private void PasteWidgetsFromClipboard()
    {
        if (_vm?.Document is not LayerDocument doc) return;
        if (string.IsNullOrEmpty(WidgetClipboard.Json)) return;
        try
        {
            Layer round = LayerSerializer.Deserialize(WidgetClipboard.Json!);
            if (round.Widgets.Count == 0) return;

            doc.PushUndo();

            ClearMultiSelect();

            int zBase = doc.Layer.Widgets.Count == 0
                ? 0
                : doc.Layer.Widgets.Max(w => w.ZIndex);

            var pasted = new List<LayerWidget>(round.Widgets.Count);
            foreach (LayerWidget clone in round.Widgets)
            {
                clone.Id = Guid.NewGuid().ToString();
                clone.Rect.X += 20;
                clone.Rect.Y += 20;
                // Keep the relative Z order; nudge above the existing stack
                // so the pasted set stays visible on top.
                clone.ZIndex = ++zBase;
                doc.Layer.Widgets.Add(clone);
                pasted.Add(clone);
            }

            doc.MarkDirty();
            _vm.RaiseSelectedLayerChanged(); // forces canvas re-render

            // After a paste the newly-pasted items are the multi-selection.
            // Re-render rebuilt _viewByWidget, so this stays in sync.
            _multiSelected.Clear();
            foreach (var w in pasted)
            {
                _multiSelected.Add(w);
                if (_viewByWidget.TryGetValue(w, out var view)) view.IsSelected = true;
            }
            _vm.SelectedWidget = pasted[^1];
            PublishMultiSelection(_vm);
            RecomputeHotkeyContext();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "PasteWidgetsFromClipboard", ex);
        }
    }

    /// <summary>
    /// Delete the current multi-selection (or the VM's SelectedWidget when no
    /// multi-set is active) in a single undo entry. Returns true when at
    /// least one widget was removed so the keyboard handler can mark the key
    /// handled.
    /// </summary>
    private bool DeleteSelectedWidgets()
    {
        if (_vm?.Document is not LayerDocument doc) return false;
        var picked = ResolvePickedWidgets();
        if (picked.Count == 0) return false;

        // Single undo entry for the whole batch — Ctrl+Z restores everything.
        doc.PushUndo();
        foreach (var w in picked)
            doc.Layer.Widgets.Remove(w);
        doc.MarkDirty();

        _multiSelected.Clear();
        _vm.SelectedWidget = doc.Layer.Widgets.FirstOrDefault();
        _vm.RaiseSelectedLayerChanged();
        RecomputeHotkeyContext();
        return true;
    }

    /// <summary>
    /// Duplicate the current multi-selection in a single undo entry. Deep-clones
    /// each picked widget through LayerSerializer (the same round-trip
    /// VisualistViewModel.DuplicateSelectedWidget / the in-file Paste path use so
    /// every converter replays identically), regenerates Ids, offsets +20px,
    /// stacks above the existing z-order, and promotes the clones to the new
    /// multi-selection. Canvas-level because the VM exposes no plural duplicate.
    /// Returns true when at least one widget was duplicated.
    /// </summary>
    private bool DuplicateSelectedWidgets()
    {
        if (_vm?.Document is not LayerDocument doc) return false;
        var picked = ResolvePickedWidgets();
        if (picked.Count == 0) return false;
        try
        {
            // Round-trip the picked set through a throwaway stub layer so the
            // clones are fully independent of the originals (deep copy of nested
            // Triggers / Graph / Timeline).
            var stub = new Layer
            {
                Name       = "_dup_stub",
                Resolution = new LayerResolution { Width = 1, Height = 1 },
            };
            foreach (var w in picked) stub.Widgets.Add(w);
            Layer round = LayerSerializer.Deserialize(LayerSerializer.Serialize(stub));
            if (round.Widgets.Count == 0) return false;

            doc.PushUndo();
            ClearMultiSelect();

            int zBase = doc.Layer.Widgets.Count == 0
                ? 0
                : doc.Layer.Widgets.Max(w => w.ZIndex);

            var clones = new List<LayerWidget>(round.Widgets.Count);
            foreach (LayerWidget clone in round.Widgets)
            {
                clone.Id = Guid.NewGuid().ToString();
                clone.Name = string.IsNullOrEmpty(clone.Name) ? "(copy)" : $"{clone.Name} (copy)";
                clone.Rect.X += 20;
                clone.Rect.Y += 20;
                clone.ZIndex = ++zBase;   // stack above the existing set, keep relative order
                doc.Layer.Widgets.Add(clone);
                clones.Add(clone);
            }

            doc.MarkDirty();
            _vm.RaiseSelectedLayerChanged(); // forces canvas re-render → rebuilds _viewByWidget

            // Promote the clones to the multi-selection (re-render already rebuilt
            // the views, so IsSelected sticks).
            _multiSelected.Clear();
            foreach (var w in clones)
            {
                _multiSelected.Add(w);
                if (_viewByWidget.TryGetValue(w, out var view)) view.IsSelected = true;
            }
            _vm.SelectedWidget = clones[^1];
            PublishMultiSelection(_vm);
            RecomputeHotkeyContext();
            return true;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerCanvasView", "DuplicateSelectedWidgets", ex);
            return false;
        }
    }

    /// <summary>
    /// The set of widgets a Ctrl+C / Ctrl+X / Del / Paste should act on.
    /// Prefers the multi-select set; falls back to the VM's primary
    /// SelectedWidget so a single-click selection still cuts/copies/deletes.
    /// </summary>
    private List<LayerWidget> ResolvePickedWidgets()
    {
        if (_vm?.SelectedLayer is not Layer layer) return new();
        if (_multiSelected.Count > 0)
        {
            // Preserve layer Z-order on copy so a paste round-trip doesn't
            // shuffle the stack.
            return layer.Widgets.Where(_multiSelected.Contains).ToList();
        }
        if (_vm.SelectedWidget is { } w && layer.Widgets.Contains(w))
            return new List<LayerWidget> { w };
        return new();
    }
}

/// <summary>
/// Per-app-process clipboard buffer for cut/copy/paste of widgets in
/// LayerCanvasView. Visualist owns its own buffer — Architect has its
/// own LogicCanvasView clipboard, not shared.
/// </summary>
internal static class WidgetClipboard
{
    /// <summary>JSON-serialized stub <see cref="Layer"/> carrying the widgets.</summary>
    public static string? Json;
}
