using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Panels.SystemLogPanel;

// Row VMs snapshot their brushes at ctor time (LevelBrush /
// MessageBrush), which keeps the bind path trivial (x:Bind to a property
// rather than a {ThemeResource …}) but makes the row blind to runtime
// theme swaps. Hub doesn't currently support live theme switching, but
// we expose RefreshBrushes() as a hook the VM can fire on a future theme
// change so the row re-resolves both brushes against the *current*
// Application.Resources — flipping the properties through INotifyPropertyChanged
// makes x:Bind OneWay pick up the new instances without re-instantiating
// the row.
public sealed class SystemLogRowVm : INotifyPropertyChanged
{
    public SystemLogRowVm(SystemLogEntry entry)
    {
        Entry = entry;
        TimestampText = entry.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
        LevelText = LevelLabel(entry.Level);
        Source = entry.Source;
        Message = entry.Message;
        IsError = entry.Level == SystemLogLevel.Error;
        _levelBrush = BrushFor(entry.Level);
        _messageBrush = MessageBrushFor(IsError);
    }

    public SystemLogEntry Entry { get; }
    public string TimestampText { get; }
    public string LevelText { get; }
    public string Source { get; }
    public string Message { get; }
    public bool IsError { get; }
    public SystemLogLevel Level => Entry.Level;

    private Brush _levelBrush;
    public Brush LevelBrush
    {
        get => _levelBrush;
        private set
        {
            if (ReferenceEquals(_levelBrush, value)) return;
            _levelBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LevelBrush)));
        }
    }

    private Brush _messageBrush;
    public Brush MessageBrush
    {
        get => _messageBrush;
        private set
        {
            if (ReferenceEquals(_messageBrush, value)) return;
            _messageBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageBrush)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Per-row expandable exception view. The
    // SystemLogEntry already carries an Exception? on the model; the panel
    // never surfaced it. We expose a chevron glyph that flips on tap, a
    // formatted multi-line stack-trace block (including the InnerException
    // chain), and Visibility helpers so x:Bind OneWay can drive a XAML
    // Visibility on a bool. Both visibilities re-raise PropertyChanged
    // when IsExceptionExpanded toggles.
    private bool _isExceptionExpanded;
    public bool IsExceptionExpanded
    {
        get => _isExceptionExpanded;
        set
        {
            if (_isExceptionExpanded == value) return;
            _isExceptionExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExceptionExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExceptionExpandedVisibility)));
        }
    }

    /// <summary>True when the row carries a non-null Exception — the
    /// chevron's visibility binds to this so chat / non-exception rows
    /// don't render an empty toggle.</summary>
    public bool HasException => Entry.Exception is not null;

    public Visibility HasExceptionVisibility =>
        HasException ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExceptionExpandedVisibility =>
        (HasException && _isExceptionExpanded) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Down chevron when collapsed (▾), up chevron when expanded (▴) — both
    /// glyphs are basic Unicode triangles already present in the suite's
    /// existing mono font set (used by the canvas socket-direction markers
    /// per Design_Orders §5). Using glyphs rather than a path icon keeps
    /// this row purely XAML-without-icon-resource so PhoenixIcons.xaml
    /// doesn't need a new entry.
    /// </summary>
    public string ChevronGlyph => _isExceptionExpanded ? "▴" : "▾";

    /// <summary>
    /// Formatted multi-line block: top exception's type + message + stack
    /// trace, followed by each InnerException in the chain. Limited to the
    /// chain length (no max-depth cap — Exception.InnerException terminates
    /// at null, and the suite's GlobalLogger.Error wraps user exceptions
    /// once, so the depth is bounded in practice).
    /// </summary>
    public string ExceptionText
    {
        get
        {
            var ex = Entry.Exception;
            if (ex is null) return string.Empty;
            var sb = new StringBuilder(capacity: 512);
            int depth = 0;
            for (var cur = ex; cur is not null; cur = cur.InnerException, depth++)
            {
                if (depth > 0) sb.AppendLine().Append("Caused by: ");
                sb.Append(cur.GetType().FullName).Append(": ").AppendLine(cur.Message ?? "<no message>");
                if (!string.IsNullOrEmpty(cur.StackTrace))
                {
                    sb.AppendLine(cur.StackTrace);
                }
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Re-resolve LevelBrush / MessageBrush against the
    /// current Application.Resources so a runtime theme swap reaches the
    /// row without recreating it. Caller is the panel VM, which fires
    /// this on every materialised row when a theme-change signal arrives.
    /// </summary>
    public void RefreshBrushes()
    {
        LevelBrush = BrushFor(Entry.Level);
        MessageBrush = MessageBrushFor(IsError);
    }

    // Guards every static brush cache below (s_levelBrushCache,
    // s_messageBrush*, s_cachedFallback). RefreshBrushes() and FlushPending()
    // hop the dispatcher, but InvalidateBrushCache() can fire from a theme-swap
    // signal while rows are still resolving, so the read/write/Clear paths are
    // not actually serialized — Dictionary<K,V> and the ??= fields race without
    // this lock.
    private static readonly object s_brushCacheLock = new();

    // Message brush cache. Only two distinct keys (error vs
    // not), so a 2-slot pair is cheaper than a Dictionary. Same lifecycle
    // as s_levelBrushCache — cleared by InvalidateBrushCache on theme swap.
    private static Brush? s_messageBrushError;
    private static Brush? s_messageBrushNormal;

    private static Brush MessageBrushFor(bool isError)
    {
        lock (s_brushCacheLock)
        {
            if (isError)
            {
                return s_messageBrushError ??= ResolveBrush("ErrBrush");
            }
            return s_messageBrushNormal ??= ResolveBrush("CoalPrimaryTextBrush");
        }
    }

    private static string LevelLabel(SystemLogLevel l) => l switch
    {
        SystemLogLevel.Debug => "DBG",
        SystemLogLevel.Info  => "INF",
        SystemLogLevel.Warn  => "WRN",
        SystemLogLevel.Error => "ERR",
        _                    => "—",
    };

    // Per-level brush cache. RefreshBrushes() iterates the
    // entire buffered ring on theme swap (up to AppConfig.SystemLogMaxRows,
    // default 10 000) and each row used to walk Application.Resources twice
    // (LevelBrush + MessageBrush) — 20 000 dict lookups per swap. With this
    // cache the swap path costs one TryGetValue per *level* (5 keys total:
    // 4 levels + Coal{Primary,Secondary}). InvalidateBrushCache() drops the
    // cache so the next BrushFor / ResolveBrush call re-walks the freshly
    // merged theme dictionary. Read by all row VMs; written during cache
    // misses. All reads/writes/Clear go through s_brushCacheLock because
    // InvalidateBrushCache() can fire concurrently with resolution and
    // Dictionary<K,V> is not safe for concurrent read/write.
    private static readonly Dictionary<SystemLogLevel, Brush> s_levelBrushCache = new();

    private static Brush BrushFor(SystemLogLevel l)
    {
        lock (s_brushCacheLock)
        {
            if (s_levelBrushCache.TryGetValue(l, out var cached)) return cached;
            var key = l switch
            {
                SystemLogLevel.Debug => "CoalSecondaryTextBrush",
                SystemLogLevel.Info  => "InfoBrush",
                SystemLogLevel.Warn  => "WarnBrush",
                SystemLogLevel.Error => "ErrBrush",
                _                    => "CoalSecondaryTextBrush",
            };
            var brush = ResolveBrush(key);
            s_levelBrushCache[l] = brush;
            return brush;
        }
    }

    // The coal-toned fallback brush
    // replaces the bare `new SolidColorBrush(Colors.Gray)` so a missing
    // theme key surfaces in the suite palette instead of as a foreign
    // OS grey. Resolved lazily on first miss, cached statically so
    // repeated misses don't re-walk Application.Resources.
    private static Brush? s_cachedFallback;

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
        // Theme dictionary not merged yet (test rigs, design-time): a
        // last-resort Colors.Gray brush keeps the panel renderable.
        s_cachedFallback = new SolidColorBrush(Colors.Gray);
        return s_cachedFallback;
    }

    /// <summary>
    /// Drop both the per-level / message-tier brush caches
    /// and the cached fallback so the next <see cref="BrushFor"/> /
    /// <see cref="MessageBrushFor"/> / <see cref="ResolveBrush"/> call
    /// re-walks Application.Resources against the freshly-merged theme
    /// dictionary. Pair this with <see cref="RefreshBrushes"/> on each
    /// row when a theme swap lands (the panel VM's
    /// <c>RefreshBrushes()</c> does both in order).
    /// </summary>
    public static void InvalidateBrushCache()
    {
        lock (s_brushCacheLock)
        {
            s_cachedFallback = null;
            s_messageBrushError = null;
            s_messageBrushNormal = null;
            s_levelBrushCache.Clear();
        }
    }
}
