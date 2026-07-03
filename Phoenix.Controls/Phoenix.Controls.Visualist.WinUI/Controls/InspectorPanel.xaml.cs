using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Clipboard;
using Phoenix.Controls.Visualist.WinUI.Core;
using Phoenix.Controls.Visualist.WinUI.Dialogs;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// InspectorPanel — single right-pane host for layer + widget properties.
/// All edits push undo on the document, mark dirty, and re-broadcast through
/// the VM so the canvas + chrome stay in sync. Per-pillar; not shared with
/// Architect.
/// </summary>
public sealed partial class InspectorPanel : UserControl
{
    private VisualistViewModel? _vm;

    // Suppress feedback loops: when the panel updates its own controls in
    // response to model changes, the resulting xxxChanged events would push
    // duplicate undo entries / re-write the same value. The flag short-circuits
    // those bounce-backs.
    private bool _suppressEcho;

    // Aspect-ratio lock. Captured once at toggle-on
    // time; subsequent W/H edits reapply the ratio so dragging width snaps
    // height proportionally (and vice-versa). _aspectRatio = W/H. Both fields
    // reset when the user un-toggles the chain, or when the selected layer
    // changes (RefreshLayerForm).
    private bool   _aspectLocked;
    private double _aspectRatio = 1.0;

    private static readonly LayerPreset[]   LayerPresets   = (LayerPreset[])Enum.GetValues(typeof(LayerPreset));

    // TRIGGERS section. Bus presence drives the per-trigger Test chips +
    // the offline hint; subscription is paired Loaded/Unloaded. The Test
    // buttons are tracked so a connection flip re-gates them without a full
    // section rebuild. _feedbackTimer clears the transient copy/test confirmation.
    private Action<bool>? _onBusConnChanged;
    private bool _busConnected;
    private readonly List<Button> _testButtons = new();
    private DispatcherTimer? _feedbackTimer;

    // NODE section. The param rows are rebuilt imperatively from
    // VM.SelectedNodeParams (mirrors RefreshTriggersSection). Per-param VM
    // PropertyChanged is tracked so the keyframe diamond + bound controls
    // refresh when a value / IsAnimated flips externally (canvas record,
    // undo, the param's own Commit re-clamp); the subscriptions are dropped
    // on every rebuild + on Unloaded so the panel doesn't leak the params.
    private readonly List<NodeParamVm> _subscribedParams = new();
    // Per-param control echo-suppression: when we push a model value back into
    // a control (slider↔numbox sync, external change) the resulting *Changed
    // event would re-Commit the same value. The per-row builders set/read this
    // around their own writes.
    private bool _suppressNodeEcho;

    // DaVinci keyframe clusters (◀ ◇/◆ ▶) register a lightweight refresh closure
    // here so a playhead move re-evaluates each diamond's on/off-keyframe glyph
    // WITHOUT rebuilding the whole node form (playback ticks PlayheadMs ~30×/s).
    // Cleared + repopulated on every RefreshNodeForm.
    private readonly List<Action> _keyframeRefreshers = new();

    public InspectorPanel()
    {
        InitializeComponent();

        foreach (LayerPreset p in LayerPresets) LayerPresetBox.Items.Add(p.ToString());

        DataContextChanged += OnDataContextChanged;
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Track Hub bus presence so the Test chips + offline hint
        // stay live. OnConnectionStatusChanged fires off the UI thread; the
        // handler marshals before touching XAML.
        if (_onBusConnChanged is null)
        {
            _onBusConnChanged = OnBusConnectionChanged;
            VisualistBusClient.Instance.OnConnectionStatusChanged += _onBusConnChanged;
        }
        ApplyBusState(VisualistBusClient.Instance.IsConnected);
        if (_vm is { } vm) RefreshTriggersSection(vm);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Without this the VM's PropertyChanged keeps the panel pinned via
        // the closure even after it's torn down — silent listener accretion
        // when the inspector is recycled across selection changes.
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            old.NodeBodyCommitted -= OnNodeParamCommittedEcho;
            _vm = null;
        }
        if (_onBusConnChanged is not null)
        {
            try { VisualistBusClient.Instance.OnConnectionStatusChanged -= _onBusConnChanged; } catch { }
            _onBusConnChanged = null;
        }
        try { _feedbackTimer?.Stop(); } catch { }
        // Drop per-param subscriptions so a recycled inspector doesn't
        // leak the NodeParamVm instances through their PropertyChanged.
        UnsubscribeNodeParams();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_vm is { } old)
        {
            old.PropertyChanged -= OnVmPropertyChanged;
            old.NodeBodyCommitted -= OnNodeParamCommittedEcho;
        }
        _vm = args.NewValue as VisualistViewModel;
        if (_vm is { } vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            // When a node-BODY inline pill commits, the canvas raises
            // NodeBodyCommitted; rebuild the NODE section so the right-pane fields
            // mirror the change. The reverse direction (Inspector → node pill) is
            // driven by WidgetEditorView listening to NodeParamCommitted, kept as a
            // separate event so an Inspector edit can't loop back here.
            vm.NodeBodyCommitted += OnNodeParamCommittedEcho;
            RefreshLayerForm(vm);
            RefreshWidgetRoster(vm);
            RefreshWidgetForm(vm);
            RefreshTriggersSection(vm);
            RefreshNodeForm(vm);   // typed per-node inspector
        }
    }

    // Echo a node-body pill edit into the Inspector's NODE form. Only
    // rebuilds when the committed node is the one currently shown, and the
    // RefreshNodeForm rebuild is guarded (_suppressEcho / _suppressNodeEcho) so it
    // can't re-fire a commit and loop.
    private void OnNodeParamCommittedEcho(Node node)
    {
        if (_vm is not { } vm) return;
        if (!ReferenceEquals(node, vm.SelectedNode)) return;
        RefreshNodeForm(vm);
    }

    /// <summary>
    /// Driven by MainView.SelectSubTab. In Widget Editor mode the LAYER
    /// settings section is collapsed (resolution/preset/name are irrelevant while
    /// editing a widget — "why do I need layer settings when editing a widget?").
    /// The WIDGETS roster (the in-editor widget switcher) and the WIDGET context
    /// stay. Layer Canvas mode restores the full pane.
    ///
    /// Additionally collapse the read-only geometry mirrors
    /// (x/y/w/h/preset) in widget-editor mode: they're stale noise while
    /// authoring a node graph (the live values + edits live on the canvas pills).
    /// The editable widget NAME and z-index stay, so this is a partial collapse
    /// of WidgetForm — not the whole form.
    /// </summary>
    public void SetEditorContext(bool widgetEditing)
    {
        var vis = widgetEditing ? Visibility.Collapsed : Visibility.Visible;
        if (LayerSection is not null)         LayerSection.Visibility         = vis;
        if (LayerSectionDivider is not null)  LayerSectionDivider.Visibility  = vis;
        // Hide the dead geometry mirrors while editing a widget;
        // keep name + z-index editors visible.
        if (WidgetGeometryMirrors is not null) WidgetGeometryMirrors.Visibility = vis;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not VisualistViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(VisualistViewModel.SelectedLayer):
                RefreshLayerForm(vm);
                RefreshWidgetRoster(vm);   // widget set may have changed
                RefreshWidgetForm(vm);
                RefreshTriggersSection(vm); // save can mint the LayerID
                break;
            case nameof(VisualistViewModel.SelectedWidget):
                RefreshWidgetRoster(vm);   // sync the highlighted row
                RefreshWidgetForm(vm);
                RefreshTriggersSection(vm); // IDs + per-trigger chips
                // A widget switch implicitly drops the node selection — the VM
                // nulls SelectedNode, but rebuild defensively so a stale NODE
                // section never lingers over the wrong widget.
                RefreshNodeForm(vm);
                break;
            case nameof(VisualistViewModel.SelectedNode):
                // The widget-graph canvas routes node selection into
                // VM.SelectedNode; the VM rebuilds SelectedNodeParams.
                // Re-render the NODE section against the new selection.
                RefreshNodeForm(vm);
                break;
            case nameof(VisualistViewModel.PlayheadMs):
                // Playhead moved (scrub / playback / prev-next) — refresh the
                // keyframe diamonds' on/off state without rebuilding the form.
                RefreshKeyframeClusters();
                break;
        }
    }

    // Re-run each registered keyframe-cluster refresher (lightweight glyph/colour
    // update). Called on PlayheadMs changes; safe when empty.
    private void RefreshKeyframeClusters()
    {
        if (_keyframeRefreshers.Count == 0) return;
        try { foreach (Action r in _keyframeRefreshers) r(); }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "RefreshKeyframeClusters", ex); }
    }

    // ─── layer form ─────────────────────────────────────────────────────

    private void RefreshLayerForm(VisualistViewModel vm)
    {
        Layer? layer = vm.SelectedLayer;
        bool enabled = layer is not null;
        SetChildrenEnabled(LayerForm, enabled);

        // "Copy OBS URL" is layer-level and lives in the LAYER
        // section now. Gate it on a saved layer (LayerID = .phxlayer file stem)
        // and show the unsaved hint when it isn't actionable yet.
        bool savedLayer = ResolveLayerId(vm) is not null;
        if (CopyObsUrlButton is not null)   CopyObsUrlButton.IsEnabled = enabled && savedLayer;
        if (LayerObsUnsavedHint is not null)
            LayerObsUnsavedHint.Visibility = (enabled && !savedLayer) ? Visibility.Visible : Visibility.Collapsed;

        if (!enabled) return;

        _suppressEcho = true;
        try
        {
            LayerNameBox.Text       = layer!.Name;
            LayerWidthBox.Value     = layer.Resolution.Width;
            LayerHeightBox.Value    = layer.Resolution.Height;
            LayerPresetBox.SelectedIndex = Array.IndexOf(LayerPresets, layer.Preset);
            // Clear the chain-link state on layer change. The captured
            // aspect ratio is layer-specific; carrying it across layer switches
            // would silently rescale the next layer's height on the first
            // width edit. Resetting to "unlocked" matches what a user sees.
            _aspectLocked = false;
            _aspectRatio  = 1.0;
            if (AspectLockToggle is not null) AspectLockToggle.IsChecked = false;
        }
        finally { _suppressEcho = false; }
    }

    private void OnLayerNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEcho || _vm?.SelectedLayer is not { } layer) return;
        if (string.Equals(layer.Name, LayerNameBox.Text, StringComparison.Ordinal)) return;
        _vm.Document?.PushUndo();
        layer.Name = LayerNameBox.Text;
        _vm.Document?.MarkDirty();
        // The layer name appears in the canvas resolution badge
        // ("$name · WxH"); refresh so renames land immediately.
        _vm.RaiseSelectedLayerChanged();
    }

    private async void OnLayerWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressEcho || _vm?.SelectedLayer is not { } layer) return;
        if (double.IsNaN(args.NewValue)) return;
        int newW = (int)Math.Round(args.NewValue);
        if (layer.Resolution.Width == newW) return;

        // When the chain-link is on, mirror the height to the captured
        // aspect ratio. Use _suppressEcho around the LayerHeightBox.Value write
        // so the OnLayerHeightChanged handler doesn't push a second undo /
        // re-fire the same mutation.
        int oldW = layer.Resolution.Width;
        int oldH = layer.Resolution.Height;
        int newH = oldH;
        if (_aspectLocked && _aspectRatio > 0)
        {
            newH = Math.Max(1, (int)Math.Round(newW / _aspectRatio));
        }

        _vm.Document?.PushUndo();
        layer.Resolution.Width = newW;
        if (_aspectLocked && newH != layer.Resolution.Height)
        {
            layer.Resolution.Height = newH;
            _suppressEcho = true;
            try { LayerHeightBox.Value = newH; }
            finally { _suppressEcho = false; }
        }
        // Auto-flip the preset combo to Custom when the user manually
        // edits W/H to a value that doesn't match any preset. MatchPreset
        // returns Custom for any (W,H) pair that isn't on the canonical list,
        // so this is just a single equality check from the layer side. Mirror
        // the new preset into the ComboBox via _suppressEcho so we don't
        // re-enter OnLayerPresetChanged.
        ApplyPresetFromResolution(layer);
        // User-initiated resolution change. Prompt
        // for Yes / No / Cancel when widgets overflow the new bounds. Cancel
        // reverts; Yes proportional-scales; No leaves widgets in place + logs.
        await HandleResolutionChangeAsync(layer, oldW, oldH);
        _vm.Document?.MarkDirty();
        // Resolution changes need to ripple to the canvas so the
        // WidgetSurface and resolution badge refresh without a re-selection.
        // SelectedLayer is the same reference so the property's own setter
        // short-circuits on equality; RaiseSelectedLayerChanged forces the
        // PropertyChanged broadcast that LayerCanvasView / chrome listen for.
        _vm.RaiseSelectedLayerChanged();
    }

    private async void OnLayerHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressEcho || _vm?.SelectedLayer is not { } layer) return;
        if (double.IsNaN(args.NewValue)) return;
        int newH = (int)Math.Round(args.NewValue);
        if (layer.Resolution.Height == newH) return;

        // Symmetric of OnLayerWidthChanged: mirror width when locked.
        int oldW = layer.Resolution.Width;
        int oldH = layer.Resolution.Height;
        int newW = oldW;
        if (_aspectLocked && _aspectRatio > 0)
        {
            newW = Math.Max(1, (int)Math.Round(newH * _aspectRatio));
        }

        _vm.Document?.PushUndo();
        layer.Resolution.Height = newH;
        if (_aspectLocked && newW != layer.Resolution.Width)
        {
            layer.Resolution.Width = newW;
            _suppressEcho = true;
            try { LayerWidthBox.Value = newW; }
            finally { _suppressEcho = false; }
        }
        // See OnLayerWidthChanged.
        ApplyPresetFromResolution(layer);
        await HandleResolutionChangeAsync(layer, oldW, oldH);
        _vm.Document?.MarkDirty();
        // See OnLayerWidthChanged.
        _vm.RaiseSelectedLayerChanged();
    }

    private async void OnLayerPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEcho || _vm?.SelectedLayer is not { } layer) return;
        int idx = LayerPresetBox.SelectedIndex;
        if (idx < 0 || idx >= LayerPresets.Length) return;
        LayerPreset newPreset = LayerPresets[idx];
        if (layer.Preset == newPreset) return;
        LayerPreset oldPreset = layer.Preset;
        int oldW = layer.Resolution.Width;
        int oldH = layer.Resolution.Height;
        _vm.Document?.PushUndo();
        layer.Preset = newPreset;

        // When the new preset is one of the named
        // sizes, push the canonical (W,H) into Resolution and reflect into
        // the NumberBoxes. Custom keeps the existing values per spec — callers
        // are expected to interpret the (0,0) sentinel from
        // LayerPresetExtensions.ToResolution() as "no change".
        if (newPreset != LayerPreset.Custom)
        {
            var (w, h) = newPreset.ToResolution();
            if (w > 0 && h > 0)
            {
                bool widthChanged  = layer.Resolution.Width  != w;
                bool heightChanged = layer.Resolution.Height != h;
                layer.Resolution.Width  = w;
                layer.Resolution.Height = h;
                _suppressEcho = true;
                try
                {
                    if (widthChanged)  LayerWidthBox.Value  = w;
                    if (heightChanged) LayerHeightBox.Value = h;
                }
                finally { _suppressEcho = false; }
                // Preset switch is just as much a resolution change as a
                // manual W/H edit; route through the same prompt. If the user
                // cancels we restore the preset selection too, not just the
                // numeric resolution.
                bool reverted = await HandleResolutionChangeAsync(layer, oldW, oldH);
                if (reverted)
                {
                    layer.Preset = oldPreset;
                    int oldIdx = Array.IndexOf(LayerPresets, oldPreset);
                    if (oldIdx >= 0)
                    {
                        _suppressEcho = true;
                        try { LayerPresetBox.SelectedIndex = oldIdx; }
                        finally { _suppressEcho = false; }
                    }
                }
            }
        }

        _vm.Document?.MarkDirty();
        // Preset change can recolour / re-letterbox the canvas
        // backdrop too; same refresh path as the resolution edits.
        _vm.RaiseSelectedLayerChanged();
    }

    // ─── aspect-ratio lock ──────────────────────────────────────────

    private void OnAspectLockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        _aspectLocked = tb.IsChecked == true;
        if (_aspectLocked && _vm?.SelectedLayer is { } layer)
        {
            int w = layer.Resolution.Width;
            int h = layer.Resolution.Height;
            // Guard against div-by-zero on a freshly-created layer whose H is
            // still 0 — bail to a safe 1:1 ratio rather than locking in NaN.
            _aspectRatio = (w > 0 && h > 0) ? (double)w / h : 1.0;
        }
    }

    // ─── resolution helpers ──────────────────────────────────────────────

    private void ApplyPresetFromResolution(Layer layer)
    {
        LayerPreset matched = LayerPresetExtensions.MatchPreset(
            layer.Resolution.Width, layer.Resolution.Height);
        if (layer.Preset == matched) return;
        layer.Preset = matched;
        int idx = Array.IndexOf(LayerPresets, matched);
        if (idx < 0) return;
        _suppressEcho = true;
        try { LayerPresetBox.SelectedIndex = idx; }
        finally { _suppressEcho = false; }
    }

    /// <summary>
    /// User-initiated resolution change handler.
    ///
    /// Called AFTER the new resolution has been written to <paramref
    /// name="layer"/>. Walks the widget list; if any widget's Rect overflows
    /// the new bounds, shows a Yes / No / Cancel <see cref="ContentDialog"/>
    /// asking whether to rescale them proportionally. Per the audit spec a
    /// resolution change is a user-initiated decision, so the modal is
    /// appropriate here — this is NOT a repeatable rejection, so the
    /// no-modal-dialogs guideline does not apply.
    ///
    /// Returns <c>true</c> when the user picked Cancel and the resolution
    /// was reverted to (<paramref name="oldW"/>, <paramref name="oldH"/>);
    /// callers should reflect the revert in any sibling controls (preset
    /// combo, NumberBoxes).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> HandleResolutionChangeAsync(Layer layer, int oldW, int oldH)
    {
        int newW = layer.Resolution.Width;
        int newH = layer.Resolution.Height;
        if (newW <= 0 || newH <= 0) return false;

        // Fast path — no overflow means silent change per spec.
        bool anyOverflow = false;
        foreach (LayerWidget w in layer.Widgets)
        {
            if (w.Rect.X + w.Rect.Width > newW || w.Rect.Y + w.Rect.Height > newH)
            {
                anyOverflow = true;
                break;
            }
        }
        if (!anyOverflow) return false;

        if (XamlRoot is null)
        {
            // No XamlRoot to host the dialog — log + leave widgets in place
            // (safest fallback; we already wrote the resolution change and the
            // user didn't get a chance to confirm proportional rescale).
            foreach (LayerWidget w in layer.Widgets)
            {
                if (w.Rect.X + w.Rect.Width > newW || w.Rect.Y + w.Rect.Height > newH)
                {
                    GlobalLogger.Log(
                        $"Visualist: widget '{w.Name}' ({w.Rect.X},{w.Rect.Y} {w.Rect.Width}×{w.Rect.Height}) " +
                        $"overflows new resolution {newW}×{newH}.",
                        source: "InspectorPanel",
                        level: LogLevel.Communication);
                }
            }
            return false;
        }

        var dlg = new ContentDialog
        {
            XamlRoot            = XamlRoot,
            Title               = "Rescale widgets?",
            Content             =
                $"Some widgets are larger than the new resolution ({newW}×{newH}). " +
                "Rescale them proportionally?",
            PrimaryButtonText   = "Yes",
            SecondaryButtonText = "No",
            CloseButtonText     = "Cancel",
            DefaultButton       = ContentDialogButton.Primary,
        };
        ContentDialogResult res;
        try
        {
            res = await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorPanel", "HandleResolutionChangeAsync ShowAsync", ex);
            return false;
        }

        if (res == ContentDialogResult.Primary)
        {
            // Yes — proportional rescale. Compute the scale ratios from the
            // resolution that was in place BEFORE this change and apply to
            // every widget's Rect (X/Y/W/H all scale, not just W/H — a widget
            // anchored to the bottom-right of FullHD should ride the corner
            // when the layer shifts to QHD).
            double sx = oldW > 0 ? (double)newW / oldW : 1.0;
            double sy = oldH > 0 ? (double)newH / oldH : 1.0;
            int rescaled = 0;
            foreach (LayerWidget w in layer.Widgets)
            {
                WidgetRect r = w.Rect;
                int nx = Math.Max(0, (int)Math.Round(r.X      * sx));
                int ny = Math.Max(0, (int)Math.Round(r.Y      * sy));
                int nw = Math.Max(1, (int)Math.Round(r.Width  * sx));
                int nh = Math.Max(1, (int)Math.Round(r.Height * sy));
                // Final guard so a rounding artefact at the right/bottom edge
                // doesn't leave a pixel sticking out past the new resolution.
                if (nx + nw > newW) nw = Math.Max(1, newW - nx);
                if (ny + nh > newH) nh = Math.Max(1, newH - ny);
                r.X = nx; r.Y = ny; r.Width = nw; r.Height = nh;
                rescaled++;
            }
            if (rescaled > 0)
            {
                GlobalLogger.Log(
                    $"Visualist: rescaled {rescaled} widget(s) proportionally to new resolution {newW}×{newH}.",
                    source: "InspectorPanel",
                    level: LogLevel.Communication);
            }
            return false;
        }

        if (res == ContentDialogResult.Secondary)
        {
            // No — leave widgets in place, log a one-liner per overflowing
            // widget so the user can find the offenders later.
            foreach (LayerWidget w in layer.Widgets)
            {
                if (w.Rect.X + w.Rect.Width > newW || w.Rect.Y + w.Rect.Height > newH)
                {
                    GlobalLogger.Log(
                        $"Visualist: widget '{w.Name}' ({w.Rect.X},{w.Rect.Y} {w.Rect.Width}×{w.Rect.Height}) " +
                        $"is larger than new resolution {newW}×{newH} — left in place.",
                        source: "InspectorPanel",
                        level: LogLevel.Communication);
                }
            }
            return false;
        }

        // Cancel — revert the resolution to (oldW, oldH) and sync the
        // NumberBoxes so the user's edit doesn't visibly stick.
        layer.Resolution.Width  = oldW;
        layer.Resolution.Height = oldH;
        _suppressEcho = true;
        try
        {
            LayerWidthBox.Value  = oldW;
            LayerHeightBox.Value = oldH;
        }
        finally { _suppressEcho = false; }
        // Preset combo may be wrong now (we ran ApplyPresetFromResolution on
        // the NEW size before prompting) — re-sync from the restored size so
        // the inspector's preset label matches reality.
        ApplyPresetFromResolution(layer);
        return true;
    }

    // ─── widget roster ───────────────────────────────────────────────
    //
    // Restores the pre-WinUI _widgetTree picker (left tree in the WinForms
    // LayerDocumentForm). Imperatively rebuilt from the layer's Widgets — the
    // list is a plain List, not observable, so we re-read it whenever
    // SelectedLayer / SelectedWidget fires (every widget-mutating VM path raises
    // one of those). Visualist-local.

    private void RefreshWidgetRoster(VisualistViewModel vm)
    {
        if (WidgetRosterList is null) return;
        var widgets = vm.SelectedLayer?.Widgets;
        _suppressEcho = true;
        try
        {
            WidgetRosterList.Items.Clear();
            if (widgets is not null)
            {
                foreach (LayerWidget w in widgets)
                {
                    string label  = string.IsNullOrEmpty(w.Name) ? "(unnamed)" : w.Name;
                    string preset = w.Preset?.ToString() ?? "—";
                    WidgetRosterList.Items.Add(new ListViewItem
                    {
                        Content  = $"{label}   ·   {preset}   ·   z{w.ZIndex}",
                        Tag      = w,
                        FontSize = 12,
                        Padding  = new Thickness(8, 2, 8, 2),
                        MinHeight = 0,
                    });
                }
            }
            // Highlight the currently-selected widget's row.
            if (vm.SelectedWidget is { } sel)
            {
                foreach (var item in WidgetRosterList.Items)
                {
                    if (item is ListViewItem lvi && ReferenceEquals(lvi.Tag, sel))
                    {
                        WidgetRosterList.SelectedItem = lvi;
                        break;
                    }
                }
            }
            else
            {
                WidgetRosterList.SelectedItem = null;
            }
        }
        finally { _suppressEcho = false; }

        bool any = widgets is { Count: > 0 };
        WidgetRosterEmptyHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        WidgetRosterList.Visibility      = any ? Visibility.Visible    : Visibility.Collapsed;
    }

    private void OnWidgetRosterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEcho || _vm is null) return;
        if (WidgetRosterList.SelectedItem is ListViewItem lvi && lvi.Tag is LayerWidget w)
            _vm.SelectedWidget = w;
    }

    private void OnWidgetRosterDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // Double-click a roster row enters that widget's editor (parity with
        // the pre-WinUI widget-tree NodeMouseDoubleClick → OpenWidgetEditor).
        if (_vm?.SelectedWidget is { } w) FindPillarMainView()?.EnterWidget(w);
    }

    // Visualist-local visual-tree walk to the embedding MainView (copied,
    // not lifted, to keep the pillar's chrome independent).
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

    // ─── widget form ────────────────────────────────────────────────────

    private void RefreshWidgetForm(VisualistViewModel vm)
    {
        LayerWidget? widget = vm.SelectedWidget;
        bool selected = widget is not null;

        WidgetForm.Visibility       = selected ? Visibility.Visible  : Visibility.Collapsed;
        WidgetEmptyHint.Visibility  = selected ? Visibility.Collapsed : Visibility.Visible;
        DeleteWidgetButton.IsEnabled = selected;
        if (!selected) return;

        _suppressEcho = true;
        try
        {
            WidgetNameBox.Text   = widget!.Name;
            // x/y/w/h/preset are read-only mirrors of the
            // inline pills on the widget body (Canvas/WidgetView.xaml). The
            // editable affordance is the inline pill row.
            WidgetXMirror.Text      = widget.Rect.X.ToString();
            WidgetYMirror.Text      = widget.Rect.Y.ToString();
            WidgetWidthMirror.Text  = widget.Rect.Width.ToString();
            WidgetHeightMirror.Text = widget.Rect.Height.ToString();
            WidgetPresetMirror.Text = widget.Preset?.ToString() ?? "(none)";
            WidgetZIndexBox.Value   = widget.ZIndex;
            // Per-widget dip-to-blank transition — model is milliseconds (0–1000);
            // the slider/box edit in SECONDS (0–1).
            double tsec = Math.Clamp(widget.TransitionMs / 1000.0, 0, 1);
            WidgetTransitionSlider.Value = tsec;
            WidgetTransitionBox.Value    = tsec;
        }
        finally { _suppressEcho = false; }
    }

    // Slider + NumberBox edit the transition in seconds and stay in sync; the
    // model stores milliseconds. Mirrors the OnWidgetZIndexChanged undo/dirty
    // pattern (PushUndo → write → MarkDirty), no-op on an unchanged value.
    private void OnWidgetTransitionSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        => ApplyWidgetTransitionSeconds(e.NewValue, syncSlider: false, syncBox: true);

    private void OnWidgetTransitionBoxChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        ApplyWidgetTransitionSeconds(args.NewValue, syncSlider: true, syncBox: false);
    }

    private void ApplyWidgetTransitionSeconds(double seconds, bool syncSlider, bool syncBox)
    {
        if (_suppressEcho || _vm?.SelectedWidget is not { } widget) return;
        double sec = Math.Clamp(double.IsNaN(seconds) ? 0 : seconds, 0, 1);
        double ms  = sec * 1000.0;
        if (Math.Abs(widget.TransitionMs - ms) < 0.5) return;
        _suppressEcho = true;
        try
        {
            if (syncSlider) WidgetTransitionSlider.Value = sec;
            if (syncBox)    WidgetTransitionBox.Value    = sec;
        }
        finally { _suppressEcho = false; }
        _vm.Document?.PushUndo();
        widget.TransitionMs = ms;
        _vm.Document?.MarkDirty();
    }

    private void OnWidgetNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEcho || _vm?.SelectedWidget is not { } widget) return;
        if (string.Equals(widget.Name, WidgetNameBox.Text, StringComparison.Ordinal)) return;
        _vm.Document?.PushUndo();
        widget.Name = WidgetNameBox.Text;
        _vm.Document?.MarkDirty();
        // Tab label / canvas footer follow widget name — re-fire SelectedWidget.
        _vm.SelectedWidget = widget;
    }

    private void OnWidgetZIndexChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressEcho || _vm?.SelectedWidget is not { } widget) return;
        if (double.IsNaN(args.NewValue)) return;
        int newZ = (int)Math.Round(args.NewValue);
        if (widget.ZIndex == newZ) return;
        _vm.Document?.PushUndo();
        widget.ZIndex = newZ;
        _vm.Document?.MarkDirty();
    }

    private void OnDeleteWidget(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.DeleteSelectedWidget()) TriggerLayerRefresh();
    }

    /// <summary>
    /// LayerCanvasView listens for SelectedLayer / SelectedWidget changes to
    /// re-render. Re-firing SelectedLayer here is the cheapest way to push a
    /// rect/preset edit through to the canvas without adding a separate
    /// LayerInvalidated event. (The existing setter is private, so we round-
    /// trip via the public alias on the VM.)
    /// </summary>
    private void TriggerLayerRefresh()
    {
        if (_vm is null) return;
        // Bumping SelectedWidget to the same value won't fire (Set short-circuits
        // on equality), but the canvas listens for SelectedLayer changes too,
        // and the document has just been mutated — RaiseSelectedLayerChanged
        // does the no-arg invalidation.
        _vm.RaiseSelectedLayerChanged();
    }

    /// <summary>
    /// WinUI Grid is a Panel, not a Control, so it has no IsEnabled. Toggle
    /// the leaf controls directly to gray the form out when no layer/widget
    /// is selected.
    /// </summary>
    private static void SetChildrenEnabled(Panel panel, bool enabled)
    {
        foreach (UIElement child in panel.Children)
            if (child is Control ctl) ctl.IsEnabled = enabled;
    }

    // ─── TRIGGERS section ────────────────────────────────
    //
    // Per-widget trigger management: a list of the widget's triggers (each row =
    // name + Test / Copy / Delete) plus a "New Trigger" button. Copy emits an
    // Architect Visual.Trigger snippet; Test fires the trigger live over the Hub
    // bus; Delete removes it. The raw Layer-ID / Widget-ID textboxes were removed
    // and "Copy OBS URL" moved to the LAYER section (layer-level). onStartup is
    // the idle loop — auto-fires, can't be Architect-triggered, must not be
    // deletable — so its row hides Test / Copy / Delete and shows a hint instead.
    // Per-pillar chrome; section rebuilt imperatively (Triggers is a plain List,
    // not observable). The Test chips gate on a saved layer + live Hub bus; the
    // offline hint stays.

    // onStartup is the auto-firing idle loop; its row is read-only (no Test /
    // Copy / Delete). Case-insensitive to match WidgetTrigger.IsValidName.
    private const string StartupTriggerName = "onStartup";

    private void RefreshTriggersSection(VisualistViewModel vm)
    {
        if (TriggersSection is null) return;
        LayerWidget? widget = vm.SelectedWidget;
        bool hasWidget = widget is not null;

        TriggersSection.Visibility        = hasWidget ? Visibility.Visible : Visibility.Collapsed;
        TriggersSectionDivider.Visibility = hasWidget ? Visibility.Visible : Visibility.Collapsed;
        _testButtons.Clear();
        TriggerRowsPanel.Children.Clear();
        if (!hasWidget) return;

        // Saved layer = stable LayerID (the .phxlayer file stem). The Copy snippet
        // + Test chips need it; New Trigger does not (creation is in-memory).
        bool saved = ResolveLayerId(vm) is not null;

        var triggers = widget!.Triggers;
        if (triggers.Count == 0)
        {
            TriggersEmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            TriggersEmptyHint.Visibility = Visibility.Collapsed;
            foreach (WidgetTrigger t in triggers)
                TriggerRowsPanel.Children.Add(BuildTriggerRow(t, saved));
        }

        UpdateTriggerTestButtons();
    }

    private FrameworkElement BuildTriggerRow(WidgetTrigger trigger, bool saved)
    {
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text         = trigger.Name,
            FontFamily   = MonoFontFamily(),
            FontSize     = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground   = (Brush)Application.Current.Resources["CoalBodyTextBrush"],
        };
        ToolTipService.SetToolTip(name, trigger.Name);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // onStartup is the idle loop: it auto-fires on layer load
        // and can't be Architect-triggered, so it gets NO Test / Copy / Delete.
        // Show a tiny "auto-fires on load" hint in their place instead.
        bool isStartup = string.Equals(trigger.Name, StartupTriggerName, StringComparison.OrdinalIgnoreCase);
        if (isStartup)
        {
            var hint = new TextBlock
            {
                Text         = "auto-fires on load",
                FontFamily   = MonoFontFamily(),
                FontSize     = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground   = (Brush)Application.Current.Resources["CoalMutedTextBrush"],
            };
            ToolTipService.SetToolTip(hint, "onStartup is the idle loop — it runs automatically when the layer loads and can't be triggered from Architect.");
            Grid.SetColumn(hint, 1);
            Grid.SetColumnSpan(hint, 3);
            grid.Children.Add(hint);
            return grid;
        }

        var test = MakeChip("Test", "Run this trigger on the live OBS source (Hub bus)");
        var capturedForTest = trigger;
        test.Click += (s, e) => OnTestTrigger(capturedForTest);
        _testButtons.Add(test);
        Grid.SetColumn(test, 1);
        grid.Children.Add(test);

        var copy = MakeChip("Copy", "Copy an Architect Visual.Trigger snippet for this trigger");
        copy.IsEnabled = saved;
        var capturedForCopy = trigger;
        copy.Click += (s, e) => OnCopyTriggerSnippet(capturedForCopy);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(copy);

        var delete = MakeChip("Delete", "Delete this trigger (undoable)");
        delete.Foreground = (Brush)Application.Current.Resources["ErrBrush"];
        var capturedForDelete = trigger;
        delete.Click += (s, e) => OnDeleteTrigger(capturedForDelete);
        Grid.SetColumn(delete, 3);
        grid.Children.Add(delete);

        return grid;
    }

    private static Button MakeChip(string text, string tip)
    {
        var b = new Button
        {
            Content         = text,
            FontSize        = 10,
            Padding         = new Thickness(8, 2, 8, 2),
            MinWidth        = 0,
            Background      = (Brush)Application.Current.Resources["CoalCardBrush"],
            BorderThickness = new Thickness(0),
            Foreground      = (Brush)Application.Current.Resources["CoalBodyTextBrush"],
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    // [FONTCAST] MonoFont is an <x:String> resource; a direct (FontFamily) cast
    // throws — wrap the string in a FontFamily (mirrors WidgetEditorView).
    private static FontFamily MonoFontFamily()
        => new FontFamily(Application.Current.Resources["MonoFont"] as string ?? "Consolas");

    // Hub serves overlays under /layer/<file-stem> and LayerRegistry keys by the
    // same stem — so the LayerID is the .phxlayer filename without extension, not
    // Layer.Name. Null for an unsaved document.
    private static string? ResolveLayerId(VisualistViewModel? vm)
    {
        string? path = vm?.Document?.FilePath;
        if (string.IsNullOrEmpty(path)) return null;
        return System.IO.Path.GetFileNameWithoutExtension(path);
    }

    private void OnCopyObsUrl(object sender, RoutedEventArgs e)
    {
        // OBS URL is layer-level (lives in the LAYER section).
        if (ResolveLayerId(_vm) is not { } id)
        {
            ShowTriggerFeedback("Save the layer first to get its OBS URL.", warn: true);
            return;
        }
        string url = $"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(id)}";
        CopyPlainText(url);
        ShowTriggerFeedback("Copied OBS URL.");
    }

    // User-initiated trigger create. Prompt for a name (modal OK,
    // since this is a user-initiated decision, not a repeatable rejection), then
    // VM.AddTrigger. A null return = dup / invalid name (per WidgetTrigger.
    // IsValidName) → non-blocking inline warning, NOT a modal error.
    private async void OnCreateTrigger(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.SelectedWidget is null)
        {
            ShowTriggerFeedback("Select a widget before adding a trigger.", warn: true);
            return;
        }
        string? name = await PromptForName("New Trigger", "onTrigger:new", "");
        if (string.IsNullOrWhiteSpace(name)) return;   // user cancelled / empty
        WidgetTrigger? added = _vm.AddTrigger(name, out var status);
        if (added is null)
        {
            ShowTriggerFeedback(status switch
            {
                VisualistViewModel.AddTriggerStatus.Duplicate =>
                    $"A trigger named \"{VisualistViewModel.NormalizeTriggerName(name)}\" already exists.",
                VisualistViewModel.AddTriggerStatus.NoWidget =>
                    "Select a widget before adding a trigger.",
                _ =>
                    $"\"{name.Trim()}\" isn't a valid trigger name — use letters, digits and underscores (e.g. \"raid\" becomes onTrigger:raid).",
            }, warn: true);
            return;
        }
        RefreshTriggersSection(_vm);
        ShowTriggerFeedback($"Added trigger: {added.Name}");
    }

    // Delete a trigger. onStartup rows never render a Delete chip,
    // so this only ever fires for deletable triggers. Removal is undoable (the VM
    // pushes undo); per the no-modal rule a trigger delete is a frequent,
    // reversible action, so we delete inline + confirm via feedback rather than
    // gating behind a confirmation dialog.
    private void OnDeleteTrigger(WidgetTrigger trigger)
    {
        if (_vm is null) return;
        if (_vm.RemoveTrigger(trigger.Name))
        {
            RefreshTriggersSection(_vm);
            ShowTriggerFeedback($"Deleted trigger: {trigger.Name}");
        }
        else
        {
            ShowTriggerFeedback("Delete failed — see System Log.", warn: true);
        }
    }

    // Modal name prompt — user-initiated create, so a modal is appropriate here
    // (not a repeatable rejection). Mirrors WidgetEditorView.PromptForName; the
    // MonoFont resource is an <x:String> so it must be wrapped, not cast.
    private async System.Threading.Tasks.Task<string?> PromptForName(string title, string placeholder, string initial)
    {
        if (XamlRoot is null) return null;
        var input = new TextBox
        {
            PlaceholderText = placeholder,
            Text            = initial,
            FontFamily      = MonoFontFamily(),
        };
        var dlg = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = title,
            Content           = input,
            PrimaryButtonText = "OK",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
        };
        try
        {
            var res = await dlg.ShowAsync();
            return res == ContentDialogResult.Primary ? input.Text : null;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorPanel", "PromptForName ShowAsync", ex);
            return null;
        }
    }

    private void OnCopyTriggerSnippet(WidgetTrigger trigger)
    {
        LayerWidget? widget = _vm?.SelectedWidget;
        string? layerPath = _vm?.Document?.FilePath;
        bool ok = VisualTriggerSnippetProducer.CopyToClipboard(layerPath, widget, trigger);
        ShowTriggerFeedback(ok ? $"Copied snippet: {trigger.Name}" : "Copy failed — see System Log.", warn: !ok);
    }

    private async void OnTestTrigger(WidgetTrigger trigger)
    {
        if (!VisualistBusClient.Instance.IsConnected)
        {
            ShowTriggerFeedback("Test unavailable — Hub bus offline.", warn: true);
            return;
        }
        if (ResolveLayerId(_vm) is not { } layerId)
        {
            ShowTriggerFeedback("Test unavailable — save the layer first.", warn: true);
            return;
        }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id))
        {
            ShowTriggerFeedback("Test unavailable — no widget selected.", warn: true);
            return;
        }
        try
        {
            await VisualistBusClient.Instance.SendVisualTriggerAsync(layerId, widget.Id, trigger.Name);
            ShowTriggerFeedback($"Test Run sent: {trigger.Name}");
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorPanel", "Test trigger", ex);
            ShowTriggerFeedback($"Test failed: {ex.Message}", warn: true);
        }
        finally { UpdateTriggerTestButtons(); }
    }

    private static void CopyPlainText(string text)
    {
        try
        {
            var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dp.SetText(text);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "CopyPlainText", ex); }
    }

    // Bus presence gates the Test chips; the offline hint explains why
    // they're greyed (non-modal, persistent — matches the disabled affordance).
    private void OnBusConnectionChanged(bool connected)
    {
        try { DispatcherQueue?.TryEnqueue(() => ApplyBusState(connected)); }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "OnBusConnectionChanged", ex); }
    }

    private void ApplyBusState(bool connected)
    {
        _busConnected = connected;
        if (BusOfflineHint is not null)
            BusOfflineHint.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        UpdateTriggerTestButtons();
    }

    private void UpdateTriggerTestButtons()
    {
        // Bring the button gate into parity with OnTestTrigger's
        // defensive checks: a widget can be selected yet have an empty Id (the
        // model allows it), in which case OnTestTrigger would bail with an error.
        // Gate the IsEnabled on the same widget.Id-not-empty guard so the button
        // appearance matches what the click would actually do.
        bool canTest = _busConnected
                    && ResolveLayerId(_vm) is not null
                    && _vm?.SelectedWidget is not null
                    && !string.IsNullOrEmpty(_vm?.SelectedWidget?.Id);
        foreach (Button b in _testButtons) b.IsEnabled = canTest;
    }

    private void ShowTriggerFeedback(string message, bool warn = false)
    {
        if (TriggerActionFeedback is null) return;
        TriggerActionFeedback.Text       = message;
        TriggerActionFeedback.Foreground = (Brush)Application.Current.Resources[warn ? "ErrBrush" : "OkBrush"];
        TriggerActionFeedback.Visibility = Visibility.Visible;
        _feedbackTimer ??= CreateFeedbackTimer();
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private DispatcherTimer CreateFeedbackTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (TriggerActionFeedback is not null) TriggerActionFeedback.Visibility = Visibility.Collapsed;
        };
        return t;
    }

    // ─── typed per-node inspector (NODE section) ───────────────
    //
    // Fusion-style: when the widget-graph canvas selects a node it forwards it
    // into VM.SelectedNode; the VM rebuilds SelectedNodeParams (one
    // NodeParamVm per editable attribute key, with companion __Range /
    // __KnownValues meta folded in). This section renders those params as
    // proper controls — Scalar→slider+numbox, Color→picker, Bool→toggle,
    // String→textbox, Enum→dropdown, VectorN→N slider+numbox pairs,
    // MediaPath→Browse… — each with a keyframe diamond on animatable rows.
    //
    // The node-body inline pills stay TEXT-ENTRY only; the rich
    // controls live ONLY here. NodeParamVm.Commit is the single persistence
    // path (PushUndo + write + MarkDirty + preview refresh) — these controls
    // only push the live *Value onto the param and let it commit. Each animatable
    // row carries a DaVinci-style ◀ ◇/◆ ▶ keyframe cluster (BuildKeyframeCluster)
    // that calls the VM's per-parameter keyframe API directly (add/remove at the
    // playhead, prev/next, ease) — superseding the old all-or-nothing diamond.
    //
    // Rows are built imperatively (mirrors RefreshTriggersSection): VectorValues
    // is a double[] that WinUI two-way Binding can't bind element-wise, and the
    // ColorPicker flyout + Browse… picker need code-behind glue.

    private void RefreshNodeForm(VisualistViewModel vm)
    {
        if (NodeSection is null) return;

        UnsubscribeNodeParams();
        _keyframeRefreshers.Clear();
        NodeParamsPanel.Children.Clear();

        Node? node = vm.SelectedNode;
        bool hasNode = node is not null;
        NodeSection.Visibility        = hasNode ? Visibility.Visible : Visibility.Collapsed;
        NodeSectionDivider.Visibility = hasNode ? Visibility.Visible : Visibility.Collapsed;
        if (!hasNode) return;

        NodeTitleText.Text = string.IsNullOrEmpty(node!.Title) ? "(untitled)" : node.Title;
        // Header short description — the node's per-instance Description override
        // when present, otherwise its Category. Kept registry-free so the NODE
        // section doesn't couple to Track-D-owned templates.
        string desc = string.IsNullOrWhiteSpace(node.Description) ? (node.Category ?? "") : node.Description;
        NodeDescriptionText.Text       = desc;
        NodeDescriptionText.Visibility = string.IsNullOrWhiteSpace(desc) ? Visibility.Collapsed : Visibility.Visible;

        var paramsList = vm.SelectedNodeParams;
        if (paramsList is null || paramsList.Count == 0)
        {
            NodeNoParamsHint.Visibility = Visibility.Visible;
            return;
        }
        NodeNoParamsHint.Visibility = Visibility.Collapsed;

        foreach (NodeParamVm p in paramsList)
        {
            try
            {
                FrameworkElement row = BuildParamRow(p);
                NodeParamsPanel.Children.Add(row);
                p.PropertyChanged += OnNodeParamChanged;
                _subscribedParams.Add(p);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("InspectorPanel", $"BuildParamRow('{p?.Key}')", ex);
            }
        }
    }

    private void UnsubscribeNodeParams()
    {
        foreach (NodeParamVm p in _subscribedParams)
        {
            try { p.PropertyChanged -= OnNodeParamChanged; } catch { }
        }
        _subscribedParams.Clear();
    }

    // A param's value / IsAnimated can change from OUTSIDE the inspector controls
    // (canvas keyframe record, undo, the param's own Commit re-clamp). The
    // cheapest correct refresh is a full rebuild — the param list is small
    // (a handful of rows) and a rebuild re-reads every live value + diamond
    // state. Marshalled to the UI thread defensively. Guarded by _suppressNodeEcho
    // so the rebuild we ourselves trigger via a control edit doesn't recurse.
    private void OnNodeParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressNodeEcho) return;
        if (_vm is not { } vm) return;
        try { DispatcherQueue?.TryEnqueue(() => { if (!_suppressNodeEcho) RefreshNodeForm(vm); }); }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "OnNodeParamChanged", ex); }
    }

    // ─── per-kind row builders ───────────────────────────────────────────
    //
    // Every row is a 2-column grid: a 64px mono label (matching the LAYER /
    // WIDGET forms) + the kind-specific editor, with an optional trailing
    // keyframe diamond toggle on animatable rows.

    private FrameworkElement BuildParamRow(NodeParamVm p)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text         = string.IsNullOrEmpty(p.Label) ? p.Key : p.Label,
            FontFamily   = MonoFontFamily(),
            FontSize     = 11,
            Foreground   = (Brush)Application.Current.Resources["CoalBodyTextBrush"],
            VerticalAlignment = VerticalAlignment.Top,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = new Thickness(0, 4, 0, 0),
        };
        ToolTipService.SetToolTip(label, p.Key);
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        FrameworkElement editor = p.Kind switch
        {
            NodeParamKind.Bool      => BuildBoolEditor(p),
            NodeParamKind.String    => BuildStringEditor(p),
            NodeParamKind.Enum      => BuildEnumEditor(p),
            NodeParamKind.Color     => BuildColorEditor(p),
            NodeParamKind.MediaPath => BuildMediaPathEditor(p),
            NodeParamKind.Vector2   => BuildVectorEditor(p, 2),
            NodeParamKind.Vector3   => BuildVectorEditor(p, 3),
            NodeParamKind.Vector4   => BuildVectorEditor(p, 4),
            _                        => BuildScalarEditor(p),   // Scalar
        };
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        // Keyframe cluster — animatable rows only (numeric / vector / colour).
        // DaVinci-style ◀ ◇/◆ ▶ : jump prev / add-or-remove at playhead / jump next.
        if (p.IsAnimatable)
        {
            FrameworkElement cluster = BuildKeyframeCluster(p);
            Grid.SetColumn(cluster, 2);
            grid.Children.Add(cluster);
        }

        return grid;
    }

    // Scalar — Slider (Min..Max) + a small NumberBox kept in two-way sync. The
    // NumberBox keeps out-of-range values editable even when the slider clamps.
    private FrameworkElement BuildScalarEditor(NodeParamVm p)
    {
        var inner = new Grid { ColumnSpacing = 6 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

        double min = p.HasRange ? p.Min : 0;
        double max = p.HasRange ? p.Max : 1;
        if (max <= min) max = min + 1;   // degenerate range guard

        var slider = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = Math.Clamp(p.NumberValue, min, max),
            StepFrequency     = (max - min) / 100.0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new NumberBox
        {
            Value             = p.NumberValue,
            SmallChange       = (max - min) / 100.0,
            LargeChange       = (max - min) / 10.0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, args) =>
        {
            if (_suppressNodeEcho) return;
            double v = args.NewValue;
            PushNodeValue(() =>
            {
                box.Value     = v;        // sync sibling (suppressed)
                p.NumberValue = v;        // Commit happens inside the VM setter
            }, p);
        };
        box.ValueChanged += (_, args) =>
        {
            if (_suppressNodeEcho) return;
            if (double.IsNaN(args.NewValue)) return;
            double v = args.NewValue;
            PushNodeValue(() =>
            {
                slider.Value  = Math.Clamp(v, min, max);   // sync sibling (suppressed)
                p.NumberValue = v;
            }, p);
        };

        Grid.SetColumn(slider, 0);
        Grid.SetColumn(box, 1);
        inner.Children.Add(slider);
        inner.Children.Add(box);
        return inner;
    }

    private FrameworkElement BuildBoolEditor(NodeParamVm p)
    {
        var toggle = new ToggleSwitch
        {
            IsOn       = p.BoolValue,
            OnContent  = "On",
            OffContent = "Off",
            MinWidth   = 0,
        };
        toggle.Toggled += (_, _) =>
        {
            if (_suppressNodeEcho) return;
            PushNodeValue(() => p.BoolValue = toggle.IsOn);
        };
        return toggle;
    }

    private FrameworkElement BuildStringEditor(NodeParamVm p)
    {
        var box = new TextBox
        {
            Text       = p.TextValue ?? "",
            FontFamily = MonoFontFamily(),
            FontSize   = 12,
            AcceptsReturn = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // One undo entry per gesture (focus-to-commit), not per keystroke —
        // commit on LostFocus / Enter, mirroring the inline pill contract.
        void Commit()
        {
            if (_suppressNodeEcho) return;
            string v = box.Text ?? "";
            if (string.Equals(v, p.TextValue ?? "", StringComparison.Ordinal)) return;
            PushNodeValue(() => p.TextValue = v);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Windows.System.VirtualKey.Enter) { Commit(); ev.Handled = true; }
        };
        return box;
    }

    private FrameworkElement BuildEnumEditor(NodeParamVm p)
    {
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = MonoFontFamily(),
            FontSize   = 12,
        };
        if (p.Options is { } opts)
            foreach (string o in opts) combo.Items.Add(o);
        // Select the current value if it's one of the options; otherwise leave
        // unselected so the user's free-form value isn't silently rewritten.
        string cur = p.TextValue ?? "";
        int idx = p.Options is null ? -1 : Array.FindIndex(p.Options, o => string.Equals(o, cur, StringComparison.Ordinal));
        combo.SelectedIndex = idx;
        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressNodeEcho) return;
            if (combo.SelectedItem is string sel)
                PushNodeValue(() => p.TextValue = sel);
        };
        return combo;
    }

    // Color — a swatch button that opens a WinUI ColorPicker flyout + a hex
    // TextBox. Round-trips #rrggbb / #rrggbbaa. ColorHex is the source of truth
    // on the param; both the swatch and the hex box write it back.
    private FrameworkElement BuildColorEditor(NodeParamVm p)
    {
        var inner = new Grid { ColumnSpacing = 6 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Color initial = ParseHexColor(p.ColorHex, out bool hadAlpha);

        var swatchFill = new SolidColorBrush(initial);
        var swatch = new Button
        {
            Width   = 28,
            Height  = 28,
            Padding = new Thickness(0),
            MinWidth = 28,
            Background      = swatchFill,
            BorderBrush     = (Brush)Application.Current.Resources["BorderPillBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(swatch, "Pick a color");

        var hexBox = new TextBox
        {
            Text       = NormalizeHex(p.ColorHex, hadAlpha),
            FontFamily = MonoFontFamily(),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var picker = new ColorPicker
        {
            Color                  = initial,
            IsAlphaEnabled         = true,
            IsMoreButtonVisible    = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible      = true,
        };
        var flyout = new Flyout { Content = picker };
        FlyoutBase.SetAttachedFlyout(swatch, flyout);
        swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);

        picker.ColorChanged += (_, args) =>
        {
            if (_suppressNodeEcho) return;
            Color c = args.NewColor;
            string hex = ColorToHex(c);
            PushNodeValue(() =>
            {
                swatchFill.Color = c;
                hexBox.Text      = hex;
                p.ColorHex       = hex;
            }, p);
        };

        void CommitHex()
        {
            if (_suppressNodeEcho) return;
            string raw = hexBox.Text ?? "";
            Color c = ParseHexColor(raw, out bool a);
            string hex = NormalizeHex(raw, a);
            if (string.Equals(hex, NormalizeHex(p.ColorHex, a), StringComparison.OrdinalIgnoreCase)) return;
            PushNodeValue(() =>
            {
                swatchFill.Color = c;
                picker.Color     = c;
                hexBox.Text      = hex;
                p.ColorHex       = hex;
            }, p);
        }
        hexBox.LostFocus += (_, _) => CommitHex();
        hexBox.KeyDown += (_, ev) =>
        {
            if (ev.Key == Windows.System.VirtualKey.Enter) { CommitHex(); ev.Handled = true; }
        };

        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(hexBox, 1);
        inner.Children.Add(swatch);
        inner.Children.Add(hexBox);
        return inner;
    }

    // VectorN — N compact Slider+NumberBox pairs (X/Y/Z/W). One keyframe diamond
    // for the whole vector (added by BuildParamRow). VectorValues is the source
    // of truth; each component writes its index back and re-assigns the array so
    // the VM setter's change detection + Commit fire.
    private static readonly string[] VectorAxes = { "X", "Y", "Z", "W" };

    private FrameworkElement BuildVectorEditor(NodeParamVm p, int arity)
    {
        var stack = new StackPanel { Spacing = 4 };
        double[] vals = p.VectorValues ?? new double[arity];

        double min = p.HasRange ? p.Min : 0;
        double max = p.HasRange ? p.Max : 1;
        if (max <= min) max = min + 1;

        for (int i = 0; i < arity; i++)
        {
            int axisIndex = i;
            double startVal = (vals.Length > i) ? vals[i] : 0;

            var compGrid = new Grid { ColumnSpacing = 6 };
            compGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            compGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            compGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var axisLabel = new TextBlock
            {
                Text       = VectorAxes[i],
                FontFamily = MonoFontFamily(),
                FontSize   = 10,
                Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            var slider = new Slider
            {
                Minimum       = min,
                Maximum       = max,
                Value         = Math.Clamp(startVal, min, max),
                StepFrequency = (max - min) / 100.0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var box = new NumberBox
            {
                Value       = startVal,
                SmallChange = (max - min) / 100.0,
                LargeChange = (max - min) / 10.0,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                VerticalAlignment = VerticalAlignment.Center,
            };

            void WriteComponent(double v)
            {
                PushNodeValue(() =>
                {
                    // Re-read the live array, clone, mutate the component, re-assign
                    // so the VM's setter sees a new reference and Commits.
                    double[] live = p.VectorValues ?? new double[arity];
                    var next = new double[arity];
                    for (int k = 0; k < arity; k++) next[k] = (live.Length > k) ? live[k] : 0;
                    next[axisIndex] = v;
                    p.VectorValues = next;
                }, p);
            }

            slider.ValueChanged += (_, args) =>
            {
                if (_suppressNodeEcho) return;
                double v = args.NewValue;
                _suppressNodeEcho = true;
                try { box.Value = v; } finally { _suppressNodeEcho = false; }
                WriteComponent(v);
            };
            box.ValueChanged += (_, args) =>
            {
                if (_suppressNodeEcho) return;
                if (double.IsNaN(args.NewValue)) return;
                double v = args.NewValue;
                _suppressNodeEcho = true;
                try { slider.Value = Math.Clamp(v, min, max); } finally { _suppressNodeEcho = false; }
                WriteComponent(v);
            };

            Grid.SetColumn(axisLabel, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(box, 2);
            compGrid.Children.Add(axisLabel);
            compGrid.Children.Add(slider);
            compGrid.Children.Add(box);
            stack.Children.Add(compGrid);
        }
        return stack;
    }

    // MediaPath — TextBox + Browse… that reuses the graph's MediaPickerDialog
    // (same picker the media loader node creation uses). The picked RELATIVE
    // path is written back into TextValue.
    private FrameworkElement BuildMediaPathEditor(NodeParamVm p)
    {
        var inner = new Grid { ColumnSpacing = 6 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            Text       = p.TextValue ?? "",
            FontFamily = MonoFontFamily(),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Commit()
        {
            if (_suppressNodeEcho) return;
            string v = box.Text ?? "";
            if (string.Equals(v, p.TextValue ?? "", StringComparison.Ordinal)) return;
            PushNodeValue(() => p.TextValue = v);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Windows.System.VirtualKey.Enter) { Commit(); ev.Handled = true; }
        };

        var browse = MakeChip("Browse…", "Pick a media file from data/media");
        browse.Click += async (_, _) =>
        {
            try
            {
                if (XamlRoot is null) return;
                var dlg = new MediaPickerDialog { XamlRoot = XamlRoot };
                var res = await dlg.ShowAsync();
                // The picker commits via either Primary (Use Selected) or a
                // double-tap Hide() — both set SelectedRelativePath.
                if (dlg.SelectedRelativePath is { Length: > 0 } rel)
                {
                    _suppressNodeEcho = true;
                    try { box.Text = rel; } finally { _suppressNodeEcho = false; }
                    PushNodeValue(() => p.TextValue = rel);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("InspectorPanel", "BuildMediaPathEditor Browse", ex);
            }
        };

        Grid.SetColumn(box, 0);
        Grid.SetColumn(browse, 1);
        inner.Children.Add(box);
        inner.Children.Add(browse);
        return inner;
    }

    // Keyframe cluster — DaVinci-style ◀ ◇/◆ ▶ on every animatable row.
    //   ◀ / ▶  : move the playhead to the prev / next keyframe ON THIS parameter.
    //   ◇      : no keyframe at the playhead (hollow). Click → add one here.
    //   ◆      : a keyframe sits at the playhead (filled). Click → remove it.
    //   gold   : the parameter is animated (has ≥1 keyframe). muted: not animated.
    //   right-click the diamond → Linear / Ease In / Ease Out / Ease In-Out /
    //   Remove animation.
    // A refresh closure is registered so a playhead move re-evaluates the glyph
    // without rebuilding the form (RefreshKeyframeClusters, on PlayheadMs change).
    private FrameworkElement BuildKeyframeCluster(NodeParamVm p)
    {
        Button prev    = MakeKfMicroButton("◀", "Jump to the previous keyframe on this parameter");
        Button diamond = MakeKfMicroButton("◇", null);
        Button next    = MakeKfMicroButton("▶", "Jump to the next keyframe on this parameter");

        void Refresh()
        {
            try
            {
                var vm = _vm;
                bool has = vm?.ParamHasKeyframes(p) == true;
                bool on  = has && vm?.IsParamOnKeyframe(p) == true;
                if (diamond.Content is TextBlock tb) tb.Text = on ? "◆" : "◇";
                diamond.Foreground = has
                    ? (Brush)Application.Current.Resources["SelectionBrush"]
                    : (Brush)Application.Current.Resources["CoalSecondaryTextBrush"];
                ToolTipService.SetToolTip(diamond, !has
                    ? "Add a keyframe at the playhead (start animating this parameter)"
                    : (on ? "On a keyframe — click to remove it"
                          : "Add a keyframe at the playhead"));
            }
            catch { /* visual refresh is best-effort */ }
        }
        Refresh();
        _keyframeRefreshers.Add(Refresh);

        prev.Click    += (_, _) => { try { _vm?.StepParamKeyframe(p, -1); } catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "StepParamKeyframe-", ex); } };
        next.Click    += (_, _) => { try { _vm?.StepParamKeyframe(p, +1); } catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "StepParamKeyframe+", ex); } };
        diamond.Click += (_, _) =>
        {
            try { _vm?.ToggleParamKeyframeAtPlayhead(p); } catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "ToggleParamKeyframeAtPlayhead", ex); }
            Refresh();
        };

        // Right-click → easing + remove-animation.
        var flyout = new MenuFlyout();
        void AddCurve(string text, KeyframeCurve curve)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) =>
            {
                try { _vm?.SetParamKeyframeCurveAtPlayhead(p, curve); } catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "SetParamKeyframeCurve", ex); }
                Refresh();
            };
            flyout.Items.Add(mi);
        }
        AddCurve("Linear",      KeyframeCurve.Linear);
        AddCurve("Ease In",     KeyframeCurve.EaseIn);
        AddCurve("Ease Out",    KeyframeCurve.EaseOut);
        AddCurve("Ease In-Out", KeyframeCurve.EaseInOut);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = "Remove animation" };
        remove.Click += (_, _) =>
        {
            try { _vm?.RemoveAllParamKeyframes(p); } catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "RemoveAllParamKeyframes", ex); }
            Refresh();
        };
        flyout.Items.Add(remove);
        diamond.ContextFlyout = flyout;

        var row = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            Spacing           = 1,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 2, 0, 0),
        };
        row.Children.Add(prev);
        row.Children.Add(diamond);
        row.Children.Add(next);
        return row;
    }

    private Button MakeKfMicroButton(string glyph, string? tooltip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            Padding           = new Thickness(3, 0, 3, 0),
            MinWidth          = 0,
            MinHeight         = 0,
            Background        = (Brush)Application.Current.Resources["BgPillBrush"],
            BorderThickness   = new Thickness(0),
            Foreground        = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(tooltip)) ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    // Run a model write under echo-suppression so the control-sync writes inside
    // <paramref name="write"/> don't re-fire the control's *Changed handler (and
    // the NodeParamVm.PropertyChanged the write raises doesn't trigger a nested
    // RefreshNodeForm). The param's own setter performs the Commit (PushUndo +
    // attribute write + MarkDirty + preview refresh) — the single persistence
    // path, never duplicated here.
    private void PushNodeValue(Action write)
    {
        _suppressNodeEcho = true;
        try { write(); }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "PushNodeValue", ex); }
        finally { _suppressNodeEcho = false; }
    }

    // Animatable overload — after committing the value, if the parameter is
    // animated and the playhead sits on one of its keyframes, rewrite that
    // keyframe so the edit lands ON THE KEYFRAME (DaVinci "edit value at the
    // playhead"), not just the static at-rest fallback. The value commit already
    // pushed undo, so the keyframe rewrite rides the same undo step.
    private void PushNodeValue(Action write, NodeParamVm animatedParam)
    {
        PushNodeValue(write);
        try
        {
            if (animatedParam is { IsAnimated: true })
                _vm?.UpdateParamKeyframeValueAtPlayhead(animatedParam);
        }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "PushNodeValue.kfUpdate", ex); }
    }

    // ─── color hex helpers ───────────────────────────────────────────────

    // Parse "#rgb" / "#rrggbb" / "#rrggbbaa" (also accepts the no-hash form).
    // Falls back to opaque white on a malformed string. hadAlpha reports whether
    // the source carried an explicit alpha channel so the round-trip preserves
    // the #rrggbb vs #rrggbbaa width.
    private static Color ParseHexColor(string? hex, out bool hadAlpha)
    {
        hadAlpha = false;
        string s = (hex ?? "").Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
        try
        {
            byte r, g, b, a = 0xFF;
            if (s.Length == 3) // rgb
            {
                r = (byte)(Convert.ToInt32(new string(s[0], 2), 16));
                g = (byte)(Convert.ToInt32(new string(s[1], 2), 16));
                b = (byte)(Convert.ToInt32(new string(s[2], 2), 16));
            }
            else if (s.Length == 6) // rrggbb
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
            }
            else if (s.Length == 8) // rrggbbaa
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
                a = Convert.ToByte(s.Substring(6, 2), 16);
                hadAlpha = true;
            }
            else
            {
                return Microsoft.UI.Colors.White;
            }
            return Color.FromArgb(a, r, g, b);
        }
        catch
        {
            return Microsoft.UI.Colors.White;
        }
    }

    // Serialize a Color back to the contract form: #rrggbb when fully opaque,
    // #rrggbbaa when it carries alpha.
    private static string ColorToHex(Color c)
        => c.A == 0xFF
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    // Normalize a possibly-shorthand / hashless hex string to the canonical
    // #rrggbb[aa] display form. preserveAlpha keeps the alpha channel when the
    // source had one.
    private static string NormalizeHex(string? hex, bool preserveAlpha)
    {
        Color c = ParseHexColor(hex, out bool a);
        return (a || preserveAlpha) ? ColorToHex(Color.FromArgb(c.A, c.R, c.G, c.B)) : $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
