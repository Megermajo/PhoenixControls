using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.WinUI.Contracts;

// Namespace deliberately avoids "Phoenix.Controls.Architect.WinUI.Windows" —
// that name would shadow the platform `Windows.*` namespace. Kept identical to
// the retired native window so the existing call sites compile unchanged.
namespace Phoenix.Controls.Architect.WinUI.Hosting;

/// <summary>
/// Entry point for the Architect Node Reference.
///
/// <para>Through 0.12.x this was a native, singleton WinUI <c>Window</c> hosting
/// a category TreeView + a hand-built detail pane. From 0.13.x it is the live
/// data source for the shared <b>HTML</b> doc viewer instead: it builds the
/// whole node catalogue from <see cref="NodeReferenceData"/> (generated from the
/// live <c>NodeRegistry</c> + authored prose + the keyboard catalogue) and hands
/// it to <see cref="DocViewerHost"/>, which Hub backs with a WebView2 window
/// rendering the bundled <c>node-reference.html</c>.</para>
///
/// <para>The class name and namespace are kept so every existing caller — F1 /
/// Help → Node Reference (chrome + sibling window) and the canvas node menu —
/// compiles and behaves the same. Passing a <paramref name="nodeTitle"/> deep
/// links to that node's card (the F1-from-selected-node flow).</para>
/// </summary>
public static class NodeDocumentationWindow
{
    /// <summary>
    /// Open (or focus / re-anchor) the HTML Node Reference. When
    /// <paramref name="nodeTitle"/> is supplied the page scrolls to and flashes
    /// that node's card.
    /// </summary>
    public static void OpenOrFocus(string? nodeTitle = null)
    {
        string injection = NodeReferenceData.BuildInjectionScript(nodeTitle);
        string? anchor = string.IsNullOrEmpty(nodeTitle)
            ? null
            : "n-" + NodeReferenceData.Slug(nodeTitle!);

        DocViewerHost.Open(new DocViewRequest(
            Page: "node-reference.html",
            InjectionScript: injection,
            Anchor: anchor,
            Title: Localizer.T("architect.window.node_reference.viewer_title", "Architect — Node Reference")));
    }
}
