using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Restores the <c>DataContext</c> on rows realized by an <see cref="ItemsRepeater"/>.
///
/// ★ WHY THIS EXISTS — the defect it fixes is invisible at build time.
///
/// WinUI's repeater sets a realized element's DataContext ONLY when the
/// DataTemplate carries no compiled bindings. From
/// <c>dev/Repeater/ViewManager.cpp</c> (<c>PrepareElement</c>):
///
/// <code>
///   if (auto const extension = element.try_as&lt;IDataTemplateComponent&gt;())
///   {
///       extension.Recycle();
///       extension.ProcessBindings(data, index, 0 /* currentPhase */, nextPhase);
///   }
///   else
///   {
///       // Set data context only if no x:Bind was used.
///       elementAsFE.DataContext(elementDataContext);
///       virtInfo-&gt;MustClearDataContext(true);
///   }
/// </code>
///
/// Every row template in this app uses <c>x:Bind</c> (house style), so every
/// repeater-realized row shipped with a NULL DataContext — while the rows
/// themselves rendered perfectly, because <c>ProcessBindings</c> feeds x:Bind
/// directly. The house row-click idiom
/// (<c>sender is FrameworkElement { DataContext: SomeRowVm row }</c>) therefore
/// pattern-matched null and returned silently: buttons that looked alive and did
/// nothing, with no exception, no log line and no failing test. Majo reported it
/// as "in loyalty those buttons are dead" — the Loyalty section selector was one
/// of ~35 repeaters carrying the same defect.
///
/// <c>ItemsControl</c> and <c>ListView</c> are NOT affected (they set the
/// container's DataContext unconditionally), which is why the Loyalty Minigames /
/// Rewards lists — deliberately kept as ItemsControls for their visual-tree walk —
/// kept working while the chips beside them did not.
///
/// Set <c>Supply="True"</c> on every <see cref="ItemsRepeater"/> in the app;
/// <c>ItemsRepeaterRowDataContextTests</c> fails the build if one is missed.
/// Deliberately explicit rather than an implicit
/// <c>&lt;Style TargetType="ItemsRepeater"&gt;</c>: a style that silently fails to
/// apply reintroduces exactly this class of invisible breakage, and the compiler
/// verifies an attribute.
///
/// ORDERING NOTE. This handler subscribes when the attached property is set,
/// during <c>InitializeComponent</c>'s XAML parse — which runs BEFORE any
/// constructor-BODY subscription in the owning UserControl, and multicast
/// handlers fire in subscription order. So this handler normally runs FIRST.
/// But "normally" is not a contract — a handler wired before
/// <c>InitializeComponent</c>, or on a repeater created in code, would invert
/// it — so the rule stands on its own: any <c>ElementPrepared</c> handler must
/// resolve its row from <c>args.Index</c>, never from DataContext; interaction
/// handlers (Click / Tapped / RightTapped) are safe because they fire long
/// after realization.
/// </summary>
public static class RowDataContext
{
    public static readonly DependencyProperty SupplyProperty =
        DependencyProperty.RegisterAttached(
            "Supply", typeof(bool), typeof(RowDataContext),
            new PropertyMetadata(false, OnSupplyChanged));

    public static bool GetSupply(DependencyObject element) => (bool)element.GetValue(SupplyProperty);

    public static void SetSupply(DependencyObject element, bool value) => element.SetValue(SupplyProperty, value);

    private static void OnSupplyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsRepeater repeater) return;

        // Idempotent: unhook first so a re-set (or a template re-parse) can't
        // stack two handlers on the same repeater.
        repeater.ElementPrepared -= OnElementPrepared;
        if (e.NewValue is true) repeater.ElementPrepared += OnElementPrepared;
    }

    private static void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is not FrameworkElement row) return;

        // ItemsSourceView is null until ItemsSource is assigned, and the index is
        // bounds-checked because a recycled element can be prepared against a
        // source that shrank in the same frame.
        var view = sender.ItemsSourceView;
        if (view is null) return;
        int index = args.Index;
        if (index < 0 || index >= view.Count) return;

        try
        {
            row.DataContext = view.GetAt(index);
        }
        catch (System.Exception ex)
        {
            // A faulting source view must never take the panel down with it —
            // the row keeps rendering (x:Bind already ran), it just stays inert.
            GlobalLogger.Error("RowDataContext", $"could not supply DataContext for row {index}", ex);
        }
    }

    // NOTE ON RECYCLING: WinUI clears the DataContext it set itself
    // (MustClearDataContext), but never one we set — deliberate. ElementClearing
    // handlers read the OUTGOING row's DataContext to unregister it (ScriptView
    // does exactly that), and the next ElementPrepared overwrites the value
    // before the element is shown again, so a stale value is never observable.
}
