# migrate-appdata.ps1
#
# Invoked from PhoenixControls.iss at ssInstall, BEFORE any payload is
# written to {app}, so the user's first launch already finds DB / dock
# layout / updater state at the new path. Defence in depth: the C#
# AppDataMigrator (Phoenix.Controls.Shared/Services/AppDataMigrator.cs)
# also runs at every Hub launch with a more lenient merge policy --
# anything this strict installer-time pass refuses to touch is picked up
# at runtime once the user has resolved the conflict by hand.
#
# Track 8 strict policy (per redesign-plan/PROMPT_track-08-installer-updater.md):
#   * Move-Item only -- NEVER copy-then-delete (a partial copy + delete
#     loses data on volume failure). Cross-volume moves are reported as
#     collisions; user is asked to migrate by hand.
#   * Idempotent -- if the new PhoenixControls/ root already exists and no
#     legacy root is present, the script no-ops and exits 0.
#   * Fail-closed on collision -- if BOTH a legacy root AND the new
#     PhoenixControls/ root are present, log the conflict and exit non-zero.
#     Manual resolution required (the user merges by hand or deletes one
#     side); we never silently union them at install time.
#   * Roaming + Local -- both %APPDATA% and %LOCALAPPDATA% are scanned;
#     both roots are live homes for the suite (see Paths.RoamingAppData /
#     Paths.LocalAppData). Roaming %APPDATA%/PhoenixControls/ holds the
#     primary user state -- the SQLite databank, config.json, window
#     layouts, logs -- while %LOCALAPPDATA%/PhoenixControls/ holds
#     machine-local state (WebView2 profiles, thumbnail/image caches,
#     viewer device tokens).
#   * -WhatIf -- prints the planned move set and exits without touching disk.
#     The Track 8 verify step ("manually run installer/migrate-appdata.ps1
#     -WhatIf") relies on this.
#
# DB filenames: the databank rename shipped as phoenix_v3.db under
# %APPDATA%/PhoenixControls/ -- a fresh filename with deliberately no
# migration shim, so this script has no per-file DB rename to perform.
# Any legacy .db / .db-wal / .db-shm sibling triplet simply rides along
# intact inside the directory move, under whatever filename the legacy
# root used; the current suite ignores it.
#
# Exit codes:
#   0  success / no-op (also returned when -WhatIf was passed)
#   2  collision detected -- legacy root AND new root both present;
#      manual merge required.
#   3  unrecoverable IO error -- a planned move failed and the legacy
#      data remains in place.

[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

# Names used by the pre-rebrand suite. Order matters when both somehow
# exist at once (impossible in practice -- they were never written
# concurrently): the first listed wins for log readability.
$LegacyNames = @('PhoenixSovereign', 'Phoenix.Sovereign')
$NewName     = 'PhoenixControls'

$Roaming = [Environment]::GetFolderPath('ApplicationData')
$Local   = [Environment]::GetFolderPath('LocalApplicationData')

# Track 8 collision policy uses this as the structured exit channel.
$collisions  = New-Object System.Collections.Generic.List[string]
$ioFailures  = New-Object System.Collections.Generic.List[string]
$plannedMoves = New-Object System.Collections.Generic.List[string]

function Test-SameVolume {
    param([string]$Source, [string]$Dest)
    try {
        $srcRoot  = [System.IO.Path]::GetPathRoot($Source).TrimEnd('\','/')
        $destRoot = [System.IO.Path]::GetPathRoot($Dest).TrimEnd('\','/')
        return [string]::Equals($srcRoot, $destRoot, [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Migrate-Pair {
    param(
        [string]$Base,        # %APPDATA% or %LOCALAPPDATA%
        [string]$LegacyName,  # 'PhoenixSovereign' / 'Phoenix.Sovereign'
        [string]$NewName      # 'PhoenixControls'
    )

    if ([string]::IsNullOrEmpty($Base)) { return }

    $oldPath = Join-Path $Base $LegacyName
    $newPath = Join-Path $Base $NewName

    $oldExists = Test-Path -LiteralPath $oldPath -PathType Container
    $newExists = Test-Path -LiteralPath $newPath -PathType Container

    # Idempotent paths -------------------------------------------------------
    if (-not $oldExists -and -not $newExists) {
        # Nothing to do; first install.
        return
    }
    if (-not $oldExists -and $newExists) {
        # Already migrated, or fresh install. No-op.
        Write-Host "[migrate-appdata] $newPath already present, $LegacyName not present -- nothing to do."
        return
    }

    # Collision: both exist -- never silently merge ---------------------------
    if ($oldExists -and $newExists) {
        $msg = "BOTH '$oldPath' AND '$newPath' exist. Manual merge required -- the installer will not silently combine them."
        Write-Host "[migrate-appdata] COLLISION: $msg" -ForegroundColor Red
        $collisions.Add($msg) | Out-Null
        return
    }

    # Single-source migration: oldExists is true, newExists is false ----------
    if (-not (Test-SameVolume -Source $oldPath -Dest $newPath)) {
        # Cross-volume Move-Item silently degrades to copy+delete in
        # PowerShell. The Track 8 spec forbids that (partial copy + delete
        # loses data on volume failure), so we stop here and let the user
        # migrate manually. Logged, exit 3 at the end.
        $msg = "'$oldPath' is on a different volume than the target '$newPath'. The installer will not copy-then-delete cross-volume. Move it manually."
        Write-Host "[migrate-appdata] CROSS-VOLUME: $msg" -ForegroundColor Yellow
        $ioFailures.Add($msg) | Out-Null
        return
    }

    $plannedMoves.Add("$oldPath -> $newPath") | Out-Null

    if ($PSCmdlet.ShouldProcess($oldPath, "Move to '$newPath'")) {
        try {
            # Atomic same-volume rename. Move-Item on a directory does this
            # natively when source and dest are on the same volume.
            Move-Item -LiteralPath $oldPath -Destination $newPath -ErrorAction Stop
            Write-Host "[migrate-appdata] Moved $oldPath -> $newPath"
        } catch {
            $msg = "Move '$oldPath' -> '$newPath' failed: $_"
            Write-Host "[migrate-appdata] ERROR: $msg" -ForegroundColor Red
            $ioFailures.Add($msg) | Out-Null
        }
    }
}

# Walk the four (Base, LegacyName) pairs.
foreach ($base in @($Roaming, $Local)) {
    foreach ($legacy in $LegacyNames) {
        Migrate-Pair -Base $base -LegacyName $legacy -NewName $NewName
    }
}

# Summary + exit ------------------------------------------------------------
if ($WhatIfPreference) {
    Write-Host ""
    Write-Host "[migrate-appdata] -WhatIf: $($plannedMoves.Count) planned move(s):"
    foreach ($m in $plannedMoves) { Write-Host "  $m" }
    if ($collisions.Count -gt 0) {
        Write-Host "[migrate-appdata] -WhatIf: $($collisions.Count) collision(s) would block the install:"
        foreach ($c in $collisions) { Write-Host "  $c" }
    }
    if ($ioFailures.Count -gt 0) {
        Write-Host "[migrate-appdata] -WhatIf: $($ioFailures.Count) cross-volume / IO blocker(s):"
        foreach ($e in $ioFailures) { Write-Host "  $e" }
    }
    exit 0
}

if ($collisions.Count -gt 0) {
    Write-Host ""
    Write-Host "[migrate-appdata] Aborted with $($collisions.Count) collision(s). Resolve manually, then re-run." -ForegroundColor Red
    exit 2
}

if ($ioFailures.Count -gt 0) {
    Write-Host ""
    Write-Host "[migrate-appdata] Completed with $($ioFailures.Count) IO blocker(s). Some legacy data was left in place." -ForegroundColor Yellow
    exit 3
}

exit 0
