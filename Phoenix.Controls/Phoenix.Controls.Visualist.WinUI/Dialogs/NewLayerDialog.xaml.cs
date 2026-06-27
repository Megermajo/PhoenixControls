using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// C12 (audit/winui-regressions-2026-05-24) — preset picker shown when the
/// user invokes File → New Layer. Pre-fix the new document came in at the
/// engine's hard-coded FullHD default; now the dialog surfaces B35's
/// preset → resolution mapping at the explicit creation event so the user
/// can pick FullHD / QHD / UHD / Vertical / Square or supply a custom W/H
/// upfront. Cancel returns a null <see cref="Choice"/> and the caller is
/// expected to abort the New action.
///
/// Per <c>feedback_no_modal_dialogs_for_repeatable_rejections.md</c> this
/// is a modal at a single explicit creation event (not a repeatable
/// rejection), so a <see cref="ContentDialog"/> is the appropriate
/// affordance. Per
/// <c>feedback_visualist_architect_chrome_independence.md</c> the visual
/// treatment authors its own copy of the Architect KeyboardShortcutsDialog
/// chrome rather than lifting the resource dictionary.
/// </summary>
public sealed partial class NewLayerDialog : ContentDialog
{
    /// <summary>
    /// User's selection. <see cref="Name"/> defaults to "untitled" when the
    /// name box is left blank. Width/Height reflect the chosen preset's
    /// canonical resolution, or the Custom NumberBox values when
    /// <see cref="Preset"/> is <see cref="LayerPreset.Custom"/>.
    /// </summary>
    public sealed record Choice(string Name, LayerPreset Preset, int Width, int Height);

    private static readonly LayerPreset[] s_presets =
        (LayerPreset[])Enum.GetValues(typeof(LayerPreset));

    /// <summary>The user's confirmed selection, or null when the dialog was cancelled.</summary>
    public Choice? Result { get; private set; }

    public NewLayerDialog()
    {
        InitializeComponent();

        // Populate the preset combo from the enum so a future preset addition
        // shows up here without touching this file. Default to FullHD — the
        // most common OBS browser-source size and the engine's prior hidden
        // default — so a user who just hits Enter ends up where they used to.
        foreach (LayerPreset p in s_presets)
            PresetBox.Items.Add(p.ToString());
        PresetBox.SelectedIndex = Array.IndexOf(s_presets, LayerPreset.FullHD);

        PrimaryButtonClick += OnPrimaryButtonClick;

        // Default focus on the name field so the user can type the layer name
        // immediately without clicking. Opened (not Loaded) is used so the
        // XamlRoot/visual tree is fully realized before we move focus —
        // mirrors NameTypeDialog. SelectAll() lets a quick rename overwrite
        // any seeded text. Wrapped in try/catch because Focus() can throw if
        // the dialog is torn down before Opened completes.
        Opened += (_, _) =>
        {
            try
            {
                NameBox.Focus(FocusState.Programmatic);
                NameBox.SelectAll();
            }
            catch { /* best-effort focus — never crash the New Layer flow */ }
        };

        // Enter inside the (single-line) name field commits the dialog as if
        // the Create button was pressed. ContentDialog's DefaultButton="Primary"
        // does NOT translate Enter-in-a-TextBox into a Primary click on its
        // own, so we wire it explicitly. Esc still routes to the Close button
        // via the built-in ContentDialog behavior.
        NameBox.KeyDown += OnNameBoxKeyDown;
    }

    private void OnNameBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;

        // Run the same commit path the Create button uses, then close with a
        // Primary result so ShowAsync returns the Choice rather than null.
        OnPrimaryButtonClick(this, null!);
        if (Result is not null)
            Hide();
    }

    /// <summary>
    /// Async show wrapper — sets <see cref="FrameworkElement.XamlRoot"/> and
    /// surfaces the user's choice (or null on cancel). Routes failures
    /// through <see cref="GlobalLogger.Error"/> so a teardown-time XamlRoot
    /// fault doesn't crash the New Layer flow.
    /// </summary>
    public static async System.Threading.Tasks.Task<Choice?> ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot is null)
        {
            GlobalLogger.Log(
                "NewLayerDialog: no XamlRoot available — skipping show.",
                "Visualist.Dialogs",
                LogLevel.System);
            return null;
        }
        try
        {
            var dlg = new NewLayerDialog { XamlRoot = xamlRoot };
            await dlg.ShowAsync();
            // Honor a populated Result regardless of the closing button code.
            // The Create button (Primary) and the Enter-to-commit path both set
            // Result before the dialog closes; the Enter path closes via Hide()
            // which reports ContentDialogResult.None, so we cannot key off the
            // button result alone. Cancel / Esc never run the commit path, so
            // Result stays null there.
            return dlg.Result;
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("Visualist.Dialogs", "NewLayerDialog.ShowAsync", ex);
            return null;
        }
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = PresetBox.SelectedIndex;
        if (idx < 0 || idx >= s_presets.Length) return;
        LayerPreset p = s_presets[idx];

        // Custom unlocks the W/H NumberBoxes; named presets show the
        // canonical resolution as a hint so the user can confirm at a glance.
        if (p == LayerPreset.Custom)
        {
            CustomSizeRow.Visibility = Visibility.Visible;
            PresetHint.Text = "custom — pick width and height";
        }
        else
        {
            CustomSizeRow.Visibility = Visibility.Collapsed;
            var (w, h) = p.ToResolution();
            PresetHint.Text = $"{w} × {h}";
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        int idx = PresetBox.SelectedIndex;
        if (idx < 0 || idx >= s_presets.Length)
        {
            // No selection — defer to FullHD so a stray empty selection
            // doesn't drop the user into a Custom W/H = 0 invalid state.
            idx = Array.IndexOf(s_presets, LayerPreset.FullHD);
        }
        LayerPreset preset = s_presets[idx];

        int w, h;
        if (preset == LayerPreset.Custom)
        {
            // NumberBox.Value is double; clamp to the int domain before the
            // cast so an out-of-range edit doesn't silently overflow.
            double rawW = double.IsNaN(CustomWidthBox.Value)  ? 1920 : CustomWidthBox.Value;
            double rawH = double.IsNaN(CustomHeightBox.Value) ? 1080 : CustomHeightBox.Value;
            w = Math.Max(1, (int)Math.Round(rawW));
            h = Math.Max(1, (int)Math.Round(rawH));
        }
        else
        {
            var (pw, ph) = preset.ToResolution();
            w = pw;
            h = ph;
        }

        string name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name)) name = "untitled";

        Result = new Choice(name, preset, w, h);
    }
}
