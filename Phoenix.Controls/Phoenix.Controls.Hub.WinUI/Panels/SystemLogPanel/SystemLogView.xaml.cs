using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Phoenix.Controls.Hub.WinUI.Panels.SystemLogPanel;

public sealed partial class SystemLogView : UserControl, IDisposable,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutSource,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutAware
{
    public SystemLogViewModel ViewModel { get; }
    private bool _disposed;

    // Within this many DIPs of the bottom the user is considered "at bottom"
    // and auto-scroll stays armed. Larger than the v4/v5 4-px threshold
    // because the new ListView produces variable row heights (TextWrapping
    // on the message column), so a few-DIP slop matters more.
    private const double BottomThresholdDips = 50.0;

    // Inner ScrollViewer of LogList — resolved from the visual tree on
    // first Loaded. ListView doesn't expose its scroller as a public
    // property in WinUI 3, so we walk the template tree once. ViewChanged
    // is hooked on this instance, not on the ListView, because it's the
    // scroller that surfaces VerticalOffset / ScrollableHeight.
    private ScrollViewer? _logScrollViewer;

    private bool _autoScrollPaused;
    private bool _scrollRequestPending;
    private double _prevVerticalOffset;
    // Guard — _prevVerticalOffset is meaningless until the first
    // ViewChanged we've actually observed; before that the field's default
    // 0.0 doesn't represent "the prior offset," it represents "we don't
    // know yet." Without this flag the first non-zero settled frame after
    // Load (e.g. snapshot replay pushes the list past the viewport) would
    // satisfy `offset < _prevVerticalOffset - 0.5` against the bogus 0 and
    // falsely trip the auto-scroll-pause path. We seed _prevVerticalOffset
    // on Loaded and use the first ViewChanged event only to update it; the
    // pause/resume logic starts on the second event.
    private bool _prevOffsetInitialized;

    // Search keystroke debounce — RebuildVisibleRows clears and re-fills
    // VisibleRows in place, which fires CollectionChanged.Reset followed
    // by a per-row Add. Typing a multi-character query without debounce
    // produced visible stutter per character; 250ms coalesces a burst
    // into one rebuild.
    private DispatcherTimer? _searchDebounce;
    private string _pendingSearchText = string.Empty;

    // Full selection ownership.
    //
    // The per-cell TextBlocks dropped IsTextSelectionEnabled (see
    // SystemLogView.xaml comment block on
    // the row template). With text-selection out of the way the ListView's
    // own pointer pipeline could in principle handle Shift/Ctrl modifiers,
    // but WinUI 3 ListView Extended selection does NOT include drag-marquee
    // multi-row select — and the requirement is specifically about
    // drag-across-rows extending the selection live. So we own all four
    // pointer phases:
    //   Pressed   — plain → single-select + start drag anchor
    //               shift → range [anchor..clicked]
    //               ctrl  → toggle clicked, move anchor
    //   Moved     — if drag-selecting, extend [anchor..rowUnderPointer]
    //   Released  — end drag (release capture)
    //   Canceled / CaptureLost — same as Released, defensive
    // _selectionAnchor is the row a no-modifier press last landed on;
    // Shift+Click and the drag-rubberband path both extend FROM it.
    private SystemLogRowVm? _selectionAnchor;
    private bool _dragSelecting;
    private int _dragAnchorIndex = -1;
    private Pointer? _dragPointer;
    private PointerEventHandler? _logPointerPressedHandler;
    private PointerEventHandler? _logPointerMovedHandler;
    private PointerEventHandler? _logPointerReleasedHandler;
    private PointerEventHandler? _logPointerCanceledHandler;
    private PointerEventHandler? _logPointerCaptureLostHandler;

    // The previous root-level wheel workaround was deleted.
    // It existed only because IsTextSelectionEnabled=True on the
    // per-cell TextBlocks was claiming PointerWheelChanged and starving the
    // inner ScrollViewer of wheel input. With that attribute now stripped
    // from every cell in SystemLogView.xaml, the ScrollViewer receives wheel
    // events natively (and marks them Handled). Re-introducing a root
    // handler with handledEventsToo:true on top of that would scroll twice
    // per wheel notch — the "scrolling does not work"
    // report was actually triggered by it once the text-block claimant
    // disappeared. Native handling is sufficient now.

    public event EventHandler? PopOutRequested;

    public SystemLogView(SystemLogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // VM owns its dispatcher via ctor injection — no
        // shared static slot to capture from the View side anymore.
        ApplyLocalizedStrings();
        // Sync chip IsChecked state to the VM's
        // initial filter mask. The VM may have restored persisted chip
        // state from AppConfig.SystemLogActiveLevels before the panel was
        // visible; without this the chips would render in their XAML
        // default state (DBG off, INF/WRN/ERR on) regardless of the
        // restored mask. Order matters: sync BEFORE the Click handlers
        // are interactable so we don't fire OnLevelChipClick for the
        // initial sync pulse.
        DbgChip.IsChecked = ViewModel.ShowDebug;
        InfChip.IsChecked = ViewModel.ShowInfo;
        WrnChip.IsChecked = ViewModel.ShowWarn;
        ErrChip.IsChecked = ViewModel.ShowError;
        ViewModel.VisibleRows.CollectionChanged += OnVisibleRowsChanged;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;

        // Observability hook for runtime theme swaps. The capability
        // (RefreshBrushes + InvalidateBrushCache) exists; the
        // missing piece was the trigger. Hub doesn't ship a live light/dark
        // switcher today, but ActualThemeChanged also fires when Windows
        // forces high-contrast at the OS level or when a parent advertises a
        // RequestedTheme override — without this subscription the buffered
        // rows would render with brushes resolved against the *old* merged
        // theme dictionary until each row scrolled out and a new row replaced
        // it. Unsubscribe in Dispose so we don't leak the panel through the
        // FrameworkElement event source if the panel is torn down (pop-out
        // hand-off, workspace rebuild).
        ActualThemeChanged += OnActualThemeChanged;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.VisibleRows.CollectionChanged -= OnVisibleRowsChanged;
        Loaded -= OnViewLoaded;
        Unloaded -= OnViewUnloaded;
        ActualThemeChanged -= OnActualThemeChanged;
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ViewChanged -= OnLogScrollViewChanged;
            _logScrollViewer = null;
        }
        if (_logPointerPressedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerPressedEvent, _logPointerPressedHandler);
            _logPointerPressedHandler = null;
        }
        if (_logPointerMovedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerMovedEvent, _logPointerMovedHandler);
            _logPointerMovedHandler = null;
        }
        if (_logPointerReleasedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerReleasedEvent, _logPointerReleasedHandler);
            _logPointerReleasedHandler = null;
        }
        if (_logPointerCanceledHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerCanceledEvent, _logPointerCanceledHandler);
            _logPointerCanceledHandler = null;
        }
        if (_logPointerCaptureLostHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerCaptureLostEvent, _logPointerCaptureLostHandler);
            _logPointerCaptureLostHandler = null;
        }
        if (_searchDebounce is not null)
        {
            // Flush any in-flight search the user typed before the 250ms
            // debounce tick had a chance to fire. Without this the query is
            // silently discarded on panel tear-down (pop-out hand-off,
            // workspace rebuild). The VM setter is a no-op when the text is
            // unchanged, so the compare is just belt-and-braces. Done BEFORE
            // ViewModel.Dispose() below so the rebuild still has a live VM.
            if (!string.Equals(ViewModel.SearchText, _pendingSearchText, StringComparison.Ordinal))
            {
                ViewModel.SearchText = _pendingSearchText;
            }
            _searchDebounce.Stop();
            _searchDebounce.Tick -= OnSearchDebounceTick;
            _searchDebounce = null;
        }
        ViewModel.Dispose();
    }

    /// <summary>
    /// Runtime theme-swap handler. ActualThemeChanged is the
    /// WinUI 3 surface that fires whenever the resolved theme for this
    /// element changes (parent RequestedTheme switch, OS high-contrast
    /// engaged, or a future in-app settings toggle). Marshal the VM
    /// RefreshBrushes() call through the dispatcher pump so an off-thread
    /// theme signal (unlikely in WinUI but cheap insurance) re-enters on
    /// the UI thread that owns _buffer / VisibleRows. Direct call when
    /// already on the UI thread — ActualThemeChanged normally fires there.
    /// </summary>
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_disposed) return;
        var dq = DispatcherQueue;
        if (dq is null || dq.HasThreadAccess)
        {
            ViewModel.RefreshBrushes();
        }
        else
        {
            dq.TryEnqueue(() =>
            {
                if (_disposed) return;
                ViewModel.RefreshBrushes();
            });
        }
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        // ListView builds its template tree on first arrange — wait for the
        // scroller to materialise, then hook ViewChanged. Re-resolving on
        // every Loaded is harmless and recovers from theme reloads.
        if (_logScrollViewer is null)
        {
            _logScrollViewer = FindDescendantScrollViewer(LogList);
            if (_logScrollViewer is not null)
            {
                _logScrollViewer.ViewChanged += OnLogScrollViewChanged;
            }
        }
        // Subscribe to the full four-phase pointer pipeline so we drive
        // selection ourselves (see the field-block comment block above).
        // handledEventsToo:true is defence in depth so the pipeline still
        // works if any descendant (theme override, future row-template
        // work) marks a phase Handled — the original IsTextSelectionEnabled
        // claim is gone but the subscription cost is one delegate slot per
        // phase, cheap insurance.
        if (_logPointerPressedHandler is null)
        {
            _logPointerPressedHandler   = OnLogPointerPressed;
            _logPointerMovedHandler     = OnLogPointerMoved;
            _logPointerReleasedHandler  = OnLogPointerReleased;
            _logPointerCanceledHandler  = OnLogPointerCanceled;
            _logPointerCaptureLostHandler = OnLogPointerCaptureLost;
            // handledEventsToo:true — defence in depth so the four-phase
            // tracking still works if any descendant (theme override, future
            // chrome) marks an event Handled. With IsTextSelectionEnabled
            // off the per-cell TextBlocks no longer claim pointer events,
            // but the subscription cost is one delegate slot per phase and
            // makes the selection robust against future row-template work.
            LogList.AddHandler(UIElement.PointerPressedEvent,     _logPointerPressedHandler,     handledEventsToo: true);
            LogList.AddHandler(UIElement.PointerMovedEvent,       _logPointerMovedHandler,       handledEventsToo: true);
            LogList.AddHandler(UIElement.PointerReleasedEvent,    _logPointerReleasedHandler,    handledEventsToo: true);
            LogList.AddHandler(UIElement.PointerCanceledEvent,    _logPointerCanceledHandler,    handledEventsToo: true);
            LogList.AddHandler(UIElement.PointerCaptureLostEvent, _logPointerCaptureLostHandler, handledEventsToo: true);
        }
        // Sync the "previous offset" snapshot to whatever the
        // scroller actually shows on first layout. Without this, the diff
        // in OnLogScrollViewChanged compares the first non-zero settled
        // frame against a default 0.0 and falsely concludes the user just
        // scrolled UP, pausing auto-scroll before any real input arrives.
        if (_logScrollViewer is not null)
        {
            _prevVerticalOffset = _logScrollViewer.VerticalOffset;
            _prevOffsetInitialized = true;
        }
        RequestAutoScroll();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        // Drop the ViewChanged subscription proactively when the panel is
        // unloaded (theme reload / pop-out hand-off). Dispose is the
        // belt-and-braces path; Unloaded covers the typical lifecycle.
        if (_logScrollViewer is not null)
        {
            _logScrollViewer.ViewChanged -= OnLogScrollViewChanged;
            _logScrollViewer = null;
        }
        if (_logPointerPressedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerPressedEvent, _logPointerPressedHandler);
            _logPointerPressedHandler = null;
        }
        if (_logPointerMovedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerMovedEvent, _logPointerMovedHandler);
            _logPointerMovedHandler = null;
        }
        if (_logPointerReleasedHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerReleasedEvent, _logPointerReleasedHandler);
            _logPointerReleasedHandler = null;
        }
        if (_logPointerCanceledHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerCanceledEvent, _logPointerCanceledHandler);
            _logPointerCanceledHandler = null;
        }
        if (_logPointerCaptureLostHandler is not null)
        {
            LogList.RemoveHandler(UIElement.PointerCaptureLostEvent, _logPointerCaptureLostHandler);
            _logPointerCaptureLostHandler = null;
        }
        _dragSelecting = false;
        _dragAnchorIndex = -1;
        _dragPointer = null;
        // Reset the offset-seed guard so the next Loaded re-
        // captures a truthful prior offset for the (potentially newly
        // hosted) scroller. Without this, re-load reuses the stale offset
        // captured before unload.
        _prevOffsetInitialized = false;
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, EventArgs.Empty);

    // Pop-out child dead-↗ fix. See LiveFeedView.MarkAsPopOutChild.
    public void MarkAsPopOutChild() => PopOutButton.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Resolves search placeholder, filter labels, pop-out / log-folder
    /// button captions, tooltips, and automation strings through
    /// <see cref="Localizer.T"/>. Called once in the ctor.
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        SearchBox.PlaceholderText = Localizer.T("panel.systemlog.search.placeholder", "search…");
        FilterLabel.Text          = Localizer.T("panel.systemlog.filter.label",       "FILTER");

        DbgChip.Content = Localizer.T("panel.systemlog.level.dbg", "DBG");
        InfChip.Content = Localizer.T("panel.systemlog.level.inf", "INF");
        WrnChip.Content = Localizer.T("panel.systemlog.level.wrn", "WRN");
        ErrChip.Content = Localizer.T("panel.systemlog.level.err", "ERR");

        OpenLogFolderLabel.Text = Localizer.T("panel.systemlog.button.log_folder", "log folder");
        ToolTipService.SetToolTip(OpenLogFolderButton,
            Localizer.T("panel.systemlog.button.log_folder.tooltip",
                "Open the Hub log folder in Explorer (%AppData%/PhoenixControls/Hub/)"));
        AutomationProperties.SetName(OpenLogFolderButton,
            Localizer.T("panel.systemlog.button.log_folder.aria", "Open Hub log folder"));

        // Export-to-file + Clear buffer labels
        // + tooltips + ARIA names. Strings flow through Localizer with
        // verbose English fallbacks so screen readers still annotate
        // correctly on a fresh (un-localized) build.
        ExportLabel.Text = Localizer.T("panel.systemlog.button.export", "export");
        ToolTipService.SetToolTip(ExportButton,
            Localizer.T("panel.systemlog.button.export.tooltip",
                "Export the currently visible rows to a .txt file"));
        AutomationProperties.SetName(ExportButton,
            Localizer.T("panel.systemlog.button.export.aria", "Export visible system log rows"));

        ClearLabel.Text = Localizer.T("panel.systemlog.button.clear", "clear");
        ToolTipService.SetToolTip(ClearButton,
            Localizer.T("panel.systemlog.button.clear.tooltip",
                "Clear the System Log panel buffer (does not delete on-disk logs)"));
        AutomationProperties.SetName(ClearButton,
            Localizer.T("panel.systemlog.button.clear.aria", "Clear System Log panel buffer"));

        PopOutLabel.Text = Localizer.T("panel.systemlog.button.popout", "pop-out");
        ToolTipService.SetToolTip(PopOutButton,
            Localizer.T("panel.systemlog.popout.tooltip", "Pop out System Log to a standalone window"));
        AutomationProperties.SetName(PopOutButton,
            Localizer.T("panel.systemlog.popout.aria", "Pop out System Log panel"));

        AutomationProperties.SetName(this,
            Localizer.T("panel.systemlog.aria.name", "System Log panel"));
        // The ARIA help text previously hard-coded "2000-entry
        // buffer," which was wrong twice over: the cap is configurable
        // through AppConfig.SystemLogMaxRows (default 10000), and the
        // upstream GlobalLogger ring (2000) is a different number from the
        // panel's own buffer. We format the live cap from
        // ConfigManager.Current so screen readers report the actual
        // headroom the user has.
        int cap = ConfigManager.Current?.SystemLogMaxRows ?? 10_000;
        if (cap <= 0) cap = 10_000;
        string capText = cap.ToString(System.Globalization.CultureInfo.CurrentCulture);
        string helpTemplate = Localizer.T("panel.systemlog.aria.help",
            "Rolling {0}-entry buffer of Hub system events, filterable by level (DBG / INF / WRN / ERR) and free-text search. Right-click for copy actions; the log-folder button opens the rolling log file in Explorer.");
        AutomationProperties.SetHelpText(this,
            string.Format(System.Globalization.CultureInfo.CurrentCulture, helpTemplate, capText));

        // Scroll-to-latest button label / tooltip / ARIA.
        ScrollToLatestLabel.Text = Localizer.T("panel.systemlog.jump_to_latest", "latest");
        ToolTipService.SetToolTip(ScrollToLatestButton,
            Localizer.T("panel.systemlog.jump_to_latest.tooltip",
                "Resume auto-scroll and jump to the newest entry"));
        AutomationProperties.SetName(ScrollToLatestButton,
            Localizer.T("panel.systemlog.jump_to_latest.aria", "Scroll to latest entry"));
    }

    private void OnLevelChipClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowDebug = DbgChip.IsChecked == true;
        ViewModel.ShowInfo  = InfChip.IsChecked == true;
        ViewModel.ShowWarn  = WrnChip.IsChecked == true;
        ViewModel.ShowError = ErrChip.IsChecked == true;
    }

    /// <summary>
    /// "(folder) log folder" header button — opens
    /// %AppData%/PhoenixControls/Hub/ in Explorer so the user can grab the
    /// rolling log file directly. The glyph itself is the
    /// PhoenixIcon_FolderOpen path in the panel XAML.
    /// </summary>
    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PhoenixControls", "Hub");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName        = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SystemLogView", "OpenLogFolder", ex);
        }
    }

    /// <summary>
    /// Header search box — substring match (case-insensitive) over
    /// Source / Message / LevelText. Pushes through ViewModel.SearchText
    /// which rebuilds VisibleRows in place.
    /// </summary>
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        _pendingSearchText = tb.Text ?? string.Empty;
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _searchDebounce.Tick += OnSearchDebounceTick;
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, object e)
    {
        _searchDebounce?.Stop();
        ViewModel.SearchText = _pendingSearchText;
        // After a filter rebuild the user almost always wants to see the
        // newest matching tail.
        _autoScrollPaused = false;
        UpdateScrollToLatestButtonVisibility();
        RequestAutoScroll();
    }

    /// <summary>
    /// Direction-aware auto-scroll gate. When the scroller settles within
    /// <see cref="BottomThresholdDips"/> of the bottom we re-arm; when the
    /// user moves the offset AWAY from the bottom (wheel up, drag, PgUp,
    /// arrow keys, scroll-bar drag) we pause. Programmatic ChangeView calls
    /// in <see cref="RequestAutoScroll"/> always land at the bottom, so
    /// they never trip pause.
    /// </summary>
    private void OnLogScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        var sv = _logScrollViewer;
        if (sv is null) return;

        double offset             = sv.VerticalOffset;
        double scrollable         = sv.ScrollableHeight;
        double distanceFromBottom = scrollable - offset;

        // Skip the pause heuristic on the very first settled
        // frame after Load. _prevVerticalOffset can be the seed value from
        // OnViewLoaded, but if Loaded fired before the scroller's first
        // measure pass the seed itself is 0.0; either way we use the first
        // ViewChanged callback to lock in a truthful prior offset and only
        // start comparing on the second.
        if (!_prevOffsetInitialized)
        {
            _prevVerticalOffset = offset;
            _prevOffsetInitialized = true;
            UpdateScrollToLatestButtonVisibility();
            return;
        }

        if (distanceFromBottom <= BottomThresholdDips)
        {
            _autoScrollPaused = false;
        }
        else if (offset < _prevVerticalOffset - 0.5)
        {
            _autoScrollPaused = true;
        }

        _prevVerticalOffset = offset;
        UpdateScrollToLatestButtonVisibility();
    }

    /// <summary>
    /// Owns the full pointer cycle for row selection: WinUI 3's ListView
    /// Extended SelectionMode does NOT
    /// include drag-across-rows multi-select, so we drive it from here.
    /// Plain  → single-select + arm drag rubber-band.
    /// Shift  → range [anchor..clicked]; replaces selection unless Ctrl is also held.
    /// Ctrl   → toggle clicked row; moves the anchor.
    /// Modifier clicks always swallow the event (Handled=true) so the
    /// ListView's own pressed-handling doesn't fight us; plain clicks also
    /// mark Handled because we replicate the single-select ourselves AND
    /// take pointer capture, so the ListView doing its own selection on top
    /// would just race us.
    /// </summary>
    private void OnLogPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only react to the primary (left) mouse button — right-click flows
        // through OnLogRightTapped, touch/pen go through their own paths.
        var point = e.GetCurrentPoint(LogList);
        if (!point.Properties.IsLeftButtonPressed) return;

        var row = FindRowFromSource(e.OriginalSource as DependencyObject);
        if (row is null) return;

        var rows = ViewModel.VisibleRows;
        int clickedIndex = rows.IndexOf(row);
        if (clickedIndex < 0) return;

        var mods = e.KeyModifiers;
        bool shift = (mods & VirtualKeyModifiers.Shift)   == VirtualKeyModifiers.Shift;
        bool ctrl  = (mods & VirtualKeyModifiers.Control) == VirtualKeyModifiers.Control;

        if (shift)
        {
            int anchorIndex = _selectionAnchor is null ? clickedIndex : rows.IndexOf(_selectionAnchor);
            if (anchorIndex < 0) anchorIndex = clickedIndex;
            SelectRange(anchorIndex, clickedIndex, additive: ctrl);
            // Shift+Click keeps the anchor; Shift+Ctrl behaves the same.
            e.Handled = true;
        }
        else if (ctrl)
        {
            if (LogList.SelectedItems.Contains(row))
            {
                LogList.SelectedItems.Remove(row);
            }
            else
            {
                LogList.SelectedItems.Add(row);
            }
            _selectionAnchor = row;
            e.Handled = true;
        }
        else
        {
            // Plain click → single-row selection AND start drag-rubberband.
            // Capture the pointer so PointerMoved fires even when the user
            // drags outside LogList (e.g. into the scrollbar gutter or off
            // the bottom of the panel). PointerReleased / Canceled /
            // CaptureLost all wind drag mode down.
            LogList.SelectedItems.Clear();
            LogList.SelectedItems.Add(row);
            _selectionAnchor   = row;
            _dragSelecting     = true;
            _dragAnchorIndex   = clickedIndex;
            _dragPointer       = e.Pointer;
            try { LogList.CapturePointer(e.Pointer); }
            catch { /* capture races — tolerated; drag still works on PointerMoved deltas. */ }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Drag-rubberband extension. When _dragSelecting is armed, every move
    /// recomputes the selection set as the inclusive range
    /// [_dragAnchorIndex .. indexOf(rowUnderPointer)]. We hit-test by
    /// coordinates rather than using e.OriginalSource because the
    /// PointerPressed handler takes pointer capture on LogList — and
    /// captured-pointer routing collapses OriginalSource onto the capture
    /// target on subsequent moves, which would otherwise leave us with
    /// no path back to the actual row under the cursor and silently
    /// no-op the drag-extend gesture.
    /// </summary>
    private void OnLogPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragSelecting) return;
        var point = e.GetCurrentPoint(LogList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDragSelection(e);
            return;
        }
        var row = FindRowAtPointerPosition(e);
        if (row is null) return;
        var rows = ViewModel.VisibleRows;
        int currentIndex = rows.IndexOf(row);
        if (currentIndex < 0) return;
        SelectRange(_dragAnchorIndex, currentIndex, additive: false);
    }

    /// <summary>
    /// Hit-test by host coordinates to find the row VM under the pointer.
    /// Used by the drag-rubberband path where pointer capture has been
    /// taken on LogList — captured-pointer routing sets OriginalSource to
    /// the capture target so a VisualTreeHelper walk from OriginalSource
    /// would terminate at LogList instead of reaching the inner
    /// ListViewItem. GetCurrentPoint(null) gives the pointer in XamlRoot
    /// coordinates which is exactly what FindElementsInHostCoordinates
    /// expects.
    /// </summary>
    private SystemLogRowVm? FindRowAtPointerPosition(PointerRoutedEventArgs e)
    {
        try
        {
            var hostPoint = e.GetCurrentPoint(null).Position;
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(hostPoint, LogList))
            {
                if (el is FrameworkElement fe && fe.DataContext is SystemLogRowVm row)
                {
                    return row;
                }
            }
        }
        catch
        {
            // FindElementsInHostCoordinates can throw if the visual tree
            // is mid-rebuild (theme reload, pop-out hand-off). Treat as
            // "no row under pointer" — drag-extend is idle for one frame.
        }
        return null;
    }

    private void OnLogPointerReleased(object sender, PointerRoutedEventArgs e) => EndDragSelection(e);
    private void OnLogPointerCanceled(object sender, PointerRoutedEventArgs e) => EndDragSelection(e);
    private void OnLogPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDragSelection(e);

    private void EndDragSelection(PointerRoutedEventArgs? e)
    {
        if (!_dragSelecting) return;
        _dragSelecting = false;
        _dragAnchorIndex = -1;
        var p = _dragPointer ?? e?.Pointer;
        _dragPointer = null;
        if (p is not null)
        {
            try { LogList.ReleasePointerCapture(p); }
            catch { /* shutdown / already released — tolerated. */ }
        }
    }

    private void SelectRange(int aIndex, int bIndex, bool additive)
    {
        var rows = ViewModel.VisibleRows;
        if (aIndex < 0 || bIndex < 0) return;
        int lo = Math.Min(aIndex, bIndex);
        int hi = Math.Max(aIndex, bIndex);
        if (lo >= rows.Count) return;
        hi = Math.Min(hi, rows.Count - 1);

        if (!additive) LogList.SelectedItems.Clear();
        for (int i = lo; i <= hi; i++)
        {
            var r = rows[i];
            if (!LogList.SelectedItems.Contains(r))
            {
                LogList.SelectedItems.Add(r);
            }
        }
    }

    /// <summary>
    /// Walk up from a pointer event's OriginalSource to find the row VM
    /// the click landed on. The ListViewItem's DataContext is the row VM
    /// when ItemsSource is bound to a typed collection.
    /// </summary>
    private static SystemLogRowVm? FindRowFromSource(DependencyObject? source)
    {
        var cursor = source;
        while (cursor is not null)
        {
            if (cursor is FrameworkElement fe && fe.DataContext is SystemLogRowVm row)
            {
                return row;
            }
            cursor = VisualTreeHelper.GetParent(cursor);
        }
        return null;
    }

    private void OnVisibleRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The ListView reads from VisibleRows directly; we don't have to
        // mirror anything into the visual tree by hand. The only thing the
        // view-side cares about is "did new content arrive? then maybe
        // auto-scroll." Reset / Remove leave the offset alone — the
        // ScrollViewer re-clamps itself.
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            RequestAutoScroll();
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Filter / search rebuild — the user expects to see the
            // newest matching tail. OnSearchDebounceTick / SetFlag both
            // un-pause; this is the no-op-safe defence-in-depth path.
            RequestAutoScroll();
        }
    }

    /// <summary>
    /// Coalesce autoscroll requests across a burst of Adds — multiple
    /// rows arriving in the same dispatcher tick produce a single
    /// ChangeView. The pause flag is the only thing that fights with
    /// programmatic scroll; user wheel / drag / key input goes through
    /// the scroller's native input handling and is detected in
    /// <see cref="OnLogScrollViewChanged"/>.
    /// </summary>
    private void RequestAutoScroll()
    {
        if (_autoScrollPaused) return;
        if (_scrollRequestPending) return;
        var dispatcher = DispatcherQueue;
        if (dispatcher is null) return;
        _scrollRequestPending = true;
        dispatcher.TryEnqueue(() =>
        {
            _scrollRequestPending = false;
            if (_autoScrollPaused) return;
            var sv = _logScrollViewer;
            if (sv is null) return;
            try
            {
                sv.UpdateLayout();
                sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
            }
            catch
            {
                /* ScrollViewer not realised yet, or shutting down. */
            }
        });
    }

    private void OnScrollToLatestClick(object sender, RoutedEventArgs e)
    {
        _autoScrollPaused = false;
        UpdateScrollToLatestButtonVisibility();
        RequestAutoScroll();
    }

    private void UpdateScrollToLatestButtonVisibility()
    {
        ScrollToLatestButton.Visibility = _autoScrollPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Ctrl+C — emit the current selection as plain-text rows in the
    /// canonical <c>[timestamp] [level] [source] message</c> format.
    /// No-op (with Handled=false) when nothing is selected so the
    /// accelerator doesn't swallow the user's intended copy from a
    /// sibling control. Stays inside the ListView's accelerator scope
    /// — KeyboardAccelerator fires only when the ListView is the
    /// focused element or one of its descendants.
    /// </summary>
    private void OnCopyAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (CopySelectionToClipboard())
        {
            args.Handled = true;
        }
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        LogList.SelectAll();
        args.Handled = true;
    }

    /// <summary>
    /// Right-click → Copy / Copy all visible / Select all + filter actions.
    /// The "Filter to source: &lt;Source&gt;" /
    /// "Clear source filter" entries — the source filter narrows the
    /// VisibleRows predicate at the VM and persists to AppConfig.
    /// </summary>
    private void OnLogRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        int selectedCount = LogList.SelectedItems?.Count ?? 0;

        // The row the user clicked. May be null if the user right-
        // clicked the panel's empty area below the rows; we still surface
        // the copy / select-all items in that case but skip the source
        // filter entries.
        var clickedRow = FindRowFromSource(e.OriginalSource as DependencyObject);

        var copy = new MenuFlyoutItem
        {
            Text = selectedCount > 1
                ? string.Format(Localizer.T("panel.systemlog.menu.copy_selection_count", "Copy {0} selected rows"), selectedCount)
                : Localizer.T("panel.systemlog.menu.copy_selection", "Copy selection"),
            KeyboardAcceleratorTextOverride = "Ctrl+C",
            IsEnabled = selectedCount > 0,
        };
        copy.Click += (_, _) => CopySelectionToClipboard();
        flyout.Items.Add(copy);

        var copyAll = new MenuFlyoutItem
        {
            Text = Localizer.T("panel.systemlog.menu.copy_all_visible", "Copy all visible rows"),
        };
        copyAll.Click += (_, _) => CopyAllVisibleToClipboard();
        flyout.Items.Add(copyAll);

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Filter to source: <Source>. Only meaningful when the user
        // right-clicked a row that has a non-empty Source. Setting persists
        // to AppConfig.SystemLogSourceFilter via the VM setter.
        if (clickedRow is not null && !string.IsNullOrEmpty(clickedRow.Source))
        {
            var filterToSource = new MenuFlyoutItem
            {
                Text = string.Format(
                    Localizer.T("panel.systemlog.menu.filter_to_source", "Filter to source: {0}"),
                    clickedRow.Source),
            };
            string sourceCapture = clickedRow.Source;
            filterToSource.Click += (_, _) => ViewModel.SourceFilter = sourceCapture;
            flyout.Items.Add(filterToSource);
        }

        // Clear source filter is only enabled when one is active. We
        // include it even when no row was clicked so the user can always
        // recover from an over-narrow filter via right-click.
        if (ViewModel.HasSourceFilter)
        {
            var clearSource = new MenuFlyoutItem
            {
                Text = Localizer.T("panel.systemlog.menu.clear_source_filter", "Clear source filter"),
            };
            clearSource.Click += (_, _) => ViewModel.SourceFilter = null;
            flyout.Items.Add(clearSource);
        }

        if (clickedRow is not null && (!string.IsNullOrEmpty(clickedRow.Source) || ViewModel.HasSourceFilter))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var selectAll = new MenuFlyoutItem
        {
            Text = Localizer.T("panel.systemlog.menu.select_all", "Select all"),
            KeyboardAcceleratorTextOverride = "Ctrl+A",
        };
        selectAll.Click += (_, _) => LogList.SelectAll();
        flyout.Items.Add(selectAll);

        var element = sender as FrameworkElement ?? LogList;
        flyout.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    /// <summary>
    /// Chevron tap toggles the row's expanded
    /// exception view. Routed via Tapped (not Click) so the row's
    /// pointer-capture pipeline doesn't swallow the event during the
    /// custom selection drag handling.
    /// </summary>
    private void OnChevronTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SystemLogRowVm row)
        {
            row.IsExceptionExpanded = !row.IsExceptionExpanded;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Double-click anywhere on the
    /// row toggles the exception expander. Rows without an exception ignore
    /// the gesture (HasException is false) so a stray double-click on a
    /// regular Info/Warn row doesn't pop an empty expander. Mirrors the
    /// chevron tap so the affordance has both an explicit handle and a
    /// "throw a double-click at the row" power-user path.
    /// </summary>
    private void OnRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is SystemLogRowVm row && row.HasException)
        {
            row.IsExceptionExpanded = !row.IsExceptionExpanded;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Export visible rows to a .txt file. Uses
    /// CustomFilePicker (the suite's IFileSaveDialog COM wrapper) rather
    /// than the WinUI 3 FileSavePicker because the latter only seeds
    /// folders via PickerLocationId — we want the picker to land in the
    /// Hub log folder by default. Format mirrors Ctrl+C / Copy-all:
    /// <c>[YYYY-MM-DD HH:mm:ss] [LEVEL] [SOURCE] Message</c> per line,
    /// with the formatted exception block on indented lines underneath
    /// when present.
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = TryGetWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                GlobalLogger.Log(
                    "SystemLogView: Export cancelled — could not resolve the host window handle.",
                    "SystemLogView", Phoenix.Controls.Shared.Models.LogLevel.CriticalError);
                return;
            }

            string startDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PhoenixControls", "Hub");
            string suggestedName = $"phoenix-systemlog-{DateTime.Now:yyyyMMdd-HHmmss}";

            string? target = CustomFilePicker.PickSaveFile(
                hwnd,
                startDir,
                suggestedName,
                new[]
                {
                    ("Text", ".txt"),
                    ("Log",  ".log"),
                });
            if (string.IsNullOrEmpty(target)) return; // user cancelled

            var sb = new StringBuilder(capacity: ViewModel.VisibleRows.Count * 128);
            foreach (var row in ViewModel.VisibleRows)
            {
                AppendExportRow(sb, row);
            }
            File.WriteAllText(target, sb.ToString(), Encoding.UTF8);
            GlobalLogger.Log(
                $"SystemLogView: exported {ViewModel.VisibleRows.Count} visible rows to '{target}'.",
                "SystemLogView", Phoenix.Controls.Shared.Models.LogLevel.System);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SystemLogView", "Export to file failed", ex);
        }
    }

    /// <summary>
    /// Row formatter — the export format:
    /// <c>[YYYY-MM-DD HH:mm:ss] [LEVEL] [SOURCE] Message</c>. The
    /// exception block (if present) goes on its own indented line
    /// underneath so a single-line-per-entry tail still works for grep
    /// and the stack trace is preserved when reading the file directly.
    /// </summary>
    private static void AppendExportRow(StringBuilder sb, SystemLogRowVm row)
    {
        string ts = row.Entry.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
        sb.Append('[').Append(ts).Append("] ")
          .Append('[').Append(row.LevelText).Append("] ")
          .Append('[').Append(row.Source ?? string.Empty).Append("] ")
          .AppendLine(row.Message ?? string.Empty);
        if (row.HasException)
        {
            // Indent the exception block with two spaces so a future
            // diff-style reader can collapse the lines together.
            string indented = "  " + row.ExceptionText.Replace("\n", "\n  ");
            sb.AppendLine(indented);
        }
    }

    /// <summary>
    /// Clear panel buffer with a confirm
    /// ContentDialog. Confirm is an accepted exception to the
    /// modal-rule (single user-initiated destructive op, not a
    /// repeatable validation rejection). On confirm we delegate to
    /// SystemLogViewModel.ClearBuffer; new entries flowing in via the
    /// EntryAdded subscription re-populate the panel naturally.
    /// </summary>
    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (XamlRoot is null) return;
            var confirm = new ContentDialog
            {
                Title             = Localizer.T("panel.systemlog.dialog.clear.title",   "Clear System Log?"),
                Content           = Localizer.T("panel.systemlog.dialog.clear.content",
                    "Removes every row currently in the panel buffer. The Hub's on-disk log files are not touched. New entries will continue to surface as they happen."),
                PrimaryButtonText = Localizer.T("panel.systemlog.dialog.clear.confirm", "Clear"),
                CloseButtonText   = Localizer.T("common.button.cancel",                 "Cancel"),
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = XamlRoot,
                RequestedTheme    = ElementTheme.Dark,
            };
            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
            ViewModel.ClearBuffer();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SystemLogView", "Clear buffer failed", ex);
        }
    }

    /// <summary>
    /// Resolve the hosting AppWindow's HWND for COM-picker callers.
    /// Returns IntPtr.Zero in test / design-time scenarios where the
    /// ContentIslandEnvironment isn't connected to an AppWindow yet.
    /// </summary>
    private IntPtr TryGetWindowHandle()
    {
        try
        {
            var root = XamlRoot;
            if (root?.ContentIslandEnvironment is null) return IntPtr.Zero;
            var wid = root.ContentIslandEnvironment.AppWindowId;
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(wid);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Returns true if a non-empty selection existed and was copied —
    /// signal back to the accelerator so it can mark Handled correctly.
    /// </summary>
    private bool CopySelectionToClipboard()
    {
        if (LogList.SelectedItems is null || LogList.SelectedItems.Count == 0)
            return false;

        // SelectedItems preserves selection order (the order items were
        // added to the selection set), not visual order. For a log feed
        // the user expects copied rows in chronological order — sort by
        // the underlying VisibleRows index.
        var rows = new List<SystemLogRowVm>();
        foreach (var item in LogList.SelectedItems)
        {
            if (item is SystemLogRowVm row) rows.Add(row);
        }
        if (rows.Count == 0) return false;

        // Building a row→index dictionary in one pass before the
        // sort makes the comparator an O(1) dict lookup. The previous code
        // called ObservableCollection<>.IndexOf inside the comparator,
        // which is O(n) per call; with k = LogList.SelectedItems.Count and
        // n = VisibleRows.Count that produced an O(k·log(k)·n) hang on the
        // "Select All + Ctrl+C at full buffer" path (10k × log(10k) × 10k
        // ≈ 1.3 billion comparisons). Now it's O(n) build + O(k·log(k))
        // sort. Use a reference-equality dictionary because SystemLogRowVm
        // doesn't override Equals/GetHashCode and we want identity match.
        var visible = ViewModel.VisibleRows;
        var indexOf = new Dictionary<SystemLogRowVm, int>(
            capacity: visible.Count,
            comparer: ReferenceEqualityComparer.Instance);
        for (int i = 0; i < visible.Count; i++)
        {
            // Tolerate hypothetical duplicates by keeping the first index.
            if (!indexOf.ContainsKey(visible[i])) indexOf[visible[i]] = i;
        }

        rows.Sort((a, b) =>
        {
            int ia = indexOf.TryGetValue(a, out var va) ? va : int.MaxValue;
            int ib = indexOf.TryGetValue(b, out var vb) ? vb : int.MaxValue;
            return ia.CompareTo(ib);
        });

        var sb = new StringBuilder(capacity: rows.Count * 96);
        foreach (var row in rows) AppendFormatted(sb, row);
        CopyToClipboard(sb.ToString().TrimEnd('\r', '\n'));
        return true;
    }

    private void CopyAllVisibleToClipboard()
    {
        var sb = new StringBuilder(capacity: ViewModel.VisibleRows.Count * 96);
        foreach (var row in ViewModel.VisibleRows) AppendFormatted(sb, row);
        CopyToClipboard(sb.ToString().TrimEnd('\r', '\n'));
    }

    private static void AppendFormatted(StringBuilder sb, SystemLogRowVm row)
    {
        sb.Append('[').Append(row.TimestampText).Append("] ")
          .Append('[').Append(row.LevelText).Append("] ")
          .Append('[').Append(row.Source ?? string.Empty).Append("] ")
          .AppendLine(row.Message ?? string.Empty);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("SystemLogView", "CopyToClipboard", ex);
        }
    }

    /// <summary>
    /// Walk the visual tree below <paramref name="root"/> and return the
    /// first ScrollViewer encountered. ListView places its scroller a
    /// few levels deep inside its default template; the first descendant
    /// matching the type is the one we want.
    /// </summary>
    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var inner = FindDescendantScrollViewer(child);
            if (inner is not null) return inner;
        }
        return null;
    }
}
