using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.WinUI.Canvas;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

/// <summary>
///  P1-A17 — Architect "Keyboard Shortcuts" reference dialog.
/// <para/>
/// Restores the pre-T15 <c>MainForm.ShowKeyboardShortcuts()</c> surface
/// that listed every chord the WinForms canvas + menu bar honoured. The
/// WinUI rewrite carried the chord set forward (and extended it — Sprint
/// 34 added Dvorak scancode coverage and F1-node-documentation) but lost
/// the discoverability surface. The new dialog is reached from:
/// <list type="bullet">
///   <item>Help → Keyboard Shortcuts… in <c>ArchitectChrome</c> (embedded view).</item>
///   <item>Help → Keyboard Shortcuts… in <c>ArchitectSiblingWindow</c> (multi-window).</item>
///   <item>F1 on an empty canvas ( P1-A13 fallback).</item>
/// </list>
/// </summary>
/// <remarks>
/// The chord catalog is hand-curated in this file (not parsed from XAML
/// attributes / source comments) — declarative listings drift quickly
/// when handlers are extended without touching the chrome XAML, and a
/// runtime XAML parse would couple the dialog to a brittle string-walk.
/// Edits to either column should mirror the canvas / chrome edits in the
/// same sprint so the dialog stays trustworthy as a contract.
/// </remarks>
public sealed partial class KeyboardShortcutsDialog : ContentDialog
{
    /// <summary>
    /// Canvas column sections — chord vocabulary surfaced inside the
    /// <see cref="Canvas.LogicCanvasView"/> Keyboard / QuickKeys / Pointer
    /// / DragDrop partials.  scancode-anchored chords
    /// (Ctrl+Z/Y/C/V/X/D/A/S/F/G) read by their QWERTY position regardless
    /// of active layout — the catalog lists the QWERTY label since that's
    /// the physical key Majo's audit canonicalised as the chord identity.
    /// </summary>
    private static readonly string[] CanvasSections =
    {
        ArchitectHotkeyCatalog.SectionEdit,
        ArchitectHotkeyCatalog.SectionNavigateView,
        ArchitectHotkeyCatalog.SectionSpawnPalette,
        ArchitectHotkeyCatalog.SectionFind,
        ArchitectHotkeyCatalog.SectionQuickKeySpawn,
        ArchitectHotkeyCatalog.SectionBookmarks,
        ArchitectHotkeyCatalog.SectionDocumentation,
        ArchitectHotkeyCatalog.SectionRetired,
    };

    /// <summary>
    /// Chrome column sections — chords routed through
    /// <see cref="Controls.ArchitectChrome"/> and
    /// <c>ArchitectSiblingWindow</c>'s <c>KeyboardAccelerators</c>. The
    /// canvas list duplicates several entries (Ctrl+S / Ctrl+O / Ctrl+Shift+S
    /// etc.) because the user's mental model splits between "canvas action"
    /// and "menu action" — the chrome accelerators route through the
    /// chrome event chain (MainView.OnChrome*), the canvas accelerators
    /// short-circuit inside the canvas itself. Functionally identical for
    /// the user; useful when diagnosing why a chord stopped firing.
    /// </summary>
    private static readonly string[] ChromeSections =
    {
        ArchitectHotkeyCatalog.SectionFile,
        ArchitectHotkeyCatalog.SectionEdit,
        ArchitectHotkeyCatalog.SectionView,
        ArchitectHotkeyCatalog.SectionHelp,
    };

    public KeyboardShortcutsDialog()
    {
        InitializeComponent();
        // Pre-resolve the dialog-local styles once. ContentDialog's content
        // sub-tree doesn't reliably inherit the dialog's own Resources via
        // implicit FindResource walks (the popup host re-parents the
        // content), so resolving against `this.Resources` up-front sidesteps
        // the "row uses an empty default Style" failure mode.
        var headerStyle = (Style)Resources["ShortcutGroupHeader"];
        var chordStyle  = (Style)Resources["ShortcutChord"];
        var descStyle   = (Style)Resources["ShortcutDescription"];
        // Single source of truth lives in ArchitectHotkeyCatalog — the
        // dialog and the bottom-left HotkeyCheatsheet overlay both render
        // off the same entries so chord-set changes stay in lockstep.
        BuildColumn(CanvasColumn, ArchitectHotkeyCatalog.GroupedBySection(CanvasSections),
                    headerStyle, chordStyle, descStyle);
        BuildColumn(ChromeColumn, ArchitectHotkeyCatalog.GroupedBySection(ChromeSections),
                    headerStyle, chordStyle, descStyle);
    }

    /// <summary>
    /// Show the dialog on <paramref name="xamlRoot"/>. Wrapped so the
    /// caller doesn't have to set <see cref="ContentDialog.XamlRoot"/>
    /// manually and so failures (e.g. XamlRoot null because the host
    /// window is mid-teardown) route through GlobalLogger consistently.
    /// </summary>
    public static async System.Threading.Tasks.Task ShowAsync(XamlRoot xamlRoot)
    {
        if (xamlRoot is null)
        {
            GlobalLogger.Log(
                "KeyboardShortcutsDialog: no XamlRoot available — skipping show.",
                "Architect.Dialogs",
                Phoenix.Controls.Shared.Models.LogLevel.System);
            return;
        }

        try
        {
            var dlg = new KeyboardShortcutsDialog { XamlRoot = xamlRoot };
            await dlg.ShowAsync();
        }
        catch (Exception ex)
        {
            GlobalLogger.Error(
                "Architect.Dialogs",
                "KeyboardShortcutsDialog.ShowAsync",
                ex);
        }
    }

    /// <summary>
    /// Populate <paramref name="host"/> with section headers + chord rows.
    /// Chord cells are fixed-width (Grid.Column 0 ⇒ 180 DIP) so chord
    /// glyphs align across rows; descriptions wrap into the remaining
    /// column.
    /// </summary>
    private static void BuildColumn(
        StackPanel host,
        IReadOnlyList<(string Section, IReadOnlyList<HotkeyEntry> Entries)> groups,
        Style headerStyle,
        Style chordStyle,
        Style descStyle)
    {
        foreach (var (section, entries) in groups)
        {
            host.Children.Add(new TextBlock
            {
                Text  = section,
                Style = headerStyle,
            });

            foreach (var entry in entries)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chordText = new TextBlock
                {
                    Text  = entry.Combo,
                    Style = chordStyle,
                };
                Grid.SetColumn(chordText, 0);

                var descText = new TextBlock
                {
                    Text  = entry.Description,
                    Style = descStyle,
                };
                Grid.SetColumn(descText, 1);

                row.Children.Add(chordText);
                row.Children.Add(descText);
                host.Children.Add(row);
            }
        }
    }
}
