<#
.SYNOPSIS
  Starts the Local AI Showroom: Foundry Local + the bridge (with package identity), then opens the browser.

.EXAMPLE
  .\scripts\start-demo.ps1                 # normal demo on a Copilot+ PC
  .\scripts\start-demo.ps1 -Aion           # also build the Aion Instruct Preview lane
  .\scripts\start-demo.ps1 -NoIdentity     # no package identity: Foundry Local + Windows ML only
  .\scripts\start-demo.ps1 -Simulate       # rehearsal mode with canned answers (needs Node.js only)
  .\scripts\start-demo.ps1 -NoRestore      # skip NuGet restore (packages already cached; no internet needed)
#>
[CmdletBinding()]
param(
    [int] $Port = 5080,
    [int] $FoundryPort = 39839,
    [switch] $Aion,
    [switch] $NoIdentity,
    [switch] $NoRestore,
    [switch] $Simulate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Simulate) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Simulation mode needs Node.js 20+ (winget install OpenJS.NodeJS.LTS).' }
    Start-Job -ScriptBlock { param($p) Start-Sleep -Seconds 2; Start-Process "http://127.0.0.1:$p" } -ArgumentList $Port | Out-Null
    node (Join-Path $repoRoot 'mock\mock-bridge.mjs') $Port
    return
}

if (Get-Command foundry -ErrorAction SilentlyContinue) {
    Write-Host "Starting Foundry Local on port $FoundryPort (no-op if it is already running) ..." -ForegroundColor Cyan
    foundry server start --port $FoundryPort --idle-timeout 0 | Out-Host
}
else {
    Write-Warning 'Foundry Local is not installed; its lanes will show as unavailable.'
}

$buildArgs = @()
if ($Aion) { $buildArgs += '-p:EnableAion=true' }
if ($NoIdentity) { $buildArgs += '-p:EnableWinAppRunSupport=false' }
if ($NoRestore) { $buildArgs += '--no-restore' }

Write-Host 'Building and starting the bridge (first build restores NuGet packages) ...' -ForegroundColor Cyan
Push-Location (Join-Path $repoRoot 'bridge')
try {
    dotnet run @buildArgs -- "--Showroom:Url=http://127.0.0.1:$Port"
}
finally {
    Pop-Location
}
