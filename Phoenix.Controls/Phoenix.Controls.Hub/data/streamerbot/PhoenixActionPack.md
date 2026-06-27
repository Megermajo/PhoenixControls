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

---

## How to install

**Option A — import the pack (recommended).**
The pack ships with the app at **`data/streamerbot/PhoenixActionPack.sb`** (next
to this file). Open it, copy its **entire** contents, then in Streamer.bot:
**Import** → paste → review → **Import**. All 29 `Phoenix: …` actions are created
with the correct names, field bindings, and the data-action C# / globals wiring —
nothing to build by hand.

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
| `Phoenix: Delete Message` | `messageId` | Twitch → Delete Message | Message Id = `%messageId%` |
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

`Phoenix: Get User` is reused by `check_role` (mod/sub/vip flags) and `get_stream`
(last game/title). The Create Clip C# snippet ships in the Hub System Log message
and in the project docs.

---

## Caveats and known gaps

- **`twitch.send_chat` is not in the pack.** Sending chat already works through
  your existing **Chat Action** (Hub → Settings → Connection → Chat Action
  Name). Leave that as-is.
- **`twitch.resolve_prediction` has no live path.** Resolving a prediction needs
  the prediction id + winning-outcome id, which only the create call can mint and
  which Streamer.bot cannot return over the WebSocket. The node logs a clear
  message and takes no action; it stays deferred until Hub gains direct Twitch
  access.
- **Poll / Prediction ids are not returned.** `Create Poll` / `Create Prediction`
  fire the poll/prediction, but `DoAction` can't return a value, so the `PollId` /
  `PredictionId` output sockets were **removed** from those nodes. `End Poll` acts
  on the active poll (no id needed); `Resolve Prediction` needs ids Streamer.bot
  can't return, so it's deferred and logs instead of firing.
- **Stream live-metrics are broadcaster-only.** Streamer.bot exposes no way to
  read `is_live` / viewer count / uptime for an **arbitrary** channel.
  `get_user` / `get_stream` return the channel's last **game** and **title**
  (offline-capable — this is the "last game played" data), but `is_online` and
  `get_stream.is_live` answer truthfully only for **your own** channel (Hub
  tracks it from the StreamOnline/StreamOffline events). Arbitrary channels
  report not-live. Viewer count for your own channel isn't wired yet (`0`).
- **Create Prediction empty outcomes.** The node sends up to five outcomes
  (`%outcomeA%`…`%outcomeE%`); unused ones are empty. Twitch rejects empty
  outcomes, so the action must add **only the non-empty** ones (e.g. gate each
  `Add Outcome` on `%outcomeC%` being non-empty), or always wire all five.
- **Whisper** requires the bot account to have a **verified phone number** and is
  rate-limited hard by Twitch (≈40 recipients/day). A correctly-wired action can
  still be rejected at Twitch.
- **Update Channel game** — Hub passes `gameId`. Streamer.bot's *Set Channel
  Game* sub-action usually wants a **category name**, not an id. If your title
  changes but the game doesn't, switch the node to pass a game name, or have the
  action resolve the id → name first.
- **Version-dependent sub-actions.** *Create Stream Marker*, *Create Clip*
  options, *Update Reward*, *Update Redemption*, and the *Chat Modes* set vary by
  Streamer.bot version. Build against your installed Streamer.bot and confirm each
  sub-action exists; if one is absent, that node stays inert (and the connect
  probe will report the action as present but the sub-action will simply no-op).

---

## Status

The action pack **ships with the app** at `data/streamerbot/PhoenixActionPack.sb`
— 29 `Phoenix: …` actions exported from and verified against a live Streamer.bot
(exported from 1.0.4; minimum 1.0.0). The names and argument variables are
compiled into Hub (`ScriptManager.PhxSbActions`) and are what the connect-probe
checks for, so the import and Hub stay in lockstep. Import the `.sb` (Option A) or
build the actions by hand from the tables — either way, Hub reports in the System
Log which actions it can and can't see once you connect.

> The `.sb` carries no secrets — it's pure action definitions (no Twitch tokens,
> no Streamer.bot credentials, no WebSocket server config). The data-action C#
> (Create Clip) is embedded as source inside it.
