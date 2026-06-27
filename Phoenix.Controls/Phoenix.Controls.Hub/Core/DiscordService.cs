using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// P4 — Discord bot REST client (https://discord.com/api/v10). Hosts a
    /// single shared <see cref="HttpClient"/> for the lifetime of the Hub, much
    /// like <c>ScriptManager._http</c>. The bot-token alternative to the legacy
    /// <c>discord.webhook</c> command. BCL-only — no third-party Discord SDK.
    ///
    /// Token is read lazily from <c>ConfigManager.Current.DiscordBotToken</c>
    /// on every call so a Settings UI edit takes effect immediately without
    /// re-instantiating the service.
    /// </summary>
    internal sealed class DiscordService : IDisposable
    {
        private const string ApiBase = "https://discord.com/api/v10";

        private static readonly Lazy<DiscordService> _instance =
            new(() => new DiscordService());
        public static DiscordService Instance => _instance.Value;

        private readonly HttpClient _http;

        private DiscordService()
        {
            _http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "PhoenixControls (https://github.com/Megermajo/PhoenixControls)");
        }

        private int _disposed;

        // [P1 swarm-audit 2026-05-29] The Lazy<> singleton's HttpClient (and its
        // HttpClientHandler) were never disposed, leaking the underlying socket
        // pool + handler for the Hub process lifetime. HubBootstrapper's shutdown
        // path calls Dispose() (wired separately). Disposing _http also disposes
        // the inner HttpClientHandler because the HttpClient(HttpMessageHandler)
        // ctor defaults disposeHandler:true. Idempotent — guarded so a double
        // Dispose (e.g. shutdown + finalize) is a no-op.
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _http.Dispose(); } catch { /* best-effort */ }
        }

        /// <summary>
        /// Per-call result envelope. Mirrors the http.* contract:
        /// <c>Error</c> empty on success, exception/HTTP message on failure.
        /// <c>MessageId</c> populated by SendMessage on success.
        /// </summary>
        public readonly struct DiscordResult
        {
            public string Error     { get; init; }
            public string MessageId { get; init; }
        }

        public Task<DiscordResult> SendMessageAsync(
            string channelId, string content, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                return Task.FromResult(new DiscordResult { Error = "Discord.SendMessage: ChannelId is empty.", MessageId = "" });
            if (string.IsNullOrEmpty(content))
                return Task.FromResult(new DiscordResult { Error = "Discord.SendMessage: Content is empty.", MessageId = "" });

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))
                return Task.FromResult(new DiscordResult { Error = "No Discord bot token configured.", MessageId = "" });

            var payload = new { content };
            return PostJsonAsync(channelId, token, payload, ct);
        }

        public Task<DiscordResult> SendEmbedAsync(
            string channelId, string title, string description,
            string color, string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                return Task.FromResult(new DiscordResult { Error = "Discord.SendEmbed: ChannelId is empty.", MessageId = "" });

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(description) && string.IsNullOrEmpty(url))
                return Task.FromResult(new DiscordResult { Error = "Discord.SendEmbed: at least one of Title / Description / Url must be set.", MessageId = "" });

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))
                return Task.FromResult(new DiscordResult { Error = "No Discord bot token configured.", MessageId = "" });

            var embed = new EmbedPayload
            {
                title       = string.IsNullOrEmpty(title)       ? null : title,
                description = string.IsNullOrEmpty(description) ? null : description,
                url         = string.IsNullOrEmpty(url)         ? null : url,
                color       = ParseColor(color)
            };
            var payload = new { embeds = new[] { embed } };
            return PostJsonAsync(channelId, token, payload, ct);
        }

        // P4 slice 2 — guild role grants. PUT /guilds/{g}/members/{u}/roles/{r}
        // returns 204 No Content on success. Bot needs Manage Roles + the target
        // role must sit below the bot's highest role in the guild's hierarchy;
        // a 403 from Discord surfaces verbatim through ExtractErrorMessage.
        public async Task<DiscordResult> AddRoleAsync(
            string guildId, string userId, string roleId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(guildId)) return Err("Discord.AddRole: GuildId is empty.");
            if (string.IsNullOrWhiteSpace(userId))  return Err("Discord.AddRole: UserId is empty.");
            if (string.IsNullOrWhiteSpace(roleId))  return Err("Discord.AddRole: RoleId is empty.");

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))   return Err("No Discord bot token configured.");

            string url =
                $"{ApiBase}/guilds/{Uri.EscapeDataString(guildId)}" +
                $"/members/{Uri.EscapeDataString(userId)}" +
                $"/roles/{Uri.EscapeDataString(roleId)}";
            return await SendNoContentAsync(HttpMethod.Put, url, token, ct).ConfigureAwait(false);
        }

        public async Task<DiscordResult> RemoveRoleAsync(
            string guildId, string userId, string roleId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(guildId)) return Err("Discord.RemoveRole: GuildId is empty.");
            if (string.IsNullOrWhiteSpace(userId))  return Err("Discord.RemoveRole: UserId is empty.");
            if (string.IsNullOrWhiteSpace(roleId))  return Err("Discord.RemoveRole: RoleId is empty.");

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))   return Err("No Discord bot token configured.");

            string url =
                $"{ApiBase}/guilds/{Uri.EscapeDataString(guildId)}" +
                $"/members/{Uri.EscapeDataString(userId)}" +
                $"/roles/{Uri.EscapeDataString(roleId)}";
            return await SendNoContentAsync(HttpMethod.Delete, url, token, ct).ConfigureAwait(false);
        }

        // PUT /channels/{c}/messages/{m}/reactions/{emoji}/@me — adds the bot's
        // own reaction. Emoji is either a unicode char (URL-encoded as a single
        // segment) or a custom-emoji handle in `name:id` form (each side is
        // encoded independently and joined with a literal `:` so Discord's
        // path parser still recognizes the custom-emoji shape).
        public async Task<DiscordResult> ReactAsync(
            string channelId, string messageId, string emoji, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(channelId)) return Err("Discord.React: ChannelId is empty.");
            if (string.IsNullOrWhiteSpace(messageId)) return Err("Discord.React: MessageId is empty.");
            if (string.IsNullOrEmpty(emoji))          return Err("Discord.React: Emoji is empty.");

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))     return Err("No Discord bot token configured.");

            string emojiPath = EncodeEmojiForPath(emoji);
            string url =
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}" +
                $"/messages/{Uri.EscapeDataString(messageId)}" +
                $"/reactions/{emojiPath}/@me";
            return await SendNoContentAsync(HttpMethod.Put, url, token, ct).ConfigureAwait(false);
        }

        // GET /users/{id} — returns the user object. Avatar URL is constructed
        // client-side from the avatar hash; users without a custom avatar
        // surface AvatarUrl as the empty string (callers can detect that).
        public async Task<DiscordUserResult> GetUserAsync(string userId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return new DiscordUserResult { Error = "Discord.GetUser: UserId is empty." };

            string token = ConfigManager.Current.DiscordBotToken ?? "";
            if (string.IsNullOrWhiteSpace(token))
                return new DiscordUserResult { Error = "No Discord bot token configured." };

            string url = $"{ApiBase}/users/{Uri.EscapeDataString(userId)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                string body = resp.Content == null
                    ? ""
                    : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    return new DiscordUserResult
                    {
                        Error = $"Discord API {(int)resp.StatusCode}: {ExtractErrorMessage(body)}"
                    };
                }

                return ParseUser(body);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new DiscordUserResult { Error = ex.Message };
            }
        }

        private async Task<DiscordResult> SendNoContentAsync(
            HttpMethod method, string url, string token, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return new DiscordResult { Error = "", MessageId = "" };

                string body = resp.Content == null
                    ? ""
                    : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new DiscordResult
                {
                    Error     = $"Discord API {(int)resp.StatusCode}: {ExtractErrorMessage(body)}",
                    MessageId = ""
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new DiscordResult { Error = ex.Message, MessageId = "" };
            }
        }

        private static DiscordResult Err(string message) =>
            new() { Error = message, MessageId = "" };

        internal static string EncodeEmojiForPath(string emoji)
        {
            int colon = emoji.IndexOf(':');
            if (colon > 0 && colon < emoji.Length - 1)
            {
                string name = emoji.Substring(0, colon);
                string id   = emoji.Substring(colon + 1);
                return $"{Uri.EscapeDataString(name)}:{Uri.EscapeDataString(id)}";
            }
            return Uri.EscapeDataString(emoji);
        }

        private static DiscordUserResult ParseUser(string body)
        {
            if (string.IsNullOrEmpty(body))
                return new DiscordUserResult { Error = "(empty body)" };
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new DiscordUserResult { Error = "Unexpected response shape." };

                string id         = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                    ? (idEl.GetString() ?? "") : "";
                string username   = root.TryGetProperty("username", out var unEl) && unEl.ValueKind == JsonValueKind.String
                                    ? (unEl.GetString() ?? "") : "";
                string globalName = root.TryGetProperty("global_name", out var gnEl) && gnEl.ValueKind == JsonValueKind.String
                                    ? (gnEl.GetString() ?? "") : "";
                string avatar     = root.TryGetProperty("avatar", out var avEl) && avEl.ValueKind == JsonValueKind.String
                                    ? (avEl.GetString() ?? "") : "";

                string avatarUrl = "";
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(avatar))
                {
                    string ext = avatar.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
                    avatarUrl = $"https://cdn.discordapp.com/avatars/{id}/{avatar}.{ext}";
                }

                return new DiscordUserResult
                {
                    Error      = "",
                    UserId     = id,
                    Username   = username,
                    GlobalName = globalName,
                    AvatarUrl  = avatarUrl
                };
            }
            catch (JsonException ex)
            {
                return new DiscordUserResult { Error = $"Discord.GetUser: malformed JSON ({ex.Message})." };
            }
        }

        public readonly struct DiscordUserResult
        {
            public string Error      { get; init; }
            public string UserId     { get; init; }
            public string Username   { get; init; }
            public string GlobalName { get; init; }
            public string AvatarUrl  { get; init; }
        }

        private async Task<DiscordResult> PostJsonAsync(
            string channelId, string token, object payload, CancellationToken ct)
        {
            string url = $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages";
            string json = JsonSerializer.Serialize(payload, _jsonOpts);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

            try
            {
                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                string body = resp.Content == null
                    ? ""
                    : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    return new DiscordResult
                    {
                        Error     = $"Discord API {(int)resp.StatusCode}: {ExtractErrorMessage(body)}",
                        MessageId = ""
                    };
                }

                return new DiscordResult
                {
                    Error     = "",
                    MessageId = ExtractMessageId(body)
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new DiscordResult { Error = ex.Message, MessageId = "" };
            }
        }

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private sealed class EmbedPayload
        {
            public string? title       { get; set; }
            public string? description { get; set; }
            public string? url         { get; set; }
            public int?    color       { get; set; }
        }

        // Accepts "#RRGGBB", "RRGGBB", or a decimal int. Empty / unparseable
        // returns null so the embed posts without a color (Discord defaults to
        // a neutral gray sidebar).
        internal static int? ParseColor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return hex;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                return dec;
            return null;
        }

        // Discord returns the new message as JSON with an "id" field.
        private static string ExtractMessageId(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("id", out var idEl))
                {
                    return idEl.ValueKind == JsonValueKind.String
                        ? (idEl.GetString() ?? "")
                        : idEl.ToString();
                }
            }
            catch (JsonException) { }
            return "";
        }

        // Discord error envelope is `{ "message": "...", "code": N, ... }`. Fall
        // back to the raw body if the shape doesn't match (so a network-layer
        // HTML error page still surfaces something useful).
        private static string ExtractErrorMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return "(empty body)";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("message", out var msgEl) &&
                    msgEl.ValueKind == JsonValueKind.String)
                {
                    return msgEl.GetString() ?? body;
                }
            }
            catch (JsonException) { }
            return body.Length > 256 ? body.Substring(0, 256) + "…" : body;
        }
    }
}
