using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Architect.WinUI.Services;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

public sealed partial class RecentFilesDialog : ContentDialog
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

    public RecentFilesDialog()
    {
        InitializeComponent();
        Reload();
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

    // ── Flyout entry point ──────────────────────────────────────────────

    /// <summary>
    /// Surface the recent-files list as a non-modal Flyout anchored on
    /// <paramref name="anchor"/>. Pick-a-row commits via <paramref name="picked"/>;
    /// clearing fires <paramref name="cleared"/>. Architect P1 — modal
    /// ContentDialog interrupts the canvas; the flyout sits beside the menu
    /// and dismisses on click-away.
    /// </summary>
    public static Flyout OpenAsFlyout(FrameworkElement anchor,
                                      Action<string>? picked  = null,
                                      Action?         cleared = null)
    {
        var paths = RecentFiles.Load();
        var rows  = paths
            .Select(p => new Row(Path.GetFileName(p), p, IsMissing: !File.Exists(p)))
            .ToList();

        // Build a self-contained flyout without spinning up the full
        // ContentDialog visual surface (which would also block input).
        var list = new ListView
        {
            ItemsSource         = rows,
            SelectionMode       = ListViewSelectionMode.Single,
            IsItemClickEnabled  = true,
            Width               = 420,
            MaxHeight           = 320,
        };
        list.ItemTemplate = BuildRowTemplate();

        var emptyHint = new TextBlock
        {
            Text                = "No recent files yet — open a .phxg from File → Open.",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            FontSize            = 12,
            Foreground          = TryFindBrush("TextLabelBrush"),
        };

        var clear = new Button
        {
            Content             = "Clear list",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, 8, 0, 0),
            IsEnabled           = rows.Count > 0,
        };

        var grid = new Grid { Width = 420 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (rows.Count == 0)
        {
            Grid.SetRow(emptyHint, 0);
            grid.Children.Add(emptyHint);
        }
        else
        {
            Grid.SetRow(list, 0);
            grid.Children.Add(list);
        }
        Grid.SetRow(clear, 1);
        grid.Children.Add(clear);

        var flyout = new Flyout { Content = grid };

        list.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Row r)
            {
                picked?.Invoke(r.FullPath);
                flyout.Hide();
            }
        };
        list.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter && list.SelectedItem is Row r)
            {
                picked?.Invoke(r.FullPath);
                ke.Handled = true;
                flyout.Hide();
            }
            else if (ke.Key == VirtualKey.Escape)
            {
                ke.Handled = true;
                flyout.Hide();
            }
        };
        clear.Click += (_, _) =>
        {
            // Architect P0 : single Clear() write instead of 10
            // sequential Load → Remove → Save round-trips on recent-files.json.
            // Matches OnClearClick()'s modal-path fix; the flyout path was
            // overlooked and still iterated Remove() per entry, creating an
            // O(n²) burst of File.ReadAllText/WriteAllText cycles that race
            // the deferred MRU writes under OneDrive / AV latency.
            RecentFiles.Clear();
            cleared?.Invoke();
            flyout.Hide();
        };

        flyout.ShowAt(anchor);
        return flyout;
    }

    private static DataTemplate BuildRowTemplate()
    {
        const string xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
              xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <StackPanel Spacing=""2"" Margin=""0,4,0,4"" Opacity=""{Binding Opacity}""
              ToolTipService.ToolTip=""{Binding FullPath}"">
    <TextBlock Text=""{Binding FileName}""
               FontSize=""13"" />
    <TextBlock Text=""{Binding FullPath}""
               FontSize=""10""
               TextTrimming=""CharacterEllipsis"" />
  </StackPanel>
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private static Brush TryFindBrush(string key)
    {
        try { return (Application.Current.Resources[key] as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray); }
        catch { return new SolidColorBrush(Microsoft.UI.Colors.Gray); }
    }
}
