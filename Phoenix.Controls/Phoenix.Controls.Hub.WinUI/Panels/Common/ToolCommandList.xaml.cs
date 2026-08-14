using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Localization;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

/// <summary>
/// Read-only "CHAT COMMANDS" section for the Pre-Build tool pages — renders the
/// <see cref="ToolCommandInfo"/> list <see cref="BuiltInCommandCatalog"/> derives
/// from a tool's config. See ToolCommandList.xaml for the geometry rationale (why
/// two of the three columns are pixel-fixed, why there is no ScrollViewer and no
/// height cap, and why a disabled row carries two signals rather than one).
///
/// <para>Typical use — a section of a page's detail scroller, fed from the page's
/// own WORKING config so the list reflects an edit the streamer has not saved yet:
/// <code>
/// &lt;cmn:ToolCommandList Commands="{x:Bind ViewModel.ChatCommands, Mode=OneWay}"
///                        AutomationProperties.Name="Loyalty chat commands" /&gt;
/// </code>
/// with the VM property built as
/// <c>BuiltInCommandCatalog.ForLoyalty(_working)</c> and re-raised whenever a verb,
/// a role tick or an enable changes.</para>
///
/// <para><b>Assign a fresh list, never mutate one in place.</b> A dependency
/// property raises its change callback on reference inequality, so re-assigning the
/// SAME list instance after editing its contents updates nothing. Every
/// <c>BuiltInCommandCatalog.For*</c> call allocates a new list, so the natural usage
/// — call the catalog again and assign the result — always refreshes. This is the
/// opposite of <see cref="ToolActivityFeed"/>'s contract, and deliberately so: that
/// one streams appended rows into a live ObservableCollection, while this one is a
/// projection of config that is cheapest to rebuild whole.</para>
///
/// <para>Set the accessible name on this control, not inside it — the inner repeater
/// deliberately carries none so the host's name governs.</para>
/// </summary>
public sealed partial class ToolCommandList : UserControl
{
    public ToolCommandList()
    {
        InitializeComponent();
        // An unset Commands DP means "no rows", which is the same state as an empty
        // list and must look the same. The change callback never runs for the
        // initial default, so the empty state is applied once here instead.
        ApplyRows(null);

        // The DEFAULT eyebrow is localized HERE rather than with a
        // loc:Localize.Key on TitleText, and the difference is load-bearing: the
        // attached property resolves at Loaded, which is AFTER a host has applied
        // its own Title attribute, so it would silently overwrite an override
        // ("MODERATOR COMMANDS") with the generic translation. A constructor set
        // runs BEFORE the host's attribute, so an override still wins — and the
        // host localizes it through its own key.
        Title = Localizer.T("panel.common.commands.title", Title);
    }

    public static readonly DependencyProperty CommandsProperty =
        DependencyProperty.Register(nameof(Commands), typeof(IReadOnlyList<ToolCommandInfo>),
            typeof(ToolCommandList), new PropertyMetadata(null, OnCommandsChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ToolCommandList),
            new PropertyMetadata("CHAT COMMANDS", OnTitleChanged));

    /// <summary>
    /// The verbs to render, normally a <see cref="BuiltInCommandCatalog"/> result.
    /// Null and empty both render the empty-state sentence.
    /// </summary>
    public IReadOnlyList<ToolCommandInfo>? Commands
    {
        get => (IReadOnlyList<ToolCommandInfo>?)GetValue(CommandsProperty);
        set => SetValue(CommandsProperty, value);
    }

    /// <summary>
    /// Header eyebrow. Defaults to "CHAT COMMANDS" and should stay that on every
    /// tool page — the block is meant to be findable by the same words everywhere.
    /// Override it only where the section genuinely covers something narrower.
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnCommandsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolCommandList c) c.ApplyRows(e.NewValue as IReadOnlyList<ToolCommandInfo>);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolCommandList c) c.TitleText.Text = e.NewValue as string ?? "";
    }

    /// <summary>
    /// Projects the catalogue rows onto the row view-models the template binds, and
    /// swaps the empty state in when there is nothing to show.
    /// </summary>
    private void ApplyRows(IReadOnlyList<ToolCommandInfo>? commands)
    {
        var rows = new List<ToolCommandRowVm>(commands?.Count ?? 0);
        if (commands is not null)
            foreach (var command in commands)
                rows.Add(new ToolCommandRowVm(command));

        Rows.ItemsSource = rows;
        Rows.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// One rendered command row.
/// </summary>
/// <remarks>
/// <para>A class rather than binding the <see cref="ToolCommandInfo"/> record struct
/// directly, for two reasons. The template needs three DERIVED values —
/// the bang-prefixed verb, whether the usage line adds anything, and the dimming —
/// and computing them here keeps the markup free of converters. And every property
/// is settled at construction, so the row is immutable and needs no change
/// notification: the list is rebuilt wholesale whenever the config changes (see
/// <see cref="ToolCommandList.Commands"/>), which is why <c>x:Bind</c>'s default
/// OneTime mode is correct throughout the template.</para>
/// </remarks>
public sealed class ToolCommandRowVm
{
    public ToolCommandRowVm(ToolCommandInfo info)
    {
        Verb = info.Verb ?? "";
        Label = info.Label ?? "";
        Roles = info.Roles ?? "";
        Shape = info.Shape ?? "";

        // The catalogue stores the verb canonically — no bang — because that is what
        // the parsers compare. Chat shows the bang, so the panel does too.
        DisplayVerb = "!" + Verb;

        // The usage line earns its space only when the command takes arguments. For a
        // bare command the shape IS the display verb, and repeating it under the label
        // would put the same six characters on the row twice.
        ShapeVisibility = Shape.Length > 0 && Shape != DisplayVerb
            ? Visibility.Visible
            : Visibility.Collapsed;

        RowOpacity = info.Enabled ? 1.0 : 0.45;
        OffVisibility = info.Enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The canonical verb, exactly as the tool's parser compares it.</summary>
    public string Verb { get; }

    /// <summary>The verb as chat shows it — the canonical verb with its bang.</summary>
    public string DisplayVerb { get; }

    public string Label { get; }

    public string Roles { get; }

    /// <summary>Full usage, e.g. "!give &lt;user&gt; &lt;amount&gt;".</summary>
    public string Shape { get; }

    /// <summary>Collapsed when <see cref="Shape"/> would only repeat
    /// <see cref="DisplayVerb"/>.</summary>
    public Visibility ShapeVisibility { get; }

    /// <summary>Dims a row whose per-command enable is off. Paired with
    /// <see cref="OffVisibility"/> so the state is never carried by contrast
    /// alone.</summary>
    public double RowOpacity { get; }

    public Visibility OffVisibility { get; }
}
