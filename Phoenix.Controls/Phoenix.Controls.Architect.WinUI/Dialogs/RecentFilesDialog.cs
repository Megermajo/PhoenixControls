using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.WinUI.Services;
using Phoenix.Controls.Shared.Localization;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

// [DIALOG-NO-XAML-FIX 2026-06-29] No .xaml / InitializeComponent — a
// code-constructed library ContentDialog throws XamlParseException at
// Application.LoadComponent when `new`'d detached (proven by the 1.0.6 runtime
// stack trace; resource stripping never helped because the throw is in the XAML
// parse itself). Content is built in code; the default template still resolves
// at ShowAsync. The per-row ItemTemplate is rebuilt via XamlReader.Load (its
// {Binding}/{ThemeResource} markup is deferred and resolves at row realization,
// so it's safe). See NameTypeDialog / ConfirmDialog / DialogTheme.cs.
public sealed class RecentFilesDialog : ContentDialog
{
    public sealed record Row(string FileName, string FullPath, bool IsMissing, bool IsPinned = false)
    {
        /// <summary>0.45 for stale entries (file moved or deleted), 1.0 otherwise.</summary>
        public double Opacity => IsMissing ? 0.45 : 1.0;

        /// <summary>Pin glyph — Segoe Fluent E840 (filled pin) when pinned,
        /// E718 (outline pin) when not. 0.10.0 UX P2.</summary>
        public string PinGlyph => IsPinned ? "" : "";
    }

    /// <summary>Path the user picked, or null when the dialog closed without picking.</summary>
    public string? PickedPath { get; private set; }

    // Named elements that were x:Name'd in the old XAML — now plain fields built
    // in the ctor.
    private readonly TextBlock MruHint;
    private readonly ListView RecentList;
    private readonly TextBlock EmptyHint;
    private readonly Button ClearButton;

    public RecentFilesDialog()
    {
        Title = Localizer.T("architect.dialog.recent.title", "Open Recent");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        CloseButtonText = Localizer.T("common.button.cancel", "Cancel");
        DefaultButton = ContentDialogButton.Close;

        var grid = new Grid { Width = 520, Height = 400 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var infoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = Localizer.T("architect.dialog.recent.infobar.title", "Read-only reference"),
            Message = Localizer.T("architect.dialog.recent.infobar.body",
                "This view can stay open while you edit — close manually when done."),
            IsOpen = true,
            IsClosable = false,
            Margin = new Thickness(0, 0, 0, 8),
        };
        Grid.SetRow(infoBar, 0);
        grid.Children.Add(infoBar);

        MruHint = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 11,
            Text = Localizer.T("architect.dialog.recent.hint", "Architect MRU — last 10 graphs opened"),
        };
        Grid.SetRow(MruHint, 1);
        grid.Children.Add(MruHint);

        RecentList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
        };
        RecentList.ItemTemplate = BuildItemTemplate();
        RecentList.ItemClick += OnItemClick;
        RecentList.KeyDown += OnRecentListKeyDown;
        // The per-row Pin button's Click can't be wired by name inside an
        // XamlReader.Load template (no code-behind to bind "OnPinToggleClick"
        // against), so attach it per realized container instead. The handler
        // reads the row off the button's DataContext exactly as before.
        RecentList.ContainerContentChanging += OnContainerContentChanging;
        Grid.SetRow(RecentList, 2);
        grid.Children.Add(RecentList);

        EmptyHint = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Text = Localizer.T("architect.dialog.recent.empty",
                "No recent files yet — open a .phxg from File → Open."),
        };
        Grid.SetRow(EmptyHint, 2);
        grid.Children.Add(EmptyHint);

        ClearButton = new Button
        {
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = Localizer.T("architect.dialog.recent.clear_button", "Clear list"),
        };
        ClearButton.Click += OnClearClick;
        Grid.SetRow(ClearButton, 3);
        grid.Children.Add(ClearButton);

        Content = grid;

        // Theme applied in code — see DialogTheme. (DataTemplate row brushes stay
        // in the template markup; template content is deferred and resolves at realization.)
        if (DialogTheme.Brush("BgPanelBrush")    is { } bg) Background  = bg;
        if (DialogTheme.Brush("BorderSoftBrush") is { } bd) BorderBrush = bd;
        if (DialogTheme.Brush("TextLabelBrush")  is { } tl) { MruHint.Foreground = tl; EmptyHint.Foreground = tl; }
        Reload();
    }

    // Per-row template — built from the EXACT DataTemplate markup the old XAML
    // carried, preserving every {Binding} and {ThemeResource} verbatim. Template
    // content is deferred (resolves at row realization in the live tree), so the
    // {ThemeResource} refs are safe here — unlike on the dialog root.
    private static DataTemplate BuildItemTemplate()
    {
        const string xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <!-- 0.10.0 UX P2: per-row Pin toggle. -->
  <Grid Margin=""0,4,0,4"" Opacity=""{Binding Opacity}""
        ToolTipService.ToolTip=""{Binding FullPath}"">
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width=""*"" />
      <ColumnDefinition Width=""Auto"" />
    </Grid.ColumnDefinitions>
    <StackPanel Grid.Column=""0"" Spacing=""2"">
      <TextBlock Text=""{Binding FileName}""
                 FontSize=""13""
                 Foreground=""{ThemeResource TextStrongBrush}"" />
      <TextBlock Text=""{Binding FullPath}""
                 FontSize=""10""
                 Foreground=""{ThemeResource TextLabelBrush}""
                 TextTrimming=""CharacterEllipsis"" />
    </StackPanel>
    <Button Grid.Column=""1""
            Width=""28""
            Height=""28""
            Padding=""0""
            Margin=""6,0,0,0""
            Background=""Transparent""
            BorderThickness=""0""
            Content=""{Binding PinGlyph}""
            FontFamily=""Segoe Fluent Icons,Segoe MDL2 Assets""
            FontSize=""12""
            ToolTipService.ToolTip=""Pin to top / unpin""
            AutomationProperties.Name=""Pin or unpin this file"" />
  </Grid>
</DataTemplate>";
        return (DataTemplate)XamlReader.Load(xaml);
    }

    // Wire each realized row's Pin button to OnPinToggleClick. The template
    // (built via XamlReader.Load) can't carry a Click="..." attribute because
    // there's no code-behind for the loader to bind the name against, so the
    // handler is attached here, once per container, when content arrives.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer?.ContentTemplateRoot is FrameworkElement rootEl &&
            FindPinButton(rootEl) is { } btn)
        {
            // De-dupe across recycling: detach then re-attach so a recycled
            // container never accumulates duplicate handlers.
            btn.Click -= OnPinToggleClick;
            btn.Click += OnPinToggleClick;

            // The row template is built through XamlReader.Load, which cannot
            // carry the loc: attached properties, so the pin button's two
            // chrome strings resolve here per realized container instead. The
            // template literals stay as the pre-realization fallback.
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(btn,
                Localizer.T("architect.dialog.recent.pin.tip", "Pin to top / unpin"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn,
                Localizer.T("architect.dialog.recent.pin.a11y", "Pin or unpin this file"));
        }
    }

    // The Pin button is the single Button in the row template; locate it by
    // walking the realized container's visual children.
    private static Button? FindPinButton(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b) return b;
            if (FindPinButton(child) is { } found) return found;
        }
        return null;
    }

    private void Reload()
    {
        // 0.10.0 UX P2: merge pinned + MRU so a pinned graph still surfaces
        // even after 10 newer files pushed it out of the recency window.
        var paths  = RecentFiles.LoadMerged();
        var pinned = RecentFiles.LoadPinned();
        var rows = paths
            .Select(p => new Row(Path.GetFileName(p), p, IsMissing: !File.Exists(p),
                                 IsPinned: pinned.Contains(p)))
            .ToList();
        RecentList.ItemsSource = rows;
        bool empty = rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = !empty;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Row r) CommitRow(r);
    }

    /// <summary>
    /// Enter on the selected ListView row commits the same as a click —
    /// pre-fix only mouse / touch click fired ItemClick, so Arrow-Up/Down +
    /// Enter (the standard MRU navigation) silently did nothing.
    /// </summary>
    private void OnRecentListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && RecentList.SelectedItem is Row r)
        {
            CommitRow(r);
            e.Handled = true;
        }
    }

    private void CommitRow(Row r)
    {
        PickedPath = r.FullPath;
        Hide();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 0.10.0 UX P2: single Clear() call instead of 10 sequential
        // Remove() round-trips through Save().
        RecentFiles.Clear();
        Reload();
    }

    /// <summary>
    /// Pin / unpin the row's path, then refresh so the pin glyph flips and
    /// the entry re-sorts to its new position. 0.10.0 UX P2.
    /// </summary>
    private void OnPinToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Row r)
        {
            // Architect P1: never pin a missing file. Reload() flags moved /
            // deleted entries IsMissing (45% opacity), but the pin button
            // stays clickable; pinning one would serialize a dead path to
            // pinned.json and LoadMerged() would float it to the top with no
            // way to dismiss it short of editing the file by hand. Allow only
            // unpin on a missing row (so a stale pin can still be cleared).
            if (r.IsMissing && !r.IsPinned)
            {
                Phoenix.Controls.Shared.Services.GlobalLogger.Log(
                    $"Recent files: refused to pin missing file '{r.FullPath}'.",
                    "Architect.RecentFiles",
                    Phoenix.Controls.Shared.Models.LogLevel.System);
                return;
            }
            RecentFiles.SetPinned(r.FullPath, !r.IsPinned);
            Reload();
        }
    }
}
