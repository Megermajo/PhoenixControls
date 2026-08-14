using System;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Custom Chat Commands (text/variable-only) — the Hub-side wiring of
    // the custom-command tool (sibling of ScriptManager.Quotes.cs / .Counters.cs).
    //
    // Unlike the other pre-build tools this adds NO imperative script command (a custom
    // command is just "on !cmd → send templated text", already hand-buildable in
    // Architect), so RegisterCustomCommandsCommands() only wires the CustomCommandsService
    // seams — it does not register any _engine command. Two responsibilities:
    //
    //   1. Seam injection — the service can't touch the script dispatcher or Hub config
    //      itself, so RaiseScriptEvent (Command.OnCustom) is wired fire-and-forget through
    //      AsyncErrorBoundary.SafeRunAsync (mirroring ScriptManager.Quotes.cs), plus the
    //      {count}/{count.next} counter access seams (pointed at CountersService — this
    //      tool CONSUMES the Counters tool, never owns a counter table/node), the
    //      {channel} resolver (AppConfig.BroadcasterUsername), {uptime} (the cached
    //      broadcaster-live state), and {game}/{title} (the ambient StreamInfoTracker).
    //
    //   2. The BUILT-IN chat-command entry (TryHandleCustomCommandsChatCommandAsync),
    //      registered as the CustomCommands provider in RegisterBuiltInChatProviders
    //      (LAST, after Quotes). A thin wrapper: the parse/gate/cooldown/render logic
    //      lives on CustomCommandsService (unit-testable without a ScriptManager), and
    //      this wrapper supplies the per-platform reply send. Default-OFF is a total no-op.
#pragma warning disable CS1998
    public partial class ScriptManager
    {
        private void RegisterCustomCommandsCommands()
        {
            var svc = CustomCommandsService.Instance;

            // ── Seam: Command.OnCustom script event ──────────────────────────
            // Flows back through the generic-event dispatcher with pre-built vars
            // (event.command / event.user / event.args). Fired-and-forgotten so a
            // faulting handler routes to the log instead of an unobserved Task.
            svc.RaiseScriptEvent = (phoenixEvent, vars) =>
            {
                _ = AsyncErrorBoundary.SafeRunAsync(
                    () => ExecuteGenericEventAsync(phoenixEvent, default, vars),
                    "CustomCommandsService", $"RaiseScriptEvent({phoenixEvent})");
            };

            // ── Seam: {count} / {count.next} — CONSUME the Counters tool ──────
            // No counter table / node is created here. {count} reads the live value;
            // {count.next} increments by one and shows the new value. Both go straight
            // to CountersService (the Counters open "Counters" table is the truth).
            svc.CounterGet  = name => CountersService.Instance.GetAsync(name ?? "");
            svc.CounterNext = name => CountersService.Instance.AddAsync(name ?? "", 1);

            // ── Seam: {channel} — the broadcaster/channel name ───────────────
            // Read live each call so a Settings edit is reflected without a restart.
            svc.ChannelProvider = () => ConfigManager.Current?.BroadcasterUsername ?? string.Empty;

            // ── Seam: {uptime} — time since the broadcaster went live ────────
            // ResolveBroadcasterUptime() reads the cached StreamOnline/Offline state
            // (_broadcasterLive / _broadcasterLiveSinceTicks) — the same source the
            // {stream.uptime} script token uses — so it's a zero-cost sync read. Renders
            // "" while offline (never throws).
            svc.UptimeProvider = () => ResolveBroadcasterUptime();

            // ── Seams: {game} / {title} — the ambient stream-info cache ──────
            // StreamInfoTracker is fed by the pushed StreamUpdate/ChannelUpdate
            // events, the title/category setter commands, own-channel Get-User
            // fetches, and a lazy one-shot seed. EnsureStreamInfoSeeded no-ops
            // once the cache is warm; both render "" until the value is known.
            svc.GameProvider = () =>
            {
                EnsureStreamInfoSeeded();
                return StreamInfoTracker.CurrentGame;
            };
            svc.TitleProvider = () =>
            {
                EnsureStreamInfoSeeded();
                return StreamInfoTracker.CurrentTitle;
            };
        }

        // ── Built-in chat-command entry (thin wrapper over the testable core) ──
        /// <summary>
        /// The built-in Custom Chat Commands provider entry. Delegates the
        /// parse/gate/cooldown/render to CustomCommandsService (unit-testable) and
        /// supplies the per-platform reply send. Returns true when a custom command was
        /// recognized (so the caller suppresses the author on_chat fan-out — a custom
        /// command replaces a legacy author on_chat for that trigger). Default-OFF is a
        /// total no-op — the service returns false the instant the tool is disabled.
        /// </summary>
        public Task<bool> TryHandleCustomCommandsChatCommandAsync(ChatMessage msg)
            => CustomCommandsService.Instance.TryHandleChatCommandAsync(
                   msg,
                   reply => { SendCustomCommandsReply(msg, reply); return Task.CompletedTask; });

        // Route the reply on the platform the command arrived on, reusing the exact
        // per-platform chat-send cores chat.send / *.send_chat use — mirrors
        // SendQuotesReply / SendCountersReply, no new send path.
        private void SendCustomCommandsReply(ChatMessage msg, string reply)
        {
            if (string.IsNullOrEmpty(reply)) return;
            switch (msg.Platform)
            {
                case ChatPlatforms.YouTube: SendYouTubeChatCore(reply, "customcommands"); break;
                case ChatPlatforms.Kick:    SendKickChatCore(reply, "customcommands"); break;
                default:                    SendTwitchChatCore(reply, "customcommands"); break;
            }
        }
    }
#pragma warning restore CS1998
}
