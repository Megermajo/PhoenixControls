using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: discord.* command registrations.
    // Seven RegisterCommand bodies (send_message / send_embed / add_role /
    // remove_role / react / get_user / webhook) lifted out of
    // RegisterHubCommands so the omnibus method shrinks one domain at a time.
    // No behavior change: registration order, command names, and result.*
    // contracts are byte-for-byte identical to the pre-split inline version.
    public partial class ScriptManager
    {
        private void RegisterDiscordCommands()
        {
            // P4 — discord.send_message(channel_id, content)
            //   discord.send_embed(channel_id, title, description, color, url)
            // Bot REST path (https://discord.com/api/v10) — see DiscordService.
            // result.* contract mirrors http.*: result.discord_error empty on
            // success, message on failure; SendMessage additionally writes
            // result.discord_message_id on success. Empty AppConfig.DiscordBotToken
            // short-circuits with "No Discord bot token configured." and never
            // touches the network. The legacy discord.webhook handler below is
            // unchanged — webhooks remain the no-token alternative.
            _engine.RegisterCommand("discord.send_message", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string channelId = bound?.GetOrDefault<string>("ChannelId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string content   = bound?.GetOrDefault<string>("Content", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);

                var result = await DiscordService.Instance
                    .SendMessageAsync(channelId, content, _engine.ExecutionToken)
                    .ConfigureAwait(false);

                await _engine.SetScriptVarAsync("result.discord_message_id", result.MessageId);
                await _engine.SetScriptVarAsync("result.discord_error",      result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord SendMessage Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            _engine.RegisterCommand("discord.send_embed", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string channelId   = bound?.GetOrDefault<string>("ChannelId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string title       = bound?.GetOrDefault<string>("Title", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string description = bound?.GetOrDefault<string>("Description", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);
                string color       = bound?.GetOrDefault<string>("Color", ArgOrEmpty(args, 3)) ?? ArgOrEmpty(args, 3);
                string url         = bound?.GetOrDefault<string>("Url", ArgOrEmpty(args, 4)) ?? ArgOrEmpty(args, 4);

                var result = await DiscordService.Instance
                    .SendEmbedAsync(channelId, title, description, color, url, _engine.ExecutionToken)
                    .ConfigureAwait(false);

                // SendEmbed surfaces only result.discord_error per the node's
                // socket contract; the message-id slot is reset so a stale value
                // from a previous discord.send_message call doesn't leak through
                // a downstream {result.discord_message_id} reference.
                await _engine.SetScriptVarAsync("result.discord_message_id", result.MessageId);
                await _engine.SetScriptVarAsync("result.discord_error",      result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord SendEmbed Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            // P4 slice 2 — discord.add_role / .remove_role / .react / .get_user.
            // Same bot-token short-circuit as send_message/embed; same
            // result.discord_error contract. get_user additionally writes
            // result.discord_user_* (id, name, global_name, avatar URL).
            _engine.RegisterCommand("discord.add_role", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string guildId = bound?.GetOrDefault<string>("GuildId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string userId  = bound?.GetOrDefault<string>("UserId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string roleId  = bound?.GetOrDefault<string>("RoleId", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);

                var result = await DiscordService.Instance
                    .AddRoleAsync(guildId, userId, roleId, _engine.ExecutionToken)
                    .ConfigureAwait(false);
                await _engine.SetScriptVarAsync("result.discord_error", result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord AddRole Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            _engine.RegisterCommand("discord.remove_role", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string guildId = bound?.GetOrDefault<string>("GuildId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string userId  = bound?.GetOrDefault<string>("UserId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string roleId  = bound?.GetOrDefault<string>("RoleId", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);

                var result = await DiscordService.Instance
                    .RemoveRoleAsync(guildId, userId, roleId, _engine.ExecutionToken)
                    .ConfigureAwait(false);
                await _engine.SetScriptVarAsync("result.discord_error", result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord RemoveRole Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            _engine.RegisterCommand("discord.react", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string channelId = bound?.GetOrDefault<string>("ChannelId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string messageId = bound?.GetOrDefault<string>("MessageId", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                string emoji     = bound?.GetOrDefault<string>("Emoji", ArgOrEmpty(args, 2)) ?? ArgOrEmpty(args, 2);

                var result = await DiscordService.Instance
                    .ReactAsync(channelId, messageId, emoji, _engine.ExecutionToken)
                    .ConfigureAwait(false);
                await _engine.SetScriptVarAsync("result.discord_error", result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord React Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            _engine.RegisterCommand("discord.get_user", async (args) =>
            {
                string userId = _engine.CurrentBoundArgs?.GetOrDefault<string>("UserId", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);

                var result = await DiscordService.Instance
                    .GetUserAsync(userId, _engine.ExecutionToken)
                    .ConfigureAwait(false);

                await _engine.SetScriptVarAsync("result.discord_user_id",          result.UserId     ?? "");
                await _engine.SetScriptVarAsync("result.discord_user_name",        result.Username   ?? "");
                await _engine.SetScriptVarAsync("result.discord_user_global_name", result.GlobalName ?? "");
                await _engine.SetScriptVarAsync("result.discord_user_avatar",      result.AvatarUrl  ?? "");
                await _engine.SetScriptVarAsync("result.discord_error",            result.Error);

                if (!string.IsNullOrEmpty(result.Error))
                    GlobalLogger.Log($"Discord GetUser Failed: {result.Error}", "Script", LogLevel.CriticalError);
                return null;
            });

            // discord.webhook(name_or_url, message)
            // If the first argument matches a key in ConfigManager.Current.Webhooks, that stored
            // URL is used. Otherwise the argument is treated as a raw webhook URL (backwards-compatible).
            _engine.RegisterCommand("discord.webhook", async (args) => {
                var bound = _engine.CurrentBoundArgs;
                string urlArg = bound?.GetOrDefault<string>("URL", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string msg    = bound?.GetOrDefault<string>("Msg", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                if (string.IsNullOrEmpty(urlArg) || string.IsNullOrEmpty(msg)) return null;
                try
                {
                    string webhookUrl = ConfigManager.Current.Webhooks.TryGetValue(urlArg, out var stored)
                        ? stored
                        : urlArg;

                    // SSRF guardrail — the webhook URL is script-derived (and
                    // ultimately chat-derived via {var.*} substitution). Without this gate
                    // a malicious viewer could coax a Hub-running script into POSTing the
                    // chat message to http://127.0.0.1:18081/ (Bus), http://127.0.0.1:11434/
                    // (Ollama), http://169.254.169.254/ (cloud-instance metadata), or any
                    // private-LAN host. Whitelist discord.com / discordapp.com only, force
                    // https, and validate every DNS-resolved IP — so a hostname that points
                    // at 127.0.0.1 (DNS rebinding included) fails before the POST.
                    var (allowed, rejectReason) = await WebhookUrlGuard
                        .ValidateAsync(webhookUrl, _engine.ExecutionToken)
                        .ConfigureAwait(false);
                    if (!allowed)
                    {
                        string err = $"Webhook URL rejected: {rejectReason}";
                        await _engine.SetScriptVarAsync("result.discord_error", err);
                        GlobalLogger.Log($"Discord Webhook BLOCKED ('{urlArg}'): {rejectReason}", "Script", LogLevel.CriticalError);
                        return null;
                    }

                    var payload = new { content = msg };
                    var json = JsonSerializer.Serialize(payload);
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    // H11 / H31 — shared HttpClient + script CT. discord.webhook is fire-and-forget,
                    // but propagating the CT means a long-running script being cancelled / timing out
                    // can drop in-flight HTTP work cleanly.
                    using var reqMsg = new HttpRequestMessage(HttpMethod.Post, webhookUrl) { Content = content };
                    using var resp = await SendWithManualRedirectAsync(reqMsg, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.discord_error", "");
                }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.discord_error", ex.Message);
                    GlobalLogger.Log($"Discord Webhook Failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });
        }
    }

    /// <summary>
    /// SSRF guardrail for <c>discord.webhook</c>. Validates that the
    /// webhook URL targets a Discord-owned hostname over HTTPS and that every
    /// IP the hostname resolves to is a public, routable address. Rejects:
    ///   * non-https schemes
    ///   * hostnames not under *.discord.com or *.discordapp.com
    ///   * literal "localhost"
    ///   * any resolved IP that is loopback, link-local, unique-local, or in
    ///     RFC1918 private ranges
    /// DNS resolution happens before the post; the resolved address set is
    /// what the validator inspects, so a public hostname that happens to A-record
    /// 127.0.0.1 (a DNS-rebinding primitive) still fails.
    /// </summary>
    internal static class WebhookUrlGuard
    {
        private static readonly string[] s_allowedHostSuffixes =
        {
            ".discord.com",
            ".discordapp.com",
        };

        private static readonly string[] s_allowedHostExact =
        {
            "discord.com",
            "discordapp.com",
        };

        internal static async Task<(bool Allowed, string RejectReason)> ValidateAsync(
            string url, System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return (false, "url is empty");

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return (false, "url is not a valid absolute URI");

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                return (false, $"scheme '{uri.Scheme}' rejected (https required)");

            string host = uri.Host;
            if (string.IsNullOrEmpty(host))
                return (false, "host is empty");

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return (false, "localhost is blocked");

            bool hostAllowed = false;
            foreach (string exact in s_allowedHostExact)
            {
                if (string.Equals(host, exact, StringComparison.OrdinalIgnoreCase))
                {
                    hostAllowed = true;
                    break;
                }
            }
            if (!hostAllowed)
            {
                foreach (string suffix in s_allowedHostSuffixes)
                {
                    if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        hostAllowed = true;
                        break;
                    }
                }
            }
            if (!hostAllowed)
                return (false, $"host '{host}' not in Discord whitelist (*.discord.com / *.discordapp.com)");

            // Resolve and validate every IP. A literal IP in the URL still goes
            // through GetHostAddressesAsync, which returns the parsed address
            // for IP-formatted hostnames — same gate either way.
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (false, $"DNS resolution failed: {ex.Message}");
            }
            if (addresses == null || addresses.Length == 0)
                return (false, "DNS returned no addresses");

            foreach (IPAddress ip in addresses)
            {
                if (IsBlockedAddress(ip, out string reason))
                    return (false, $"resolved IP {ip} rejected: {reason}");
            }

            return (true, "");
        }

        private static bool IsBlockedAddress(IPAddress ip, out string reason)
        {
            // IPv4-mapped IPv6 (::ffff:a.b.c.d) — unwrap so the IPv4 ranges below
            // catch tricks like ::ffff:127.0.0.1 from sneaking through.
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();

            if (IPAddress.IsLoopback(ip))
            {
                reason = "loopback";
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                // 10.0.0.0/8
                if (b[0] == 10) { reason = "RFC1918 10.0.0.0/8"; return true; }
                // 172.16.0.0/12
                if (b[0] == 172 && (b[1] & 0xF0) == 16) { reason = "RFC1918 172.16.0.0/12"; return true; }
                // 192.168.0.0/16
                if (b[0] == 192 && b[1] == 168) { reason = "RFC1918 192.168.0.0/16"; return true; }
                // 169.254.0.0/16 link-local (covers AWS/GCP metadata 169.254.169.254)
                if (b[0] == 169 && b[1] == 254) { reason = "link-local 169.254.0.0/16"; return true; }
                // 0.0.0.0/8 — "this network", routes to local host on many stacks
                if (b[0] == 0) { reason = "0.0.0.0/8"; return true; }
                // 127.0.0.0/8 is covered by IsLoopback above for the canonical
                // 127.0.0.1, but other 127.x addresses can slip through some
                // stacks — keep an explicit guard.
                if (b[0] == 127) { reason = "loopback 127.0.0.0/8"; return true; }
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal) { reason = "IPv6 link-local fe80::/10"; return true; }
                if (ip.IsIPv6SiteLocal) { reason = "IPv6 site-local fec0::/10"; return true; }
                // Unique-local fc00::/7 — RFC4193 ULAs (private equivalent).
                byte[] b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) { reason = "IPv6 unique-local fc00::/7"; return true; }
            }

            reason = "";
            return false;
        }
    }
}
