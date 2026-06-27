using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Phoenix.Controls.Hub.WinUI.Services;

/// <summary>
/// M17 (2026-05-14) — central tracker for per-Window activation state.
/// Status dots (StatusDot / ScriptStatusDot) pause their pulse storyboards
/// when their host Window goes Deactivated and resume on re-activation so
/// background windows don't keep paying the composition cost for an
/// animation the user can't see anyway.
///
/// WinUI 3 UserControls have no direct Window reference — XamlRoot is the
/// only stable per-Window identity exposed to elements in the tree.
/// MainWindow + PopOutWindowFactory call <see cref="Register"/> as soon as
/// their Window has Content (XamlRoot is materialised). Consumers subscribe
/// via <see cref="SubscribeForXamlRoot"/> on Loaded and dispose the
/// subscription on Unloaded.
/// </summary>
internal static class WindowActivationTracker
{
    // Per-Window state. ConditionalWeakTable would be cleaner but XamlRoot
    // / Window references are held by the OS for the window's full lifetime,
    // so a plain Dictionary keyed by XamlRoot is enough. Entries are removed
    // on Window.Closed.
    private sealed class Entry
    {
        public bool IsActive;
        public event Action<bool>? Changed;
        public void Raise(bool active)
        {
            IsActive = active;
            try { Changed?.Invoke(active); }
            catch { /* subscriber faulted; isolate it */ }
        }
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<XamlRoot, Entry> s_byXamlRoot = new();
    private static readonly ConditionalWeakTable<Window, object> s_registeredWindows = new();

    /// <summary>
    /// Register a Window so its activation state is mirrored into the
    /// tracker. Safe to call multiple times for the same Window; only the
    /// first call wires the Activated/Closed handlers. The Window must
    /// already have <see cref="Window.Content"/> assigned so XamlRoot is
    /// available.
    /// </summary>
    public static void Register(Window window)
    {
        if (window is null) return;
        // Idempotency gate — the ConditionalWeakTable's Add throws if the
        // key already exists, which is exactly the "already registered"
        // signal we want without keeping a separate HashSet.
        try { s_registeredWindows.Add(window, new object()); }
        catch (ArgumentException) { return; }

        // Resolve the XamlRoot now (Window.Content was set before Register
        // is called) and prime the entry as Active — WinUI doesn't fire
        // Activated for the initial focus state on every machine, so a dot
        // loaded into an already-active window would stay paused until the
        // user clicked elsewhere and back.
        var xamlRoot = (window.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is not null)
        {
            var entry = GetOrCreate(xamlRoot);
            entry.IsActive = true;
        }

        window.Activated += OnWindowActivated;
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        if (sender is not Window w) return;
        var xamlRoot = (w.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;
        bool active = e.WindowActivationState != WindowActivationState.Deactivated;
        Entry entry;
        lock (s_lock)
        {
            if (!s_byXamlRoot.TryGetValue(xamlRoot, out entry!))
            {
                entry = new Entry();
                s_byXamlRoot[xamlRoot] = entry;
            }
        }
        entry.Raise(active);
    }

    private static void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is not Window w) return;
        w.Activated -= OnWindowActivated;
        w.Closed -= OnWindowClosed;
        var xamlRoot = (w.Content as FrameworkElement)?.XamlRoot;
        if (xamlRoot is null) return;
        lock (s_lock) { s_byXamlRoot.Remove(xamlRoot); }
    }

    /// <summary>
    /// Subscribe to activation changes for the Window that owns <paramref name="xamlRoot"/>.
    /// Returns the entry's current <c>IsActive</c> via <paramref name="initial"/> so
    /// callers can start the pulse in the correct state without waiting for the
    /// first Activated fire. Disposing the returned <see cref="IDisposable"/>
    /// unsubscribes.
    /// </summary>
    public static IDisposable SubscribeForXamlRoot(XamlRoot xamlRoot, Action<bool> onChanged, out bool initial)
    {
        if (xamlRoot is null || onChanged is null)
        {
            initial = true;
            return new NoopDisposable();
        }
        var entry = GetOrCreate(xamlRoot);
        initial = entry.IsActive;
        entry.Changed += onChanged;
        return new Subscription(entry, onChanged);
    }

    private static Entry GetOrCreate(XamlRoot xamlRoot)
    {
        lock (s_lock)
        {
            if (!s_byXamlRoot.TryGetValue(xamlRoot, out var entry))
            {
                // Default to Active so a Window that hasn't fired Activated
                // yet doesn't pause animations on its visible content. The
                // first OnWindowActivated will correct the state.
                entry = new Entry { IsActive = true };
                s_byXamlRoot[xamlRoot] = entry;
            }
            return entry;
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Entry? _entry;
        private Action<bool>? _handler;
        public Subscription(Entry entry, Action<bool> handler)
        {
            _entry = entry;
            _handler = handler;
        }
        public void Dispose()
        {
            var entry = _entry;
            var handler = _handler;
            _entry = null;
            _handler = null;
            if (entry is not null && handler is not null)
                entry.Changed -= handler;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
