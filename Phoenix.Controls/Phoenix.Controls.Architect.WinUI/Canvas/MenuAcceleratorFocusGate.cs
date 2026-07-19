using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Disables a menu bar's conflicting <see cref="KeyboardAccelerator"/>s while a
/// text-input control holds keyboard focus OR an Architect inline editor is
/// open (<see cref="InlineEditGate"/>), and re-enables them once both clear.
/// <para>
/// WHY: a WinUI accelerator is window-scoped and matches regardless of which
/// control has focus. The Architect chrome advertises bare-letter chords
/// (F = frame selection, C = comment frame) and canvas Ctrl-chords
/// (Ctrl+Z/Y/F/G/N/W/Space) on its menu items — so typing those letters into a
/// value pill, rename box, inspector field or databank cell fired the canvas
/// action mid-word (Majo: "hotkeys still affect the canvas when typed into a
/// pill or other text-panel"). Worse, a MATCHED accelerator marks the KeyDown
/// handled, which can cancel the character for text controls — so merely
/// suppressing the action in the Invoked handler could still eat the
/// keystroke. Setting <see cref="KeyboardAccelerator.IsEnabled"/> to false is
/// the by-construction fix: a disabled accelerator never matches, so the
/// keystroke reaches the focused text box completely untouched. The
/// per-accelerator Invoked focus gate (OnMenuAcceleratorInvoked in the chrome
/// / sibling window) stays as a backstop for the focus-transition race.
/// Additionally, under Architect's XAML-Islands hosting FocusManager does not
/// reliably report the canvas value-pill's TextBox as focused, so this gate
/// ALSO closes on the focus-independent <see cref="InlineEditGate"/> signal
/// (set the instant an inline edit begins) — otherwise bare F / Ctrl+Z / … kept
/// firing while the user was still typing in a pill (Majo report).
/// </para>
/// <para>
/// Ctrl+S / Ctrl+Shift+S / Ctrl+O stay enabled while typing — parity with the
/// canvas keyboard guard (<c>LogicCanvasView.OnHostKeyDown</c>), which lets
/// exactly the save/open chords through while an inline editor is active.
/// </para>
/// </summary>
internal sealed class MenuAcceleratorFocusGate
{
    private static readonly HashSet<(VirtualKey Key, VirtualKeyModifiers Mods)> s_allowedWhileTyping = new()
    {
        (VirtualKey.S, VirtualKeyModifiers.Control),
        (VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift),
        (VirtualKey.O, VirtualKeyModifiers.Control),
    };

    private readonly List<KeyboardAccelerator> _gated = new();
    private bool _hooked;
    // Last focus verdict from OnGotFocus. Combined with InlineEditGate.IsActive
    // (the focus-independent "an inline editor is open" signal) so the gate
    // stays closed even when FocusManager fails to report the canvas pill's
    // TextBox as focused under Architect's XAML-Islands hosting. See InlineEditGate.
    private bool _typingFocus;

    /// <summary>
    /// Collect the gated accelerators from <paramref name="menuBar"/> (all of
    /// them except the save/open allow-list, sub-menus included) and start
    /// tracking focus. Idempotent — safe to call again after a re-attach; the
    /// accelerator list is rebuilt from the live menu each time.
    /// </summary>
    internal void Attach(MenuBar? menuBar)
    {
        if (menuBar is null) return;
        _gated.Clear();
        foreach (var top in menuBar.Items)
            Collect(top.Items);
        if (!_hooked)
        {
            FocusManager.GotFocus += OnGotFocus;
            InlineEditGate.Changed += OnInlineEditChanged;
            _hooked = true;
        }
        // Freshly-collected accelerators must reflect the current gate state —
        // an inline edit may already be open across a re-attach (pillar-tab
        // switch), and a stale IsEnabled would let the chord through.
        ReevaluateEnabled();
    }

    /// <summary>
    /// Stop tracking focus and re-enable everything, so a detached menu can
    /// never be left with its chords stuck off. Call from Unloaded / Closed —
    /// the FocusManager event is static and would otherwise keep the owner
    /// alive.
    /// </summary>
    internal void Detach()
    {
        if (_hooked)
        {
            FocusManager.GotFocus -= OnGotFocus;
            InlineEditGate.Changed -= OnInlineEditChanged;
            _hooked = false;
        }
        _typingFocus = false;
        foreach (var a in _gated) a.IsEnabled = true;
        _gated.Clear();
    }

    private void Collect(IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            if (item is MenuFlyoutSubItem sub)
            {
                Collect(sub.Items);
                continue;
            }
            foreach (var accel in item.KeyboardAccelerators)
                if (!s_allowedWhileTyping.Contains((accel.Key, accel.Modifiers)))
                    _gated.Add(accel);
        }
    }

    private void OnGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        // AutoSuggestBox surfaces its inner TextBox as the focused element, so
        // the TextBox arm covers it too.
        _typingFocus = e.NewFocusedElement
            is TextBox or RichEditBox or PasswordBox or NumberBox or AutoSuggestBox;
        ReevaluateEnabled();
    }

    // Re-evaluated on inline-edit begin/end — the moment FocusManager's (here
    // unreliable) focus signal would otherwise miss a canvas pill edit.
    private void OnInlineEditChanged(object? sender, System.EventArgs e) => ReevaluateEnabled();

    // A gated accelerator is disabled while EITHER a text input holds focus OR
    // an Architect inline editor is open. A disabled accelerator never matches,
    // so the keystroke reaches the field untouched instead of firing the chord.
    private void ReevaluateEnabled()
    {
        bool disable = _typingFocus || InlineEditGate.IsActive;
        foreach (var a in _gated)
            a.IsEnabled = !disable;
    }
}
