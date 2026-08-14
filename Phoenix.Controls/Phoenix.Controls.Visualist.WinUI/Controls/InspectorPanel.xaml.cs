using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Clipboard;
using Phoenix.Controls.Visualist.WinUI.Core;
using Phoenix.Controls.Visualist.WinUI.Dialogs;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
// Aliased rather than a plain `using Phoenix.Controls.Visualist.Core;` — this file already
// imports Phoenix.Controls.Visualist.WinUI.Core, and pulling in the engine namespace
// wholesale next to it is how a same-named type in either becomes an ambiguous-reference
// build break in an unrelated future sprint. One alias, one type.
using AlertRegen = Phoenix.Controls.Visualist.Core.WidgetPresets.AlertBoxRegenResult;
// Second alias, same rule: the ownership banner pre-flights the trigger-name collision with
// the engine's own pure predicate (WidgetPresets.AlertBoxTriggerNameTaken) so the banner and
// the compiler's refusal cannot drift apart.
using AlertPresets = Phoenix.Controls.Visualist.Core.WidgetPresets;

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

    // Widget preset picker (WIDGET section). Preset is nullable, so index 0 is a
    // synthetic "(none)" entry and the enum values follow in declaration order.
    private static readonly WidgetPreset[]  WidgetPresetValues = (WidgetPreset[])Enum.GetValues(typeof(WidgetPreset));
    private const string NoPresetLabel = "(none)";

    // Widget numeric-edit gesture (drag-scrub on a geometry / z-index label).
    // A drag streams many box writes; snapshot undo ONCE on gesture start and
    // skip the per-write PushUndo so the whole drag lands as a single undo entry.
    // MarkDirty still fires per write (the geometry boxes are persistent named
    // XAML, so a live SelectedLayer refresh doesn't tear them down). Keyboard /
    // spinner edits never enter a gesture and commit per-change as before.
    private bool _widgetEditGestureActive;

    // Per-trigger volume slider gesture. Unlike the geometry boxes, trigger rows
    // are rebuilt imperatively on MarkDirty (RefreshTriggersSection is wired to
    // SelectedLayer), so a mid-drag MarkDirty would tear down the very slider
    // being dragged. Snapshot undo on the first change of the gesture and DEFER
    // MarkDirty to gesture end so the row survives the drag.
    private bool _triggerVolGestureActive;
    private bool _triggerVolGestureUndoPushed;
    private bool _triggerVolGestureDirty;

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

    // ALERT BOX section (V8). Two flags, both about not destroying the control the
    // author is using:
    //
    //   _alertBoxCommitInFlight — a settings commit MarkDirty()s, which re-fires
    //     SelectedLayer, which would rebuild the imperative media rows and tear down the
    //     very TextBox that just committed on Enter (it still has focus). _suppressEcho
    //     cannot be reused for this: RefreshWidgetForm sets it and CLEARS it in its own
    //     finally, so a flag raised around the commit would be wiped mid-flight.
    //   _alertBoxDetachArmed — detach is irreversible, so the button is a two-step
    //     confirm IN PLACE rather than a ContentDialog
    //     (feedback_no_modal_dialogs_for_repeatable_rejections keeps modals out of this
    //     panel, and an in-place arm/confirm is also un-mis-clickable in one gesture).
    //     Re-armed to false on every section refresh so navigating away disarms it.
    private bool _alertBoxCommitInFlight;
    private bool _alertBoxDetachArmed;
    private DispatcherTimer? _alertBoxFeedbackTimer;

    // Coalescing flags for OnNodeParamChanged: a burst of param changes (a
    // trigger switch flipping IsAnimated on several rows, a multi-component
    // vector write) previously enqueued one full node-form rebuild PER change.
    // Each flag resets when its queued action runs, collapsing a burst to one.
    private bool _nodeFormRefreshPending;
    private bool _kfClusterRefreshPending;

    public InspectorPanel()
    {
        InitializeComponent();

        foreach (LayerPreset p in LayerPresets) LayerPresetBox.Items.Add(p.ToString());

        // Widget preset picker: "(none)" first (Preset is nullable), then the enum.
        // Labels come from WidgetPresetLabels so this list reads the same as the canvas
        // context menu and the preset gallery ("Alert Box", not "AlertBox"). Selection is
        // resolved POSITIONALLY (WidgetPresetValues[idx - 1]), so the display text is free
        // to be friendly.
        WidgetPresetBox.Items.Add(Localizer.T("visualist.inspector.widget.preset.none", NoPresetLabel));
        foreach (WidgetPreset p in WidgetPresetValues) WidgetPresetBox.Items.Add(WidgetPresetLabels.For(p));

        // The message field's help text lives on AlertBoxSettings, not in XAML: it has to
        // name {Args3} (and say when it is zero) precisely because the DEFAULT caption no
        // longer uses it, and keeping both halves of that contract in one file is what makes
        // it assertable.
        ToolTipService.SetToolTip(AlertBoxMessageBox, AlertBoxSettings.MessageFormatTooltip);

        // Fusion-style drag-scrub on the geometry + z-index field labels.
        AttachLabelScrub(WidgetXLabel,      WidgetXBox,      1, 0, 16384);
        AttachLabelScrub(WidgetYLabel,      WidgetYBox,      1, 0, 16384);
        AttachLabelScrub(WidgetWidthLabel,  WidgetWidthBox,  1, 1, 16384);
        AttachLabelScrub(WidgetHeightLabel, WidgetHeightBox, 1, 1, 16384);
        AttachLabelScrub(WidgetZIndexLabel, WidgetZIndexBox, 1, 0, 1_000_000);

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
            // Flush any in-flight slider gesture so its deferred dirty-mark
            // lands before the panel lets go of the VM.
            old.EndNodeAttributeGesture();
            _vm = null;
        }
        if (_onBusConnChanged is not null)
        {
            try { VisualistBusClient.Instance.OnConnectionStatusChanged -= _onBusConnChanged; } catch { }
            _onBusConnChanged = null;
        }
        try { _feedbackTimer?.Stop(); } catch { }
        try { _alertBoxFeedbackTimer?.Stop(); } catch { }
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
            // Flush any in-flight slider gesture before swapping VMs.
            old.EndNodeAttributeGesture();
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
            RefreshAlertBoxSection(vm);
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
                RefreshAlertBoxSection(vm);
                RefreshTriggersSection(vm); // save can mint the LayerID
                break;
            case nameof(VisualistViewModel.SelectedWidget):
                RefreshWidgetRoster(vm);   // sync the highlighted row
                RefreshWidgetForm(vm);
                RefreshAlertBoxSection(vm);
                RefreshTriggersSection(vm); // IDs + per-trigger chips
                // A widget switch implicitly drops the node selection — the VM
                // nulls SelectedNode, but rebuild defensively so a stale NODE
                // section never lingers over the wrong widget.
                RefreshNodeForm(vm);
                break;
            case nameof(VisualistViewModel.SelectedWidgets):
                // The canvas multi-select set changed (lasso / shift-click / group
                // op) — re-seed the WIDGET form so it flips between single-edit and
                // mixed-value / apply-to-all mode. Roster stays anchored on the
                // primary; triggers / node sections follow SelectedWidget as before.
                RefreshWidgetForm(vm);
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
            LayerLanguageBox.Text   = layer.TargetLanguage ?? string.Empty;
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

    // ─── layer target language ──────────────────────────────────────
    //
    // Default caption translation target (BCP-47) for Caption.LiveCaption nodes
    // on this layer. Committed on LostFocus / Enter — not per-keystroke — so a
    // rename is one undo entry. Empty string persists as null ("no translation").

    private void OnLayerTargetLanguageLostFocus(object sender, RoutedEventArgs e)
        => CommitLayerTargetLanguage();

    private void OnLayerTargetLanguageKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { CommitLayerTargetLanguage(); e.Handled = true; }
    }

    private void CommitLayerTargetLanguage()
    {
        if (_suppressEcho || _vm?.SelectedLayer is not { } layer) return;
        string v   = (LayerLanguageBox.Text ?? string.Empty).Trim();
        string cur = layer.TargetLanguage ?? string.Empty;
        if (string.Equals(cur, v, StringComparison.Ordinal)) return;
        _vm.Document?.PushUndo();
        layer.TargetLanguage = v.Length == 0 ? null : v;
        _vm.Document?.MarkDirty();
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
            Title               = Localizer.T("visualist.inspector.rescale.title", "Rescale widgets?"),
            Content             = string.Format(
                Localizer.T("visualist.inspector.rescale.body_format",
                    "Some widgets are larger than the new resolution ({0}×{1}). Rescale them proportionally?"),
                newW, newH),
            PrimaryButtonText   = Localizer.T("visualist.inspector.rescale.yes", "Yes"),
            SecondaryButtonText = Localizer.T("visualist.inspector.rescale.no", "No"),
            CloseButtonText     = Localizer.T("common.cancel", "Cancel"),
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
                    string label       = string.IsNullOrEmpty(w.Name)
                        ? Localizer.T("visualist.canvas.widget.unnamed", "(unnamed)")
                        : w.Name;
                    string presetLabel = WidgetPresetLabels.For(w.Preset, "—");
                    WidgetRosterList.Items.Add(new ListViewItem
                    {
                        Content  = $"{label}   ·   {presetLabel}   ·   z{w.ZIndex}",
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
        IReadOnlyList<LayerWidget> multi = vm.SelectedWidgets;
        bool isMulti = multi.Count > 1;

        LayerWidget? widget = vm.SelectedWidget;
        bool selected = isMulti || widget is not null;

        WidgetForm.Visibility        = selected ? Visibility.Visible  : Visibility.Collapsed;
        WidgetEmptyHint.Visibility   = selected ? Visibility.Collapsed : Visibility.Visible;
        DeleteWidgetButton.IsEnabled = selected;

        // Multi-select: banner on, single-only name row off (each widget keeps its
        // own name — renaming N widgets to one string would be destructive).
        WidgetMultiHeader.Visibility = isMulti ? Visibility.Visible   : Visibility.Collapsed;
        WidgetNameRow.Visibility     = isMulti ? Visibility.Collapsed : Visibility.Visible;
        if (isMulti)
            WidgetMultiHeaderText.Text = string.Format(
                Localizer.T("visualist.inspector.widget.multi_header_format",
                    "{0} widgets selected · edits apply to all"),
                multi.Count);

        if (!selected) return;

        _suppressEcho = true;
        try
        {
            if (isMulti)
            {
                // Mixed-value seeding: show the shared value when every widget
                // agrees, else NaN → the NumberBox renders its "—" placeholder.
                WidgetXBox.Value      = AllSameInt(multi, w => w.Rect.X,      out int mx) ? mx : double.NaN;
                WidgetYBox.Value      = AllSameInt(multi, w => w.Rect.Y,      out int my) ? my : double.NaN;
                WidgetWidthBox.Value  = AllSameInt(multi, w => w.Rect.Width,  out int mw) ? mw : double.NaN;
                WidgetHeightBox.Value = AllSameInt(multi, w => w.Rect.Height, out int mh) ? mh : double.NaN;
                WidgetZIndexBox.Value = AllSameInt(multi, w => w.ZIndex,      out int mz) ? mz : double.NaN;

                // Preset: common → its index; mixed → -1 (ComboBox shows blank).
                WidgetPresetBox.SelectedIndex = AllSamePreset(multi, out WidgetPreset? mp)
                    ? (mp is { } wp ? Array.IndexOf(WidgetPresetValues, wp) + 1 : 0)
                    : -1;

                // Transition (seconds): common or blank. The slider can't render a
                // mixed state, so it parks at 0 while the box shows "—".
                if (AllSameTransition(multi, out double tms))
                {
                    double tsec = Math.Clamp(tms / 1000.0, 0, 1);
                    WidgetTransitionSlider.Value = tsec;
                    WidgetTransitionBox.Value    = tsec;
                }
                else
                {
                    WidgetTransitionSlider.Value = 0;
                    WidgetTransitionBox.Value    = double.NaN;
                }
            }
            else
            {
                WidgetNameBox.Text    = widget!.Name;
                // x/y/w/h are committing NumberBoxes now (drag-scrub on their labels);
                // preset is a ComboBox. Seed them here under _suppressEcho so the sync
                // write doesn't re-fire the commit handlers.
                WidgetXBox.Value      = widget.Rect.X;
                WidgetYBox.Value      = widget.Rect.Y;
                WidgetWidthBox.Value  = widget.Rect.Width;
                WidgetHeightBox.Value = widget.Rect.Height;
                // ComboBox: index 0 = "(none)", enum values follow in declaration order.
                WidgetPresetBox.SelectedIndex = widget.Preset is { } wp
                    ? Array.IndexOf(WidgetPresetValues, wp) + 1
                    : 0;
                WidgetZIndexBox.Value = widget.ZIndex;
                // Per-widget dip-to-blank transition — model is milliseconds (0–1000);
                // the slider/box edit in SECONDS (0–1).
                double tsec = Math.Clamp(widget.TransitionMs / 1000.0, 0, 1);
                WidgetTransitionSlider.Value = tsec;
                WidgetTransitionBox.Value    = tsec;
            }
        }
        finally { _suppressEcho = false; }
    }

    // ─── multi-select mixed-value helpers ─────────────────────────────────
    // Return true (+ the shared value) when every selected widget agrees on a
    // field; false when they differ (the field then shows a blank "—").
    private static bool AllSameInt(IReadOnlyList<LayerWidget> ws, Func<LayerWidget, int> sel, out int value)
    {
        value = ws.Count > 0 ? sel(ws[0]) : 0;
        for (int i = 1; i < ws.Count; i++)
            if (sel(ws[i]) != value) return false;
        return true;
    }

    private static bool AllSamePreset(IReadOnlyList<LayerWidget> ws, out WidgetPreset? value)
    {
        value = ws.Count > 0 ? ws[0].Preset : null;
        for (int i = 1; i < ws.Count; i++)
            if (!Nullable.Equals(ws[i].Preset, value)) return false;
        return true;
    }

    private static bool AllSameTransition(IReadOnlyList<LayerWidget> ws, out double value)
    {
        value = ws.Count > 0 ? ws[0].TransitionMs : 0;
        for (int i = 1; i < ws.Count; i++)
            if (Math.Abs(ws[i].TransitionMs - value) > 0.5) return false;
        return true;
    }

    // The widgets an inspector WIDGET-field edit applies to: the full
    // multi-selection when 2+ are selected, else just the primary SelectedWidget.
    private IReadOnlyList<LayerWidget> WidgetEditTargets()
    {
        if (_vm?.SelectedWidgets is { Count: > 1 } multi) return multi;
        return _vm?.SelectedWidget is { } w
            ? new List<LayerWidget> { w }
            : (IReadOnlyList<LayerWidget>)Array.Empty<LayerWidget>();
    }

    /// <summary>
    /// Fan a committed WIDGET edit out to the same live surfaces a canvas drag /
    /// resize / nudge reaches through <c>LayerCanvasView.PublishLiveWidgetEdit</c>.
    /// Pre-fix the inspector's committing boxes only called
    /// <c>Document.MarkDirty()</c>, so with auto-sync on, DRAGGING a widget
    /// updated the live OBS source but typing the same X/Y into the inspector
    /// did not.
    ///
    /// Two targets, matching the canvas contract exactly:
    ///   1. An open full-layer popout preview — ALWAYS updated: it's a design
    ///      surface the author explicitly opened. No-op when none is open.
    ///   2. The live production OBS browser source over the Hub bus — ONLY when
    ///      the user opted into <c>AutoSyncOnEdit</c>. That flag is the whole of
    ///      the user's consent to touch an on-air overlay, so a streamer who
    ///      never enabled it must never see their overlay shift while they
    ///      design. Skipped for an unsaved scratch layer (no LayerID ⇒ no live
    ///      browser source).
    ///
    /// Both legs are ephemeral (compositor.js applies WIDGET_UPDATE in memory; a
    /// browser refresh reverts to the saved layer) and independently guarded so a
    /// publish fault can't break the edit that triggered it.
    /// </summary>
    private void PublishLiveWidgetEdit(IReadOnlyList<LayerWidget> targets)
    {
        if (targets.Count == 0) return;

        var preview = FindPillarMainView()?.ActiveLayerPreviewWindow?.Preview;
        if (preview is not null)
        {
            try { foreach (LayerWidget w in targets) preview.PostWidgetUpdate(w); }
            catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "popout live update", ex); }
        }

        try
        {
            if (!VisualistUserConfig.Instance.AutoSyncOnEdit) return;
            if (ResolveLayerId(_vm) is not { } layerId) return;
            foreach (LayerWidget widget in targets)
            {
                _ = Phoenix.Controls.Shared.Core.AsyncErrorBoundary.SafeRunAsync(
                    () => VisualistBusClient.Instance.SendWidgetLiveUpdateAsync(layerId, widget),
                    "InspectorPanel",
                    "live OBS widget update");
            }
        }
        catch (Exception ex) { GlobalLogger.Error("InspectorPanel", "live OBS widget update", ex); }
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
        if (_suppressEcho) return;
        var targets = WidgetEditTargets();
        if (targets.Count == 0) return;
        double sec = Math.Clamp(double.IsNaN(seconds) ? 0 : seconds, 0, 1);
        double ms  = sec * 1000.0;
        if (targets.All(w => Math.Abs(w.TransitionMs - ms) < 0.5)) return;
        _suppressEcho = true;
        try
        {
            if (syncSlider) WidgetTransitionSlider.Value = sec;
            if (syncBox)    WidgetTransitionBox.Value    = sec;
        }
        finally { _suppressEcho = false; }
        _vm!.Document?.PushUndo();
        foreach (var w in targets) w.TransitionMs = ms;
        _vm!.Document?.MarkDirty();
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
        if (_suppressEcho || double.IsNaN(args.NewValue)) return;
        var targets = WidgetEditTargets();
        if (targets.Count == 0) return;
        int newZ = Math.Max(0, (int)Math.Round(args.NewValue));
        if (targets.All(w => w.ZIndex == newZ)) return;
        if (!_widgetEditGestureActive) _vm!.Document?.PushUndo();
        foreach (var w in targets) w.ZIndex = newZ;
        _vm!.Document?.MarkDirty();
        // Mid-gesture writes stream one commit per pointer-move; the publish is
        // deferred to EndWidgetEditGesture so the bus sees ONE message for the
        // whole scrub, matching the canvas publishing on drag END.
        if (!_widgetEditGestureActive) PublishLiveWidgetEdit(targets);
    }

    // ─── editable widget geometry (x / y / w / h) ─────────────────────────
    //
    // Committing NumberBoxes replacing the old read-only mirrors. Each write
    // pushes ONE undo per edit gesture (a drag-scrub batches via
    // _widgetEditGestureActive; a spinner / keyboard nudge is its own gesture),
    // writes widget.Rect, and MarkDirty — whose OnChanged re-raises SelectedLayer
    // so the canvas + the sibling boxes re-render.

    private enum RectComponent { X, Y, Width, Height }

    private void OnWidgetXChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => CommitWidgetRectComponent(RectComponent.X, args.NewValue);
    private void OnWidgetYChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => CommitWidgetRectComponent(RectComponent.Y, args.NewValue);
    private void OnWidgetWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => CommitWidgetRectComponent(RectComponent.Width, args.NewValue);
    private void OnWidgetHeightChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        => CommitWidgetRectComponent(RectComponent.Height, args.NewValue);

    private void CommitWidgetRectComponent(RectComponent comp, double raw)
    {
        if (_suppressEcho || double.IsNaN(raw)) return;
        var targets = WidgetEditTargets();
        if (targets.Count == 0) return;
        int nv = (int)Math.Round(raw);
        nv = comp is RectComponent.Width or RectComponent.Height ? Math.Max(1, nv) : Math.Max(0, nv);

        // No-op guard: burn no undo slot only when EVERY target already holds nv.
        // In multi-edit, "apply to all" pulls the differing widgets into line.
        bool anyChange = false;
        foreach (var w in targets)
        {
            int cur = comp switch
            {
                RectComponent.X     => w.Rect.X,
                RectComponent.Y     => w.Rect.Y,
                RectComponent.Width => w.Rect.Width,
                _                   => w.Rect.Height,
            };
            if (cur != nv) { anyChange = true; break; }
        }
        if (!anyChange) return;

        if (!_widgetEditGestureActive) _vm!.Document?.PushUndo();
        foreach (var w in targets)
        {
            switch (comp)
            {
                case RectComponent.X:     w.Rect.X      = nv; break;
                case RectComponent.Y:     w.Rect.Y      = nv; break;
                case RectComponent.Width: w.Rect.Width  = nv; break;
                default:                  w.Rect.Height = nv; break;
            }
        }
        _vm!.Document?.MarkDirty();   // OnChanged re-raises SelectedLayer → canvas re-render
        // See OnWidgetZIndexChanged — a drag-scrub batches its publish to the
        // gesture end; a spinner / keyboard commit publishes immediately.
        if (!_widgetEditGestureActive) PublishLiveWidgetEdit(targets);
    }

    // Widget preset picker (WIDGET section). Sets only the widget's Preset TAG
    // (roster label / canvas categorization) — it does NOT regenerate the graph
    // (that heavier "apply preset template" action lives on the palette). Index 0
    // is the synthetic "(none)" → null.
    //
    // ★ V8 exception: AlertBox is the one COMPILED preset, so tagging alone would leave
    // the author looking at a fully populated ALERT BOX section attached to a widget with
    // no alert trigger — configured-looking and inert. Picking it therefore also compiles,
    // inside the SAME undo entry as the tag write (CommitAlertBoxSettings(pushUndo:false)),
    // so one Ctrl+Z cannot leave a half-applied preset. A DETACHED widget is skipped: the
    // compiler refuses it at its own choke point, which is what makes re-picking the preset
    // unable to silently re-attach.
    //
    // ★ The compile's OUTCOME is reported inline, exactly like a settings edit. This picker is
    // the shortest path to a refusal in the whole feature — one click on a widget that already
    // owns a hand-authored onTrigger:alert — and it used to be the only path that reported
    // nothing at all, leaving a SystemLog line as the sole trace of a compile that did not
    // happen (with the ownership banner underneath it then describing regeneration in the
    // future tense, which reads as success).
    private void OnWidgetPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEcho) return;
        int idx = WidgetPresetBox.SelectedIndex;
        if (idx < 0) return;   // -1 = mixed-value placeholder, not a user pick
        var targets = WidgetEditTargets();
        if (targets.Count == 0) return;
        WidgetPreset? newPreset = idx == 0 ? (WidgetPreset?)null : WidgetPresetValues[idx - 1];
        if (targets.All(w => Nullable.Equals(w.Preset, newPreset))) return;
        _vm!.Document?.PushUndo();
        foreach (var w in targets) w.Preset = newPreset;

        // First refusal wins the message: in a multi-select the author needs to know that
        // SOMETHING was refused, and naming the first one is more use than "n failed".
        AlertRegen? alertOutcome = null;
        LayerWidget? alertWidget = null;
        if (newPreset == WidgetPreset.AlertBox)
        {
            foreach (var w in targets)
            {
                AlertRegen r = _vm!.CommitAlertBoxSettings(w, mutate: null, pushUndo: false);
                if (alertOutcome is null || (alertOutcome == AlertRegen.Regenerated && r != AlertRegen.Regenerated))
                {
                    alertOutcome = r;
                    alertWidget  = w;
                }
            }
        }

        _vm!.Document?.MarkDirty();   // roster label + canvas refresh via SelectedLayer
        // Preset never rides a drag gesture (it's a ComboBox), so publish straight
        // away — same fan-out every other committing widget field now takes.
        PublishLiveWidgetEdit(targets);
        // Refresh FIRST (it un-collapses the section that hosts the feedback line), report
        // after — RefreshAlertBoxSection does not touch the feedback text.
        if (_vm is { } vm) RefreshAlertBoxSection(vm);
        if (alertOutcome is { } outcome && alertWidget is { } aw)
            ReportAlertBoxResult(aw, outcome, pathProblem: null);
    }

    // ─── ALERT BOX section (V8) ──────────────────────────────────────────
    //
    // The ONLY compiled preset. AlertBoxSettings is the source of truth and the widget's
    // onTrigger:<id> graph is regenerated from it on every commit here, so a hand edit to
    // that graph is destroyed by the next field edit. That is data loss, so the section
    // leads with a PERSISTENT banner saying exactly that (never a transient toast the
    // author can miss), every regeneration also lands in the SystemLog, and the one-way
    // "Detach to manual editing" button is the sanctioned way to stop it for good.
    //
    // Fallback + per-kind media rows are built imperatively (the kind list is
    // variable-length — EnsureCanonicalKinds tops it up when Hub grows a new alert family,
    // and author-invented kinds survive), mirroring RefreshTriggersSection.

    private void RefreshAlertBoxSection(VisualistViewModel vm)
    {
        if (AlertBoxSection is null) return;

        LayerWidget? widget = vm.SelectedWidget;
        bool show = widget is { Preset: WidgetPreset.AlertBox };

        AlertBoxSection.Visibility        = show ? Visibility.Visible : Visibility.Collapsed;
        AlertBoxSectionDivider.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            AlertBoxRowsPanel.Children.Clear();
            _alertBoxDetachArmed = false;
            return;
        }

        // A commit re-fires SelectedLayer; rebuilding the rows here would tear down the
        // TextBox that just committed on Enter while it still owns focus. The model and
        // the controls are already in agreement at that point (the value came FROM the
        // control), so the rebuild has nothing to do — only the banner, which is
        // persistent XAML and safe to retouch, can have changed.
        bool rebuildRows = !_alertBoxCommitInFlight;

        // ★ Two model writes in a REFRESH path, both deliberate. A widget can legitimately
        // arrive here tagged AlertBox with no settings at all (the WidgetView preset pill
        // writes the tag and nothing else), and the rows the section renders ARE the
        // settings — so materialising them is what makes the section editable at all, and a
        // display-only clone would silently swallow the first edit made into it.
        //
        // Neither write MarkDirty()s, on purpose: selecting a widget must not put the
        // document into an unsaved state. Both are additive and idempotent, so if the author
        // never edits anything the widget stays exactly as tag-only as it was, and the first
        // real edit persists the whole settings object through the normal commit path.
        AlertBoxSettings settings = widget!.AlertBox ??= AlertBoxSettings.CreateDefault();
        bool detached = settings.Detached;

        // Banner: FOUR states. Detached / name-collision / compiled / NOT YET compiled.
        //
        // ★ The third state is not hypothetical and the banner must not lie about it. A widget
        // can sit here tagged AlertBox with CompiledTriggerName empty — the canvas preset PILL
        // writes only the tag, and a compile can also have been refused. The banner used to fall
        // back to ResolvedTriggerName and still read "these settings OWN the … graph: every
        // change REGENERATES it", asserting ownership of a graph that does not exist and a
        // trigger that has no row in the TRIGGERS list below. A streamer then points the Alerts
        // tool at onTrigger:alert and nothing ever fires, with every surface saying it is fine.
        //
        // ★ And the FOURTH state exists because the third one's remedy is wrong for it. The
        // "edit any field … to generate it" instruction is true only when the resolved trigger
        // name is FREE. In the collision state — the resolved name is held by a hand-authored
        // trigger the compiler must not overwrite — editing the message, the duration or a media
        // path refuses again, forever: CompiledTriggerName stays empty, so this banner is the
        // PERSISTENT surface the author keeps reading while the 7-second inline feedback line has
        // long since disappeared. Only the two remedies named below can ever work. It is keyed
        // off WidgetPresets.AlertBoxTriggerNameTaken — the same pure predicate the VM's commit and
        // ApplyPreset pre-flight with, so the banner and the refusal cannot disagree.
        //
        // The collision is tested BEFORE `compiled`, because both can hold at once: an author who
        // renames TriggerId onto a hand-built trigger keeps the ownership record of the PREVIOUS
        // compile, and in that state the collision is the actionable fact — "these settings own
        // <old name>" is true but tells them nothing about why their edits stopped landing.
        //
        // ★ …and that same overlap is why the collision arm itself has TWO sub-states. The arm used
        // to assert flatly that nothing will fire, which is false in exactly the renamed-onto-a-
        // hand-trigger case just described: RegenerateAlertBox refuses in its ownership PRE-FLIGHT,
        // which runs ahead of the rename cleanup, so the previously compiled trigger and its whole
        // alert graph survive untouched — and Hub matches a widget's trigger by NAME, so alerts keep
        // firing on the OLD name. Telling that author "nothing will fire" sends them debugging an
        // overlay that works. The distinguishing fact is whether a previous compile is still
        // INSTALLED (`priorLive`), which is a strictly stronger test than `compiled`: the ownership
        // record can outlive the trigger it names if the author deleted it by hand, and then nothing
        // really does fire.
        //
        // Every state is stated as a PROMISE plus the gesture that fulfils it, and NONE of them
        // regenerates from here: this is a refresh path, and a render must never have a compiling
        // side effect — RefreshAlertBoxSection runs on every selection change and every commit
        // echo. AlertBoxTriggerNameTaken is pure (it reads, and its no-settings stand-in is a
        // local), so calling it from a render is safe.
        bool compiled = settings.CompiledTriggerName is { Length: > 0 };
        bool nameTaken = !detached && AlertPresets.AlertBoxTriggerNameTaken(widget);
        // A previous compile that is STILL INSTALLED — the ownership record alone is not enough,
        // because the author can have deleted that trigger in the TRIGGERS list below while the
        // record survives. Only this can decide whether something is still firing. Read-only.
        bool priorLive = compiled
                         && widget.Triggers is { } ownedLookup
                         && ownedLookup.Exists(t => t is not null && string.Equals(
                                t.Name, settings.CompiledTriggerName, StringComparison.OrdinalIgnoreCase));
        string collidingName = settings.ResolvedTriggerName
                               ?? ("onTrigger:" + AlertBoxSettings.DefaultTriggerId);
        AlertBoxOwnershipDot.Fill = (Brush)Application.Current.Resources[
            detached ? "OkBrush" : nameTaken ? "ErrBrush" : "WarnBrush"];
        AlertBoxOwnershipText.Text = detached
            ? Localizer.T("visualist.inspector.alertbox.banner.detached",
                "Detached — this widget's graph is yours now. These settings are kept for "
                + "reference but no longer generate anything, and there is no re-attach "
                + "(undo the detach if it was a mistake).")
            : nameTaken
              ? string.Format(
                    Localizer.T("visualist.inspector.alertbox.banner.blocked_head_format",
                        "BLOCKED — \"{0}\" already exists on this widget as a "
                        + "hand-built trigger, and these settings will not overwrite it, so no alert "
                        + "graph is being generated for that name. "),
                    collidingName)
                + (priorLive
                   ? string.Format(
                        Localizer.T("visualist.inspector.alertbox.banner.blocked_prior_live_format",
                            "Your previously generated \"{0}\" trigger is "
                            + "still installed, so alerts KEEP FIRING on that older name — this Alert Box "
                            + "is not dead, it is stuck one rename behind. "),
                        settings.CompiledTriggerName)
                   : Localizer.T("visualist.inspector.alertbox.banner.blocked_nothing_live",
                        "Nothing generated by these settings is installed on this widget, so nothing "
                        + "will fire. "))
                + Localizer.T("visualist.inspector.alertbox.banner.blocked_tail",
                    "Editing the message, the "
                    + "duration or a media path will keep being refused. There are exactly two ways "
                    + "out: give this Alert Box a different trigger name above, or rename (or delete) "
                    + "that trigger in the TRIGGERS list below.")
              : compiled
                ? string.Format(
                    Localizer.T("visualist.inspector.alertbox.banner.owned_format",
                        "These settings OWN the widget's \"{0}"
                        + "\" graph: every change here REGENERATES it, so anything you hand-edit in "
                        + "the widget-graph editor is replaced on the next edit. Detach below to take "
                        + "the graph over permanently."),
                    settings.CompiledTriggerName)
                : string.Format(
                    Localizer.T("visualist.inspector.alertbox.banner.not_generated_format",
                        "No alert graph has been generated yet — the \"{0}"
                        + "\" trigger does not exist on this widget, so nothing will fire. Edit any "
                        + "field in this section (trigger name, message, duration or a media path) to "
                        + "generate it. From then on these settings OWN that graph and every change "
                        + "REGENERATES it."),
                    collidingName);

        // The three scalar boxes are persistent XAML, so seeding them is always safe;
        // _suppressEcho stops the seed write from re-entering the commit handlers.
        _suppressEcho = true;
        try
        {
            AlertBoxTriggerBox.Text  = settings.TriggerId ?? "";
            AlertBoxMessageBox.Text  = settings.MessageFormat ?? "";
            AlertBoxDurationBox.Value = Math.Round(
                Math.Clamp(double.IsFinite(settings.DurationMs) ? settings.DurationMs : 4000, 0, 600000) / 1000.0, 3);
        }
        finally { _suppressEcho = false; }

        // Detached settings stay READABLE but are no longer editable — leaving them live
        // would invite an edit that silently does nothing.
        AlertBoxTriggerBox.IsEnabled  = !detached;
        AlertBoxMessageBox.IsEnabled  = !detached;
        AlertBoxDurationBox.IsEnabled = !detached;
        AlertBoxDetachButton.Visibility = detached ? Visibility.Collapsed : Visibility.Visible;
        _alertBoxDetachArmed = false;
        AlertBoxDetachLabel.Text = Localizer.T(
            "visualist.inspector.alertbox.detach.label", "Detach to manual editing…");
        AlertBoxDetachButton.Foreground = (Brush)Application.Current.Resources["CoalBodyTextBrush"];

        if (!rebuildRows) return;

        AlertBoxRowsPanel.Children.Clear();
        // Top up any alert kind these settings predate (see the write-in-refresh note above)
        // so a family Hub grew has somewhere to be configured the first time the author
        // opens the widget — no .phxlayer migration, no silent gap.
        settings.EnsureCanonicalKinds();

        string imgLabel = Localizer.T("visualist.inspector.alertbox.row.img.label", "img");
        string sndLabel = Localizer.T("visualist.inspector.alertbox.row.snd.label", "snd");

        AlertBoxRowsPanel.Children.Add(MakeAlertGroupLabel(
            Localizer.T("visualist.inspector.alertbox.fallback.label", "fallback"),
            Localizer.T("visualist.inspector.alertbox.fallback.tip",
                "Shown for any alert kind with no media of its own — including a kind this "
                + "build has never heard of (Hub labels an unmapped family generically).")));
        AlertBoxRowsPanel.Children.Add(BuildAlertMediaRow(
            widget, imgLabel, settings.FallbackImage, detached, v => settings.FallbackImage = v));
        AlertBoxRowsPanel.Children.Add(BuildAlertMediaRow(
            widget, sndLabel, settings.FallbackSound, detached, v => settings.FallbackSound = v));

        foreach (AlertBoxKindRow row in settings.Rows)
        {
            if (row is null) continue;
            AlertBoxKindRow captured = row;
            AlertBoxRowsPanel.Children.Add(MakeAlertGroupLabel(
                row.Kind, Localizer.T("visualist.inspector.alertbox.kind.tip",
                    "Matched against the alert's Args1 field, exactly and "
                    + "case-sensitively. Leave both boxes empty to use the fallback.")));
            AlertBoxRowsPanel.Children.Add(BuildAlertMediaRow(
                widget, imgLabel, row.Image, detached, v => captured.Image = v));
            AlertBoxRowsPanel.Children.Add(BuildAlertMediaRow(
                widget, sndLabel, row.Sound, detached, v => captured.Sound = v));
        }
    }

    // A kind / group header line inside the media-row list.
    private FrameworkElement MakeAlertGroupLabel(string text, string tip)
    {
        var tb = new TextBlock
        {
            Text       = string.IsNullOrWhiteSpace(text)
                ? Localizer.T("visualist.inspector.alertbox.kind.unnamed", "(unnamed kind)")
                : text,
            FontFamily = MonoFontFamily(),
            FontSize   = 10,
            Margin     = new Thickness(0, 6, 0, 0),
            Foreground = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(tb, tip);
        return tb;
    }

    /// <summary>
    /// One media path row: a 28px mono label, a TextBox, and a Browse… chip that reuses
    /// the same <see cref="MediaPickerDialog"/> the node-level MediaPath editor uses (so a
    /// picked path is relative to <c>data/media</c> by construction).
    ///
    /// <para>Commits on LostFocus / Enter / pick. Every commit regenerates the compiled
    /// graph through the VM, and a path that will not play is reported inline BEFORE the
    /// commit — an Alert Box wires all of its paths, and the wired-path guard refuses a
    /// non-relative one at render time with no visible trace beyond a browser diagnostic.
    /// </para>
    /// </summary>
    private FrameworkElement BuildAlertMediaRow(
        LayerWidget widget, string label, string current, bool readOnly, Action<string> write)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text       = label,
            FontFamily = MonoFontFamily(),
            FontSize   = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["CoalMutedTextBrush"],
        };
        var box = new TextBox
        {
            Text       = current ?? "",
            FontFamily = MonoFontFamily(),
            FontSize   = 11,
            IsEnabled  = !readOnly,
            PlaceholderText = "alerts/…",
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(box, Localizer.T("visualist.inspector.alertbox.row.path.tip",
            "Path relative to data/media (e.g. alerts/follow.png). Empty = use the fallback."));

        void Commit(string raw)
        {
            if (_suppressEcho || readOnly) return;
            string v = (raw ?? "").Trim();
            if (string.Equals(v, (current ?? "").Trim(), StringComparison.Ordinal)) return;
            // ★ The write is handed to the commit as a callback rather than performed here.
            // LayerDocument.PushUndo serialises the CURRENT layer, so writing first would put
            // the new path INSIDE the "before" snapshot: Ctrl+Z would restore the graph the
            // commit regenerated but leave the bad path visible in this very box, which reads
            // as "undo did nothing" and invites a second edit that destroys the graph again.
            CommitAlertBoxFromInspector(
                widget,
                AlertBoxSettings.DescribeMediaPathProblem(v),
                _ => { current = v; write(v); });
        }

        box.LostFocus += (_, _) => Commit(box.Text);
        box.KeyDown += (_, ev) =>
        {
            if (ev.Key == Windows.System.VirtualKey.Enter) { Commit(box.Text); ev.Handled = true; }
        };

        var browse = MakeChip(
            Localizer.T("visualist.inspector.alertbox.row.browse.label", "Browse…"),
            Localizer.T("visualist.inspector.alertbox.row.browse.tip", "Pick a media file from data/media"));
        browse.IsEnabled = !readOnly;
        browse.Click += async (_, _) =>
        {
            try
            {
                if (XamlRoot is null) return;
                var dlg = new MediaPickerDialog { XamlRoot = XamlRoot };
                await dlg.ShowAsync();
                if (dlg.SelectedRelativePath is { Length: > 0 } rel)
                {
                    _suppressEcho = true;
                    try { box.Text = rel; } finally { _suppressEcho = false; }
                    Commit(rel);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("InspectorPanel", "AlertBox media Browse", ex);
            }
        };

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(box, 1);
        Grid.SetColumn(browse, 2);
        grid.Children.Add(lbl);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        return grid;
    }

    // trigger / message boxes commit on focus-out and Enter, matching every other string
    // field in this panel (one undo entry per gesture, not per keystroke).
    private void OnAlertBoxFieldLostFocus(object sender, RoutedEventArgs e) => CommitAlertBoxScalars();

    private void OnAlertBoxFieldKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        CommitAlertBoxScalars();
        e.Handled = true;
    }

    private void OnAlertBoxDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressEcho) return;
        if (double.IsNaN(args.NewValue)) return;
        CommitAlertBoxScalars();
    }

    private void CommitAlertBoxScalars()
    {
        if (_suppressEcho) return;
        if (_vm?.SelectedWidget is not { Preset: WidgetPreset.AlertBox } widget) return;
        if (widget.AlertBox is not { } settings || settings.Detached) return;

        string trigger = (AlertBoxTriggerBox.Text ?? "").Trim();
        string message = AlertBoxMessageBox.Text ?? "";
        double seconds = double.IsNaN(AlertBoxDurationBox.Value) ? 0 : AlertBoxDurationBox.Value;
        double ms      = Math.Clamp(seconds, 0, 600) * 1000.0;

        bool changed = !string.Equals(trigger, settings.TriggerId ?? "", StringComparison.Ordinal)
                    || !string.Equals(message, settings.MessageFormat ?? "", StringComparison.Ordinal)
                    || Math.Abs(ms - settings.DurationMs) > 0.5;
        if (!changed) return;

        // An invalid trigger id is refused by the compiler, and refusing it while KEEPING
        // the author's text would leave the box showing something that isn't what compiled.
        // Report and revert the box instead — the model keeps the working value.
        if (!AlertBoxSettings.IsValidTriggerId(trigger))
        {
            _suppressEcho = true;
            try { AlertBoxTriggerBox.Text = settings.TriggerId ?? ""; } finally { _suppressEcho = false; }
            ShowAlertBoxFeedback(
                string.Format(
                    Localizer.T("visualist.inspector.alertbox.feedback.invalid_trigger_format",
                        "\"{0}\" is not a valid trigger name — a letter first, then letters, "
                        + "digits or underscores. Kept the previous value."),
                    trigger), warn: true);
            return;
        }

        // Same callback discipline as the media rows: the three assignments happen INSIDE the
        // commit, after its undo snapshot, so one Ctrl+Z takes back both the settings and the
        // graph they regenerated.
        CommitAlertBoxFromInspector(widget, null, s =>
        {
            s.TriggerId     = trigger;
            s.MessageFormat = message;
            s.DurationMs    = ms;
        });
    }

    /// <summary>
    /// Push a settings edit through the VM (undo → <paramref name="mutate"/> → regenerate →
    /// dirty → rebroadcast) and report the outcome inline.
    ///
    /// <para><paramref name="mutate"/> carries the actual field write. It is NOT performed by
    /// the caller: <c>PushUndo</c> snapshots the current layer, so a caller that writes first
    /// leaves Ctrl+Z restoring the graph without restoring the setting that replaced it. The VM
    /// owns the ordering — see <c>VisualistViewModel.CommitAlertBoxSettings</c>.</para>
    ///
    /// <para><paramref name="pathProblem"/> carries a media-path warning when the value that
    /// was just written will not play — it is surfaced even on a SUCCESSFUL commit, because the
    /// graph compiles perfectly and then silently renders nothing.</para>
    /// </summary>
    private void CommitAlertBoxFromInspector(
        LayerWidget widget, string? pathProblem, Action<AlertBoxSettings>? mutate = null)
    {
        // A row's commit closure can outlive the VM binding (a media-picker dialog is
        // awaited, the panel is recycled underneath it, then the pick resolves).
        if (_vm is not { } vm) return;

        _alertBoxCommitInFlight = true;
        AlertRegen result;
        try { result = vm.CommitAlertBoxSettings(widget, mutate); }
        finally { _alertBoxCommitInFlight = false; }

        ReportAlertBoxResult(widget, result, pathProblem);
    }

    /// <summary>
    /// Turn a compile outcome into the inline feedback line. Shared by every surface that can
    /// trigger a compile — the settings fields, the media rows AND the preset picker — so no
    /// path can regenerate (or silently refuse to) without saying so.
    /// </summary>
    private void ReportAlertBoxResult(LayerWidget widget, AlertRegen result, string? pathProblem)
    {
        switch (result)
        {
            case AlertRegen.Regenerated:
                if (pathProblem is not null)
                    ShowAlertBoxFeedback(string.Format(
                        Localizer.T("visualist.inspector.alertbox.feedback.path_problem_format",
                            "Saved, but that path {0}"),
                        pathProblem), warn: true);
                else
                    ShowAlertBoxFeedback(Localizer.T(
                        "visualist.inspector.alertbox.feedback.regenerated",
                        "Alert graph regenerated from settings."));
                break;
            case AlertRegen.Detached:
                ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.feedback.detached",
                    "This Alert Box is detached — settings no longer regenerate its graph."), warn: true);
                break;
            case AlertRegen.InvalidTriggerId:
                ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.feedback.invalid_id",
                    "Trigger name is invalid — nothing was compiled."), warn: true);
                break;
            case AlertRegen.TriggerNameTaken:
                // The two ways out are spelled out because neither is guessable from "taken",
                // and the widget is untouched either way — nothing has been lost yet.
                ShowAlertBoxFeedback(
                    string.Format(
                        Localizer.T("visualist.inspector.alertbox.feedback.name_taken_format",
                            "\"{0}\" is already a "
                            + "hand-built trigger on this widget, so nothing was generated — the graph you "
                            + "authored there is untouched. Either give this Alert Box a different trigger "
                            + "id above, or rename that trigger in the TRIGGERS list below."),
                        widget.AlertBox?.ResolvedTriggerName ?? "onTrigger:alert"),
                    warn: true);
                break;
            case AlertRegen.RegistryUnavailable:
                ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.feedback.registry_unavailable",
                    "Node templates unavailable — nothing was compiled. Check the System Log."), warn: true);
                break;
            case AlertRegen.Failed:
                // Deliberately worded like RegistryUnavailable: both mean "the commit did not
                // happen and the reason is in the log". This arm exists because a caught throw
                // used to arrive as NotAnAlertBox and print the line below — so the ONE outcome
                // that needs the author in the System Log read as a claim about their selection.
                ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.feedback.failed",
                    "Something went wrong — nothing was compiled. Check the System Log."), warn: true);
                break;
            case AlertRegen.NoDocument:
                // The layer went away while this edit was in flight — the media-row Browse… chip
                // awaits a dialog, so its commit closure can outlive the document. Reported as
                // NotAnAlertBox this printed a false claim about the selection; reported as Failed
                // it sent the author to a log that had nothing in it. Says what happened, and the
                // VM now writes a matching System Log line.
                ShowAlertBoxFeedback(
                    Localizer.T("visualist.inspector.alertbox.feedback.no_document",
                        "This layer is no longer open, so the edit could not be saved. Re-open the "
                        + "layer and make it again."), warn: true);
                break;
            default:
                // NotAnAlertBox only, now that Failed and NoDocument have their own arms.
                ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.feedback.not_alertbox",
                    "Not an Alert Box widget."), warn: true);
                break;
        }
    }

    // Two-step, in-place confirm. First click ARMS (relabels + reddens the button), second
    // click detaches. Never a ContentDialog — this panel keeps modals out entirely — and an
    // arm/confirm pair also means one stray click cannot perform an irreversible action.
    private void OnAlertBoxDetach(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedWidget is not { Preset: WidgetPreset.AlertBox } widget) return;
        if (widget.AlertBox is { Detached: true }) return;

        if (!_alertBoxDetachArmed)
        {
            _alertBoxDetachArmed = true;
            AlertBoxDetachLabel.Text = Localizer.T("visualist.inspector.alertbox.detach.armed_label",
                "Click again to detach — this cannot be undone except by Ctrl+Z");
            AlertBoxDetachButton.Foreground = (Brush)Application.Current.Resources["ErrBrush"];
            ShowAlertBoxFeedback(
                Localizer.T("visualist.inspector.alertbox.detach.warning",
                    "Detaching hands the compiled graph to you permanently: these settings stop "
                    + "generating it and there is no re-attach."), warn: true);
            return;
        }

        _alertBoxDetachArmed = false;
        if (_vm.DetachAlertBox(widget))
        {
            RefreshAlertBoxSection(_vm);
            ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.detach.done",
                "Detached — the graph is now hand-owned and will not be regenerated."));
        }
        else
        {
            RefreshAlertBoxSection(_vm);
            ShowAlertBoxFeedback(Localizer.T("visualist.inspector.alertbox.detach.failed",
                "Detach failed — check the System Log."), warn: true);
        }
    }

    // Inline, non-modal outcome line. Its OWN timer rather than the TRIGGERS section's:
    // _feedbackTimer's Tick handler is a closure created in CreateFeedbackTimer that hides
    // TriggerActionFeedback, so sharing it would cross-hide the two messages and leak this
    // section's longer warn interval into every trigger toast. Warnings linger longer than
    // confirmations — a media-path warning is the one message the author must actually read.
    private void ShowAlertBoxFeedback(string message, bool warn = false)
    {
        if (AlertBoxFeedback is null) return;
        AlertBoxFeedback.Text = message;
        AlertBoxFeedback.Foreground = (Brush)Application.Current.Resources[warn ? "WarnBrush" : "OkBrush"];
        AlertBoxFeedback.Visibility = Visibility.Visible;

        if (_alertBoxFeedbackTimer is null)
        {
            _alertBoxFeedbackTimer = new DispatcherTimer();
            _alertBoxFeedbackTimer.Tick += (_, _) =>
            {
                _alertBoxFeedbackTimer?.Stop();
                if (AlertBoxFeedback is not null) AlertBoxFeedback.Visibility = Visibility.Collapsed;
            };
        }
        _alertBoxFeedbackTimer.Stop();
        _alertBoxFeedbackTimer.Interval = TimeSpan.FromSeconds(warn ? 7 : 2.5);
        _alertBoxFeedbackTimer.Start();
    }

    // ─── Fusion-style label drag-scrub ────────────────────────────────────
    //
    // Press + drag horizontally on a numeric field's LABEL to nudge the paired
    // NumberBox. dx pixels → value delta; a faster flick scales the per-pixel step
    // up, Shift = fine (0.1×). Writes ride the NumberBox's own ValueChanged commit
    // path, so the whole drag lands as ONE undo entry via the widget-edit gesture
    // (BeginWidgetEditGesture snapshots undo once on press). Mirrors the canvas
    // pointer-capture gestures: left-button-held guard + capture-lost recovery. The
    // label is a transparent Border so its whole cell is grabbable. A private
    // accumulator carries sub-integer drags so slow scrubs still advance.
    private void AttachLabelScrub(Border label, NumberBox box, double baseStep, double min, double max)
    {
        bool   active = false;
        uint   pointerId = 0;
        double lastX = 0;
        double accum = 0;

        label.PointerPressed += (s, e) =>
        {
            var pp = e.GetCurrentPoint(label);
            if (!pp.Properties.IsLeftButtonPressed) return;
            if (_vm?.SelectedWidget is null) return;
            active    = true;
            pointerId = e.Pointer.PointerId;
            lastX     = pp.Position.X;
            accum     = double.IsNaN(box.Value) ? 0 : box.Value;
            BeginWidgetEditGesture();
            label.CapturePointer(e.Pointer);
            e.Handled = true;
        };

        void EndScrub()
        {
            if (!active) return;
            active = false;
            EndWidgetEditGesture();
        }

        label.PointerMoved += (s, e) =>
        {
            if (!active || e.Pointer.PointerId != pointerId) return;
            var pp = e.GetCurrentPoint(label);
            if (!pp.Properties.IsLeftButtonPressed) { EndScrub(); return; }
            double x  = pp.Position.X;
            double dx = x - lastX;
            lastX = x;
            if (dx == 0) return;
            bool   fine  = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == Windows.System.VirtualKeyModifiers.Shift;
            double speed = Math.Abs(dx);
            double mult  = speed > 10 ? 4.0 : speed > 4 ? 2.0 : 1.0;
            double step  = baseStep * mult * (fine ? 0.1 : 1.0);
            accum = Math.Clamp(accum + dx * step, min, max);
            double snapped = Math.Round(accum);
            if (Math.Abs(box.Value - snapped) >= 0.5)
                box.Value = snapped;   // fires ValueChanged → gesture-batched commit
        };

        label.PointerReleased    += (s, e) => { if (e.Pointer.PointerId == pointerId) EndScrub(); };
        label.PointerCaptureLost += (s, e) => EndScrub();
        label.PointerCanceled    += (s, e) => EndScrub();
    }

    // One undo snapshot per drag gesture. The geometry boxes are persistent XAML,
    // so MarkDirty can fire live per-move without tearing them down — only the
    // undo push is batched here.
    private void BeginWidgetEditGesture()
    {
        EndWidgetEditGesture();          // flush any dangling gesture (missed release)
        _vm?.Document?.PushUndo();
        _widgetEditGestureActive = true;
    }

    private void EndWidgetEditGesture()
    {
        if (!_widgetEditGestureActive) return;
        _widgetEditGestureActive = false;
        // The scrub streamed one commit per pointer-move and each of those
        // skipped the live publish; fan the settled geometry out once here so a
        // label scrub costs the bus a single WIDGET_UPDATE, exactly like the
        // canvas's publish-on-drag-end.
        PublishLiveWidgetEdit(WidgetEditTargets());
    }

    private void OnDeleteWidget(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        // Multi-select: Delete removes ALL selected in one undo (matches the
        // "edits apply to all" banner + the canvas Del key); else the anchor.
        bool removed = _vm.SelectedWidgets.Count > 1
            ? _vm.DeleteSelectedWidgets()
            : _vm.DeleteSelectedWidget();
        if (removed) TriggerLayerRefresh();
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

        // ★ V8 — which of these rows is GENERATED. The disclosure has to be here, not only in
        // the ALERT BOX section: this list (and the widget-graph editor it opens) is where an
        // author hand-edits, and a compiled trigger row is otherwise indistinguishable from one
        // they wrote. Someone who applied the AlertBox preset from the preset gallery never saw
        // the ownership banner at all, so without this chip they can author into a
        // settings-owned graph for an hour and lose it to the next settings field edit.
        // Null once detached — a detached graph is hand-owned and regenerates never again.
        string? generatedTriggerName =
            widget is { Preset: WidgetPreset.AlertBox, AlertBox: { Detached: false } ab }
            && ab.CompiledTriggerName is { Length: > 0 } compiled
                ? compiled
                : null;

        var triggers = widget!.Triggers;
        if (triggers.Count == 0)
        {
            TriggersEmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            TriggersEmptyHint.Visibility = Visibility.Collapsed;
            foreach (WidgetTrigger t in triggers)
                TriggerRowsPanel.Children.Add(BuildTriggerRow(t, saved, generatedTriggerName));
        }

        UpdateTriggerTestButtons();
    }

    private FrameworkElement BuildTriggerRow(WidgetTrigger trigger, bool saved, string? generatedTriggerName)
    {
        // A trigger row is now two stacked sub-rows: the name + action chips on
        // top, and a per-trigger master-volume slider beneath. onStartup keeps its
        // read-only top row (auto-fires hint, no rename) but still gets a volume.
        var outer = new StackPanel { Spacing = 2 };

        // Columns: name · generated-chip · Test · Copy · Delete. The chip column is Auto and
        // stays empty for a hand-authored row, so nothing shifts for the common case.
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool isStartup = string.Equals(trigger.Name, StartupTriggerName, StringComparison.OrdinalIgnoreCase);
        bool isGenerated = generatedTriggerName is { Length: > 0 } gen
                        && string.Equals(trigger.Name, gen, StringComparison.OrdinalIgnoreCase);

        var name = new TextBlock
        {
            Text         = trigger.Name,
            FontFamily   = MonoFontFamily(),
            FontSize     = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground   = (Brush)Application.Current.Resources["CoalBodyTextBrush"],
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // onStartup is the idle loop: it auto-fires on layer load
        // and can't be Architect-triggered, so it gets NO Test / Copy / Delete /
        // rename. Show a tiny "auto-fires on load" hint in their place instead.
        if (isStartup)
        {
            ToolTipService.SetToolTip(name, trigger.Name);
            var hint = new TextBlock
            {
                Text         = Localizer.T("visualist.inspector.triggers.startup.hint", "auto-fires on load"),
                FontFamily   = MonoFontFamily(),
                FontSize     = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground   = (Brush)Application.Current.Resources["CoalMutedTextBrush"],
            };
            ToolTipService.SetToolTip(hint, Localizer.T("visualist.inspector.triggers.startup.tip",
                "onStartup is the idle loop — it runs automatically when the layer loads and can't be triggered from Architect."));
            Grid.SetColumn(hint, 1);
            Grid.SetColumnSpan(hint, 4);
            grid.Children.Add(hint);
            outer.Children.Add(grid);
            outer.Children.Add(BuildTriggerVolumeRow(trigger));
            return outer;
        }

        // Non-startup: double-click the name to rename (reuses the modal name
        // prompt + VM.RenameTrigger; a bad / duplicate name surfaces inline).
        ToolTipService.SetToolTip(name, isGenerated
            ? string.Format(
                Localizer.T("visualist.inspector.triggers.row.tip_generated_format",
                    "{0}  ·  generated from the ALERT BOX settings  ·  double-click to rename"),
                trigger.Name)
            : string.Format(
                Localizer.T("visualist.inspector.triggers.row.tip_format",
                    "{0}  ·  double-click to rename"),
                trigger.Name));
        var capturedForRename = trigger;
        name.DoubleTapped += (s, e) => OnRenameTrigger(capturedForRename);

        if (isGenerated)
        {
            var chip = MakeGeneratedChip();
            Grid.SetColumn(chip, 1);
            grid.Children.Add(chip);
        }

        var test = MakeChip(
            Localizer.T("visualist.inspector.triggers.test.label", "Test"),
            Localizer.T("visualist.inspector.triggers.test.tip", "Run this trigger on the live OBS source (Hub bus)"));
        var capturedForTest = trigger;
        test.Click += (s, e) => OnTestTrigger(capturedForTest);
        _testButtons.Add(test);
        Grid.SetColumn(test, 2);
        grid.Children.Add(test);

        var copy = MakeChip(
            Localizer.T("visualist.inspector.triggers.copy.label", "Copy"),
            Localizer.T("visualist.inspector.triggers.copy.tip", "Copy an Architect Visual.Trigger snippet for this trigger"));
        copy.IsEnabled = saved;
        var capturedForCopy = trigger;
        copy.Click += (s, e) => OnCopyTriggerSnippet(capturedForCopy);
        Grid.SetColumn(copy, 3);
        grid.Children.Add(copy);

        var delete = MakeChip(
            Localizer.T("common.button.delete", "Delete"),
            Localizer.T("visualist.inspector.triggers.delete.tip", "Delete this trigger (undoable)"));
        delete.Foreground = (Brush)Application.Current.Resources["ErrBrush"];
        var capturedForDelete = trigger;
        delete.Click += (s, e) => OnDeleteTrigger(capturedForDelete);
        Grid.SetColumn(delete, 4);
        grid.Children.Add(delete);

        outer.Children.Add(grid);
        outer.Children.Add(BuildTriggerVolumeRow(trigger));
        return outer;
    }

    // Per-trigger MASTER volume (0..1) — scales every Audio.Play node in this
    // trigger at runtime. Applies to every trigger row, onStartup included. The
    // thumb drag is batched into one undo + one deferred MarkDirty
    // (Begin/EndTriggerVolGesture) because MarkDirty rebuilds the trigger rows and
    // would otherwise tear down the slider mid-drag.
    private FrameworkElement BuildTriggerVolumeRow(WidgetTrigger trigger)
    {
        var g = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        double v0 = Math.Clamp(trigger.Volume, 0, 1);

        var lbl = new TextBlock
        {
            Text        = Localizer.T("visualist.inspector.trigger.volume.label", "vol"),
            FontFamily  = MonoFontFamily(),
            FontSize    = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground  = (Brush)Application.Current.Resources["CoalMutedTextBrush"],
        };
        var slider = new Slider
        {
            Minimum       = 0,
            Maximum       = 1,
            StepFrequency = 0.05,
            Value         = v0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(slider, Localizer.T("visualist.inspector.trigger.volume.tip",
            "Master volume for this trigger (0–1) — scales every Audio.Play in the graph"));
        var pct = new TextBlock
        {
            Text        = FormatVolPercent(v0),
            FontFamily  = MonoFontFamily(),
            FontSize    = 10,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground  = (Brush)Application.Current.Resources["CoalSecondaryTextBrush"],
        };

        var captured = trigger;
        slider.ValueChanged += (s, e) =>
        {
            if (_suppressEcho) return;
            double v = Math.Clamp(e.NewValue, 0, 1);
            pct.Text = FormatVolPercent(v);
            if (Math.Abs(captured.Volume - v) < 0.0005) return;
            if (_triggerVolGestureActive)
            {
                if (!_triggerVolGestureUndoPushed) { _vm?.Document?.PushUndo(); _triggerVolGestureUndoPushed = true; }
                captured.Volume = v;
                _triggerVolGestureDirty = true;   // MarkDirty deferred to gesture end
            }
            else
            {
                _vm?.Document?.PushUndo();
                captured.Volume = v;
                _vm?.Document?.MarkDirty();
            }
        };
        // Bracket the pointer drag so the stream is one undo + one MarkDirty (at
        // release). handledEventsToo — Slider consumes the raw pointer events.
        slider.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => BeginTriggerVolGesture()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => EndTriggerVolGesture()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => EndTriggerVolGesture()), true);
        slider.AddHandler(UIElement.PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => EndTriggerVolGesture()), true);

        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(pct, 2);
        g.Children.Add(lbl);
        g.Children.Add(slider);
        g.Children.Add(pct);
        return g;
    }

    private static string FormatVolPercent(double v)
        => $"{(int)Math.Round(Math.Clamp(v, 0, 1) * 100)}%";

    private void BeginTriggerVolGesture()
    {
        EndTriggerVolGesture();   // flush any dangling gesture
        _triggerVolGestureActive     = true;
        _triggerVolGestureUndoPushed = false;
    }

    private void EndTriggerVolGesture()
    {
        if (!_triggerVolGestureActive) return;
        _triggerVolGestureActive     = false;
        _triggerVolGestureUndoPushed = false;
        if (_triggerVolGestureDirty)
        {
            _triggerVolGestureDirty = false;
            _vm?.Document?.MarkDirty();
        }
    }

    // Rename a non-startup trigger. Reuses the modal name prompt (user-initiated,
    // not a repeatable rejection — same UX as New Trigger) prefilled with the
    // current name, normalizes it (bare "raid" → "onTrigger:raid"), then routes
    // through VM.RenameTrigger. A dup / invalid name surfaces as inline feedback.
    private async void OnRenameTrigger(WidgetTrigger trigger)
    {
        if (_vm is null) return;
        string? input = await PromptForName(
            Localizer.T("visualist.inspector.triggers.rename.title", "Rename Trigger"),
            "onTrigger:new", trigger.Name);
        if (string.IsNullOrWhiteSpace(input)) return;   // cancelled / empty
        string proposed = VisualistViewModel.NormalizeTriggerName(input);
        if (string.Equals(proposed, trigger.Name, StringComparison.Ordinal)) return;
        if (_vm.RenameTrigger(trigger.Name, proposed))
        {
            RefreshTriggersSection(_vm);
            ShowTriggerFeedback(string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.renamed_format", "Renamed to {0}"),
                proposed));
        }
        else
        {
            ShowTriggerFeedback(string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.rename_failed_format",
                    "Rename failed — \"{0}\" is invalid or already taken."),
                input.Trim()), warn: true);
        }
    }

    /// <summary>
    /// The "generated" marker on a compiled Alert Box trigger row. Inline chrome, never a
    /// dialog — this is a state the author sees on every selection, and modals are kept out of
    /// this panel entirely (a repeatable notice in a modal gets dismissed unread).
    ///
    /// <para>A Border + TextBlock rather than <see cref="MakeChip"/>: chips here are clickable
    /// actions, and this is a label. Tooltip carries the rule AND the way out, because the row
    /// itself cannot say "your edits here are temporary" in the space available.</para>
    /// </summary>
    private static FrameworkElement MakeGeneratedChip()
    {
        var tb = new TextBlock
        {
            Text       = Localizer.T("visualist.inspector.triggers.generated.label", "generated"),
            FontFamily = MonoFontFamily(),
            FontSize   = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["WarnBrush"],
        };
        var border = new Border
        {
            Child           = tb,
            Padding         = new Thickness(5, 1, 5, 1),
            CornerRadius    = new CornerRadius(3),
            Background      = (Brush)Application.Current.Resources["CoalCardBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(border,
            Localizer.T("visualist.inspector.triggers.generated.tip",
                "This trigger's graph is GENERATED from the ALERT BOX settings above. Every edit to "
                + "those settings rebuilds it from scratch, so anything you author in its widget "
                + "graph is replaced on the next settings change. Use \"Detach to manual editing\" in "
                + "the ALERT BOX section to take the graph over permanently."));
        return border;
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
            ShowTriggerFeedback(Localizer.T("visualist.inspector.feedback.obs_url_unsaved",
                "Save the layer first to get its OBS URL."), warn: true);
            return;
        }
        string url = $"http://127.0.0.1:18080/layer/{Uri.EscapeDataString(id)}";
        CopyPlainText(url);
        ShowTriggerFeedback(Localizer.T("visualist.inspector.feedback.obs_url_copied", "Copied OBS URL."));
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
            ShowTriggerFeedback(Localizer.T("visualist.inspector.triggers.feedback.no_widget",
                "Select a widget before adding a trigger."), warn: true);
            return;
        }
        string? name = await PromptForName(
            Localizer.T("visualist.inspector.triggers.new.dialog_title", "New Trigger"),
            "onTrigger:new", "");
        if (string.IsNullOrWhiteSpace(name)) return;   // user cancelled / empty
        WidgetTrigger? added = _vm.AddTrigger(name, out var status);
        if (added is null)
        {
            ShowTriggerFeedback(status switch
            {
                VisualistViewModel.AddTriggerStatus.Duplicate =>
                    string.Format(
                        Localizer.T("visualist.inspector.triggers.feedback.duplicate_format",
                            "A trigger named \"{0}\" already exists."),
                        VisualistViewModel.NormalizeTriggerName(name)),
                VisualistViewModel.AddTriggerStatus.NoWidget =>
                    Localizer.T("visualist.inspector.triggers.feedback.no_widget",
                        "Select a widget before adding a trigger."),
                _ =>
                    string.Format(
                        Localizer.T("visualist.inspector.triggers.feedback.invalid_name_format",
                            "\"{0}\" isn't a valid trigger name — use letters, digits and underscores (e.g. \"raid\" becomes onTrigger:raid)."),
                        name.Trim()),
            }, warn: true);
            return;
        }
        RefreshTriggersSection(_vm);
        ShowTriggerFeedback(string.Format(
            Localizer.T("visualist.inspector.triggers.feedback.added_format", "Added trigger: {0}"),
            added.Name));
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
            ShowTriggerFeedback(string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.deleted_format", "Deleted trigger: {0}"),
                trigger.Name));
        }
        else
        {
            ShowTriggerFeedback(Localizer.T("visualist.inspector.triggers.feedback.delete_failed",
                "Delete failed — see System Log."), warn: true);
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
            PrimaryButtonText = Localizer.T("common.ok", "OK"),
            CloseButtonText   = Localizer.T("common.cancel", "Cancel"),
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
        ShowTriggerFeedback(ok
            ? string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.copied_format", "Copied snippet: {0}"),
                trigger.Name)
            : Localizer.T("visualist.inspector.triggers.feedback.copy_failed", "Copy failed — see System Log."),
            warn: !ok);
    }

    private async void OnTestTrigger(WidgetTrigger trigger)
    {
        if (!VisualistBusClient.Instance.IsConnected)
        {
            ShowTriggerFeedback(Localizer.T("visualist.inspector.triggers.feedback.test_offline",
                "Test unavailable — Hub bus offline."), warn: true);
            return;
        }
        if (ResolveLayerId(_vm) is not { } layerId)
        {
            ShowTriggerFeedback(Localizer.T("visualist.inspector.triggers.feedback.test_unsaved",
                "Test unavailable — save the layer first."), warn: true);
            return;
        }
        LayerWidget? widget = _vm?.SelectedWidget;
        if (widget is null || string.IsNullOrEmpty(widget.Id))
        {
            ShowTriggerFeedback(Localizer.T("visualist.inspector.triggers.feedback.test_no_widget",
                "Test unavailable — no widget selected."), warn: true);
            return;
        }
        try
        {
            await VisualistBusClient.Instance.SendVisualTriggerAsync(layerId, widget.Id, trigger.Name);
            ShowTriggerFeedback(string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.test_sent_format", "Test Run sent: {0}"),
                trigger.Name));
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("InspectorPanel", "Test trigger", ex);
            ShowTriggerFeedback(string.Format(
                Localizer.T("visualist.inspector.triggers.feedback.test_failed_format", "Test failed: {0}"),
                ex.Message), warn: true);
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

        NodeTitleText.Text = string.IsNullOrEmpty(node!.Title)
            ? Localizer.T("visualist.inspector.node.untitled", "(untitled)")
            : node.Title;
        // Header short description — resolved by the VM (V14, Majo's call).
        //
        // This block used to compute its own text (instance Description, else
        // Category) and the comment here justified staying registry-free as a
        // deliberate decoupling from the Track-D-owned templates. Majo overrode
        // that decision: the decoupling's real effect was that the ~60 curated
        // WidgetNodeRegistry tooltips reached no INSPECTOR surface (they were not
        // unreachable everywhere — WidgetNodeReferenceData.BuildNode already used
        // GetTooltip as its docs-summary fallback, so the ones without a
        // WidgetNodeProse entry did show in the node-documentation window; this
        // header is the surface that had none of them). And since Instantiate does
        // not copy a template Description onto the node, the expression collapsed
        // to the Category for every freshly placed node — only a canvas rename
        // (WidgetGraphCanvas.CommitNodeRename, the one writer of Node.Description)
        // could ever make the middle rung fire. So the header now reads VisualistViewModel
        // .SelectedNodeDescription, which adds the tooltip as a first rung and
        // KEEPS Description → Category behind it (see its doc comment — the
        // Category rung is what this line used to render and must not be lost).
        // No new subscription is needed: RefreshNodeForm already re-runs on every
        // selection change, and BuildNodeParams raises the property first.
        string desc = vm.SelectedNodeDescription;
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
    // (canvas keyframe record, undo, the param's own Commit re-clamp). For value
    // changes the cheapest correct refresh is a full rebuild — the param list is
    // small (a handful of rows) and a rebuild re-reads every live value + diamond
    // state. An IsAnimated flip only drives the ◀ ◇/◆ ▶ glyph, so it takes the
    // lightweight cluster refresh instead. Both paths coalesce via a pending
    // flag so N param changes in one burst schedule one queued action, not N.
    // Marshalled to the UI thread defensively. Guarded by _suppressNodeEcho
    // so the rebuild we ourselves trigger via a control edit doesn't recurse.
    private void OnNodeParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressNodeEcho) return;
        if (_vm is not { } vm) return;
        try
        {
            if (string.Equals(e.PropertyName, nameof(NodeParamVm.IsAnimated), StringComparison.Ordinal))
            {
                if (_kfClusterRefreshPending) return;
                _kfClusterRefreshPending = true;
                bool queued = DispatcherQueue?.TryEnqueue(() =>
                {
                    _kfClusterRefreshPending = false;
                    RefreshKeyframeClusters();
                }) == true;
                if (!queued) _kfClusterRefreshPending = false;
                return;
            }

            if (_nodeFormRefreshPending) return;
            _nodeFormRefreshPending = true;
            bool ok = DispatcherQueue?.TryEnqueue(() =>
            {
                _nodeFormRefreshPending = false;
                if (!_suppressNodeEcho) RefreshNodeForm(vm);
            }) == true;
            if (!ok) _nodeFormRefreshPending = false;
        }
        catch (Exception ex)
        {
            _nodeFormRefreshPending = false;
            _kfClusterRefreshPending = false;
            GlobalLogger.Error("InspectorPanel", "OnNodeParamChanged", ex);
        }
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

        // p.Min/p.Max now carry the declared __Range OR the name/type-derived
        // default band computed in BuildNodeParams — use them directly.
        double min = p.Min;
        double max = p.Max;
        if (max <= min) max = min + 1;   // degenerate range guard

        // Int sockets snap the slider to whole steps and round every commit; the
        // paired box arrows also step by 1. Floats keep the fine 1/100th band.
        bool   isInt = p.IsInteger;
        double step  = isInt ? 1.0 : (max - min) / 100.0;
        if (step <= 0) step = isInt ? 1.0 : 0.01;
        double large = isInt ? Math.Max(1, Math.Round((max - min) / 10.0)) : (max - min) / 10.0;

        double seed = isInt ? Math.Round(p.NumberValue) : p.NumberValue;

        var slider = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = Math.Clamp(seed, min, max),
            StepFrequency     = step,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new NumberBox
        {
            Value             = seed,
            SmallChange       = step,
            LargeChange       = large,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            VerticalAlignment = VerticalAlignment.Center,
        };

        slider.ValueChanged += (_, args) =>
        {
            if (_suppressNodeEcho) return;
            double v = isInt ? Math.Round(args.NewValue) : args.NewValue;
            PushNodeValue(() =>
            {
                box.Value     = v;        // sync sibling (suppressed)
                p.NumberValue = v;        // Commit happens inside the VM setter
            }, p);
        };
        AttachSliderUndoGesture(slider);
        box.ValueChanged += (_, args) =>
        {
            if (_suppressNodeEcho) return;
            if (double.IsNaN(args.NewValue)) return;
            double v = isInt ? Math.Round(args.NewValue) : args.NewValue;
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
            OnContent  = Localizer.T("visualist.inspector.node.bool.on", "On"),
            OffContent = Localizer.T("visualist.inspector.node.bool.off", "Off"),
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
        ToolTipService.SetToolTip(swatch, Localizer.T("visualist.inspector.node.color.tip", "Pick a color"));

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
            AttachSliderUndoGesture(slider);
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

        var browse = MakeChip(
            Localizer.T("visualist.inspector.node.media.browse.label", "Browse…"),
            Localizer.T("visualist.inspector.node.media.browse.tip", "Pick a media file from data/media"));
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
        Button prev    = MakeKfMicroButton("◀",
            Localizer.T("visualist.inspector.node.keyframe.prev.tip", "Jump to the previous keyframe on this parameter"));
        Button diamond = MakeKfMicroButton("◇", null);
        Button next    = MakeKfMicroButton("▶",
            Localizer.T("visualist.inspector.node.keyframe.next.tip", "Jump to the next keyframe on this parameter"));

        void Refresh()
        {
            try
            {
                // Single-pass has/on probe — this closure runs per param on
                // every PlayheadMs tick (~30×/s during playback).
                (bool has, bool on) = _vm?.ParamKeyframeState(p) ?? (false, false);
                if (diamond.Content is TextBlock tb) tb.Text = on ? "◆" : "◇";
                diamond.Foreground = has
                    ? (Brush)Application.Current.Resources["SelectionBrush"]
                    : (Brush)Application.Current.Resources["CoalSecondaryTextBrush"];
                ToolTipService.SetToolTip(diamond, !has
                    ? Localizer.T("visualist.inspector.node.keyframe.add_start.tip",
                        "Add a keyframe at the playhead (start animating this parameter)")
                    : (on ? Localizer.T("visualist.inspector.node.keyframe.remove.tip",
                                "On a keyframe — click to remove it")
                          : Localizer.T("visualist.inspector.node.keyframe.add.tip",
                                "Add a keyframe at the playhead")));
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
        AddCurve(Localizer.T("visualist.curve.linear",      "Linear"),      KeyframeCurve.Linear);
        AddCurve(Localizer.T("visualist.curve.ease_in",     "Ease In"),     KeyframeCurve.EaseIn);
        AddCurve(Localizer.T("visualist.curve.ease_out",    "Ease Out"),    KeyframeCurve.EaseOut);
        AddCurve(Localizer.T("visualist.curve.ease_in_out", "Ease In-Out"), KeyframeCurve.EaseInOut);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem
        {
            Text = Localizer.T("visualist.inspector.node.keyframe.remove_animation", "Remove animation"),
        };
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

    // A slider thumb drag streams ValueChanged once per frame; bracket the
    // pointer gesture so the VM batches the stream into ONE undo entry + ONE
    // MarkDirty (Begin/EndNodeAttributeGesture) while per-tick writes keep the
    // canvas preview live. Slider handles the raw pointer events internally,
    // so the hooks must register with handledEventsToo. Keyboard nudges and
    // NumberBox edits never enter a gesture and commit exactly as before.
    private void AttachSliderUndoGesture(Slider slider)
    {
        slider.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _vm?.BeginNodeAttributeGesture()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _vm?.EndNodeAttributeGesture()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _vm?.EndNodeAttributeGesture()), true);
        slider.AddHandler(UIElement.PointerCanceledEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _vm?.EndNodeAttributeGesture()), true);
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
