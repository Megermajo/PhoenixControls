namespace Phoenix.Controls.Architect.WinUI.ViewModels;

// One row in the right-hand inspector panel — eyebrow label + value.
// The design package shows three field shapes (Field() in architect.jsx):
//
//   - mono text:   <Field label="Message" value="Hello, {user.name}!" mono/>
//   - sans text:   <Field label="Stream"  value="online"/>
//   - bool toggle: <Field label="Reply to source" type="bool" value/>
//
// Track 5's InspectorFieldView XAML branches on Kind to render the right
// shape. Track 6 just supplies the data + the kind label; selection
// changes on the canvas / databank push fresh field lists into the
// LogicInspectorViewModel.Fields / DatabankInspectorViewModel.Fields
// observable collections.
//
// Was a record pre-fix, which made BoolValue init-only and silently broke
// the bool ToggleSwitch — flipping the switch wrote into the local DP but
// nothing wired the change back into the underlying Node.Attributes.
// Now a class with a settable BoolValue and an optional WriteBackBool
// callback the inspector hands in when constructing the field.
//
// QC19-02 (0.10.0 follow-up) — Value is now mutable + observable with an
// optional WriteBackText closure that mirrors WriteBackBool: the
// inspector hands in a per-field persistence delegate, and the Value
// setter (driven by the TwoWay binding from InspectorField's commit
// path) invokes it. Pre-fix the DatabankInspector's Value binding was
// OneTime against a get-only property — double-tap-edit visually
// committed but nothing reached the DB.
public sealed class InspectorFieldViewModel : ObservableObject
{
    private bool   _boolValue;
    private string _value;

    public InspectorFieldViewModel(
        string             label,
        string             value,
        InspectorFieldKind kind = InspectorFieldKind.SansText,
        System.Action<bool>?           writeBackBool = null,
        System.Func<string, bool>?     writeBackText = null)
    {
        Label = label;
        _value = value ?? string.Empty;
        Kind  = kind;
        WriteBackBool = writeBackBool;
        WriteBackText = writeBackText;
        _boolValue = IsBool && string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);
    }

    public string             Label { get; }
    public InspectorFieldKind Kind  { get; }

    public bool IsBool => Kind == InspectorFieldKind.Bool;
    public bool IsMono => Kind == InspectorFieldKind.MonoText;

    /// <summary>
    /// Two-way bound to the value pill in <c>InspectorField.xaml</c>. The
    /// setter forwards the new value to <see cref="WriteBackText"/> so
    /// the underlying model (DB cell, Node.Attributes, etc.) persists.
    /// Pre-fix this was a getter-only property bound OneTime, so the
    /// inline TextBox commit landed in the control's DP but never
    /// reached the data layer (QC19-02).
    ///
    /// When WriteBackText returns false the setter rolls the cached
    /// value back to the prior state so subsequent rebuilds reflect the
    /// rejected change. Rollback is communicated to the bound control
    /// via the second PropertyChanged tick — the InspectorField surfaces
    /// the rejection through its own ErrorState path.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            var candidate = value ?? string.Empty;
            if (string.Equals(_value, candidate, System.StringComparison.Ordinal)) return;
            var prior = _value;
            _value = candidate;
            OnPropertyChanged();
            // WriteBackText returns false on validation rejection — roll
            // back so the field reflects truth on the next refresh. The
            // setter completes either way; null = accept (parity with the
            // InspectorField control's WriteBack default).
            //
            // S10 P1: the closure (e.g. DatabankInspector → ApplyLocalEdit →
            // DatabankCellViewModel.Value → SetField → synchronous
            // PropertyChanged) can throw, and a PropertyChanged handler that
            // faults would propagate straight out of this setter and crash the
            // inspector. Guard it: on fault, log (no modal — repeatable
            // guardrail rejection) and treat as a rejection so the cached
            // value rolls back and the bound control flips into its
            // ErrorState path via the second PropertyChanged tick.
            bool accept;
            try
            {
                accept = WriteBackText?.Invoke(candidate) ?? true;
            }
            catch (System.Exception ex)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Inspector field '{Label}' write-back failed: {ex.Message}",
                    "Architect.Inspector",
                    Phoenix.Controls.Shared.Models.LogLevel.CriticalError);
                accept = false;
            }
            if (!accept)
            {
                _value = prior;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Two-way bound to the bool ToggleSwitch in
    /// <c>InspectorField.xaml</c>. The setter forwards the new state to
    /// <see cref="WriteBackBool"/> so the underlying Node.Attributes
    /// entry persists. Pre-fix the toggle's local DP setter was the only
    /// write path and nothing reached the model — flipping the switch
    /// silently no-op'd at the data layer.
    /// </summary>
    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (_boolValue == value) return;
            _boolValue = value;
            OnPropertyChanged();
            WriteBackBool?.Invoke(value);
        }
    }

    /// <summary>
    /// Callback fired by the BoolValue setter when the bound toggle flips.
    /// Inspector VMs hand in a closure that mutates the right
    /// <c>Node.Attributes</c> entry plus pokes any redundant cache.
    /// Null on non-bool fields.
    /// </summary>
    public System.Action<bool>? WriteBackBool { get; }

    /// <summary>
    /// Callback fired by the Value setter when the bound text pill
    /// commits. Return true to accept the edit, false to reject it (the
    /// setter rolls back to the prior cached value). Null on non-text
    /// fields, or when the inspector intentionally surfaces a read-only
    /// row (link-detail eyebrow + value pairs, etc.). QC19-02.
    /// </summary>
    public System.Func<string, bool>? WriteBackText { get; }
}

public enum InspectorFieldKind
{
    /// <summary>Plain sans-serif text (default).</summary>
    SansText,
    /// <summary>Monospace text — used for paths, scripts, raw values.</summary>
    MonoText,
    /// <summary>On/off pill toggle.</summary>
    Bool,
}
