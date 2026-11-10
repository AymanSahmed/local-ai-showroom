LOCAL AI SHOWROOM - OFFLINE BUNDLE
==================================

Runs with no internet, no NuGet, no .NET SDK and no winget. Nothing is restored, compiled or downloaded at launch.

On the offline PC (Windows 11 24H2+, Developer Mode ON):

  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
  .\start-offline.ps1 -InstallPrerequisites     first run only (installs the bundled Windows App Runtime)
  .\start-offline.ps1                           every other time -> http://127.0.0.1:5080

Folder contents
  app\        the bridge (self-contained, includes the .NET runtime) and the web UI
  prereqs\    Windows App Runtime installers; optionally the Foundry Local installer
  models\     optional: foundry\ (Foundry Local model cache), resnet50\ (Silicon Lab model)
  openvino-sideload\   optional (Intel x64): Python + a newer OpenVINO provider, used to run "-openvino-npu/-gpu" models
  start-offline.ps1

What works offline
  - OpenVINO NPU/GPU models, through the "OpenVINO sideload" card, when openvino-sideload\ is in the bundle. Foundry Local 0.12/0.13
    can't load these itself (upstream bug); the sideload runs the same cached model files. Click "Load" on its card, or use the
    "Run it with the OpenVINO sideload instead" button after a Foundry load error. Needs the Intel NPU/GPU driver on the PC.
  - Foundry Local chat with models already in models\foundry (or already downloaded on that PC)
  - Windows ML / Silicon Lab with models\resnet50
  - Windows AI APIs (language model, OCR, image description) on a Copilot+ PC whose Windows components are already installed
  - The whole web UI (no CDNs, fonts or analytics)

What still needs internet (do it on an online PC first)
  - ONE-TIME PROVISIONING OF THE TARGET LAPTOP: connect it to the internet once, open the Readiness tab and click
      * Prepare on each Windows AI feature (Windows Update downloads the language model / OCR / image description;
        they can't be bundled and afterwards work offline). Needs a Copilot+ PC with an NPU.
      * Download & register EPs on the Windows ML card (vendor execution providers such as OpenVINO / QNN).
    Foundry hardware variants (names containing -openvino-, -qnn-, -vitis-, -trt-) need those providers to load.
    Without them, load a "generic-cpu" variant, which always works offline.
  - Downloading models: scripts\prepare-models.ps1 or the "Browse model catalog" button, then rebuild the bundle with -IncludeFoundryModels
  - Windows AI model components (Windows Update) and Windows ML execution providers
  - Edge Translator language packs (download them in Edge on a connected PC)
  - Browsing the Foundry catalog

Notes
  - If something doesn't work (Foundry Local errors, Windows AI "not available", ...), run  .\diagnose.ps1  while the app is
    running and send the report file it writes (user and PC names are removed). Add -TryPrepare to also ask Windows to prepare
    the Windows AI features (needs internet). The Readiness tab also has a "Copy diagnostics" button.
  - The offline copy registers a separate package (LocalAIShowroom.Bridge.Offline). Remove it with:
      Get-AppxPackage LocalAIShowroom.Bridge.Offline | Remove-AppxPackage
  - If you move this folder, run .\start-offline.ps1 again; it re-registers the new location.
  - Bundles inside a OneDrive (cloud-synced) folder run from a local copy of app\ (%LOCALAPPDATA%\LocalAIShowroom\offline-app),
    because Windows can't start a package from cloud placeholders ("The process has no package identity"). Force it anywhere
    with .\start-offline.ps1 -LocalCopy. Prefer a non-synced folder (or "Always keep on this device") for models\ and openvino-sideload\.
  - -InstallPrerequisites only installs the Windows App Runtime. Install Foundry Local separately
    (prereqs\FoundryLocal, if bundled): Add-AppxPackage <file>.msix
  - Bundling models changes Foundry Local's model-cache folder on the target PC (foundry cache cd).
