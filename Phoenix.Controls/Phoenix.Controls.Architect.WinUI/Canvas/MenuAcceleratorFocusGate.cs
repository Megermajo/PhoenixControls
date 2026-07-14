using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Disables a menu bar's conflicting <see cref="KeyboardAccelerator"/>s while
/// a text-input control holds keyboard focus, and re-enables them the moment
/// focus moves on.
/// <para>
/// WHY: a WinUI accelerator is window-scoped and matches regardless of which
/// control has focus. The Architect chrome advertises bare-letter chords
/// (F = frame selection, C = comment frame) and canvas Ctrl-chords
/// (Ctrl+Z/Y/F/G/N/W/Space) on its menu items — so typing those letters into a
/// value pill, rename box, inspector field or databank cell fired the canvas
/// action mid-word (user report: "hotkeys still affect the canvas when typed into a
/// pill or other text-panel"). Worse, a MATCHED accelerator marks the KeyDown
/// handled, which can cancel the character for text controls — so merely
/// suppressing the action in the Invoked handler could still eat the
/// keystroke. Setting <see cref="KeyboardAccelerator.IsEnabled"/> to false is
/// the by-construction fix: a disabled accelerator never matches, so the
/// keystroke reaches the focused text box completely untouched. The
/// per-accelerator Invoked focus gate (OnMenuAcceleratorInvoked in the chrome
/// / sibling window) stays as a backstop for the focus-transition race.
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
            _hooked = true;
        }
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
            _hooked = false;
        }
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
        bool typing = e.NewFocusedElement
            is TextBox or RichEditBox or PasswordBox or NumberBox or AutoSuggestBox;
        foreach (var a in _gated)
            a.IsEnabled = !typing;
    }
}
