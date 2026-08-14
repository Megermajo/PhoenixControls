using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Phoenix.Controls.Hub.WinUI.Panels.Common;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Hub.WinUI.Panels.LoyaltyPanel;

/// <summary>
/// One editable text / number field on the Loyalty page, generalised so the
/// six config tabs can carry dozens of inputs without a property triple each.
/// The field reads the live working-copy value through <c>_format</c> (so it
/// never holds a stale snapshot) and writes edits through <c>_apply</c>, which
/// parses the draft, mutates the working <see cref="LoyaltyConfig"/> and
/// schedules a debounced save. An unparseable draft is simply retained without
/// a write — the user keeps typing; nothing is clobbered. Validation posture
/// per the standing rule: numeric range clamp only, strings saved verbatim.
/// </summary>
public sealed class LoyaltyDraftField : ObservableObject
{
    private readonly Func<string> _format;
    private readonly Action<string> _apply;
    private string _draft;

    public LoyaltyDraftField(Func<string> format, Action<string> apply)
    {
        _format = format;
        _apply = apply;
        _draft = _format();
    }

    /// <summary>Raw text bound two-way to the input box.</summary>
    public string Text
    {
        get => _draft;
        set { if (Set(ref _draft, value ?? string.Empty)) _apply(_draft); }
    }
}

/// <summary>
/// A single boolean toggle. <see cref="Value"/> is <c>bool?</c> (the shape a
/// <c>CheckBox.IsChecked</c> two-way binding needs, and the shape the rest of
/// the panel's field factories still hand around); the getter reads the live
/// working value, the setter coerces null→false, writes through and schedules a
/// save.
///
/// Since the shell rebuild the rendered control is a <c>TogglePill</c>, which is
/// DISPLAY-ONLY and takes a plain <c>bool</c> — hence <see cref="IsOn"/>. The
/// pill binds OneWay to that projection and the host Button's Click flips
/// <see cref="Value"/>, so both members must raise for the other.
/// </summary>
public sealed class LoyaltyBoolField : ObservableObject
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    public LoyaltyBoolField(Func<bool> get, Action<bool> set)
    {
        _get = get;
        _set = set;
    }

    public bool? Value
    {
        get => _get();
        set { _set(value == true); Raise(nameof(Value)); Raise(nameof(IsOn)); }
    }

    /// <summary>
    /// Non-nullable projection of <see cref="Value"/> for <c>TogglePill.IsOn</c>.
    /// Read-only on purpose: the pill paints, it never writes.
    /// </summary>
    public bool IsOn => _get();
}

/// <summary>
/// Factory helpers that wire a getter / setter on the working config to a
/// draft field, folding the shared "mutate then schedule save" step
/// (<paramref name="changed"/>) into every write so the call sites in the row
/// / section VMs stay one line each.
/// </summary>
internal static class LoyaltyField
{
    public static LoyaltyBoolField Bool(Func<bool> get, Action<bool> set, Action changed)
        => new(get, v => { set(v); changed(); });

    public static LoyaltyDraftField Text(Func<string> get, Action<string> set, Action changed)
        => new(get, s => { set(s ?? string.Empty); changed(); });

    public static LoyaltyDraftField Int(Func<int> get, Action<int> set, Action changed, int min = 0, int max = int.MaxValue)
        => new(
            () => get().ToString(CultureInfo.InvariantCulture),
            s =>
            {
                if (int.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                {
                    set(Math.Clamp(v, min, max));
                    changed();
                }
            });

    public static LoyaltyDraftField Long(Func<long> get, Action<long> set, Action changed, long min = 0, long max = long.MaxValue)
        => new(
            () => get().ToString(CultureInfo.InvariantCulture),
            s =>
            {
                if (long.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v))
                {
                    set(Math.Clamp(v, min, max));
                    changed();
                }
            });

    public static LoyaltyDraftField Double(Func<double> get, Action<double> set, Action changed, double min = 0.0, double max = double.MaxValue)
        => new(
            () => get().ToString("0.####", CultureInfo.InvariantCulture),
            s =>
            {
                string raw = (s ?? string.Empty).Trim().Replace(',', '.');
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && !double.IsNaN(v) && !double.IsInfinity(v))
                {
                    set(Math.Clamp(v, min, max));
                    changed();
                }
            });

    /// <summary>Comma-separated string list (e.g. slot symbols, exclusions).</summary>
    public static LoyaltyDraftField CsvStrings(Func<List<string>> get, Action<List<string>> set, Action changed)
        => new(
            () => string.Join(", ", get()),
            s =>
            {
                var list = (s ?? string.Empty)
                    .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToList();
                set(list);
                changed();
            });

    /// <summary>Comma-separated double list (e.g. slot triple multipliers).</summary>
    public static LoyaltyDraftField CsvDoubles(Func<List<double>> get, Action<List<double>> set, Action changed)
        => new(
            () => string.Join(", ", get().Select(d => d.ToString("0.####", CultureInfo.InvariantCulture))),
            s =>
            {
                var list = new List<double>();
                foreach (var tok in (s ?? string.Empty).Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(tok.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        && !double.IsNaN(v) && !double.IsInfinity(v))
                        list.Add(v);
                }
                set(list);
                changed();
            });
}
