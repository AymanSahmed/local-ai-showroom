<#
.SYNOPSIS
  Sets up the OpenVINO sideload: a private, portable Python with a NEWER OpenVINO execution provider, used to run the
  OpenVINO model variants that Foundry Local itself cannot load (OrtEpApi_ReleaseEpContextConfig_SinceV28 error).

.DESCRIPTION
  Needs internet once. Everything ends up in ONE folder (default %USERPROFILE%\LocalAIShowroom\openvino-sideload):

    python\          portable Python (copied from uv's managed install; relocatable, no installer, no PATH changes)
    site-packages\   onnxruntime-genai, onnxruntime-ep-openvino (the newer provider) and their dependencies
    worker.py        the small worker the bridge talks to

  The folder is self-contained, so you can copy it to a PC without internet (the offline bundle can include it with
  build-offline-bundle.ps1 -IncludeOpenVinoSideload). Nothing outside this folder is changed, except that uv and a
  Python build may be downloaded into uv's own cache when they are not there yet.

  Manual alternative (any Windows x64 Python 3.11, with internet):
    py -3.11 -m pip install --target <Root>\site-packages onnxruntime-genai==0.19.0 onnxruntime-ep-openvino==1.8.1
  then copy sideload\openvino\worker.py to <Root> and a Python 3.11 folder to <Root>\python.

.EXAMPLE
  .\scripts\setup-openvino-sideload.ps1
  .\scripts\setup-openvino-sideload.ps1 -Root D:\showroom\openvino-sideload -Force
#>
[CmdletBinding()]
param(
    [string] $Root = (Join-Path $env:USERPROFILE 'LocalAIShowroom\openvino-sideload'),
    [string] $PythonVersion = '3.11',
    [string] $GenAiVersion = '0.19.0',
    [string] $EpVersion = '1.8.1',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$worker = Join-Path $repoRoot 'sideload\openvino\worker.py'

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Write-Ok([string] $Message) { Write-Host "    [ok] $Message" -ForegroundColor Green }
function Test-Command([string] $Name) { return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

if (-not (Test-Path $worker)) { throw "worker.py not found at $worker. Run this script from the showroom repository." }
$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($arch -ne 'AMD64') {
    Write-Warning "This PC is $arch. The OpenVINO provider only ships for Windows x64 (Intel), so the sideload will not work here."
}

if ((Test-Path (Join-Path $Root 'python\python.exe')) -and -not $Force) {
    Write-Host "An installation already exists at $Root. Use -Force to rebuild it." -ForegroundColor Yellow
}

# ------------------------------------------------------------------------------------------------ uv
Write-Step 'uv (downloads and manages the portable Python)'
if (-not (Test-Command 'uv')) {
    winget install --id astral-sh.uv --exact --silent --accept-source-agreements --accept-package-agreements
    $env:Path = "$([Environment]::GetEnvironmentVariable('Path','Machine'));$([Environment]::GetEnvironmentVariable('Path','User'))"
    if (-not (Test-Command 'uv')) { throw 'uv could not be installed. Install it from https://docs.astral.sh/uv/ and run this script again.' }
}
Write-Ok (uv --version)

# ------------------------------------------------------------------------------------------------ portable Python
Write-Step "Portable Python $PythonVersion"
uv python install $PythonVersion
if ($LASTEXITCODE -ne 0) { throw "uv could not install Python $PythonVersion." }
$uvPythons = (uv python dir).Trim()
$source = Get-ChildItem $uvPythons -Directory -Filter "cpython-$PythonVersion*-windows-x86_64-none" |
    Where-Object { Test-Path (Join-Path $_.FullName 'python.exe') } | Sort-Object Name -Descending | Select-Object -First 1
if (-not $source) { throw "Could not find the uv-managed Python $PythonVersion under $uvPythons." }

$pythonDir = Join-Path $Root 'python'
if ((Test-Path $pythonDir) -and $Force) { Remove-Item $pythonDir -Recurse -Force }
if (-not (Test-Path (Join-Path $pythonDir 'python.exe'))) {
    New-Item -ItemType Directory -Path $pythonDir -Force | Out-Null
    robocopy $source.FullName $pythonDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying Python failed (robocopy exit $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0
}
$python = Join-Path $pythonDir 'python.exe'
Write-Ok (& $python --version)

# ------------------------------------------------------------------------------------------------ packages
Write-Step "Packages: onnxruntime-genai $GenAiVersion, onnxruntime-ep-openvino $EpVersion (and dependencies)"
$site = Join-Path $Root 'site-packages'
if ((Test-Path $site) -and $Force) { Remove-Item $site -Recurse -Force }
uv pip install --python $python --target $site "onnxruntime-genai==$GenAiVersion" "onnxruntime-ep-openvino==$EpVersion"
if ($LASTEXITCODE -ne 0) { throw 'Installing the packages failed. Check your internet connection and run this script again.' }

Copy-Item $worker (Join-Path $Root 'worker.py') -Force
Write-Ok "worker.py copied to $Root"

# ------------------------------------------------------------------------------------------------ self-test
Write-Step 'Self-test (loads the packages and registers the OpenVINO provider)'
$env:SHOWROOM_SITE_PACKAGES = $site
$env:PYTHONNOUSERSITE = '1'
$result = & $python -X utf8 (Join-Path $Root 'worker.py') --selftest 2>&1 | Out-String
Remove-Item Env:\SHOWROOM_SITE_PACKAGES -ErrorAction SilentlyContinue
try { $info = $result.Trim() | ConvertFrom-Json } catch { throw "Self-test printed something unexpected:`n$result" }
if (-not $info.ok) { throw "Self-test failed: $($info.error)" }
Write-Ok "onnxruntime $($info.onnxruntime), onnxruntime-genai $($info.onnxruntime_genai), onnxruntime-ep-openvino $($info.onnxruntime_ep_openvino)"
Write-Ok "OpenVINO devices seen by the new provider: $($info.openvino_devices -join ', ')"
if ($info.openvino_devices -notcontains 'NPU' -and $info.openvino_devices -notcontains 'GPU') {
    Write-Warning 'No NPU or GPU device was reported. The sideload still works on the CPU device, but NPU/GPU variants need an Intel NPU/GPU with current drivers.'
}

$sizeMb = [math]::Round((Get-ChildItem $Root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
Write-Step "Done: $Root ($sizeMb MB)"
Write-Host '    This folder is self-contained: copy it to an offline PC, or include it in the offline bundle with'
Write-Host '    build-offline-bundle.ps1 -IncludeOpenVinoSideload.'
