using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.ChatPanel;

/// <summary>
/// Crash-proof setter for the chat role-badge <see cref="PathIcon"/>.Data.
///
/// A <see cref="Geometry"/> is a DependencyObject and can be parented to only one
/// element at a time. In the virtualized chat <c>ItemsRepeater</c>, a geometry that
/// still carries a parent — a recycled container, or a lingering XamlReader-wrapper
/// context — makes <c>PathIcon.set_Data</c> throw
/// <see cref="System.ArgumentException"/> "Value does not fall within the expected
/// range." Previously that escaped to <c>App.UnhandledException</c> and crashed
/// Hub.WinUI (SystemHistory 2026-06-03, recurring class first seen 2026-05-22).
///
/// Binding <c>local:ChatBadgeData.SafeData</c> instead of <c>Data</c> routes the
/// assignment through the guarded handler below, so a bad geometry degrades to
/// "no badge for that one row" rather than a fatal crash. Defense-in-depth paired
/// with <c>ChatRowVm.BadgeGeometry</c> returning a FRESH instance per access (so the
/// normal path never hands set_Data a parented geometry in the first place).
/// </summary>
public static class ChatBadgeData
{
    public static readonly DependencyProperty SafeDataProperty =
        DependencyProperty.RegisterAttached(
            "SafeData",
            typeof(Geometry),
            typeof(ChatBadgeData),
            new PropertyMetadata(null, OnSafeDataChanged));

    public static Geometry? GetSafeData(DependencyObject obj) => (Geometry?)obj.GetValue(SafeDataProperty);
    public static void SetSafeData(DependencyObject obj, Geometry? value) => obj.SetValue(SafeDataProperty, value);

    private static void OnSafeDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PathIcon icon) return;
        try
        {
            icon.Data = e.NewValue as Geometry;
        }
        catch (System.Exception ex)
        {
            // Parented / invalid geometry — show no badge for this row instead of
            // letting the exception escape to App.UnhandledException and crash Hub.
            try { icon.Data = null; } catch { /* best-effort */ }
            GlobalLogger.Error("ChatBadgeData",
                "PathIcon.Data rejected a badge geometry; badge skipped for this row", ex);
        }
    }
}
