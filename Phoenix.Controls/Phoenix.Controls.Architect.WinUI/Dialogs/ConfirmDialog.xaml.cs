using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Architect.WinUI.Dialogs;

public enum ConfirmDialogButton
{
    /// <summary>Default focus on the primary (destructive / commit) button.</summary>
    Primary,
    /// <summary>Default focus on the close button — safer for genuinely destructive prompts.</summary>
    Close,
}

public sealed partial class ConfirmDialog : ContentDialog
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static ConfirmDialog ForMessage(XamlRoot root, string title, string message,
                                           string yesText = "Yes", string noText = "Cancel")
        => ForMessage(root, title, message, ConfirmDialogButton.Primary, yesText, noText);

    /// <summary>
    /// Factory with explicit default-button selection. Use
    /// <see cref="ConfirmDialogButton.Close"/> for irreversible actions
    /// (delete, overwrite) so the safer choice is what Enter triggers.
    /// </summary>
    public static ConfirmDialog ForMessage(XamlRoot root, string title, string message,
                                           ConfirmDialogButton defaultButton,
                                           string yesText = "Yes", string noText = "Cancel")
    {
        var d = new ConfirmDialog
        {
            XamlRoot = root,
            Title = title,
            PrimaryButtonText = yesText,
            CloseButtonText = noText,
            DefaultButton = defaultButton == ConfirmDialogButton.Close
                ? Microsoft.UI.Xaml.Controls.ContentDialogButton.Close
                : Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        d.MessageText.Text = message;
        return d;
    }

    /// <summary>
    /// 0.10.0 UX P2 — danger variant. Flips the warning header on, defaults
    /// the close button (safer choice on Enter), and reads
    /// <paramref name="destructiveVerb"/> as the primary button label so the
    /// dialog never defaults to a generic "Yes" for destructive intent.
    /// </summary>
    public static ConfirmDialog ForDanger(XamlRoot root, string title, string message,
                                          string destructiveVerb, string cancelText = "Cancel")
    {
        var d = new ConfirmDialog
        {
            XamlRoot = root,
            Title = title,
            PrimaryButtonText = destructiveVerb,
            CloseButtonText = cancelText,
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
        };
        d.MessageText.Text = message;
        d.DangerHeader.Visibility = Visibility.Visible;
        return d;
    }
}
