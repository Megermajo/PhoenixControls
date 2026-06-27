using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

public sealed partial class SaveValidationDialog : ContentDialog
{
    public sealed class Row
    {
        public string SeverityText { get; init; } = "";
        public string Message      { get; init; } = "";
        public Brush  AccentBrush  { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public SaveValidationDialog()
    {
        InitializeComponent();
    }

    public static SaveValidationDialog ForResults(XamlRoot root, IReadOnlyList<ValidationWarning> warnings)
    {
        var d = new SaveValidationDialog { XamlRoot = root };
        bool hasErrors = warnings.Any(w => w.Severity == ValidationSeverity.Error);
        d.HeaderText.Text = hasErrors
            ? $"This graph has {warnings.Count(w => w.Severity == ValidationSeverity.Error)} error(s) and {warnings.Count(w => w.Severity == ValidationSeverity.Warning)} warning(s). Saving anyway will produce a .phx the engine may refuse to run."
            : $"This graph has {warnings.Count} validator warning(s). Saving is safe — review the issues below or proceed.";
        d.Title = hasErrors ? "Validation errors" : "Validation warnings";
        d.WarningList.ItemsSource = warnings.Select(w => new Row
        {
            SeverityText = w.Severity == ValidationSeverity.Error ? "ERROR" : "WARNING",
            Message      = w.Message,
            AccentBrush  = ResolveAccent(w.Severity),
        }).ToList();
        return d;
    }

    private static Brush ResolveAccent(ValidationSeverity sev)
    {
        try
        {
            string key = sev == ValidationSeverity.Error ? "ErrBrush" : "WarnBrush";
            if (Application.Current.Resources[key] is Brush b) return b;
        }
        catch { /* fall through */ }
        return sev == ValidationSeverity.Error
            ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
            : new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
    }
}
