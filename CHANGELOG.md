# Changelog

All notable changes to Phoenix Controls are documented here.

**v1.0** is the first public release — the starting point. From here on, each
release lists what's **New**, **Fixed**, and **Gone** in plain language.

---

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
