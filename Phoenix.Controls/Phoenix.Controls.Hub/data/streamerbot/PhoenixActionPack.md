# Phoenix Controls — Streamer.bot Action Pack

Twitch action nodes in Architect (Shoutout, Ban, Timeout, VIP, Announcement,
Polls, Rewards, …) run **through Streamer.bot**. They do **not** send Twitch
chat slash-commands — Twitch removed IRC chat-commands in Feb 2023, and a
message like `/shoutout user` sent through an API just posts literal text and
executes nothing. The only path that works live is **Streamer.bot's own native
sub-actions** (Send Shoutout, Ban User, Chat Modes, …), which call Twitch's
Helix API for you.

So each Twitch action node calls a **named Streamer.bot action that you create
once** — exactly like the Chat Action you already configure for `send_chat`.
Hub sends `DoAction { action: { name }, args: { … } }`; the `args` arrive inside
the Streamer.bot action as `%variable%` references, which you wire into the
native sub-action's fields.

> **This file is the contract.** The names and argument variables below are what
> Hub sends — the Streamer.bot actions must match them exactly. Import the action
> pack (recommended) or build the actions by hand from the table.

> **Version requirements.** The Twitch + OBS set works on any Streamer.bot
> **0.2+**. The YouTube set needs Streamer.bot **1.0+** with the YouTube
> platform connected. The Kick set needs Streamer.bot **1.0.2+** with Kick
> connected (app OAuth + streamer.bot website account link).

> **The shipped `PhoenixActionPack.sb` includes the YouTube and Kick sets plus a
> custom-C# (Execute-Code) set added 2026-07-22 for the actions Streamer.bot's
> native sub-actions can't do** — reward cost/enable, fulfill/reject redemption,
> delete message (Twitch + Kick), whisper, sub-only mode, create poll, resolve
> prediction, and — added 2026-08-09 — **`Phoenix: Get Stream Status`**, which asks
> Twitch's Helix API directly for live state, viewer count and stream start time.
> Importing the pack creates all **57** actions in one go: **56** named
> `Phoenix: …` plus `PhoenixControlsChat`, the chat-send action Hub uses for
> `send_chat`.
> Only three documented actions are **deliberately not** in the pack because
> Streamer.bot 1.0.x can't back them at all — `Phoenix: YT Get User`, `Phoenix:
> Kick Set Reward Cost`, `Phoenix: Kick Set Reward Enabled`. Their Architect nodes
> are hidden from the palette; they're kept in the tables below only for reference.

---

## How to install

**Option A — import the pack (recommended).**
The pack ships with the app at **`data/streamerbot/PhoenixActionPack.sb`** (next
to this file). Open it, copy its **entire** contents, then in Streamer.bot:
**Import** → paste → review → **Import**. All **57** actions — **56** named
`Phoenix: …` (Twitch, OBS, YouTube, and Kick) plus `PhoenixControlsChat` — are
created with the correct names, field bindings, and the data-action / custom-C#
wiring — nothing to build by hand.
(Three documented actions Streamer.bot 1.0.x can't back are omitted on purpose —
`Phoenix: YT Get User`, `Phoenix: Kick Set Reward Cost`, `Phoenix: Kick Set
Reward Enabled`; their nodes are hidden in Architect.)

**Option B — build by hand.**
For each row in the table: create an Action with the **exact** name shown, add
the listed **native sub-action**, and set its fields to the `%variables%` shown.
Streamer.bot exposes a `DoAction` request's `args` as action variables of the
same name, so `args: { user }` → `%user%`.

After installing, (re)connect Hub to Streamer.bot. Hub probes `GetActions` on
connect and writes a line to the System Log telling you how many pack actions it
found — and names any that are missing.

---

## The contract — one Streamer.bot action per node

| Phoenix action name (exact) | Hub sends `args` | Streamer.bot native sub-action | Field bindings |
|---|---|---|---|
| `Phoenix: Shoutout` | `user` | Twitch → Send Shoutout | Target = `%user%` |
| `Phoenix: Timeout` | `user`, `duration` (seconds) | Twitch → Timeout User | User = `%user%`, Duration = `%duration%` |
| `Phoenix: Ban` | `user`, `reason` | Twitch → Ban User | User = `%user%`, Reason = `%reason%` |
| `Phoenix: Unban` | `user` | Twitch → Unban User | User = `%user%` |
| `Phoenix: Mod` | `user` | Twitch → Add Moderator | User = `%user%` |
| `Phoenix: Unmod` | `user` | Twitch → Remove Moderator | User = `%user%` |
| `Phoenix: VIP` | `user` | Twitch → Add VIP | User = `%user%` |
| `Phoenix: Unvip` | `user` | Twitch → Remove VIP | User = `%user%` |
| `Phoenix: Delete Message` — **not in the pack** | `messageId` | Twitch → Delete Message | Message Id = `%messageId%`. Node hidden. **Building this does not enable the Automod Delete rung** — Hub also needs the chat message id, which no part of this build supplies (see the Automod note above) |
| `Phoenix: Reply` | `messageId`, `message` | Twitch → Send Chat Message (reply) | Message = `%message%`, Reply-To Message Id = `%messageId%` |
| `Phoenix: Slow Mode` | `seconds` (0 = off) | Twitch → Chat Modes → Slow Mode | Duration = `%seconds%` (0 ⇒ set State = Off) |
| `Phoenix: Follower Mode` | `minutes` (-1 = off) | Twitch → Chat Modes → Follow Mode | Duration = `%minutes%` (-1 ⇒ set State = Off) |
| `Phoenix: Sub-Only Mode` | `enabled` (`true`/`false`) | Twitch → Chat Modes → Subscriber Mode | State = On when `%enabled%` = `true`, else Off |
| `Phoenix: Marker` | `description` | Twitch → Create Stream Marker | Description = `%description%` |
| `Phoenix: Whisper` | `user`, `message` | Twitch → Send Whisper | User = `%user%`, Message = `%message%` |
| `Phoenix: Update Channel` | `title`, `gameId` | Twitch → Set Channel Title (+ Set Channel Game) | Title = `%title%`; Game = `%gameId%` (see caveat) |
| `Phoenix: Announcement` | `message` | Twitch → Send Announcement | Message = `%message%` (colour is **fixed** — SB can't bind announcement colour to a variable) |
| `Phoenix: Create Clip` | `duration`, `title`, `req` | **C# (Execute Code)** — see [Data actions](#data-actions-readback-via-globals) | writes `phx_clip_url` / `phx_clip_ok` |
| `Phoenix: Create Poll` | `title`, `choices` (comma list), `duration` | Twitch → Create Poll | Title = `%title%`, Choices = `%choices%`, Duration = `%duration%` |
| `Phoenix: End Poll` | `pollId` (advisory) | Twitch → End Poll | acts on the active poll |
| `Phoenix: Create Prediction` | `title`, `outcomeA`…`outcomeE`, `duration` | Twitch → Create Prediction | Title = `%title%`, Outcomes = `%outcomeA%`…`%outcomeE%`, Duration = `%duration%`. **Only add the non-empty outcomes** — C/D/E are empty when the node wires fewer than 5, and Twitch rejects empty outcomes |
| `Phoenix: Update Reward Cost` | `rewardId`, `cost` | Twitch → Update Reward | Reward = `%rewardId%`, Cost = `%cost%` |
| `Phoenix: Set Reward Enabled` | `rewardId`, `enabled` (`true`/`false`) | Twitch → Update Reward | Reward = `%rewardId%`, Enabled = `%enabled%` |
| `Phoenix: Fulfill Redemption` | `redemptionId` | Twitch → Update Redemption | Redemption = `%redemptionId%`, Status = Fulfilled |
| `Phoenix: Reject Redemption` | `redemptionId` | Twitch → Update Redemption | Redemption = `%redemptionId%`, Status = Cancelled |

### OBS control (`obs.*` nodes)

OBS control also goes through Streamer.bot — its OBS sub-actions talk to OBS over
obs-websocket. (Phoenix's own OBS connection is event-only today, so it can't drive
these directly yet.) Sub-action names/fields vary by Streamer.bot version; verify
against your install.

| Phoenix action name (exact) | Hub sends `args` | Streamer.bot native sub-action | Field bindings |
|---|---|---|---|
| `Phoenix: OBS Set Scene` | `scene` | OBS → Set Scene | Scene = `%scene%` |
| `Phoenix: OBS Source Visible` | `scene`, `source` | OBS → Toggle Source Visibility | Scene = `%scene%`, Source = `%source%` — **toggles** on↔off (SB can't bind the state to a variable) |
| `Phoenix: OBS Refresh Browser` | `scene`, `source`, `link` | OBS → set Browser Source URL (+ refresh) | Source = `%source%`, URL = `%link%` — the action **sets the URL**, so the node now has a Url input |
| `Phoenix: OBS Start Recording` | — | OBS → Start Recording | — |
| `Phoenix: OBS Stop Recording` | — | OBS → Stop Recording | — |
| `Phoenix: OBS Start Streaming` | — | OBS → Start Streaming | — |
| `Phoenix: OBS Stop Streaming` | — | OBS → Stop Streaming | — |
| `Phoenix: OBS Save Replay` | — | OBS → Save Replay Buffer | — |
| `Phoenix: OBS Source Position` | `scene`, `source`, `x`, `y` | OBS → Set Source Transform | Position X = `%x%`, Position Y = `%y%` |
| `Phoenix: OBS Source Scale` | `scene`, `source`, `scaleX`, `scaleY` | OBS → Set Source Transform | Scale X = `%scaleX%`, Scale Y = `%scaleY%` |
| `Phoenix: OBS Source Rotation` | `scene`, `source`, `degrees` | OBS → Set Source Transform | Rotation = `%degrees%` |
| `Phoenix: OBS Filter Visible` | `scene`, `source`, `filter` | OBS → Toggle Filter Visibility | Source = `%source%`, Filter = `%filter%` — **toggles** on↔off |
| `Phoenix: OBS Screenshot` | `scene`, `source`, `path` | OBS → Take Source Screenshot | Source = `%source%`, Path = `%path%` |

---

## Data actions (readback via globals)

The **data nodes** (`get_user` / `check_role` / `get_stream` / `get_follow_age` /
`is_online`, plus `create_clip`) need data to come *back* to Hub. `DoAction` is
fire-and-forget — its reply is just an ack and carries no action output — so each
data action **fetches the data, writes it into non-persisted globals named
`phx_*`, and echoes Hub's per-call token into `phx_req` as its LAST sub-action.**
Hub fires the action, then polls `GetGlobals(persisted:false)` until `phx_req`
matches its token; because `phx_req` is written last, every other `phx_*` value
is guaranteed present by then.

Two rules for every `phx_*` Set-Global sub-action:

1. **Persisted = OFF** (these are transient scratch — Hub reads them with
   `persisted:false`).
2. **Auto Type = OFF** (store as strings; Auto Type would store ids as numbers /
   flags as bools, which Hub reads as plain strings).

And **`phx_req` must be the LAST sub-action**, its value exactly `%req%`.

| Phoenix action name (exact) | Hub sends `args` | Fetch sub-action | Globals written (Variable = Value) |
|---|---|---|---|
| `Phoenix: Get User` | `user`, `req` | Twitch → **Get User Info for Target** (`%user%`) | `phx_user_id`=`%targetUserId%`, `phx_user_login`=`%targetUserName%`, `phx_user_display`=`%targetUser%`, `phx_user_avatar`=`%targetUserProfileImageUrl%`, `phx_user_created`=`%createdAt%`, `phx_user_game`=`%game%`, `phx_user_title`=`%targetChannelTitle%`, `phx_user_mod`=`%targetIsModerator%`, `phx_user_sub`=`%targetIsSubscribed%`, `phx_user_vip`=`%targetIsVip%`, then `phx_req`=`%req%` |
| `Phoenix: Get Follow Age` | `user`, `req` | Twitch → **Get Follow Age Info for Target** (`%user%`) | `phx_follow_days`=`%followAgeDays%`, `phx_follow_date`=`%followDate%`, `phx_follow_is`=`%isFollowing%`, then `phx_req`=`%req%` |
| `Phoenix: Create Clip` | `duration`, `title`, `req` | **C# (Execute Code)** — `CPH.CreateClip(title, duration)` | `phx_clip_url`=clip URL, `phx_clip_ok`=`1`/`0`, then `phx_req`=`req` (written last) |
| `Phoenix: Get Stream Status` | `user`, `req` | **C# (Execute Code)** — `GET https://api.twitch.tv/helix/streams` with `CPH.TwitchOAuthToken` + `CPH.TwitchClientId` (blank `user` ⇒ `CPH.TwitchGetBroadcaster()`) | `phx_stream_known`=`1`/`0`, `phx_stream_live`=`1`/`0`, `phx_stream_login`, `phx_stream_viewers` (invariant integer), `phx_stream_started` (Twitch `started_at`, RFC3339 UTC; `""` offline), `phx_stream_title`, `phx_stream_game`, `phx_stream_err`, then `phx_req`=`req` (written last) |

`Phoenix: Get User` is reused by `check_role` (mod/sub/vip flags), and by
`get_stream` / `is_online` as their fallback when the status action is absent.
The Create Clip C# snippet ships in the Hub System Log message and in the project
docs.

**`Phoenix: Get Stream Status` is the only action that talks to Twitch's API
directly**, and it exists because Streamer.bot exposes no stream-liveness surface
at all — there is no CPH stream-info method, and `GetBroadcaster` / `GetInfo` /
`GetActiveViewers` are identity and chat-tracking requests (`GetActiveViewers` is
non-zero while offline, so it is not a liveness proxy). `%isLive%` exists only as
a *trigger* argument. Helix `/streams` needs **no scope** — an app or user token
both work — so Streamer.bot's own credentials are enough. It needs a
**broadcaster account connected in Streamer.bot**; with only a bot account the
token is unavailable and the action reports `phx_stream_known=0`.

`phx_stream_known` is load-bearing: `0` means *"we could not ask"*, which Hub must
never render as *"you are offline with no viewers"* — a failed call would
otherwise switch the live-gated tools off mid-stream. Every Hub caller falls back
to its previous behaviour on `0` instead of publishing the zeroed fields.

Hub dispatches it on Streamer.bot connect and then about once a minute for the
configured broadcaster, which is what lets `{stream.uptime}` and the live-gated
tools recover after starting or restarting Hub **mid-stream** (the go-live event
has already happened by then, so nothing else can arm them). One call per refresh
while live, up to three while offline — far inside Helix's ~800 points/minute.

---

## YouTube actions (7 in the pack) — requires Streamer.bot ≥ 1.0 with the YouTube platform connected

The YouTube nodes work exactly like the Twitch set: each node calls a **named
Streamer.bot action that you create once**, and the `args` arrive inside the
action as `%variable%` references you wire into the native YouTube sub-action's
fields.

> These 7 actions **ship in `PhoenixActionPack.sb`** — importing the pack creates
> them. The 8th, `Phoenix: YT Get User`, is **not available**: Streamer.bot 1.0.x
> exposes no YouTube user-info sub-action, so its node is hidden in Architect and
> the row below is reference-only.

| Phoenix action name (exact) | Hub command | Hub sends `args` | Streamer.bot native sub-action | Field bindings |
|---|---|---|---|---|
| `Phoenix: YT Send Chat` | `youtube.send_chat` | `message` | YouTube → Send Message to Channel | Message = `%message%` |
| `Phoenix: YT Set Title` | `youtube.set_title` | `title` | YouTube → Set Title | Title = `%title%` |
| `Phoenix: YT Set Description` | `youtube.set_description` | `description` | YouTube → Set Description | Description = `%description%` |
| `Phoenix: YT Timeout` | `youtube.timeout` | `user`, `duration` (seconds) | YouTube → Timeout User | User = `%user%` (by name), Duration = `%duration%` |
| `Phoenix: YT Ban` | `youtube.ban` | `user` | YouTube → Ban User | User = `%user%` (YouTube's ban carries **no reason** — user only) |
| `Phoenix: YT Create Poll` | `youtube.create_poll` | `question`, `choice1`…`choice4` | YouTube → Create Poll | Question = `%question%`, Choices = `%choice1%`…`%choice4%`. Hub splits the node's comma-separated **Choices** into up to 4 `choiceN` args; YouTube polls take **no duration** |
| `Phoenix: YT End Poll` | `youtube.end_poll` | — | YouTube → End Poll | acts on the active poll |
| `Phoenix: YT Get User` — **NOT AVAILABLE** | `youtube.get_user` | `user`, `req` | *(no YouTube user-info sub-action in SB 1.0.x)* | node hidden; not in the pack |

### YouTube data action — NOT AVAILABLE

`Phoenix: YT Get User` **has no live path in Streamer.bot 1.0.x**: unlike Twitch
and Kick, YouTube has no "Get User Info for Target" sub-action, so there is
nothing for the action to read. The `youtube.get_user` node is therefore hidden
in Architect and the action is **not** shipped in the pack. If a future
Streamer.bot adds a YouTube user-info sub-action, the node can be un-hidden and
the `phx_yt_*` global round-trip (identical to the Kick one below) wired up.

---

## Kick actions (10 in the pack) — requires Streamer.bot ≥ 1.0.2 with Kick connected (app OAuth + streamer.bot website account link)

The Kick nodes follow the same pattern: each node calls a **named Streamer.bot
action that you create once**, and the `args` arrive inside the action as
`%variable%` references you wire into the native Kick sub-action's fields.

> The 11 working actions below **ship in `PhoenixActionPack.sb`** — Kick Delete
> Message gained a custom C# `CPH.KickDeleteChatMessage` sub-action (2026-07-22).
> Two more are reference-only: **`Phoenix: Kick Set Reward Cost`** / **`Set Reward
> Enabled`** can't work — Kick rewards are fixed at Streamer.bot config time (no
> `%rewardId%` binding) and there is no Kick reward-management C# method. Those two
> nodes stay hidden in Architect.

| Phoenix action name (exact) | Hub command | Hub sends `args` | Streamer.bot native sub-action | Field bindings |
|---|---|---|---|---|
| `Phoenix: Kick Send Chat` | `kick.send_chat` | `message` | Kick → Send Message to Channel | Message = `%message%` |
| `Phoenix: Kick Reply` | `kick.reply` | `messageId`, `message` | Kick → Reply To Message | Message Id = `%messageId%`, Message = `%message%` |
| `Phoenix: Kick Timeout` | `kick.timeout` | `user`, `duration` (seconds) | Kick → Timeout User | User = `%user%`, Duration = `%duration%` |
| `Phoenix: Kick Ban` | `kick.ban` | `user`, `reason` | Kick → Ban User | User = `%user%`, Reason = `%reason%` |
| `Phoenix: Kick Unban` | `kick.unban` | `user` | Kick → Unban User | User = `%user%` |
| `Phoenix: Kick Untimeout` | `kick.untimeout` | `user` | Kick → UnTimeout User | User = `%user%` |
| `Phoenix: Kick Set Title` | `kick.set_title` | `title` | Kick → Set Channel Title | Title = `%title%` |
| `Phoenix: Kick Set Category` | `kick.set_category` | `category` | Kick → Set Channel Category | Category = `%category%` |
| `Phoenix: Kick Get User` | `kick.get_user` | `user`, `req` | **data action** — see below | writes the `phx_kick_*` globals |
| `Phoenix: Kick Delete Message` | `kick.delete_message` | `messageId` | **C# (Execute Code)** — `CPH.KickDeleteChatMessage(messageId)` | deletes the Kick message by id |
| `Phoenix: Kick Set Reward Cost` — **NOT AVAILABLE** | `kick.set_reward_cost` | `rewardId`, `cost` | *(Kick rewards fixed at config time — no `%rewardId%`)* | not in the pack; node hidden |
| `Phoenix: Kick Set Reward Enabled` — **NOT AVAILABLE** | `kick.set_reward_enabled` | `rewardId`, `enabled` | *(Kick rewards fixed at config time — no `%rewardId%`)* | not in the pack; node hidden |

### Kick data action

`Phoenix: Kick Get User` follows the **same contract as `Phoenix: Get User`**:
the action **fetches the data, writes it into non-persisted globals named
`phx_*`, and echoes Hub's per-call token into `phx_req` as its LAST
sub-action.** Hub fires the action, then polls `GetGlobals(persisted:false)`
until `phx_req` matches its token; because `phx_req` is written last, every
other `phx_kick_*` value is guaranteed present by then.

The same two rules apply to every `phx_kick_*` Set-Global sub-action —
**Persisted = OFF** and **Auto Type = OFF** — and **`phx_req` must be the LAST
sub-action**, its value exactly `%req%`.

| Phoenix action name (exact) | Hub sends `args` | Fetch sub-action | Globals written (Variable = Value) |
|---|---|---|---|
| `Phoenix: Kick Get User` | `user`, `req` | Kick → **Get User Info for Target** (`%user%`) | `phx_kick_id` = user id, `phx_kick_login` = login, `phx_kick_display_name` = display name, `phx_kick_profile_image` = profile image URL, `phx_kick_is_mod` = moderator flag, `phx_kick_is_sub` = subscriber flag, then `phx_req`=`%req%` |

The exact `%variable%` names the Kick user-info sub-action populates vary by
Streamer.bot version — check the sub-action's output variables in your install
and bind each `phx_kick_*` global to the matching one.

> **Kick has no mod/sub flags.** Streamer.bot's *Kick → Get User Info for Target*
> outputs id, login, display name and profile image, but **no** moderator or
> subscriber flag. Leave `phx_kick_is_mod` / `phx_kick_is_sub` unbound (or set to
> `false`); `kick.get_user` will always report `user.is_mod` / `user.is_sub` as
> `false`. Use Twitch's `check_role` where you need those flags.

---

## Caveats and known gaps

- **`twitch.send_chat` is not in the pack.** Sending chat already works through
  your existing **Chat Action** (Hub → Settings → Connection → Chat Action
  Name). Leave that as-is.
- **`twitch.resolve_prediction` resolves the LAST prediction by winning-outcome
  index** — the `Phoenix: Resolve Prediction` action uses SB's *Resolve Last
  Prediction* sub-action (`winningIndex = %outcome%`). The node takes a single
  `WinningOutcome` index (0 = first outcome); no prediction/outcome ids needed.
- **Poll / Prediction ids are not returned.** `Create Poll` / `Create Prediction`
  fire the poll/prediction but expose no id (`DoAction` can't return one), so those
  nodes have no `PollId` / `PredictionId` output. `End Poll` acts on the active
  poll; `Resolve Prediction` acts on the last prediction by winning-outcome index.
- **Stream live-metrics need `Phoenix: Get Stream Status`.** With that action
  imported, `is_online` and `get_stream` report real `is_live` / **viewer count**
  / **uptime** for **any** channel, not just your own, and `{stream.uptime}` is
  anchored to Twitch's own `started_at`. Without it, Streamer.bot has no
  liveness path at all: `get_user` / `get_stream` still return the channel's last
  **game** and **title** (offline-capable — the "last game played" data), but
  `is_live` answers truthfully only for **your own** channel (from the
  StreamOnline/StreamOffline events) and arbitrary channels report not-live. In
  that fallback the viewer count is reported as **empty, not `0`** — "we could
  not ask" is not the same claim as "you have no viewers".
- **Create Prediction empty outcomes.** The node sends up to five outcomes
  (`%outcomeA%`…`%outcomeE%`); unused ones are empty. Twitch rejects empty
  outcomes, so the action must add **only the non-empty** ones (e.g. gate each
  `Add Outcome` on `%outcomeC%` being non-empty), or always wire all five.
- **Whisper** requires the bot account to have a **verified phone number** and is
  rate-limited hard by Twitch (≈40 recipients/day). A correctly-wired action can
  still be rejected at Twitch.
- **Update Channel game** — the `Phoenix: Update Channel` C# accepts either a game
  **name** or a numeric game **id**: it calls `CPH.SetChannelGameById` for a numeric
  value, else `CPH.SetChannelGame`, so Hub's `gameId` works either way.
- **Version-dependent sub-actions.** *Create Stream Marker*, *Create Clip*
  options, *Update Reward*, *Update Redemption*, and the *Chat Modes* set vary by
  Streamer.bot version. Build against your installed Streamer.bot and confirm each
  sub-action exists; if one is absent, that node stays inert (and the connect
  probe will report the action as present but the sub-action will simply no-op).

---

## Status

The action pack **ships with the app** at
`data/streamerbot/PhoenixActionPack.sb` and creates **57 actions** on import
(**56** named `Phoenix: …` plus `PhoenixControlsChat`) — the full Twitch, OBS,
YouTube, and Kick surface that has a working
Streamer.bot 1.0.x path (native sub-actions + custom C# where the natives fall
short) — exported from and verified against a live Streamer.bot (exported from
1.0.4; minimum Streamer.bot 1.0.0 for Twitch/OBS, 1.0 for YouTube, 1.0.2 for Kick).

Only three documented actions are **reference-only** because Streamer.bot 1.0.x
can't back them even via custom C# — `Phoenix: YT Get User`, `Phoenix: Kick Set
Reward Cost`, and `Phoenix: Kick Set Reward Enabled`. Their Architect nodes are
hidden and the connect-probe does not expect them. The OBS *Source Position /
Scale / Rotation* nodes also aren't in the pack — Hub drives those over its own
OBS-WebSocket connection, falling back to a Streamer.bot relay only if you add
those actions yourself.

The names and argument variables are compiled into Hub
(`ScriptManager.PhxSbActions`) and are what the connect-probe checks for, so the
import and Hub stay in lockstep. Import the `.sb` (Option A) or build the
actions by hand from the tables — either way, Hub reports in the System Log
which actions it can and can't see once you connect.

> The `.sb` carries no secrets — it's pure action definitions (no Twitch tokens,
> no Streamer.bot credentials, no WebSocket server config). The data-action C#
> (Create Clip) is embedded as source inside it.
