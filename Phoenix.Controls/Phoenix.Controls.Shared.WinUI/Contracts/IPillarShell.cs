using System.Threading.Tasks;

namespace Phoenix.Controls.Shared.WinUI.Contracts;

// Lightweight shell-level abstraction. Each pillar (Hub / Architect /
// Visualist) exposes one of these so cross-cutting concerns
// (chrome wiring, launcher bookkeeping) can talk in pillar-neutral terms.
// The pillars do NOT share paint code — chrome XAML lives per-pillar
// per feedback_visualist_architect_chrome_independence.md. This contract
// is data-only.
//
//  CanLaunchOtherPillars was removed in R5 — the pre-T15 launcher
// chrome that gated cross-pillar Open from this flag is gone (Architect and
// Visualist are sibling libraries hosted inside Hub.WinUI now, not separate
// exes the user launches sibling-style). Re-add when a new cross-pillar
// launch surface materialises.
public interface IPillarShell
{
    string     Title { get; } // e.g. "Phoenix Controls — Architect"
    PillarKind Kind  { get; } // Hub, Architect, Visualist
}

/// <summary>
/// Identifies which of the three pillars a shell instance represents.
/// </summary>
/// <remarks>
///  Ordinals are <b>load-bearing</b>. Hub=0 is the implicit
/// default for any caller that compares <see cref="PillarKind"/> as
/// <c>int</c> (e.g. cross-pillar focus ordering, settings keys persisted
/// as integers, or any future bit-flag aggregation). Explicit ordinals
/// lock the contract so an alphabetical re-sort of the enum members
/// can't silently shift the values. Add new pillars at the END of the
/// list with the next free ordinal — never insert between existing
/// members.
/// </remarks>
public enum PillarKind
{
    Hub        = 0,
    Architect  = 1,
    Visualist  = 2
}

// Per-pillar save-prompt contract. Implemented by the embedded MainView
// of each editor pillar so MainWindow.AppWindow.Closing can ask "is there
// unsaved work? do you want to save / discard / cancel?" without coupling
// itself to either pillar's VM internals (TODO 2026-05-07 round 2 P0 #1).
//
// Default implementations make this opt-in: a pillar that hasn't wired
// dirty tracking yet (Architect today) exposes IsDirty=false +
// PromptSaveBeforeCloseAsync returning true (proceed). A pillar that
// has dirty tracking (Visualist) overrides both.
public interface IPillarShellHost
{
    /// <summary>True when the pillar's working document has unsaved edits.</summary>
    bool IsDirty { get; }

    /// <summary>
    /// File name (basename) of the pillar's working document, or "(unsaved)"
    /// when the pillar has no loaded file yet. Returned without the dirty
    /// marker — Hub's chrome composes the bullet/asterisk prefix based on
    /// <see cref="IsDirty"/> separately. Used by MainWindow to push the
    /// active pillar's filename into HubChrome.FileName when the user
    /// switches pillars or saves a file (TODO 2026-05-07 round 2 P2 — the
    /// filename chip used to stay empty for both pillars).
    /// </summary>
    string CurrentDocumentDisplayName { get; }

    /// <summary>
    /// Fires whenever <see cref="IsDirty"/> or <see cref="CurrentDocumentDisplayName"/>
    /// changes. MainWindow subscribes per-pillar so the chrome filename chip
    /// stays in sync with edits / loads / saves without per-pillar knowledge
    /// of which VM property changed.
    /// </summary>
    event System.EventHandler? ShellStateChanged;

    /// <summary>
    /// Show a save / discard / cancel prompt for the pillar's working document.
    /// Returns true to proceed with close, false if the user chose to cancel.
    /// Implementations should no-op (return true) when <see cref="IsDirty"/>
    /// is false so the caller doesn't have to gate on the flag.
    ///
    /// Implementations resolve their own XamlRoot — the contract stays
    /// WinUI-SDK-free so it can live in Phoenix.Controls.Shared.WinUI
    /// (which deliberately doesn't reference Microsoft.WindowsAppSDK).
    /// </summary>
    Task<bool> PromptSaveBeforeCloseAsync();
}

/// <summary>
/// Optional pillar contract surfaced for HubChrome's Edit-menu enable/disable
/// hook. A pillar that owns an undo/redo stack implements this so the chrome
/// can query the current stacks each time the Edit menu pops AND respond to
/// push notifications when the stacks change — instead of rendering Undo /
/// Redo always-enabled and silently no-opping.
///
/// Architect pulls these from its <c>UndoRedoController</c>; Visualist
/// proxies its <c>LayerDocument</c> stacks. Implementations should treat
/// "no document open" as both flags false (e.g. Architect with no graph
/// loaded — same state as undo depth 0).
/// </summary>
public interface ICanExecuteUndoRedo
{
    /// <summary>True when the pillar has at least one undoable edit on its stack.</summary>
    bool CanUndo { get; }

    /// <summary>True when the pillar has at least one redoable edit on its stack.</summary>
    bool CanRedo { get; }

    /// <summary>
    /// Fires when <see cref="CanUndo"/> / <see cref="CanRedo"/> may have
    /// changed. HubChrome subscribes after the active pillar swaps so the
    /// Edit-menu Undo/Redo IsEnabled stays in sync without the chrome having
    /// to poll. May be raised on a non-UI thread (e.g. during the initial
    /// graph load) — subscribers must marshal to the UI thread before
    /// touching XAML state.
    /// </summary>
    event System.EventHandler? CanExecuteChanged;
}
