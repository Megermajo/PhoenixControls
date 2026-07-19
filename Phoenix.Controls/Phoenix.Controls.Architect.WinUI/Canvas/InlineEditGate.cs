using System;
using System.Threading;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Process-wide gate that is "active" while ANY Architect inline editor is open
/// on a node body — a value pill, a socket-label rename, a middle-attribute
/// pill, or a node-title rename. Driven by the <c>IsEditing</c> /
/// <c>IsRenaming</c> / <c>IsTitleRenaming</c> setters on the canvas view-models,
/// so it flips the instant an edit begins (synchronously, in the click/tap
/// handler) and stays true for the WHOLE session — never depending on keyboard
/// focus.
/// <para>
/// WHY: the window-scoped menu <see cref="Microsoft.UI.Xaml.Input.KeyboardAccelerator"/>s
/// (bare F = Frame Selection, Ctrl+Z/Y/F/G, F1/F2/F4/F5, …) match regardless of
/// which control holds keyboard focus. The 1.0.31 fix
/// (<see cref="MenuAcceleratorFocusGate"/> + the per-accelerator
/// <c>OnMenuAcceleratorInvoked</c> backstop) disabled them while a text input
/// holds focus — but under Architect's XAML-Islands-hosted-by-Hub setup,
/// <see cref="Microsoft.UI.Xaml.Input.FocusManager"/> does not reliably report
/// the canvas pill's <c>TextBox</c> as the focused element, so the accelerators
/// still fired MID-TYPING and jumped the camera (Majo: text pills "casually
/// respond" to hotkeys even while typing, whereas the flyout search box — whose
/// focus IS reported — never does). This gate is focus-INDEPENDENT: it mirrors
/// the canvas keyboard guard's own <c>IsAnyInlineEditorActive()</c> (which
/// already reads the VM edit flags, not focus), giving the menu accelerators the
/// same reliable signal. A DISABLED accelerator never matches, so the keystroke
/// reaches the pill untouched.
/// </para>
/// <para>
/// Process-static, idiomatic here (see <see cref="SocketRenderState"/> and
/// NodeView's static pin-selection state): accelerators are per-window but an
/// inline edit is only ever active in the focused window, so a shared gate is
/// safe — disabling a background window's accelerators while you type in the
/// foreground one is harmless. Enter/Exit are balanced through the setter
/// transitions; <see cref="Reset"/> (called when a canvas clears its node views
/// on graph reload) recovers the count if a view-model is discarded mid-edit.
/// </para>
/// </summary>
internal static class InlineEditGate
{
    private static int _active;

    /// <summary>True while at least one inline editor is open anywhere in the process.</summary>
    internal static bool IsActive => Volatile.Read(ref _active) > 0;

    /// <summary>
    /// Raised whenever <see cref="IsActive"/> transitions (0↔1). Consumed by
    /// <see cref="MenuAcceleratorFocusGate"/> to re-evaluate accelerator
    /// enablement at edit begin/end — the moment the (unreliable) focus signal
    /// would otherwise miss.
    /// </summary>
    internal static event EventHandler? Changed;

    /// <summary>
    /// An inline editor became visible. Call from the true-transition of an
    /// <c>IsEditing</c> / <c>IsRenaming</c> / <c>IsTitleRenaming</c> setter.
    /// </summary>
    internal static void Enter()
    {
        if (Interlocked.Increment(ref _active) == 1) RaiseChanged();
    }

    /// <summary>
    /// An inline editor closed. Call from the false-transition of the same
    /// setters. Clamped at zero so a stray Exit (e.g. a setter firing false
    /// after <see cref="Reset"/> already zeroed the count) can't drive it
    /// negative and wedge the gate permanently on.
    /// </summary>
    internal static void Exit()
    {
        int v = Interlocked.Decrement(ref _active);
        if (v < 0)
        {
            Interlocked.Exchange(ref _active, 0);
            return; // already inactive — no 1→0 transition to announce
        }
        if (v == 0) RaiseChanged();
    }

    /// <summary>
    /// Force the gate inactive — called when a canvas clears its node views
    /// (graph reload / DataContext rebind), which by construction closes every
    /// inline editor (they live inside NodeViews). Prevents a view-model
    /// discarded mid-edit from leaving the count stuck above zero. Self-heals:
    /// a later Exit from that discarded VM lands on the clamped-at-zero path.
    /// </summary>
    internal static void Reset()
    {
        if (Interlocked.Exchange(ref _active, 0) != 0) RaiseChanged();
    }

    private static void RaiseChanged()
        => SafeEvent.Raise(Changed, null, EventArgs.Empty, "InlineEditGate", "Changed");
}
