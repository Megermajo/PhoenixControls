using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // QuotesService — the Hub-runtime brain of the databank-backed quote-store
    // pre-build tool (sibling of CountersService / LoyaltyService). Stateless-over-DB:
    // the OPEN "Quotes" table is the single source of truth, so the service NEVER
    // caches the quotes (a streamer db.* write must not be clobbered) — only the tool
    // CONFIG (command words + role gates + reply template) is held in memory, mirroring
    // the CountersConfig pattern. Every quote op delegates to one atomic DB call;
    // numbering (num = MAX+1, stable gaps) is enforced inside DB.QuoteAddAsync's
    // transaction.
    //
    // The service is Hub-side but cannot touch the script dispatcher directly, so the
    // RaiseScriptEvent (Quote.OnAdded) seam is injected from Hub
    // (RegisterQuotesCommands), exactly like Counters. NO overlay push (a quote store
    // has no live readout widget — any on-add flourish is authored via Quote.OnAdded →
    // an effect node → the shared VISUAL_TRIGGER pipeline).
    public sealed class QuotesService
    {
        private readonly DB _db;

        public QuotesService(DB db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        private static QuotesService? _instance;
        private static readonly object _instanceGate = new();
        public static QuotesService Instance
        {
            get
            {
                var i = _instance;
                if (i != null) return i;
                lock (_instanceGate) return _instance ??= new QuotesService(DB.Instance);
            }
        }

        // ── Config (cached; quotes are NEVER cached) ────────────────────────
        private volatile QuotesConfig _config = new();
        public QuotesConfig Config => _config;

        /// <summary>Master gate — false makes the built-in chat commands and the
        /// Quote.OnAdded event a total no-op. The Architect Quote.* nodes still perform
        /// the raw DB read/write regardless (like any script command) — only the
        /// reactive side-effects go dormant.</summary>
        public bool Active => _config.Enabled;

        // ── Injected Hub-side seam ──────────────────────────────────────────
        /// <summary>Raises a Quote.OnAdded script event (wired in RegisterQuotesCommands).</summary>
        public Action<string, IReadOnlyDictionary<string, string>>? RaiseScriptEvent { get; set; }

        // ── Change notifications (UI) ───────────────────────────────────────
        public event EventHandler? ConfigChanged;
        public event EventHandler? QuotesChanged;

        private void RaiseConfigChanged() => SafeEvent.Raise(ConfigChanged, this, EventArgs.Empty, "QuotesService", "ConfigChanged");
        private void RaiseQuotesChanged() => SafeEvent.Raise(QuotesChanged, this, EventArgs.Empty, "QuotesService", "QuotesChanged");

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // ── Lifecycle ───────────────────────────────────────────────────────
        public async Task InitializeAsync()
        {
            try
            {
                string? raw = await _db.LoadQuotesConfigAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var cfg = JsonSerializer.Deserialize<QuotesConfig>(raw!, JsonOpts);
                    if (cfg != null) _config = Normalize(cfg);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("QuotesService", "InitializeAsync: config load failed", ex);
            }
            try
            {
                await _db.EnsureQuotesTableAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("QuotesService", "InitializeAsync: quotes-table ensure failed", ex);
            }
            GlobalLogger.Log(
                $"QuotesService online — tool {(Active ? "ENABLED" : "disabled")}.",
                "QuotesService", LogLevel.System);
        }

        private static QuotesConfig Normalize(QuotesConfig cfg)
        {
            cfg.AddCommand    = string.IsNullOrWhiteSpace(cfg.AddCommand)    ? "addquote" : cfg.AddCommand.Trim();
            cfg.GetCommand    = string.IsNullOrWhiteSpace(cfg.GetCommand)    ? "quote"    : cfg.GetCommand.Trim();
            cfg.DeleteCommand = string.IsNullOrWhiteSpace(cfg.DeleteCommand) ? "delquote" : cfg.DeleteCommand.Trim();
            cfg.AddRoles    ??= QuoteRoles.Mods();
            cfg.GetRoles    ??= QuoteRoles.All();
            cfg.DeleteRoles ??= QuoteRoles.Mods();
            cfg.ReplyTemplate ??= "";
            return cfg;
        }

        // ── Config edits (panel) ────────────────────────────────────────────
        /// <summary>Replaces the whole config, persists it, and notifies the UI. The
        /// incoming object is deep-CLONED (JSON round-trip) so the panel VM can't alias
        /// the hot-path config instance (Counters/Automod lesson).</summary>
        public async Task SaveConfigAsync(QuotesConfig cfg)
        {
            _config = Normalize(Clone(cfg ?? new QuotesConfig()));
            try
            {
                string json = JsonSerializer.Serialize(_config, JsonOpts);
                await _db.SaveQuotesConfigAsync(json, NowUnixMs()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("QuotesService", "SaveConfigAsync failed", ex);
            }
            RaiseConfigChanged();
        }

        private static QuotesConfig Clone(QuotesConfig src)
        {
            try
            {
                string json = JsonSerializer.Serialize(src ?? new QuotesConfig());
                return JsonSerializer.Deserialize<QuotesConfig>(json) ?? new QuotesConfig();
            }
            catch { return new QuotesConfig(); }
        }

        // ── Quote operations (nodes + chat + panel) ─────────────────────────
        /// <summary>Adds a quote (num = MAX+1, stable gaps). Returns the new number and
        /// fires Quote.OnAdded when the tool is active.</summary>
        public async Task<int> AddAsync(string text, string name, string addedBy)
        {
            int number = await _db.QuoteAddAsync(text ?? "", name ?? "", addedBy ?? "").ConfigureAwait(false);
            RaiseQuotesChanged();

            // Quote.OnAdded is the tool's ACTIVE surface — gated by the master toggle so
            // a disabled tool raises no event. The Architect Quote.Add node still writes
            // the DB regardless (databank capability preserved); only the reactive event
            // goes dormant.
            if (Active)
            {
                var raise = RaiseScriptEvent;
                if (raise != null)
                {
                    try
                    {
                        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["event.number"] = number.ToString(CultureInfo.InvariantCulture),
                            ["event.text"]   = text ?? "",
                            ["event.name"]   = name ?? "",
                        };
                        raise("Quote.OnAdded", vars);
                    }
                    catch (Exception ex)
                    {
                        GlobalLogger.Error("QuotesService", "RaiseScriptEvent(Quote.OnAdded) failed", ex);
                    }
                }
            }
            return number;
        }

        /// <summary>Quote #num, or null when missing.</summary>
        public Task<QuoteEntry?> GetByNumberAsync(int num) => _db.QuoteGetByNumberAsync(num);

        /// <summary>A random quote, or null when the store is empty.</summary>
        public Task<QuoteEntry?> GetRandomAsync() => _db.QuoteGetRandomAsync();

        /// <summary>Total number of stored quotes.</summary>
        public Task<long> CountAsync() => _db.QuoteCountAsync();

        /// <summary>Deletes quote #num (survivors NOT renumbered). True when one went.</summary>
        public async Task<bool> DeleteAsync(int num)
        {
            bool gone = await _db.QuoteDeleteAsync(num).ConfigureAwait(false);
            if (gone) RaiseQuotesChanged();
            return gone;
        }

        /// <summary>Edits quote #num's text/name in place (panel + the Architect
        /// Quote.Edit node; no renumber). True when a row was updated — an unknown number
        /// is a false, never an insert.
        ///
        /// A null <paramref name="text"/> / <paramref name="name"/> leaves that column
        /// untouched (see <see cref="DB.QuoteUpdateAsync"/>). The panel always passes both
        /// non-null, so an emptied box there clears the field. The node maps its EMPTY pins
        /// to null by default, so a graph that fixes only the wording cannot silently wipe
        /// the attribution — and passes them through as "" once its ClearEmpty pin is on,
        /// which is what makes clearing a field reachable from a graph too.</summary>
        public async Task<bool> UpdateAsync(int num, string? text, string? name)
        {
            bool ok = await _db.QuoteUpdateAsync(num, text, name).ConfigureAwait(false);
            if (ok) RaiseQuotesChanged();
            return ok;
        }

        /// <summary>All quotes, ordered by number (panel).</summary>
        public Task<List<QuoteEntry>> ListAsync() => _db.QuoteListAsync();

        // ── Built-in chat-command core (testable; ScriptManager supplies the send) ──
        /// <summary>
        /// The built-in Quotes chat commands, factored here so they can be unit-tested
        /// without a full ScriptManager: <c>!&lt;add&gt; &lt;name&gt; &lt;quote&gt;</c>
        /// (name = first word, remainder = text), <c>!&lt;get&gt;</c> / <c>!&lt;get&gt; N</c>,
        /// <c>!&lt;del&gt; N</c>. Each op is gated by its role checkmark set; a
        /// role-denied command is still consumed (returns true) but produces no reply,
        /// mirroring the Counters parser. Replies route through
        /// <paramref name="replyAsync"/> (the ScriptManager wrapper feeds the
        /// per-platform send core; a test feeds a capturing callback). Returns true when
        /// a Quotes command was recognized (so the caller suppresses the author on_chat
        /// fan-out). DEFAULT-OFF IS A TOTAL NO-OP — returns false the instant the tool is
        /// disabled. Bot self-messages are already dropped upstream.
        /// </summary>
        public async Task<bool> TryHandleChatCommandAsync(ChatMessage msg, Func<string, Task> replyAsync)
        {
            if (!Active || msg is null) return false;
            replyAsync ??= static _ => Task.CompletedTask;

            var cfg = _config;
            string text = (msg.Message ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '!') return false;

            string body = text.Substring(1);
            int sp = body.IndexOf(' ');
            string token = (sp < 0 ? body : body.Substring(0, sp)).Trim();
            if (token.Length == 0) return false;
            string rest = sp < 0 ? string.Empty : body.Substring(sp + 1).Trim();

            // Verb match goes through the one shared canonicalizer: `token` has had
            // its '!' stripped by the parse above, the CONFIGURED word may still
            // carry one, and before ChatVerb only Polls and SongRequest handled
            // that — so a configured "!quote" was a dead command here. Argument
            // order is (token, configured); ChatVerb canonicalizes both sides.
            static bool Eq(string a, string b) => ChatVerb.Matches(b, a);
            // Role checks consult the User-Management group overlay (a group-granted
            // Mod/VIP/Sub passes like the platform rank, and the Regular tick resolves
            // off the same overlay; passthrough while dormant).
            var eff = UserManagementService.Instance.Effective(msg);
            bool RoleOk(QuoteRoles? r) =>
                r != null && r.Allows(eff.IsSub, eff.IsVip, eff.IsMod, msg.IsBroadcaster, eff.IsRegular);

            // GET — !<get> (random) / !<get> N (by number).
            if (Eq(token, cfg.GetCommand))
            {
                if (!RoleOk(cfg.GetRoles)) return true;   // consumed, silent

                QuoteEntry? q;
                if (rest.Length > 0 && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    q = await GetByNumberAsync(n).ConfigureAwait(false);
                    if (q == null) { await replyAsync($"Quote #{n} not found.").ConfigureAwait(false); return true; }
                }
                else
                {
                    q = await GetRandomAsync().ConfigureAwait(false);
                    if (q == null) { await replyAsync("No quotes yet.").ConfigureAwait(false); return true; }
                }
                await replyAsync(FormatQuote(cfg, q)).ConfigureAwait(false);
                return true;
            }

            // ADD — !<add> <name> <quote> (name = first word, remainder = text).
            if (Eq(token, cfg.AddCommand))
            {
                if (!RoleOk(cfg.AddRoles)) return true;   // consumed, silent

                (string name, string quote) = SliceNameAndText(rest);
                if (name.Length == 0 || quote.Length == 0)
                {
                    await replyAsync($"Usage: !{cfg.AddCommand} <name> <quote>").ConfigureAwait(false);
                    return true;
                }
                int number = await AddAsync(quote, name, msg.Username ?? "").ConfigureAwait(false);
                await replyAsync($"Added quote #{number}").ConfigureAwait(false);
                return true;
            }

            // DELETE — !<del> N.
            if (Eq(token, cfg.DeleteCommand))
            {
                if (!RoleOk(cfg.DeleteRoles)) return true;   // consumed, silent

                if (rest.Length == 0 || !int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                {
                    await replyAsync($"Usage: !{cfg.DeleteCommand} <number>").ConfigureAwait(false);
                    return true;
                }
                bool gone = await DeleteAsync(n).ConfigureAwait(false);
                await replyAsync(gone ? $"Deleted quote #{n}" : $"Quote #{n} not found.").ConfigureAwait(false);
                return true;
            }

            return false;   // not a Quotes command — author scripts handle it normally
        }

        /// <summary>Slices the inline add argument into (name, text): the name is always
        /// ONE word (token[0]); the text is the remainder after the first space.</summary>
        internal static (string Name, string Text) SliceNameAndText(string rest)
        {
            rest = (rest ?? string.Empty).Trim();
            if (rest.Length == 0) return ("", "");
            int sp = rest.IndexOf(' ');
            if (sp < 0) return (rest, "");                 // only a name, no quote → usage
            string name = rest.Substring(0, sp).Trim();
            string txt = rest.Substring(sp + 1).Trim();
            return (name, txt);
        }

        // Reply-template tokens, resolved in ONE pass (same shape as
        // CustomCommandsService.TokenRx). A chained .Replace() re-scans values it has
        // ALREADY substituted, so a quote whose text literally contains "{name}" got the
        // quoted person's name spliced into the viewer's words — the template is the
        // streamer's, the quote body is not.
        private static readonly Regex ReplyTokenRx = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

        /// <summary>Get-reply text: ReplyTemplate with {number}/{text}/{name}, else
        /// "Quote #N: &lt;text&gt; — &lt;name&gt;".</summary>
        internal static string FormatQuote(QuotesConfig cfg, QuoteEntry q)
        {
            string tmpl = cfg?.ReplyTemplate ?? "";
            string numStr = q.Number.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(tmpl))
                return $"Quote #{numStr}: {q.Text} — {q.Name}";

            var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["number"] = numStr,
                ["text"]   = q.Text ?? "",
                ["name"]   = q.Name ?? "",
            };
            // Unknown tokens stay verbatim (m.Value) — same contract as custom commands.
            return ReplyTokenRx.Replace(tmpl,
                m => tokens.TryGetValue(m.Groups[1].Value, out string? v) ? v : m.Value);
        }
    }
}
