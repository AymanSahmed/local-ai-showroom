<#
.SYNOPSIS
  Sets the app version everywhere it appears. Run this for every change you ship.

.DESCRIPTION
  Single source of truth is the VERSION file. This script also updates:
    - bridge\wwwroot\js\version.js   (shown in the UI header as the UI version)
    - bridge\Package.appxmanifest    (package identity version, x.y.z.0)
  The bridge assembly version (csproj) and the simulation bridge read VERSION directly, and
  build-offline-bundle.ps1 stamps it into the offline manifest.

.EXAMPLE
  .\scripts\set-version.ps1 3.0.1
#>
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $repoRoot 'VERSION'), "$Version`n", $utf8)

$versionJs = Join-Path $repoRoot 'bridge\wwwroot\js\version.js'
$js = [IO.File]::ReadAllText($versionJs)
$js = [regex]::Replace($js, "APP_VERSION = '[^']*'", "APP_VERSION = '$Version'")
[IO.File]::WriteAllText($versionJs, $js, $utf8)

$manifest = Join-Path $repoRoot 'bridge\Package.appxmanifest'
$xml = [IO.File]::ReadAllText($manifest)
$xml = [regex]::Replace($xml, '(<Identity[^>]*?Version=")[^"]*(")', "`${1}$Version.0`${2}")
[IO.File]::WriteAllText($manifest, $xml, $utf8)

Write-Host "Version set to $Version (VERSION, version.js, Package.appxmanifest). Rebuild and hard-refresh (Ctrl+F5)." -ForegroundColor Green
