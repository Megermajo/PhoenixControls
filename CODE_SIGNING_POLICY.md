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

> **Status — unsigned.** Release binaries are currently **unsigned**, so Windows
> may show an "unknown publisher" / SmartScreen prompt on first run — expected
> for a young project. Verify your download against the published `.sha256`
> sidecar if you want to confirm integrity. Code signing is planned; this section
> will be updated with the details once it's in place.

## Privacy

This program will not transfer any information to other networked systems unless
specifically requested by the user or the person installing or operating it.

Phoenix Controls runs locally, stores its data under
`%LOCALAPPDATA%\PhoenixControls\`, and communicates only with your local
Streamer.bot instance and any external services you explicitly configure. It has
no telemetry and no accounts.
