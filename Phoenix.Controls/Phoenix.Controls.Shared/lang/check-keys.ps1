#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Diff between Localizer.T() keys referenced in code and keys present in lang/en.json.

.DESCRIPTION
  Walks Phoenix.Controls/**/*.cs (and .xaml), extracts every key reference, and compares
  against the en.json bundle. Reports:

    - Missing keys: referenced by code but not in en.json (will render as [key] at runtime).
    - Orphan keys:  defined in en.json but unused in code (safe to delete).
    - Per-language coverage: for each non-en bundle, count of keys present vs total.

  Three reference-extraction passes run per file. (1) the canonical
  Localizer.T("literal") pattern; (2) interpolated lookups built via the
  BubbleKeyFor helper in NodeRegistry.Templates.cs, expanded by mirroring the
  helper's lowercase-snake-case rewrite; (3) any dotted snake_case string
  literal that matches the localization-key shape — needed because keys are
  passed as plain strings into TitleKey="..." XAML attributes, MenuDefinition
  rows, WirePopOut(...) tuples, and other helper APIs where a strict
  Localizer.T regex would miss them. Pass (3) is permissive on purpose: it
  errs on the side of "this key is probably used" so the audit doesn't drown
  a real orphan in 50 false positives. The strict Localizer.T count is still
  reported separately so the canonical-usage signal is preserved.

  Exit code 0 = no missing keys; 1 = missing keys found (so CI can gate on it).

  ASCII-only on purpose: Windows PowerShell 5.1 reads .ps1 files as cp1252 by default
  and chokes on non-BOM UTF-8 with non-ASCII glyphs. Keeping the source ASCII lets the
  same script run on PS 5.1 and PS 7+ without a BOM dance.

.PARAMETER Root
  Path to the Phoenix.Controls solution folder. Defaults to the path two levels up
  from this script (i.e. the script lives at Phoenix.Controls.Shared/lang/).

.PARAMETER ShowOrphans
  When set, lists every orphan key. Default is to print the count + the first
  20, since most "orphans" found by pass (3) are likely false positives a human
  audit needs to decide on.

.EXAMPLE
  pwsh ./check-keys.ps1
  pwsh ./check-keys.ps1 -Root c:/tmp/sovereign-loca-rework/Phoenix.Controls
  pwsh ./check-keys.ps1 -ShowOrphans
#>

[CmdletBinding()]
param(
    [string]$Root,
    [switch]$ShowOrphans
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Root = Resolve-Path (Join-Path $scriptDir '..\..')
}

if (-not (Test-Path $Root)) {
    Write-Error "Root folder not found: $Root"
    exit 2
}

$bundleDir = Join-Path $Root 'Phoenix.Controls.Shared/lang'
$enPath    = Join-Path $bundleDir 'en.json'

if (-not (Test-Path $enPath)) {
    Write-Error "Canonical bundle not found: $enPath"
    exit 2
}

# Load en.json. ConvertFrom-Json returns PSCustomObject in Windows PowerShell 5.1
# (no -AsHashtable until PS 6+). Read property names off PSObject so the script
# runs on both editions.
$en = Get-Content -LiteralPath $enPath -Raw -Encoding UTF8 | ConvertFrom-Json
$enKeyArr = @($en.PSObject.Properties.Name)
$enKeys = [System.Collections.Generic.HashSet[string]]::new([string[]]$enKeyArr)

# --- Reference-extraction patterns -------------------------------------------------

# (1) Canonical Localizer.T() — the precise signal. Matches Localizer.T("key")
# and Localizer.T($"key") for literal-only keys. Convention is lowercase
# snake_case-segment dotted, but a small handful of legacy camelCase keys exist
# in WidgetGraphCanvas — accept both so the audit stays accurate until those
# are renamed.
$strictPattern = 'Localizer\.T\(\$?"([a-zA-Z0-9_.]+)"'

# (2) BubbleKeyFor("Math.Add") — at NodeRegistry.Templates.cs:423 the helper is
# defined as: "architect.node.bubble." + title.Replace(".", "_").ToLowerInvariant().
# We mirror that here so the resolved key is treated as referenced. The same
# expansion fires for AddBinaryOp / AddUnaryOp call sites in
# NodeRegistry.Templates.{LogicAsync,DataUtilities}.cs — both helpers forward
# their first-argument title through BubbleKeyFor before invoking Localizer.T.
$bubbleKeyForPattern = '(?:BubbleKeyFor|AddBinaryOp|AddUnaryOp)\s*\("([^"]+)"'

# (3) Any string literal in code/markup that looks like a localization key.
# Two or more dotted lowercase snake_case segments. Used to defang the orphan
# list when keys travel as plain strings through helpers (TitleKey="...",
# MenuDefinition.Item("chrome.hub.file.exit", ...), tuple args to
# WirePopOut(...), etc.). This is permissive — any random string that happens
# to match the shape will be treated as "used" — but the cost of a missed
# orphan is delete-restore-with-a-grep, while the cost of a false-positive
# orphan is wasted human triage that erodes trust in the audit.
$keyLiteralPattern = '"([a-z][a-z0-9_]*(?:\.[a-z][a-zA-Z0-9_]*){1,5})"'

# XAML attribute pattern: TitleKey="popout.title.chat", Tag="some.key",
# LocalizationKey="x.y.z", etc. Single-quoted variants are also valid in XAML.
$xamlKeyAttrPattern = '(?:Key|Tag)\s*=\s*"([a-z][a-z0-9_]*(?:\.[a-z][a-zA-Z0-9_]*){1,5})"'

$strictKeys     = [System.Collections.Generic.HashSet[string]]::new()
$expansionKeys  = [System.Collections.Generic.HashSet[string]]::new()
$permissiveKeys = [System.Collections.Generic.HashSet[string]]::new()
$skipFolders    = @('bin', 'obj', '.git', '.vs')

# C# pass. Strip single-line `//` comments before pattern-matching so that
# inert documentation references (e.g. TestAssemblyInit.cs naming
# "architect.node.bubble.X" inside a `// comment` as a placeholder) don't show
# up as missing keys. Multiline /* */ comments are rare in this codebase and
# left intact.
Get-ChildItem -LiteralPath $Root -Filter '*.cs' -Recurse -File |
    Where-Object {
        $p = $_.FullName
        -not ($skipFolders | Where-Object { $p -match "[\\/]$_[\\/]" })
    } |
    ForEach-Object {
        $raw = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        $text = ($raw -split "`n" | ForEach-Object { $_ -replace '(?<!:)//.*$', '' }) -join "`n"

        foreach ($m in [regex]::Matches($text, $strictPattern)) {
            [void]$strictKeys.Add($m.Groups[1].Value)
        }
        foreach ($m in [regex]::Matches($text, $bubbleKeyForPattern)) {
            $title = $m.Groups[1].Value
            $resolved = 'architect.node.bubble.' + ($title -replace '\.', '_').ToLowerInvariant()
            [void]$expansionKeys.Add($resolved)
        }
        foreach ($m in [regex]::Matches($text, $keyLiteralPattern)) {
            [void]$permissiveKeys.Add($m.Groups[1].Value)
        }
    }

# XAML pass — strings live in attribute values, not inside Localizer.T(...).
Get-ChildItem -LiteralPath $Root -Filter '*.xaml' -Recurse -File |
    Where-Object {
        $p = $_.FullName
        -not ($skipFolders | Where-Object { $p -match "[\\/]$_[\\/]" })
    } |
    ForEach-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8

        foreach ($m in [regex]::Matches($text, $xamlKeyAttrPattern)) {
            [void]$permissiveKeys.Add($m.Groups[1].Value)
        }
        foreach ($m in [regex]::Matches($text, $keyLiteralPattern)) {
            [void]$permissiveKeys.Add($m.Groups[1].Value)
        }
    }

# Union of all reference passes.
$codeKeys = [System.Collections.Generic.HashSet[string]]::new($strictKeys, [StringComparer]::Ordinal)
foreach ($k in $expansionKeys)  { [void]$codeKeys.Add($k) }
foreach ($k in $permissiveKeys) { [void]$codeKeys.Add($k) }

# Compute the diffs.
$missing = $strictKeys.Where({ -not $enKeys.Contains($_) }) | Sort-Object
$orphans = $enKeys.Where({ -not $codeKeys.Contains($_) }) | Sort-Object

Write-Host ""
Write-Host "Phoenix Controls - localization key audit" -ForegroundColor Cyan
Write-Host ("Root: {0}" -f $Root)
Write-Host ("Bundle: {0}" -f $enPath)
Write-Host ""
Write-Host ("Strict Localizer.T() references: {0} unique keys" -f $strictKeys.Count)
Write-Host ("BubbleKeyFor() expansions:       {0} unique keys" -f $expansionKeys.Count)
Write-Host ("Permissive key-shape literals:   {0} unique keys" -f $permissiveKeys.Count)
Write-Host ("Union of all reference passes:   {0} unique keys" -f $codeKeys.Count)
Write-Host ("en.json defines:                 {0} keys" -f $enKeys.Count)
Write-Host ""

if ($missing.Count -eq 0) {
    Write-Host "[OK]   No missing keys (strict Localizer.T pass)." -ForegroundColor Green
} else {
    Write-Host ("[FAIL] MISSING ({0}) - referenced by Localizer.T(), not in en.json:" -f $missing.Count) -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "    $_" }
}

Write-Host ""

if ($orphans.Count -eq 0) {
    Write-Host "[OK]   No orphan keys." -ForegroundColor Green
} else {
    Write-Host ("[WARN] ORPHANS ({0}) - defined in en.json, no reference found (incl. permissive pass):" -f $orphans.Count) -ForegroundColor Yellow
    if ($ShowOrphans -or $orphans.Count -le 20) {
        $orphans | ForEach-Object { Write-Host "    $_" }
    } else {
        ($orphans | Select-Object -First 20) | ForEach-Object { Write-Host "    $_" }
        Write-Host ("    ... and {0} more (re-run with -ShowOrphans to list all)" -f ($orphans.Count - 20))
    }
}

# Per-language coverage.
$bundles = Get-ChildItem -LiteralPath $bundleDir -Filter '*.json' -File |
    Where-Object { $_.BaseName -ne 'en' } |
    Sort-Object BaseName

if ($bundles.Count -gt 0) {
    Write-Host ""
    Write-Host "Per-language coverage (against en.json):" -ForegroundColor Cyan
    foreach ($b in $bundles) {
        $other = Get-Content -LiteralPath $b.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $otherKeys = @($other.PSObject.Properties.Name)
        $present = ($otherKeys | Where-Object { $enKeys.Contains($_) }).Count
        $pct = if ($enKeys.Count -gt 0) { ($present * 100.0 / $enKeys.Count) } else { 0 }
        Write-Host ("  {0,-12} {1,5} / {2}  ({3:N1}%)" -f $b.BaseName, $present, $enKeys.Count, $pct)
    }
}

Write-Host ""

if ($missing.Count -gt 0) { exit 1 }
exit 0
