<#
.SYNOPSIS
  Starts the Local AI Showroom from an offline bundle. No internet, NuGet, .NET SDK or winget needed.

.DESCRIPTION
  1. Checks Developer Mode (needed to register the app's package identity).
  2. Installs the bundled Windows App Runtime if this PC does not have it yet (-InstallPrerequisites).
  3. Registers the package from .\app (Add-AppxPackage -Register) the first time or after the folder moved.
  4. Starts Foundry Local (if installed) and the bridge, then opens http://127.0.0.1:<port>.

.EXAMPLE
  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
  .\start-offline.ps1 -InstallPrerequisites     # first run on a new PC
  .\start-offline.ps1                           # every other time
#>
[CmdletBinding()]
param(
    [int] $Port = 5080,
    [int] $FoundryPort = 39839,
    [switch] $InstallPrerequisites,
    [switch] $SkipFoundry,
    [switch] $NoBrowser,
    [switch] $LocalCopy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$app = Join-Path $root 'app'
$packageName = 'LocalAIShowroom.Bridge.Offline'
$alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\LocalAIShowroomBridge.exe'
$localApp = Join-Path $env:LOCALAPPDATA 'LocalAIShowroom\offline-app'

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    [ok] $Message" -ForegroundColor Green }

if (-not (Test-Path (Join-Path $app 'AppxManifest.xml'))) { throw "Missing .\app\AppxManifest.xml. Run this script from inside the offline bundle folder." }

# ------------------------------------------------------------------------------------------------ Developer Mode
Write-Step 'Checking Developer Mode'
$devMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -ne 1) { throw 'Developer Mode is off. Turn it on in Settings > System > For developers, then run this script again.' }
Write-Ok 'Developer Mode is on'

# ------------------------------------------------------------------------------------------------ Windows App Runtime
Write-Step 'Checking the Windows App Runtime'
$manifestXml = [xml](Get-Content (Join-Path $app 'AppxManifest.xml') -Raw)
$required = [version]$manifestXml.Package.Dependencies.PackageDependency.MinVersion
$installed = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue | Where-Object { [version]$_.Version -ge $required } | Select-Object -First 1
if ($installed) {
    Write-Ok "Windows App Runtime $($installed.Version) found"
}
else {
    $runtimeDir = Join-Path $root 'prereqs\WindowsAppRuntime'
    if (-not $InstallPrerequisites) {
        throw "Windows App Runtime $required or newer is not installed. Run: .\start-offline.ps1 -InstallPrerequisites (uses the installers in $runtimeDir)"
    }
    # Framework first; the Main, Singleton and DDLM packages depend on it.
    foreach ($name in 'Microsoft.WindowsAppRuntime.2.msix', 'Microsoft.WindowsAppRuntime.Main.2.msix', 'Microsoft.WindowsAppRuntime.Singleton.2.msix', 'Microsoft.WindowsAppRuntime.DDLM.2.msix') {
        $file = Join-Path $runtimeDir $name
        if (-not (Test-Path $file)) { throw "Missing $file" }
        Write-Host "    Installing $name ..."
        Add-AppxPackage -Path $file -ErrorAction Stop
    }
    Write-Ok 'Windows App Runtime installed'
}

# ------------------------------------------------------------------------------------------------ register package identity
Write-Step 'Registering the app (package identity)'
# A package registered from a OneDrive / cloud-synced folder can fail to start ("The process has no package identity"):
# its files are placeholders that Windows can't open as a package. Such folders run from a local copy instead.
function Test-SyncedFolder([string] $Path) {
    foreach ($v in $env:OneDrive, $env:OneDriveCommercial, $env:OneDriveConsumer) {
        if ($v -and $Path.StartsWith($v.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    if ($Path -match '\\OneDrive[^\\]*\\') { return $true }
    $manifest = Get-Item -LiteralPath (Join-Path $Path 'AppxManifest.xml')
    return [bool]($manifest.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Copy-AppLocally {
    Write-Host "    Copying the app to a local folder (not synced): $localApp"
    New-Item -ItemType Directory -Path $localApp -Force | Out-Null
    robocopy $app $localApp /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the app to $localApp failed (robocopy $LASTEXITCODE). Close any running copy of the bridge and try again." }
    $global:LASTEXITCODE = 0
    return $localApp
}

function Register-App([string] $Dir) {
    $registered = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Select-Object -First 1
    $dirPath = (Resolve-Path -LiteralPath $Dir).Path
    $sameLocation = $registered -and $registered.InstallLocation -and (Test-Path -LiteralPath $registered.InstallLocation) -and
        ((Resolve-Path -LiteralPath $registered.InstallLocation).Path -eq $dirPath)
    $sameVersion = $registered -and ([version]$registered.Version -eq [version]$manifestXml.Package.Identity.Version)
    if ($sameLocation -and $sameVersion -and (Test-Path $alias)) {
        Write-Ok 'Already registered'
        return
    }
    if ($registered) {
        # A different folder, version or a stale registration: replace it (removing a loose registration never deletes files).
        Write-Host '    Replacing the previous registration ...'
        $registered | Remove-AppxPackage -ErrorAction SilentlyContinue
    }
    Add-AppxPackage -Register (Join-Path $Dir 'AppxManifest.xml') -ErrorAction Stop
    Write-Ok "Registered from $Dir"
    if (-not (Test-Path $alias)) { throw "The app alias was not created at $alias. Sign out and in, or re-run this script." }
}

$runFrom = $app
$localReason = if ($LocalCopy) { 'requested with -LocalCopy' } elseif (Test-SyncedFolder $app) { 'this folder is synced by OneDrive' } else { $null }
if ($localReason) {
    Write-Host "    Using a local copy of the app ($localReason)."
    $runFrom = Copy-AppLocally
}
Register-App $runFrom
# ------------------------------------------------------------------------------------------------ Foundry Local
$foundry = Get-Command foundry -ErrorAction SilentlyContinue
if ($SkipFoundry) {
    Write-Step 'Foundry Local skipped'
}
elseif ($foundry) {
    Write-Step 'Starting Foundry Local'
    Write-Host "    CLI: $($foundry.Source)  version: $((foundry --version 2>&1 | Out-String).Trim())"
    $bundledModels = Join-Path $root 'models\foundry'
    $restart = $false
    if (Test-Path $bundledModels) {
        $current = [regex]::Match((foundry cache location 2>&1 | Out-String), '[A-Za-z]:\\[^\r\n]+').Value.Trim()
        if ($current -ne $bundledModels) {
            Write-Host "    Switching Foundry Local's model cache to the bundled models: $bundledModels (was: $current)"
            foundry cache cd $bundledModels | Out-Host
            $restart = $true   # a running daemon keeps using the old cache until it restarts
        }
    }
    if ($restart) { foundry server restart --port $FoundryPort --idle-timeout 0 | Out-Host }
    else { foundry server start --port $FoundryPort --idle-timeout 0 | Out-Host }
    Write-Host '    Models available to Foundry Local on this PC:'
    foundry model list --cached --variants 2>&1 | Out-Host
}
else {
    $installer = Get-ChildItem (Join-Path $root 'prereqs\FoundryLocal') -Filter '*.msix*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($installer) { Write-Warning "Foundry Local is not installed. Install it from $($installer.FullName) (Add-AppxPackage) and run this script again." }
    else { Write-Warning 'Foundry Local is not installed; its lanes will show as unavailable.' }
}

# ------------------------------------------------------------------------------------------------ start the bridge
function Test-PortFree([int] $p) { -not (Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue) }
if (-not (Test-PortFree $Port)) {
    $ownerPid = (Get-NetTCPConnection -State Listen -LocalPort $Port | Select-Object -First 1).OwningProcess
    $ownerName = (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue).ProcessName
    $free = ($Port + 1)..($Port + 20) | Where-Object { Test-PortFree $_ } | Select-Object -First 1
    if (-not $free) { throw "Port $Port is in use by $ownerName (process $ownerPid) and no free port was found nearby. Stop it or use -Port." }
    Write-Warning "Port $Port is already in use by $ownerName (process $ownerPid), probably another copy of the showroom. Using port $free instead."
    $Port = $free
}
Write-Step "Starting the bridge on http://127.0.0.1:$Port (Ctrl+C to stop)"
$bridgeArgs = @("--Showroom:Url=http://127.0.0.1:$Port")
if ($NoBrowser) { $bridgeArgs += '--Showroom:OpenBrowser=false' }
$resnet = Join-Path $root 'models\resnet50'
if (Test-Path $resnet) { $bridgeArgs += "--WindowsML:ModelPath=$resnet" }
$sideload = Join-Path $root 'openvino-sideload'
if (Test-Path (Join-Path $sideload 'python\python.exe')) {
    Write-Host "    OpenVINO sideload found: $sideload"
    $bridgeArgs += "--OpenVinoSideload:Root=$sideload"
    $bundledFoundryModels = Join-Path $root 'models\foundry'
    if (Test-Path $bundledFoundryModels) { $bridgeArgs += "--OpenVinoSideload:ModelRoots=$bundledFoundryModels" }
}
try {
    & $alias @bridgeArgs
}
catch {
    if ($runFrom -ne $app -or $_.Exception.Message -notmatch 'package identity') { throw }
    # Registered from the bundle folder but Windows still started it without identity: retry from a local copy.
    Write-Warning "The app started without package identity ($($_.Exception.Message.Trim())). Retrying from a local copy."
    $runFrom = Copy-AppLocally
    Register-App $runFrom
    & $alias @bridgeArgs
}

