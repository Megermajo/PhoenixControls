using System;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.QuotesPanel;

/// <summary>
/// One row in the Quotes list — a stored <see cref="QuoteEntry"/> exposed as its
/// read-only number/metadata plus editable Text/Name (committed by the ViewModel's
/// Save button, which writes through QuotesService by number — the DB is the truth).
/// Delete is likewise driven by the ViewModel.
///
/// Since the house-shell rebuild the row is rendered TWICE: as a compact picker row
/// in the 0.9* roster column (<see cref="TextPreview"/> / <see cref="ProvenanceHint"/>
/// / <see cref="DirtyVisibility"/> / <see cref="RowBackground"/>) and, when it is the
/// selected row, as the full editor in the 1.4* detail column (Text / Name / the
/// provenance hints). Both surfaces bind the SAME instance, so a keystroke in the
/// editor moves the roster preview live and the dirty marker appears the moment the
/// edit becomes uncommitted.
/// </summary>
public sealed class QuoteRowVm : ObservableObject
{
    public QuoteRowVm(QuoteEntry entry)
    {
        Number = entry?.Number ?? 0;
        _text = entry?.Text ?? "";
        _name = entry?.Name ?? "";
        AddedBy = entry?.AddedBy ?? "";
        AddedAt = entry?.AddedAt ?? "";
    }

    /// <summary>The streamer-facing #N (stable, never reused).</summary>
    public int Number { get; }

    public string NumberText => "#" + Number.ToString(CultureInfo.InvariantCulture);

    private string _text;
    public string Text
    {
        get => _text;
        set { var v = value ?? ""; if (_text == v) return; _text = v; MarkDirty(); Raise(nameof(Text)); Raise(nameof(TextPreview)); }
    }

    private string _name;
    public string Name
    {
        get => _name;
        set { var v = value ?? ""; if (_name == v) return; _name = v; MarkDirty(); Raise(nameof(Name)); Raise(nameof(ProvenanceHint)); }
    }

    /// <summary>True once the streamer has typed an uncommitted edit into Text/Name.
    /// The list refresh skips its wholesale rebuild while any row is dirty so an
    /// in-progress edit (and the caret) survives a live QuotesChanged burst; cleared
    /// by <see cref="MarkClean"/> when the Save commits.
    ///
    /// It is also observable, because the roster row shows an "unsaved" marker off it:
    /// with the editor moved into the detail column, switching selection away from a
    /// half-typed row must not make that draft invisible. The draft itself lives on
    /// this instance and is never discarded by a selection change.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set { if (_isDirty == value) return; _isDirty = value; Raise(nameof(IsDirty)); Raise(nameof(DirtyVisibility)); }
    }

    private bool _isDirty;

    private void MarkDirty() => IsDirty = true;

    internal void MarkClean() => IsDirty = false;

    /// <summary>Roster "unsaved" marker gate.</summary>
    public Visibility DirtyVisibility => _isDirty ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Refresh this row's Text/Name from a freshly-read DB entry WITHOUT
    /// marking it dirty — used by the list reconcile so an externally-edited quote
    /// updates in place. A dirty row (the streamer is mid-edit) is left untouched so
    /// the in-progress edit and caret survive the refresh.</summary>
    internal void SyncFromEntry(QuoteEntry entry)
    {
        if (IsDirty || entry is null) return;
        string t = entry.Text ?? "";
        string n = entry.Name ?? "";
        if (_text != t) { _text = t; Raise(nameof(Text)); Raise(nameof(TextPreview)); }
        if (_name != n) { _name = n; Raise(nameof(Name)); Raise(nameof(ProvenanceHint)); }
    }

    public string AddedBy { get; }
    public string AddedAt { get; }

    /// <summary>Compact "by &lt;user&gt;" provenance line (empty when unknown).</summary>
    public string AddedByHint => string.IsNullOrWhiteSpace(AddedBy)
        ? ""
        : string.Format(Localizer.T("panel.quotes.editor.added_by", "added by {0}"), AddedBy);

    /// <summary>Compact local date the quote was stored ("2026-07-22"; empty when
    /// unknown). AddedAt is stamped as an ISO-8601 UTC round-trip string by
    /// DB.Quotes; an unparseable value falls back to the raw string so the
    /// provenance is never silently dropped.</summary>
    public string AddedAtHint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(AddedAt)) return "";
            return DateTimeOffset.TryParse(AddedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var stamped)
                ? stamped.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : AddedAt;
        }
    }

    // ── Roster projections (0.9* picker column) ─────────────────────────

    /// <summary>Single-line preview of the quote text for the narrow roster row. The
    /// row itself trims with an ellipsis, but embedded line breaks would otherwise
    /// grow the row, so they are folded to spaces here. A blank quote shows the
    /// placeholder rather than an empty row nobody can click meaningfully.</summary>
    public string TextPreview
    {
        get
        {
            string t = (_text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length == 0 ? Localizer.T("panel.quotes.roster.empty_quote", "(empty quote)") : t;
        }
    }

    /// <summary>Attribution + date on one mono line for the roster row: "— Name ·
    /// added by bob · 2026-07-22", with every missing part dropped rather than
    /// rendered blank.</summary>
    public string ProvenanceHint
    {
        get
        {
            string who = (_name ?? "").Trim();
            string by = AddedByHint;
            string at = AddedAtHint;

            string s = who.Length > 0 ? "— " + who : "";
            if (by.Length > 0) s = s.Length > 0 ? s + " · " + by : by;
            if (at.Length > 0) s = s.Length > 0 ? s + " · " + at : at;
            return s;
        }
    }

    private bool _isSelected;

    /// <summary>True while this row is the one the detail column is editing.</summary>
    public bool IsSelected => _isSelected;

    internal void SetSelected(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        Raise(nameof(IsSelected));
        Raise(nameof(RowBackground));
    }

    /// <summary>Roster row ground — an ember wash on the selected row, Transparent
    /// otherwise. Transparent and NOT null: a null Background is not hit-testable in
    /// WinUI, which would make the un-selected rows unclickable.</summary>
    public Brush RowBackground => _isSelected
        ? (s_selectedWash ??= ToolBrushes.HorizontalEmberFade(0x22))
        : (s_transparent ??= new SolidColorBrush(Colors.Transparent));

    private static Brush? s_selectedWash, s_transparent;
}
