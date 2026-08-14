using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Localization;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

// Shared name + (optional) type prompt used by Variables / Macros / Processes
// rail CRUD. 0.10.0 polish:
//   * Enter on NameBox commits via OnNameBoxKeyDown (mirrors the F2 idiom).
//   * Duplicate-name validation gated on an existingNames seed.
//   * DefaultBox parses against the picked Type — empty is always allowed,
//     non-empty must round-trip through Int32/Double parse for Number and
//     match "true"/"false" for Bool. Number parsing preserves precision
//     (Double instead of Int32) so 0.5 doesn't truncate.
//   * Static TryParse(input, out NameTypeResult) helper for non-dialog
//     consumers (scripted tests, paste-from-clipboard) that want the same
//     validation surface without a XAML ContentDialog.
//
// [DIALOG-NO-XAML-FIX 2026-06-29] This dialog has NO .xaml / InitializeComponent.
//   A code-constructed ContentDialog defined in a LIBRARY assembly
//   (Architect.WinUI) throws XamlParseException at Application.LoadComponent when
//   `new`'d while detached — proven by the 1.0.6 runtime stack trace, which still
//   crashed AFTER the resource markup was stripped. The resource
//   theory was wrong; the throw is in the XAML parse itself, before any resource
//   or DialogTheme code runs. Building the content in code removes LoadComponent
//   entirely, so the parse can't fail by construction (the default ContentDialog
//   template still resolves at ShowAsync against Hub's app scope, which merges
//   XamlControlsResources — the show path that already works for Hub's dialogs).
//   See DialogTheme.cs.
public sealed class NameTypeDialog : ContentDialog
{
    private static readonly Regex NameRx = new(@"^[A-Za-z_][A-Za-z0-9_\.]*$", RegexOptions.Compiled);

    public string EnteredName { get; private set; } = string.Empty;
    public string EnteredType { get; private set; } = "String";
    public string EnteredDefault { get; private set; } = string.Empty;

    private HashSet<string> _existingNames = new(StringComparer.OrdinalIgnoreCase);

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock EyebrowText;
    private readonly TextBox NameBox;
    private readonly ComboBox TypeBox;
    private readonly TextBox DefaultBox;
    private readonly TextBlock ErrorText;

    public NameTypeDialog()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("common.ok", "OK");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        EyebrowText = new TextBlock
        {
            FontSize = 10,
            CharacterSpacing = 80,
            Text = Localizer.T("architect.dialog.name_type.eyebrow", "DETAILS"),
        };

        NameBox = new TextBox
        {
            PlaceholderText = Localizer.T("architect.dialog.name_type.name_placeholder", "Name"),
            FontSize = 13,
        };
        NameBox.KeyDown += OnNameBoxKeyDown;

        TypeBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            FontSize = 12,
        };
        // String / Number / Bool are DATA-TYPE identifiers, not chrome: the
        // selected Content string round-trips through SelectedTypeLabel into
        // EnteredType and is matched case-insensitively by ValidateDefault /
        // TryParse. They stay English by decision (type names are out of scope).
        TypeBox.Items.Add(new ComboBoxItem { Content = "String" });
        TypeBox.Items.Add(new ComboBoxItem { Content = "Number" });
        TypeBox.Items.Add(new ComboBoxItem { Content = "Bool" });
        TypeBox.SelectedIndex = 0;
        TypeBox.SelectionChanged += OnTypeBoxSelectionChanged;

        DefaultBox = new TextBox
        {
            PlaceholderText = Localizer.T("architect.dialog.name_type.default_placeholder",
                "Default value (optional)"),
            Visibility = Visibility.Collapsed,
            FontSize = 12,
        };
        DefaultBox.KeyDown += OnDefaultBoxKeyDown;
        DefaultBox.TextChanged += OnDefaultBoxTextChanged;

        ErrorText = new TextBlock { FontSize = 11, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(EyebrowText);
        panel.Children.Add(NameBox);
        panel.Children.Add(TypeBox);
        panel.Children.Add(DefaultBox);
        panel.Children.Add(ErrorText);
        Content = panel;

        // Theme is applied in code (NOT via XAML resource markup) — see
        // DialogTheme for why a code-constructed library dialog can't resolve
        // {StaticResource}/{ThemeResource} at load.
        if (DialogTheme.Brush("BgPanelBrush")    is { } bg) Background  = bg;
        if (DialogTheme.Brush("BorderSoftBrush") is { } bd) BorderBrush = bd;
        if (DialogTheme.Brush("TextLabelBrush")  is { } tl) EyebrowText.Foreground = tl;
        if (DialogTheme.Brush("StatusRedBrush")  is { } sr) ErrorText.Foreground   = sr;
        PrimaryButtonClick += OnPrimary;
        // Focus + select the name field when the dialog
        // opens so the user can type immediately (and a typed name replaces any
        // seeded default). Opened is the correct ContentDialog hook — it fires
        // after the popup is in the tree, unlike Loaded which can race the host.
        Opened += (_, _) =>
        {
            try { NameBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); NameBox.SelectAll(); }
            catch { /* designer / pre-realised */ }
        };
    }

    public static NameTypeDialog ForVariable(XamlRoot root, string title,
                                             string initialName = "", string initialType = "String", string initialDefault = "",
                                             IEnumerable<string>? existingNames = null)
    {
        var d = new NameTypeDialog
        {
            XamlRoot = root,
            Title = title,
        };
        d.EyebrowText.Text = Localizer.T("architect.dialog.name_type.variable_eyebrow", "VARIABLE");
        d.NameBox.Text = initialName;
        d.TypeBox.Visibility = Visibility.Visible;
        d.DefaultBox.Visibility = Visibility.Visible;
        d.DefaultBox.Text = initialDefault;
        d.TypeBox.SelectedIndex = initialType?.ToLowerInvariant() switch
        {
            "number" or "int" or "integer" or "float" => 1,
            "bool" or "boolean" => 2,
            _ => 0,
        };
        d.SetExistingNames(existingNames);
        return d;
    }

    public static NameTypeDialog ForName(XamlRoot root, string title, string eyebrow, string initialName = "",
                                         IEnumerable<string>? existingNames = null)
    {
        var d = new NameTypeDialog
        {
            XamlRoot = root,
            Title = title,
        };
        d.EyebrowText.Text = eyebrow;
        d.NameBox.Text = initialName;
        d.SetExistingNames(existingNames);
        return d;
    }

    private void SetExistingNames(IEnumerable<string>? names)
    {
        _existingNames = names is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(names.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
    }

    // ── Live commit handlers ──────────────────────────────────────────

    private void OnNameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            // Programmatically click the primary button so the validation +
            // commit path runs identically to a button tap.
            e.Handled = true;
            TryCommit();
        }
    }

    private void OnDefaultBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            TryCommit();
        }
    }

    private void OnDefaultBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        // Live-validate the default value against the picked type so the
        // user sees the same error they'd hit on OK without committing.
        var msg = ValidateDefault(DefaultBox.Text ?? string.Empty, SelectedTypeLabel());
        SetErrorText(msg);
    }

    private void OnTypeBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Re-validate the default in light of the new type — e.g. switching
        // String → Bool with "yes" in the box must surface the error.
        if (DefaultBox.Visibility == Visibility.Visible)
            OnDefaultBoxTextChanged(this, null!);
    }

    private void TryCommit()
    {
        if (!ValidateAll(out var error))
        {
            SetErrorText(error);
            return;
        }
        EnteredName = (NameBox.Text ?? string.Empty).Trim();
        EnteredType = SelectedTypeLabel();
        EnteredDefault = DefaultBox.Text ?? string.Empty;
        Hide();
    }

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!ValidateAll(out var error))
        {
            SetErrorText(error);
            args.Cancel = true;
            return;
        }
        EnteredName = (NameBox.Text ?? string.Empty).Trim();
        EnteredType = SelectedTypeLabel();
        EnteredDefault = DefaultBox.Text ?? string.Empty;
    }

    private string SelectedTypeLabel() =>
        (TypeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "String";

    private bool ValidateAll(out string error)
    {
        error = string.Empty;
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (!NameRx.IsMatch(name))
        {
            error = Localizer.T("architect.dialog.name_type.err_invalid_name",
                "Name must start with a letter or underscore and contain only letters, digits, underscore or dot.");
            return false;
        }
        if (_existingNames.Contains(name))
        {
            error = string.Format(
                Localizer.T("architect.dialog.name_type.err_duplicate_format",
                    "'{0}' already exists — pick a unique name."),
                name);
            return false;
        }
        if (DefaultBox.Visibility == Visibility.Visible)
        {
            var defMsg = ValidateDefault(DefaultBox.Text ?? string.Empty, SelectedTypeLabel());
            if (defMsg.Length > 0) { error = defMsg; return false; }
        }
        return true;
    }

    private static string ValidateDefault(string raw, string type)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        switch (type.ToLowerInvariant())
        {
            case "number":
                // Number parses through Double (preserves precision so 0.5
                // doesn't truncate). Allow ints by accepting the same surface.
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return string.Format(
                        Localizer.T("architect.dialog.name_type.err_default_number_format",
                            "Default '{0}' is not a valid Number."),
                        raw);
                return string.Empty;
            case "bool":
                if (!raw.Equals("true",  StringComparison.OrdinalIgnoreCase) &&
                    !raw.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return string.Format(
                        Localizer.T("architect.dialog.name_type.err_default_bool_format",
                            "Default '{0}' must be 'true' or 'false'."),
                        raw);
                return string.Empty;
            default:
                return string.Empty;
        }
    }

    private void SetErrorText(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── Non-dialog API ─────────────────────────────────────────────────

    /// <summary>
    /// Result of <see cref="TryParse"/> — same shape as the dialog's
    /// EnteredName / EnteredType / EnteredDefault triple plus a normalised
    /// numeric representation for "Number" types so callers don't have to
    /// re-parse.
    /// </summary>
    public sealed record NameTypeResult(string Name, string Type, string Default);

    /// <summary>
    /// Validate <paramref name="input"/> (one of "name", "name : Type", or
    /// "name : Type = default") against the same rules the dialog enforces.
    /// On success returns true with <paramref name="result"/>; on failure
    /// returns false with <paramref name="error"/> describing what broke.
    /// </summary>
    public static bool TryParse(string input, out NameTypeResult? result, out string error)
    {
        result = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = Localizer.T("architect.dialog.name_type.err_empty_input", "Empty input.");
            return false;
        }

        string name;
        string type = "String";
        string defaultValue = string.Empty;

        var eq = input.IndexOf('=');
        var head = eq < 0 ? input : input.Substring(0, eq);
        if (eq >= 0) defaultValue = input.Substring(eq + 1).Trim();

        var colon = head.IndexOf(':');
        if (colon < 0)
        {
            name = head.Trim();
        }
        else
        {
            name = head.Substring(0, colon).Trim();
            type = head.Substring(colon + 1).Trim();
            if (string.IsNullOrEmpty(type)) type = "String";
        }

        if (!NameRx.IsMatch(name))
        {
            error = Localizer.T("architect.dialog.name_type.err_invalid_name",
                "Name must start with a letter or underscore and contain only letters, digits, underscore or dot.");
            return false;
        }
        // Normalise type to one of the dialog's three options.
        type = type.ToLowerInvariant() switch
        {
            "number" or "int" or "integer" or "float" or "double" => "Number",
            "bool"   or "boolean"                                  => "Bool",
            _                                                      => "String",
        };
        var defMsg = ValidateDefault(defaultValue, type);
        if (defMsg.Length > 0) { error = defMsg; return false; }
        result = new NameTypeResult(name, type, defaultValue);
        return true;
    }

    /// <summary>Shorthand overload — discards the error string.</summary>
    public static bool TryParse(string input, out NameTypeResult? result)
        => TryParse(input, out result, out _);
}
