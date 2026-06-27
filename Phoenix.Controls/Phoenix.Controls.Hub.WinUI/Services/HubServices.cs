using Phoenix.Controls.Shared.WinUI.Contracts;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Top-level container realising IHubServices. Constructed once by
// HubBootstrapper after the existing WinForms Hub services
// (Bus / HUDServer / WS / ScriptManager / GlobalLogger)
// are running. Each sub-service owns its own subscription lifetime and
// is disposed together via DisposeAsync.
public sealed class HubServices : IHubServices, IAsyncDisposable
{
    public HubServices(
        ILiveFeedSource       liveFeed,
        IChatSource           chat,
        IScriptHostMonitor    scripts,
        ISystemLogSource      systemLog,
        IConnectionStatus     status,
        ILayerRegistrySource  layers)
    {
        LiveFeed  = liveFeed;
        Chat      = chat;
        Scripts   = scripts;
        SystemLog = systemLog;
        Status    = status;
        Layers    = layers;
        // Giveaway bridges to the shared Hub-runtime GiveawayService.Instance —
        // no external dependency, so it's constructed here rather than injected
        // (keeps the bootstrapper + existing ctor callers/tests untouched).
        Giveaway  = new GiveawaySourceBridge();
    }

    public ILiveFeedSource       LiveFeed  { get; }
    public IChatSource           Chat      { get; }
    public IScriptHostMonitor    Scripts   { get; }
    public ISystemLogSource      SystemLog { get; }
    public IConnectionStatus     Status    { get; }
    public ILayerRegistrySource  Layers    { get; }
    public IGiveawaySource       Giveaway  { get; }

    public async ValueTask DisposeAsync()
    {
        // Walk the sub-services and dispose any that registered cleanup. The
        // contract types don't require IDisposable, but the implementations
        // here do — log/bus subscriptions and timers leak otherwise.
        await DisposeIfPossibleAsync(LiveFeed).ConfigureAwait(false);
        await DisposeIfPossibleAsync(Chat).ConfigureAwait(false);
        await DisposeIfPossibleAsync(Scripts).ConfigureAwait(false);
        await DisposeIfPossibleAsync(SystemLog).ConfigureAwait(false);
        await DisposeIfPossibleAsync(Status).ConfigureAwait(false);
        await DisposeIfPossibleAsync(Layers).ConfigureAwait(false);
        await DisposeIfPossibleAsync(Giveaway).ConfigureAwait(false);
    }

    private static async ValueTask DisposeIfPossibleAsync(object o)
    {
        if (o is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else if (o is IDisposable d)  d.Dispose();
    }
}
