namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Implemented by panels that host their own pop-out ↗ button inside the
/// header chrome. When the panel itself is hosted as a pop-out (i.e. the
/// content of a <see cref="Services.PopOutWindowFactory"/> Window), the
/// factory calls <see cref="MarkAsPopOutChild"/> so the panel hides its
/// own ↗ button.
///
/// The embedded workspace allows
/// arbitrary-depth fan-out — every ↗ click on the embedded panel spawns
/// a fresh window with its own VM. The child's own ↗ stays hidden so
/// pop-out spawning flows from the embedded panel only; that keeps the
/// PopOutStateStore restore graph rooted on the workspace rather than
/// having to track a tree of parent-pop-outs.
/// </summary>
internal interface IPopOutAware
{
    void MarkAsPopOutChild();
}
