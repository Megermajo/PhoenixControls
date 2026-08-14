using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Shared.Localization;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Visualist.WinUI.Localization;

/// <summary>
/// Markup-side localization: one attached attribute beside a literal, no
/// code-behind, no <c>x:Name</c>.
///
/// <code>
///   &lt;TextBlock Text="Entries" loc:Localize.Key="panel.giveaway.entries.title" /&gt;
/// </code>
///
/// <para><b>Why an attached property and not the house code-behind pass.</b>
/// The established pattern (<c>SettingsDialog.xaml.cs</c>) names every element
/// and assigns <c>Localizer.T(...)</c> to it from <c>OnLoaded</c>. That works at
/// 101 strings in one file; the Pre-Build panels hold ~1,100 across fourteen,
/// and two of them (<c>SongRequestView</c>, <c>LoyaltyView</c>) carry 0 and 1
/// named elements — the house pattern cannot even start there without mass
/// renaming. This costs one attribute per string and nothing else.</para>
///
/// <para><b>The English literal stays and is the fallback.</b> Every setter
/// resolves through <c>Localizer.T(key, currentValue)</c>, so a key that is
/// missing from every bundle renders the original English text rather than
/// <c>[panel.x.y]</c>. That is the same contract <c>PanelHeader.TitleKey</c> has
/// shipped with, and it means a botched key is a translation gap, never a
/// visible defect.</para>
///
/// <para><b>Resolved at Loaded, not at parse.</b> XAML applies attributes in
/// document order, so writing the text at property-change time would lose a race
/// with a <c>Text=</c> that follows the key attribute in the same tag. Loaded is
/// also the point where <c>Localizer.Init</c> is guaranteed to have run — Hub's
/// splash pre-boot calls it before <c>MainWindow</c> exists, but a control
/// constructed earlier (test rigs, deferred init) would otherwise bake in
/// placeholders. Elements set from code AFTER load are applied immediately.</para>
///
/// <para><b>★ This file is deliberately duplicated in all three pillars</b>
/// (<c>Phoenix.Controls.Hub.WinUI</c>, <c>Phoenix.Controls.Architect.WinUI</c>,
/// <c>Phoenix.Controls.Visualist.WinUI</c>) and must stay byte-identical apart
/// from its namespace line — <c>LocalizeAttachedPropertyTests</c> fails the
/// build when the copies drift. It is NOT lifted into
/// <c>Phoenix.Controls.Shared.WinUI</c> because that project carries no
/// WindowsAppSDK reference by design (its csproj states the reason: it must
/// build on a stand-alone .NET SDK), and a <c>DependencyProperty</c> needs
/// <c>Microsoft.UI.Xaml</c>. Per-pillar UI code is also the standing rule.</para>
/// </summary>
public static class Localize
{
    // ─────────────────────────────────────────────────────────────────────
    //  Attached properties
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Key for the element's primary text slot — <c>TextBlock.Text</c>,
    /// <c>ContentControl.Content</c>, <c>ToggleSwitch.Header</c>,
    /// <c>ContentDialog.Title</c>, or whatever <see cref="PropertyProperty"/>
    /// names. See <see cref="ResolveTextMember"/> for the full resolution order.
    /// </summary>
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetKey(DependencyObject o) => (string)o.GetValue(KeyProperty);
    public static void SetKey(DependencyObject o, string value) => o.SetValue(KeyProperty, value);

    /// <summary>
    /// Names the member <see cref="KeyProperty"/> writes, for controls the
    /// default map does not know — a custom control's <c>Caption</c>,
    /// <c>Eyebrow</c>, <c>Label</c>. Resolves a
    /// <c>&lt;Name&gt;Property</c> DependencyProperty first, then a plain CLR
    /// string property (which is what <c>ToolPageHeader.Title</c> is).
    /// </summary>
    public static readonly DependencyProperty PropertyProperty =
        DependencyProperty.RegisterAttached(
            "Property", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetProperty(DependencyObject o) => (string)o.GetValue(PropertyProperty);
    public static void SetProperty(DependencyObject o, string value) => o.SetValue(PropertyProperty, value);

    /// <summary>Key for <c>Header</c> (TextBox / ComboBox / NumberBox / ToggleSwitch / Expander).</summary>
    public static readonly DependencyProperty HeaderKeyProperty =
        DependencyProperty.RegisterAttached(
            "HeaderKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetHeaderKey(DependencyObject o) => (string)o.GetValue(HeaderKeyProperty);
    public static void SetHeaderKey(DependencyObject o, string value) => o.SetValue(HeaderKeyProperty, value);

    /// <summary>
    /// Key for the element's SECOND text slot — <c>InfoBar.Message</c> under its
    /// <c>Title</c>, <c>RailSection.Hint</c> under its <c>Title</c>. Those
    /// controls carry two independent strings, and <see cref="KeyProperty"/>
    /// drives exactly one of them (the first <see cref="ProbeOrder"/> match,
    /// which is the title), so the second one had to be assigned from
    /// code-behind — the pattern this class exists to retire. Additive: nothing
    /// about how <see cref="KeyProperty"/> resolves changes.
    ///
    /// <para>Resolves <c>Message</c> first, then <c>Hint</c> — the two spellings
    /// this repo uses for "the supporting line under the title". See
    /// <see cref="ApplyToMessageSlot"/>.</para>
    /// </summary>
    public static readonly DependencyProperty MessageKeyProperty =
        DependencyProperty.RegisterAttached(
            "MessageKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetMessageKey(DependencyObject o) => (string)o.GetValue(MessageKeyProperty);
    public static void SetMessageKey(DependencyObject o, string value) => o.SetValue(MessageKeyProperty, value);

    /// <summary>Key for <c>PlaceholderText</c> (TextBox / AutoSuggestBox / ComboBox / NumberBox).</summary>
    public static readonly DependencyProperty PlaceholderKeyProperty =
        DependencyProperty.RegisterAttached(
            "PlaceholderKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetPlaceholderKey(DependencyObject o) => (string)o.GetValue(PlaceholderKeyProperty);
    public static void SetPlaceholderKey(DependencyObject o, string value) => o.SetValue(PlaceholderKeyProperty, value);

    /// <summary>Key for <c>ToolTipService.ToolTip</c>. Only a string tooltip is
    /// localized. A tooltip already holding anything else — a <c>ToolTip</c>
    /// element, a panel, any composed content — is left exactly as authored and
    /// logged once: overwriting it would trade a designed tooltip for a line of
    /// text, and since a non-string leaves nothing to fall back to, a key that
    /// resolved nowhere would replace it with the literal <c>[key]</c>. Localize
    /// a rich tooltip from inside its own content instead.</summary>
    public static readonly DependencyProperty TooltipKeyProperty =
        DependencyProperty.RegisterAttached(
            "TooltipKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetTooltipKey(DependencyObject o) => (string)o.GetValue(TooltipKeyProperty);
    public static void SetTooltipKey(DependencyObject o, string value) => o.SetValue(TooltipKeyProperty, value);

    /// <summary>
    /// Key for <c>AutomationProperties.Name</c> — the screen-reader label.
    /// In scope by explicit decision: 391 of these ship as English literals and
    /// none were localized anywhere before this pass, so a blind German user got
    /// an entirely English screen reader over a translated UI.
    /// </summary>
    public static readonly DependencyProperty AutomationKeyProperty =
        DependencyProperty.RegisterAttached(
            "AutomationKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetAutomationKey(DependencyObject o) => (string)o.GetValue(AutomationKeyProperty);
    public static void SetAutomationKey(DependencyObject o, string value) => o.SetValue(AutomationKeyProperty, value);

    /// <summary>Key for <c>AutomationProperties.HelpText</c>.</summary>
    public static readonly DependencyProperty HelpTextKeyProperty =
        DependencyProperty.RegisterAttached(
            "HelpTextKey", typeof(string), typeof(Localize),
            new PropertyMetadata(string.Empty, OnKeyChanged));

    public static string GetHelpTextKey(DependencyObject o) => (string)o.GetValue(HelpTextKeyProperty);
    public static void SetHelpTextKey(DependencyObject o, string value) => o.SetValue(HelpTextKeyProperty, value);

    /// <summary>Internal — marks an element whose Loaded handler is already
    /// attached, so setting three keys on one element hooks the event once.</summary>
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(Localize), new PropertyMetadata(false));

    // ─────────────────────────────────────────────────────────────────────
    //  Application
    // ─────────────────────────────────────────────────────────────────────

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;

        if (fe.IsLoaded)
        {
            // Set from code after the element joined the tree — nothing can
            // overwrite us, so resolve now instead of waiting for a reload
            // that may never come.
            Apply(fe);
            return;
        }

        if ((bool)fe.GetValue(HookedProperty)) return;
        fe.SetValue(HookedProperty, true);
        fe.Loaded += OnElementLoaded;
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) Apply(fe);
    }

    /// <summary>
    /// Resolves every key set on <paramref name="fe"/>. Idempotent: a second
    /// pass (template recycling, a re-parented pop-out) reads the already
    /// translated value as its fallback and lands on the same bundle string.
    /// </summary>
    private static void Apply(FrameworkElement fe)
    {
        string key = GetKey(fe);
        if (key.Length > 0) ApplyToTextSlot(fe, key);

        string header = GetHeaderKey(fe);
        if (header.Length > 0) SetStringMember(fe, "Header", header);

        string message = GetMessageKey(fe);
        if (message.Length > 0) ApplyToMessageSlot(fe, message);

        string placeholder = GetPlaceholderKey(fe);
        if (placeholder.Length > 0) SetStringMember(fe, "PlaceholderText", placeholder);

        string tooltip = GetTooltipKey(fe);
        if (tooltip.Length > 0)
        {
            // A string tooltip (or none at all) is ours to write. Anything else
            // is a composed tooltip somebody built on purpose: replacing it
            // would destroy that content, and with no string to hand back as
            // the fallback a key that resolved nowhere would leave "[key]"
            // sitting where the rich tooltip used to be.
            object? current = ToolTipService.GetToolTip(fe);
            if (current is null or string)
            {
                ToolTipService.SetToolTip(fe, Localizer.T(tooltip, current as string));
            }
            else
            {
                WarnOnce($"Localize.TooltipKey=\"{tooltip}\" sits on a {fe.GetType().Name} whose tooltip is " +
                         $"a {current.GetType().Name}, not a string, so it was left as authored. " +
                         $"Localize the tooltip's own content instead.");
            }
        }

        string automation = GetAutomationKey(fe);
        if (automation.Length > 0)
        {
            AutomationProperties.SetName(fe, Localizer.T(automation, AutomationProperties.GetName(fe)));
        }

        string help = GetHelpTextKey(fe);
        if (help.Length > 0)
        {
            AutomationProperties.SetHelpText(fe, Localizer.T(help, AutomationProperties.GetHelpText(fe)));
        }
    }

    private static void ApplyToTextSlot(FrameworkElement fe, string key)
    {
        string explicitName = GetProperty(fe);
        if (explicitName.Length > 0)
        {
            SetStringMember(fe, explicitName, key);
            return;
        }

        string? member = ResolveTextMember(fe);
        if (member == null)
        {
            WarnOnce($"Localize.Key=\"{key}\" sits on a {fe.GetType().Name}, which has no known text slot. " +
                     $"Add loc:Localize.Property=\"<PropertyName>\" beside the key.");
            return;
        }

        SetStringMember(fe, member, key);
    }

    /// <summary>The names a second text slot goes by here. <c>Message</c> is the
    /// framework's (<c>InfoBar</c>); <c>Hint</c> is this repo's
    /// (<c>RailSection</c>). No control carries both.</summary>
    private static readonly string[] MessageSlots = { "Message", "Hint" };

    /// <summary>
    /// Writes <see cref="MessageKeyProperty"/> into the first
    /// <see cref="MessageSlots"/> name the control actually has. Unlike
    /// <c>Header</c> / <c>PlaceholderText</c> there is no single framework name
    /// to hard-code, and a control carrying neither is named in the log rather
    /// than warned about under a name its author never used.
    /// </summary>
    private static void ApplyToMessageSlot(FrameworkElement fe, string key)
    {
        foreach (string name in MessageSlots)
        {
            if (FindMember(fe.GetType(), name) == null) continue;
            SetStringMember(fe, name, key);
            return;
        }

        WarnOnce($"Localize.MessageKey=\"{key}\" sits on a {fe.GetType().Name}, which has neither a " +
                 $"'Message' nor a 'Hint'. If that control's second string is spelled differently, drive " +
                 $"it with loc:Localize.Key plus loc:Localize.Property=\"<PropertyName>\".");
    }

    /// <summary>
    /// The default text slot per control family, then a probe. The explicit map
    /// exists so the common cases never depend on reflection ordering; the probe
    /// covers this repo's own controls (<c>ToolStatTile.Caption</c>,
    /// <c>PanelHeader.Eyebrow</c>, …) without each needing a Property attribute.
    ///
    /// <para><c>TextBox</c> is deliberately absent: its <c>Text</c> is the
    /// streamer's data, never chrome. A TextBox localizes through
    /// <see cref="HeaderKeyProperty"/> / <see cref="PlaceholderKeyProperty"/>.</para>
    /// </summary>
    private static string? ResolveTextMember(FrameworkElement fe) => fe switch
    {
        TextBox => null,
        RichEditBox => null,
        AutoSuggestBox => null,
        TextBlock => "Text",
        ToggleSwitch => "Header",
        ContentDialog => "Title",
        ContentControl => "Content",
        _ => ProbeTextMember(fe),
    };

    /// <summary>
    /// Probe order for controls outside the explicit map — this repo's own
    /// <c>UserControl</c>s (<c>TogglePill.Label</c>, <c>ToolStatTile.Caption</c>,
    /// <c>ToolPageHeader.Title</c>).
    ///
    /// <para><c>Content</c> is deliberately ABSENT. A WinUI <c>UserControl</c>
    /// is a <c>Control</c>, not a <c>ContentControl</c>, so it never matches the
    /// map above — but it does expose an <c>object Content</c> holding its
    /// visual tree, and probing for it would make every custom control resolve
    /// to "replace my entire child tree with a string". Real content controls
    /// are already handled one level up.</para>
    /// </summary>
    private static readonly string[] ProbeOrder = { "Text", "Title", "Label", "Caption", "Eyebrow", "Header" };

    private static string? ProbeTextMember(FrameworkElement fe)
    {
        foreach (string name in ProbeOrder)
        {
            if (FindMember(fe.GetType(), name) != null) return name;
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Member resolution (DependencyProperty first, CLR property second)
    // ─────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<(Type, string), object?> _memberCache = new();

    private static object? FindMember(Type type, string name) =>
        _memberCache.GetOrAdd((type, name), static k =>
        {
            (Type t, string n) = k;

            FieldInfo? dpField = t.GetField(
                n + "Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (dpField?.GetValue(null) is DependencyProperty dp) return dp;

            PropertyInfo? clr = t.GetProperty(
                n, BindingFlags.Public | BindingFlags.Instance);
            if (clr != null && clr.CanRead && clr.CanWrite
                && (clr.PropertyType == typeof(string) || clr.PropertyType == typeof(object)))
            {
                return clr;
            }

            return null;
        });

    /// <summary>
    /// Writes <c>Localizer.T(key, currentValue)</c> into <paramref name="name"/>.
    /// The current value is read first and handed in as the fallback, which is
    /// what keeps the English literal authoritative when the bundle has no entry.
    ///
    /// <para><b>That fallback only exists when the slot currently holds a
    /// string.</b> An <c>object</c>-typed slot holding something else reads back
    /// as null, so a key missing from every bundle writes <c>[key]</c> over it
    /// rather than leaving English behind. Two routes to that state are closed
    /// upstream: the tooltip is checked by its caller in <see cref="Apply"/>,
    /// and <see cref="ProbeOrder"/> omits <c>Content</c> so a custom control
    /// never resolves to its own visual tree. What remains is a
    /// <c>Localize.Key</c> written by hand onto a real <c>ContentControl</c>
    /// whose <c>Content</c> is an element — which is an explicit instruction to
    /// put a string there, and is honoured as one.</para>
    /// </summary>
    private static void SetStringMember(FrameworkElement fe, string name, string key)
    {
        object? member = FindMember(fe.GetType(), name);

        switch (member)
        {
            case DependencyProperty dp:
            {
                string? fallback = fe.GetValue(dp) as string;
                fe.SetValue(dp, Localizer.T(key, fallback));
                return;
            }
            case PropertyInfo clr:
            {
                string? fallback = clr.GetValue(fe) as string;
                clr.SetValue(fe, Localizer.T(key, fallback));
                return;
            }
            default:
                WarnOnce($"Localize could not find a settable '{name}' on {fe.GetType().Name} " +
                         $"(key \"{key}\"). The literal stays untranslated.");
                return;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Diagnostics
    // ─────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    /// <summary>
    /// A mis-authored key attribute is a silent no-op by design (the English
    /// literal survives), so it needs a log line or nobody ever finds it. Once
    /// per distinct message — these fire from Loaded, which repeats on every
    /// template realization.
    /// </summary>
    private static void WarnOnce(string message)
    {
        if (!_warned.TryAdd(message, 0)) return;
        GlobalLogger.Log(message, "Localize", Phoenix.Controls.Shared.Models.LogLevel.System);
    }
}
