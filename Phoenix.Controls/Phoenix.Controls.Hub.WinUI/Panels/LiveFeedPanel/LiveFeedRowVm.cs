using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.LiveFeedPanel;

// Lightweight per-row VM. Pre-computes display fields so x:Bind is one-shot
// (DataTemplates in WinUI 3 ItemsRepeater don't support DataTemplate fallbacks
// off complex value-converter chains as cleanly as WPF — flatten upfront).
//
//  IconBrush goes through INotifyPropertyChanged so a runtime theme
// swap reaches the row via the panel VM's RefreshBrushes() hook. x:Bind on
// IconBrush uses Mode=OneWay in LiveFeedView.xaml so the change actually
// flows.
public sealed class LiveFeedRowVm : INotifyPropertyChanged
{
    public LiveFeedRowVm(LiveFeedEntry entry, bool isError = false)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        Who = entry.Who;
        Detail = entry.Detail;
        IsError = isError;
        // B3 (audit 2026-05-24) — Errors render with a distinct glyph + a
        // suite "error" brush. We don't add a new LiveFeedKind enum value
        // (Records.cs is out of this sprint's scope); instead the IsError
        // flag rides alongside the existing Kind so the renderer can pick
        // a different icon/colour combo without touching the enum.
        Icon = isError ? "!" : IconFor(entry.Kind);
        _iconBrush = isError ? ResolveBrush("ErrBrush") : BrushFor(entry.Kind);
    }

    public LiveFeedEntry Entry { get; }
    public string TimestampText { get; }
    public string Who { get; }
    public string Detail { get; }
    public string Icon { get; }
    // B3 (audit 2026-05-24) — true when this row was sourced from a
    // GlobalLogger CriticalError entry rather than a stream event /
    // visual trigger. Powers the panel's "Errors" filter chip + the
    // ErrBrush icon tint above.
    public bool IsError { get; }

    private Brush _iconBrush;
    public Brush IconBrush
    {
        get => _iconBrush;
        private set
        {
            if (ReferenceEquals(_iconBrush, value)) return;
            _iconBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBrush)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///  Re-resolve IconBrush against the current Application.Resources
    /// so a runtime theme swap reaches the row without recreating it. Caller
    /// is the panel VM, which fires this on every materialised row when
    /// ActualThemeChanged signals a swap.
    /// </summary>
    public void RefreshBrushes()
    {
        IconBrush = IsError ? ResolveBrush("ErrBrush") : BrushFor(Entry.Kind);
    }

    private static string IconFor(LiveFeedKind k) => k switch
    {
        LiveFeedKind.Sub    => "✦",  // ✦
        LiveFeedKind.Chat   => "☆",  // ☆
        LiveFeedKind.Redeem => "◆",  // ◆
        LiveFeedKind.Raid   => "⚑",  // ⚑
        LiveFeedKind.Visual => "▰",  // ▰
        LiveFeedKind.Follow => "+",
        _                   => "·",
    };

    //  Per-kind brush cache. RefreshBrushes() iterates the entire
    // buffered ring on theme swap and each row used to walk Application.Resources
    // once per call. With the cache the swap path costs one TryGetValue per
    // *kind* (7 keys total). InvalidateBrushCache() drops the cache so the
    // next BrushFor / ResolveBrush call re-walks the freshly merged dictionary.
    private static readonly Dictionary<LiveFeedKind, Brush> s_kindBrushCache = new();

    // Perf-review H9 sibling: static fallback brush singleton — the previous
    // `new SolidColorBrush(Colors.Gray)` allocated per row on theme-lookup
    // miss, defeating WinUI's brush-sharing fast path. HUB-UX P2 — resolved
    // lazily from the CoalFallbackBrush token so a theme-key miss surfaces
    // in suite chrome instead of foreign OS grey.
    private static Brush? s_cachedFallback;

    private static Brush BrushFor(LiveFeedKind k)
    {
        if (s_kindBrushCache.TryGetValue(k, out var cached)) return cached;
        var key = k switch
        {
            LiveFeedKind.Sub    => "EmberPrimaryBrush",
            LiveFeedKind.Chat   => "CoalBodyTextBrush",
            LiveFeedKind.Redeem => "InfoBrush",
            LiveFeedKind.Raid   => "EmberDeepBrush",
            LiveFeedKind.Visual => "CoalSecondaryTextBrush",
            LiveFeedKind.Follow => "OkBrush",
            _                   => "CoalSecondaryTextBrush",
        };
        var brush = ResolveBrush(key);
        s_kindBrushCache[k] = brush;
        return brush;
    }

    private static Brush ResolveBrush(string key)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush b) return b;
        return ResolveFallback();
    }

    private static Brush ResolveFallback()
    {
        if (s_cachedFallback is not null) return s_cachedFallback;
        if (Application.Current?.Resources is { } res
            && res.TryGetValue("CoalFallbackBrush", out var resource)
            && resource is Brush b)
        {
            s_cachedFallback = b;
            return b;
        }
        s_cachedFallback = new SolidColorBrush(Colors.Gray);
        return s_cachedFallback;
    }

    /// <summary>
    ///  Drop the per-kind / fallback caches so the next BrushFor /
    /// ResolveBrush call re-walks Application.Resources against the freshly-
    /// merged theme dictionary. Pair with <see cref="RefreshBrushes"/> on
    /// each row when a theme swap lands (the panel VM does both in order).
    /// </summary>
    public static void InvalidateBrushCache()
    {
        s_cachedFallback = null;
        s_kindBrushCache.Clear();
    }
}
