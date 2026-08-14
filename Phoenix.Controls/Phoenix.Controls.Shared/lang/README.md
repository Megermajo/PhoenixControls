# Phoenix Controls — Language Bundles

This folder is the canonical home for the suite's UI translation bundles. One JSON file per language, flat dotted keys covering Hub / Architect / Visualist / Shared.

The runtime resolver lives at [`../Localization/Localizer.cs`](../Localization/Localizer.cs); the user picker lives in `Phoenix.Controls.Hub.WinUI/Dialogs/SettingsDialog.xaml.cs` (Tools → Settings in Hub's `MainWindow` opens it focused on the Language row).

## File layout

```
lang/
├── en.json   ← canonical / fallback (always present, never edited by anything but humans)
├── de.json   ← optional, future
└── …
```

`en.json` is the source of truth for English values **and** the safety-net that every other language falls back to. Removing a key from `en.json` is a breaking change.

## Distribution

`Phoenix.Controls.Shared.csproj` declares `lang/*.json` as `Content` with `CopyToOutputDirectory=PreserveNewest`. Every consumer (Hub, Architect, Visualist, Viewer) inherits via `<ProjectReference>` and ships its own `bin/{config}/net8.0-windows/lang/` populated automatically. **Do not duplicate JSON files into per-project folders** — keep this one canonical copy.

## Persistence

The user's chosen language code lives at:

```
%AppData%/PhoenixControls/language.json
```

This sits alongside the SQLite databank (same `%AppData%/PhoenixControls/` folder) so all three suite processes read the same preference at startup. Schema is trivial:

```json
{ "language": "en" }
```

Hub's `SettingsDialog` writes it via `LanguageConfig.Save(code)`. Architect and Visualist read it via `LanguageConfig.Load()` during their own `Localizer.Init` call.

## Adding a new language

1. Copy `en.json` → `<code>.json` where `<code>` is a BCP-47 / .NET-recognized code (`de`, `fr`, `es`, `pt-BR`, …).
2. Translate the **values** only. Keys are persistence — never rename or remove them.
3. Restart any running suite app to load the new bundle (live-switch is a future milestone).
4. Run `pwsh ./check-keys.ps1` to verify no keys are missing.

When you add a language, the `Localizer` discovers it automatically from the `lang/*.json` filename glob — no code changes needed.

The Settings → Language dropdown shows each language's native name plus its code (e.g. `Deutsch (de)`) by looking up `CultureInfo.GetCultureInfo(code).NativeName`. If the code isn't a recognized .NET culture, the dropdown falls back to displaying just the code.

## Key naming conventions

Flat dotted keys, lowercase, snake_case in segments:

```
<pillar>.<area>.<element>.<purpose>
```

| Segment | Examples |
|---|---|
| `pillar`  | `hub`, `architect`, `visualist`, `shared`, `common` |
| `area`    | `mainwindow`, `settings`, `systemlog`, `canvas`, `node`, `panel`, `validation` |
| `element` | `button`, `menu`, `dialog`, `column`, `tooltip`, `status`, `error` |
| `purpose` | `save`, `cancel`, `not_found`, `connection_failed`, `restart_notice` |

Examples:

```
hub.mainwindow.title
hub.settings.row.streamerbot_url
architect.canvas.context.add_node
architect.node.bubble.math_add
visualist.timeline.transport.play
```

Two strings that share English text but appear in different places get different keys. The cost is one duplicate value; the gain is per-context translatability (e.g. a "Save" button vs a "Save changes?" dialog can diverge).

## How a UI string reaches the Localizer

Three mechanisms, in the order you should reach for them.

### 1. `Localizer.T(key, englishFallback)` — C# surfaces

The original and still the right tool for anything set from code: dialog text, state
phrases, `string.Format` templates, code-behind that already holds a reference to the
element.

```csharp
PageHeader.Title = Localizer.T("panel.scheduling.header.title", "Scheduling");
```

Always pass the English fallback. It is what renders if the key is missing from every
bundle, so a typo degrades to correct English rather than to `[panel.x.y]`.

### 2. `loc:Localize.*` attached properties — XAML surfaces

One attribute beside the literal, no `x:Name`, no code-behind:

```xml
xmlns:loc="using:Phoenix.Controls.Hub.WinUI.Localization"
…
<TextBlock Text="Chat line" loc:Localize.Key="panel.scheduling.message.line.label" />
<TextBox PlaceholderText="e.g. hello"
         AutomationProperties.Name="Greeting"
         loc:Localize.PlaceholderKey="panel.x.greeting.placeholder"
         loc:Localize.AutomationKey="panel.x.greeting.a11y" />
```

| attribute | writes |
|---|---|
| `Localize.Key` | the element's primary text slot — `TextBlock.Text`, `ContentControl.Content`, `ToggleSwitch.Header`, `ContentDialog.Title`, or a custom control's `Text`/`Title`/`Label`/`Caption`/`Eyebrow` |
| `Localize.Property` | names the target member explicitly when the default would pick the wrong one |
| `Localize.HeaderKey` | `Header` |
| `Localize.PlaceholderKey` | `PlaceholderText` |
| `Localize.TooltipKey` | `ToolTipService.ToolTip` |
| `Localize.AutomationKey` | `AutomationProperties.Name` |
| `Localize.HelpTextKey` | `AutomationProperties.HelpText` |

**The English literal stays and is the fallback** — every setter resolves through
`Localizer.T(key, currentValue)`, reading the current value first. **Resolution happens at
`Loaded`**, because XAML applies attributes in document order and writing at parse time
would lose a race with a `Text=` that follows the key attribute in the same tag.

`TextBox.Text` is never localized: it holds the streamer's data.

The class is deliberately **duplicated in all three WinUI projects** — a `DependencyProperty`
needs `Microsoft.UI.Xaml`, and `Phoenix.Controls.Shared.WinUI` carries no WindowsAppSDK
reference by design. `LocalizeAttachedPropertyTests` fails the build if the three copies
drift, or if a view declares another pillar's namespace.

### 3. Control-owned `*Key` properties

`PanelHeader` resolves `TitleKey` → `Title` and `EyebrowKey` → `Eyebrow` itself, in its own
`Loaded`. Use these rather than the attached property on that control.

### What guards it

| test | catches |
|---|---|
| `LocalizationBundleParityTests` | a key present in one bundle and missing from another; lost BOM; empty values |
| `LocalizationParityTests` | a translated value whose `{placeholder}` set differs from English |
| `LocalizationKeyCoverageTests` | a key the code asks for that no bundle defines — including keys passed through helper APIs (`Tip`, `PopOutWindowFactory.Create`, `MenuEntry`, `ToolRow`/`ToolGroup`, `SafeCreate`, `BuildPlaceholderPanel`) and keys living only in XAML attributes. Its backstop fails on any key-shaped literal that is neither a bundle key nor documented as a non-key |
| `XamlLiteralLocalizationTests` | a hardcoded user-facing literal in shipped XAML with no key beside it — the only test that can see a surface which never calls the Localizer at all |
| `LocalizationBundleIntegrityTests` | mojibake regression in de/fr/es |

The fourth one exists because every other test starts from something that *exists* — a
bundle entry or a found call site. A panel with zero call sites contributes zero rows to
every harvest and is indistinguishable from a fully localized one; thirteen Pre-Build panels
shipped 100% hardcoded at 4382/4382 green before it was written.

## Format-string placeholders

Keys whose runtime value comes from `string.Format(Localizer.T("key"), arg)` use positional `{0}`, `{1}`, `{2}` placeholders:

```json
"hub.mainwindow.error_bar.script_error_repeat": "  SCRIPT ERROR ({0}x): {1}"
```

The C# call site is:

```csharp
string.Format(Localizer.T("hub.mainwindow.error_bar.script_error_repeat"), count, message)
```

Translators must preserve the placeholders verbatim. Reordering them per locale is fine — `string.Format` resolves by index, not position.

## What is NOT translated

Per project rules, the following stay canonical English regardless of selected language:

- **`NodeId`** strings (e.g. `"Math.Add"`, `"Flow.Begin"`) — persistence + dispatch keys.
- **Node header display names** — by user request, all node names stay English.
- **Socket labels** (e.g. `"A"`, `"B"`, `"Result"`, `"Condition"`) — terse technical names.
- **All `Flow.*` category text** including bubble descriptors — by user request.
- **Bus message types** (`VISUAL_TRIGGER`, `HUB_EVENT`, etc.) — wire protocol.
- **`.phx` script source** — programming language syntax.
- **Streamer.bot / Twitch payloads** — external API identifiers.
- **`SocketDataType` enum identifiers** — serialization contract.
- **Log source identifiers** (the second arg to `GlobalLogger.Log(msg, "System")`) — category tags, not chrome.
- **`AppRole` values** (`"pillar_hub"`, `"pillar_architect"`, etc.) — internal role keys.

If you find yourself adding keys that overlap any of the above, stop and ask — the localization scope was deliberate.

## Translator quick-reference (DE / FR / ES already shipped)

Captured here so future translators (PT-BR, IT, NL, …) have the same conventions the DE / FR / ES pass used in 0.7.0.

### Ground rules (non-negotiable)

1. **Keys are persistence — never rename, reorder, or remove a key.** The output JSON has the same key set as `en.json`, in the same order, with translated values only.
2. **Preserve placeholders verbatim:** `{0}`, `{1}`, `{var.X}`, `{user.foo}`, `{global.bar}`, `{Args1}`, etc. Reordering `{0}` / `{1}` per locale grammar is fine — `string.Format` resolves by index.
3. **Preserve embedded newlines and whitespace exactly:** `\n`, `\r\n`, leading / trailing spaces (some keys are intentionally indented or padded — e.g. `"  SCRIPT ERROR ({0}x): {1}"`).
4. **Do not translate canonical English tokens that appear inline** — see the *What is NOT translated* list above, plus the file-extension tokens `.phx` / `.phxg` / `.phxlayer`, keyboard chords (`Ctrl+S`, `F5`, `Alt+Click`), and `%AppData%` / path literals.
5. **Shorter is better when space is tight.** Toolbar buttons, status pills, column headers, and menu entries should not balloon: aim for ≤ 1.3× the English length on UI chrome. Long descriptive `bubble` / `description` / `where` strings can grow naturally.
6. **Tone: informal "you" — Twitch streamer audience.**
   - German: **du / dich / dir**, lowercase pronoun (modern convention, not the formal capitalized "Du").
   - French: **tu / toi / te**.
   - Spanish: **tú / te / ti** (Latin American neutral; avoid voseo).
7. **Output is exactly one valid JSON object.** **UTF-8 *with* BOM** (`EF BB BF`) — double-quoted keys and values, escape `\\`, `\"`, `\n`, `\r`, `\t` correctly. No trailing commas. No comments.
   > This rule read "UTF-8 without BOM" until 2026-08-03 and was wrong about the very files it describes: all four shipped bundles start `EF BB BF`, and stripping it is how the `Ã`-mojibake regression gets reintroduced — `LanguageBundle.LoadFromFile` reads either form, but an editor or re-encoding tool with no BOM to go on guesses the codepage and guesses wrong. Save **with** BOM. `LocalizationBundleParityTests.Every_Bundle_Starts_With_The_Utf8_Bom` fails the build if a bundle loses it, and `LocalizationBundleIntegrityTests` is what catches the mojibake once it lands.
8. **Sanity-check before declaring done:** JSON round-trips through `JsonSerializer.Deserialize<Dictionary<string,string>>`, same key count as `en.json` (**880 at this tip** — `LocalizationBundleParityTests.Every_Bundle_Has_Exactly_The_Same_Key_Set_As_English` is the authority, and it compares key SETS, not counts), every `{N}` / `{name}` placeholder from the source appears in the translated value with the same indices / names. `pwsh ./check-keys.ps1` is the canonical audit.

### Domain glossary (vocabulary the shipped bundles use)

Recurring terms whose translation must stay consistent across the whole bundle. These are the choices DE / FR / ES locked in during 0.7.0; new locales should keep them aligned unless there's a strong idiomatic reason to diverge.

| English | DE | FR | ES | Note |
|---|---|---|---|---|
| Hub / Architect / Visualist / Viewer | — | — | — | Proper nouns, do not translate |
| Streamer.bot / Twitch / OBS | — | — | — | Do not translate |
| layer | Layer | calque | capa | Visualist OBS browser-source unit |
| widget | Widget | widget | widget | Keep English in DE / FR / ES |
| trigger (noun / verb) | Trigger / auslösen | déclencheur / déclencher | disparador / disparar | |
| node | Knoten | nœud | nodo | Graph element |
| socket | Socket | socket | conector | Pin on a node |
| wire | Verbindung | liaison | conexión | Edge between sockets |
| macro | Makro | macro | macro | |
| script | Skript | script | script | |
| canvas | Canvas | canvas | lienzo | Graph editing surface |
| keyframe | Keyframe | image-clé | fotograma clave | |
| event | Event | événement | evento | |
| panel | Panel | panneau | panel | |
| log / logging | Log / Protokoll | journal | registro | |
| settings | Einstellungen | paramètres | ajustes | |
| save / load | Speichern / Laden | Enregistrer / Charger | Guardar / Cargar | |
| close / cancel / OK | Schließen / Abbrechen / OK | Fermer / Annuler / OK | Cerrar / Cancelar / OK | |
| chat | Chat | chat | chat | Twitch context |
| stream / streamer | Stream / Streamer | stream / streamer | stream / streamer | Keep English |
| overlay | Overlay | overlay | overlay | OBS context, keep English |
| browser source | Browser-Source | source navigateur | fuente de navegador | OBS term |
| databank | Databank | banque de données | banco de datos | Phoenix's SQLite layer |

## Tools

### `check-keys.ps1`

Diff between keys referenced in code and keys present in `en.json`:

```pwsh
pwsh ./check-keys.ps1
# or with a specific worktree path:
pwsh ./check-keys.ps1 -Root c:/path/to/Phoenix.Controls
```

Output:

- **Missing** — referenced by `Localizer.T(...)` but not defined in `en.json`. Will render as `[key]` placeholders at runtime.
- **Orphan** — defined in `en.json` but not referenced anywhere in code. Safe to delete.
- **Per-language coverage** — for each non-en bundle, count of keys present vs total.

Exit code is non-zero when missing keys exist, so CI can gate on it.

### Runtime missing-key warning

`Localizer` logs at `LogLevel.Debug` (one line per key per session) when a key falls through from the current language to the English fallback. The line shape is:

```
Localizer: missing key 'hub.foo.bar' in 'de' — using 'en' fallback
```

These surface in the Hub's System Log window at Debug level. Filter by source `"Localizer"` to see them.

> This warning was **documented here from the start and did not actually exist** until the
> localization retrofit: `Localizer._missingKeysLogged` was declared and cleared on `Init`,
> and nothing ever added to it. So a de/fr/es gap produced English text with no exception,
> no log line and no failing test — invisible from every direction at once. It is live now.
> It only ever fires for a *translation* gap (the key is in `en.json` but not in the active
> bundle); a key missing from **all** bundles never reaches the Localizer at all, which is
> what the build-time guards below are for.

## Live-switching (deferred)

v1 is restart-required. Future live-switching will introduce a `Localizer.LanguageChanged` event that every form subscribes to and re-applies via a per-form `LocalizeUI()` pass. The runtime is structured so adding the event is a one-place change — every existing `Localizer.T(...)` call is already cheap (dictionary lookup) and safe to call from paint code.
