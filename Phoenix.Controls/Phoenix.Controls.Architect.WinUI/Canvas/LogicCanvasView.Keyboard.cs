using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.System;
using Windows.UI.Core;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Keyboard partial — DEL deletes the selection, Esc cancels in-flight drags,
// Ctrl+S / Ctrl+O bubble through public events so the shell (MainWindow)
// can chain into ArchitectViewModel.Open / Save.
//
// Copy/Paste/Cut/Duplicate live on Ctrl+C / V / X / D in the main switch
// below; clipboard plumbing lives in LogicCanvasView.Clipboard.cs. Quick-
// key spawn (B/S/D/O/N) arms via TryArmQuickKey → fires on next bare-canvas
// click via TryQuickKeySpawn; the key→template map is in
// LogicCanvasView.QuickKeys.cs.
public sealed partial class LogicCanvasView
{
    // 0.11.x polish — Z / Y deliberately do NOT have scancode anchoring.
    // On German QWERTZ the Z and Y keytops are swapped relative to US
    // QWERTY, and the user expectation is "the chord follows the label
    // I see, not the physical position" — pressing the labeled Z key on
    // a German keyboard (which sits at the US-Y physical position) must
    // run Undo. The VirtualKey.Z / VirtualKey.Y arms in the main switch
    // below already deliver that: Windows translates the scancode through
    // the active keyboard layout, the resulting VirtualKey matches the
    // label, and Ctrl+Z / Ctrl+Y do the right thing on every layout the
    // user can actually read off their keys (QWERTY / QWERTZ / AZERTY).
    // The previously-defined ScanCode_PhysicalZ / ScanCode_PhysicalY arms
    // were removed because they forced "bottom-left = Undo" — which made
    // German users' Ctrl+Z (top-right Z) trigger Redo and vice versa.
    //
    // The OTHER scancode arms (C/V/X/D/A/S/F/G) are kept below: those
    // letters are in the same physical position on QWERTY and QWERTZ, so
    // scancode anchoring is harmless there and still covers Dvorak.

    // Dvorak scancode coverage. Pre-fix only Ctrl+Z / Ctrl+Y had
    // scancode-anchored handling, which covered QWERTY/QWERTZ/AZERTY (the
    // letters Majo's audit said were "physical-position-stable on these
    // three") but not Dvorak. On Dvorak the physical C/V/X/D/A/S/F/G keys
    // sit at completely different VirtualKey codes, so Ctrl+C/V/X/D/A/S/F/G
    // would either misfire or do nothing without scancode anchoring.
    //
    //   Physical C (Dvorak "J")  → scancode 0x2E
    //   Physical V (Dvorak "K")  → scancode 0x2F
    //   Physical X (Dvorak "Q")  → scancode 0x2D
    //   Physical D (Dvorak "H")  → scancode 0x20
    //   Physical A (Dvorak "A")  → scancode 0x1E   (same position on Dvorak)
    //   Physical S (Dvorak "O")  → scancode 0x1F
    //   Physical F (Dvorak "U")  → scancode 0x21
    //   Physical G (Dvorak "I")  → scancode 0x22
    //
    // The VirtualKey.C/V/X/D/A/S/F/G arms in the main switch stay as the
    // QWERTY safety net for layouts/VMs where KeyStatus.ScanCode isn't
    // populated. Mirrors the pattern from 0x2C (Z) / 0x15 (Y).
    private const uint ScanCode_PhysicalC = 0x2E;
    private const uint ScanCode_PhysicalV = 0x2F;
    private const uint ScanCode_PhysicalX = 0x2D;
    private const uint ScanCode_PhysicalD = 0x20;
    private const uint ScanCode_PhysicalA = 0x1E;
    private const uint ScanCode_PhysicalS = 0x1F;
    private const uint ScanCode_PhysicalF = 0x21;
    private const uint ScanCode_PhysicalG = 0x22;

    // Pointer-over-canvas tracking. ZoomKeyboard reads this to
    // decide whether to anchor on the cursor (matches wheel-zoom math at
    // LogicCanvasView.xaml.cs:520-524) or the viewport centre (legacy
    // behaviour, used when the pointer is outside the host).
    private bool _pointerOverHost;

    /// <summary>
    /// Raised when the user presses Ctrl+W on the canvas. The
    /// chord is advertised in <c>ArchitectHotkeyCatalog</c> as "Close window
    /// (sibling windows only)" but the canvas owns no window of its own, so it
    /// surfaces the intent as an event and lets the host decide. The intended
    /// subscriber is <c>ArchitectSiblingWindow</c>, which routes this to its
    /// own <c>Close()</c>; the primary <c>MainView</c> shell and the
    /// <c>SubGraphWindow</c> editor canvas deliberately do NOT subscribe, so
    /// Ctrl+W is inert there (matching the catalog's "sibling windows only"
    /// scoping). Mirrors the no-op-when-unsubscribed contract used by
    /// <c>KeyboardShortcutsRequested</c> / <c>InspectorToggleRequested</c>.
    /// Declared here (rather than alongside the other canvas events in
    /// LogicCanvasView.xaml.cs) to keep the change self-contained
    /// in the keyboard partial.
    /// </summary>
    public event System.EventHandler? CloseWindowRequested;

    // True when any node is mid inline-edit
    // (title rename, socket value-pill / label edit, or middle-attribute
    // edit). Focus-independent so it catches the window where a pill is in
    // edit mode but keyboard focus hasn't landed on its TextBox yet. Delete
    // is a rare keypress so the O(sockets) scan is cheap.
    private bool IsAnyInlineEditorActive()
    {
        if (_vm is null) return false;
        foreach (var n in _vm.Nodes)
        {
            if (n.IsTitleRenaming) return true;
            foreach (var s in n.Inputs)  if (s.IsEditing || s.IsRenaming) return true;
            foreach (var s in n.Outputs) if (s.IsEditing || s.IsRenaming) return true;
            foreach (var m in n.MiddleAttributes) if (m.IsEditing) return true;
        }
        return false;
    }

    // True when any Flyout / context-menu popup owned by this window is open
    // (spawn palette, node finder, node / socket / pill / frame context menu,
    // colour picker, frame-rename box). Canvas keyboard shortcuts must yield to
    // these — several are shown via ShowAt without pulling focus off the canvas,
    // so this UserControl's KeyDown still fires underneath them. Only
    // FlyoutPresenter / MenuFlyoutPresenter popups count; the transient hover
    // TooltipPopup is a bare Popup and is intentionally NOT treated as a menu,
    // so a tooltip can never swallow a shortcut.
    private bool IsAnyMenuOrFlyoutOpen()
    {
        var root = XamlRoot;
        if (root is null) return false;
        try
        {
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            {
                if (popup.Child is Microsoft.UI.Xaml.Controls.FlyoutPresenter
                                or Microsoft.UI.Xaml.Controls.MenuFlyoutPresenter)
                    return true;
            }
        }
        catch { /* never break key handling on a popup query */ }
        return false;
    }

    // True when a text-input control currently holds keyboard focus anywhere in
    // the window — a value-pill / rename box on the canvas, an inspector field,
    // or a flyout's search box. Focus-based so it still fires during the
    // click→type focus race (pill visible, its TextBox not yet focused, so the
    // KeyDown's OriginalSource is still the canvas).
    private bool IsTextInputFocused()
    {
        var root = XamlRoot;
        if (root is null) return false;
        try
        {
            return Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)
                is Microsoft.UI.Xaml.Controls.TextBox
                or Microsoft.UI.Xaml.Controls.RichEditBox
                or Microsoft.UI.Xaml.Controls.PasswordBox
                or Microsoft.UI.Xaml.Controls.NumberBox
                or Microsoft.UI.Xaml.Controls.AutoSuggestBox;
        }
        catch { return false; }
    }

    private void OnHostKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null) return;

        bool ctrlEarly = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        bool altEarly  = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                          & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

        // ── AltGr passthrough (non-US keyboard layouts) ─────────────────────
        // Windows surfaces AltGr as Ctrl+Alt. On QWERTZ / AZERTY / Nordic / etc.
        // layouts AltGr is the ONLY way to type { } [ ] @ \ ~ | € — the very
        // characters a script author needs inside a value pill ({var}
        // substitution, JSON payloads, regex). Pre-fix every Ctrl-chord
        // accelerator below treated the Ctrl half as a real modifier, so AltGr+7
        // fired "bookmark slot 7", AltGr+0 fired "zoom reset", and the inline-
        // editor guard (which only yielded when NO Ctrl was down) let the
        // keystroke fall through to the canvas instead of reaching the focused
        // pill — a German user simply could not type "{" or "}" (Majo report
        // 2026-06-24). AltGr is never a canvas chord: yield it. Inside a focused
        // field the character reaches the TextBox; on the bare canvas it is
        // harmlessly inert. The Ctrl+Alt+F6 / F7 DEV toggles are the only
        // intentional Ctrl+Alt chords and are exempted — AltGr never produces a
        // character with a function key, so there is no collision there.
        bool altGr = ctrlEarly && altEarly;
        if (altGr && e.Key is not (VirtualKey.F6 or VirtualKey.F7))
            return;

        // ── Open sub-menu / flyout guard ────────────────────────────────────
        // A context menu, the spawn palette, the node finder, a colour / pill
        // picker, or the frame-rename box is open. Its own content owns the
        // keyboard, but several of those surfaces are shown via ShowAt without
        // pulling focus off the canvas, so this UserControl's KeyDown STILL
        // fires underneath them — and bare F framed the viewport, quick-keys
        // spawned nodes, Space re-opened the palette, etc. while the user was
        // navigating a sub-menu (Majo report 2026-07-01). Yield every key while
        // any flyout / menu popup is open; the surface handles it, or it is
        // harmlessly inert. Hover tooltips are excluded in IsAnyMenuOrFlyoutOpen.
        if (IsAnyMenuOrFlyoutOpen())
            return;

        // ── Inline-editor guard ─────────────────────────────────────────────
        // When an inline editor (value pill, socket / title rename, inspector
        // field, search box) is the keystroke's target, the field owns text
        // input — Delete must not nuke the node mid-typing, bare F must not frame
        // the viewport, quick-keys must not spawn nodes, Space/Tab must not open
        // the palette. The IsAnyInlineEditorActive() arm also covers the
        // click→type focus race: on a pointer-captured canvas Focus() is deferred
        // a dispatcher cycle, so for 1-2 frames the pill TextBox is visible-but-
        // unfocused and e.OriginalSource is still the canvas; the VM IsEditing
        // flags it reads are set the instant the edit begins.
        //
        // Pre-fix this guard yielded ONLY when no Ctrl was held, so a Ctrl-chord
        // while editing fell through to the canvas — and Ctrl+C / V / X hit the
        // NODE clipboard, so copying selected text OUT of a value pill copied the
        // whole node instead (Majo report 2026-06-24). Now the field also owns
        // the standard text-editing chords (copy / cut / paste / select-all /
        // undo / redo); only the non-conflicting app commands (Ctrl+S save,
        // Ctrl+O open) fall through to the canvas. Every other canvas chord
        // (duplicate, group, autoformat, frame, quick-spawn) stays inert while
        // the user is typing.
        bool inInlineEditor = e.OriginalSource is Microsoft.UI.Xaml.Controls.TextBox
                                               or Microsoft.UI.Xaml.Controls.RichEditBox
                                               or Microsoft.UI.Xaml.Controls.PasswordBox
                                               or Microsoft.UI.Xaml.Controls.NumberBox
                                               or Microsoft.UI.Xaml.Controls.AutoSuggestBox
                              || IsAnyInlineEditorActive()
                              || IsTextInputFocused();
        if (inInlineEditor)
        {
            // No Ctrl → plain typing / navigation belongs to the field.
            if (!ctrlEarly) return;
            // Ctrl held inside a field: the field handles the standard editing
            // chords; only Save / Open fall through to the canvas.
            if (e.Key is not (VirtualKey.S or VirtualKey.O)) return;
        }

        bool ctrl = ctrlEarly;
        bool alt  = altEarly;

        // ── Ctrl-chord scancode anchoring (layout-independent) ───────────────
        // Z and Y intentionally NOT handled here — they live in the VirtualKey
        // switch below so the chord follows the keyboard label (German QWERTZ
        // Ctrl+Z = labeled-Z = Undo, the same chord QWERTY users expect).
        // The remaining arms (C/V/X/D/A/S/F/G) anchor by scancode because
        // those letters are in the same physical position on QWERTY/QWERTZ,
        // and the scancode form is what makes Dvorak users hit the canonical
        // Blueprints chord set.
        if (ctrl)
        {
            uint sc = e.KeyStatus.ScanCode;
            switch (sc)
            {
                case ScanCode_PhysicalC:
                    Copy();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalV:
                    Paste();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalX:
                    CutSelection();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalD:
                    DuplicateSelection();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalA:
                    SelectAll();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalS:
                    SaveRequested?.Invoke(this, System.EventArgs.Empty);
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalF:
                    ShowNodeFinderFlyout();
                    e.Handled = true;
                    return;
                case ScanCode_PhysicalG:
                    if (_vm.SelectedNodes.Count < 2)
                    {
                        GlobalLogger.Log(
                            "Ctrl+G needs >= 2 nodes selected",
                            "Architect.LogicCanvasView",
                            LogLevel.System);
                    }
                    else
                    {
                        CollapseSelectionToMacro();
                    }
                    e.Handled = true;
                    return;
            }
        }

        // Bookmarks: Ctrl+1..9 stores the current pan/zoom on Graph.Bookmarks;
        // Alt+1..9 restores. Pre-T15 parity (Helpers.SetBookmarkSlot / RecallBookmarkSlot).
        // Handled before the main switch so Ctrl+Number doesn't fall through to
        // Quick-Spawn's digit branch.
        if ((ctrl || alt) && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
        {
            int slot = (int)e.Key - (int)VirtualKey.Number0;
            if (ctrl) SetBookmarkSlot(slot); else RecallBookmarkSlot(slot);
            e.Handled = true;
            return;
        }

        // 0.11.x polish — bare Y mid-wire-drag drops a Flow.Reroute at
        // the cursor and continues the drag from the reroute's free pin.
        // Majo's QWERTZ Y sits on the bottom-left letter key (= QWERTY
        // physical Z position), the same chord his pre-T15 muscle memory
        // already associated with "knot here". Gated tightly on
        // DragState.WireDrop so the unmodified Y key stays a free chord
        // outside the drag context. Checked BEFORE the main switch so the
        // chord wins over any future Y mapping added in the switch body.
        if (!ctrl && !alt
            && e.Key == VirtualKey.Y
            && _drag == DragState.WireDrop
            && TryInsertRerouteUnderCursorDuringWireDrag())
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // Space — open the spawn palette at view centre. 0.10.8
            // readability sweep: Majo's #8 calls out bare-Space as the
            // canonical "open the node search" chord, matching the pre-WinUI
            // Architect behaviour. Ctrl+Space is preserved below as the
            // Blueprints fallback so the chord that shipped in 0.10.0 still
            // works for muscle memory.
            //
            // The previous bare-Space behaviour — arm Space-held-LMB pan
            // mode — has been retired. MMB drag still pans
            // (LogicCanvasView.Pointer.cs:81 checks IsMiddleButtonPressed)
            // and the arrow-key view-nudge handler stays available, so
            // pan-mode loses an idiom but keeps a viable replacement.
            case VirtualKey.Space when !ctrl && !alt:
                ShowSpawnPaletteAtViewCenter();
                e.Handled = true;
                break;

            // Ctrl+Space — Blueprints alias for the spawn palette. Kept
            // alongside bare Space so users carrying habits from the 0.10.0
            // chrome don't lose the chord.
            case VirtualKey.Space when ctrl:
                ShowSpawnPaletteAtViewCenter();
                e.Handled = true;
                break;

            case VirtualKey.Delete:
                // Defense-in-depth backstop. The
                // OriginalSource TextBox guard at the top of this handler
                // catches Delete while an inline editor HAS keyboard focus.
                // But a value pill / title / middle-attr editor can briefly be
                // in edit mode with focus elsewhere (the focus-on-edit-entry
                // fix lives in NodeView). Never nuke
                // the selected node while ANY inline editor is open; swallow the
                // key so it can't fall through to DeleteSelection.
                if (IsAnyInlineEditorActive())
                {
                    e.Handled = true;
                    break;
                }
                DeleteSelection();
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                // Drop the held quick-spawn key FIRST so Esc always disarms
                // (pre-fix _heldQuickKey was cleared only on KeyUp / LostFocus,
                // so a user tapping S and then Esc to "cancel" left the canvas
                // armed and the next bare-canvas click silently spawned a
                // Flow.Sequence.
                bool hadHeldKey = _heldQuickKey is not null;
                if (hadHeldKey)
                {
                    _heldQuickKey = null;
                    GlobalLogger.Log("Quick-spawn cancelled", "Architect.LogicCanvasView", LogLevel.System);
                }
                // 0.11.x polish — Esc also clears the canvas-wide pin-
                // selection ring (the gold halo that appears around the
                // last-clicked pin). Pre-fix the ring stuck around forever;
                // treating Esc as the universal "clear transient
                // affordances" key mirrors how it tears down the other
                // in-flight gestures below.
                NodeView.ClearPinSelection();

                // Tear down any in-flight gesture exhaustively. Pre-fix only
                // WireDrop / Marquee were canceled — NodeDrag / FrameMove /
                // FrameResize / Pan stayed live (the node kept following the
                // cursor until the next click) and pointer capture was never
                // released. AbortInFlightGesture mirrors HandlePointerEnd's
                // teardown without needing a PointerRoutedEventArgs.
                if (_drag != DragState.Idle)
                {
                    AbortInFlightGesture();
                    e.Handled = true;
                }
                else if (_vm.PickerVarChainName is not null)
                {
                    // Sticky var-chain picker — Esc clears the dim overlay
                    // before clearing selection so the user can leave the
                    // picker without losing what they had selected.
                    _vm.PickerVarChainName = null;
                    e.Handled = true;
                }
                else if (_vm.Selection is not null || _vm.SelectedNodes.Count > 0)
                {
                    _vm.Selection = null;
                    e.Handled = true;
                }
                else if (hadHeldKey)
                {
                    // Even with no selection / drag / picker, swallow Esc when
                    // we cleared an armed quick-key so the event doesn't bubble
                    // to a parent close-shortcut.
                    e.Handled = true;
                }
                break;

            // F1 opens documentation for the selected node. When no
            // node is selected, log a discoverability breadcrumb (a later
            // Keyboard Shortcuts dialog is the proper fallback; the log
            // line keeps F1 from looking dead in the meantime — no modal).
            // Ctrl+Alt+F6 — DEV toggle for experimental node
            // virtualization (default OFF; on-screen perf test only — see
            // ToggleNodeVirtualization). F6 is otherwise unused in the canvas.
            case VirtualKey.F6 when ctrl && alt:
                ToggleNodeVirtualization();
                e.Handled = true;
                break;

            // Ctrl+Alt+F7 — toggle the immediate-
            // mode GPU canvas (default OFF; the permanent large-graph perf fix —
            // see LogicCanvasView.Win2D.cs / ToggleImmediateMode). F7 is
            // otherwise unused in the canvas.
            case VirtualKey.F7 when ctrl && alt:
                ToggleImmediateMode();
                e.Handled = true;
                break;

            case VirtualKey.F1 when !ctrl && !alt:
                if (_vm.Selection is NodeViewModel selectedNode
                    && !string.IsNullOrEmpty(selectedNode.Title))
                {
                    OpenNodeDocumentationFor(selectedNode.Title);
                }
                else if (_vm.SelectedNodes.Count == 1
                         && !string.IsNullOrEmpty(_vm.SelectedNodes[0].Title))
                {
                    OpenNodeDocumentationFor(_vm.SelectedNodes[0].Title);
                }
                else
                {
                    // F1-without-selection falls back to
                    // the Keyboard Shortcuts help dialog. The canvas raises an event so the chrome
                    // (which owns the dialog factory + XamlRoot) does the
                    // show. Hosts that don't subscribe (e.g. the SubGraph
                    // editor window) silently no-op — the canvas-side
                    // behaviour is identical to pre-fix in that case.
                    KeyboardShortcutsRequested?.Invoke(this, System.EventArgs.Empty);
                }
                e.Handled = true;
                break;

            case VirtualKey.S when ctrl:
                SaveRequested?.Invoke(this, System.EventArgs.Empty);
                e.Handled = true;
                break;

            case VirtualKey.O when ctrl:
                OpenRequested?.Invoke(this, System.EventArgs.Empty);
                e.Handled = true;
                break;

            // Ctrl+F sits next to Ctrl+S / Ctrl+O for readability;
            // pre-fix it lived below Ctrl+Y which made it easy to miss when
            // skimming the chord set. Functionally identical to the
            // bottom-of-switch handler that previously hosted it.
            case VirtualKey.F when ctrl:
                // 0.10.0 — Ctrl+F opens the in-graph node finder Flyout
                // (filters by Title against the active graph). The chrome
                // Edit → Find Node... menu item routes to the same surface
                // via RequestFindNodeFromShell.
                ShowNodeFinderFlyout();
                e.Handled = true;
                break;

            // F2 opens the rename flyout for the currently
            // selected single frame. Matches Majo's canvas-rename idiom
            // unification: every canvas rename surface honours BOTH double-click
            // AND F2. Double-click on a frame label routes through
            // OnHostDoubleTapped; F2 is the keyboard chord. Silent no-op when
            // the selection isn't exactly one frame so a stray F2 with mixed /
            // empty selection doesn't pop an irrelevant flyout. The flyout
            // anchors at the frame's centre in host-space so the textbox
            // appears over the label rather than at the cursor (which may be
            // far away when the user is reaching for F2 on the keyboard).
            case VirtualKey.F2 when !ctrl && !alt:
                if (_vm.SelectedFrames.Count == 1)
                {
                    var frame = _vm.SelectedFrames[0];
                    var canvasCentre = new Windows.Foundation.Point(
                        frame.X + frame.Width / 2,
                        frame.Y + 8);
                    var hostPoint = new Windows.Foundation.Point(
                        canvasCentre.X * _vm.Zoom + _vm.PanX,
                        canvasCentre.Y * _vm.Zoom + _vm.PanY);
                    ShowFrameRenameFlyout(frame, hostPoint);
                    e.Handled = true;
                }
                break;

            // F4 toggles the
            // inspector panel's expanded / rolled-up state. The canvas
            // doesn't own the inspector column itself (MainView /
            // ArchitectSiblingWindow do); raise InspectorToggleRequested
            // so whichever host is wired flips the visibility via the
            // same plumbing the chrome "View → Toggle Inspector" menu and
            // the InspectorChevronButton use. Hosts that don't subscribe
            // (e.g. SubGraphWindow's sub-graph editor canvas) silently
            // no-op — matches the KeyboardShortcutsRequested fall-through
            // pattern already in this file.
            case VirtualKey.F4 when !ctrl && !alt:
                InspectorToggleRequested?.Invoke(this, System.EventArgs.Empty);
                e.Handled = true;
                break;

            // F3 / Shift+F3 walk
            // the Find-Node match set without needing the flyout open.
            // StepFindCursor falls back to opening the Find flyout when the
            // match set is empty so the chord is discoverable from a clean
            // canvas state. Shift gates the direction (advance vs reverse).
            case VirtualKey.F3 when !ctrl && !alt:
                {
                    bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                                  & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                    StepFindCursor(reverse: shift);
                    e.Handled = true;
                    break;
                }

            case VirtualKey.C when ctrl:
                Copy();
                e.Handled = true;
                break;

            case VirtualKey.V when ctrl:
                Paste();
                e.Handled = true;
                break;

            case VirtualKey.D when ctrl:
                DuplicateSelection();
                e.Handled = true;
                break;

            case VirtualKey.X when ctrl:
                CutSelection();
                e.Handled = true;
                break;

            case VirtualKey.A when ctrl:
                SelectAll();
                e.Handled = true;
                break;

            case VirtualKey.L when ctrl:
                AutoFormatGraph();
                e.Handled = true;
                break;

            case VirtualKey.F when !ctrl && !alt:
                FrameSelection();
                e.Handled = true;
                break;

            case VirtualKey.Home:
                ZoomToFit();
                e.Handled = true;
                break;

            // Keyboard zoom — Ctrl+0 reset, Ctrl++/= zoom in, Ctrl+- zoom out
            // (TODO 2026-05-07 round 2 P3 — no keyboard zoom). Anchored on
            // the host viewport centre because there's no cursor on a key
            // press; mirrors the same clamp range PointerWheel uses.
            // 0.10.0: Ctrl+Shift+= / Ctrl+Shift+- swaps the 1.1
            // per-press step for 1.025 (fine-step) so users can land
            // on targeted zoom levels (100%, 150%, 200%) without
            // overshooting.
            case VirtualKey.Number0 when ctrl:
                ZoomKeyboard(1.0 / Math.Max(_vm.Zoom, 0.0001));
                e.Handled = true;
                break;
            case VirtualKey.Add when ctrl:
            case (VirtualKey)187 when ctrl:           // VK_OEM_PLUS / Ctrl+=
                ZoomKeyboard(KeyboardZoomStep());
                e.Handled = true;
                break;
            case VirtualKey.Subtract when ctrl:
            case (VirtualKey)189 when ctrl:           // VK_OEM_MINUS
                ZoomKeyboard(1.0 / KeyboardZoomStep());
                e.Handled = true;
                break;

            case VirtualKey.Z when ctrl:
                {
                    bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                                  & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
                    if (shift) _history?.Redo();
                    else       _history?.Undo();
                    e.Handled = true;
                    break;
                }

            case VirtualKey.Y when ctrl:
                _history?.Redo();
                e.Handled = true;
                break;

            // Tab is intentionally NOT handled here — letting it fall through
            // to default → TryArmQuickKey (returns false for Tab) → e.Handled
            // stays false → WinUI's FocusManager processes Tab as system
            // focus traversal. 0.10.0 — moved spawn-palette open to Ctrl+Space
            // (above) so Tab walks out of the canvas as users expect.

            // Ctrl+F handler relocated next to Ctrl+S / Ctrl+O
            // above for readability.

            case VirtualKey.G when ctrl:
                // 0.10.0 — Ctrl+G "Group" wraps the current multi-selection
                // in a Macro shell. CollapseSelectionToMacro is a no-op on
                // <2 selected nodes (so a stray Ctrl+G with nothing selected
                // is harmless rather than spawning an empty macro).
                // Log the silent-reject path so the System Log
                // surfaces the reason; mirrors the no-modal-for-repeatable-
                // rejections rule (don't pop a modal, but DO leave a breadcrumb).
                if (_vm.SelectedNodes.Count < 2)
                {
                    GlobalLogger.Log(
                        "Ctrl+G needs >= 2 nodes selected",
                        "Architect.LogicCanvasView",
                        LogLevel.System);
                }
                else
                {
                    CollapseSelectionToMacro();
                }
                e.Handled = true;
                break;

            // Ctrl+W "Close window". ArchitectHotkeyCatalog
            // (Canvas/ArchitectHotkeyCatalog.cs line 89) advertises this
            // chord ("Close window (sibling windows only)") in the Keyboard
            // Shortcuts dialog, but pre-fix this handler had no VirtualKey.W
            // case at all — the documented chord did nothing, which read as a
            // dead shortcut to anyone who tried it. The canvas doesn't own a
            // window of its own; it raises CloseWindowRequested so whichever
            // host wants to honour the chord (ArchitectSiblingWindow routes it
            // to Close()) can subscribe. Hosts that DON'T subscribe — the main
            // MainView shell and the SubGraphWindow editor canvas — silently
            // no-op, which is the right behaviour there: the catalog scopes
            // this to "sibling windows only", so closing the primary editor
            // surface or the sub-graph editor via Ctrl+W is intentionally
            // inert. Mirrors the KeyboardShortcutsRequested / InspectorToggle
            // Requested fall-through pattern already in this file. e.Handled
            // is set regardless so the chord can't bubble to an ancestor
            // close-accelerator and double-fire on subscribed hosts.
            case VirtualKey.W when ctrl:
                CloseWindowRequested?.Invoke(this, System.EventArgs.Empty);
                e.Handled = true;
                break;

            case VirtualKey.C when !ctrl && !alt:
                // 0.10.0 — bare C drops a comment frame at the view centre
                // (canvas-space). Pre-0.10.0 the only paths were a right-click
                // menu item and a Ctrl+RightClick fast-path; the bare-C
                // accelerator matches the OBS / DaVinci editing rhythm where
                // typed letters seed annotations in-place.
                AddCommentFrameAtViewCenter();
                e.Handled = true;
                break;

            // Arrow-key node nudge. Moves the
            // current selection by 1 px (10 px with Shift) so keyboard-only
            // users can fine-tune layout without grabbing the mouse. Pushes
            // a single undo entry per key-hold GESTURE (snapshot on the
            // fresh press, position-only mutation on auto-repeats) — the
            // same one-entry-per-gesture idiom the pointer node-drag uses.
            // KeyStatus.WasKeyDown discriminates repeat events; a discrete
            // press snapshots exactly as before. Group-drag is honoured.
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Up:
            case VirtualKey.Down:
                if (!ctrl && TryNudgeSelection(e.Key, e.KeyStatus.WasKeyDown))
                {
                    e.Handled = true;
                    break;
                }
                goto default;

            default:
                // No-modifier letter keys arm a quick-spawn — see QuickKeys partial.
                if (!ctrl && TryArmQuickKey(e.Key)) e.Handled = true;
                break;
        }
    }

    // True once a nudge gesture has pushed its undo snapshot. Guards the
    // (rare) case where the FIRST arrow event this canvas sees is already an
    // auto-repeat (focus regained mid-hold): without at least one snapshot on
    // the stack the repeat's mutation would be un-undoable, so an unarmed
    // repeat snapshots anyway. Cleared at gesture end (arrow KeyUp) and on
    // LostFocus in the QuickKeys partial — WasKeyDown mirrors global
    // keyboard state, so a repeat delivered to a re-focused canvas must not
    // reuse an arm from a gesture that ended while focus was elsewhere.
    private bool _nudgeUndoArmed;

    /// <summary>
    /// Arrow-key node nudge. Returns true when something moved (so caller
    /// marks the event handled). Single PushUndo per key-hold gesture — the
    /// snapshot lands on the fresh press (<paramref name="isRepeat"/> false)
    /// and auto-repeats only mutate positions, so holding a key no longer
    /// serializes the whole graph ~30×/s. A discrete press snapshots exactly
    /// as before. The active multi-selection moves together if more than one
    /// node is selected. Pre-fix the canvas had no keyboard layout-tweak
    /// path at all.
    /// </summary>
    private bool TryNudgeSelection(VirtualKey key, bool isRepeat)
    {
        if (_vm is null) return false;
        // Resolve target set: multi-selection wins, otherwise the focused
        // Selection (if it's a node).
        System.Collections.Generic.IList<NodeViewModel> targets;
        if (_vm.SelectedNodes.Count > 0)
            targets = System.Linq.Enumerable.ToList(_vm.SelectedNodes);
        else if (_vm.Selection is NodeViewModel solo)
            targets = new[] { solo };
        else
            return false;

        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                      & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        int step = shift ? 10 : 1;
        (int dx, int dy) = key switch
        {
            VirtualKey.Left  => (-step, 0),
            VirtualKey.Right => ( step, 0),
            VirtualKey.Up    => (0, -step),
            VirtualKey.Down  => (0,  step),
            _                => (0, 0),
        };
        if (dx == 0 && dy == 0) return false;

        // Snapshot once per gesture: the fresh press captures the pre-hold
        // pose (so Ctrl+Z restores it in one step); repeats reuse it.
        if (!isRepeat || !_nudgeUndoArmed)
        {
            PushUndo();
            _nudgeUndoArmed = true;
        }
        foreach (var n in targets)
            _vm.TranslateNode(n, dx, dy);
        return true;
    }

    private void ShowSpawnPaletteAtViewCenter()
    {
        // Anchor at the visible canvas centre when no cursor pos is available.
        var hostCenter = new Windows.Foundation.Point(
            HostRoot.ActualWidth  / 2,
            HostRoot.ActualHeight / 2);
        ShowSpawnPalette(hostCenter);
    }

    /// <summary>
    /// 0.10.0 — bare C / "Add comment frame here" canvas-space spawn at the
    /// visible viewport centre. The frame footprint is the AddFrame default
    /// (240×160 in canvas-space, gold ember chrome). Pushed onto the undo
    /// stack via AddFrame's internal PushUndo.
    /// </summary>
    private void AddCommentFrameAtViewCenter()
    {
        if (_vm is null) return;
        var hostCenter = new Windows.Foundation.Point(
            HostRoot.ActualWidth  / 2,
            HostRoot.ActualHeight / 2);
        var canvasCenter = HostToCanvas(hostCenter);
        // Place the frame so its centre lands at the viewport centre instead
        // of its top-left — matches the cursor-anchored "Add Comment Frame"
        // entry on the empty-canvas right-click menu, which uses the click
        // point directly as the top-left.
        const double w = 240.0;
        const double h = 160.0;
        AddFrame(
            canvasCenter.X - w / 2,
            canvasCenter.Y - h / 2,
            w, h, "Comment",
            ArchitectCanvasPalette.CommentFrameDefault);
    }

    /// <summary>
    /// Delete the current selection. When <paramref name="pushUndo"/> is
    /// true (default) a single undo snapshot is pushed for the whole batch
    /// so Ctrl+Z restores it in one step. Callers like <see cref="CutSelection"/>
    /// pass <c>false</c> after they've already snapshotted themselves,
    /// avoiding the double-undo entry the pre-fix Cut produced (one for
    /// Copy's silent state + one for the inner Delete).
    /// </summary>
    private void DeleteSelection(bool pushUndo = true)
    {
        if (_vm is null) return;

        // DEL spans nodes + wires + frames. Pre-fix
        // only nodes participated in multi-DEL; a marquee that grabbed a wire +
        // a frame + three nodes silently lost the wire + frame on DEL. Now any
        // non-empty selected* collection triggers the batched path so a single
        // Ctrl+Z restores everything in one step.
        bool anyMulti = _vm.SelectedNodes.Count > 0
                     || _vm.SelectedLinks.Count > 0
                     || _vm.SelectedFrames.Count > 0;
        if (anyMulti)
        {
            var doomedNodes  = _vm.SelectedNodes .ToArray();
            var doomedLinks  = _vm.SelectedLinks .ToArray();
            var doomedFrames = _vm.SelectedFrames.ToArray();
            if (pushUndo) PushUndo();
            // Wires first (so RemoveNode's per-node link sweep doesn't double-
            // fire on already-gone wires); then nodes; then frames.
            foreach (var l in doomedLinks)  RemoveLink(l, pushUndo: false);
            foreach (var n in doomedNodes)  RemoveNode(n, pushUndo: false);
            foreach (var f in doomedFrames) RemoveFrame(f);
            return;
        }

        // Otherwise the focused-target Selection gets dropped (link or single node).
        switch (_vm.Selection)
        {
            case NodeViewModel n: RemoveNode(n, pushUndo: pushUndo); break;
            case LinkViewModel l: RemoveLink(l, pushUndo: pushUndo); break;
        }
    }

    // ─── Cut / Select-All / Frame / Zoom-fit ─────────────────────────────

    /// <summary>
    /// Ctrl+X — Copy current selection to the in-process clipboard then delete
    /// it under a SINGLE undo snapshot. Pre-fix Copy was silent (no undo) but
    /// DeleteSelection pushed its own undo, so Ctrl+Z after Cut only undid
    /// the delete half and the user had to press Ctrl+Z twice (the second
    /// usually rewound past the Cut into a prior structural change).
    /// </summary>
    private void CutSelection()
    {
        if (_vm is null) return;
        if (_vm.SelectedNodes.Count == 0 && _vm.Selection is not NodeViewModel) return;
        PushUndo();
        Copy();
        DeleteSelection(pushUndo: false);
    }

    /// <summary>
    /// Ctrl+A — selects every node in the active graph. Mirrors pre-T15
    /// SelectAll: replaces the multi-set with Graph.Nodes; primary Selection is
    /// cleared so the inspector doesn't pin to a single node.
    /// </summary>
    private void SelectAll()
    {
        if (_vm is null) return;
        _vm.SetMultiSelection(_vm.Nodes);
    }

    /// <summary>
    /// Home — zoom + pan to fit every node in view, padded by 40px on each
    /// side. Clamped to [0.2, 2.0] zoom envelope per pre-T15 ZoomToFit. No-op
    /// on an empty graph (resets to identity per pre-T15).
    /// </summary>
    private void ZoomToFit()
    {
        if (_vm is null) return;
        if (_vm.Nodes.Count == 0)
        {
            _vm.Zoom = 1.0; _vm.PanX = 0; _vm.PanY = 0;
            ApplyViewTransform();
            return;
        }
        FitToBounds(ComputeNodesBounds(_vm.Nodes), padding: 40.0);
    }

    /// <summary>
    /// F — UE-Blueprints idiom: zoom to fit the current selection. Falls back
    /// to <see cref="ZoomToFit"/> when nothing is selected. Selection bbox
    /// padded by 60px per pre-T15 FrameSelection.
    /// </summary>
    private void FrameSelection()
    {
        if (_vm is null) return;
        var selection = _vm.SelectedNodes.Count > 0
            ? (System.Collections.Generic.IEnumerable<NodeViewModel>)_vm.SelectedNodes
            : (_vm.Selection is NodeViewModel solo
                ? new[] { solo }
                : System.Linq.Enumerable.Empty<NodeViewModel>());
        var list = System.Linq.Enumerable.ToList(selection);
        if (list.Count == 0) { ZoomToFit(); return; }
        FitToBounds(ComputeNodesBounds(list), padding: 60.0);
    }

    private (double X, double Y, double W, double H) ComputeNodesBounds(
        System.Collections.Generic.IEnumerable<NodeViewModel> nodes)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            minX = System.Math.Min(minX, n.X);
            minY = System.Math.Min(minY, n.Y);
            maxX = System.Math.Max(maxX, n.X + n.Width);
            maxY = System.Math.Max(maxY, n.Y + n.Height);
        }
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private void FitToBounds((double X, double Y, double W, double H) bounds, double padding)
    {
        if (_vm is null) return;
        double w = System.Math.Max(1, bounds.W + padding * 2);
        double h = System.Math.Max(1, bounds.H + padding * 2);
        double viewW = System.Math.Max(1, HostRoot.ActualWidth);
        double viewH = System.Math.Max(1, HostRoot.ActualHeight);
        double zoom = System.Math.Min(viewW / w, viewH / h);
        zoom = System.Math.Clamp(zoom, 0.2, 2.0);
        double cx = bounds.X - padding;
        double cy = bounds.Y - padding;
        _vm.Zoom = zoom;
        _vm.PanX = -cx * zoom + (viewW - w * zoom) / 2.0;
        _vm.PanY = -cy * zoom + (viewH - h * zoom) / 2.0;
        ApplyViewTransform();
    }

    // ─── Bookmarks (Ctrl+1..9 set / Alt+1..9 recall) ──────────────────────

    /// <summary>
    /// Ctrl+<paramref name="slot"/> — captures current pan + zoom into
    /// <c>Graph.Bookmarks</c> at index <paramref name="slot"/> (1..9). Mirrors
    /// pre-T15 SetBookmarkSlot.
    /// </summary>
    private void SetBookmarkSlot(int slot)
    {
        if (_vm is null || slot < 1 || slot > 9) return;
        var bookmarks = _vm.Graph.Bookmarks;
        while (bookmarks.Count < slot) bookmarks.Add(new Phoenix.Controls.Shared.Models.GraphBookmark());
        var bm = bookmarks[slot - 1];
        bm.Name       = $"Bookmark {slot}";
        bm.ViewOffset = new System.Drawing.Point((int)System.Math.Round(_vm.PanX), (int)System.Math.Round(_vm.PanY));
        bm.Zoom       = (float)_vm.Zoom;
        GlobalLogger.Log($"Bookmark {slot} set", "Architect.LogicCanvasView", LogLevel.System);
    }

    /// <summary>
    /// Alt+<paramref name="slot"/> — restores pan + zoom from the bookmark at
    /// <paramref name="slot"/> if set; logs an empty-slot notice otherwise.
    /// </summary>
    private void RecallBookmarkSlot(int slot)
    {
        if (_vm is null || slot < 1 || slot > 9) return;
        var bookmarks = _vm.Graph.Bookmarks;
        if (slot > bookmarks.Count)
        {
            GlobalLogger.Log($"Bookmark {slot} empty (Ctrl+1..9 sets, Alt+1..9 recalls)", "Architect.LogicCanvasView", LogLevel.System);
            return;
        }
        var bm = bookmarks[slot - 1];
        _vm.PanX = bm.ViewOffset.X;
        _vm.PanY = bm.ViewOffset.Y;
        _vm.Zoom = bm.Zoom > 0 ? bm.Zoom : 1.0;
        ApplyViewTransform();
    }

    /// <summary>
    /// 0.10.0 — discoverable bookmark legend. Opens a small Flyout
    /// anchored at the host top-centre listing all 9 slots, the Ctrl+1..9
    /// (set) and Alt+1..9 (recall) chord hints, and per-slot state ("set"
    /// vs "empty" plus the captured zoom%). Pre-P2 the bookmark chords
    /// lived only in source comments and the GlobalLogger output —
    /// nothing in the UI surfaced them. Called from
    /// <c>ArchitectChrome.MenuViewBookmarks.Click</c>.
    /// </summary>
    public void ShowBookmarkLegendFlyout()
    {
        if (_vm is null) return;
        var bookmarks = _vm.Graph.Bookmarks;

        var stack = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Spacing = 4,
            Padding = new Microsoft.UI.Xaml.Thickness(10),
            MinWidth = 320,
        };

        var header = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text       = "Bookmarks (Ctrl+1..9 set · Alt+1..9 recall)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize   = 12,
        };
        stack.Children.Add(header);

        for (int slot = 1; slot <= 9; slot++)
        {
            string label;
            if (slot > bookmarks.Count)
            {
                label = $"  {slot}: empty";
            }
            else
            {
                var bm = bookmarks[slot - 1];
                int zoomPct = (int)System.Math.Round((bm.Zoom > 0 ? bm.Zoom : 1.0) * 100);
                label = $"  {slot}: {bm.Name ?? $"Bookmark {slot}"} · {zoomPct}%";
            }
            stack.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text     = label,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily?)
                    Microsoft.UI.Xaml.Application.Current?.Resources["MonoFont"]
                    ?? new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11,
            });
        }

        var flyout = new Microsoft.UI.Xaml.Controls.Flyout
        {
            Content   = stack,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
        };
        flyout.Closed += (_, _) =>
        {
            try { Focus(Microsoft.UI.Xaml.FocusState.Programmatic); }
            catch { /* never break dismissal */ }
        };
        var anchor = new Windows.Foundation.Point(HostRoot.ActualWidth / 2, 16);
        flyout.ShowAt(HostRoot, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position           = anchor,
            ShowMode           = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            Placement          = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
        });
    }

    // ─── Auto-format graph (Ctrl+L) ───────────────────────────────────────

    /// <summary>
    /// Ctrl+L — BFS column layout, light port of pre-T15 AutoFormatGraph.
    /// Phase 1 partitions nodes by whether they have a Flow socket; phase 2
    /// assigns a column to each exec node from BFS over flow links (cycle
    /// fallback: topmost-leftmost as seed); phase 3 places data nodes one
    /// column left of their consumer (fixpoint, capped at 16 iterations);
    /// phase 4 buckets each column, sorts by Y then Title, places at
    /// (col*280, idx*140). Single undo snapshot.
    /// </summary>
    private void AutoFormatGraph()
    {
        if (_vm is null || _vm.Nodes.Count == 0) return;
        const double colStepX = 280.0;
        const double rowStepY = 140.0;

        PushUndo();

        // When the user has a multi-selection of >= 2 nodes,
        // scope the auto-format pass to just those nodes — mirrors UE
        // Blueprints' "Align/Straighten Connections" respecting selection.
        // Empty / single-node selections fall back to the whole-graph path
        // so the chord remains useful as a one-shot tidy.
        System.Collections.Generic.IList<NodeViewModel> targetNodes;
        bool wholeGraph = _vm.SelectedNodes.Count < 2;
        if (wholeGraph)
        {
            targetNodes = _vm.Nodes;
        }
        else
        {
            targetNodes = _vm.SelectedNodes.ToList();
        }

        var execNodes = new System.Collections.Generic.List<NodeViewModel>();
        var dataNodes = new System.Collections.Generic.List<NodeViewModel>();
        foreach (var n in targetNodes)
        {
            bool hasFlow = false;
            foreach (var s in n.Model.Sockets)
                if (Phoenix.Controls.Shared.Models.SocketTypeHelper.IsFlowPin(s)) { hasFlow = true; break; }
            (hasFlow ? execNodes : dataNodes).Add(n);
        }

        // Column assignment for exec spine — BFS from sources (no inbound flow link).
        var col = new System.Collections.Generic.Dictionary<string, int>();
        var queue = new System.Collections.Generic.Queue<NodeViewModel>();
        foreach (var n in execNodes)
        {
            if (!HasInboundFlow(n)) { col[n.Id] = 0; queue.Enqueue(n); }
        }
        if (queue.Count == 0 && execNodes.Count > 0)
        {
            // Cycle / no clear source — seed with topmost-leftmost.
            var seed = execNodes[0];
            foreach (var n in execNodes)
                if (n.Y < seed.Y || (n.Y == seed.Y && n.X < seed.X)) seed = n;
            col[seed.Id] = 0;
            queue.Enqueue(seed);
        }
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            int curCol = col[n.Id];
            foreach (var lk in _vm.Graph.Links)
            {
                if (lk.FromNodeId != n.Model.Id) continue;
                var to = FindVm(lk.ToNodeId);
                if (to is null) continue;
                int next = curCol + 1;
                if (!col.TryGetValue(to.Id, out int existing) || next > existing)
                {
                    col[to.Id] = next;
                    queue.Enqueue(to);
                }
            }
        }

        // Data nodes propagate left of consumers, fixpoint capped at 16.
        for (int iter = 0; iter < 16; iter++)
        {
            bool changed = false;
            foreach (var n in dataNodes)
            {
                int? minConsumer = null;
                foreach (var lk in _vm.Graph.Links)
                {
                    if (lk.FromNodeId != n.Model.Id) continue;
                    var to = FindVm(lk.ToNodeId);
                    if (to is null) continue;
                    if (col.TryGetValue(to.Id, out int c))
                        minConsumer = minConsumer is null ? c : System.Math.Min(minConsumer.Value, c);
                }
                int target = minConsumer is null ? 0 : minConsumer.Value - 1;
                if (!col.TryGetValue(n.Id, out int prev) || prev != target)
                {
                    col[n.Id] = target;
                    changed = true;
                }
            }
            if (!changed) break;
        }

        // Bucket per column, sort by current Y then Title, place.
        // Only place nodes that were in the original scope —
        // selection-scoped runs must leave the unselected nodes alone.
        var buckets = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<NodeViewModel>>();
        foreach (var n in targetNodes)
        {
            int c = col.TryGetValue(n.Id, out int v) ? v : 0;
            if (!buckets.TryGetValue(c, out var list))
            {
                list = new System.Collections.Generic.List<NodeViewModel>();
                buckets[c] = list;
            }
            list.Add(n);
        }

        int xOffset = 0;
        int minCol = int.MaxValue;
        foreach (var k in buckets.Keys) if (k < minCol) minCol = k;
        if (minCol == int.MaxValue) minCol = 0;
        xOffset = -minCol;

        // Anchor the placement to the top-left of the original
        // selection (when scoped) so a Ctrl+L over a multi-selection doesn't
        // catapult the formatted block to canvas origin (0,0); whole-graph
        // runs continue to land at origin since the un-anchored xOffset
        // already places col=0 there.
        double anchorX = 0, anchorY = 0;
        if (!wholeGraph && targetNodes.Count > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            foreach (var n in targetNodes)
            {
                if (n.X < minX) minX = n.X;
                if (n.Y < minY) minY = n.Y;
            }
            anchorX = minX;
            anchorY = minY;
        }

        var orderedCols = new System.Collections.Generic.List<int>(buckets.Keys);
        orderedCols.Sort();
        foreach (var c in orderedCols)
        {
            var bucket = buckets[c];
            bucket.Sort((a, b) =>
            {
                int dy = a.Y.CompareTo(b.Y);
                if (dy != 0) return dy;
                return string.Compare(a.Title, b.Title, System.StringComparison.OrdinalIgnoreCase);
            });
            for (int i = 0; i < bucket.Count; i++)
            {
                var n = bucket[i];
                int newX = (int)System.Math.Round(anchorX + (c + xOffset) * colStepX);
                int newY = (int)System.Math.Round(anchorY + i * rowStepY);
                n.Translate(newX - n.X, newY - n.Y);
            }
        }

        _vm.OnGraphMutated();
    }

    private bool HasInboundFlow(NodeViewModel n)
    {
        if (_vm is null) return false;
        foreach (var lk in _vm.Graph.Links)
        {
            if (lk.ToNodeId != n.Model.Id) continue;
            var s = n.Model.Sockets.Find(x => x.Id == lk.ToSocketId);
            if (s is not null && Phoenix.Controls.Shared.Models.SocketTypeHelper.IsFlowPin(s)) return true;
        }
        return false;
    }

    private NodeViewModel? FindVm(string nodeId)
    {
        if (_vm is null) return null;
        foreach (var n in _vm.Nodes) if (n.Model.Id == nodeId) return n;
        return null;
    }

    /// <summary>
    /// 0.10.0 — Shift-fine zoom step. Returns 1.025 when Shift is
    /// held at the time the chord fires, otherwise the standard 1.1 step.
    /// Mirrors the per-detent factor selection inside the wheel-zoom
    /// path in <c>LogicCanvasView.Pointer.cs</c>.
    /// </summary>
    private static double KeyboardZoomStep()
    {
        bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                      & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        return shift ? 1.025 : 1.1;
    }

    /// <summary>
    /// Multiplicative zoom around the cursor when the pointer is currently
    /// inside the host (matches the wheel-zoom anchor math at
    /// LogicCanvasView.xaml.cs:520-524) or the host viewport centre as a
    /// fallback when the pointer is outside / has never entered (e.g.
    /// keyboard-only invocation immediately after window focus). Clamp
    /// range (0.2 .. 4.0) mirrors PointerWheel + the VM Zoom setter.
    /// </summary>
    private void ZoomKeyboard(double factor)
    {
        if (_vm is null) return;
        double oldZoom = Math.Max(_vm.Zoom, 0.0001);
        double newZoom = System.Math.Clamp(oldZoom * factor, 0.2, 4.0);
        if (System.Math.Abs(newZoom - oldZoom) < 1e-6) return;

        double anchorX, anchorY;
        // Anchor on the cursor when the pointer is over the canvas
        // so Ctrl+0 / Ctrl++ / Ctrl+- behave the same as wheel-zoom from the
        // user's POV (the thing under the cursor stays put). When the
        // pointer is outside the host, fall back to the viewport centre.
        // Bounds-check _lastHostPoint against the current ActualWidth /
        // Height so a stale value from a previous resize can't shoot the
        // anchor off-screen.
        if (_pointerOverHost
            && _lastHostPoint.X >= 0 && _lastHostPoint.X <= HostRoot.ActualWidth
            && _lastHostPoint.Y >= 0 && _lastHostPoint.Y <= HostRoot.ActualHeight)
        {
            anchorX = _lastHostPoint.X;
            anchorY = _lastHostPoint.Y;
        }
        else
        {
            anchorX = HostRoot.ActualWidth  / 2;
            anchorY = HostRoot.ActualHeight / 2;
        }

        double canvasX = (anchorX - _vm.PanX) / oldZoom;
        double canvasY = (anchorY - _vm.PanY) / oldZoom;
        _vm.Zoom = newZoom;
        _vm.PanX = anchorX - canvasX * newZoom;
        _vm.PanY = anchorY - canvasY * newZoom;
        ApplyViewTransform();
    }

    /// <summary>
    /// Pointer-over-canvas tracking. Wired on HostRoot's
    /// PointerEntered / PointerExited from OnLoaded; consumed by
    /// ZoomKeyboard to pick the anchor (cursor vs viewport centre).
    /// </summary>
    internal void OnHostPointerEntered(object sender, PointerRoutedEventArgs e) => _pointerOverHost = true;

    internal void OnHostPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverHost = false;
        // Clear the frame-edge
        // hover cursor when the pointer leaves the canvas. Without this the
        // last directional shape we set sticks across the chrome / sibling
        // panels until the user re-enters the canvas.
        UpdateFrameEdgeHoverCursor(source: null);
        // Drop the GPU-canvas hover highlight
        // when the pointer leaves the canvas.
        if (_useImmediateMode) ClearImmediateHover();
    }
}
