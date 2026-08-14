using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Phoenix.Controls.Visualist.WinUI.ViewModels;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Visualist preset gallery
/// window. Lists every built-in <see cref="WidgetPreset"/> as a tile in a
/// 3-column GridView; each tile has an Apply / Drop-on-canvas button that
/// routes through <see cref="VisualistViewModel.ApplyPreset"/>.
///
/// <para>
/// The intended design is "presets discovered in data/presets/" — that path
/// is not populated yet (no user-authored preset format ships). The
/// gallery currently surfaces the built-in <see cref="WidgetPreset"/>
/// enum (Image / Video / Text / Audio / WebSource / Particles / Chat / CC)
/// so the discovery surface lights up without blocking on the format
/// design. When the disk-backed preset catalogue lands, this view-model
/// switches to load that catalogue and the built-in entries either stay
/// (as a "starter" section) or migrate to authored .phxpreset files.
/// </para>
///
/// <para>Tile names are read from <c>WidgetPresetLabels</c> — the single author-facing
/// spelling shared with the Inspector's picker and the canvas context menu.</para>
///
/// <para>The window owns its own paint helpers (no Phoenix.Controls.Shared/UI/).
/// Applying a preset commits silently — no confirmation modal.</para>
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

        Title = Localizer.T("visualist.window.preset_gallery.caption", "Phoenix Controls — Preset Gallery");

        bool hasTarget = !string.IsNullOrEmpty(targetWidgetId);
        HeaderSubtitle.Text = hasTarget
            ? Localizer.T("visualist.window.preset_gallery.subtitle.apply",
                "Apply replaces the selected widget's preset + onStartup graph.")
            : Localizer.T("visualist.window.preset_gallery.subtitle.spawn",
                "Drop on Canvas spawns a new widget with the preset's starter graph.");

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
            EmptyHint.Text = Localizer.T("visualist.window.preset_gallery.empty",
                "No presets yet — save a widget graph as preset via Edit → Save Preset.");
        }
        else
        {
            PresetGrid.ItemsSource = Tiles;
        }

        StatusText.Text = string.Format(
            Tiles.Count == 1
                ? Localizer.T("visualist.window.preset_gallery.status.count.one",   "{0} preset available")
                : Localizer.T("visualist.window.preset_gallery.status.count.other", "{0} presets available"),
            Tiles.Count);
    }

    private void PopulateTiles(bool hasTarget)
    {
        string buttonLabel = hasTarget
            ? Localizer.T("common.button.apply", "Apply")
            : Localizer.T("visualist.window.preset_gallery.button.drop", "Drop on Canvas");

        // Each tile's swatch is a gradient flavoured by the preset's
        // semantic colour — matches Visualist's existing "preset-to-thumb"
        // convention in WidgetView.ApplyAppearance. The colours route
        // through theme tokens with literal-ARGB fallbacks so a stripped
        // theme can't tear down the gallery.
        //
        // Tile NAMES are not typed here any more: they come from WidgetPresetLabels, the one
        // shared spelling every Visualist surface reads. Hand-typed names here were how the
        // gallery ended up calling a preset "Alert Box" while the Inspector's picker called the
        // same thing "AlertBox".
        Tiles.Add(MakeTile(WidgetPreset.Image,     "img → display",      0xC8, 0x82, 0x2B, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Video,     "loadUrl → display",  0x88, 0x9B, 0x2E, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Text,      "text.render",        0xE5, 0xA2, 0x4E, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Audio,     "audio.load → play",  0x7A, 0x47, 0x10, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.WebSource, "embed URL",          0x4A, 0x6E, 0x9A, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Particles, "particles.emit",     0xB8, 0x6E, 0x3C, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.Chat,      "onTrigger → text",   0x55, 0x6E, 0x8F, buttonLabel));
        Tiles.Add(MakeTile(WidgetPreset.CC,        "live caption",       0x9A, 0x6E, 0x4A, buttonLabel));
        // V8 — the only COMPILED preset. Applying it seeds the idle (blank) onStartup AND
        // compiles an onTrigger:<id> alert chain from AlertBoxSettings; the per-kind media
        // is then configured in the Inspector's ALERT BOX section. No asset is needed — the
        // tile swatch is a generated gradient like every other row, tinted with the alert
        // red-orange band so it reads as the "something fires here" preset.
        Tiles.Add(MakeTile(WidgetPreset.AlertBox,  "arg → select → img+snd", 0xC2, 0x54, 0x3C, buttonLabel));
        // V15 — the iframe preset. Subtitled by the FEED, not by the word "embed": the
        // WebSource tile above already reads "embed URL" and is a URL-fetched image, so
        // two tiles promising an embed would be the collision this naming avoids.
        // Deep violet so it does not read as a sibling of the blue WebSource swatch.
        Tiles.Add(MakeTile(WidgetPreset.Player,    "song queue / clip",  0x6B, 0x4A, 0x9A, buttonLabel));
    }

    private static PresetTile MakeTile(
        WidgetPreset preset, string thumbLabel,
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
            Name = WidgetPresetLabels.For(preset),
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
            // Status lines use the same shared label as the tiles — an author who clicked
            // "Alert Box" must not be told "Applied 'AlertBox'".
            string label = WidgetPresetLabels.For(preset);

            // ★ The out-params are the whole point of this call shape, and there are TWO because
            // they answer two different questions.
            //
            // `refusal` names WHY an alert chain is missing. ApplyPreset used to return a non-null
            // widget on both of its Alert Box skips and on a compile refusal too, so a
            // `spawned is null` test reported "Applied 'Alert Box' to 'x'" for every one of them —
            // a streamer who detached a widget and re-picked Alert Box to reset it was told it
            // worked while nothing at all had happened.
            //
            // `outcome` says WHAT HAPPENED, and this window must not re-derive it. It used to
            // choose "Spawned …" versus "Applied … to …" from whether _initialTargetWidgetId was
            // null — i.e. from what was selected when the window OPENED. This window is non-modal:
            // the author can delete that widget on the canvas and then press Apply, ApplyPreset
            // finds no target, takes the SPAWN branch and creates a brand-new widget — and the
            // strip said a widget had been converted. The same value also separates a clean
            // refusal from a throw caught after the destructive half, which need opposite advice.
            var spawned = _vm.ApplyPreset(preset, _initialTargetWidgetId, out var refusal, out var outcome);
            switch (outcome)
            {
                case WidgetPresets.PresetApplyOutcome.RefusedNoChange:
                    // The ONLY outcome that may be described as "not applied".
                    StatusText.Text = string.Format(
                        Localizer.T("visualist.window.preset_gallery.status.refused_format",
                            "'{0}' not applied — {1}"),
                        label, DescribeRefusal(refusal));
                    return;

                case WidgetPresets.PresetApplyOutcome.FailedAfterChange:
                    // Never "not applied": the widget really was changed before the throw, and
                    // the undo entry the apply took is a real recovery the author has to be told
                    // about — otherwise they read a failure and leave the half-applied state.
                    StatusText.Text = string.Format(
                        Localizer.T("visualist.window.preset_gallery.status.partial_format",
                            "'{0}' was only PARTIALLY applied to '{1}' — something went wrong after "
                            + "the widget had already been changed. Press Ctrl+Z in the editor to "
                            + "restore it, then check the System Log."),
                        label,
                        spawned?.Name ?? Localizer.T("visualist.window.preset_gallery.target.the_widget", "the widget"));
                    return;

                case WidgetPresets.PresetApplyOutcome.FailedAfterSpawn:
                    // ★ The same post-change window as the arm above, and deliberately NOT the same
                    // sentence. This arm's widget was CREATED by the apply, so "PARTIALLY applied to
                    // '<name>'" would name a widget the author never had, and "Ctrl+Z to restore it"
                    // is inverted — Ctrl+Z REMOVES it. Told to restore something that only gets
                    // deleted, the author goes looking for work that was never at risk. Ctrl+Z is
                    // still the right key; it just does the other thing here, so say which.
                    StatusText.Text = string.Format(
                        Localizer.T("visualist.window.preset_gallery.status.half_built_format",
                            // ★ Keep "REMOVE it" contiguous in this source text. Wrapping the line
                            // between REMOVE and it renders identically but breaks
                            // AlertBoxPresetTests' source anchor, which is what pins this arm as
                            // the one saying REMOVE instead of the sibling arm's "restore it".
                            "'{0}' was only HALF-BUILT as a new widget ('{1}') — something went wrong "
                            + "after it had already been created. Press Ctrl+Z in the editor to "
                            + "REMOVE it (nothing of yours was replaced), then check the System Log."),
                        label,
                        spawned?.Name ?? Localizer.T("visualist.window.preset_gallery.target.the_new_widget", "the new widget"));
                    return;

                default:
                    string applied = outcome == WidgetPresets.PresetApplyOutcome.Spawned
                        ? string.Format(
                            Localizer.T("visualist.window.preset_gallery.status.spawned_format",
                                "Spawned '{0}' ({1}) on canvas"),
                            spawned?.Name, label)
                        : string.Format(
                            Localizer.T("visualist.window.preset_gallery.status.applied_format",
                                "Applied '{0}' to '{1}'"),
                            label, spawned?.Name);
                    StatusText.Text = refusal is null
                        ? applied
                        : string.Format(
                            Localizer.T("visualist.window.preset_gallery.status.applied_no_chain_format",
                                "{0} — but no alert chain was generated: {1}"),
                            applied, DescribeRefusal(refusal));
                    return;
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("PresetGalleryWindow", "OnApplyClicked", ex);
            StatusText.Text = Localizer.T("visualist.window.preset_gallery.status.apply_failed",
                "Apply failed — check System Log");
        }
    }

    /// <summary>
    /// One line per refusal, worded to agree with the Inspector's <c>ReportAlertBoxResult</c> —
    /// the same outcome must not read as two different problems depending on which surface the
    /// author happened to use. Shortened to fit a one-line status strip; the Inspector keeps the
    /// long form because it has the room and the fields to act on.
    /// </summary>
    private static string DescribeRefusal(WidgetPresets.AlertBoxRegenResult? refusal) => refusal switch
    {
        WidgetPresets.AlertBoxRegenResult.Detached =>
            Localizer.T("visualist.window.preset_gallery.refusal.detached",
                "this Alert Box is detached, so its settings no longer generate its graph. "
                + "Detach is one-way — add a new Alert Box widget instead."),
        WidgetPresets.AlertBoxRegenResult.TriggerNameTaken =>
            Localizer.T("visualist.window.preset_gallery.refusal.trigger_taken",
                "that trigger name is already a hand-built trigger on this widget, so nothing was "
                + "generated and your graph is untouched. Rename it, or give the Alert Box a "
                + "different trigger id in the Inspector."),
        WidgetPresets.AlertBoxRegenResult.InvalidTriggerId =>
            Localizer.T("visualist.window.preset_gallery.refusal.invalid_trigger",
                "the Alert Box trigger name is invalid — nothing was compiled."),
        WidgetPresets.AlertBoxRegenResult.RegistryUnavailable =>
            Localizer.T("visualist.window.preset_gallery.refusal.no_templates",
                "node templates unavailable — nothing was compiled. Check the System Log."),
        WidgetPresets.AlertBoxRegenResult.NoDocument =>
            Localizer.T("visualist.window.preset_gallery.refusal.no_document",
                "no layer is open any more, so there was nothing to apply the preset to. Open or "
                + "create a layer first."),
        _ => Localizer.T("visualist.window.preset_gallery.refusal.unknown",
                "something went wrong — check the System Log."),
    };

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        try { Close(); } catch { /* best-effort */ }
    }
}
