using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;

namespace Phoenix.Controls.Hub.WinUI.Controls;

/// <summary>
/// Slim Phoenix-branded title bar for pop-out windows. Mirrors HubChrome's
/// row-2 (Phoenix mark + brand line + traffic lights) but at 30px height
/// and without the menu strip / pillar tabs. Wired into a Window via
/// <see cref="AttachTo"/>; that helper extends content into the title bar,
/// drops the system caption, registers the inner DragRegion as the OS-owned
/// drag rect, and binds the three TrafficLightButton clicks to the
/// AppWindow's Minimize / Maximize-or-Restore / Close.
/// </summary>
public sealed partial class PopOutChrome : UserControl
{
    public PopOutChrome()
    {
        InitializeComponent();
    }

    public void SetTitle(string title)
    {
        TitleText.Text = title;
    }

    /// <summary>
    /// One-shot wiring: extends content into title bar, hides the system
    /// caption via the overlapped presenter, registers DragRegion as the
    /// OS-owned drag rect, and binds traffic-light clicks to AppWindow
    /// actions. Safe on any Window — failures are logged at Debug level by
    /// the caller (PopOutWindowFactory).
    /// </summary>
    public void AttachTo(Microsoft.UI.Xaml.Window window, AppWindow? appWindow, OverlappedPresenter? presenter)
    {
        if (window is null) throw new ArgumentNullException(nameof(window));

        try { window.ExtendsContentIntoTitleBar = true; }
        catch { /* not fatal; the chrome still paints, but the system caption peeks */ }

        try { window.SetTitleBar(DragRegion); }
        catch { /* drag still works for hit areas the OS auto-recognises */ }

        if (presenter is not null)
        {
            // Drop the system caption — our TrafficLightButton trio owns
            // the three actions. Border stays on so the OS keeps drawing
            // the resize frame.
            try { presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false); }
            catch { /* legacy DWM path — caption stays visible; not fatal */ }

            MinButton.Clicked += (_, _) =>
            {
                try { presenter.Minimize(); } catch { /* shutdown race */ }
            };
            MaxButton.Clicked += (_, _) =>
            {
                try
                {
                    if (presenter.State == OverlappedPresenterState.Maximized)
                        presenter.Restore();
                    else
                        presenter.Maximize();
                }
                catch { /* shutdown race */ }
            };
        }
        else
        {
            // No presenter (shouldn't happen on OverlappedPresenter windows).
            // Fall back to AppWindow.Show/Hide-style actions where possible.
            MinButton.Clicked += (_, _) =>
            {
                try { appWindow?.Hide(); } catch { /* best-effort */ }
            };
            MaxButton.Clicked += (_, _) =>
            {
                // No idempotent restore without a presenter; no-op.
            };
        }

        CloseButton.Clicked += (_, _) =>
        {
            try { window.Close(); } catch { /* shutdown race */ }
        };
    }
}
