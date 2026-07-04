<div align="center">

<img src="https://raw.githubusercontent.com/Megermajo/PhoenixControls/main/assets/phoenix-mark.png" alt="Phoenix Controls" width="140" />

# Phoenix Controls — Source

*The source branch. Build it, read it, or contribute to it.*

[![License](https://img.shields.io/badge/license-MIT-E5A24E?style=flat-square)](LICENSE)
[![Stack](https://img.shields.io/badge/stack-.NET%208%20%C2%B7%20WinUI%203-1B1713?style=flat-square)](#)
[![AI authored](https://img.shields.io/badge/source-100%25%20AI--authored-E5A24E?style=flat-square)](#ai-disclaimer)

</div>

---

This is the **`code`** branch — the full source for Phoenix Controls. If you just
want to **use** the app, you don't need anything here:

- 🏠 **Landing page & overview:** the [`main`](https://github.com/Megermajo/PhoenixControls/tree/main#readme) branch
- ⬇️ **Download the installer:** the [Releases](https://github.com/Megermajo/PhoenixControls/releases/latest) page
- 🌐 **Project home:** [megermajo-productions.com](https://megermajo-productions.com)

## What this is

Phoenix Controls is a local-first streaming workshop that sits on top of
Streamer.bot — three apps sharing one connection:

- **Hub** — the live cockpit (chat, scripts, system log, bot bridge).
- **Architect** — a node-based editor for off-screen logic.
- **Visualist** — an overlay compositor that stays idle until Architect calls it.

## Building from source

You'll need the **.NET 8 SDK** on Windows 10 or 11.

```bash
git clone --branch code https://github.com/Megermajo/PhoenixControls.git
cd PhoenixControls
dotnet build Phoenix.Controls/Phoenix.Controls.sln -c Debug
```

Run `Phoenix.Controls.Hub.WinUI` — it hosts Architect and Visualist inside it.

## Releases are built from this branch

Every published release is built on GitHub-hosted CI **from this branch**, then
packaged into the installer on the [Releases](https://github.com/Megermajo/PhoenixControls/releases/latest)
page. See the [Code signing policy](https://github.com/Megermajo/PhoenixControls/blob/main/CODE_SIGNING_POLICY.md)
for the current signing status.

## Contributing

Bug reports, ideas, and pull requests are welcome — see
[CONTRIBUTING](https://github.com/Megermajo/PhoenixControls/blob/main/CONTRIBUTING.md).
Pull requests target this branch.

## AI disclaimer

**100% of the source in this repository was authored by AI** — directed,
reviewed, and shipped by **Megermajo**. No human-written code was committed.
Read before you run, and audit anything you bind to live credentials.

## License

MIT — see [LICENSE](LICENSE). Do what you like with it, just keep the copyright
notice.

---

<div align="center">
<sub>Phoenix Controls · A product by Megermajo Productions · 100% AI-authored source</sub>
</div>
