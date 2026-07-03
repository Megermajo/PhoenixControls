using System.Collections.Concurrent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Services;
using Windows.UI;

// Alias avoids the ambiguity with System.IO.Path. We only use the shape
// type as the XamlReader.Load output container — see ParseGeometry.
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;

// Role-keyed brush + glyph resolver for the Hub Chat panel.
//
// The Phoenix ChatPanel is a functional surface. Username coloring is
// categorical (Broadcaster / Mod / VIP / Sub / Regular) so the streamer reads
// role at a glance; the role-prefix glyph stays alongside as the
// unambiguous identification (color is a-glance, glyph is exact).
//
// Precedence ladder (matches Twitch badge ranking):
//   Broadcaster > Mod > VIP > Sub > Regular.
//
// The resolver keys directly off the four IRC tag-bag flags so a
// future caller that has only the flags (no ChatRole enum) can still
// resolve without losing precedence semantics. Brush lookup falls
// back to a static SolidColorBrush when the theme dictionary entry
// is missing (e.g. design-time pop-out hosts that haven't merged
// PhoenixDark). Geometry strings are pulled from the Phoenix icon
// dictionary and parsed via the XamlReader trick (PathGeometry can't
// be string-constructed in WinUI 3); the mini-language STRING is
// cached per key, but every resolve produces a fresh Geometry instance
// — see s_pathDataCache.
internal static class RoleColorBrush
{
    // The canonical role palette lives in PhoenixDark.xaml as
    // ChatRole{Broadcaster,Mod,Vip,Sub,Regular}Brush. We previously kept a
    // per-role fallback palette inline here, which forked the source of
    // truth: any future palette tweak in PhoenixDark would silently drift
    // from the inline copy. The merged-dictionary lookup is guaranteed at
    // runtime (Hub.WinUI App.xaml merges PhoenixDark), so the only legitimate
    // miss is a design-time / test host that hasn't merged the theme — for
    // that case a single neutral SolidColorBrush is enough.
    //
    // Allocated once at class load so raid-burst chat-row construction
    // doesn't allocate per-message (mirrors ChatRowVm's pre-existing pattern
    // of caching the fallback brush rather than newing one per call).
    private static readonly Brush s_neutralFallback =
        new SolidColorBrush(Color.FromArgb(0xFF, 0x9C, 0x8A, 0x72));

    // Caches the path mini-language STRING (not the parsed Geometry). A
    // Geometry is a DependencyObject — a single instance can only be parented
    // to one PathIcon at a time. Sharing the parsed instance across chat rows
    // caused ItemsRepeater's second materialised row to throw
    // ArgumentException "Value does not fall within the expected range" out of
    // PathIcon.set_Data, which escaped to App.UnhandledException and crashed
    // Hub.WinUI on the next incoming chat message (see SystemHistory ids
    // 1276672 / 1276722 on 2026-05-22). Caching only the string keeps the
    // Application.Resources lookup cheap; XamlReader.Load runs once per row,
    // producing an instance the row owns outright.
    private static readonly ConcurrentDictionary<string, string?> s_pathDataCache = new();

    public static Brush Resolve(bool isBroadcaster, bool isMod, bool isVip, bool isSubscriber)
    {
        string key = ResolveKey(isBroadcaster, isMod, isVip, isSubscriber);
        return Lookup(key);
    }

    public static Geometry? ResolveGeometry(bool isBroadcaster, bool isMod, bool isVip, bool isSubscriber)
    {
        string key = ResolveGeometryKey(isBroadcaster, isMod, isVip, isSubscriber);
        return ResolveGeometryByKey(key);
    }

    public static Geometry? ResolveGeometryByKey(string resourceKey)
    {
        string? mini = s_pathDataCache.GetOrAdd(resourceKey, LookupPathData);
        if (mini is null) return null;
        return ParseGeometry(resourceKey, mini);
    }

    private static string? LookupPathData(string resourceKey)
    {
        if (Application.Current?.Resources is not { } res) return null;
        if (!res.TryGetValue(resourceKey, out var resource)) return null;
        return resource as string;
    }

    // PathIcon geometry key for the role-prefix badge. The resolver always
    // returns a non-null key — no role permutation collapses to "no badge"
    // per the brief's "every role permutation must render correctly".
    public static string ResolveGeometryKey(bool isBroadcaster, bool isMod, bool isVip, bool isSubscriber)
    {
        if (isBroadcaster) return "PhoenixIcon_ChatRoleBroadcaster";
        if (isMod)         return "PhoenixIcon_ChatRoleMod";
        if (isVip)         return "PhoenixIcon_ChatRoleVip";
        if (isSubscriber)  return "PhoenixIcon_ChatRoleSub";
        return "PhoenixIcon_ChatRoleRegular";
    }

    // Tooltip text — short, descriptive, used by AutomationProperties +
    // ToolTipService on the glyph. Always returns a non-empty label.
    public static string ResolveLabel(bool isBroadcaster, bool isMod, bool isVip, bool isSubscriber)
    {
        if (isBroadcaster) return "Broadcaster";
        if (isMod)         return "Moderator";
        if (isVip)         return "VIP";
        if (isSubscriber)  return "Subscriber";
        return "Viewer";
    }

    private static string ResolveKey(bool isBroadcaster, bool isMod, bool isVip, bool isSubscriber)
    {
        if (isBroadcaster) return "ChatRoleBroadcasterBrush";
        if (isMod)         return "ChatRoleModBrush";
        if (isVip)         return "ChatRoleVipBrush";
        if (isSubscriber)  return "ChatRoleSubBrush";
        return                    "ChatRoleRegularBrush";
    }

    private static Brush Lookup(string key)
    {
        if (Application.Current?.Resources is { } res
            && res.TryGetValue(key, out var resource)
            && resource is Brush b) return b;
        // Should only ever fire in design-time / test hosts that haven't
        // merged PhoenixDark.xaml — Hub.WinUI's App.xaml merges it on
        // startup so the role tokens always resolve in the shipping app.
        return s_neutralFallback;
    }

    private static Geometry? ParseGeometry(string resourceKey, string mini)
    {
        try
        {
            // PathIcon.Data / Path.Data both apply WinUI's Geometry TypeConverter
            // when the string sits in a resource referenced by StaticResource. For
            // x:Bind we need an actual Geometry instance, so round-trip the mini-
            // language string through XamlReader.Load — the same parser the
            // TypeConverter uses internally. A fresh Geometry per call so each
            // PathIcon owns its own DependencyObject (see s_pathDataCache).
            string xaml = "<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\""
                          + mini.Replace("\"", "&quot;")
                          + "\" />";
            if (XamlReader.Load(xaml) is not XamlPath path) return null;

            // Detach the Geometry from the temp Path before returning. Without
            // this clear, the Geometry's InheritanceContext still points to the
            // temp Path RCW (which goes out of managed scope but the WinRT
            // object may outlive the call) — so when PathIcon.set_Data later
            // tries to take ownership, the framework rejects the assignment
            // with ArgumentException "Value does not fall within the expected
            // range." (SystemHistory ids 1276672 / 1276722 / 1276835 on
            // 2026-05-22 — the per-row fix stopped Geometry
            // *sharing* across rows but didn't address the lingering parent
            // from the parsing wrapper, so the crash kept reproducing on the
            // first ItemsRepeater materialisation pass after each chat message.)
            var geom = path.Data;
            path.Data = null;
            return geom;
        }
        catch (System.Exception ex)
        {
            GlobalLogger.Error("RoleColorBrush", $"failed to parse geometry '{resourceKey}'", ex);
            return null;
        }
    }
}
