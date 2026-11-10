<#
.SYNOPSIS
  One-time setup of a demo PC for the Local AI Showroom.

.DESCRIPTION
  Checks the PC, then installs with winget what the demo needs (safe to re-run):
    - .NET 10 SDK                       builds and runs the bridge
    - Windows App Development CLI       'winapp': package identity for 'dotnet run'
    - Windows App Runtime 2             runtime for Windows App SDK 2.x (Windows AI APIs + Windows ML)
    - Foundry Local                     open models behind an OpenAI-compatible endpoint
    - uv + winml-cli (optional)         builds and benchmarks the Silicon Lab model

.EXAMPLE
  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
  .\scripts\setup.ps1

.EXAMPLE
  .\scripts\setup.ps1 -SkipWinmlCli
#>
[CmdletBinding()]
param(
    [switch] $SkipFoundryLocal,
    [switch] $SkipWinmlCli,
    [string] $DataRoot = (Join-Path $env:USERPROFILE 'LocalAIShowroom')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    [ok] $Message" -ForegroundColor Green }
function Test-Command([string] $Name) { return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Install-WingetPackage([string] $Id, [string] $Name) {
    Write-Host "    Installing $Name ($Id) ..."
    winget install --id $Id --exact --silent --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "winget returned $LASTEXITCODE for $Id. It may already be installed, or winget needs an update."
    }
    Update-SessionPath
}

# ------------------------------------------------------------------------------------------------ PC checks
Write-Step 'Checking this PC'
$os = Get-CimInstance Win32_OperatingSystem
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
Write-Host "    Windows       : $($os.Caption) (build $($os.BuildNumber))"
Write-Host "    Processor     : $cpu"
Write-Host "    Architecture  : $env:PROCESSOR_ARCHITECTURE"
Write-Host "    Memory        : $ramGb GB"

if ([int]$os.BuildNumber -lt 26100) {
    Write-Warning 'Windows 11 24H2 (build 26100) or later is recommended. NPU execution providers and Windows AI APIs need it.'
}

$npu = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
    Where-Object { $_.PNPClass -eq 'ComputeAccelerator' -or ($_.Name -cmatch '\bNPU\b') -or ($_.Name -match 'Neural|AI Boost|Hexagon|Ryzen AI') } |
    Select-Object -ExpandProperty Name -Unique
if ($npu) { Write-Ok "NPU found: $($npu -join ', ')" }
else { Write-Warning 'No NPU detected. Foundry Local and Windows ML still run on GPU/CPU; Windows AI APIs need a Copilot+ PC.' }

$devMode = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($devMode -eq 1) {
    Write-Ok 'Developer Mode is on'
}
else {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        # Never use New-Item -Force here: on an existing registry key it recreates the key and drops its other values.
        $unlockKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
        if (-not (Test-Path $unlockKey)) { New-Item -Path $unlockKey | Out-Null }
        Set-ItemProperty -Path $unlockKey -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
        Write-Ok 'Developer Mode turned on'
    }
    else {
        Write-Warning 'Developer Mode is OFF. Turn it on in Settings > System > For developers (needed for package identity), or re-run this script as administrator.'
    }
}

if (-not (Test-Command 'winget')) { throw 'winget is required. Install "App Installer" from the Microsoft Store, then re-run.' }

# ------------------------------------------------------------------------------------------------ .NET 10 SDK
Write-Step '.NET 10 SDK'
$hasNet10 = (Test-Command 'dotnet') -and (@(dotnet --list-sdks 2>$null) -match '^10\.').Count -gt 0
if ($hasNet10) { Write-Ok '.NET 10 SDK found' } else { Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK' }

# ------------------------------------------------------------------------------------------------ winapp CLI
Write-Step 'Windows App Development CLI (winapp)'
if (Test-Command 'winapp') { Write-Ok 'winapp found' } else { Install-WingetPackage 'Microsoft.winappcli' 'winapp CLI' }

# ------------------------------------------------------------------------------------------------ Windows App Runtime 2
Write-Step 'Windows App Runtime 2 (Windows App SDK 2.x)'
$war2 = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue
if ($war2) { Write-Ok "Windows App Runtime $(@($war2)[0].Version) found" }
else { Install-WingetPackage 'Microsoft.WindowsAppRuntime.2.0' 'Windows App Runtime 2' }

# ------------------------------------------------------------------------------------------------ Foundry Local
if (-not $SkipFoundryLocal) {
    Write-Step 'Foundry Local'
    if (Test-Command 'foundry') { Write-Ok 'foundry CLI found' } else { Install-WingetPackage 'Microsoft.FoundryLocal' 'Foundry Local' }
}

# ------------------------------------------------------------------------------------------------ winml-cli
$localSettings = [ordered]@{
    WindowsML = [ordered]@{ ModelPath = (Join-Path $DataRoot 'models\resnet50') }
}

if (-not $SkipWinmlCli) {
    Write-Step 'winml-cli (Python 3.11 x64 + uv)'
    if (-not (Test-Command 'uv')) { Install-WingetPackage 'astral-sh.uv' 'uv' }

    if (Test-Command 'uv') {
        # winml-cli publishes x64 wheels only: on Arm64 use an x64 Python (runs under emulation).
        $python = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'cpython-3.11-windows-x86_64-none' } else { '3.11' }
        $cliDir = Join-Path $DataRoot 'winml-cli'
        $venvPython = Join-Path $cliDir '.venv\Scripts\python.exe'
        New-Item -ItemType Directory -Path $cliDir -Force | Out-Null

        uv python install $python
        if (-not (Test-Path $venvPython)) { uv venv --python $python (Join-Path $cliDir '.venv') }
        uv pip install --python $venvPython --upgrade winml-cli

        $winml = Join-Path $cliDir '.venv\Scripts\winml.exe'
        if (Test-Path $winml) {
            Write-Ok "winml-cli installed: $winml"
            $localSettings['WinmlCli'] = [ordered]@{ Command = $winml }
            Write-Host '    First run of winml-cli (answers its one-time telemetry question and lists devices/EPs):'
            & $winml sys --list-device --list-ep
        }
        else {
            Write-Warning 'winml-cli did not install. See https://github.com/microsoft/winml-cli for manual steps.'
        }
    }
}

# ------------------------------------------------------------------------------------------------ bridge settings
$settingsPath = Join-Path $repoRoot 'bridge\appsettings.local.json'
$localSettings | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Ok "Wrote $settingsPath"

Write-Step 'Done'
Write-Host '    Next:'
Write-Host '      1. .\scripts\prepare-models.ps1     (download Foundry Local models, build the ResNet-50 model)'
Write-Host '      2. .\scripts\start-demo.ps1         (starts Foundry Local + the bridge and opens the browser)'
Write-Host '    Rehearse without hardware: .\scripts\start-demo.ps1 -Simulate'
