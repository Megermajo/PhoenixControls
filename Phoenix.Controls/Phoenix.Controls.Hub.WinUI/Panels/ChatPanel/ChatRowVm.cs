using System;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.WinUI.Contracts;
using Windows.UI;

namespace Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;

// Row-level VM for the Hub Chat panel.
//
// Role coloring follows the precedence ladder:
//   Broadcaster > Mod > VIP > Sub > Regular
// The four IRC tag-bag flags travel through alongside the
// precedence-resolved Role so the resolver can branch on them directly
// and the badge geometry is always renderable for every role
// permutation. The chat body is plain text (no `!`-prefix highlighting
// — command activity surfaces in the Script panel, not in chat
// coloring).
//
// Brush properties go through INotifyPropertyChanged so
// ActualThemeChanged on the host View can re-resolve via RefreshBrushes
// without recreating every row. x:Bind on UsernameBrush / BadgeBrush
// uses Mode=OneWay in ChatView.xaml so the change actually flows.
public sealed class ChatRowVm : INotifyPropertyChanged
{
    public ChatRowVm(ChatMessage msg)
    {
        Message = msg;
        Username = msg.Username;
        Body = msg.Body;

        // Bot rows are out of the role hierarchy — they're filtered upstream
        // before reaching this VM in production, but the FakeChat seeds a Bot
        // row for design-time previews. Bot keeps a distinct ember tint and
        // a Phoenix-glyph badge so the design-time preview still reads.
        IsBot = msg.Role == ChatRole.Bot;
        IsBroadcaster = msg.IsBroadcaster;
        IsMod         = msg.IsMod;
        IsVip         = msg.IsVip;
        IsSubscriber  = msg.IsSubscriber;

        // BadgeGeometry is a fresh-per-access getter (see below); only the label
        // is fixed per row. Bot rows are out of the role hierarchy and use the
        // Phoenix glyph; everyone else resolves by precedence.
        BadgeLabel = IsBot
            ? "Bot"
            : RoleColorBrush.ResolveLabel(IsBroadcaster, IsMod, IsVip, IsSubscriber);
        _usernameBrush = ResolveUsernameBrush();
        _badgeBrush    = _usernameBrush;

        // HH:mm — wall-clock for at-a-glance scanning. Seconds would crowd
        // the row at 10pt mono; the panel already auto-scrolls so the
        // operator never relies on second-precision for ordering.
        Timestamp = msg.Timestamp.LocalDateTime.ToString("HH:mm");

        // Platform tag — one brand-colored letter ahead of the role badge so
        // multi-platform chat stays glance-sortable. Role coloring/badge are
        // untouched (standing rule: role color + role badge are the row's
        // functional encoding; the platform letter is an additive dimension).
        switch (msg.Platform)
        {
            case "youtube":
                PlatformTag = "Y";
                PlatformName = "YouTube";
                _platformBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x4E, 0x45));
                break;
            case "kick":
                PlatformTag = "K";
                PlatformName = "Kick";
                _platformBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x53, 0xFC, 0x18));
                break;
            default:
                PlatformTag = "T";
                PlatformName = "Twitch";
                _platformBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xA9, 0x70, 0xFF));
                break;
        }
    }

    public string PlatformTag { get; }
    public string PlatformName { get; }

    private readonly Brush _platformBrush;
    public Brush PlatformBrush => _platformBrush;

    public ChatMessage Message { get; }
    public string Username { get; }
    public string Body { get; }
    public string Timestamp { get; }

    public bool IsBroadcaster { get; }
    public bool IsMod { get; }
    public bool IsVip { get; }
    public bool IsSubscriber { get; }
    public bool IsBot { get; }

    private Brush _usernameBrush;
    public Brush UsernameBrush
    {
        get => _usernameBrush;
        private set
        {
            if (ReferenceEquals(_usernameBrush, value)) return;
            _usernameBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsernameBrush)));
        }
    }

    private Brush _badgeBrush;
    public Brush BadgeBrush
    {
        get => _badgeBrush;
        private set
        {
            if (ReferenceEquals(_badgeBrush, value)) return;
            _badgeBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BadgeBrush)));
        }
    }

    // FRESH geometry on every access. A Geometry is a
    // DependencyObject that can be parented to only one element; ItemsRepeater
    // recycles row containers, so a single STORED instance bound to a recycled
    // PathIcon threw ArgumentException out of PathIcon.set_Data and crashed
    // Hub.WinUI. Returning a brand-new instance per x:Bind evaluation means each
    // PathIcon owns its own geometry and it is never reparented. Re-parse is
    // cheap (cached path string, tiny glyph). Guarded at the sink by
    // ChatBadgeData.SafeData as defense-in-depth.
    public Microsoft.UI.Xaml.Media.Geometry? BadgeGeometry =>
        IsBot ? RoleColorBrush.ResolveGeometryByKey("PhoenixIcon_Phoenix")
              : RoleColorBrush.ResolveGeometry(IsBroadcaster, IsMod, IsVip, IsSubscriber);

    public string BadgeLabel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Re-resolve UsernameBrush / BadgeBrush against the current
    /// Application.Resources so a runtime theme swap reaches the row without
    /// recreating it. Caller is the panel VM, which fires this on every
    /// materialised row when ActualThemeChanged signals a swap.
    /// </summary>
    public void RefreshBrushes()
    {
        var resolved = ResolveUsernameBrush();
        UsernameBrush = resolved;
        BadgeBrush    = resolved;
    }

    private Brush ResolveUsernameBrush()
    {
        if (IsBot) return LookupBotBrush();
        return RoleColorBrush.Resolve(IsBroadcaster, IsMod, IsVip, IsSubscriber);
    }

    // Bot ember-tint cache. Resolved lazily on first miss, cached
    // statically so repeated misses don't re-walk Application.Resources.
    // InvalidateBrushCache() drops it on theme swap.
    private static Brush? s_cachedBotBrush;

    private static Brush LookupBotBrush()
    {
        if (s_cachedBotBrush is not null) return s_cachedBotBrush;
        if (Microsoft.UI.Xaml.Application.Current?.Resources is { } res
            && res.TryGetValue("EmberDeepBrush", out var resource)
            && resource is Brush b)
        {
            s_cachedBotBrush = b;
            return b;
        }
        s_cachedBotBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0x82, 0x2B));
        return s_cachedBotBrush;
    }

    /// <summary>
    /// Drop cached brushes so the next Resolve / LookupBotBrush
    /// call re-walks Application.Resources against the freshly-merged theme.
    /// Pair this with <see cref="RefreshBrushes"/> on each row when a theme
    /// swap lands (the panel VM does both in order).
    /// </summary>
    public static void InvalidateBrushCache()
    {
        s_cachedBotBrush = null;
    }
}
