# Code signing policy

This page describes how **Phoenix Controls** release binaries are built,
reviewed, signed, and what data the program handles.

## Build & release

Release binaries are built **only from the public source** on the
[`code`](https://github.com/Megermajo/PhoenixControls/tree/code) branch, on
GitHub-hosted CI runners — never from a personal machine. Every release is
built, reviewed, and explicitly approved before it is published.

## Roles

Phoenix Controls is maintained by a single author, who holds all roles:

| Role         | Member    | Responsibility                                      |
| ------------ | --------- | --------------------------------------------------- |
| **Author**   | Megermajo | Directs, reviews, and commits the source.           |
| **Reviewer** | Megermajo | Reviews all changes before a release is cut.        |
| **Approver** | Megermajo | Approves each individual signing / release request. |

## Code signing

> **Status — enrolling.** Phoenix Controls is enrolling in the free
> [SignPath Foundation](https://signpath.org) open-source code-signing program.
> Until enrollment completes, release binaries are **unsigned**, and Windows
> will show an "unknown publisher" prompt on first run. Once active, this
> section will read:
>
> > Free code signing provided by [SignPath.io](https://about.signpath.io),
> > certificate by [SignPath Foundation](https://signpath.org).
>
> Signed binaries will show **SignPath Foundation** as the verified Windows
> publisher. A freshly issued certificate builds Windows SmartScreen reputation
> gradually, so early downloads may still show a caution prompt until that
> reputation accumulates.

## Privacy

This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it.

Phoenix Controls runs locally, stores its data under
`%LOCALAPPDATA%\PhoenixControls\`, and communicates only with your local
Streamer.bot instance and any external services you explicitly configure. It has
no telemetry and no accounts.
