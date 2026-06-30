using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

/// <summary>
/// Resolves theme brushes / fonts from the running app's resources IN CODE, for
/// the Architect ContentDialogs that are constructed by `new` (rail CRUD, menus)
/// while detached from any visual tree.
///
/// <para><b>Why this is necessary.</b> These dialogs live in a library assembly
/// (Architect.WinUI) and are instantiated before they are shown. ANY resource
/// markup that resolves at load time on a directly-loaded element — a root
/// attribute or a non-template child, with EITHER <c>{StaticResource}</c> OR
/// <c>{ThemeResource}</c> — is evaluated during <c>InitializeComponent</c>
/// (<c>Application.LoadComponent</c>), and a disconnected library dialog cannot
/// reach the host App.Resources at that moment, so it throws
/// <c>XamlParseException</c>. (Merging PhoenixDark into the dialog's own
/// Resources throws too — its Button/MenuFlyout styles reference
/// system/XamlControlsResources keys that are out of scope for a disconnected
/// dialog.) This was proven across release cycles 1.0.2–1.0.5: the merge form,
/// the {StaticResource} form, AND the {ThemeResource} form all crashed
/// LeftRail.AddButton, which logged + swallowed the throw → rail
/// "+"/rename/delete silently dead.</para>
///
/// <para><b>The reliable path.</b> Keep the dialog XAML free of directly-resolved
/// resource markup and apply the theme here, from
/// <see cref="Application"/>.Current.Resources — a runtime API that needs no tree
/// attachment and is fully populated by the time a dialog is constructed.
/// Resource refs INSIDE a DataTemplate are fine to leave in XAML: template
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
