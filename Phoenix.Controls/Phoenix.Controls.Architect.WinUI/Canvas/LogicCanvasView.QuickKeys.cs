using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Architect.Core;
using Windows.Foundation;
using Windows.System;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Quick-key spawn — UE-Blueprints idiom mirrored from
// Canvas.Keyboard.cs's _quickKeyMap. Hold a letter, click empty
// canvas, get a node pre-typed for the held key. The hold state lives on
// the canvas (not on a captured pointer) so the user can press the key
// before the cursor enters the canvas region.
//
// Same map as the WinForms canvas (kept in lockstep — the muscle memory
// transfers between editors).
public sealed partial class LogicCanvasView
{
    private static readonly Dictionary<VirtualKey, string> s_quickKeyMap = new()
    {
        { VirtualKey.B, "Logic.If" },
        { VirtualKey.S, "Logic.Sequence" },
        { VirtualKey.D, "Flow.Delay" },
        { VirtualKey.O, "Flow.DoOnce" },
        { VirtualKey.N, "Flow.DoN" },
        // P2-A8 — Digit quick-keys spawn a Value.Int with the pressed digit
        // pre-filled into Attributes["Value"]. Mirrors pre-T15 commit
        // 34493c18 which mapped Keys.D0..D9 the same way. The digit value
        // is applied in TryQuickKeySpawn after CreateNode so the seed runs
        // through NodeRegistry's attribute scaffolding (not bolted on
        // outside it). Ctrl+Number1..9 still routes to bookmarks above the
        // main switch in Keyboard.cs — the bare-digit arm here is reached
        // only when no modifier is held.
        { VirtualKey.Number0, "Value.Int" },
        { VirtualKey.Number1, "Value.Int" },
        { VirtualKey.Number2, "Value.Int" },
        { VirtualKey.Number3, "Value.Int" },
        { VirtualKey.Number4, "Value.Int" },
        { VirtualKey.Number5, "Value.Int" },
        { VirtualKey.Number6, "Value.Int" },
        { VirtualKey.Number7, "Value.Int" },
        { VirtualKey.Number8, "Value.Int" },
        { VirtualKey.Number9, "Value.Int" },
    };

    /// <summary>
    /// P2-A8 helper — returns the digit character 0..9 for a number-row
    /// VirtualKey, or null for any other key. Used by TryQuickKeySpawn to
    /// pre-fill Attributes["Value"] on the spawned Value.Int node.
    /// </summary>
    private static int? DigitForKey(VirtualKey key) => key switch
    {
        VirtualKey.Number0 => 0,
        VirtualKey.Number1 => 1,
        VirtualKey.Number2 => 2,
        VirtualKey.Number3 => 3,
        VirtualKey.Number4 => 4,
        VirtualKey.Number5 => 5,
        VirtualKey.Number6 => 6,
        VirtualKey.Number7 => 7,
        VirtualKey.Number8 => 8,
        VirtualKey.Number9 => 9,
        _                  => null,
    };

    private VirtualKey? _heldQuickKey;

    private void OnHostKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_heldQuickKey == e.Key) _heldQuickKey = null;
        // Space-as-pan-mode tear-down — partnered with the KeyDown handler
        // in the Keyboard partial. KeyUp on Space drops the pan-mode arm
        // so a subsequent LMB+drag goes back to marquee / node-drag.
        if (e.Key == VirtualKey.Space) _spaceHeld = false;
    }

    /// <summary>
    /// Drop the held quick-key when the canvas loses focus. Without this,
    /// holding B and alt-tabbing away (or opening a flyout that takes
    /// focus) leaves the canvas armed — the next click on bare canvas
    /// would silently spawn a Logic.If the user never asked for. KeyUp
    /// doesn't fire on the host while focus is elsewhere. Same
    /// reasoning applies to Space-as-pan-mode: alt-tabbing while Space
    /// is physically held leaves the canvas "stuck" in pan-mode.
    /// </summary>
    private void OnHostLostFocus(object sender, RoutedEventArgs e)
    {
        _heldQuickKey = null;
        _spaceHeld    = false;
    }

    /// <summary>
    /// Called from OnHostKeyDown for keys that aren't otherwise handled
    /// (DEL/Esc/Ctrl-shortcuts get to e.Handled first). Arms _heldQuickKey
    /// so the next left-click on empty canvas spawns instead of marqueeing.
    /// </summary>
    private bool TryArmQuickKey(VirtualKey key)
    {
        if (s_quickKeyMap.ContainsKey(key))
        {
            _heldQuickKey = key;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pointer hook — called from OnHostPointerPressed when left-clicking on
    /// empty canvas. Returns true if the click was consumed by quick-key
    /// spawn (so the pointer handler skips marquee).
    /// </summary>
    private bool TryQuickKeySpawn(Point hostPoint)
    {
        if (_heldQuickKey is null || _vm is null) return false;
        if (!s_quickKeyMap.TryGetValue(_heldQuickKey.Value, out var title)) return false;

        var canvasPos = HostToCanvas(hostPoint);
        var node = NodeRegistry.CreateNode(
            title,
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is not null)
        {
            // P2-A8 — Digit quick-keys (0..9) pre-fill Attributes["Value"]
            // with the pressed digit so the spawned Value.Int reads as e.g.
            // "5" out of the gate rather than the template default "0".
            // Matches pre-T15 commit 34493c18's Keys.D0..D9 → Value.Int
            // path. Non-digit quick-keys (B/S/D/O/N) take the template
            // defaults unchanged.
            int? digit = DigitForKey(_heldQuickKey.Value);
            if (digit is not null)
            {
                node.Attributes["Value"] = digit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            AddNode(node, canvasPos.X, canvasPos.Y);
        }
        // Preserve the held state — Architect users typically chain spawns
        // ("hold B, click click click for three branches"). Released by KeyUp.
        return true;
    }
}
