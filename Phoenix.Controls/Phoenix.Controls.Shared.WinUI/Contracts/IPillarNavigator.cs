namespace Phoenix.Controls.Shared.WinUI.Contracts;

// Single-HUB collapse (TODO.md P0 #3) — Architect / Visualist are now
// embedded UserControls inside Hub.MainWindow's MainPaneRegion, not
// sibling exes. Cross-cutting code that used to spawn a sibling pillar
// (e.g. ScriptHostMonitor.OpenInArchitectAsync) now asks Hub to swap
// the active tab via this navigator. Hub.MainWindow implements it.
//
// openTarget is an optional path the destination pillar should open on
// activation — for Architect that's a `.phxg` graph file, for Visualist
// it's a `.phxlayer`. Implementations are free to ignore the hint when
// the destination doesn't yet support deep-link open.
public interface IPillarNavigator
{
    void NavigateTo(PillarKind kind, string? openTarget = null);
}
