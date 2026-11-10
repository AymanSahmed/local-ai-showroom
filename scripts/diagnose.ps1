<#
.SYNOPSIS
  Collects a diagnostics report for the Local AI Showroom on this PC (system, NPU, Windows AI / Windows App Runtime,
  Foundry Local, and the running bridge) so problems on a machine you can't debug directly can be understood.

.DESCRIPTION
  Safe to run at any time: it only reads information, except with -TryPrepare which asks Windows to prepare the
  Windows AI features (that can download components, so run it online). The report is written next to this script
  and printed. Your user name, PC name and profile path are replaced by placeholders.

.EXAMPLE
  .\diagnose.ps1                 # bridge on the default port 5080 (start it first with start-offline.ps1)
  .\diagnose.ps1 -Port 5090
  .\diagnose.ps1 -TryPrepare     # also tries Prepare for each Windows AI feature (may download; needs internet)
#>
[CmdletBinding()]
param(
    [int] $Port = 5080,
    [switch] $TryPrepare
)

$ErrorActionPreference = 'Continue'
$report = [System.Collections.Generic.List[string]]::new()

function Redact([string] $Text) {
    if (-not $Text) { return $Text }
    $t = $Text
    if ($env:USERPROFILE) { $t = $t.Replace($env:USERPROFILE, '%USERPROFILE%') }
    if ($env:USERNAME) { $t = $t -replace [regex]::Escape($env:USERNAME), '<user>' }
    if ($env:COMPUTERNAME) { $t = $t -replace [regex]::Escape($env:COMPUTERNAME), '<pc>' }
    return $t
}
function Add-Line([string] $Text = '') { $report.Add((Redact $Text)) }
function Add-Section([string] $Title) { Add-Line ''; Add-Line ('=' * 78); Add-Line $Title; Add-Line ('=' * 78) }
function Add-Table($Data) {
    if ($null -eq $Data -or @($Data).Count -eq 0) { Add-Line '(none)'; return }
    foreach ($line in (($Data | Format-List | Out-String -Width 220) -split "`r?`n")) { if ($line.Trim()) { Add-Line $line } }
}
function Run-Section([string] $Title, [scriptblock] $Body) {
    Add-Section $Title
    try { & $Body } catch { Add-Line "ERROR: $($_.Exception.Message)" }
}
function Run-Foundry([string] $Arguments) {
    if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) { Add-Line 'foundry CLI not found on PATH'; return }
    Add-Line "> foundry $Arguments"
    $out = & foundry @($Arguments -split ' ') 2>&1 | Out-String -Width 240
    foreach ($line in ($out -split "`r?`n")) { if ($line.Trim()) { Add-Line "  $line" } }
}

Add-Line "Local AI Showroom diagnostics  ·  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
$versionFile = @((Join-Path $PSScriptRoot 'VERSION'), (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($versionFile) { Add-Line "App version file: $((Get-Content $versionFile -Raw).Trim())" }

Run-Section '1. System' {
    $os = Get-CimInstance Win32_OperatingSystem
    $nt = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    Add-Line "Windows        : $($os.Caption) $($nt.DisplayVersion) build $($os.BuildNumber).$($nt.UBR) ($($os.OSArchitecture))"
    Add-Line "Processor      : $((Get-CimInstance Win32_Processor | Select-Object -First 1).Name)"
    Add-Line "Memory         : $([math]::Round($os.TotalVisibleMemorySize / 1MB, 1)) GB"
    Add-Line "Culture/region : $((Get-Culture).Name)  /  GeoId $((Get-ItemProperty 'HKCU:\Control Panel\International\Geo' -ErrorAction SilentlyContinue).Nation)"
    Add-Line "Developer Mode : $(((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense) -eq 1)"
    Add-Line 'GPUs:'
    Get-CimInstance Win32_VideoController | ForEach-Object { Add-Line "  $($_.Name)  (driver $($_.DriverVersion))" }
}

Run-Section '2. NPU / compute accelerators (hardware as Windows sees it)' {
    # 'NPU' must be a whole word: it also appears inside "Input".
    $devices = Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.PNPClass -eq 'ComputeAccelerator' -or ($_.Name -cmatch '\bNPU\b') -or ($_.Name -match 'Neural|AI Boost|Hexagon|Ryzen AI') }
    if (-not $devices) {
        Add-Line 'No NPU / compute accelerator device found. Without one, Windows AI APIs cannot run on the NPU.'
    }
    foreach ($d in $devices) {
        $driver = Get-CimInstance Win32_PnPSignedDriver -Filter "DeviceID='$($d.DeviceID.Replace('\', '\\'))'" -ErrorAction SilentlyContinue
        Add-Line "$($d.Name)  [status: $($d.Status), class: $($d.PNPClass), driver: $($driver.DriverVersion), date: $($driver.DriverDate)]"
    }
    Add-Line ''
    Add-Line 'Note: Windows AI features (Phi Silica, text recognition, image description) need a Copilot+ PC: an NPU of 40+ TOPS'
    Add-Line '(Snapdragon X, Intel Core Ultra 200V, AMD Ryzen AI 300), Windows 11 24H2+ with current updates. Smaller NPUs (for'
    Add-Line 'example first-generation Intel Core Ultra) are reported as NotSupportedOnCurrentSystem even though an NPU exists.'
}

Run-Section '3. Windows App Runtime, app package, Windows AI components, execution providers' {
    function Add-Packages($Packages) {
        if (-not $Packages) { Add-Line '  (none)'; return }
        foreach ($p in $Packages) { Add-Line ("  {0}  {1}  {2}  [{3}]" -f $p.Name, $p.Version, $p.Architecture, $p.Status) }
    }
    Add-Line 'Windows App Runtime packages:'
    Add-Packages (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime*' | Sort-Object Name, Version, Architecture)
    Add-Line 'Local AI Showroom packages:'
    $showroom = Get-AppxPackage -Name 'LocalAIShowroom*'
    Add-Packages $showroom
    foreach ($p in $showroom) { Add-Line "    location: $($p.InstallLocation)" }
    if ($showroom | Where-Object Name -eq 'LocalAIShowroom.Bridge.Offline') {
        Add-Line "  offline app alias present: $(Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\LocalAIShowroomBridge.exe'))"
    }
    Add-Line 'Windows AI component packages (language model / vision):'
    Add-Packages (Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'AIX|WindowsWorkload|Phi|Client\.AI|WindowsAI|Copilot' } | Sort-Object Name -Unique)
    Add-Line 'Windows ML / execution provider packages:'
    Add-Packages (Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'WindowsML|ExecutionProvider|OpenVINO|QNN|VitisAI|MIGraphX|TensorRT|WinML' } | Sort-Object Name -Unique)
}

Run-Section '4. Foundry Local' {
    function Add-FoundryModels([string] $Filter) {
        $json = (& foundry model list $Filter --variants --output json 2>&1 | Out-String)
        try {
            $variants = @(($json | ConvertFrom-Json).variants)
            if (-not $variants) { Add-Line "  (none)"; return }
            foreach ($v in $variants) { Add-Line ("  {0}  [{1}, {2}, {3} MB]" -f $v.variantId, $v.device, $v.executionProvider, $v.fileSizeMb) }
        }
        catch { Add-Line "  could not read the list: $json" }
    }
    $cmd = Get-Command foundry -ErrorAction SilentlyContinue
    Add-Line "foundry CLI: $(if ($cmd) { $cmd.Source } else { 'NOT FOUND on PATH (install Foundry Local)' })"
    if ($cmd) {
        Run-Foundry '--version'
        Run-Foundry 'cache location'
        Run-Foundry 'server status'
        Add-Line 'Downloaded (cached) model variants:'
        Add-FoundryModels '--cached'
        Add-Line 'Loaded model variants:'
        Add-FoundryModels '--loaded'
        Add-Line ''
        Add-Line 'Last daemon log lines (the real reason a model fails to load is usually here):'
        Run-Foundry 'server logs -n 40'
    }
}

Run-Section "5. The running bridge (http://127.0.0.1:$Port)" {
    $base = "http://127.0.0.1:$Port"
    try {
        $ping = Invoke-RestMethod "$base/api/ping" -TimeoutSec 5
        Add-Line "ping: version $($ping.version), simulated: $($ping.simulated)"
    }
    catch {
        Add-Line "The bridge is not running on port $Port ($($_.Exception.Message)). Start it with .\start-offline.ps1 and run this again."
        return
    }
    $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 120
    Add-Line "Bridge process: $($status.machine.processArchitecture), $($status.machine.dotnet), package identity: $($status.windowsAI.hasIdentity) ($($status.windowsAI.packageFamilyName))"
    Add-Line "Windows App Runtime seen by the bridge: $($status.windowsAI.runtimeVersion)"
    Add-Line ''
    Add-Line 'Windows AI features:'
    foreach ($f in $status.windowsAI.features) { Add-Line "  $($f.name): $($f.state)$(if ($f.error) { '  -> ' + $f.error })" }
    if ($status.windowsAI.error) { Add-Line "  error: $($status.windowsAI.error)" }
    Add-Line ''
    Add-Line 'Windows ML:'
    Add-Line "  devices: $((@($status.windowsML.devices) | ForEach-Object { "$($_.type)/$($_.ep)" }) -join ', ')"
    Add-Line "  providers: $((@($status.windowsML.providers) | ForEach-Object { "$($_.name)=$($_.readyState)" }) -join ', ')"
    Add-Line "  catalog error: $($status.windowsML.catalogError)   registration error: $($status.windowsML.registrationError)"
    Add-Line "  model: found=$($status.windowsML.model.found) path=$($status.windowsML.model.path)"
    if ($status.windowsML.error) { Add-Line "  error: $($status.windowsML.error)" }
    Add-Line ''
    Add-Line 'Foundry Local as seen by the bridge:'
    Add-Line "  running: $($status.foundry.running)  version: $($status.foundry.version)  endpoint: $($status.foundry.endpoint)  cliFound: $($status.foundry.cliFound)"
    if ($status.foundry.error) { Add-Line "  error: $($status.foundry.error)" }
    foreach ($m in @($status.foundry.models)) { Add-Line "  model: $($m.id)  device=$($m.device) cached=$($m.cached) loaded=$($m.loaded)" }

    Add-Line ''
    Add-Line 'OpenVINO sideload as seen by the bridge:'
    if ($status.openvino) {
        $ov = $status.openvino
        Add-Line "  installed: $($ov.installed)  root: $($ov.root)  python: $($ov.pythonFound)  packages: $($ov.packagesFound)  worker: $($ov.workerFound)"
        Add-Line "  loaded: $($ov.loaded)  busy: $($ov.busy)"
        if ($ov.error) { Add-Line "  error: $($ov.error)" }
        foreach ($m in @($ov.models)) { Add-Line "  model: $($m.id)  device=$($m.device) loaded=$($m.loaded)" }
    }
    else { Add-Line '  (this bridge version has no sideload information)' }
    if ($TryPrepare) {
        Add-Line ''
        Add-Line 'Prepare results (this can download Windows AI components):'
        foreach ($feature in 'languageModel', 'textRecognizer', 'imageDescription') {
            try {
                $r = Invoke-RestMethod "$base/api/windows-ai/prepare" -Method Post -ContentType 'application/json' -Body (@{ feature = $feature } | ConvertTo-Json) -TimeoutSec 900
                Add-Line "  $feature -> $($r.result) $($r.hresult) $($r.error)"
            }
            catch { Add-Line "  $feature -> request failed: $($_.ErrorDetails.Message) $($_.Exception.Message)" }
        }
    }
}

$text = $report -join "`r`n"
$file = Join-Path $PSScriptRoot ("diagnostics-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
[System.IO.File]::WriteAllText($file, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host $text
Write-Host "`nReport saved to: $file" -ForegroundColor Green
Write-Host 'Send that file (or its contents) to whoever is helping you.' -ForegroundColor Green
