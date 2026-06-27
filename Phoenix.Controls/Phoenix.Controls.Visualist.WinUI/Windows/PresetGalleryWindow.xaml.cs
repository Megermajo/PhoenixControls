using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// C3 (audit/winui-regressions-2026-05-24) — Visualist preset gallery
/// window. Lists every built-in <see cref="WidgetPreset"/> as a tile in a
/// 3-column GridView; each tile has an Apply / Drop-on-canvas button that
/// routes through <see cref="VisualistViewModel.ApplyPreset"/>.
///
/// <para>
/// The audit calls for "presets discovered in data/presets/" — that path
/// is not populated yet (no user-authored preset format ships). The
/// gallery currently surfaces the built-in <see cref="WidgetPreset"/>
/// enum (Image / Video / Text / Audio / WebSource / Particles / Chat / CC)
/// so the discovery surface lights up without blocking on the format
/// design. When the disk-backed preset catalogue lands, this view-model
/// switches to load that catalogue and the built-in entries either stay
/// (as a "starter" section) or migrate to authored .phxpreset files.
/// </para>
///
/// <para>Per <c>feedback_visualist_architect_chrome_independence.md</c> the
/// window owns its own paint helpers (no Phoenix.Controls.Shared/UI/).
/// Per <c>feedback_no_modal_dialogs_for_repeatable_rejections.md</c>
/// applying a preset commits silently — no confirmation modal.</para>
/// </summary>
public sealed partial class PresetGalleryWindow : Window
{
    private readonly VisualistViewModel _vm;
    private readonly string? _initialTargetWidgetId;

    /// <summary>
    /// View-model wrapper exposed to the GridView's DataTemplate. Mirrors
    /// the per-tile chrome: name + thumbnail brush + label + button text +
    /// the <see cref="WidgetPreset"/> the Apply button forwards back.
    /// </summary>
    public sealed class PresetTile
    {
        // Plain settable properties — XamlTypeInfo's generated metadata
        // pathway for bound DataTemplate properties requires set; accessors,
        // not init-only. Mutation outside the constructor flow isn't
        // expected and there's no INotifyPropertyChanged: a tile, once
        // built, is read-only by convention.
        public string Name { get; set; } = "";
        public string ThumbnailLabel { get; set; } = "";
        public Brush ThumbnailBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.DimGray);
        public string ButtonLabel { get; set; } = "Apply";
        public WidgetPreset Preset { get; set; }
    }

    public ObservableCollection<PresetTile> Tiles { get; } = new();

    /// <summary>
    /// Constructor. The caller supplies the live <see cref="VisualistViewModel"/>
    /// so the gallery talks to the same document the user is editing.
    /// <paramref name="targetWidgetId"/> seeds the "apply to selected"
    /// case: when non-null the buttons read "Apply" and the preset
    /// replaces the existing widget's onStartup graph; when null the
    /// buttons read "Drop on Canvas" and Apply spawns a new widget.
    /// </summary>
    public PresetGalleryWindow(VisualistViewModel vm, string? targetWidgetId = null)
    {
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        _initialTargetWidgetId = targetWidgetId;
        InitializeComponent();

        Title = "Phoenix Controls — Preset Gallery";

        bool hasTarget = !string.IsNullOrEmpty(targetWidgetId);
        HeaderSubtitle.Text = hasTarget
            ? "Apply replaces the selected widget's preset + onStartup graph."
            : "Drop on Canvas spawns a new widget with the preset's starter graph.";

        try
        {
            PopulateTiles(hasTarget);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PresetGalleryWindow", "PopulateTiles", ex);
        }

        if (Tiles.Count == 0)
        {
            PresetGrid.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "No presets yet — save a widget graph as preset via Edit → Save Preset.";
        }
        else
        {
            PresetGrid.ItemsSource = Tiles;
        }

        StatusText.Text = $"{Tiles.Count} preset{(Tiles.Count == 1 ? "" : "s")} available";
    }

    private void PopulateTiles(bool hasTarget)
    {
        string buttonLabel = hasTarget ? "Apply" : "Drop on Canvas";

        // Each tile's swatch is a gradient flavoured by the preset's
        // semantic colour — matches Visualist's existing "preset-to-thumb"
        // convention in WidgetView.ApplyAppearance. The colours route
        // through theme tokens with literal-ARGB fallbacks so a stripped
        // theme can't tear down the gallery.
        Tiles.Add(MakeTile(WidgetPreset.Image,     "Image",      "img → display",      0xC8, 0x82, 0x2B, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Video,     "Video",      "loadUrl → display",  0x88, 0x9B, 0x2E, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Text,      "Text",       "text.render",        0xE5, 0xA2, 0x4E, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Audio,     "Audio",      "audio.load → play",  0x7A, 0x47, 0x10, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.WebSource, "Web Source", "embed URL",          0x4A, 0x6E, 0x9A, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Particles, "Particles",  "particles.emit",     0xB8, 0x6E, 0x3C, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Chat,      "Chat",       "onTrigger → text",   0x55, 0x6E, 0x8F, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.CC,        "Captions",   "live caption",       0x9A, 0x6E, 0x4A, buttonLabel));
    }

    private static PresetTile MakeTile(
        WidgetPreset preset, string name, string thumbLabel,
        byte r, byte g, byte b, string buttonLabel)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint   = new Windows.Foundation.Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, r, g, b),         Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF,
            (byte)Math.Max(0, r - 0x30),
            (byte)Math.Max(0, g - 0x30),
            (byte)Math.Max(0, b - 0x30)), Offset = 1.0 });
        return new PresetTile
        {
            Name = name,
            ThumbnailLabel = thumbLabel,
            ThumbnailBrush = brush,
            ButtonLabel = buttonLabel,
            Preset = preset,
        };
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not WidgetPreset preset) return;

            // Honour the constructor-supplied target: when null the first
            // click after open spawns a fresh widget; after a spawn a
            // future click also spawns rather than mutating the just-
            // created widget (mirrors palette behaviour — each click is a
            // new spawn).
            var spawned = _vm.ApplyPreset(preset, _initialTargetWidgetId);
            if (spawned is null)
            {
                StatusText.Text = $"Apply '{preset}' failed — check System Log";
                return;
            }
            StatusText.Text = _initialTargetWidgetId is null
                ? $"Spawned '{spawned.Name}' ({preset}) on canvas"
                : $"Applied '{preset}' to '{spawned.Name}'";
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PresetGalleryWindow", "OnApplyClicked", ex);
            StatusText.Text = "Apply failed — check System Log";
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* best-effort */ }
    }
}
