using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.ApplicationModel.DataTransfer;

namespace Phoenix.Controls.Hub.WinUI.Panels.ScriptPanel;

public sealed partial class ScriptView : UserControl, IDisposable,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutSource,
    Phoenix.Controls.Hub.WinUI.Panels.Common.IPopOutAware
{
    public ScriptViewModel ViewModel { get; }
    private bool _disposed;

    // Hub UI sweep 2026-05-22 — root-level wheel handler. ItemsRepeater
    // inside a ScrollViewer doesn't always forward PointerWheelChanged
    // to its host (per-row CheckBox + TextBlock children can claim it),
    // so the panel was wheel-deaf in Majo's 0.10.6 report. Listening at
    // the UserControl root with handledEventsToo:true routes the delta
    // into ScriptScrollViewer regardless. Matches LiveFeedView pattern.
    private readonly PointerEventHandler _onRootWheelHandler;

    // ── Multi-select restoration (Lane C, 2026-06-01) ──────────────────
    // Pre-T15 the WinForms ScriptMonitorWindow used a FullRowSelect ListView
    // whose SelectedItems drove ApplyPolicyToSelected(policy). The WinUI
    // ItemsRepeater has no built-in selection model, so selection is
    // hand-rolled here:
    //   • _selectedRows is the source of truth — a set of selected row VMs,
    //     kept parallel to ViewModel.Rows (membership by VM reference).
    //   • _selectionAnchor is the pivot for Shift-range selection.
    //   • _realizedOverlays maps a *currently-realized* row VM to its
    //     SelectionOverlay Border. ItemsRepeater virtualizes/recycles
    //     containers, so we can't statically bind the overlay; instead we
    //     register/unregister overlays as elements are prepared/cleared and
    //     repaint the visible subset whenever selection changes.
    // ScriptRowVm is out-of-lane (owned elsewhere) and has no IsSelected
    // property, which is why selection lives entirely in the View.
    private readonly HashSet<ScriptRowVm> _selectedRows = new();
    private readonly Dictionary<ScriptRowVm, Border> _realizedOverlays = new();
    private ScriptRowVm? _selectionAnchor;

    public event EventHandler? PopOutRequested;

    public ScriptView(ScriptViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // C1 (2026-05-14): VM owns its dispatcher via ctor injection — no
        // shared static slot to capture from the View side anymore.
        ApplyLocalizedStrings();
        // Mirror the SystemLogView pattern: ActualThemeChanged
        // fires on OS high-contrast engage, parent RequestedTheme override,
        // or a future in-app settings toggle. Without this subscription
        // the errored-row tint and traffic-light text brush stay resolved
        // against the *old* merged theme until a script state change
        // re-raises and re-resolves.
        ActualThemeChanged += OnActualThemeChanged;

        // Hub UI sweep 2026-05-22 — install root wheel handler.
        _onRootWheelHandler = new PointerEventHandler(OnRootWheel);
        this.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _onRootWheelHandler,
            handledEventsToo: true);
    }

    /// <summary>
    /// Hub UI sweep 2026-05-22 — root-level wheel handler. Routes the
    /// pointer-wheel delta into ScriptScrollViewer when the cursor is
    /// inside its bounds. Mirrors LiveFeedView.OnRootWheel.
    /// </summary>
    private void OnRootWheel(object sender, PointerRoutedEventArgs e)
    {
        if (ScriptScrollViewer is null) return;
        var ptInSv = e.GetCurrentPoint(ScriptScrollViewer);
        var pos = ptInSv.Position;
        if (pos.X < 0 || pos.Y < 0
            || pos.X > ScriptScrollViewer.ActualWidth
            || pos.Y > ScriptScrollViewer.ActualHeight)
            return;
        int delta = ptInSv.Properties.MouseWheelDelta;
        if (delta == 0) return;
        double dy = -delta * 50.0 / 120.0;
        // Clamp the floor explicitly: a large upward delta otherwise yields a
        // negative offset that ChangeView only clamps internally. Make the
        // intent visible rather than relying on that undocumented behaviour.
        double newOffset = Math.Max(0, ScriptScrollViewer.VerticalOffset + dy);
        ScriptScrollViewer.ChangeView(null, newOffset, null, disableAnimation: false);
        e.Handled = true;
    }

    /// <summary>
    /// Runtime theme-swap handler — marshal RefreshBrushes onto
    /// the UI thread (ActualThemeChanged usually fires there but stay
    /// defensive).
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

    /// <summary>
    /// Resolves column headers, pop-out chrome, and automation strings through
    /// <see cref="Localizer.T"/>. Called once in the ctor (Hub UI sweep —
    /// localization batch).
    /// </summary>
    private void ApplyLocalizedStrings()
    {
        HeaderScript.Text = Localizer.T("panel.scripts.header.script", "SCRIPT");
        HeaderStatus.Text = Localizer.T("panel.scripts.header.status", "STATUS");
        HeaderTime.Text   = Localizer.T("panel.scripts.header.time",   "TIME");
        HeaderRam.Text    = Localizer.T("panel.scripts.header.ram",    "RAM");
        HeaderRuns.Text   = Localizer.T("panel.scripts.header.runs",   "RUNS");
        // Concurrent-slot column header.
        HeaderQueue.Text  = Localizer.T("panel.scripts.header.queue",  "QUEUE");

        // Hub UI sweep 2026-05-22 — visible "pop-out" label next to the icon.
        PopOutLabel.Text = Localizer.T("panel.common.button.popout", "pop-out");
        ToolTipService.SetToolTip(PopOutButton,
            Localizer.T("panel.common.popout.tooltip", "Pop-out"));
        AutomationProperties.SetName(PopOutButton,
            Localizer.T("panel.scripts.popout.aria", "Pop out Scripts panel"));

        AutomationProperties.SetName(this,
            Localizer.T("panel.scripts.aria.name", "Scripts panel"));
        AutomationProperties.SetHelpText(this,
            Localizer.T("panel.scripts.aria.help",
                "One row per script in the active data/logic folder, with traffic-light status, CPU and RAM cost, and run count. Right-click a row for open, reload, reveal, and last-error actions."));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        try { this.RemoveHandler(UIElement.PointerWheelChangedEvent, _onRootWheelHandler); }
        catch { /* shutdown best-effort */ }
        // Drop selection bookkeeping so disposed row VMs / detached overlays
        // aren't held alive past teardown.
        _selectedRows.Clear();
        _realizedOverlays.Clear();
        _selectionAnchor = null;
        ViewModel.Dispose();
    }

    private void OnPopOutClick(object sender, RoutedEventArgs e)
        => PopOutRequested?.Invoke(this, EventArgs.Empty);

    // Hub UI sweep P2 — pop-out child dead-↗ fix. See LiveFeedView.MarkAsPopOutChild.
    public void MarkAsPopOutChild() => PopOutButton.Visibility = Visibility.Collapsed;

    private async void OnEnableCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ScriptRowVm row)
        {
            // ToggleEnabledAsync can throw (file watch race, disk full, etc.)
            // — keep the exception off the dispatcher's unhandled path.
            try { await ViewModel.ToggleEnabledAsync(row); }
            catch (Exception ex) { GlobalLogger.Error("ScriptView", "Toggle enabled failed", ex); }
        }
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ScriptRowVm row) return;
        e.Handled = true;

        // Standard list semantics: if the right-clicked row is part of the
        // current multi-selection, the menu acts on the whole selection. If
        // it isn't, the right-click first re-selects just that row (replacing
        // any prior selection) so the menu acts on what the user pointed at.
        if (!_selectedRows.Contains(row))
        {
            SelectSingle(row);
        }

        BuildContextMenu(row).ShowAt(fe, e.GetPosition(fe));
    }

    // ── Selection: pointer handling ────────────────────────────────────
    // Plain tap  → select only this row (anchor reset).
    // Ctrl+tap   → toggle this row in/out of the selection (anchor = row).
    // Shift+tap  → select the contiguous range from the anchor to this row.
    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ScriptRowVm row) return;

        // [build fix] TappedRoutedEventArgs has no KeyModifiers (only KeyRoutedEventArgs
        // does); read the live Ctrl/Shift state via the WinUI 3 input source instead.
        bool ctrl  = IsModifierKeyDown(global::Windows.System.VirtualKey.Control);
        bool shift = IsModifierKeyDown(global::Windows.System.VirtualKey.Shift);

        if (shift && _selectionAnchor is not null)
        {
            SelectRange(_selectionAnchor, row, additive: ctrl);
        }
        else if (ctrl)
        {
            ToggleSelection(row);
            _selectionAnchor = row;
        }
        else
        {
            SelectSingle(row);
        }

        e.Handled = true;
    }

    // Live keyboard-modifier read for click-with-modifier selection. Tapped/Pointer
    // routed args don't carry modifier state in WinUI 3, so query the input source.
    private static bool IsModifierKeyDown(global::Windows.System.VirtualKey key)
        => global::Microsoft.UI.Input.InputKeyboardSource
               .GetKeyStateForCurrentThread(key)
               .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

    // ── Selection: state mutation ──────────────────────────────────────
    private void SelectSingle(ScriptRowVm row)
    {
        _selectedRows.Clear();
        _selectedRows.Add(row);
        _selectionAnchor = row;
        RefreshSelectionVisuals();
    }

    private void ToggleSelection(ScriptRowVm row)
    {
        if (!_selectedRows.Remove(row))
            _selectedRows.Add(row);
        RefreshSelectionVisuals();
    }

    private void SelectRange(ScriptRowVm anchor, ScriptRowVm target, bool additive)
    {
        var rows = ViewModel.Rows;
        int a = rows.IndexOf(anchor);
        int b = rows.IndexOf(target);
        if (a < 0 || b < 0)
        {
            // Anchor scrolled out of the collection or was removed — degrade
            // to a single-row selection rather than throwing.
            SelectSingle(target);
            return;
        }
        if (!additive) _selectedRows.Clear();
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        for (int i = lo; i <= hi; i++)
            _selectedRows.Add(rows[i]);
        // Anchor stays put across a Shift-range so the user can grow/shrink
        // the range from the same pivot (matches WinForms ListView behavior).
        RefreshSelectionVisuals();
    }

    // ── Selection: visual reflection ───────────────────────────────────
    // Paints every currently-realized overlay to match _selectedRows. Cheap:
    // only realized (on-screen) rows are in _realizedOverlays, so the loop is
    // bounded by viewport size, not by the script count.
    private void RefreshSelectionVisuals()
    {
        foreach (var kvp in _realizedOverlays)
        {
            kvp.Value.Visibility = _selectedRows.Contains(kvp.Key)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    // ── Selection: ItemsRepeater realization lifecycle ─────────────────
    // ItemsRepeater recycles containers as the user scrolls. ElementPrepared
    // fires when a container is (re)bound to a row VM; we resolve the row's
    // SelectionOverlay Border, register it in _realizedOverlays, and apply the
    // current selection state so a recycled row shows the right tint.
    //
    // ★ The row is resolved from args.Index, NOT from fe.DataContext. WinUI leaves
    // DataContext null on any x:Bind template (see Panels/Common/RowDataContext.cs);
    // RowDataContext.Supply fills it in from its own ElementPrepared handler, and
    // two handlers on one event have no guaranteed order — so at THIS point in
    // realization only the index is authoritative. The interaction handlers above
    // fire long after realization and read DataContext safely.
    private void OnRowElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement fe) return;
        var source = sender.ItemsSourceView;
        if (source is null || args.Index < 0 || args.Index >= source.Count) return;
        if (source.GetAt(args.Index) is not ScriptRowVm row) return;
        var overlay = FindDescendantByName(fe, "SelectionOverlay") as Border;
        if (overlay is null) return;
        _realizedOverlays[row] = overlay;
        overlay.Visibility = _selectedRows.Contains(row)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ElementClearing fires when a container is recycled off-screen. Drop its
    // overlay registration so RefreshSelectionVisuals doesn't touch a detached
    // element (the same Border instance will be re-registered on the next
    // ElementPrepared for whatever row it's recycled onto).
    private void OnRowElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement fe && fe.DataContext is ScriptRowVm row)
        {
            _realizedOverlays.Remove(row);
        }
    }

    /// <summary>
    /// Depth-first walk of the visual subtree under <paramref name="root"/>
    /// for a named element. Used to reach the per-row SelectionOverlay Border,
    /// which lives inside the ItemsRepeater DataTemplate and so isn't a field
    /// on the View. The row template is shallow (a Grid with one Border + one
    /// Grid of cells), so the walk is a handful of nodes.
    /// </summary>
    private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name)
                return child;
            var nested = FindDescendantByName(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    // Built per-invocation so labels (Enable/Disable) and disabled-state
    // (View Last Error) reflect the current row at the moment of the click.
    //
    // Multi-select (Lane C): the policy submenu applies to *all* selected
    // rows (the right-click already normalized selection so `row` is in the
    // set — see OnRowRightTapped). The per-row actions (Edit / Open / Reveal /
    // Reload / Toggle / View Last Error / Copy Path) stay single-target on the
    // right-clicked row, matching the WinForms baseline where only policy was
    // a batch operation.
    private MenuFlyout BuildContextMenu(ScriptRowVm row)
    {
        var flyout = new MenuFlyout();

        // Effective policy targets: the live selection if the clicked row is
        // part of it, otherwise just the clicked row (defensive — normally
        // already true after OnRowRightTapped). Snapshot to a list so the
        // click handlers don't capture the mutable set.
        var policyTargets = _selectedRows.Contains(row) && _selectedRows.Count > 0
            ? _selectedRows.ToList()
            : new List<ScriptRowVm> { row };

        // Header — script name (or "N scripts" when batch-selected), dimmed
        // and uppercase, non-interactive.
        var header = new MenuFlyoutItem
        {
            Text = policyTargets.Count > 1
                ? string.Format(
                    Localizer.T("panel.scripts.menu.header.multi", "{0} scripts selected"),
                    policyTargets.Count)
                : row.ScriptName,
            IsEnabled = false,
            CharacterSpacing = 100,
        };
        if (Application.Current.Resources.TryGetValue("CoalSecondaryTextBrush", out var dim) && dim is Brush dimBrush)
            header.Foreground = dimBrush;
        flyout.Items.Add(header);
        flyout.Items.Add(new MenuFlyoutSeparator());

        // Edit in Architect — primary action, Ember-tinted. No
        // KeyboardAcceleratorTextOverride: a previous revision labelled
        // it "Ctrl+E", and a later one "Enter", but neither shortcut was
        // ever registered via ScriptView.KeyboardAccelerators.
        // [P3 decision, 2026-06-01] Intentionally NOT wired: the WinForms
        // ScriptMonitorWindow baseline also registered no
        // context-menu accelerators, so leaving them off keeps parity. The
        // override label is omitted rather than half-shown so the menu never
        // advertises a shortcut that doesn't fire. Wire both the accelerator
        // and the label together if shortcuts are ever desired.
        var edit = new MenuFlyoutItem { Text = Localizer.T("panel.scripts.menu.edit_in_architect", "Edit in Architect") };
        if (Application.Current.Resources.TryGetValue("EmberPrimaryBrush", out var em) && em is Brush emBrush)
            edit.Foreground = emBrush;
        edit.Click += async (_, _) => await Safe(() => ViewModel.OpenInArchitectAsync(row));
        flyout.Items.Add(edit);
        var openText = new MenuFlyoutItem { Text = Localizer.T("panel.scripts.menu.open_in_text_editor", "Open in Text Editor") };
        openText.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(row.Status.Path)) return;
            OpenInTextEditor(row.Status.Path);
        };
        flyout.Items.Add(openText);

        var reveal = new MenuFlyoutItem { Text = Localizer.T("panel.scripts.menu.reveal_in_folder", "Reveal in Folder") };
        reveal.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(row.Status.Path)) return;
            RevealInExplorer(row.Status.Path);
        };
        flyout.Items.Add(reveal);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var reload = new MenuFlyoutItem { Text = Localizer.T("panel.scripts.menu.reload_script", "Reload Script") };
        reload.Click += async (_, _) => await Safe(() => ViewModel.ReloadAsync(row));
        flyout.Items.Add(reload);

        var toggle = new MenuFlyoutItem { Text = row.EnableActionLabel };
        toggle.Click += async (_, _) => await Safe(() => ViewModel.ToggleEnabledAsync(row));
        flyout.Items.Add(toggle);

        // Set Policy → Queue / Overlap / Discard.
        // Tracks ScriptManager._executionPolicies; all three policies are
        // enforced at the dispatch gates (IsEnforced). A policy that ever
        // ships un-enforced picks up a parenthesised "(not yet enforced)"
        // tag so users aren't misled about runtime behaviour.
        // Applies to all selected rows (policyTargets).
        flyout.Items.Add(BuildPolicySubMenu(policyTargets));

        flyout.Items.Add(new MenuFlyoutSeparator());

        var viewError = new MenuFlyoutItem
        {
            Text = Localizer.T("panel.scripts.menu.view_last_error", "View Last Error"),
            IsEnabled = row.State == ScriptState.Errored,
        };
        viewError.Click += async (_, _) => await ShowLastErrorAsync(row);
        flyout.Items.Add(viewError);

        var copyPath = new MenuFlyoutItem { Text = Localizer.T("panel.scripts.menu.copy_path", "Copy Path") };
        copyPath.Click += (_, _) =>
        {
            // DataPackage.SetText throws on a null argument, and this handler
            // (unlike OpenInTextEditor / RevealInExplorer) has no try-catch —
            // guard the empty path so the right-click never throws.
            if (string.IsNullOrEmpty(row.Status.Path)) return;
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(row.Status.Path);
                Clipboard.SetContent(pkg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ScriptView", "Copy path failed", ex);
            }
        };
        flyout.Items.Add(copyPath);

        return flyout;
    }

    /// <summary>
    /// "Set Policy → Queue / Overlap / Discard"
    /// submenu. Builds a MenuFlyoutSubItem with three MenuFlyoutItems,
    /// each tagged with a leading "● " for the currently-selected policy
    /// (MenuFlyout has no native radio-item support in WinUI 3 — the
    /// leading dot is the conventional marker).
    ///
    /// Lane C (2026-06-01) — restored the WinForms batch-apply: the submenu
    /// now operates on every row in <paramref name="targets"/>. The "current"
    /// marker reflects the first target's policy (the WinForms picker likewise
    /// ticked the first selected row); applying sets the chosen policy on all
    /// targets in one shot.
    /// </summary>
    private MenuFlyoutSubItem BuildPolicySubMenu(IReadOnlyList<ScriptRowVm> targets)
    {
        var sub = new MenuFlyoutSubItem
        {
            Text = Localizer.T("panel.scripts.menu.set_policy", "Set Policy"),
        };
        ScriptManager.ExecutionPolicy current;
        try
        {
            current = targets.Count > 0
                ? ScriptManager.Instance.GetPolicy(targets[0].Status.Name)
                : ScriptManager.ExecutionPolicy.Queue;
        }
        catch { current = ScriptManager.ExecutionPolicy.Queue; }

        sub.Items.Add(BuildPolicyItem(targets, ScriptManager.ExecutionPolicy.Queue,
            Localizer.T("panel.scripts.menu.policy.queue", "Queue"),
            current));
        sub.Items.Add(BuildPolicyItem(targets, ScriptManager.ExecutionPolicy.Overlap,
            Localizer.T("panel.scripts.menu.policy.overlap", "Overlap"),
            current));
        sub.Items.Add(BuildPolicyItem(targets, ScriptManager.ExecutionPolicy.Discard,
            Localizer.T("panel.scripts.menu.policy.discard", "Discard"),
            current));
        return sub;
    }

    private MenuFlyoutItem BuildPolicyItem(
        IReadOnlyList<ScriptRowVm> targets,
        ScriptManager.ExecutionPolicy policy,
        string label,
        ScriptManager.ExecutionPolicy current)
    {
        // Marker "●" prefixes the currently-selected policy; a policy that
        // ships un-enforced trails "(not yet enforced)" so users aren't
        // surprised when it doesn't change runtime behaviour.
        // ScriptManager.IsEnforced is the source of truth.
        bool selected = current == policy;
        bool enforced = ScriptManager.IsEnforced(policy);
        string text = (selected ? "● " : "    ") + label;
        if (!enforced)
        {
            text += " " + Localizer.T("panel.scripts.menu.policy.not_enforced_suffix", "(not yet enforced)");
        }
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => ApplyPolicyToTargets(targets, policy);
        return item;
    }

    /// <summary>
    /// Lane C (2026-06-01) — restores the WinForms ApplyPolicyToSelected
    /// batch behaviour. Sets <paramref name="policy"/> on every target via
    /// ScriptManager.SetPolicy and emits a single plural-aware System log
    /// matching the pre-T15 contract: "Execution policy set to {policy} for
    /// {count} script(s)." Counts only the rows whose policy was actually
    /// applied (a failed SetPolicy is logged but excluded from the count).
    /// </summary>
    private static void ApplyPolicyToTargets(
        IReadOnlyList<ScriptRowVm> targets,
        ScriptManager.ExecutionPolicy policy)
    {
        int applied = 0;
        foreach (var target in targets)
        {
            string name = target.Status.Name;
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                ScriptManager.Instance.SetPolicy(name, policy);
                applied++;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("ScriptView", $"Set policy failed for '{name}'", ex);
            }
        }

        if (applied > 0)
        {
            GlobalLogger.Log(
                $"Execution policy set to {policy} for {applied} script(s).",
                "ScriptView", Phoenix.Controls.Shared.Models.LogLevel.System);
        }
    }

    private async Task ShowLastErrorAsync(ScriptRowVm row)
    {
        string titleFmt   = Localizer.T("panel.scripts.lasterror.title_format", "{0} — last error");
        string emptyText  = Localizer.T("panel.scripts.lasterror.no_error",     "(no error captured)");
        string closeLabel = Localizer.T("common.button.close",                  "Close");
        // Hub UI sweep 2026-05-22 — RequestedTheme=Dark pins the dialog
        // to the coal/ember chrome; without it WinUI's ContentDialog
        // defaults to ElementTheme.Default which pulls Fluent's
        // light-grey title-bar and button chrome.
        var dlg = new ContentDialog
        {
            Title = string.Format(titleFmt, row.ScriptName),
            Content = new TextBlock
            {
                Text = row.Status.LastError ?? emptyText,
                TextWrapping = TextWrapping.Wrap,
                // Resource *indexing* throws KeyNotFoundException on a
                // missing key; use the TryGetValue + pattern-match guard that
                // the brush lookups above (CoalSecondaryTextBrush /
                // EmberPrimaryBrush) already use, so an absent MonoFont falls
                // back to the literal instead of crashing the dialog setup.
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(
                    (Application.Current.Resources.TryGetValue("MonoFont", out var monoRes)
                        && monoRes is string monoFont)
                        ? monoFont
                        : "JetBrains Mono, Cascadia Code, Consolas, monospace"),
            },
            CloseButtonText = closeLabel,
            XamlRoot = XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };
        await dlg.ShowAsync();
    }

    private static void OpenInTextEditor(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Per feedback_no_modal_dialogs_for_repeatable_rejections.md — log
            // via GlobalLogger instead of popping a dialog. The user will see
            // the failure in the System Log panel.
            GlobalLogger.Error("ScriptView", $"Open in editor failed: {path}", ex);
        }
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ScriptView", $"Reveal in folder failed: {path}", ex);
        }
    }

    private static async Task Safe(Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { GlobalLogger.Error("ScriptView", "Context-menu action failed", ex); }
    }
}
