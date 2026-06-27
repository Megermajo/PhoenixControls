# CreateShortcuts.ps1
# Run this script once to create .lnk shortcuts for the Phoenix Controls
# launcher and key data folders. Shortcuts are machine-specific and are
# .gitignored in the dev tree.
#
# Works in two modes — auto-detected from the script's siblings:
#
#   1. Release-zip mode (default). Layout:
#        <root>/CreateShortcuts.ps1           <- this script
#        <root>/Hub/Phoenix.Controls.Hub.WinUI.exe
#        <root>/Hub/data/assets/
#        <root>/Hub/data/logic/
#        <root>/Viewer/...
#        <root>/Updater/...
#      Matches .github/workflows/release.yml staging.
#
#   2. Dev-tree mode (detected when a sibling `Phoenix.Controls/` source
#      folder is present). Targets the Debug build output instead so the
#      same script keeps working from a git checkout:
#        <root>/Phoenix.Controls/Phoenix.Controls.Hub.WinUI/bin/Debug/net8.0-windows10.0.19041.0/Phoenix.Controls.Hub.WinUI.exe
#        <root>/Phoenix.Controls/Phoenix.Controls.Hub/data/assets/
#        <root>/Phoenix.Controls/Phoenix.Controls.Hub/data/logic/
#
# Post-T15 layout: Hub.WinUI.exe is the single user-facing entry point. It
# hosts Architect and Visualist internally via its pillar tabs, so we no
# longer ship separate Architect / Visualist shortcuts (their WinUI
# projects are libraries now, not exes).

$root = $PSScriptRoot
$wsh  = New-Object -ComObject WScript.Shell

# -- Mode detection ---------------------------------------------------------
# Dev tree = sibling `Phoenix.Controls/` source folder next to the script.
# Anything else (release zip, installer output) uses release layout.
$devSourceRoot = Join-Path $root 'Phoenix.Controls'
$isDevTree     = Test-Path (Join-Path $devSourceRoot 'Phoenix.Controls.sln')

if ($isDevTree) {
    $hubExe   = Join-Path $devSourceRoot 'Phoenix.Controls.Hub.WinUI/bin/Debug/net8.0-windows10.0.19041.0/Phoenix.Controls.Hub.WinUI.exe'
    $dataBase = Join-Path $devSourceRoot 'Phoenix.Controls.Hub/data'
    Write-Host "Detected Dev tree layout (Phoenix.Controls/ source folder present)."
} else {
    $hubExe   = Join-Path $root 'Hub/Phoenix.Controls.Hub.WinUI.exe'
    $dataBase = Join-Path $root 'Hub/data'
    Write-Host "Using release-zip layout (Hub/ next to this script)."
}

function New-Shortcut {
    param(
        [string]$Name,
        [string]$Target,
        [string]$Description = ''
    )

    $lnk = $wsh.CreateShortcut("$root\$Name.lnk")
    $lnk.TargetPath       = $Target
    $lnk.Description      = $Description
    $lnk.WorkingDirectory = Split-Path $Target -Parent
    $lnk.Save()
    Write-Host "Created: $Name.lnk -> $Target"
}

# -- App launcher -----------------------------------------------------------
# Single entry point: Hub.WinUI hosts Architect / Visualist via pillar tabs.

New-Shortcut `
    -Name        'Launch Phoenix Controls' `
    -Target      $hubExe `
    -Description 'Phoenix Controls - Hub runtime with Architect and Visualist pillar tabs'

# -- Data folder shortcuts --------------------------------------------------

$assetsFolderLnk = $wsh.CreateShortcut("$root\Open Assets Folder.lnk")
$assetsFolderLnk.TargetPath  = "$dataBase\assets"
$assetsFolderLnk.Description = 'Overlay media assets served at /assets/'
$assetsFolderLnk.Save()
Write-Host "Created: Open Assets Folder.lnk -> $dataBase\assets"

$logicFolderLnk = $wsh.CreateShortcut("$root\Open Logic Folder.lnk")
$logicFolderLnk.TargetPath  = "$dataBase\logic"
$logicFolderLnk.Description = 'Phoenix Controls Script - generated .phx logic files (paired .phxg graphs author them)'
$logicFolderLnk.Save()
Write-Host "Created: Open Logic Folder.lnk -> $dataBase\logic"

Write-Host ""
Write-Host "Done. Three shortcuts created next to this script."
Write-Host "Note: shortcuts are machine-specific (full local paths)."
