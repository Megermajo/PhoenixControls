using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public sealed partial class InspectorField : UserControl
{
    /// <summary>Glyph: filled checkbox (☑ U+2611).</summary>
    private const string BoolCheckedGlyph   = "☑";
    /// <summary>Glyph: empty checkbox (☐ U+2610).</summary>
    private const string BoolUncheckedGlyph = "☐";

    /// <summary>
    /// Fires after a successful commit (Enter or LostFocus with no validation
    /// failure). Carries the new value so parent inspectors can persist via
    /// their own write-back path even when no <see cref="WriteBack"/> closure
    /// is attached. 0.10.0 — InspectorField writeable.
    /// </summary>
    public event EventHandler<string>? ValueChanged;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(InspectorField),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is InspectorField f) f.LabelText.Text = (e.NewValue as string ?? string.Empty).ToUpperInvariant();
            }));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(InspectorField),
            new PropertyMetadata(string.Empty, (d, e) =>
            {
                if (d is InspectorField f) f.RenderValue(e.NewValue as string ?? string.Empty);
            }));

    public static readonly DependencyProperty MonoProperty =
        DependencyProperty.Register(nameof(Mono), typeof(bool), typeof(InspectorField),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is InspectorField f && e.NewValue is bool mono)
                {
                    var key = mono ? "MonoFont" : "SansFont";
                    if (Application.Current.Resources[key] is string family)
                    {
                        f.ValueText.FontFamily = new FontFamily(family);
                        f.ValueBox.FontFamily  = new FontFamily(family);
                    }
                }
            }));

    public static readonly DependencyProperty IsBoolProperty =
        DependencyProperty.Register(nameof(IsBool), typeof(bool), typeof(InspectorField),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is InspectorField f) f.RefreshMode();
            }));

    public static readonly DependencyProperty BoolValueProperty =
        DependencyProperty.Register(nameof(BoolValue), typeof(bool), typeof(InspectorField),
            new PropertyMetadata(false, (d, e) =>
            {
                if (d is InspectorField f && e.NewValue is bool b) f.RenderBool(b);
            }));

    /// <summary>
    /// Closure invoked on a successful text-mode commit. Receives the new
    /// value (already trimmed of pure whitespace by the caller — i.e. the
    /// raw TextBox text). Return value of true means "accept", false means
    /// "reject and flip into ErrorState"; null is treated as accept.
    /// </summary>
    public Func<string, bool>? WriteBack { get; set; }

    /// <summary>
    /// Whether the text-mode field is read-only. When true the value pill
    /// won't switch into EditMode on double-tap; useful for inspector
    /// fields that surface read-only metadata.
    /// </summary>
    public static readonly DependencyProperty IsReadOnlyProperty =
        DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(InspectorField),
            new PropertyMetadata(false));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Mono { get => (bool)GetValue(MonoProperty); set => SetValue(MonoProperty, value); }
    public bool IsBool { get => (bool)GetValue(IsBoolProperty); set => SetValue(IsBoolProperty, value); }
    public bool BoolValue { get => (bool)GetValue(BoolValueProperty); set => SetValue(BoolValueProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    public InspectorField()
    {
        InitializeComponent();
        RefreshMode();
        RenderValue(Value);
        RenderBool(BoolValue);
        VisualStateManager.GoToState(this, "ReadState", false);
    }

    private void RefreshMode()
    {
        TextRoot.Visibility       = IsBool ? Visibility.Collapsed : Visibility.Visible;
        BoolValuePanel.Visibility = IsBool ? Visibility.Visible   : Visibility.Collapsed;
    }

    // ── Bool mode ─────────────────────────────────────────────────────

    private void RenderBool(bool on)
    {
        BoolGlyph.Text     = on ? BoolCheckedGlyph : BoolUncheckedGlyph;
        BoolStateText.Text = on
            ? Localizer.T("architect.inspector.field.bool.on",  "on")
            : Localizer.T("architect.inspector.field.bool.off", "off");
    }

    private void OnBoolHitTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsReadOnly) return;
        bool next = !BoolValue;
        BoolValue = next;
        // Mirror the text-field commit flow. Pre-fix the DP
        // flip was the only side-effect; nothing raised ValueChanged for
        // parents that wire to the event, and the control-level
        // WriteBack closure (if attached) never fired for the bool path.
        // The VM's BoolValue setter still handles WriteBackBool for VMs
        // that route persistence through the VM; this surfaces parity
        // with the text path for callers that hook the control directly.
        string textForm = next ? "true" : "false";
        bool accept = WriteBack?.Invoke(textForm) ?? true;
        if (!accept)
        {
            // Roll back the visual flip and surface the rejection through
            // the ErrorState path used by the text-field commit. No modal
            // (feedback_no_modal_dialogs_for_repeatable_rejections).
            BoolValue = !next;
            ErrorMessageText.Text       = string.IsNullOrEmpty(ErrorMessageText.Text)
                ? Localizer.T("architect.inspector.field.invalid", "Invalid value.")
                : ErrorMessageText.Text;
            ErrorMessageText.Visibility = Visibility.Visible;
            VisualStateManager.GoToState(this, "ErrorState", false);
        }
        else
        {
            ValueChanged?.Invoke(this, textForm);
        }
        e.Handled = true;
    }

    // ── Text mode ─────────────────────────────────────────────────────

    private void RenderValue(string raw)
    {
        // Empty pill chrome stays visible as a clickable
        // affordance with NO placeholder text. The Border + MinWidth in
        // XAML keep the surface hit-targetable for double-tap-to-edit
        // even when ValueText is blank.
        ValueText.Text       = raw ?? string.Empty;
        ValueText.Foreground = TryFindBrush("CoalPrimaryTextBrush");
        ValueBox.Text        = raw ?? string.Empty;
    }

    private void OnValuePillDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsReadOnly || IsBool) return;
        BeginEdit();
        e.Handled = true;
    }

    /// <summary>Public helper so callers can flip into edit mode programmatically.</summary>
    public void BeginEdit()
    {
        if (IsReadOnly || IsBool) return;
        VisualStateManager.GoToState(this, "EditMode", false);
        ErrorMessageText.Visibility = Visibility.Collapsed;
        // Seed the textbox with the actual Value (not the (empty) placeholder).
        ValueBox.Text = Value ?? string.Empty;
        ValueBox.Focus(FocusState.Programmatic);
        ValueBox.SelectAll();
    }

    private void OnValueBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            TryCommit();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CancelEdit();
        }
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // LostFocus also commits — matches the Architect-canvas pill rename
        // pattern (StartSocketRename) so users get consistent behaviour.
        // ReadState-only when we lost focus due to Escape (caller already cleared).
        if (ValueBox.Visibility == Visibility.Visible)
            TryCommit();
    }

    private void TryCommit()
    {
        var candidate = ValueBox.Text ?? string.Empty;
        bool accept = WriteBack?.Invoke(candidate) ?? true;
        if (!accept)
        {
            // Same key as the bool-commit rejection above: one control, one
            // "the commit was refused" phrase, so a translator writes it once.
            ErrorMessageText.Text       = string.IsNullOrEmpty(ErrorMessageText.Text)
                ? Localizer.T("architect.inspector.field.invalid", "Invalid value.")
                : ErrorMessageText.Text;
            ErrorMessageText.Visibility = Visibility.Visible;
            VisualStateManager.GoToState(this, "ErrorState", false);
            return;
        }
        ErrorMessageText.Visibility = Visibility.Collapsed;
        // Update the DP so RenderValue refreshes the pill.
        Value = candidate;
        ValueChanged?.Invoke(this, candidate);
        VisualStateManager.GoToState(this, "ReadState", false);
    }

    private void CancelEdit()
    {
        ValueBox.Text = Value ?? string.Empty;
        ErrorMessageText.Visibility = Visibility.Collapsed;
        VisualStateManager.GoToState(this, "ReadState", false);
    }

    /// <summary>
    /// Surface an error explicitly (e.g. async validation fired off after a
    /// commit was tentatively accepted). Renders the error message and
    /// flips into ErrorState until the next successful commit or cancel.
    /// </summary>
    public void ShowError(string message)
    {
        ErrorMessageText.Text       = message ?? string.Empty;
        ErrorMessageText.Visibility = Visibility.Visible;
        VisualStateManager.GoToState(this, "ErrorState", false);
    }

    private static Brush TryFindBrush(string key)
    {
        try { return (Application.Current.Resources[key] as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray); }
        catch { return new SolidColorBrush(Microsoft.UI.Colors.Gray); }
    }
}
