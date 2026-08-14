using System;
using System.Collections;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.Dialogs;
using Phoenix.Controls.Visualist.WinUI.Models;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.Foundation;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

public sealed partial class LayerRail : UserControl
{
    // Finding #22 — empty-state hint. We toggle EmptyHint.Visibility off the
    // bound Layers collection (mirrors MediaLibraryPanel's EmptyHint pattern).
    // The ItemsSource is set via XAML binding ({Binding Layers}), so it only
    // resolves once DataContext is wired and Loaded fires — subscribe there,
    // and unsubscribe on Unloaded so the handle doesn't outlive the control.
    private INotifyCollectionChanged? _observed;

    public LayerRail()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookCollection();
        UpdateEmptyHint();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookCollection();
    }

    private void HookCollection()
    {
        // Re-resolve in case the DataContext (and thus the bound collection)
        // changed while detached, then re-hook only if it actually moved.
        var incc = LayersList.ItemsSource as INotifyCollectionChanged;
        if (ReferenceEquals(incc, _observed))
            return;

        UnhookCollection();

        if (incc is not null)
        {
            incc.CollectionChanged += OnLayersChanged;
            _observed = incc;
        }
    }

    private void UnhookCollection()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnLayersChanged;
            _observed = null;
        }
    }

    private void OnLayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateEmptyHint();

    private void UpdateEmptyHint()
    {
        bool empty = (LayersList.ItemsSource as ICollection)?.Count is null or 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── layer management (context menu + header "+") ────────────────────
    //
    // The rail was a read-only file list — no rename / duplicate / delete
    // anywhere. These handlers add a per-row MenuFlyout (Rename… / Duplicate /
    // Delete… + New Layer), a header "+" button, and an empty-space → New Layer
    // right-tap. The file work itself lives on the VM (RenameLayer /
    // DuplicateLayer / DeleteLayer), which routes through the existing
    // LayerSerializer / LRU cache / RecentFiles / RefreshLayers plumbing; this
    // code-behind only owns the UI (menus + dialogs + surfacing status).

    private VisualistViewModel? Vm => DataContext as VisualistViewModel;

    private void OnNewLayerButtonClick(object sender, RoutedEventArgs e)
        => _ = InvokeNewLayerAsync();

    // Empty rail / header right-tap → New Layer only. Row right-taps set
    // Handled in OnLayerRowRightTapped, so those never reach this bubbling
    // handler on the root Grid.
    private void OnRailRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.Handled || Vm is null) return;
        if (sender is not FrameworkElement fe) return;
        var menu = new MenuFlyout();
        AddNewLayerItem(menu);
        ShowMenu(menu, fe, e.GetPosition(fe));
        e.Handled = true;
    }

    private void OnLayerRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Vm is null) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not LayerListItem item) return;

        var menu = new MenuFlyout();
        // File operations only apply to a real saved layer. The synthetic
        // "(unsaved)" row has no file on disk, so it gets New Layer only.
        if (!item.IsUnsaved)
        {
            var rename = new MenuFlyoutItem { Text = Localizer.T("common.context.rename", "Rename…") };
            rename.Click += (_, __) => _ = RenameLayerAsync(item);
            menu.Items.Add(rename);

            var duplicate = new MenuFlyoutItem { Text = Localizer.T("common.context.duplicate", "Duplicate") };
            duplicate.Click += (_, __) => DuplicateLayer(item);
            menu.Items.Add(duplicate);

            var delete = new MenuFlyoutItem { Text = Localizer.T("common.context.delete", "Delete…") };
            delete.Click += (_, __) => _ = DeleteLayerAsync(item);
            menu.Items.Add(delete);

            menu.Items.Add(new MenuFlyoutSeparator());
        }
        AddNewLayerItem(menu);

        ShowMenu(menu, fe, e.GetPosition(fe));
        // Consume so the root-Grid handler doesn't also open a New-Layer menu.
        e.Handled = true;
    }

    private void AddNewLayerItem(MenuFlyout menu)
    {
        var newLayer = new MenuFlyoutItem { Text = Localizer.T("visualist.rail.new_layer", "New Layer") };
        newLayer.Click += (_, __) => _ = InvokeNewLayerAsync();
        menu.Items.Add(newLayer);
    }

    private static void ShowMenu(MenuFlyout menu, FrameworkElement target, Point position)
    {
        try { menu.ShowAt(target, new FlyoutShowOptions { Position = position }); }
        catch (Exception ex) { GlobalLogger.Error("Visualist.LayerRail", "ShowMenu", ex); }
    }

    // Reuse the File → New Layer preset dialog so the rail's "+" / context New
    // Layer matches Ctrl+N exactly. Falls back to the VM's plain NewLayer when
    // no XamlRoot is available (pre-realised host).
    private async Task InvokeNewLayerAsync()
    {
        if (Vm is not { } vm) return;
        if (XamlRoot is null) { vm.NewLayer(); return; }
        try
        {
            var choice = await NewLayerDialog.ShowAsync(XamlRoot);
            if (choice is null) return;
            vm.NewLayer(choice.Name, choice.Preset, choice.Width, choice.Height);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.LayerRail", "InvokeNewLayer", ex);
        }
    }

    private async Task RenameLayerAsync(LayerListItem item)
    {
        if (Vm is not { } vm) return;
        if (XamlRoot is null || string.IsNullOrEmpty(item.Path)) return;

        string current = System.IO.Path.GetFileNameWithoutExtension(item.Path);
        string? name = await PromptForNameAsync(
            Localizer.T("visualist.rail.rename_title", "Rename Layer"),
            current, current);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (string.Equals(name, current, StringComparison.Ordinal)) return;

        VisualistViewModel.LayerFileOpResult result = vm.RenameLayer(item.Path, name);
        if (result != VisualistViewModel.LayerFileOpResult.Ok)
            await ShowOpErrorAsync(result, name);
    }

    private void DuplicateLayer(LayerListItem item)
    {
        if (Vm is not { } vm || string.IsNullOrEmpty(item.Path)) return;
        VisualistViewModel.LayerFileOpResult result = vm.DuplicateLayer(item.Path, out _);
        if (result != VisualistViewModel.LayerFileOpResult.Ok)
            _ = ShowOpErrorAsync(result, System.IO.Path.GetFileNameWithoutExtension(item.Path));
    }

    private async Task DeleteLayerAsync(LayerListItem item)
    {
        if (Vm is not { } vm) return;
        if (XamlRoot is null || string.IsNullOrEmpty(item.Path)) return;

        string display = item.FileName;
        // NEVER delete without an explicit confirm.
        var confirm = new ContentDialog
        {
            XamlRoot          = XamlRoot,
            Title             = Localizer.T("visualist.rail.delete_title", "Delete layer?"),
            Content           = string.Format(
                Localizer.T("visualist.rail.delete_body", "Delete layer \"{0}\"? This removes the .phxlayer file."),
                display),
            PrimaryButtonText = Localizer.T("common.button.delete", "Delete"),
            CloseButtonText   = Localizer.T("common.button.cancel", "Cancel"),
            // Default to Cancel so an accidental Enter doesn't destroy a file.
            DefaultButton     = ContentDialogButton.Close,
        };
        var res = await confirm.ShowAsync();
        if (res != ContentDialogResult.Primary) return;

        VisualistViewModel.LayerFileOpResult result = vm.DeleteLayer(item.Path);
        if (result != VisualistViewModel.LayerFileOpResult.Ok)
            await ShowOpErrorAsync(result, display);
    }

    // Simple single-line name prompt — mirrors WidgetEditorView.PromptForName
    // (MonoFont TextBox in a ContentDialog). No .xaml / InitializeComponent, so
    // it's safe to construct from this library assembly (see NewLayerDialog).
    private async Task<string?> PromptForNameAsync(string title, string placeholder, string initial)
    {
        if (XamlRoot is null) return null;
        var input = new TextBox
        {
            PlaceholderText = placeholder,
            Text            = initial,
            // [FONTCAST] MonoFont is an <x:String>; a direct cast throws.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(
                Application.Current.Resources["MonoFont"] as string ?? "Consolas"),
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
        // Focus + select the seeded text so a rename can be typed over at once.
        input.Loaded += (_, __) =>
        {
            try { input.Focus(FocusState.Programmatic); input.SelectAll(); }
            catch { /* best-effort focus */ }
        };
        var res = await dlg.ShowAsync();
        return res == ContentDialogResult.Primary ? input.Text : null;
    }

    // Surface a failed op as a single-button ContentDialog. The detail
    // (exception) is already in the System Log via the VM's GlobalLogger.Error.
    private async Task ShowOpErrorAsync(VisualistViewModel.LayerFileOpResult result, string name)
    {
        if (XamlRoot is null) return;
        string msg = result switch
        {
            VisualistViewModel.LayerFileOpResult.NameInvalid =>
                Localizer.T("visualist.rail.err_name_invalid", "That name can't be used for a layer file."),
            VisualistViewModel.LayerFileOpResult.NameTaken =>
                string.Format(Localizer.T("visualist.rail.err_name_taken", "A layer named \"{0}\" already exists."), name),
            VisualistViewModel.LayerFileOpResult.NotFound =>
                Localizer.T("visualist.rail.err_not_found", "That layer file no longer exists."),
            VisualistViewModel.LayerFileOpResult.OpenInSiblingWindow =>
                Localizer.T("visualist.rail.err_open_sibling",
                    "That layer is open in another Visualist window — close it there first."),
            _ =>
                Localizer.T("visualist.rail.err_io", "The operation failed — see the System Log."),
        };
        try
        {
            var dlg = new ContentDialog
            {
                XamlRoot        = XamlRoot,
                Title           = Localizer.T("visualist.rail.err_title", "Layer operation"),
                Content         = msg,
                CloseButtonText = Localizer.T("common.ok", "OK"),
                DefaultButton   = ContentDialogButton.Close,
            };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.LayerRail", "ShowOpError", ex);
        }
    }
}
