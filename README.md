<div align="center">

<img src="assets/phoenix-mark.png" alt="Phoenix Controls" width="160" />

# Phoenix Controls

*A self-hosted control plane for streamers.*

[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-1B1713?style=flat-square)](#)
[![Stack](https://img.shields.io/badge/stack-.NET%20%C2%B7%20WinUI%203-1B1713?style=flat-square)](#)
[![License](https://img.shields.io/badge/license-MIT-E5A24E?style=flat-square)](LICENSE)
[![AI authored](https://img.shields.io/badge/source-100%25%20AI--authored-E5A24E?style=flat-square)](#-ai-disclaimer)

<br />

<a href="https://github.com/Megermajo/PhoenixControls/releases/latest/download/PhoenixControls-Setup.exe"><img src="https://img.shields.io/badge/Download-Installer-E5A24E?style=for-the-badge&logo=windows&logoColor=1B1713" alt="Download Installer" height="34" /></a>
&nbsp;&nbsp;
<a href="https://github.com/Megermajo/PhoenixControls/archive/refs/heads/code.zip"><img src="https://img.shields.io/badge/Download-Source%20Code-1B1713?style=for-the-badge&logo=github&logoColor=E5A24E" alt="Download Source Code (zip)" height="34" /></a>

<sub>Installer link goes live with the first release · Source Code is a `.zip` of the latest source on the `code` branch</sub>

</div>

---

> [!NOTE]
> ### 🎉 Version 1.0 — just shipped
> This is the first public release. It's stable and ready to use — and actively
> developed, so expect a steady flow of new features and fixes. Hit a rough edge?
> Open an [issue](../../issues) or say hi in [Discussions](../../discussions).

---

> [!IMPORTANT]
> ### 🤖 AI Disclaimer
> **100% of the source in this repository was authored by AI** — directed, reviewed,
> and shipped by **Megermajo**. No human-written code was committed.
> Treat every file accordingly: read before you run, and audit anything you bind
> to live credentials.

---

## Table of Contents

1. [Introduction](#1--introduction)
2. [Quickstart Guide](#2--quickstart-guide)
3. [The Tools](#3--the-tools)
4. [On the Horizon](#4--on-the-horizon)
5. [License](#5--license)

---

## 1 · Introduction

Phoenix Controls is a streaming workshop you run on your own machine.
You wire up logic visually, watch it run live, and let it drive what's on screen — all from one place.

Think of **Streamer.bot** as the engine that talks to Twitch and YouTube.
Phoenix Controls is the workshop you build around it: a friendlier way to author flows, a live dashboard for what's running, and a compositor for your overlay.
**It doesn't replace Streamer.bot — it sits on top of it.**

The suite is split into three apps that share one connection:

| | App | Role | What it does |
|---|---|---|---|
| **H** | **Hub** | *The Operator* | Live cockpit — watch chat, see scripts firing, send manual messages, read the system log. |
| **A** | **Architect** | *The Logician* | Node-based editor for off-screen logic. Drag triggers and actions onto a canvas, wire them, save. |
| **V** | **Visualist** | *The Stage Hand* | Overlay compositor. Mostly idle — shows a calm visual until Architect fires an event, then reacts on screen. |

> 💡 **Local-first.** Everything runs on your machine. No accounts, no cloud, no telemetry.
> Phoenix sends chat *through* Streamer.bot, so it uses your existing bot account and rate limits.

---

## 2 · Quickstart Guide

Four steps, usually under ten minutes. You'll need a working Streamer.bot on the same machine.

### Step 1 — Install Phoenix Controls

Grab a build from the repo's **Releases** page:

- **Installer** (`PhoenixControls-Setup.exe`) — registers the `.phx` file type and adds Start-menu shortcuts.
- **Portable zip** — unzip anywhere and launch Hub from `Hub\Phoenix.Controls.Hub.WinUI.exe` (or run `CreateShortcuts.ps1` once to add Start-menu shortcuts).

> 📂 **Where your data lives:** `%LOCALAPPDATA%\PhoenixControls\`
> Sample flows land in `data\logic\examples\`.

### Step 2 — Set up Streamer.bot

Phoenix talks to Streamer.bot over its built-in WebSocket server, and sends chat by invoking a named **Action** there.
That action is the one piece of wiring you build by hand — after that, every flow you author in Phoenix goes through it automatically.

**2.1 — Enable the WebSocket server**

In Streamer.bot, go to *Servers/Clients → WebSocket Server* and confirm it's running on `127.0.0.1:8080`.

**2.2 — Create the chat Message Action**

Add a new Action in Streamer.bot named `PhoenixControlsChat` (or anything — just remember the name).
Give it **one sub-action**:

| # | Category | Type | Settings |
|---|---|---|---|
| 1 | Twitch | Message | text = `%message%` |

> 💡 **Why an action, not a raw chat call?** Going through a named action means Streamer.bot owns rate-limiting, role checks, and your existing bot-account credentials. Phoenix never touches Twitch directly — it just dispatches.

### Step 3 — Point Phoenix at it

Open Hub. The top strip has status dots — they go green as each service comes up. The one you care about is **Streamer.bot**.

If it stays amber or red, open *Settings → Connection* and confirm:

| Setting | Value |
|---|---|
| `StreamerBot URL` | `ws://127.0.0.1:8080/` |
| `StreamerBot Chat Action` | `PhoenixControlsChat` (or whatever you named it) |

### Step 4 — Build your first flow

Open Architect. Right-click the canvas → drop a **Chat Message** trigger and a **Send Message** action.
Add a branch that checks for `!ping` and a text node that formats `"@{user} pong"`. Wire them up. Save.

Hub picks the file up immediately — the Scripts panel shows it as *idle*.
Type `!ping` in your own chat and watch the row flicker, the Live Feed scroll, and "pong" land back in chat through the action you wired in step 2.

> ✨ **That's the loop.** Every flow you'll ever build follows the same shape:
> trigger on top, branches and transforms in the middle, an output at the bottom.
> Save the file and it goes live — no restart.

---

## 3 · The Tools

### 🅷  Hub

> *Live cockpit · bot bridge · script monitor*

Your stream-time dashboard. Watch chat scroll, see every script firing, send a manual message as your bot, and read the system log when something doesn't fire the way you expected. Four panels: **Live Feed**, **Chat**, **Scripts**, and **System Log**.

### 🅰  Architect

> *Node-based logic editor*

A graph editor for everything that happens off-screen. Triggers on the left (chat messages, channel-point redeems, raids, subs, hotkeys, OBS scene changes), actions and logic in the middle, outputs on the right.
Drag a few nodes, wire them, save — that's a flow. **Hot-reload** means you can iterate without restarting anything.

### 🆅  Visualist

> *Overlay compositor — idle until called*

What your viewers actually see. Most of the time, Visualist sits in its **idle state** — a calm background visual, a logo, a chosen image, nothing animated.
Then Architect fires an event (a raid, a sub, a custom trigger), Visualist wakes up, plays the alert or overlay you authored, and returns to idle.

You design both halves: the idle layer that lives on stream, and the reactions stacked on top of it.

---

## 4 · On the Horizon

A few things we're working toward. Some are already wired internally but stay hidden until they're solid enough to trust on stream. **Parked, not promised.**

- **AI nodes** — prompt an LLM, auto-moderate chat, generate images, describe what's on screen. The plumbing exists but isn't finished or tested yet, so it's hidden until it's reliable.
- **Live Twitch & OBS data** — reading live state *back* into your flows (viewer counts, follower info, OBS scene state), not just sending actions out. Today Phoenix dispatches through Streamer.bot; richer read-back is the next step.
- **AI Inspector** — once AI is live, a window into each call: what you asked, what it remembered, what it cost.
- **A more distinctive Hub window** — a custom-shaped silhouette instead of a plain rectangle.
- **Pop-out windows that remember you** — filters and drafts that survive a restart, not just a pop-out.
- **Smoother idle frames** — performance polish so Hub stays light even when it's quiet.

If any of these matter to you, say so in the repo's [Discussions](../../discussions) tab.

---

## 5 · License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for the full text.

In short: do what you like with it, just keep the copyright notice.

### Code signing

Release builds are produced on GitHub-hosted CI from the public source on the
[`code`](https://github.com/Megermajo/PhoenixControls/tree/code) branch. See the
[**Code signing policy**](CODE_SIGNING_POLICY.md) for how releases are built, who
approves them, and the privacy commitment.

---

<div align="center">

**A product by Megermajo Productions**

[megermajo-productions.com](https://megermajo-productions.com)

<sub>Phoenix Controls · forged by Megermajo &amp; contributors · 100% AI-authored source</sub>

</div>
