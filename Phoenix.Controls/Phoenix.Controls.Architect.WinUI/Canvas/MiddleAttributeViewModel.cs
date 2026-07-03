using System;
using System.Collections.Generic;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// View-model for a single middle-attribute row painted on a node body —
/// Design_Orders §5.1: `Key …… [pill]` / `Key …… ☑/☐` for
/// <see cref="Phoenix.Controls.Architect.Core.NodeTemplate.DefaultProperties"/>
/// keys that have NO matching input socket on the node. Pre-fix the only
/// place to author these keys (e.g. <c>Bus.OnMessage.EventType</c>,
/// <c>Logic.If.Operator</c>, <c>Schedule.RunAt.DateTime</c>, ~20 templates)
/// was the right-side inspector — which violated the node-UI rule: the
/// inspector must stay slim, so the affordance has to live on the node body.
///
/// Mirrors the editing contract on <see cref="SocketViewModel"/>:
///   • <see cref="BeginEdit"/> snapshots the baseline,
///   • <see cref="EndEdit"/> commits or rolls back to baseline,
///   • no-op edits return false so the canvas skips the undo-push.
/// Bool rows skip the text editor entirely and toggle on tap, mirroring
/// the <c>InspectorField.OnBoolHitTapped</c> pattern (consumed shape, not
/// modified — a parallel agent owns InspectorField.xaml.cs).
/// </summary>
public sealed class MiddleAttributeViewModel : ObservableObject
{
    private readonly Node _parentNode;
    private readonly string _key;
    private readonly string _defaultValue;
    private readonly bool _isBool;
    private string? _value;
    private bool _isEditing;
    private string? _editBaseline;
    private bool _editActive;

    public MiddleAttributeViewModel(Node parentNode, string key, string defaultValue, bool isBool)
    {
        _parentNode   = parentNode;
        _key          = key ?? string.Empty;
        _defaultValue = defaultValue ?? string.Empty;
        _isBool       = isBool;
        _value        = ResolveInitialValue();
    }

    private string? ResolveInitialValue()
    {
        if (_parentNode?.Attributes == null) return _defaultValue;
        return _parentNode.Attributes.TryGetValue(_key, out var v) && v != null
            ? v
            : _defaultValue;
    }

    /// <summary>Underlying <see cref="Node"/> — used by NodeViewModel write-through paths.</summary>
    public Node ParentNode => _parentNode;

    /// <summary>The <see cref="Phoenix.Controls.Architect.Core.NodeTemplate.DefaultProperties"/>
    /// key this row represents — also the <see cref="Node.Attributes"/> key.</summary>
    public string Key => _key;

    /// <summary>True when the row renders as a boolean toggle (☑/☐) instead of an editable pill.</summary>
    public bool IsBool => _isBool;

    /// <summary>Visibility helper — invert of <see cref="IsBool"/> so the XAML pill column
    /// collapses on bool rows.</summary>
    public bool IsPillRow => !_isBool;

    /// <summary>
    /// True when this middle-attribute row offers a ▾ picker listing available
    /// targets (Process.Spawn → the graph's processes, Macro.Call → its macros)
    /// instead of free-text entry — mirrors the DB.* TableName/Column picker on
    /// <see cref="SocketViewModel.HasDatabankPicker"/>. Picking is also the ONLY
    /// way to actually BIND the call: typing the name free-text wrote
    /// ProcessName/MacroName but never the ProcessId/MacroId the exporter binds
    /// on, so the spawn exported as "not found". The chevron only shows on the
    /// pill row (not the bool case); NodeView.OnMiddleAttrPickerClicked resolves
    /// the live list + performs the bind — this flag just flips the chevron.
    /// </summary>
    public bool HasOptionsPicker
        => !_isBool && NodeGeometry.IsOptionsPickerAttr(_parentNode, _key);

    /// <summary>
    /// Stored attribute value. Setter writes through to
    /// <see cref="Node.Attributes"/> using the same semantics as
    /// <see cref="SocketViewModel.ValuePill"/> — null/empty removes the entry
    /// so a serialised .phxg doesn't carry blank overrides for template
    /// defaults that match the default value.
    /// </summary>
    public string? Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                // edit-loss hardening — guard the write but ALWAYS raise the display
                // notifications below, so even a node with a null Attributes bag still
                // refreshes its pill text instead of silently keeping the stale value.
                var attrs = _parentNode.Attributes;
                if (attrs != null)
                {
                    if (string.IsNullOrEmpty(value))
                        attrs.Remove(_key);
                    else
                        attrs[_key] = value!;
                }
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(BoolGlyph));
                OnPropertyChanged(nameof(IsMultiline));
                OnPropertyChanged(nameof(PillMaxWidth));
                OnPropertyChanged(nameof(PillTextWrapping));
                // Tooltip embeds DisplayText so
                // every value-edit needs to re-raise the tooltip binding or
                // hover keeps showing the stale "Key: old-value" string.
                OnPropertyChanged(nameof(Tooltip));
            }
        }
    }

    /// <summary>
    /// Display text for the pill — the actual value when set, otherwise "—"
    /// so the empty pill still reads as "click to type" (same affordance
    /// shape as <see cref="SocketViewModel.PillDisplayText"/>).
    /// </summary>
    public string DisplayText => string.IsNullOrEmpty(_value) ? "—" : _value!;

    /// <summary>Boolean view of <see cref="Value"/> — true iff the stored string is "true" (case-insensitive).</summary>
    public bool BoolValue
        => !string.IsNullOrEmpty(_value)
           && string.Equals(_value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Glyph for the bool toggle — ☑ when on, ☐ when off. Mirrors
    /// <c>InspectorField.BoolCheckedGlyph / BoolUncheckedGlyph</c> (consumed pattern, not lifted file).</summary>
    public string BoolGlyph => BoolValue ? "☑" : "☐";

    /// <summary>
    /// Hover-tooltip text for the middle-attribute
    /// row. <see cref="Phoenix.Controls.Architect.Core.NodeTemplate"/> has
    /// no per-attribute description map today (only
    /// <c>SocketDescriptions</c> covers per-socket strings); the falling-back
    /// behaviour is to show <c>"{Key}: {DisplayText}"</c> so the user at
    /// least sees the attribute key + its current value on hover instead of
    /// a blank surface. A future enrichment can layer in a template-side
    /// <c>AttributeDescriptions</c> map and surface that here.
    /// </summary>
    public string Tooltip => $"{_key}: {DisplayText}";

    /// <summary>
    /// True while the inline-pill TextBox is showing. Set by the view on
    /// tap, cleared on Enter / Esc / blur via <see cref="EndEdit"/>. Bool
    /// rows never enter this state — they toggle directly.
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetField(ref _isEditing, value);
    }

    /// <summary>
    /// Multi-line affordance — currently inherits the same key-set heuristic
    /// as <see cref="SocketViewModel.IsMultilineAttr"/> (Template / Script /
    /// Payload, or any value containing a newline). None of the ~20 templates
    /// surfaced as middle-attr rows today use those names, but the check
    /// keeps the editor behaviour symmetrical with input pills so a future
    /// template that opts in just works.
    /// </summary>
    public bool IsMultiline => NodeGeometry.IsMultilinePill(_key, _value);

    /// <summary>The pill's MaxWidth — bound by NodeView.xaml as the
    /// finite MEASURE-time constraint that forces the value to wrap WITHIN the node
    /// body instead of overflowing its right edge (which the rounded border then
    /// clips). It is the node's actual available width on this row
    /// (<see cref="NodeGeometry.PillWrapWidth"/> with <see cref="NodeGeometry.MiddleAttrPillLead"/>),
    /// capped at <see cref="NodeGeometry.WrapPillCap"/> / <see cref="NodeGeometry.MultilinePillCap"/>.
    /// The node grows wide-first toward that cap, then the pill wraps and the node
    /// grows tall (height measured at the SAME width). Re-raised on value change and
    /// via <see cref="RaisePillConstraint"/> when a sibling pill changes the body width.</summary>
    public double PillMaxWidth
        => NodeGeometry.PillWrapWidth(
            _parentNode,
            NodeGeometry.MiddleAttrPillLead(_key)
              + (HasOptionsPicker ? NodeGeometry.MiddleAttrPickerChevronWidth : 0.0),
            IsMultiline, isDb: false);

    /// <summary>Re-raise <see cref="PillMaxWidth"/> — called by
    /// NodeViewModel when the node body width changes (e.g. a sibling row's pill
    /// edit) so this pill re-evaluates the width it may wrap within.</summary>
    internal void RaisePillConstraint() => OnPropertyChanged(nameof(PillMaxWidth));

    /// <summary>Text-wrapping mode for the read-only pill TextBlock. Always
    /// <see cref="Microsoft.UI.Xaml.TextWrapping.Wrap"/> now — middle-attr pills
    /// have no DB single-line case, so every value wraps to show in full rather
    /// than ellipsis-truncating. Mirrors
    /// <see cref="SocketViewModel.PillTextWrapping"/> for the non-DB path.</summary>
    public Microsoft.UI.Xaml.TextWrapping PillTextWrapping
        => Microsoft.UI.Xaml.TextWrapping.Wrap;

    /// <summary>Text-trimming for the read-only pill TextBlock — always None:
    /// middle-attr pills wrap, so they must never truncate. Mirrors
    /// the non-DB branch of <see cref="SocketViewModel.PillTextTrimming"/>.</summary>
    public Microsoft.UI.Xaml.TextTrimming PillTextTrimming
        => Microsoft.UI.Xaml.TextTrimming.None;

    /// <summary>Snapshot the current value as the rollback baseline. Mirrors
    /// <see cref="SocketViewModel.BeginValuePillEdit"/>.</summary>
    public void BeginEdit()
    {
        if (_isBool) return;
        _editBaseline = _value;
        _editActive   = true;
        IsEditing     = true;
    }

    /// <summary>
    /// Close the edit session. Esc rolls back to the baseline through the
    /// <see cref="Value"/> setter (which also rewrites <see cref="Node.Attributes"/>);
    /// Enter / focus-loss commits. Returns true when the committed value differs
    /// from the baseline — the caller gates the undo-push on this so a typed-then-
    /// reverted session doesn't accrete a no-op snapshot on the shared history stack.
    /// Same contract as <see cref="SocketViewModel.EndValuePillEdit"/>.
    /// </summary>
    public bool EndEdit(bool commit)
    {
        if (_isBool) return false;
        if (!_editActive)
        {
            IsEditing = false;
            return false;
        }
        bool changed = !string.Equals(_value, _editBaseline, StringComparison.Ordinal);
        if (!commit && changed)
        {
            Value   = _editBaseline;
            changed = false;
        }
        IsEditing     = false;
        _editBaseline = null;
        _editActive   = false;
        return changed;
    }

    /// <summary>
    /// Toggle the bool row. Returns true so the caller can push undo and re-raise
    /// header bindings — mirrors the "changed" contract of <see cref="EndEdit"/>.
    /// Bool rows never have an empty baseline-vs-current rollback case: a tap is
    /// always a state change. No-op if this row isn't a bool row.
    /// </summary>
    public bool ToggleBool()
    {
        if (!_isBool) return false;
        Value = BoolValue ? "false" : "true";
        return true;
    }
}
