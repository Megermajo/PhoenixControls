using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: Alerts tool wiring — the seams that give the Hub-state-agnostic
    // AlertsService its three outputs (per-platform chat send, Twitch shoutout, and
    // the shared visual-trigger fan-out). Registers NO script command: the tool is a
    // no-code shortcut over capabilities every graph already has (event node →
    // Logic.If size branches → Chat.Send + Visual.Trigger + Twitch.Shoutout), so
    // Architect-first parity holds with zero new engine surface.
    //
    // Sibling of ScriptManager.Scheduling.cs; registered from RegisterHubCommands.
    // The event tap itself lives in ExecuteGenericEventAsync's pre-guard region
    // beside the Timer/Loyalty taps.
    public partial class ScriptManager
    {
        private void RegisterAlertsCommands()
        {
            var svc = AlertsService.Instance;

            // Alert lines post on the platform the event arrived on.
            svc.SendChat = (platform, text) =>
            {
                if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
                switch (platform)
                {
                    case ChatPlatforms.YouTube: SendYouTubeChatCore(text, "alerts"); break;
                    case ChatPlatforms.Kick:    SendKickChatCore(text, "alerts"); break;
                    default:                    SendTwitchChatCore(text, "alerts"); break;
                }
                return Task.CompletedTask;
            };

            // Raid auto-shoutout — same SB action the twitch.shoutout node dispatches.
            svc.Shoutout = user =>
            {
                if (!string.IsNullOrWhiteSpace(user))
                    DispatchNamedAction("alerts.shoutout", PhxSbActions.Shoutout, new { user });
                return Task.CompletedTask;
            };

            // Visual hookup — the shared (layer, trigger) fan-out every tool effect
            // rides (see ScriptManager.Visual.cs). The widget graph receives
            // Args1=KIND / Args2=USER / Args3=AMOUNT via the standard Args expansion.
            svc.FireVisual = (layerId, triggerName, eventData) =>
                FireVisualTriggerFanOutAsync(layerId, triggerName, eventData, "AlertsService");
        }
    }
}
