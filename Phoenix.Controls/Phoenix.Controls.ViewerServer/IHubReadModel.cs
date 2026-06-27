using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.ViewerServer;

/// <summary>
/// The contract <see cref="ViewerServer"/> consumes from its host. The
/// Hub-side implementation (eventually living in
/// <c>Phoenix.Controls.Hub.WinUI/Services/HubViewerReadModel.cs</c> per
/// Track 4, or in the WinForms Hub as a transitional shim) wraps the
/// existing Hub services — script registry, live feed buffer, chat
/// history, GlobalLogger ring buffer, connection state — and projects
/// them into the read-only DTOs declared in <see cref="ViewerSnapshot"/>.
///
/// Kept deliberately minimal: a one-shot snapshot for bootstrap and a
/// single push stream for live updates. The viewer never talks back, so
/// no command surface is exposed here.
/// </summary>
public interface IHubReadModel
{
    /// <summary>
    /// Build the bootstrap payload the viewer renders on first load. Implementations
    /// should bound list sizes (last 100 chat, last 100 live feed, last 200 system
    /// log) per the design package.
    /// </summary>
    Task<ViewerSnapshot> GetSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// Subscribe to live updates. The returned <see cref="IDisposable"/>
    /// unsubscribes when disposed. The handler is invoked on whichever
    /// thread the source publishes on; it must be cheap and non-blocking.
    /// </summary>
    IDisposable Subscribe(Action<ViewerEvent> handler);
}
