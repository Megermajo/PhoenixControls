using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Visualist.WinUI.Services;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

// Visualist-side parallel of Architect's RecentFilesDialog. Kept duplicated —
// dialog chrome is paint code, not a cross-pillar service. The MRU backing
// (Phoenix.Controls.Visualist.WinUI.Services.RecentFiles) is also a pillar-
// local copy of the same pattern.
//
// No .xaml / InitializeComponent — a
// code-constructed library ContentDialog (Visualist.WinUI) throws
// XamlParseException at Application.LoadComponent when `new`'d detached
// (proven by the 1.0.6 runtime stack trace; resource stripping never helped
// because the throw is in the XAML parse itself). Content is built in code; the
// ItemTemplate is rebuilt verbatim via XamlReader.Load (template content is
// deferred, so {Binding}/{ThemeResource} resolve safely at row realization).
// The default ContentDialog template still resolves at ShowAsync against Hub's
// app scope. See DialogTheme.cs / Architect NameTypeDialog for the rationale.
public sealed class RecentFilesDialog : ContentDialog
{
    public sealed record Row(string FileName, string FullPath);

    /// <summary>Path the user picked, or null when the dialog closed without picking.</summary>
    public string? PickedPath { get; private set; }

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock HeaderHintText;
    private readonly ListView RecentList;
    private readonly TextBlock EmptyHint;
    private readonly Button ClearButton;

    // ItemTemplate markup preserved VERBATIM from the old XAML — {Binding} +
    // {ThemeResource} are deferred (resolved at row realization), so they are
    // safe here even though the dialog is code-constructed.
    private const string RecentItemTemplate = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <StackPanel Spacing=""2"" Margin=""0,4,0,4"">
        <TextBlock Text=""{Binding FileName}""
                   FontFamily=""{ThemeResource MonoFont}""
                   FontSize=""13""
                   Foreground=""{ThemeResource CoalPrimaryTextBrush}"" />
        <TextBlock Text=""{Binding FullPath}""
                   FontFamily=""{ThemeResource MonoFont}""
                   FontSize=""10""
                   Foreground=""{ThemeResource CoalSecondaryTextBrush}""
                   TextTrimming=""CharacterEllipsis"" />
    </StackPanel>
</DataTemplate>";

    public RecentFilesDialog()
    {
        Title = Localizer.T("visualist.dialog.recent.title", "Open Recent Layer");
        PrimaryButtonText = Localizer.T("common.button.open", "Open");
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Primary;

        var grid = new Grid { Width = 520, Height = 360 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        HeaderHintText = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 11,
            Text = Localizer.T("visualist.dialog.recent.header_hint",
                "Visualist MRU — last 10 layers opened"),
        };
        Grid.SetRow(HeaderHintText, 0);

        RecentList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(RecentItemTemplate),
        };
        RecentList.ItemClick += OnItemClick;
        RecentList.KeyDown += OnRecentListKeyDown;
        Grid.SetRow(RecentList, 1);

        EmptyHint = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Text = Localizer.T("visualist.dialog.recent.empty",
                "No recent layers yet — open a .phxlayer from File → Open Layer."),
        };
        Grid.SetRow(EmptyHint, 1);

        ClearButton = new Button
        {
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = Localizer.T("visualist.dialog.recent.clear", "Clear list"),
        };
        ClearButton.Click += OnClearClick;
        Grid.SetRow(ClearButton, 2);

        grid.Children.Add(HeaderHintText);
        grid.Children.Add(RecentList);
        grid.Children.Add(EmptyHint);
        grid.Children.Add(ClearButton);
        Content = grid;

        // Code-constructed library dialog — theme applied in code via
        // DialogTheme; no directly-resolved resource markup (DataTemplate refs
        // are deferred and safe). See Architect NameTypeDialog / DialogTheme.cs.
        if (DialogTheme.Brush("CoalSurfaceBrush") is { } bg) Background = bg;
        if (DialogTheme.Brush("CoalSecondaryTextBrush") is { } sc)
        {
            HeaderHintText.Foreground = sc;
            EmptyHint.Foreground = sc;
        }
        if (DialogTheme.Font("MonoFont") is { } mf) HeaderHintText.FontFamily = mf;

        Reload();

        // Commit via the canonical Primary button so the dialog honors the
        // ContentDialog contract (Primary = commit, Close/Esc = cancel).
        // Previously the only commit path was ItemClick → Hide(), which left
        // Esc / Open-button semantics ambiguous. ItemClick stays as a
        // double-click shortcut; Enter-in-the-list is handled below.
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    private void Reload()
    {
        var paths = RecentFiles.Load();
        var rows = paths
            .Select(p => new Row(Path.GetFileName(p), p))
            .ToList();
        RecentList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = !empty;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Row r)
        {
            PickedPath = r.FullPath;
            Hide();
        }
    }

    // Primary ("Open") button: commit the currently-selected row. If nothing
    // is selected the click is cancelled so the dialog stays open rather than
    // closing with a null pick.
    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (RecentList.SelectedItem is Row r)
        {
            PickedPath = r.FullPath;
        }
        else
        {
            // No selection — keep the dialog open instead of committing nothing.
            args.Cancel = true;
        }
    }

    // Enter inside the list commits the highlighted row — mirrors Architect's
    // RecentFilesDialog.OnRecentListKeyDown so keyboard-only navigation can open
    // a recent layer without reaching for the Open button.
    private void OnRecentListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (RecentList.SelectedItem is Row r)
        {
            PickedPath = r.FullPath;
            e.Handled = true;
            Hide();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        foreach (var p in RecentFiles.Load()) RecentFiles.Remove(p);
        Reload();
    }
}
