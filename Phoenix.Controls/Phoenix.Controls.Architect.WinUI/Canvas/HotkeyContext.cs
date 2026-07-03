namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Architect canvas interaction modes that change which chords are most
/// useful to surface in the bottom-left hotkey cheatsheet overlay. The
/// canvas pushes the active context into <c>HotkeyCheatsheet.SetContext</c>
/// as selection / drag / panning / inline-edit state transitions.
/// </summary>
/// <remarks>
/// Granularity matches the user-facing "what can I do right now" question
/// without exploding into per-chord branches:
/// <list type="bullet">
///   <item><see cref="Canvas"/> — idle, nothing selected.</item>
///   <item><see cref="NodeSelected"/> — exactly one node selected.</item>
///   <item><see cref="MultiSelection"/> — 2+ items (nodes / wires / frames).</item>
///   <item><see cref="WireSelected"/> — single wire selected (Del, Esc).</item>
///   <item><see cref="FrameSelected"/> — single comment frame (F2 rename).</item>
///   <item><see cref="DraggingWire"/> — wire-drop in flight.</item>
///   <item><see cref="Panning"/> — middle-mouse pan in flight.</item>
///   <item><see cref="TextEditing"/> — inline editor has focus; suppress canvas chords.</item>
/// </list>
/// Per the per-pillar chrome rule the
/// Visualist analogue is intentionally a separate enum even though it
/// shares the same shape — the two pillars never share canvas paint code.
/// </remarks>
public enum HotkeyContext
{
    Canvas,
    NodeSelected,
    MultiSelection,
    WireSelected,
    FrameSelected,
    DraggingWire,
    Panning,
    TextEditing,
}
