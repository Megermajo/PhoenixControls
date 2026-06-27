# TRIAGE — Machine-Synthesized Translation Strings

This document inventories the keys in `de.json` / `fr.json` / `es.json` that
were machine-synthesized during Sprint A backfill and need a native-speaker
pass. The flag source is the `_sprint_a_backfill_note` field at the bottom of
each non-EN bundle. **All three bundles carry the same note verbatim**, so the
flagged surface area is identical across DE / FR / ES — there is no per-bundle
drift in what is flagged (only in the translation quality, which is what this
triage is for).

Verbatim backfill note (identical in `de.json`, `fr.json`, `es.json`):

> DE/FR/ES strings under hub.docs.tab.\*, hub.docs.nodes.\*, hub.feature.auto_updater.description, hub.feature.ai_suite.\*, hub.feature.docs_nodes_tab.\*, architect.mainform.process\*, architect.mainform.section.processes, architect.mainform.status.process_\*, architect.node.bubble.ai_\*, chrome.architect.file.openRecent, plus dialog.settings.\* / dialog.welcome.\* / dialog.updater_progress.\* are machine-synthesized in Sprint A/I — flagged for native review.

Resolving each prefix glob against the canonical `en.json`, the flagged set
totals **118 keys per bundle** (118 × 3 bundles = 354 strings needing review).
The breakdown by namespace is below; each language section repeats the same
grouping because the flagged keys are identical across DE / FR / ES.

---

## German (de.json)

**Total flagged: 118 keys.** Bundle structure: flat string→string JSON,
matching EN parity. Native register expected: standard developer / streaming
software register (Sie-form for dialog buttons; technical loanwords like
"Stream", "Chat", "Bus" kept as-is per existing convention in the bundle).

### `hub.docs.tab.*` — 2 keys
Representative EN: `hub.docs.tab.features` → `"Features"`
- `hub.docs.tab.features`
- `hub.docs.tab.nodes`

### `hub.docs.nodes.*` — 12 keys
Representative EN: `hub.docs.nodes.search.placeholder` → `"Search nodes by name, category or description…"`
- `hub.docs.nodes.search.placeholder`
- `hub.docs.nodes.section.inputs`
- `hub.docs.nodes.section.outputs`
- `hub.docs.nodes.section.properties`
- `hub.docs.nodes.detail.category_label`
- `hub.docs.nodes.detail.no_description`
- `hub.docs.nodes.detail.no_category_snippet`
- `hub.docs.nodes.table.name`
- `hub.docs.nodes.table.type`
- `hub.docs.nodes.table.description`
- `hub.docs.nodes.table.key`
- `hub.docs.nodes.table.value`

### `hub.feature.auto_updater.description` — 1 key
Representative EN (truncated): `"The Updates section at the bottom of Settings shows the running suite version (read from Directory.Build.props), the matching GitHub release tag, and whether a newer release is available on GitHub Releases. Hub queries the Releases API in the background at startup so the status reflects what is published right now…"` (~7 sentences total, ends `"…installations driven by the bundled installer / portable zip are self-contained."`)
- `hub.feature.auto_updater.description`

### `hub.feature.ai_suite.*` — 3 keys
Representative EN: `hub.feature.ai_suite.title` → `"AI Provider Suite"`
- `hub.feature.ai_suite.title`
- `hub.feature.ai_suite.description` (long paragraph — covers `AI.Prompt`, `AI.StreamText`, `AI.Moderate`, `AI.GenerateImage`, `AI.VisionDescribe`, `AI.WithTools`, provider-routing rules, AppConfig key names)
- `hub.feature.ai_suite.where`

### `hub.feature.docs_nodes_tab.*` — 3 keys
Representative EN: `hub.feature.docs_nodes_tab.title` → `"Documentation — Nodes Tab"`
- `hub.feature.docs_nodes_tab.title`
- `hub.feature.docs_nodes_tab.description`
- `hub.feature.docs_nodes_tab.where`

### `architect.mainform.process*` (wildcard — matches `process`, `process_ctx`, `process_panel`) — 9 keys
Representative EN: `architect.mainform.process.default_new_name` → `"NewProcess"`
- `architect.mainform.process.default_new_name`
- `architect.mainform.process_ctx.delete`
- `architect.mainform.process_ctx.find_references`
- `architect.mainform.process_ctx.open_editor`
- `architect.mainform.process_ctx.rename`
- `architect.mainform.process_panel.btn.add.tooltip`
- `architect.mainform.process_panel.btn.delete.tooltip`
- `architect.mainform.process_panel.btn.edit.tooltip`
- `architect.mainform.process_panel.btn.rename.tooltip`

### `architect.mainform.section.processes` — 1 key
Representative EN: `architect.mainform.section.processes` → `"Processes"`
- `architect.mainform.section.processes`

### `architect.mainform.status.process_*` — 2 keys
Representative EN: `architect.mainform.status.process_find_refs_count` → `"Highlighted {0} Process.Spawn site(s) for '{1}'."`
- `architect.mainform.status.process_find_refs_count`
- `architect.mainform.status.process_find_refs_none`

### `architect.node.bubble.ai_*` — 6 keys
Representative EN: `architect.node.bubble.ai_moderate` → `"Checks Text against OpenAI's content-moderation classifier. Flagged is true when any category fires; Category names the worst-scoring category, or is empty when nothing fires. Useful for auto-moderating chat or AI-generated alert text."`
- `architect.node.bubble.ai_generate_image`
- `architect.node.bubble.ai_moderate`
- `architect.node.bubble.ai_prompt`
- `architect.node.bubble.ai_stream_text`
- `architect.node.bubble.ai_vision_describe`
- `architect.node.bubble.ai_with_tools`

These are the help-bubble tooltip strings shown on the node body in Architect
— each is a 2-to-5 sentence technical description with embedded socket names
(Response, Error, ImageUrl, etc.) that must stay verbatim because they're
literal pin labels.

### `chrome.architect.file.openRecent` — 1 key
Representative EN: `chrome.architect.file.openRecent` → `"Open Recent..."`
- `chrome.architect.file.openRecent`

### `dialog.settings.*` — 61 keys
Representative EN: `dialog.settings.title` → `"Phoenix Controls — Settings"`

This is the largest single group — the entire Settings dialog (labels, group
headers, helper text, validation messages, button captions). 61 keys is too
many to enumerate inline; scan the bundle for `^  "dialog.settings\.` to walk
them in order. The dialog covers: connection settings (Streamer.bot URL, HUD
port, chat action name, bot username), script-runtime caps (timeout,
max-chat-scripts, max-webhook-scripts), AI provider keys, updater section,
and the Save / Cancel / Defaults buttons.

### `dialog.welcome.*` — 8 keys
Representative EN: `dialog.welcome.heading` → `"Pick a sample graph to get started — or skip and start blank."`
- `dialog.welcome.title`
- `dialog.welcome.button.skip`
- `dialog.welcome.eyebrow.first_run`
- `dialog.welcome.heading`
- `dialog.welcome.body.intro`
- `dialog.welcome.button.use_this`
- `dialog.welcome.empty.title`
- `dialog.welcome.empty.body`

### `dialog.updater_progress.*` — 9 keys
Representative EN: `dialog.updater_progress.title` → `"Updating Phoenix Controls…"`
- `dialog.updater_progress.title`
- `dialog.updater_progress.phase.query`
- `dialog.updater_progress.phase.download`
- `dialog.updater_progress.phase.verify`
- `dialog.updater_progress.phase.prepare`
- `dialog.updater_progress.phase.await_hub_exit`
- `dialog.updater_progress.percent_format`
- `dialog.updater_progress.button.cancel`
- `dialog.updater_progress.button.cancelling`

---

## French (fr.json)

**Total flagged: 118 keys.** Identical grouping to German (the backfill note
is byte-identical across all three bundles). Native register expected:
formal vous-form for dialog buttons; technical English nouns kept where the
French software-localization convention favors them (e.g. "Bus", "Stream",
"Chat" typically stay English in similar tools).

### `hub.docs.tab.*` — 2 keys
Representative EN: `hub.docs.tab.features` → `"Features"`
- `hub.docs.tab.features`
- `hub.docs.tab.nodes`

### `hub.docs.nodes.*` — 12 keys
Representative EN: `hub.docs.nodes.search.placeholder` → `"Search nodes by name, category or description…"`
(See German section for full key list — identical.)

### `hub.feature.auto_updater.description` — 1 key
See German section for the representative EN. Single long paragraph.

### `hub.feature.ai_suite.*` — 3 keys
Representative EN: `hub.feature.ai_suite.title` → `"AI Provider Suite"`
(See German section for full key list — identical.)

### `hub.feature.docs_nodes_tab.*` — 3 keys
Representative EN: `hub.feature.docs_nodes_tab.title` → `"Documentation — Nodes Tab"`
(See German section for full key list — identical.)

### `architect.mainform.process*` — 9 keys
Representative EN: `architect.mainform.process.default_new_name` → `"NewProcess"`
(See German section for full key list — identical.)

### `architect.mainform.section.processes` — 1 key
Representative EN: `"Processes"`

### `architect.mainform.status.process_*` — 2 keys
Representative EN: `architect.mainform.status.process_find_refs_count` → `"Highlighted {0} Process.Spawn site(s) for '{1}'."`
(See German section for full key list — identical.)

### `architect.node.bubble.ai_*` — 6 keys
Representative EN: `architect.node.bubble.ai_moderate` → `"Checks Text against OpenAI's content-moderation classifier. Flagged is true when any category fires; Category names the worst-scoring category, or is empty when nothing fires. Useful for auto-moderating chat or AI-generated alert text."`
(See German section for full key list — identical.)

### `chrome.architect.file.openRecent` — 1 key
Representative EN: `"Open Recent..."`

### `dialog.settings.*` — 61 keys
Representative EN: `dialog.settings.title` → `"Phoenix Controls — Settings"`
(Walk the bundle for `^  "dialog.settings\.` to enumerate.)

### `dialog.welcome.*` — 8 keys
Representative EN: `dialog.welcome.heading` → `"Pick a sample graph to get started — or skip and start blank."`
(See German section for full key list — identical.)

### `dialog.updater_progress.*` — 9 keys
Representative EN: `dialog.updater_progress.title` → `"Updating Phoenix Controls…"`
(See German section for full key list — identical.)

---

## Spanish (es.json)

**Total flagged: 118 keys.** Identical grouping to German and French.
Native register expected: standard tú/usted convention per the existing
bundle's voice; technical English nouns kept where Spanish software
localization typically does (e.g. "Bus", "Stream", "Chat").

### `hub.docs.tab.*` — 2 keys
Representative EN: `hub.docs.tab.features` → `"Features"`
- `hub.docs.tab.features`
- `hub.docs.tab.nodes`

### `hub.docs.nodes.*` — 12 keys
Representative EN: `hub.docs.nodes.search.placeholder` → `"Search nodes by name, category or description…"`
(See German section for full key list — identical.)

### `hub.feature.auto_updater.description` — 1 key
See German section for the representative EN. Single long paragraph.

### `hub.feature.ai_suite.*` — 3 keys
Representative EN: `hub.feature.ai_suite.title` → `"AI Provider Suite"`
(See German section for full key list — identical.)

### `hub.feature.docs_nodes_tab.*` — 3 keys
Representative EN: `hub.feature.docs_nodes_tab.title` → `"Documentation — Nodes Tab"`
(See German section for full key list — identical.)

### `architect.mainform.process*` — 9 keys
Representative EN: `architect.mainform.process.default_new_name` → `"NewProcess"`
(See German section for full key list — identical.)

### `architect.mainform.section.processes` — 1 key
Representative EN: `"Processes"`

### `architect.mainform.status.process_*` — 2 keys
Representative EN: `architect.mainform.status.process_find_refs_count` → `"Highlighted {0} Process.Spawn site(s) for '{1}'."`
(See German section for full key list — identical.)

### `architect.node.bubble.ai_*` — 6 keys
Representative EN: `architect.node.bubble.ai_moderate` → `"Checks Text against OpenAI's content-moderation classifier. Flagged is true when any category fires; Category names the worst-scoring category, or is empty when nothing fires. Useful for auto-moderating chat or AI-generated alert text."`
(See German section for full key list — identical.)

### `chrome.architect.file.openRecent` — 1 key
Representative EN: `"Open Recent..."`

### `dialog.settings.*` — 61 keys
Representative EN: `dialog.settings.title` → `"Phoenix Controls — Settings"`
(Walk the bundle for `^  "dialog.settings\.` to enumerate.)

### `dialog.welcome.*` — 8 keys
Representative EN: `dialog.welcome.heading` → `"Pick a sample graph to get started — or skip and start blank."`
(See German section for full key list — identical.)

### `dialog.updater_progress.*` — 9 keys
Representative EN: `dialog.updater_progress.title` → `"Updating Phoenix Controls…"`
(See German section for full key list — identical.)

---

## Notes / surprises from the inventory

- **No per-bundle drift in the flag set.** The three `_sprint_a_backfill_note`
  values are byte-identical, so every flagged namespace is flagged in all
  three languages — there's no language that has already been native-reviewed
  for a given namespace.
- **No empty / missing notes.** All three notes are well-formed comma-lists,
  not stubs.
- **Parity confirmed.** Key counts in each flagged namespace match EN parity
  in all three bundles (e.g. `dialog.settings.*` = 61 in EN / DE / FR / ES;
  `hub.docs.nodes.*` = 12 in all four).
- **No English bleed-through spotted in spot-checks.** Sampled
  `hub.docs.tab.features`, `dialog.welcome.title`, and
  `architect.node.bubble.ai_moderate` in all three bundles — all returned
  natural-looking target-language text, not raw English. (This does not
  guarantee correctness across all 118 keys, but the machine output is at
  least plausibly translated, not pass-through.)
- **Embedded socket names and identifiers in `architect.node.bubble.ai_*`.**
  These tooltips contain literal English socket-pin names
  (`Response`, `Error`, `ImageUrl`, `Flagged`, `Category`, `SystemPrompt`,
  `UserPrompt`, `Model`, `Prompt`, `MemoryVar`, `Tools`, `ToolCalls`, etc.)
  and model/provider strings (`dall-e-3`, `gpt-4o-mini`, `claude-`,
  `ollama/<name>`, etc.) that must remain verbatim. Spot-checks show the
  machine translations preserved these — worth verifying namespace-wide.
- **`architect.mainform.process.default_new_name` (`"NewProcess"`)** is a
  default identifier used as a created-object name in code. If Architect
  treats this as a literal string (not a label), it may need to stay English
  regardless of locale — confirm with Hub/Architect behavior before
  translating.
- **Format placeholders** (e.g. `{0}`, `{1}`, `{0}%`) appear in
  `architect.mainform.status.process_*` and `dialog.updater_progress.percent_format`.
  Native review should confirm these placeholders are preserved in all three
  bundles (spot-check on `process_find_refs_count` was clean).

---

## How to triage

Review the flagged keys one namespace at a time, eyeballing the target-language
translation against the EN representative quoted above; when the synthesized
output reads naturally in the target language, leave it alone — the goal is
fluency, not rewriting. Fix only the strings that are awkward, technically
wrong, or break with embedded socket names / format placeholders. Once a
namespace (e.g. `dialog.welcome.*`) is fully native-reviewed in a given
bundle, remove that prefix from the bundle's `_sprint_a_backfill_note` so
future passes know what's left.

---

## Sprint M — comm-loca-fix polish backfill (2026-05-18)

The chrome sweep driven by `CommLocalizationReviewReport.md` added 36 new keys
to `en.json` and machine-translated DE / FR / ES counterparts. The new
namespaces below are flagged for native review on the same triage cadence as
the Sprint A/I prefixes above.

**New EN keys (36):**

- `common.button.*` — `save`, `discard`, `cancel`, `dont_save`, `delete`, `open` (6)
- `common.context.*` — `duplicate`, `rename`, `delete` (3)
- `architect.dialog.unsaved_changes.*` — `title`, `content.graph`, `content.window` (3)
- `architect.dialog.name_type.variable_eyebrow` (1)
- `architect.dialog.var_chain.*` — `writers_header_empty`, `readers_header_empty`, `writers_header_format`, `readers_header_format` (4)
- `architect.window.*` — `node_reference.title`, `sibling.title` (2)
- `architect.databank.context.*` — `delete_table`, `export_csv` (2)
- `architect.databank.dialog.delete_table.*` — `title`, `content_format` (2)
- `architect.databank.dialog.delete_row.*` — `title`, `content` (2)
- `visualist.dialog.unsaved_layer.*` — `title`, `content_format` (2)
- `visualist.canvas.*` — `empty_hint`, `context.add_node`, `context.bring_to_front`, `context.send_to_back`, `context.delete_wire` (5)
- `dialog.settings.update.*` — `force_download_error_format`, `release_available_format`, `force_download_failed_format`, `unexpected_status_format` (4)

**Also backfilled (previously EN-only):**

- `dialog.updater_progress.unresponsive.body` — was missing from DE / FR / ES.
- `dialog.updater_progress.button.close` — was missing from DE / FR / ES.

**Format-placeholder keys to spot-check verbatim during native review:**

- `architect.databank.dialog.delete_table.content_format` — `{0}` = table name (single-quoted in EN; whatever quoting convention each locale prefers is fine, but the `{0}` index must stay).
- `architect.dialog.var_chain.writers_header_format` / `readers_header_format` — the `{{{0}}}` is intentional: the inner `{{` `}}` are C# string.Format escapes that render literal `{` `}` around the variable name. The locale string should keep both the escaped braces AND the `{0}`/`{1}` indices in the order shown.
- `visualist.dialog.unsaved_layer.content_format` — `{0}` is the layer file name, wrapped in `\"…\"` quotes in EN. Locales should preserve the indexed placeholder; quote style is locale-discretion.
- `dialog.settings.update.release_available_format` — `{0}` remote tag, `{1}` local version, `{2}` asset URL, `{3}` SHA-256 — keep the indices stable across locales. The string also contains a literal `Phoenix.Controls.Updater` brand reference that stays canonical English per the no-translate rule.
- `dialog.settings.update.force_download_error_format` / `force_download_failed_format` / `unexpected_status_format` — `{0}` is a raw error message or .NET type name; pass through verbatim.

**Brand / canonical-English to leave untouched in the new keys:**

- `Phoenix.Controls.Updater` — product name, stays English in all locales (already preserved across DE / FR / ES in the new release-available format).
- `Architect` — pillar product name. The `architect.window.sibling.title` value stays "Architect" in all locales by current convention (matches the existing `pillartab.architect.label = "ARCHITECT"` precedent).
- `Hub` — pillar product name (referenced in `dialog.settings.update.release_available_format`).
- `SHA-256`, `Asset`, `Local version`, `Latest release` — technical terms; the EN bundle uses English headers, the machine-translated bundles localized them where idiomatic and left them English where the convention favors it. Native review can confirm.

**Native-review checklist for the Sprint M keys:**

1. Confirm the placeholder indices `{0}`, `{1}` etc. survive at the same positions or have been reordered consistently with the locale's grammar.
2. Confirm escaped braces `{{ }}` in the `var_chain.*_header_format` keys are still present (they render literal `{varName}` braces in the dialog).
3. Confirm the dialog button shortcuts (`common.button.cancel` etc.) match the local platform convention for affirmative / dismiss / discard semantics.
4. Confirm "Don't Save" maps to a culturally natural negation in DE (`Nicht speichern`), FR (`Ne pas enregistrer`), ES (`No guardar`).
5. Spot-check the longer empty-state hint (`visualist.canvas.empty_hint`) for fluency — multi-clause sentences are the highest-risk machine output.
