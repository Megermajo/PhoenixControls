using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

/// <summary>
/// Sprint F — read-only viewer for .phx (generated runtime script) files.
///
/// Activated by <c>App.OnLaunchedCore</c> when the installer-registered
/// <c>.phx</c> file association double-click fires Hub.WinUI with
/// <c>--open &lt;path&gt;</c>. The window is intentionally NOT a
/// ContentDialog — Explorer double-click flows expect a standalone window,
/// and a ContentDialog requires a XamlRoot host that doesn't exist yet at
/// the point we'd want to show it (MainWindow may not have been activated).
///
/// Hard rule (the project docs): the user MUST NOT be able to type into the body.
/// IsReadOnly="True" on the TextBox guarantees this — Ctrl+V / Ctrl+X /
/// keypress are all suppressed by the WinUI TextBox in read-only mode.
/// Copy-to-clipboard is allowed because reviewing the generated script and
/// pasting it into a chat for help is a normal workflow.
/// </summary>
public sealed partial class ReadOnlyPhxWindow : Window
{
    // ~900 × 700 DIPs per sprint brief. AppWindow.Resize wants physical
    // pixels — at 100% scale that's a direct map; at higher scales WinUI
    // handles DIP→physical conversion via the AppWindow API itself.
    private const int InitialWidthDips  = 900;
    private const int InitialHeightDips = 700;

    private readonly string _phxPath;
    private readonly string? _siblingGraphPath;
    private readonly Action<string>? _openInArchitectCallback;

    /// <summary>
    /// Construct the viewer for a specific <c>.phx</c> path.
    /// </summary>
    /// <param name="phxPath">
    /// Absolute path to the .phx file the user double-clicked. Loaded
    /// synchronously in the constructor; load failures surface as inline
    /// text in the body region (not a modal — per
    /// <c>feedback_no_modal_dialogs_for_repeatable_rejections.md</c>).
    /// </param>
    /// <param name="openInArchitectCallback">
    /// Optional callback invoked when the user clicks "Open graph in
    /// Architect" — receives the sibling <c>.phxg</c> path. The viewer
    /// itself can't reach <c>MainWindow.NavigateTo</c> directly without
    /// dragging in a navigator reference; the App layer passes a
    /// pre-bound delegate that routes to the existing pillar-launch flow.
    /// If null, the button is hidden even when a sibling graph exists.
    /// </param>
    public ReadOnlyPhxWindow(string phxPath, Action<string>? openInArchitectCallback = null)
    {
        InitializeComponent();

        _phxPath = phxPath ?? string.Empty;
        _openInArchitectCallback = openInArchitectCallback;
        _siblingGraphPath = ResolveSiblingGraphPath(_phxPath);

        Title = string.IsNullOrEmpty(_phxPath)
            ? Localizer.T("dialog.readonly_phx.window.title", "Phoenix Controls — Script Viewer")
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localizer.T("dialog.readonly_phx.window.title_format",
                    "Phoenix Controls — {0} (read-only)"),
                Path.GetFileName(_phxPath));

        ConfigurePresenter();
        PopulateChrome();
        // Body loading happens off the constructor — the root
        // Grid's Loaded event drives LoadBodyAsync so we can await
        // File.ReadAllTextAsync without blocking the UI thread on large
        // .phx files. Until the load completes, BodyText shows a brief
        // placeholder set below.
        BodyText.Text = string.Empty;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // async void is acceptable for a Loaded handler — exceptions are
        // already caught inside LoadBodyAsync and routed to GlobalLogger.
        await LoadBodyAsync();
    }

    private void ConfigurePresenter()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // Sizing in DIPs is fine on AppWindow.Resize for a single-monitor
            // session — physical-pixel scaling is handled by the window-
            // manager when the initial size is set pre-show. Splash uses an
            // explicit DPI multiply because its visual relies on
            // pixel-perfect alignment; the viewer is a text body in a
            // resizable window where the user can drag the edge regardless.
            appWindow.Resize(new SizeInt32(InitialWidthDips, InitialHeightDips));

            // Standard chrome — keep min/max/close + drag. Unlike the
            // splash this window is part of a normal user session.
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, true);
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
            }
        }
        catch (Exception ex)
        {
            // Window still opens at the system default size; non-fatal.
            GlobalLogger.Error("ReadOnlyPhxWindow", "ConfigurePresenter failed", ex);
        }
    }

    private void PopulateChrome()
    {
        FilePathText.Text = string.IsNullOrEmpty(_phxPath)
            ? Localizer.T("dialog.readonly_phx.path.none", "(no path provided)")
            : _phxPath;

        if (!string.IsNullOrEmpty(_siblingGraphPath))
        {
            SiblingGraphPath.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localizer.T("dialog.readonly_phx.sibling_graph_format", "Sibling graph: {0}"),
                _siblingGraphPath);
            SiblingGraphPath.Visibility = Visibility.Visible;

            // Button visibility is gated on BOTH a sibling graph file and a
            // callback being available — if App didn't supply the
            // Architect-routing delegate, hiding the button is friendlier
            // than rendering a dead control.
            if (_openInArchitectCallback is not null)
            {
                OpenGraphButton.Visibility = Visibility.Visible;
            }
        }
    }

    private async Task LoadBodyAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(_phxPath))
            {
                BodyText.Text = Localizer.T("dialog.readonly_phx.body.no_path",
                    "(no path provided to viewer)");
                return;
            }

            if (!File.Exists(_phxPath))
            {
                BodyText.Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localizer.T("dialog.readonly_phx.body.not_found_format",
                        "File not found:\n{0}"),
                    _phxPath);
                GlobalLogger.Log(
                    $"ReadOnlyPhxWindow: file not found — {_phxPath}",
                    "ReadOnlyPhxWindow", Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }

            // ReadAllTextAsync keeps the UI thread responsive
            // on large .phx files. UTF-8 with BOM detection matches what
            // ScriptExporter writes; the async overload preserves that
            // behaviour.
            string contents = await File.ReadAllTextAsync(_phxPath, Encoding.UTF8).ConfigureAwait(true);
            BodyText.Text = contents;
        }
        catch (Exception ex)
        {
            // Per feedback_no_modal_dialogs_for_repeatable_rejections.md —
            // a read fault surfaces inline and via GlobalLogger, never as
            // a separate dialog. The user already opened a viewer; popping
            // a second window for the failure would be hostile UX.
            BodyText.Text = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localizer.T("dialog.readonly_phx.body.read_failed_format",
                    "Failed to read file:\n{0}\n\n{1}: {2}"),
                _phxPath, ex.GetType().Name, ex.Message);
            GlobalLogger.Error("ReadOnlyPhxWindow", $"read failed — {_phxPath}", ex);
        }
    }

    private static string? ResolveSiblingGraphPath(string phxPath)
    {
        if (string.IsNullOrEmpty(phxPath)) return null;
        try
        {
            string? dir = Path.GetDirectoryName(phxPath);
            string stem = Path.GetFileNameWithoutExtension(phxPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem)) return null;
            string candidate = Path.Combine(dir, stem + ".phxg");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ReadOnlyPhxWindow",
                $"ResolveSiblingGraphPath('{phxPath}') failed", ex);
            return null;
        }
    }

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.RequestedOperation = DataPackageOperation.Copy;
            package.SetText(BodyText.Text ?? string.Empty);
            Clipboard.SetContent(package);
            // Flush so the data lands on the system clipboard before the
            // window can be closed — without Flush a fast Close immediately
            // after Copy can drop the content (the WinRT clipboard is
            // owned by the source process until Flush hands it to the OS).
            Clipboard.Flush();
            CopyFeedbackText.Text = Localizer.T("dialog.readonly_phx.copy.ok", "Copied.");
        }
        catch (Exception ex)
        {
            // Non-fatal — user can re-try, or select text manually.
            CopyFeedbackText.Text = Localizer.T("dialog.readonly_phx.copy.failed", "Copy failed.");
            GlobalLogger.Error("ReadOnlyPhxWindow", "OnCopyClicked failed", ex);
        }
    }

    private void OnOpenGraphClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(_siblingGraphPath) || _openInArchitectCallback is null) return;
            _openInArchitectCallback(_siblingGraphPath);
            // Close after routing so the streamer lands on the Architect
            // tab without an orphan viewer behind it. The graph viewer is
            // a transient surface — once the user has chosen to author,
            // the read-only view has served its purpose.
            Close();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ReadOnlyPhxWindow",
                $"OnOpenGraphClicked('{_siblingGraphPath}') failed", ex);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); }
        catch (Exception ex) { GlobalLogger.Error("ReadOnlyPhxWindow", "OnCloseClicked failed", ex); }
    }
}
