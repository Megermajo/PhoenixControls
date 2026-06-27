using System;
using Microsoft.UI.Dispatching;

namespace Phoenix.Controls.Hub.WinUI.Panels.Common;

// Per-VM marshal helper. Wraps the DispatcherQueue passed into the VM's
// constructor and offers a HasThreadAccess fast-path Post().
//
// C1 (2026-05-14): replaces the prior static `DispatcherQueueOwner` slot
// that was captured by whichever View constructed first. The static
// approach worked for Hub today (every panel ctor runs on the same UI
// thread) but it tangled two unrelated VMs through a shared piece of
// state — a pop-out window on a different dispatcher would have used
// the embedded panel's queue, and unit-test seams couldn't inject a
// synchronous dispatcher per VM. Ctor-injection clears both.
//
// Internal because callers should hold one of these via a VM field, not
// reach for it from outside the panel layer.
internal sealed class UiDispatcherPump
{
    private readonly DispatcherQueue? _dq;

    public UiDispatcherPump(DispatcherQueue? dispatcher)
    {
        _dq = dispatcher;
    }

    /// <summary>
    /// Run <paramref name="action"/> on the UI thread, skipping the dispatcher
    /// hop when the caller is already on it. Mirrors the perf-review H1
    /// fast-path the static slot used to provide.
    /// </summary>
    public void Post(Action action)
    {
        var dq = _dq;
        if (dq is null) { action(); return; }
        if (dq.HasThreadAccess) action();
        else dq.TryEnqueue(() => action());
    }
}
