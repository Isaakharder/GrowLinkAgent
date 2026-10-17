# Installer tests on Windows: upgrade from the original 1.0.1 install, preservation of
# settings / encrypted keys / folders, single instance, rollback, uninstall, and the
# safety stops (system-wide install, agent running elsewhere).
#   ./tools/installer-test.ps1 -New <1.1.0 setup> -Rollback <1.0.1 setup> -Original <original 1.0.1 setup> -Out <dir>
param(
    [Parameter(Mandatory)] [string] $New,
    [Parameter(Mandatory)] [string] $Rollback,
    [Parameter(Mandatory)] [string] $Original,
    [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$New = (Resolve-Path $New).Path; $Rollback = (Resolve-Path $Rollback).Path; $Original = (Resolve-Path $Original).Path
$results = [System.Collections.Generic.List[string]]::new()
function Check([bool] $ok, [string] $what) {
    $line = ($(if ($ok) { 'PASS' } else { 'FAIL' })) + ": $what"
    $results.Add($line); Write-Host $line
    if (-not $ok) { $results | Set-Content "$Out/installer-test-summary.txt"; throw $line }
}
function Setup([string] $exe, [string] $name, [string[]] $extra = @()) {
    $log = Join-Path (Resolve-Path $Out) "$name.log"
    $p = Start-Process $exe -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $extra) -Wait -PassThru
    return @{ Code = $p.ExitCode; Log = (Get-Content $log -Raw) }
}
function Agents { @(Get-CimInstance Win32_Process -Filter "Name = 'GrowLinkAgent.exe'") }
function WaitFor([scriptblock] $cond, [int] $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 500 }
    return [bool](& $cond)
}
function Hash([string] $path) { (Get-FileHash $path -Algorithm SHA256).Hash }
function FolderState([string] $dir) {
    (Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($dir.Length))|$(Hash $_.FullName)" }) -join "`n"
}

$legacyDir = Join-Path $env:LOCALAPPDATA 'GrowLinkAgent'          # where 1.0.x installed
$dataDir = Join-Path $env:APPDATA 'GrowLinkAgent'                  # settings + encrypted keys
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'GrowLinkAgent.lnk'
$hkcuKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C7A4F8B2-3E5D-4A6C-9B1E-0F2D3C4E5B6A}_is1'
$hklmKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C7A4F8B2-3E5D-4A6C-9B1E-0F2D3C4E5B6A}_is1'
$root = 'C:\AgentTest'
$synopta = Join-Path $root 'Synopta Exports'
$flow = Join-Path $root 'FlowMaster'

# ── An existing installation, as on the climate PC ───────────────────────────
Get-Process GrowLinkAgent -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $dataDir, $legacyDir, $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dataDir, "$synopta\Uploaded\2026-10", "$synopta\Failed", $flow | Out-Null
# Existing results in the watch folder that nobody may move or change.
Copy-Item 'GrowLinkAgent.Tests/SampleFiles/Block Summary_20260717_080003.csv' "$synopta\Uploaded\2026-10\"
Copy-Item 'GrowLinkAgent.Tests/SampleFiles/Block Summary_20260717_090003.csv' "$synopta\Failed\"
# Settings: Synopta → CropLink (a placeholder address that doesn't resolve), FlowMaster → GrowLink.
@{
    ServerUrl = ''; WatchFolder = $flow; BlockSummaryFolder = $synopta; UseRouteKeys = $true
    RouteConfigs = @(
        @{ RouteId = 'block-summary'; ServerUrl = 'https://croplink.example.invalid'; Enabled = $true },
        @{ RouteId = 'flowmaster-packline'; ServerUrl = 'https://growlink.example.invalid'; Enabled = $true })
} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dataDir 'config.json') -Encoding utf8
# Encrypted keys exactly as the agent stores them (DPAPI, current user).
Add-Type -AssemblyName System.Security
$json = '{"block-summary":"croplink-test-key-123","flowmaster":"flowmaster-test-key-456"}'
$cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($json), $null, 'CurrentUser')
[IO.File]::WriteAllBytes((Join-Path $dataDir 'secure-routes.dat'), $cipher)
$before = @{ config = Hash "$dataDir\config.json"; keys = Hash "$dataDir\secure-routes.dat"; folders = FolderState $root }

$r = Setup $Original 'install-original-1.0.1' @("/DIR=`"$legacyDir`"", '/TASKS=startup')
Check ($r.Code -eq 0) 'original 1.0.1 installer: installs'
Check (Test-Path "$legacyDir\GrowLinkAgent.exe") 'original 1.0.1: in %LOCALAPPDATA%\GrowLinkAgent'
Check (Test-Path $startup) 'original 1.0.1: Startup shortcut'
Start-Process "$legacyDir\GrowLinkAgent.exe"
Check (WaitFor { (Agents).Count -eq 1 }) '1.0.1 agent running'
Start-Sleep 5

# ── Upgrade to 1.1.0 while 1.0.1 runs ────────────────────────────────────────
$r = Setup $New 'upgrade-to-1.1.0'
Check ($r.Code -eq 0) "1.1.0 upgrade: exit code 0 (got $($r.Code))"
Check ($r.Log -match 'Install mode root key: HKEY_CURRENT_USER' -and $r.Log -match 'Administrative install mode: No') '1.1.0 upgrade: per-user install mode, not administrative (setup log)'
Check (-not (Test-Path $hklmKey)) '1.1.0 upgrade: nothing registered for all users (HKLM)'
$ver = (Get-Item "$legacyDir\GrowLinkAgent.exe").VersionInfo.ProductVersion
Check ($ver -like '1.1.0*') "1.1.0 upgrade: same folder %LOCALAPPDATA%\GrowLinkAgent now has $ver"
Check (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\GrowLinkAgent'))) '1.1.0 upgrade: no second copy in %LOCALAPPDATA%\Programs'
Check ((Get-ItemProperty $hkcuKey).DisplayVersion -eq '1.1.0') '1.1.0 upgrade: Apps & Features shows 1.1.0 (per user)'
Check (Test-Path $startup) '1.1.0 upgrade: Startup shortcut kept'
Check ((Hash "$dataDir\config.json") -eq $before.config) '1.1.0 upgrade: config.json unchanged'
Check ((Hash "$dataDir\secure-routes.dat") -eq $before.keys) '1.1.0 upgrade: encrypted keys unchanged'
Check ((FolderState $root) -eq $before.folders) '1.1.0 upgrade: watch folders, Uploaded and Failed unchanged'
Check ((Get-Content "$dataDir\config.json" -Raw) -match 'croplink\.example\.invalid') '1.1.0 upgrade: Synopta still points at CropLink'
Check (WaitFor { $a = Agents; $a.Count -eq 1 -and $a[0].ExecutablePath -eq "$legacyDir\GrowLinkAgent.exe" }) '1.1.0 upgrade: exactly one agent running again (the new one)'

# ── One instance only ────────────────────────────────────────────────────────
Start-Process "$legacyDir\GrowLinkAgent.exe"; Start-Process "$legacyDir\GrowLinkAgent.exe"
Start-Sleep 6
Check ((Agents).Count -eq 1) 'single instance: launching twice more still leaves one agent'

# ── Keys decrypted by 1.1.0, retry keeps files (server unreachable) ──────────
Copy-Item 'GrowLinkAgent.Tests/SampleFiles/Block Summary_20260717_100005.csv' "$synopta\"
$logFile = Join-Path $dataDir ("logs\" + (Get-Date -Format 'yyyy-MM-dd') + '.log')
Check (WaitFor { (Test-Path $logFile) -and ((Get-Content $logFile -Raw) -match 'Kept in the folder') } 90) '1.1.0: unreachable server → file kept in the folder for retry'
$agentLog = Get-Content $logFile -Raw
Check ($agentLog -notmatch "has no configured key in slot 'block-summary'") '1.1.0: decrypted the existing Synopta key (DPAPI)'
Check ($agentLog -match 'Climate upload to CropLink \(legacy\)') '1.1.0: destination identified as CropLink (legacy)'
Check (Test-Path "$synopta\Block Summary_20260717_100005.csv") '1.1.0: pending file still in the folder (not Failed)'
Check (-not (Test-Path "$synopta\Failed\Block Summary_20260717_100005.csv")) '1.1.0: nothing moved to Failed'
Copy-Item $logFile "$Out/agent-1.1.0.log"

# Screenshot of the running agent (best effort: needs an interactive desktop).
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    [Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [Drawing.Point]::Empty, $b.Size)
    $bmp.Save("$Out/agent-1.1.0-screen.png")
} catch { Write-Host "screenshot skipped: $_" }

# ── Safety stop: agent running from another folder ───────────────────────────
New-Item -ItemType Directory -Force "$root\Other" | Out-Null
Get-Process GrowLinkAgent | Stop-Process -Force; Start-Sleep 2
Copy-Item "$legacyDir\GrowLinkAgent.exe" "$root\Other\"
Start-Process "$root\Other\GrowLinkAgent.exe"
Check (WaitFor { (Agents).Count -eq 1 }) 'a copy running from another folder'
$r = Setup $New 'refused-running-elsewhere'
Check ($r.Code -ne 0 -and $r.Log -match 'running from another location') "setup refuses while an agent runs from elsewhere (exit $($r.Code))"
Check ((Agents)[0].ExecutablePath -eq "$root\Other\GrowLinkAgent.exe") '… and left that agent alone'
Get-Process GrowLinkAgent | Stop-Process -Force; Start-Sleep 2

# ── Safety stop: system-wide installation present ────────────────────────────
New-Item -Path $hklmKey -Force | Out-Null
New-ItemProperty -Path $hklmKey -Name InstallLocation -Value 'C:\Program Files\GrowLinkAgent' -Force | Out-Null
try {
    $r = Setup $New 'refused-system-wide'
    Check ($r.Code -ne 0 -and $r.Log -match 'installed for all users') "setup refuses when a system-wide install exists (exit $($r.Code))"
    Check ((Get-Item "$legacyDir\GrowLinkAgent.exe").VersionInfo.ProductVersion -like '1.1.0*') '… and changed nothing'
} finally { Remove-Item $hklmKey -Force }

# ── Rollback to 1.0.1, then forward again ────────────────────────────────────
$r = Setup $Rollback 'rollback-to-1.0.1'
Check ($r.Code -eq 0) 'rollback installer: exit code 0'
Check ((Get-Item "$legacyDir\GrowLinkAgent.exe").VersionInfo.ProductVersion -notlike '1.1.0*') 'rollback: 1.0.1 back in the same folder'
Check ((Hash "$dataDir\config.json") -eq $before.config -and (Hash "$dataDir\secure-routes.dat") -eq $before.keys) 'rollback: settings and encrypted keys unchanged'
Check ((Get-ChildItem "$synopta\Uploaded" -Recurse -File).Count -ge 1 -and (Test-Path "$synopta\Failed\Block Summary_20260717_090003.csv")) 'rollback: Uploaded/Failed files still there'
Get-Process GrowLinkAgent -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2
$r = Setup $New 'upgrade-again-to-1.1.0'
Check ($r.Code -eq 0 -and (Get-Item "$legacyDir\GrowLinkAgent.exe").VersionInfo.ProductVersion -like '1.1.0*') 'upgrade again after rollback'

# ── Uninstall keeps settings and files ───────────────────────────────────────
$uninst = (Get-ItemProperty $hkcuKey).UninstallString.Trim('"')
Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
Start-Sleep 3
Check (-not (Test-Path "$legacyDir\GrowLinkAgent.exe")) 'uninstall: program removed'
Check (-not (Test-Path $startup)) 'uninstall: Startup shortcut removed'
Check ((Agents).Count -eq 0) 'uninstall: agent stopped'
Check ((Hash "$dataDir\config.json") -eq $before.config -and (Hash "$dataDir\secure-routes.dat") -eq $before.keys) 'uninstall: settings and encrypted keys kept'
Check ((Get-ChildItem "$synopta\Uploaded" -Recurse -File).Count -ge 1 -and (Test-Path "$synopta\Failed\Block Summary_20260717_090003.csv")) 'uninstall: watch folders untouched'

# ── Fresh install goes to %LOCALAPPDATA%\Programs ────────────────────────────
$r = Setup $New 'fresh-install-1.1.0' @('/TASKS=startup')
Check ($r.Code -eq 0 -and (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\GrowLinkAgent\GrowLinkAgent.exe'))) 'fresh install: %LOCALAPPDATA%\Programs\GrowLinkAgent'
Get-Process GrowLinkAgent -ErrorAction SilentlyContinue | Stop-Process -Force

$results | Set-Content "$Out/installer-test-summary.txt"
Write-Host "`nALL INSTALLER TESTS PASSED ($($results.Count) checks)"
