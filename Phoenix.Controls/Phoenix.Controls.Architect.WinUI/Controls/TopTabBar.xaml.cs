using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Phoenix.Controls.Architect.WinUI.Controls;

public enum ArchitectTab
{
    Logic,
    Databank,
}

public sealed partial class TopTabBar : UserControl
{
    private ArchitectTab _selected = ArchitectTab.Logic;

    public ArchitectTab SelectedTab => _selected;

    public event EventHandler<ArchitectTab>? TabChanged;

    public TopTabBar()
    {
        InitializeComponent();
        ApplyVisuals();
    }

    private void OnLogicTapped(object sender, TappedRoutedEventArgs e) => Select(ArchitectTab.Logic);

    private void OnDatabankTapped(object sender, TappedRoutedEventArgs e) => Select(ArchitectTab.Databank);

    private void Select(ArchitectTab tab)
    {
        if (tab == _selected) return;
        _selected = tab;
        ApplyVisuals();
        TabChanged?.Invoke(this, _selected);
    }

    /// <summary>
    /// 0.10.0 (arch-ux-state #3) — public entry used by
    /// MainView.ApplyPersistedLayoutAndState to restore the last-active tab
    /// without simulating a tap. Fires TabChanged the same way the user-click
    /// path does so downstream subscribers (MainView.OnTabChanged) run.
    /// </summary>
    public void SelectTab(ArchitectTab tab) => Select(tab);

    private void ApplyVisuals()
    {
        StyleTab(LogicTab, LogicLabel, _selected == ArchitectTab.Logic);
        StyleTab(DatabankTab, DatabankLabel, _selected == ArchitectTab.Databank);
    }

    private void StyleTab(Border tab, TextBlock label, bool active)
    {
        if (active)
        {
            tab.Background = Resource("CoalRaisedBrush");
            tab.BorderBrush = Resource("EmberDeepBrush");
            tab.BorderThickness = new Thickness(1, 1, 1, 0);
            label.Foreground = Resource("CoalPaperBrush");
        }
        else
        {
            tab.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            tab.BorderBrush = Resource("CoalCardBrush");
            tab.BorderThickness = new Thickness(1, 1, 1, 1);
            label.Foreground = Resource("CoalSecondaryTextBrush");
        }
    }

    // Defensive theme-key lookup mirroring LeftRail's pattern — a missing key
    // (theme dictionary swap, hot reload edge) used to throw an
    // InvalidCastException from the bare `(Brush)Resources[key]`. Falling back
    // to a neutral brush keeps the chrome rendering rather than crashing the
    // whole Architect surface.
    private static readonly Brush s_resourceFallback =
        new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private static Brush Resource(string key)
    {
        try { return (Application.Current.Resources[key] as Brush) ?? s_resourceFallback; }
        catch { return s_resourceFallback; }
    }
}
