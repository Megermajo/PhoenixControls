using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Controls;
using Windows.Graphics;
using Windows.System;

// NOTE on namespace: the files under the Windows/ folder live in the
// ...WinUI.Hosting namespace, NOT ...WinUI.Windows. That is deliberate — a
// "Windows" namespace nested under the assembly root shadows the global
// `Windows.*` WinRT namespace for every fully-qualified reference in the
// assembly (Windows.Storage / Windows.Graphics / Windows.Foundation), which
// breaks compilation across unrelated files. VisualistSiblingWindow /
// VisualistWindowRegistry already follow this convention; LayerPreviewWindow
// matches it.
namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// LayerPreviewWindow — top-level popout that hosts a <see cref="LayerPreviewPanel"/>
/// (full layer) or a <see cref="WidgetSinglePreviewPanel"/> (one widget filling
/// the canvas, via compositor.js's <c>?widget=</c> param).
///
/// <para>WinUI re-architecture of the pre-T15 WinForms
/// <c>Phoenix.Controls.Visualist/Forms/LayerPreviewWindow.cs</c>. The two-mode
/// constructor and the layer-aspect default sizing are preserved; the platform
/// changes are the WinForms <c>Form</c> → WinUI <see cref="Window"/> + AppWindow
/// sizing and the <c>Shown</c> event → <c>Activated</c>-once load.</para>
///
/// <para>One window per editor is the caller's responsibility (the editor caches
/// the open instance and re-activates it on re-summon). Closing disposes the
/// hosted WebView2 via the panel's Shutdown(). Esc closes the window.</para>
/// </summary>
public sealed partial class LayerPreviewWindow : Window
{
    public LayerPreviewPanel? Preview { get; }
    public WidgetSinglePreviewPanel? WidgetPreview { get; }

    private readonly string? _layerId;
    private readonly string? _widgetId;
    private bool _loaded;

    /// <summary>Full-layer popout (legacy ctor parity).</summary>
    public LayerPreviewWindow(string title, string? layerId, int layerWidth, int layerHeight)
        : this(title, layerId, widgetId: null, layerWidth, layerHeight)
    {
    }

    /// <summary>
    /// Two-mode constructor. <paramref name="widgetId"/> = null builds a
    /// full-layer popout; a non-null id targets one widget filling the canvas.
    /// </summary>
    public LayerPreviewWindow(string title, string? layerId, string? widgetId, int layerWidth, int layerHeight)
    {
        InitializeComponent();

        _layerId  = layerId;
        _widgetId = widgetId;
        bool isWidgetMode = !string.IsNullOrEmpty(widgetId);

        Title = isWidgetMode ? $"Preview — {title} (widget)" : $"Preview — {title}";

        if (isWidgetMode)
        {
            WidgetPreview = new WidgetSinglePreviewPanel();
            PreviewHost.Children.Add(WidgetPreview);
            if (layerHeight > 0)
                WidgetPreview.SetWidgetAspect(Math.Max(1, layerWidth) / (double)Math.Max(1, layerHeight));
        }
        else
        {
            Preview = new LayerPreviewPanel();
            PreviewHost.Children.Add(Preview);
        }

        SizeToLayer(layerWidth, layerHeight);

        // Esc closes — the popout is "opened to glance, dismissed quickly".
        if (Content is FrameworkElement root)
        {
            root.KeyDown += (s, e) =>
            {
                if (e.Key == VirtualKey.Escape) { try { Close(); } catch { } e.Handled = true; }
            };
        }

        Activated += OnActivatedOnce;
        Closed    += OnClosed;
    }

    private void SizeToLayer(int layerWidth, int layerHeight)
    {
        try
        {
            int safeW = Math.Max(1, layerWidth);
            int safeH = Math.Max(1, layerHeight);
            // Default ~50% of the layer's native resolution, clamped, with the
            // layer aspect driving the height so a vertical layer pops portrait
            // and an ultrawide pops landscape.
            int defaultW = Math.Clamp(safeW / 2, 480, 1280);
            int defaultH = Math.Max(240, (int)Math.Round(defaultW * (safeH / (double)safeW)));

            var hwnd     = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow?.Resize(new SizeInt32(defaultW, defaultH));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewWindow", "SizeToLayer", ex);
        }
    }

    private async void OnActivatedOnce(object sender, WindowActivatedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        Activated -= OnActivatedOnce;
        try
        {
            if (WidgetPreview is not null) await WidgetPreview.LoadAsync(_layerId, _widgetId);
            else if (Preview is not null)  await Preview.LoadLayerAsync(_layerId);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LayerPreviewWindow", "initial load", ex);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try { Preview?.Shutdown(); } catch { }
        try { WidgetPreview?.Shutdown(); } catch { }
    }

    /// <summary>Reload the WebView2 source — useful after a Save changes the
    /// layer file on disk.</summary>
    public Task RefreshAsync(string? layerId)
    {
        if (Preview is not null) return Preview.LoadLayerAsync(layerId);
        WidgetPreview?.Refresh();
        return Task.CompletedTask;
    }
}
