using System;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Shared.WinUI.Contracts;

/// <summary>
/// A request to show one of the bundled in-app HTML documentation pages in the
/// shared <c>DocViewerWindow</c> (a WebView2 host that lives in Hub.WinUI).
/// </summary>
/// <param name="Page">
/// The bundled page file name, relative to the Docs asset root — e.g.
/// <c>"node-reference.html"</c>, <c>"changelog.html"</c>, <c>"readme.html"</c>.
/// </param>
/// <param name="InjectionScript">
/// Optional JavaScript executed before the page's own scripts run (via
/// <c>CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync</c>). The Node
/// Reference page is data-driven: Architect builds a <c>window.PHX = {…}</c>
/// catalogue from the live NodeRegistry and passes it here. Static pages
/// (README / Changelog) leave this null.
/// </param>
/// <param name="Anchor">
/// Optional element id to scroll to once the page is rendered — used by the
/// F1-from-selected-node deep link (<c>n-&lt;node-slug&gt;</c>). When the viewer
/// is already showing the same page it re-navigates in place via
/// <c>window.phxNavigate(anchor)</c> instead of reloading.
/// </param>
/// <param name="Title">Optional window title; defaults to a generic doc title.</param>
public sealed record DocViewRequest(
    string Page,
    string? InjectionScript = null,
    string? Anchor = null,
    string? Title = null);

/// <summary>
/// Cross-pillar seam for the in-app HTML documentation viewer.
///
/// <para>The viewer itself (a WebView2 host + the bundled Docs/ assets) lives in
/// <c>Hub.WinUI</c> — the single user-facing exe and the only project that
/// already depends on WebView2. The design-time pillars (Architect / Visualist)
/// must be able to open it without taking a reference on Hub.WinUI or on
/// WebView2, so they call through this delegate instead. Hub registers the
/// concrete opener at startup (<c>MainWindow</c> ctor sets
/// <see cref="Opener"/>).</para>
///
/// <para>This keeps the per-pillar chrome-independence rule intact: Shared.WinUI
/// carries only the contract (a record + a delegate), never a WinUI control or
/// any WebView2 type — it stays a plain net8.0-windows library with no
/// WindowsAppSDK dependency, exactly as the other contracts here do.</para>
/// </summary>
public static class DocViewerHost
{
    /// <summary>
    /// The concrete open handler, wired by Hub at startup. Null until Hub's
    /// shell is constructed (e.g. in a unit test, or if a pillar somehow runs
    /// stand-alone) — <see cref="Open"/> degrades to a logged no-op then.
    /// </summary>
    public static Action<DocViewRequest>? Opener { get; set; }

    /// <summary>
    /// Show (or re-navigate) the documentation viewer. Best-effort: a missing
    /// opener or a throwing handler is logged, never thrown back at the caller —
    /// opening help must never crash the editing flow.
    /// </summary>
    public static void Open(DocViewRequest request)
    {
        if (request is null) return;

        var opener = Opener;
        if (opener is null)
        {
            GlobalLogger.Log(
                $"DocViewerHost.Open('{request.Page}') called before Hub registered an opener — ignored.",
                "DocViewerHost", LogLevel.Debug);
            return;
        }

        try
        {
            opener(request);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("DocViewerHost", $"Open('{request.Page}')", ex);
        }
    }
}
