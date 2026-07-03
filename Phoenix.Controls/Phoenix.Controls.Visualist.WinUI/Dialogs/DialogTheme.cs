using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Visualist.WinUI.Dialogs;

/// <summary>
/// Resolves theme brushes / fonts from the running app's resources IN CODE, for
/// the Visualist ContentDialogs that are constructed by `new` while detached
/// from any visual tree.
///
/// <para><b>Why this is necessary.</b> These dialogs live in a library assembly
/// (Visualist.WinUI) and are instantiated before they are shown. ANY resource
/// markup that resolves at load time on a directly-loaded element — a root
/// attribute, a non-template child, or a keyed-Style setter, with EITHER
/// <c>{StaticResource}</c> OR <c>{ThemeResource}</c> — is evaluated during
/// <c>InitializeComponent</c> (<c>Application.LoadComponent</c>), and a
/// disconnected library dialog cannot reach the host App.Resources at that
/// moment, so it throws <c>XamlParseException</c>. This is the same defect that
/// burned the Architect rail dialogs across release cycles 1.0.2–1.0.5; the
/// pillar-local copy here mirrors Architect's <c>DialogTheme</c> because each
/// pillar owns its own chrome helper.</para>
///
/// <para><b>The reliable path.</b> Keep the dialog XAML free of directly-resolved
/// resource markup and apply the theme here, from
/// <see cref="Application"/>.Current.Resources — a runtime API that needs no tree
/// attachment. Resource refs INSIDE a DataTemplate are fine to leave: template
/// content is deferred until the item is realized in the tree, so it never
/// resolves at <c>InitializeComponent</c>.</para>
/// </summary>
internal static class DialogTheme
{
    /// <summary>The app-resource brush for <paramref name="key"/>, or null if absent.</summary>
    public static Brush? Brush(string key)
    {
        try
        {
            // Indexer (not TryGetValue) — only the indexer searches a
            // ResourceDictionary's MergedDictionaries, and the app theme is a
            // merged PhoenixDark. Wrapped because the indexer can throw on a
            // missing key.
            if (Application.Current?.Resources[key] is Brush b) return b;
        }
        catch { /* key absent — caller keeps the framework default */ }
        return null;
    }

    /// <summary>The app-resource font for <paramref name="key"/> (FontFamily or font-string), or null.</summary>
    public static FontFamily? Font(string key)
    {
        try
        {
            var v = Application.Current?.Resources[key];
            if (v is FontFamily f) return f;
            if (v is string s) return new FontFamily(s);
        }
        catch { /* key absent */ }
        return null;
    }
}
