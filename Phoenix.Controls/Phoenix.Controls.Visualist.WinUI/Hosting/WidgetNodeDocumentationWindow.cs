using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Visualist.WinUI.Hosting;

/// <summary>
/// Entry point for the Visualist Widget Node Reference — the mirror of Architect's
/// <c>NodeDocumentationWindow</c>.
///
/// <para>Builds the whole widget-node catalogue from
/// <see cref="WidgetNodeReferenceData"/> (generated live from
/// <c>WidgetNodeRegistry</c> + authored prose) and hands it to
/// <see cref="DocViewerHost"/>, which Hub backs with a WebView2 window rendering
/// the bundled <c>widget-node-reference.html</c>. Passing a
/// <paramref name="nodeTitle"/> deep-links to that node's card.</para>
///
/// <para>Routed through the shared <see cref="DocViewerHost"/> seam so Visualist
/// never references WebView2 or Hub.WinUI — exactly as Architect's node reference does.</para>
/// </summary>
public static class WidgetNodeDocumentationWindow
{
    /// <summary>
    /// Open (or focus / re-anchor) the HTML Widget Node Reference. When
    /// <paramref name="nodeTitle"/> is supplied the page scrolls to and flashes
    /// that node's card.
    /// </summary>
    public static void OpenOrFocus(string? nodeTitle = null)
    {
        string injection = WidgetNodeReferenceData.BuildInjectionScript(nodeTitle);
        string? anchor = string.IsNullOrEmpty(nodeTitle)
            ? null
            : "n-" + WidgetNodeReferenceData.Slug(nodeTitle!);

        DocViewerHost.Open(new DocViewRequest(
            Page: "widget-node-reference.html",
            InjectionScript: injection,
            Anchor: anchor,
            Title: "Visualist — Widget Node Reference"));
    }
}
