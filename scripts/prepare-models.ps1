<#
.SYNOPSIS
  Downloads the demo models (do this the day before, on good Wi-Fi).

.DESCRIPTION
  1. Foundry Local: starts the service on a fixed port and downloads the chat models.
  2. Windows ML:    builds a hardware-portable ResNet-50 with winml-cli for the Silicon Lab
                    and makes sure ImageNet labels (config.json) sit next to the model.

.EXAMPLE
  .\scripts\prepare-models.ps1
  .\scripts\prepare-models.ps1 -FoundryModels phi-4-mini,qwen2.5-1.5b -NoQuant
#>
[CmdletBinding()]
param(
    [string[]] $FoundryModels = @('phi-4-mini', 'qwen2.5-0.5b'),
    [int] $FoundryPort = 39839,
    [switch] $SkipFoundry,
    [switch] $SkipResNet,
    [switch] $NoQuant,
    [string] $DataRoot = (Join-Path $env:USERPROFILE 'LocalAIShowroom')
)

$ErrorActionPreference = 'Stop'
function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }

# ------------------------------------------------------------------------------------------------ Foundry Local
if (-not $SkipFoundry) {
    Write-Step "Foundry Local (port $FoundryPort)"
    if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) {
        Write-Warning 'foundry CLI not found. Run scripts\setup.ps1 or: winget install Microsoft.FoundryLocal'
    }
    else {
        foundry server start --port $FoundryPort --idle-timeout 0
        if ($LASTEXITCODE -ne 0) { foundry service start } # older 0.x CLI
        foreach ($model in $FoundryModels) {
            Write-Host "    Downloading $model (the best variant for this PC: NPU, GPU or CPU) ..."
            foundry model download $model
        }
        if ($FoundryModels.Count -gt 0) { foundry model load $FoundryModels[0] }
        foundry model list --cached
        foundry server status
    }
}

# ------------------------------------------------------------------------------------------------ ResNet-50 via winml-cli
if (-not $SkipResNet) {
    Write-Step 'ResNet-50 for Windows ML (winml-cli)'
    $winml = Join-Path $DataRoot 'winml-cli\.venv\Scripts\winml.exe'
    if (-not (Test-Path $winml)) { $winml = 'winml' }
    if (-not (Get-Command $winml -ErrorAction SilentlyContinue)) {
        Write-Warning 'winml-cli not found. Run scripts\setup.ps1 (or see https://github.com/microsoft/winml-cli).'
    }
    else {
        $out = Join-Path $DataRoot 'models\resnet50'
        New-Item -ItemType Directory -Path $out -Force | Out-Null

        & $winml inspect -m microsoft/resnet-50
        $buildArgs = @('build', '-m', 'microsoft/resnet-50', '-o', $out)
        if ($NoQuant) { $buildArgs += '--no-quant' }
        & $winml @buildArgs

        $labels = Get-ChildItem -Path $out -Recurse -Filter 'config.json' -ErrorAction SilentlyContinue |
            Where-Object { (Get-Content $_.FullName -Raw) -match '"id2label"' }
        if (-not $labels) {
            Write-Host '    Fetching ImageNet labels (config.json) from Hugging Face ...'
            Invoke-WebRequest -UseBasicParsing -Uri 'https://huggingface.co/microsoft/resnet-50/resolve/main/config.json' -OutFile (Join-Path $out 'config.json')
        }

        $model = Join-Path $out 'model.onnx'
        if (-not (Test-Path $model)) {
            $model = Get-ChildItem -Path $out -Recurse -Filter '*.onnx' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
        if ($model) {
            Write-Host "    Model: $model"
            Write-Host '    Quick benchmark (auto = NPU first, then GPU, then CPU):'
            & $winml perf -m $model --device auto --iterations 20
        }
        else {
            Write-Warning "No .onnx file found under $out. Check the winml build output above."
        }
    }
}

Write-Step 'Models ready. Start the demo with .\scripts\start-demo.ps1'
