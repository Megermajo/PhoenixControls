using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
// code-constructed library ContentDialog throws XamlParseException at
// Application.LoadComponent when `new`'d detached (proven by the 1.0.6 runtime
// stack trace; resource stripping never helped because the throw is in the XAML
// parse itself). Content is built in code; the default template still resolves
// at ShowAsync. See NameTypeDialog / DialogTheme.cs for the full rationale.
//
// The ItemsControl ItemTemplate is loaded via XamlReader.Load from the EXACT
// DataTemplate markup that lived in the old .xaml — its {Binding} and
// {ThemeResource} refs are deferred (resolved at row realization in the live
// tree), so they are safe to keep here.
public sealed class SaveValidationDialog : ContentDialog
{
    public sealed class Row
    {
        public string SeverityText { get; init; } = "";
        public string Message      { get; init; } = "";
        public Brush  AccentBrush  { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock HeaderText;
    private readonly ItemsControl WarningList;

    // The DataTemplate markup preserved VERBATIM from the old .xaml. Namespaces
    // added on the root so XamlReader.Load can parse it standalone. {Binding} and
    // {ThemeResource} are deferred until the row is realized — safe here.
    private const string WarningItemTemplateXaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Border Margin=""0,0,0,4""
            Padding=""8,6,8,6""
            Background=""{ThemeResource SelectionDimBrush}""
            BorderBrush=""{Binding AccentBrush}""
            BorderThickness=""3,0,0,0""
            CornerRadius=""2"">
        <StackPanel Orientation=""Horizontal"" Spacing=""8"">
            <Ellipse Width=""10"" Height=""10""
                     VerticalAlignment=""Top""
                     Margin=""0,4,0,0""
                     Fill=""{Binding AccentBrush}"" />
            <StackPanel Spacing=""2"">
                <TextBlock Text=""{Binding SeverityText}""
                           FontSize=""10""
                           CharacterSpacing=""80""
                           Foreground=""{Binding AccentBrush}"" />
                <TextBlock Text=""{Binding Message}""
                           FontSize=""12""
                           TextWrapping=""Wrap""
                           Foreground=""{ThemeResource TextStrongBrush}"" />
            </StackPanel>
        </StackPanel>
    </Border>
</DataTemplate>";

    public SaveValidationDialog()
    {
        Title = Localizer.T("architect.dialog.save_validation.title", "Validation");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        PrimaryButtonText = Localizer.T("architect.dialog.save_validation.save_anyway", "Save anyway");
        SecondaryButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Secondary;

        var root = new Grid { Width = 540, Height = 320 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        HeaderText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(HeaderText, 0);

        WarningList = new ItemsControl
        {
            ItemTemplate = (DataTemplate)XamlReader.Load(WarningItemTemplateXaml),
        };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = WarningList,
        };
        Grid.SetRow(scroller, 1);

        root.Children.Add(HeaderText);
        root.Children.Add(scroller);
        Content = root;

        // Theme applied in code — see DialogTheme.
        if (DialogTheme.Brush("BgPanelBrush")    is { } bg) Background  = bg;
        if (DialogTheme.Brush("BorderSoftBrush") is { } bd) BorderBrush = bd;
        if (DialogTheme.Brush("TextStrongBrush") is { } ts) HeaderText.Foreground = ts;
    }

    public static SaveValidationDialog ForResults(XamlRoot root, IReadOnlyList<ValidationWarning> warnings)
    {
        var d = new SaveValidationDialog { XamlRoot = root };
        bool hasErrors = warnings.Any(w => w.Severity == ValidationSeverity.Error);
        d.HeaderText.Text = hasErrors
            ? string.Format(
                Localizer.T("architect.dialog.save_validation.body_errors_format",
                    "This graph has {0} error(s) and {1} warning(s). Saving anyway will produce a .phx the engine may refuse to run."),
                warnings.Count(w => w.Severity == ValidationSeverity.Error),
                warnings.Count(w => w.Severity == ValidationSeverity.Warning))
            : string.Format(
                Localizer.T("architect.dialog.save_validation.body_warnings_format",
                    "This graph has {0} validator warning(s). Saving is safe — review the issues below or proceed."),
                warnings.Count);
        d.Title = hasErrors
            ? Localizer.T("architect.dialog.save_validation.title_errors", "Validation errors")
            : Localizer.T("architect.dialog.save_validation.title_warnings", "Validation warnings");
        d.WarningList.ItemsSource = warnings.Select(w => new Row
        {
            SeverityText = w.Severity == ValidationSeverity.Error
                ? Localizer.T("architect.dialog.save_validation.severity.error", "ERROR")
                : Localizer.T("architect.dialog.save_validation.severity.warning", "WARNING"),
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
