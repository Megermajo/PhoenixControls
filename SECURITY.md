# Security Policy

Phoenix Controls is a local-first desktop app. It runs on your machine, talks to
your local Streamer.bot, and stores its data under `%LOCALAPPDATA%\PhoenixControls\`.
It has no servers of its own and sends nothing to us.

## Supported versions

Only the **latest release** receives security fixes — there are no long-term
support branches. Update to the newest build before reporting an issue.

| Version        | Supported |
| -------------- | --------- |
| Latest release | ✅        |
| Anything older | ❌        |

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Report privately through GitHub's [**Report a vulnerability**](https://github.com/Megermajo/PhoenixControls/security/advisories/new)
button (the repository's *Security → Advisories* tab). If you can't use that,
reach out through [megermajo-productions.com](https://megermajo-productions.com).

Please include:

- What the issue is and where it lives (file, screen, or feature).
- Steps to reproduce, or a proof of concept.
- The release version and your Windows version.

You'll get an acknowledgement, and we'll keep you posted while a fix is worked
out. You'll be credited in the release notes unless you'd rather stay anonymous.

## A note on AI-authored source

Every line in this repository was authored by AI and reviewed by Megermajo (see
the [AI disclaimer](README.md#-ai-disclaimer)). Treat the binaries like any other
third-party download: read before you run, and audit anything you bind to live
credentials.
