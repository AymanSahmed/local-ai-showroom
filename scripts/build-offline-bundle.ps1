<#
.SYNOPSIS
  Builds a self-contained OFFLINE bundle of the Local AI Showroom (run this once on a PC with internet).

.DESCRIPTION
  The bundle folder can be copied (USB / file share) to a PC with no internet. On that PC nothing is restored,
  compiled or downloaded: no NuGet, no .NET SDK, no winget.

    app\         self-contained bridge (.NET runtime included) + web UI + package-identity manifest
    prereqs\     Windows App Runtime 2.x installers (the exact version the bridge was built against)
    models\      (optional) Foundry Local model cache and the Silicon Lab ONNX model
    openvino-sideload\   (optional, -IncludeOpenVinoSideload) runs OpenVINO NPU/GPU models that Foundry Local can't load
    start-offline.ps1   registers the package, starts Foundry Local and the bridge

  Models are the only large downloads. Download them on this PC first (scripts\prepare-models.ps1 or the
  'Browse model catalog' button), then use -IncludeFoundryModels so they travel with the bundle.

.EXAMPLE
  .\scripts\build-offline-bundle.ps1
  .\scripts\build-offline-bundle.ps1 -IncludeFoundryModels -IncludeFoundryInstaller -Zip
  .\scripts\build-offline-bundle.ps1 -IncludeFoundryModels -IncludeOpenVinoSideload   # Intel NPU/GPU laptops
  .\scripts\build-offline-bundle.ps1 -Architecture arm64     # for a Snapdragon X target built on any PC
#>
[CmdletBinding()]
param(
    [ValidateSet('auto', 'x64', 'arm64')] [string] $Architecture = 'auto',
    [string] $OutputDir,
    [switch] $IncludeFoundryModels,
    [switch] $IncludeFoundryInstaller,
    [switch] $IncludeOpenVinoSideload,
    [string] $OpenVinoSideloadRoot = (Join-Path $env:USERPROFILE 'LocalAIShowroom\openvino-sideload'),
    [switch] $SkipResNet,
    [switch] $Zip,
    [string] $DataRoot = (Join-Path $env:USERPROFILE 'LocalAIShowroom')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$bridge = Join-Path $repoRoot 'bridge'
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'dist\LocalAIShowroom-offline' }

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Copy-Folder([string] $From, [string] $To) {
    New-Item -ItemType Directory -Path $To -Force | Out-Null
    robocopy $From $To /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $From -> $To" }
    $global:LASTEXITCODE = 0
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is needed on this (build) PC. Run scripts\setup.ps1 first.' }

if ($Architecture -eq 'auto') {
    $native = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $Architecture = if ($native -eq 'ARM64') { 'arm64' } else { 'x64' }
}
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }

# Refuse to wipe anything that does not look like a previous bundle.
if (Test-Path $OutputDir) {
    if (-not (Test-Path (Join-Path $OutputDir 'start-offline.ps1'))) { throw "$OutputDir exists and is not an offline bundle. Choose another -OutputDir." }
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null
$app = Join-Path $OutputDir 'app'

# ------------------------------------------------------------------------------------------------ publish
Write-Step "Publishing the bridge (self-contained, win-$Architecture)"
dotnet publish (Join-Path $bridge 'LocalAIShowroom.Bridge.csproj') -c Release -r "win-$Architecture" --self-contained true "-p:Platform=$platform" -o $app --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Settings and package assets are not part of the publish output.
Copy-Item (Join-Path $bridge 'appsettings.json') $app -Force
Copy-Folder (Join-Path $bridge 'Assets') (Join-Path $app 'Assets')
Get-ChildItem $app -Filter '*.pdb' | Remove-Item -Force

# ------------------------------------------------------------------------------------------------ identity manifest
Write-Step 'Writing the package-identity manifest'
$assets = Get-Content (Join-Path $bridge 'obj\project.assets.json') -Raw | ConvertFrom-Json
$sdk = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -match '^Microsoft\.WindowsAppSDK/(\d+\.\d+\.\d+)' } | Select-Object -First 1
if (-not $sdk) { throw 'Could not determine the Windows App SDK version from obj\project.assets.json. Build the bridge once first (dotnet build).' }
$sdkVersion = ($sdk -split '/')[1]
$runtimeMin = "$sdkVersion.0"
Write-Host "    Windows App SDK $sdkVersion -> requires Windows App Runtime >= $runtimeMin"

$manifest = Get-Content (Join-Path $bridge 'offline\AppxManifest.template.xml') -Raw
$manifest = $manifest.Replace('{{ARCH}}', $Architecture).Replace('{{RUNTIME_MIN}}', $runtimeMin).Replace('{{VERSION}}', "$((Get-Content (Join-Path $repoRoot 'VERSION') -Raw).Trim()).0")
Set-Content -Path (Join-Path $app 'AppxManifest.xml') -Value $manifest -Encoding UTF8

# ------------------------------------------------------------------------------------------------ prerequisites
Write-Step 'Bundling the Windows App Runtime installers'
$runtimePackage = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windowsappsdk.runtime\$sdkVersion\tools\MSIX\win10-$Architecture"
if (-not (Test-Path $runtimePackage)) { throw "Windows App Runtime packages not found at $runtimePackage. Run a normal build once (dotnet build) so NuGet restores them." }
Copy-Folder $runtimePackage (Join-Path $OutputDir 'prereqs\WindowsAppRuntime')

if ($IncludeFoundryInstaller) {
    Write-Step 'Downloading the Foundry Local installer (needs internet, this PC only)'
    $foundryDir = Join-Path $OutputDir 'prereqs\FoundryLocal'
    New-Item -ItemType Directory -Path $foundryDir -Force | Out-Null
    winget download --id Microsoft.FoundryLocal --exact --download-directory $foundryDir --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { Write-Warning 'winget download failed. Copy the Foundry Local .msix into prereqs\FoundryLocal manually.' }
}

# ------------------------------------------------------------------------------------------------ models
$resnet = Join-Path $DataRoot 'models\resnet50'
if (-not $SkipResNet -and (Test-Path $resnet)) {
    Write-Step 'Bundling the Silicon Lab ONNX model'
    Copy-Folder $resnet (Join-Path $OutputDir 'models\resnet50')
}
elseif (-not $SkipResNet) {
    Write-Warning "No Silicon Lab model at $resnet (run scripts\prepare-models.ps1). Silicon Lab will show 'No model' offline."
}

if ($IncludeOpenVinoSideload) {
    if ($Architecture -ne 'x64') { Write-Warning 'The OpenVINO sideload is Intel x64 only; skipping it for this architecture.' }
    else {
        Write-Step 'Bundling the OpenVINO sideload (Python + OpenVINO provider, about 400 MB)'
        if (-not (Test-Path (Join-Path $OpenVinoSideloadRoot 'python\python.exe'))) {
            Write-Host "    Not installed at $OpenVinoSideloadRoot; setting it up now (needs internet, this PC only)"
            & (Join-Path $PSScriptRoot 'setup-openvino-sideload.ps1') -Root $OpenVinoSideloadRoot
        }
        if (-not (Test-Path (Join-Path $OpenVinoSideloadRoot 'python\python.exe'))) { throw "The OpenVINO sideload setup did not produce $OpenVinoSideloadRoot." }
        Copy-Folder $OpenVinoSideloadRoot (Join-Path $OutputDir 'openvino-sideload')
        Get-ChildItem (Join-Path $OutputDir 'openvino-sideload') -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($IncludeFoundryModels) {
    Write-Step 'Bundling the Foundry Local model cache (can be many GB)'
    $cacheOutput = (foundry cache location 2>&1 | Out-String)
    $cache = [regex]::Match($cacheOutput, '[A-Za-z]:\\[^\r\n]+').Value.Trim()
    if (-not $cache -or -not (Test-Path $cache)) { throw "Could not find the Foundry Local cache folder (foundry cache location said: $cacheOutput)" }
    Write-Host "    $cache"
    Copy-Folder $cache (Join-Path $OutputDir 'models\foundry')
}

# ------------------------------------------------------------------------------------------------ launcher + docs
Copy-Item (Join-Path $PSScriptRoot 'start-offline.ps1') (Join-Path $OutputDir 'start-offline.ps1')
Copy-Item (Join-Path $PSScriptRoot 'diagnose.ps1') (Join-Path $OutputDir 'diagnose.ps1')
Copy-Item (Join-Path $repoRoot 'VERSION') (Join-Path $OutputDir 'VERSION')
Copy-Item (Join-Path $PSScriptRoot 'offline-readme.txt') (Join-Path $OutputDir 'README-OFFLINE.txt')

$sizeMb = [math]::Round((Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Step "Bundle ready: $OutputDir ($sizeMb MB, win-$Architecture)"

if ($Zip) {
    $zipPath = "$OutputDir.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $zipPath
    Write-Host "    Zip: $zipPath"
}

Write-Host '    Copy the folder to the offline PC, then run: .\start-offline.ps1'
