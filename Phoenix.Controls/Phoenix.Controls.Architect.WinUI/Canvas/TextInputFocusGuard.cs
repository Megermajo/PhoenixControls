using Microsoft.UI.Xaml;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Single source for the "is the user currently typing into a text field?"
/// test. Focus-based, so it covers every text surface in the window — canvas
/// value pills and rename boxes, inspector fields, rail rename boxes, the
/// spawn-palette search box, databank cells — without each surface having to
/// opt in. Consumed by the canvas keyboard guard
/// (<c>LogicCanvasView.OnHostKeyDown</c>) AND by the chrome / sibling-window
/// menu accelerators: a WinUI <c>KeyboardAccelerator</c> is window-scoped and
/// fires regardless of which control holds keyboard focus, so without this
/// check typing plain letters like "f" or "c" into a pill also framed the
/// viewport / dropped a comment frame, and Ctrl+Z inside a pill ran the GRAPH
/// undo instead of the text box's own undo.
/// </summary>
internal static class TextInputFocusGuard
{
    internal static bool IsTextInputFocused(XamlRoot? root)
    {
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
}
