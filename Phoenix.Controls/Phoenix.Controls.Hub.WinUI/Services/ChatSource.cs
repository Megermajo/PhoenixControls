using System.Text.Json;
using Phoenix.Controls.Hub.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Shared.WinUI.Contracts;

// Both namespaces above export a ChatMessage type. Models.ChatMessage is
// the WS-shaped wire model with IsBroadcaster/IsMod/IsVip/IsSub boolean
// flags; Contracts.ChatMessage is the panel-facing record IChatSource
// returns. The alias disambiguates: ModelChatMessage names the wire form,
// and unaliased ChatMessage resolves to Contracts.ChatMessage so the
// surrounding IChatSource implementation reads naturally.
using ModelChatMessage = Phoenix.Controls.Shared.Models.ChatMessage;
using ChatMessage      = Phoenix.Controls.Shared.WinUI.Contracts.ChatMessage;

namespace Phoenix.Controls.Hub.WinUI.Services;

// Chat panel feed. Wraps WS — its OnChatMessage event already
// applies the AppConfig.BotUsername self-trigger guard in WS
// BEFORE invoking the event, so bot lines never reach
// this layer at all and we do not need to filter again.
//
// NOTE: bot messages are not surfaced in IChatSource for visibility. The existing
// Hub explicitly hides them from chat display
// ("ChatViewWindow doesn't render the bot's own line"). The ring is
// already filtered, so no extra filtering is needed here.
public sealed class ChatSource : IChatSource, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly Action<ModelChatMessage> _onChat;
    // Fan-out: every subscriber to MessageReceived
    // receives every chat line; no primary-subscriber gate.
    private int _disposed;

    public ChatSource(IUiDispatcher uiDispatcher)
    {
        _ui = uiDispatcher;
        _onChat = msg =>
        {
            var mapped = Map(msg);
            _ui.Post(() => MessageReceived?.Invoke(this, mapped));
        };

        // Subscribing to the live event covers all messages from now on; the
        // initial backlog (up to RecentChatCapacity = 50 in WS) is
        // handed back through Snapshot() instead, so panels don't double-fire.
        WS.Instance.OnChatMessage += _onChat;
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        var ring = WS.Instance.PeekRecentChat(0);
        if (ring.Count == 0) return Array.Empty<ChatMessage>();
        var result = new List<ChatMessage>(ring.Count);
        foreach (var m in ring) result.Add(Map(m));
        return result;
    }

    public event EventHandler<ChatMessage>? MessageReceived;

    public Task SendAsBotAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;

        // Mirror the engine's twitch.send_chat command (ScriptManager.Twitch.cs):
        // cap at the 500-char Twitch IRC limit, dispatch through
        // the user-configured Streamer.bot DoAction so bot identity / chat
        // origin stay consistent with .phx scripts.
        if (text.Length > 500)
        {
            GlobalLogger.Log(
                $"Chat → DROPPED: message length {text.Length} exceeds 500-char Twitch cap.",
                "Chat", LogLevel.CriticalError);
            return Task.CompletedTask;
        }

        // Explicit guards for the two silent-failure modes the
        // panel previously hid behind the raw WS→SB payload log:
        //   (a) StreamerBotChatAction blank in config → SB receives an
        //       empty action.name, drops the request without responding,
        //       chat never reaches Twitch, no error visible to the user.
        //   (b) Streamer.bot WebSocket disconnected → WS.Send already logs
        //       "DROPPED (not connected)" at CriticalError, but only as the
        //       generic transport-level message. We surface a chat-tagged
        //       version up-front so the System Log shows the failure under
        //       the same "Chat" source as successful sends.
        string actionName = (ConfigManager.Current.StreamerBotChatAction ?? string.Empty).Trim();
        if (actionName.Length == 0)
        {
            GlobalLogger.Log(
                "Chat → DROPPED: Streamer.bot Chat Action name is not configured. " +
                "Set it in Hub Settings → Connection → Chat Action Name (must match an Action in Streamer.bot).",
                "Chat", LogLevel.CriticalError);
            return Task.CompletedTask;
        }
        if (!WS.Instance.IsConnected)
        {
            GlobalLogger.Log(
                $"Chat → DROPPED: Streamer.bot WebSocket is not connected (action='{actionName}', msg=\"{text}\").",
                "Chat", LogLevel.CriticalError);
            return Task.CompletedTask;
        }

        try
        {
            // Unique request id per send so SB's request/response correlation
            // doesn't see duplicate "phoenix-controls-hub-chat-send" ids
            // across rapid back-to-back chat sends.
            string reqId = WS.NewRequestId("chat");
            var payload = JsonSerializer.Serialize(new
            {
                request = "DoAction",
                id      = reqId,
                action  = new { name = actionName },
                args    = new { message = text },
            });
            WS.Instance.Send(payload);
            GlobalLogger.Log($"Chat → \"{text}\" via {actionName}", "Chat", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("ChatSource", "SendAsBotAsync failed", ex);
        }
        return Task.CompletedTask;
    }

    private static ChatMessage Map(ModelChatMessage m) => new(
        Timestamp:     DateTimeOffset.Now,
        Role:          ToRole(m),
        Username:      m.Username ?? "",
        Body:          m.Message  ?? "",
        IsBroadcaster: m.IsBroadcaster,
        IsMod:         m.IsMod,
        IsVip:         m.IsVip,
        IsSubscriber:  m.IsSub);

    private static ChatRole ToRole(ModelChatMessage m)
    {
        // Order mirrors Twitch's badge precedence — broadcaster outranks mod
        // outranks vip outranks sub. Bot is unreachable (filtered upstream
        // by WS.IsBlockedAccount before OnChatMessage fires).
        if (m.IsBroadcaster) return ChatRole.Broadcaster;
        if (m.IsMod)         return ChatRole.Mod;
        if (m.IsVip)         return ChatRole.Vip;
        if (m.IsSub)         return ChatRole.Sub;
        return ChatRole.Viewer;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        WS.Instance.OnChatMessage -= _onChat;
    }
}
