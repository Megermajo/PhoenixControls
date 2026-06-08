# Contributing to Phoenix Controls

Thanks for taking an interest. Phoenix Controls is open source under the MIT
license, and contributions are welcome — bug reports, ideas, and pull requests
all help.

## Before you start

- **Source lives on the [`code`](https://github.com/Megermajo/PhoenixControls/tree/code) branch.** The default `main` branch holds the landing page and docs.
- **The source is AI-authored** (see the [AI disclaimer](README.md#-ai-disclaimer)). Pull requests are reviewed and merged by Megermajo.
- For anything non-trivial, **open an issue or a [Discussion](https://github.com/Megermajo/PhoenixControls/discussions) first** so the approach can be agreed before you spend time on it.

## Building from source

You'll need the **.NET 8 SDK** on Windows 10 or 11.

```bash
git clone --branch code https://github.com/Megermajo/PhoenixControls.git
cd PhoenixControls
dotnet build Phoenix.Controls/Phoenix.Controls.sln -c Debug
```

`Phoenix.Controls.Hub.WinUI` is the app you run; Architect and Visualist are
hosted inside it. A clean build (0 errors) is the bar before any change is
considered done.

## Pull requests

- **Keep it building.** Every PR must compile cleanly (`dotnet build`, 0 errors).
- **Don't commit personal runtime content.** Files like `*.phx`, `*.phxg`, and
  `*.phxlayer` are your own scripts/graphs/layers — leave them out of commits.
- **Sign your commits.** The `code` branch requires verified, signed commits.
- **One focused change per PR**, with a short description of what and why.
- Match the surrounding code's style; this isn't the place for unrelated
  reformatting.

## Reporting bugs and ideas

Use the issue templates (Bug report / Feature request). For open-ended questions
or "would this be a good idea?" conversations, the
[Discussions](https://github.com/Megermajo/PhoenixControls/discussions) tab is
the right place.

By contributing, you agree your contribution is licensed under the project's MIT
license.
