# Changelog

All notable changes to Phoenix Controls are documented here.

**v1.0** is the first public release — the starting point. From here on, each
release lists what's **New**, **Fixed**, and **Gone** in plain language.

---

## Update - 1.1 - 2026.08.15

> ## ⚠ Before you use this update — read this first
>
> **1. Re-import the Action Pack.** This update adds new Streamer.bot actions. Without them the new viewer-count and stream-uptime readouts stay blank and some Architect nodes do nothing. In Hub open the **File** menu and choose **"Open Action-Pack Folder"**, open the **PhoenixActionPack.sb** file and copy everything in it. Then in Streamer.bot open **Import**, paste it in and click **Import**.
>
> **2. Every ready-made tool starts switched off.** Version 1.1 adds fourteen of them and none of them touch your chat until you turn them on. Open the new **Pre-Builds** tab and switch on what you want, one at a time.
>
> **3. Moderator, VIP and Subscriber membership comes from the platform.** Those three groups are usable everywhere a group is usable — a role checkbox, a node, a script check — but who is in them is whatever Twitch, YouTube or Kick says. To grant rights the platform does not, use the **Regular** group or a custom group; both can also be earned through watch hours.

**Version 1.1 — the ready-made tools**

**New**
- Phoenix now ships fourteen ready-made tools. Each is a page you switch on and fill in — no graph building required — and they live under a new **Pre-Builds** tab, listed down the left the way Settings does it and grouped by what they do.
- **Loyalty** — a channel points system with a wallet, an earn engine, a reward store and built-in mini-games.
- **Timer** — countdown, stopwatch and subathon clock that events can add time to, with goals that post to chat and play an overlay effect.
- **Song Requests** — viewers request tracks from chat, with a queue, per-viewer limits and role rules, and a player that can run in your overlay.
- **Polls & Betting** — run a poll from chat, or take bets and pay the winners out.
- **Ranks** — viewers climb named ranks as they earn, and can check where they stand from chat.
- **Soundboard** — chat-triggered sound effects, with cooldowns and role limits.
- **Alerts** — tiered chat responses for follows, subs, gift subs, gift bombs, bits and raids, each with an optional overlay effect and a raid auto-shoutout.
- **Automod** — a spam and word filter with rules, escalation and a permit command; it can delete a message outright instead of only timing the viewer out.
- **User Management** — welcomes each viewer's first message per stream, greets brand-new chatters once ever, and grants rights through groups.
- **Scheduling** — recurring timed chat messages, with an only-while-live gate and a hold-off so a quiet chat is not spammed.
- **Counters** — named counts (deaths, wins, anything) that chat and your logic can read and change.
- **Quotes** — save and recall chat quotes.
- **Custom Commands** — your own text and variable chat commands without building a graph.
- **Viewer queue** — viewers line up from chat, subscribers and VIPs can be weighted ahead of everyone else, and you can see and manage the line.
- **Giveaway** works in both places — inline like every other tool, or in its own window for a second monitor.
- Every tool shows the chat words it answers to, and you can change them.
- The tools feed Architect as well. Twelve of the fourteen raise their own trigger nodes, and the data they keep sits in databank tables your graphs can read and write directly — so anything a tool does, you can also build on. Alerts and Scheduling are the two that do not expose nodes yet.
- Every tool page carries an activity list showing what it has actually been doing, and a status light that says when something is on but waiting, or held up by something else being off.
- Watch time is recorded in the background, with every tool switched off. It needs Streamer.bot connected, and by default it counts only while your stream is detected live.
- The Regular group can be earned by watch hours. The number is checked live rather than written into a member list, so changing it applies to everyone at once.
- Custom groups can carry a watch-hour rule of their own, readable from your scripts and nodes.

**New — everywhere else**
- German, French and Spanish coverage has been extended across the app.
- Tips and donations from ten services reach your logic as trigger nodes, with the amount and currency worked out for you. Turning them into a chat line and an overlay effect is the Alerts tool's job, once you have set up a tier.
- Twitch goals and charity campaigns reach your overlays.
- Overlay widgets read live channel data, so counts and goals show real numbers.
- Viewer count and stream uptime are real numbers.
- Chat commands and scheduled messages can show your live stream title and game with {title} and {game}; scripts get them too.
- New **Alert Box** preset in Visualist — a complete alert built from settings.
- New Clock, Countdown and Stopwatch widgets for your overlays.
- New **Custom Web Overlay** — put live HTML and CSS straight into an overlay.
- A widget's media source can be picked at trigger time instead of being fixed when you build it.
- Node thumbnails in Visualist follow the timeline as you scrub it.
- Visualist gained a layer manager, resizable panels, composition guides for thirds, centre and safe areas, a richer inspector, a better timeline, and editing several widgets at once.
- The databank is yours — 23 of the 25 built-in tables can be edited from your scripts and from the databank browser. Two stay protected: the paired-device list and the remote audit log.
- Role checks offer Regular alongside Moderator, VIP and Subscriber.
- Wider Twitch event coverage across the trigger nodes.
- Motion throughout the app — tools fade in, values flash when they change, new rows slide into live lists.

**Fixed**
- Keyframe animation authored in Visualist now plays in OBS.
- Architect's Inspector is a small panel in the top-right corner instead of a full-height side panel, and the part of the canvas it used to cover is usable again.
- Home, F and clicking the mini-map centre your graph on the part of the canvas you can actually see.
- A graph could open looking empty while the mini-map showed it perfectly. Fixed.
- The mini-map no longer jumps when you move, add or delete a node at the edge of your graph.
- Architect's Live Debug node flash fires again while a script runs. It had never once fired in a released build.
- Architect: a chat command containing a quote mark no longer exports a broken script.
- Architect: the Edit menu's Group command works, and no longer clashes with the show-grid shortcut.
- Architect: a graph opened in a background tab re-centres itself when its saved view points off the canvas.
- Architect: a pin whose variable is animated in Visualist shows its marker as soon as the keyframes are added.
- Architect's graph checks no longer report node names that are not there.
- The Live Feed no longer files chat commands as stream events. "!subs" from anyone was shown as a subscription, as was any command from a viewer whose name contains "sub".
- The Live Feed's Who column shows the viewer again, on every kind of event, and right-click "filter to this user" works again.
- The Scripts panel gives each running process its own row, instead of stacking them onto one row whose name kept changing.
- Command detection agrees between the tools and your graphs. A line starting with an invisible character could raise a command nothing was able to answer.
- Pop-ups — saving before exit, restoring a backup, recovering unsaved work — use the app's own dark styling instead of Windows'. Their main button is amber rather than system blue.
- The remote relay's LAN switch stays on. It showed as off every time Settings opened, and saving Settings quietly switched it back off.
- Hotkey and clipboard scripts have their own run limits instead of sharing one with webhook scripts.
- Scripts still running when Phoenix closes are cancelled as part of the shutdown, and closing the app no longer loses the last lines of the log.
- Hub's own panels receive the messages Hub itself broadcasts.
- Overlay effects report back when they finish, so logic waiting on them carries on.
- Resizing the layer canvas no longer distorts the widget preview.
- Visualist's Inspector header shows its tooltips, and editing is steadier — reliable undo on QWERTZ keyboards and no more sticky drags.
- Long streams with rotating image URLs no longer grow memory in the overlay asset cache.
- The Documentation window no longer describes windows and buttons that were never shipped.
- Faster, lighter performance across the whole suite.

**Gone**
- Removed the old (non-functioning) Remote Bridge. A working replacement for remote work will follow with v1.2.

## Hotfix - 1.0.2607261 - 2026.07.26

> Heads up — if this particular update fails to install once, restart the PC and run it again; from this version on, that cause is gone.

**Fixed**
- Closing the Hub now always ends the app completely — no invisible leftover process in Task Manager.
- Updates no longer fail to install after the app was used and closed — restarting the PC is no longer needed.
- The Hub can no longer refuse to start because an invisible leftover from the previous session was still running.
- Leftover browser helper processes are cleaned up when the Hub closes.
- File → Exit now behaves like the window's close button — it asks about unsaved Architect or Visualist work first.
- File → Exit and "Install & restart" now run the full shutdown instead of skipping it.
- The updater clears a stuck old instance on its own before installing.
- The updater's log now states clearly when Windows was still holding the old files and that a restart clears it.
- The updater now also closes the separate Viewer window before installing an update.
- Right-clicking empty canvas in Architect now always opens the add-node menu.
- Right-clicking empty canvas no longer clears the nodes you had selected.
- Architect's right-click "Spawn" menu is easier to browse — crowded categories now open as themed sub-menus.
- The Platforms, Events, Flow Control, Databank, OBS, and Collections node lists are grouped instead of one long flat list.

## Hotfix - 1.0.2607231 - 2026.07.23

> Heads up — the freeze investigation is still ongoing. This update lightens the load on the areas where freezes have occurred and adds deeper diagnostics to close in on the remaining cause; it is not the final fix yet.

**New**
- Freeze reports now record how busy the processor, memory, and graphics card were at the moment of a freeze.
- A new optional deep-diagnostics setting captures a complete snapshot on the next freeze, to help pin down the remaining freeze cause.

**Fixed**
- Architect puts noticeably less load on the graphics card while drawing node graphs.
- Node text in Architect is drawn from a cache instead of being rebuilt over and over.
- Opening or switching a graph in Architect no longer does its reset work twice.
- The Architect minimap no longer keeps working in the background while it is hidden.
- The visible minimap stays smooth while a busy graph is being edited.
- A burst of quick script saves no longer briefly stalls the Architect window.

## Small update - 1.0.2607221 - 2026.07.22

> Heads up — this update adds new Streamer.bot actions, so you need to import the Action Pack again for them to work. In Hub, open the **File** menu and choose **"Open Action-Pack Folder"**, open the **PhoenixActionPack.sb** file, and copy everything in it. Then in Streamer.bot open **Import**, paste it in, and click **Import**.

**New**
- Architect's "Get Stream" node now reports whether your channel is live.
- Architect has a new "Chat Message Count" value your logic can read.
- Architect's "Recurring Schedule" trigger can wait until chat has been active before it runs.
- The chat-message trigger now carries the message's ID, so replies and message-deletes have something to target.
- Press C in Architect to wrap the selected nodes in a comment frame.
- Architect can now change a channel-point reward's cost and turn rewards on or off.
- Architect can now delete a chat message on Twitch and on Kick.
- Architect can now send a whisper, switch subscriber-only chat on or off, and create a poll.
- Architect's "Resolve Prediction" node now closes the last prediction on your channel.
- "Fulfill Redemption" and "Reject Redemption" now run on their own inside a channel-point redemption.

**Fixed**
- Architect's Edit → Group menu item, and Ctrl+G, group the selected nodes again.
- Ctrl+Shift+G now toggles the canvas grid instead of grouping nodes.
- Corrected several cards in Architect's built-in Node Reference, including which node to use to reply to a whisper, and added the "Going Live" and "Session End" triggers.
- The Terms of Service shown on first launch are written in clearer, plainer language. What the terms mean is unchanged.

## Hotfix - 1.0.2607211 - 2026.07.21

**New**
- Whispers sent to your bot account now appear in Hub's Live Feed.
- The Live Feed has a new "Whispers" filter to pull them up on their own.
- Architect has a new "Whisper Received" trigger that runs your logic when someone whispers the bot.
- The new trigger hands your logic the sender, their id, and the message.
- Whisper text stays on screen only — the saved log records who whispered, never what they wrote.
- Phoenix Controls now shows its Terms of Service the first time you start it, and again whenever the terms change.

**Fixed**
- A cooldown with no viewer assigned no longer blocks its command forever, so commands like a raffle or a wheel start work again.
- Cooldowns now keep two timers side by side: one for the whole channel, one per viewer.
- Setting a cooldown's global time to zero switches that timer off.
- German, French, and Spanish now cover the whole app — around 220 labels that were still showing English are translated.
- Missing translations can no longer slip in unnoticed.
- Update backups are cleared out after seven days instead of piling up.
- Update cleanup no longer deletes a backup that still holds your only copy of an overlay file.
- Steadier update cleanup overall — empty and duplicate backups go immediately, one rollback snapshot is kept.

> Heads up — open and re-save your graphs in Architect once for the cooldown changes to take effect. After that a cooldown's global time applies to the whole channel; set it to 0 on a node to keep the per-viewer timer only.

## Hotfix - 1.0.2607201 - 2026.07.20

**Fixed**
- Fixed a remaining case where Architect could still freeze when you started editing a node while panning or zooming.
- Steadier focus handling when opening a node's text field mid-gesture.

## Hotfix - 1.0.2607191 - 2026.07.19

**New**
- Architect has a new "Going Live" trigger that runs your logic the moment your stream starts.
- Architect has a new "Session End" trigger that runs your logic when your stream ends.
- Both new triggers let you choose which platforms they react to: Twitch, YouTube, or Kick.

**Fixed**
- Updates now install reliably even when the app was open earlier in the same session, without needing a PC restart before the update can apply.
- Architect no longer freezes when you edit a node's text while panning or zooming the graph.
- Steadier, more reliable editing on the Architect canvas during live changes.
- Typing in a node's text field no longer sets off menu shortcuts by accident.
- Wires no longer drift away from their pins while you edit multi-line node text.

## Hotfix - 1.0.2607172 - 2026.07.17

**New**
- Phoenix Controls now recovers on its own if the app ever freezes: after a short spell of an unresponsive window it saves troubleshooting details and restarts itself automatically, so you are back up without having to close and reopen it. It will not restart in a loop, and you can turn it off in settings.
- When a freeze happens, Phoenix now writes an easy-to-read report explaining the most likely cause - including whether your graphics driver stopped responding - so problems are much faster to pin down.

**Fixed**
- Brief slowdowns that used to leave no trace are now recorded, and freezes are captured in far more detail.

## Hotfix - 1.0.2607171 - 2026.07.17

**New**
- Hub now warns you right away if any of your scripts or overlays are missing after an update, instead of starting up quietly.

**Fixed**
- Updates now keep your scripts and overlays safe even when file recovery only partly finishes - nothing is cleared while a backup still holds your only copy.
- A recovery that does not fully complete is retried the next time you start Hub, instead of being treated as finished.
- Your edited copy of a built-in script is now kept when an update ships a new version of that script.
- Improved the reliability of update recovery so it no longer discards files it did not manage to restore.

## Hotfix - 1.0.2607144 - 2026.07.14

**Fixed**
- Scripts tied to a live process now run as a single instance instead of stacking duplicate copies of themselves.
- Turning a script off now reliably stops it - the disable switch is respected everywhere the script runs.
- Removed a source of slowdown where the same script could fire repeatedly and pile up on itself.

## Hotfix - 1.0.2607143 - 2026.07.14

**New**
- Hub: giveaways can charge channel points per ticket - pick the currency table on the ticket node, set the price in the giveaway's settings, and entries are paid automatically.
- Architect: a "Buy as many as possible" switch on the Giveaway Ticket node converts a viewer's points into tickets, up to their points and the per-user limit.
- Architect: the Giveaway Ticket node reports how many tickets a call actually bought, and a dedicated branch fires when a viewer cannot afford the entry.
- Architect: the ticket node's table picker can create the standard "ChannelPoints" currency table with one click.
- Hub: the Giveaway settings gained a "Ticket price" field (0 = free).
- Architect: one "Chat Send" node replaces the per-platform send nodes - Twitch/YouTube/Kick checkmarks pick the targets, and a Platforms input can override them at runtime.
- Architect: the Giveaway Ticket node reads the entrant's subscriber and moderator status through new IsSub/IsMod inputs.
- Hub: giveaways gained a "Moderator bonus" draw weight next to the subscriber bonus.
- Architect: a new "Giveaway Is Active" node tells scripts whether a giveaway is currently open.
- Architect: "Var Set" nodes pass their stored value onward through a new Value output.
- Hub: after an update, the changelog opens once on first start so you can see what changed.
- Streamer.bot: the bundled action pack now includes ready-made YouTube and Kick actions - no more building them by hand.
- Architect: a "Twitch Reply" node and a MessageId output on the Chat Message trigger lay the groundwork for replying to specific chat messages. Live chat does not fill the message ID yet, so replies activate in a coming update.
- Architect: the Kick Ban node accepts an optional reason.
- Hub: hovering any pin on a giveaway node shows an explanation of what it does.

**Fixed**
- Architect: reroute knots can be moved again - the left half drags the knot, the right half starts a wire.
- Hub: giveaway, graph-editor, and preview windows open in front of the main window instead of behind it.
- Hub: older exported scripts calling the Giveaway Ticket with unfilled values no longer mix up their result branches.
- Hub: the app records diagnostic details when the interface freezes, to help pin down hangs.
- Hub: closed a series of rare crash paths across the Hub, the script exporter, and the overlay, found in an internal stability review.
- Architect: the YouTube Create Poll node now sends the question and up to four choices the way Streamer.bot expects.
- Architect: freshly placed user-lookup nodes (Get User, Check Role, Follow Age, Last Active) now look up the chatting user instead of sending a placeholder.
- Architect: the State "On Change" trigger's Name, OldValue, and NewValue outputs now carry the real values instead of staying empty.
- Architect: OBS position, scale, and rotation nodes accept decimal values on their pins.
- Hub: no more warnings about action-pack entries that cannot exist, after connecting to Streamer.bot.
- Hub: scripts no longer wait several seconds when asking for data from an action the pack does not provide.
- Architect: many corrections across the built-in node reference, node tooltips, and first-run tips.

**Gone**
- Architect: the Giveaway Ticket node's free-text "Role" input was replaced by the IsSub/IsMod inputs - saved graphs convert automatically.
- Architect: the separate Twitch/YouTube/Kick "Send Chat" nodes merged into "Chat Send" - saved graphs convert automatically.
- Architect: the YouTube Create Poll node's Duration input was removed - Streamer.bot sets the poll length.
- Architect: YouTube Get User, Kick Delete Message, and the Kick reward nodes left the node menu - Streamer.bot cannot perform them. Saved graphs still load.

## Hotfix - 1.0.2607142 - 2026.07.14

**Fixed**
- Hub: updating no longer deletes the scripts stored in the logic folder.
- Hub: updating no longer deletes the layers, media, and settings stored in the install folder.
- Hub: files an earlier update removed are brought back automatically the next time Phoenix Controls starts.
- Hub: update backups are kept longer while they still hold files that were not brought back yet.
- Installer: reinstalling no longer replaces scripts or layers you edited.
- Installer: uninstalling leaves your scripts, layers, and media on disk.

> Heads up — An earlier update could remove the scripts and layers stored inside the install folder. We are truly sorry about this. Your files are not lost: they were moved to a "Phoenix Controls.bak.(date)" folder right next to the install folder, and this version brings them back automatically the first time it starts. Updating with the built-in updater is safe — your files reappear on the first start after the update. If anything is still missing afterwards, copy it from that folder back into Hub\data\.

## Hotfix - 1.0.2607141 - 2026.07.14

**New**
- Hub: giveaways can set a "Max tickets per user" limit, editable per giveaway in the Giveaway panel (0 = unlimited).
- Hub: the giveaway "Subscriber bonus" is editable - set how much extra weight a subscriber's tickets carry in the winner draw (1 = no bonus).
- Hub: before a draw with a subscriber bonus, every entrant's current subscriber status is checked live through Streamer.bot.
- Hub: a ready-to-use giveaway entry script ships with the app - viewers can earn tickets with !ticket right after install.
- Hub: the Giveaway settings panel can be collapsed and expanded.
- Architect: the Giveaway Ticket node gained a "Limit" output that fires when a viewer hits the ticket cap, so scripts can answer with their own message.

**Fixed**
- Architect: keyboard shortcuts no longer fire while typing in a text field - letters stay in the text instead of triggering canvas actions.
- Architect: the first edit of a value pill in a freshly opened window no longer makes the node disappear.
- Architect: auto-scaling text pills no longer cut off the last letters.
- Architect: branch conditions built on checks like cooldowns or databank lookups work again - they previously always came out false.
- Architect: once an "Else If" chain matches a branch, the later branches are skipped - a script no longer runs two branches of the same ladder.
- Architect: combined conditions stop early - a cooldown check placed behind an already-decided condition no longer fires or starts its cooldown.
- Architect: a check used on both sides of one comparison now runs once instead of twice.
- Architect: saving a script with a broken macro or process body now says what is wrong instead of pointing at nothing.
- Hub: waits and delays no longer count against the script time limit - timed flows like a raffle that sleeps for minutes now finish instead of being cut off.
- Hub: a script sleeping on a delay no longer holds up chat - new messages and other commands keep flowing while it waits.
- Hub: parallel branches with their own waits no longer cut each other's remaining time short.
- Hub: reading more command words than a viewer typed now gives empty values instead of leftover text.
- Hub: script commands that expect whole numbers now round decimal inputs to the nearest whole number instead of treating them as 0.
- Hub: point amounts calculated from percentages or divisions no longer silently add 0.
- Hub: lowering a giveaway's ticket limit never takes away tickets viewers already earned.
- Hub: the ticket-limit box in the Giveaway panel no longer resets while you are typing in it.
- Hub: the winner overlay's "1 in N" odds now match the draw's real, bonus-weighted odds.
- Hub: double-clicking "Draw winner" can no longer pick two winners at once.
- Hub: a draw no longer stalls for minutes when many entrants have left chat.
- Hub: entrant badges refresh from the live subscriber check before a draw.

**Gone**
- Hub: the unused "Entry command", "Tickets per message" and "Draw method" rows left the Giveaway settings - ticket entry comes from the entry script.

## Hotfix - 1.0.2607121 - 2026.07.12

**New**
- Architect: OBS nodes to move, scale, and rotate scene sources are now available — they control OBS directly.
- Hub: choose the translation service (DeepL, Google, or LibreTranslate) in Settings — changes apply without a restart.
- Hub: the status bar shows what the script engine is currently doing.
- Viewer: the browser view shows its connection latency and how long ago it last synced.

**Fixed**
- Architect: disabled nodes are now skipped correctly when a script runs — the flow continues past them instead of stopping.
- Architect: disabling a trigger node now stops that script from firing at all.
- Architect: "Do Once", "Do N", and "Flip Flop" nodes start fresh when a script is edited — they no longer stay used up from before the change.
- Hub: the script overlap setting is now enforced — a script that is already running skips or waits as configured.
- Hub: the connection dots show a clear error when sign-in fails and an in-progress state while reconnecting.
- Viewer: the connection display shows a warning when Streamer.bot is connected but Twitch is not linked.
- Hub: the overlay server reports a clear error if it stops working, instead of failing silently.
- Hub: switching between the Hub, Architect, and Visualist tabs is more reliable.
- Improved reliability of script event handling.
- Visualist: hovering over a node pin highlights its connected wires.
- Visualist: web images show a preview on the node while editing.
- Visualist: a widget whose image cannot load shows "(image unavailable)" instead of staying blank.

## Hotfix - 1.0.2607101 - 2026.07.10

**New**
- YouTube Live support: chat messages, stream events, and viewer data now work alongside Twitch.
- Kick support: chat messages, stream events, and viewer data now work alongside Twitch.
- Architect: new YouTube and Kick action nodes — send chat, moderate, and update the stream from a script.
- Architect: new nodes to look up YouTube and Kick user details in a script.
- Architect: one unified "Chat Message" trigger node with a checkmark per platform — choose which platforms a script listens to.
- Hub: the Chat panel shows which platform each message came from.
- Existing graphs update to the new Chat Message node automatically — Twitch-only scripts behave exactly as before.

**Fixed**
- Hub, Architect, and Visualist all run noticeably smoother after a broad performance pass.
- Architect: the canvas goes quiet when you are not interacting — no more constant background work.
- Architect: editing values and titles on large graphs responds faster.
- Architect: holding an arrow key to nudge nodes counts as one undo step instead of many.
- Visualist: dragging a slider counts as one undo step instead of one per movement.
- Databank: large tables scroll smoothly and the column headers stay aligned with the rows.
- Hub: Chat and Live Feed keep your reading position when old rows are trimmed.
- Overlays reconnect cleanly when an OBS browser source reloads — stale connections no longer pile up.
- The "Open Recent" list shows newly saved files right away.
- Old log entries are cleaned up in the background in small steps, without causing pauses.

## Hotfix - 1.0.2607051 - 2026.07.05

**Fixed**
- German menus and dialogs no longer show broken placeholder symbols in place of accented letters — the text reads correctly again.
- French menus and dialogs no longer show broken placeholder symbols in place of accented letters — the text reads correctly again.
- Spanish menus and dialogs no longer show broken placeholder symbols in place of accented letters and inverted question marks — the text reads correctly again.

## Hotfix - 1.0.2607043 - 2026.07.04

**Fixed**
- Installing an update now completes. The updater used to finish downloading and then close with an error instead of applying the update; updates now install and Hub restarts on its own.
- The "update available" prompt now matches the Phoenix Controls look instead of showing as a plain system dialog.
- Architect no longer crashes when exporting a script with very deeply nested macros — it now stops cleanly with a clear message.

> Heads up — the in-app updater couldn't finish installing in earlier versions, so it can't install this update for you. Download and install this version manually once (use the Download button); from this version on, the in-app updater installs updates on its own.

## Hotfix - 1.0.2607042 - 2026.07.04

**Fixed**
- Architect: the "Do N" flow node now runs its Loop Body for the first N calls — before, it skipped straight to Completed every time and never ran the body.
- Architect: a Databank "Increment" node now adds a calculated Amount correctly — when the Amount came from another node it was adding zero instead of the real value.
- Architect: point and currency transfers now subtract from the sender — a transfer could add to the receiver while the sender kept everything, quietly duplicating currency.
- Improved reliability and stability of counters and loops in Architect.

## Hotfix - 1.0.2607041 - 2026.07.04

**New**
- Hub now offers to install updates: while a newer release is out, a prompt at startup asks to "Install & restart" — one click downloads the update, applies it, and restarts Hub.

**Fixed**
- The "Download & install latest release" button in Settings no longer fails with "one or more numeric fields are invalid" — a hidden, never-filled Viewer server port field was blocking every download attempt.
- Saving Settings no longer silently resets the Viewer server options (enabled, port, LAN access, channel name) to their defaults.
- An invalid number in Settings can no longer block downloading an update — bad values now roll back to the last saved ones instead, same as Save.
- Improved reliability and stability of in-app updates.

> Heads up — the in-app updater was broken in earlier versions, so it can't install this update for you. Download and install this version manually once (use the Download button); from this version on, the in-app updater works normally.

## Hotfix - 1.0.2607031 - 2026.07.03

**New**
- Architect: paired event nodes now stay matched across scripts — add, rename, or retype a value on one side and its partners update instantly, even in other open windows.

**Fixed**
- Architect: naming an Event Trigger now fills in its paired bubbles right away — the node no longer stays bare after naming it.
- Architect: a freshly named event node syncs its bubbles with partners in the same script and in other scripts.
- Architect: an event's arguments and return values stay on their own separate channels — a return no longer turns into a second argument.
- Architect: event nodes no longer show garbled or overlapping sockets after mixing arguments and return values.
- Architect: clicking a "+" slot on an event node adds the bubble right away, even when the mouse drifts a little during the click.
- Architect: pressing an event node near its "+" slot row no longer starts an unintended wire drag.
- Architect: right-clicking a bubble on an event node opens its socket menu — changing a bubble's type or removing it works from there.
- Architect: renaming an event bubble sticks and carries over to partner nodes in other scripts.
- Architect: giving a new event node an existing event's name shapes it to match that event, without wiping the other nodes' values.
- Architect: loading a script no longer shrinks event nodes — paired nodes keep their fullest set of bubbles.
- Architect: event nodes damaged by earlier versions repair themselves when their script opens.
- Architect: cross-file event sync can no longer wipe the bubbles of an already-defined partner node.
- Improved reliability and stability of event nodes in Architect.
- Architect: saving a script that wires flow into a macro's Exit node no longer fails and leaves the script outdated.
- Architect: flow reaching a macro's Exit node now ends the macro cleanly.
- Architect: values wired into a macro's Exit node now come out of the Macro Call node's outputs.
- Architect: macro outputs reset on every call — no leftover values from an earlier run.
- Improved reliability and stability of macros in Architect.
- Architect: double-clicking a node to edit it no longer makes it jump across the canvas or vanish after loading another script in the same window.
- Architect: values edited directly on a node are no longer lost when the editor closes as you click elsewhere.
- Architect: value outputs on text and convert nodes can now connect to more than one node.
- Architect: comment frames can now be renamed — double-click the frame's title, or right-click it.
- Architect: pressing a shortcut key while typing in a value box no longer triggers canvas actions behind it.
- Architect: long Template, Script, and Payload text wraps inside the node instead of stretching over its outputs.
- Improved reliability and stability of inline node editing in Architect.
- Architect: working on the canvas no longer floods the System Log with repeated warnings.

## Hotfix - 1.0.2606302 - 2026.06.30

**Fixed**
- Hub's in-app updater now installs new versions — the Update button in Settings spotted updates but couldn't apply them before.
- An update now finishes cleanly and Hub reopens on the new version.
- New macros in Architect open with their In and Out nodes already in place.
- Macros that used to open as an empty graph are repaired automatically when you open them.
- Macros now pass their data through and run, instead of opening blank.
- Dragging a wire onto a "+" slot on Event, Macro, and Process nodes now adds that input or return — it often did nothing before.
- The "+" slots are easier to see, with a clearer dashed outline.
- The "+" slots are easier to land a wire on.
- Macro and Process call nodes update as you change their inputs and outputs, with no need to save and reopen.

> Heads up — the in-app updater was broken in earlier versions, so it can't install this update for you. Download and install this version manually once (use the Download button); from this version on, the in-app updater works normally.

## Hotfix - 1.0.2606301 - 2026.06.30

**New**
- Architect and Visualist now have a built-in node reference — open it to read what every node does, with its inputs and outputs, right inside the app.
- Processes keep running once started — any triggers you place on a process (schedules, chat commands, events) stay active until the process is stopped, instead of running once and quitting.
- The same process can run as several copies at once, each with its own start values.
- The Start and Stop nodes turn a process on and off from your logic.

**Fixed**
- Architect's left-rail Add, Rename, and Delete buttons for variables, macros, and processes now open their dialog every time.
- Architect's confirm prompts, recent files, save checks, keyboard shortcuts, and variable trace now open reliably.
- Visualist's media library, media picker, new layer, shape editor, and curve editor now open reliably.
- Process and Macro nodes now pick their target from a dropdown, instead of a free-text box that often didn't match and broke the saved script.
- Architect Process, Macro, and Event node pins connect to wires the moment you place the node, instead of only after saving and reopening the graph.
- Wires stay attached to an Architect node when you resize it, instead of drifting outside its body.
- Architect's node search lists the closest matches first and finds nodes by their title, and node tooltips show again on hover.
- An event's trigger and executor keep their added inputs and return values in sync across files.
- Long value pills on Architect nodes wrap to fit instead of spilling past the node edge.
