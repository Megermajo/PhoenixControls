using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace Phoenix.Controls.Hub.WinUI.Dialogs;

// Sprint D — in-app Documentation window. Replaces the pre-existing Tools →
// Documentation handler that did a Process.Start of the GitHub README. The
// window renders HubFeatureRegistry as a three-pane catalogue (categories →
// features → details) so streamers can browse the suite's user-facing surface
// without leaving Hub for a browser tab.
//
// Lifecycle: singleton-ish per the NodeDocumentationWindow pattern. A second
// Tools → Documentation click activates the existing window rather than
// spawning a duplicate; a Closed handler clears the static so the next open
// gets a fresh window.
public sealed partial class DocumentationWindow : Window
{
    // ── View-model rows. Init-only so the XAML DataTemplate bindings stay
    //    one-way and the rows are immutable once added to the ListView. ──

    /// <summary>
    /// Category row for the left-hand pane. Carries a count label so the
    /// streamer sees at a glance how many features sit under each section.
    /// </summary>
    public sealed class CategoryRow
    {
        public string Name       { get; init; } = "";
        public int    Count      { get; init; }
        public string CountLabel { get; init; } = "";
        public IReadOnlyList<HubFeature> Features { get; init; } = Array.Empty<HubFeature>();
    }

    /// <summary>
    /// Feature row for the middle pane. Carries the original record so the
    /// detail pane can hydrate without a second registry lookup.
    /// </summary>
    public sealed class FeatureRow
    {
        public string      Title    { get; init; } = "";
        public string      Category { get; init; } = "";
        public HubFeature? Feature  { get; init; }
    }

    private static DocumentationWindow? s_instance;

    private readonly List<CategoryRow> _categories = new();

    /// <summary>
    /// Open the singleton documentation window, or focus the existing one.
    /// Mirrors the <c>NodeDocumentationWindow.OpenOrFocus</c> shape so the
    /// MainWindow call site reads the same as the Architect equivalent.
    /// </summary>
    public static void OpenOrFocus()
    {
        try
        {
            if (s_instance is null)
            {
                s_instance = new DocumentationWindow(HubFeatureRegistry.GetAll());
            }
            s_instance.Activate();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocumentationWindow", "OpenOrFocus", ex);
            // Belt + braces — if Activate threw on a torn-down window, clear
            // the cached instance so the next click can recover.
            s_instance = null;
        }
    }

    /// <summary>
    /// Constructor takes the registry data via injection so the window stays
    /// testable and HubFeatureRegistry isn't a hard dependency of the type
    /// itself. In practice <see cref="OpenOrFocus"/> is the only call site
    /// inside the app; <c>features</c> always comes from the static accessor.
    /// </summary>
    public DocumentationWindow(IReadOnlyList<HubFeature> features)
    {
        InitializeComponent();

        Title = Localizer.T("dialog.documentation.title", "Phoenix Controls — Documentation");

        //  WinUI 3 custom chrome path — the documentation window
        // keeps the system caption (no traffic-light overlay) but ties its
        // drag region to HeaderBar so the user can move the window from the
        // gradient strip as well as the system caption above. SetTitleBar
        // honours a child UIElement; the chrome row sits flush with the
        // window top.
        ExtendsContentIntoTitleBar = true;
        try { SetTitleBar(HeaderBar); } catch { /* SetTitleBar can fail pre-Loaded on some SDK builds; ignore */ }

        ApplyLocalizedChrome();
        ConfigureAppWindow();
        HydrateFromRegistry(features ?? Array.Empty<HubFeature>());

        Closed += (_, _) =>
        {
            if (ReferenceEquals(s_instance, this)) s_instance = null;
        };
    }

    /// <summary>
    /// Pull every localized string the window needs into its TextBlocks.
    /// Keys are scoped under <c>dialog.documentation.*</c> so a future
    /// translator-pass can pick them up by namespace; English fallbacks
    /// match the visible text 1:1 with the seed copy so non-EN users still
    /// see readable chrome until the bundle catches up.
    /// </summary>
    private void ApplyLocalizedChrome()
    {
        EyebrowText.Text = Localizer.T(
            "dialog.documentation.eyebrow",
            "REFERENCE");
        HeadingText.Text = Localizer.T(
            "dialog.documentation.heading",
            "Hub Feature Catalogue");
        SubheadingText.Text = Localizer.T(
            "dialog.documentation.subheading",
            "Every user-facing Hub surface, indexed by category. Pick a category on the left, then a feature in the middle pane for description and where-to-find.");

        CategoriesHeader.Text = Localizer.T(
            "dialog.documentation.categories_header",
            "CATEGORIES");
        FeaturesHeader.Text = Localizer.T(
            "dialog.documentation.features_header",
            "FEATURES");

        DetailDescriptionHeader.Text = Localizer.T(
            "dialog.documentation.description_header",
            "WHAT IT IS");
        DetailWhereHeader.Text = Localizer.T(
            "dialog.documentation.where_header",
            "WHERE TO FIND IT");
    }

    /// <summary>
    /// Resolve the underlying <see cref="AppWindow"/> and apply the suite-
    /// standard chrome knobs:
    ///   - 960×620 starting size (DPI-aware physical pixels).
    ///   - Standard overlapped presenter — resizable, with title bar,
    ///     no always-on-top.
    ///   - Suite icon so the taskbar entry matches the Hub.
    /// Best-effort throughout — the window must still open even if any of
    /// these knobs fault on a non-standard SDK build (the documentation
    /// content is the load-bearing surface).
    /// </summary>
    private void ConfigureAppWindow()
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            try { appWindow.SetIcon("Assets/app.ico"); } catch { /* asset path differs in some build configs */ }

            // 960×620 DIPs converted to physical pixels via the window's
            // current DPI — matches the SplashWindow's DPI-aware sizing
            // pattern so a 150% display gets a properly-scaled launch size.
            var size = ResolvePhysicalSize(hwnd, widthDips: 960, heightDips: 620);
            appWindow.Resize(size);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable    = true;
                presenter.IsMaximizable  = true;
                presenter.IsMinimizable  = true;
                presenter.IsAlwaysOnTop  = false;
                // Keep the system caption visible — the Documentation window
                // is a secondary window, not the brand chrome, so the user
                // expects normal min/max/close affordances on it.
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: true);
            }

            // Centre on the active display so the window doesn't land off-
            // screen on a multi-monitor rig. Best-effort; defaults to the
            // primary display if the lookup fails.
            try
            {
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                var work = displayArea.WorkArea;
                appWindow.Move(new PointInt32(
                    work.X + (work.Width  - size.Width)  / 2,
                    work.Y + (work.Height - size.Height) / 2));
            }
            catch { /* centring is best-effort */ }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocumentationWindow", "ConfigureAppWindow", ex);
        }
    }

    /// <summary>
    /// DIP → physical-pixel converter. WinUI 3's AppWindow.Resize operates in
    /// physical pixels even though the XAML sizing space is in DIPs, so a
    /// raw <c>Resize(new SizeInt32(960, 620))</c> renders ~71% on a 144 DPI
    /// display. Mirrors the SplashWindow.ResolvePhysicalSize helper.
    /// </summary>
    private static SizeInt32 ResolvePhysicalSize(IntPtr hwnd, int widthDips, int heightDips)
    {
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi >= 96 ? dpi / 96.0 : 1.0;
        return new SizeInt32(
            (int)Math.Round(widthDips  * scale),
            (int)Math.Round(heightDips * scale));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// Build the category + feature view-models from the supplied registry
    /// data, push them into the ListViews, and pre-select the first row in
    /// each pane so the detail surface always has content on open.
    /// </summary>
    private void HydrateFromRegistry(IReadOnlyList<HubFeature> features)
    {
        _categories.Clear();

        // Preserve registry order — the file groups features by section
        // (Main Window → Dashboard → Tools → Windows → Behind the Scenes →
        // Architect) and that ordering matches how a streamer learns the
        // surface. A naive alpha-sort would scramble it. We grab the first
        // occurrence of each category in registry order, then pull its
        // members in the order they were registered.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var f in features)
        {
            var cat = f?.Category ?? "";
            if (string.IsNullOrEmpty(cat)) continue;
            if (seen.Add(cat)) order.Add(cat);
        }

        foreach (var cat in order)
        {
            var members = features
                .Where(f => string.Equals(f?.Category, cat, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _categories.Add(new CategoryRow
            {
                Name       = cat,
                Count      = members.Count,
                CountLabel = string.Format(
                    CultureInfo.InvariantCulture,
                    Localizer.T("dialog.documentation.count_format", "{0} feature(s)"),
                    members.Count),
                Features   = members,
            });
        }

        CategoryList.ItemsSource = _categories;

        if (_categories.Count > 0)
        {
            CategoryList.SelectedIndex = 0;
        }
        else
        {
            // Empty registry — clear the panes rather than leaving stale
            // text behind. Should never fire in production because the
            // registry seeds itself in its static ctor, but be defensive.
            ClearDetailPane();
            FeatureList.ItemsSource = Array.Empty<FeatureRow>();
        }
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryList.SelectedItem is not CategoryRow row)
        {
            FeatureList.ItemsSource = Array.Empty<FeatureRow>();
            ClearDetailPane();
            return;
        }

        var rows = row.Features
            .Select(f => new FeatureRow
            {
                Title    = f.Title,
                Category = f.Category,
                Feature  = f,
            })
            .ToList();

        FeatureList.ItemsSource = rows;

        if (rows.Count > 0)
            FeatureList.SelectedIndex = 0;
        else
            ClearDetailPane();
    }

    private void OnFeatureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FeatureList.SelectedItem is not FeatureRow row || row.Feature is null)
        {
            ClearDetailPane();
            return;
        }

        var f = row.Feature;
        DetailCategoryText.Text    = (f.Category ?? "").ToUpperInvariant();
        DetailTitleText.Text       = string.IsNullOrEmpty(f.Title) ? "—" : f.Title;

        DetailDescriptionText.Text = string.IsNullOrEmpty(f.Description)
            ? Localizer.T("dialog.documentation.no_description", "(no description provided)")
            : f.Description;

        DetailWhereText.Text = string.IsNullOrEmpty(f.WhereToFind)
            ? Localizer.T("dialog.documentation.no_where", "(no location hint provided)")
            : f.WhereToFind;
    }

    private void ClearDetailPane()
    {
        DetailCategoryText.Text    = string.Empty;
        DetailTitleText.Text       = Localizer.T(
            "dialog.documentation.empty_selection",
            "Select a feature on the left to see its description.");
        DetailDescriptionText.Text = string.Empty;
        DetailWhereText.Text       = string.Empty;
    }
}
