; ====================================================================
; PhoenixControls.iss — Inno Setup 6 script for the Phoenix Controls
; retail-brand installer.
;
; **Single fully-bundled installer, built locally.** One `.exe` per
; build, period. The runtime payload is compressed into the .exe itself
; via solid lzma2/ultra; there is no install-time download, no companion
; .zip, no .sha256 sidecar, no third-party Inno Download Plugin
; dependency, no GitHub Actions workflow. The streamer downloads ONE
; file, runs it, and the suite is laid out under {app}\... by Inno's
; own extractor (which integrity-checks the bundled files).
;
;   iscc /DPayloadRoot=<dist-tree> PhoenixControls.iss
;       → Output\Phoenix Controls Setup.exe
;
; PayloadRoot is the staged runtime tree — the parent of the per-pillar
; folders. Only Hub\, Viewer\, and Updater\ ship
; as separate folders; Architect.WinUI / Visualist.WinUI are libraries
; whose DLLs already live in Hub\. Pass an absolute path on the iscc
; command line. build-local.ps1 stages the tree and invokes iscc with
; the correct flag for you.
;
; The installer's product version (Add/Remove Programs entry) is fixed
; at 1.0 and intentionally independent of the suite version. Each
; rebuild bundles whatever runtime you have staged at iscc time.
;
; The installer:
;   • Runs an AppData migration shim BEFORE writing any files, so the
;     first launch of Hub already finds DB / dock-layout / updater state
;     at %AppData%/PhoenixControls/. The C# AppDataMigrator runs again
;     at Hub startup as defence-in-depth.
;   • Lays the suite out as
;       {app}\Hub\Phoenix.Controls.Hub.WinUI.exe   (+ Architect/Visualist DLLs)
;       {app}\Viewer\Phoenix.Controls.Viewer.exe
;       {app}\Updater\Phoenix.Controls.Updater.exe
;     The suite collapsed to a single user-facing app: Architect.WinUI and
;     Visualist.WinUI are libraries whose DLLs ride in Hub\ via
;     ProjectReferences. The Viewer stays WinForms for now (a follow-up
;     will port it to a WinUI Window host).
;   • Phoenix Controls is presented as ONE app from the user's POV:
;     Hub.WinUI is the only user-facing entry point and embeds
;     Architect / Visualist as MainView UserControls inside its pillar
;     tabs. The installer therefore writes a single "Phoenix Controls"
;     Start Menu shortcut pointing at Hub. The Viewer / Updater binaries
;     are still laid down under {app}\... because Hub launches them as
;     child processes, but the streamer never sees them as separate
;     "apps".
;   • Opt-in desktop shortcut for Hub plus opt-in "Launch Hub at
;     sign-in" (HKCU\Run).
;   • Defaults to per-user install (%LocalAppData%\Programs\Phoenix Controls\)
;     with a UI option to elevate to per-machine install in Program Files.
;   • Brand-tinted wizard banner via WizardImageFile.
; ====================================================================

#define MyAppName       "Phoenix Controls"
#define MyAppPublisher  "Megermajo"
#define MyAppURL        "https://github.com/Megermajo/PhoenixControls"
; Hub.WinUI is the only standalone exe in the
; payload — Architect.WinUI and Visualist.WinUI are libraries embedded
; into Hub. File associations therefore route through Hub.WinUI; if/when
; Hub learns to honour `--open <path>` (currently ignored at launch) the
; .phxg/.phxlayer associations below will deep-link automatically.
#define MyAppExeName    "Phoenix.Controls.Hub.WinUI.exe"

; Installer's own product version. Hardcoded — independent of the suite
; version of whatever payload is bundled. Each build embeds whatever was
; staged at iscc time, but the .exe itself reports as 1.0 in Add/Remove
; Programs so upgrades stack correctly via AppId.
#define AppVersion "1.0"

; PayloadRoot — directory whose children become {app}\... at install time.
; Must be passed via /DPayloadRoot=<absolute-path> on the iscc command
; line. Compile fails fast if the directory is missing so a typo can
; never produce a runtime-less installer. build-local.ps1 stages the
; tree and computes the absolute path for you.
#ifndef PayloadRoot
  #error PayloadRoot is required — invoke iscc with /DPayloadRoot=<path-to-staged-dist>. Use build-local.ps1 for the standard local-build path.
#endif
#if !DirExists(PayloadRoot)
  #error PayloadRoot does not exist — the path passed via /DPayloadRoot must be the staged 'phoenix-controls/' tree containing Hub\, Architect\, Visualist\, etc.
#endif

[Setup]
; AppId — stable GUID identifying this product across versions. Required
; for Inno's upgrade/uninstall machinery (Add/Remove Programs registration).
; Do NOT change once shipped; reissuing the GUID would orphan existing
; installs (the old install would not be detected as upgradable).
AppId={{D8E1F2A0-7C5B-4D11-9F3C-FE71B91A02D4}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\Phoenix Controls
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableReadyPage=no
OutputDir=Output
OutputBaseFilename=PhoenixControls-Setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
; Brand assets — wizard banner composites the real phoenix-mark.png onto
; a coal+ember background in the Phoenix palette;
; SetupIconFile gives the installer .exe itself the Phoenix Controls
; icon (same app.ico every pillar ships). Both are regenerated /
; re-copied via branding/generate-wizard-banner.ps1 + a manual cp from
; Hub.WinUI/Assets/ when the design refreshes.
WizardImageFile=branding\wizard-banner.png
WizardImageStretch=no
WizardImageAlphaFormat=defined
SetupIconFile=branding\app.ico
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\Hub\{#MyAppExeName}
; Per-user installs go to %LocalAppData%\Programs\Phoenix Controls
; (the Windows convention for non-elevated app installs); per-machine
; goes to {pf}\Phoenix Controls. {autopf} resolves to the right one.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Tasks — opt-in post-install actions ──────────────────────────────────
;
; Phoenix Controls is one app from the user's POV (Hub spawns Architect /
; Visualist internally), so there is no per-pillar component split — the
; full payload always installs and only the opt-in shortcuts / file
; associations / sign-in launch are user-selectable.
[Tasks]
Name: "desktopHub";   Description: "Create a desktop shortcut";                       GroupDescription: "Additional shortcuts:"; Flags: checkedonce
Name: "registerPhx";  Description: "Register .phx + .phxg file types with Phoenix Controls"; GroupDescription: "File associations:"; Flags: checkedonce
Name: "runOnSignin";  Description: "Launch Phoenix Controls when I sign in to Windows";     GroupDescription: "Startup:"; Flags: unchecked

[Files]
; The runtime payload is bundled directly into the installer .exe via solid
; lzma2/ultra compression. PayloadRoot's children become {app}\... at install
; time (Hub\, Architect\, Visualist\, Viewer\, Updater\, …). recursesubdirs
; + createallsubdirs preserves the dist tree shape verbatim; ignoreversion
; treats every file as content (never compares versions during upgrades, so
; the entire payload is rewritten cleanly even if a sibling .dll was
; downgraded between releases).
;
; USER-DATA SPLIT — {app}\Hub\data\ is the user's working set (scripts in
; logic\, layers\, media\, assets\, config.json). The payload ships seed
; copies of those same files, and a blanket `ignoreversion` rewrite would
; silently clobber user-edited scripts / layers on every Setup.exe upgrade.
; The payload is therefore installed in three slices:
;   1. Everything OUTSIDE Hub\data → full rewrite (binaries, runtime).
;   2. Release-owned data subtrees (overlay\, streamerbot\) → full rewrite
;      (runtime code / import pack the new build expects; not user-authored).
;   3. The rest of Hub\data → `onlyifdoesntexist`: seeds land on a fresh
;      install, existing files are NEVER touched on upgrade. Marked
;      `uninsneveruninstall` so an uninstall keeps the user's scripts,
;      layers, media and settings on disk.
Source: "{#PayloadRoot}\*"; DestDir: "{app}"; Excludes: "\Hub\data\*"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\Hub\data\overlay\*"; DestDir: "{app}\Hub\data\overlay"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\Hub\data\streamerbot\*"; DestDir: "{app}\Hub\data\streamerbot"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\Hub\data\*"; DestDir: "{app}\Hub\data"; Excludes: "\overlay\*,\streamerbot\*"; Flags: recursesubdirs createallsubdirs onlyifdoesntexist uninsneveruninstall

; The AppData migration shim rides into {tmp} and is invoked from [Code]
; before payload writes start; dontcopy keeps it out of the install root.
Source: "migrate-appdata.ps1"; Flags: dontcopy

; The Windows App Runtime is NOT bundled as a separate redist. Hub.WinUI
; builds self-contained (<WindowsAppSDKSelfContained>true</...> in
; Phoenix.Controls.Hub.WinUI.csproj) so the Microsoft.WindowsAppRuntime.* /
; Microsoft.UI.Xaml.* DLLs ship inside the Hub\ folder of the staged payload
; and land under {app}\Hub\ verbatim. No framework/DDLM registration is
; needed on the user's machine — eliminates the entire "requires Windows
; App Runtime 1.5" failure mode that hit users with mixed/aging
; WindowsAppRuntime installs (the framework was registered but no matching
; DDLM survived Windows' aggressive GC pass).

[Icons]
; ONE shortcut. Phoenix Controls is a single app from the user's POV:
; Hub.WinUI is the entry point and spawns Architect / Visualist via its
; pillar tabs internally. Lives directly under {autoprograms} (no nested
; "Phoenix Controls" folder) so the Win11 Start search shows it as a
; single tile.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\Hub\{#MyAppExeName}"; WorkingDir: "{app}\Hub"

; Desktop — opt-in via the desktopHub task.
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\Hub\{#MyAppExeName}"; WorkingDir: "{app}\Hub"; Tasks: desktopHub

[Registry]
; .phx + .phxg file association — opens in Hub.WinUI, which is the single
; user-facing exe post-collapse. Hub does not yet parse `--open <path>`
; from argv (App.OnLaunched ignores command-line args today); the flag is
; passed defensively so a future Hub --open implementation deep-links to
; the Architect tab + loaded graph without an installer change. Until
; that lands, double-clicking a .phxg/.phx in Explorer simply launches
; Hub at its default state.
;
;   * .phxg — the editable graph (Architect's authoring file).
;   * .phx  — the generated runtime script (registered for symmetry; future
;             Hub builds may show a "this is a generated file, open the
;             matching .phxg?" prompt).
;
; Per-user (HKCU) so we don't need elevation. uninsdeletekey cleans up the
; ProgID + association entries on uninstall.
Root: HKCU; Subkey: "Software\Classes\.phx";  ValueType: string; ValueData: "PhoenixControls.PhxScript"; Flags: uninsdeletekey; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\.phxg"; ValueType: string; ValueData: "PhoenixControls.PhxgGraph";  Flags: uninsdeletekey; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxScript";                       ValueType: string; ValueData: "Phoenix Controls Script"; Flags: uninsdeletekey; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxScript\DefaultIcon";           ValueType: string; ValueData: "{app}\Hub\{#MyAppExeName},0"; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxScript\shell\open\command";    ValueType: string; ValueData: """{app}\Hub\{#MyAppExeName}"" --open ""%1"""; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxgGraph";                       ValueType: string; ValueData: "Phoenix Controls Graph"; Flags: uninsdeletekey; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxgGraph\DefaultIcon";           ValueType: string; ValueData: "{app}\Hub\{#MyAppExeName},0"; Tasks: registerPhx
Root: HKCU; Subkey: "Software\Classes\PhoenixControls.PhxgGraph\shell\open\command";    ValueType: string; ValueData: """{app}\Hub\{#MyAppExeName}"" --open ""%1"""; Tasks: registerPhx

; Launch Hub at sign-in — HKCU\Run, opt-in via the runOnSignin task. The
; uninstdeletevalue flag removes the entry on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PhoenixControls.Hub"; ValueData: """{app}\Hub\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: runOnSignin

[Run]
; Optional: launch Phoenix Controls at the end of installation.
Filename: "{app}\Hub\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: postinstall nowait skipifsilent unchecked

; ====================================================================
; [Code] — Pascal Script
; ====================================================================
[Code]

function RunPowerShellHelper(const ScriptName, Args: String): Integer;
var
  ResultCode: Integer;
  PSArgs: String;
begin
  PSArgs := '-NoProfile -ExecutionPolicy Bypass -File "' +
            ExpandConstant('{tmp}\') + ScriptName + '" ' + Args;
  if not Exec('powershell.exe', PSArgs, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := -1
  else
    Result := ResultCode;
end;

// ── .NET 8 Desktop Runtime detection ──────────────────────────────────
// The suite is framework-dependent (net8.0-windows / WinUI). On a
// streamer's PC without .NET 8 installed, double-clicking Hub.exe would
// fail with the .NET-missing dialog. Detect at install time and offer
// to open the download page.
//
// Detection strategy: check %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App
// for any 8.x.x subfolder. .NET enumerates installed runtimes via this
// directory layout, so a real install always leaves a folder here.
function IsDotNet8DesktopInstalled(): Boolean;
var
  FindRec: TFindRec;
  BaseDir: String;
begin
  Result := False;
  // {commonpf} expands to the architecture-correct Program Files folder.
  BaseDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BaseDir) then
    exit;
  if FindFirst(BaseDir + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Returns False to abort the install when the user wants to install
// .NET 8 first. The user chooses Yes (open MS download page + abort)
// or No (try to continue anyway — apps will fail at first launch with
// the standard .NET runtime-missing prompt).
function PromptInstallDotNet8(): Boolean;
var
  Choice: Integer;
  ErrCode: Integer;
begin
  Choice := MsgBox(
    'Phoenix Controls requires the .NET 8 Desktop Runtime, which is not installed on this PC.' #13#13 +
    'Click Yes to open Microsoft''s download page in your browser, then re-run this installer after installing the runtime.' #13#13 +
    'Click No to install Phoenix Controls anyway — the apps will not start until the runtime is installed.',
    mbConfirmation, MB_YESNO);
  if Choice = IDYES then
  begin
    ShellExec('open',
      'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
      '', '', SW_SHOW, ewNoWait, ErrCode);
    Result := False;  // Abort the install
  end
  else
  begin
    Result := True;   // Continue without the runtime
  end;
end;

// migrate-appdata.ps1 returns:
//   0 = success / no-op / -WhatIf
//   2 = collision (legacy + new both exist)   → install MUST stop
//   3 = IO blocker (cross-volume / locked file) → install continues
//                                                  with a warning
function RunMigrationShim(): Integer;
begin
  ExtractTemporaryFile('migrate-appdata.ps1');
  Result := RunPowerShellHelper('migrate-appdata.ps1', '');
end;

// PrepareToInstall fires after the user clicks Install on the Ready page,
// before any files are written. Returning a non-empty string aborts the
// install with that message; returning '' lets it proceed. We use this
// to gate on the .NET 8 Desktop Runtime — the user gets a chance to
// install it (or proceed at their own risk) before the setup commits.
//
// Note: the Windows App Runtime is NOT installed here. Hub.WinUI builds
// self-contained (<WindowsAppSDKSelfContained>true</…> in
// Phoenix.Controls.Hub.WinUI.csproj) so the runtime DLLs ride inside the
// Hub\ payload and need no machine-wide framework/DDLM registration.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  if not IsDotNet8DesktopInstalled() then
  begin
    if not PromptInstallDotNet8() then
    begin
      // User chose Yes (open download page); browser is now opening.
      // Returning a non-empty string aborts the install cleanly.
      Result := 'Install cancelled. Re-run this installer after the .NET 8 Desktop Runtime finishes installing.';
      exit;
    end;
    // else: user chose No — proceed without the runtime; Hub.exe will fail
    // to launch on first run with the standard .NET runtime-missing prompt.
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  MigrationCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Migrate any pre-rebrand AppData *before* writing new files. This
    // way Hub's first launch already sees its DB / dock-layout / etc.
    // at the new location.
    //
    // Strict policy: if the migrator reports a collision (exit
    // code 2 — legacy + new roots both present) we ABORT the install
    // rather than silently running on top of mixed state. The C#
    // AppDataMigrator at runtime is more permissive and merges, but at
    // install time we want the user to resolve it explicitly.
    MigrationCode := RunMigrationShim();
    if MigrationCode = 2 then
    begin
      MsgBox(
        'Phoenix Controls cannot install: a legacy "PhoenixSovereign" or ' +
        '"Phoenix.Sovereign" folder still exists alongside the current ' +
        '"PhoenixControls" folder under %APPDATA% or %LOCALAPPDATA%.' #13#13 +
        'Open the migrate-appdata.ps1 log for details, then either rename ' +
        'or merge the folders by hand and re-run this installer.',
        mbCriticalError, MB_OK);
      Abort;
    end;
    // exit 3 (cross-volume / IO blocker) is non-fatal: the legacy data is
    // left in place and the user can move it manually later. The C#
    // AppDataMigrator at runtime will pick it up if the user later moves
    // it onto the same volume as %APPDATA%.
  end;
end;
