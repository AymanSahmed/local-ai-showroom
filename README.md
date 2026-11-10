# Local AI Showroom

**One browser tab that shows, live on a Copilot+ PC, every Microsoft on-device AI stack doing real business work, with zero cloud calls.**

Local AI Showroom is a light web app plus a small local "bridge" service. The browser handles the UI. The bridge talks to the AI runtimes installed on the PC:

| Stack | What the audience sees | Built on |
|---|---|---|
| **Windows AI APIs** | Built-in language model, OCR, image description, all on the NPU | [Windows App SDK 2.x](https://github.com/microsoft/WindowsAppSDK) |
| **Foundry Local** | Open models (Phi, Qwen, DeepSeek…) on NPU / GPU / CPU via an OpenAI-compatible API | Foundry Local CLI |
| **Windows ML** | *Your own* ONNX model racing on NPU vs GPU vs CPU | Windows ML + [winml-cli](https://github.com/microsoft/winml-cli) |
| **Aion Instruct** *(optional)* | The next built-in model (replacing Phi Silica) | [Aion-Instruct-Preview-Sample](https://github.com/microsoft/Aion-Instruct-Preview-Sample) SDK |
| **Edge built-in AI** *(bonus)* | The browser's own on-device model (Prompt API) | Microsoft Edge Canary/Dev |

[Foundry Toolkit for VS Code](https://marketplace.visualstudio.com/items?itemName=ms-windows-ai-studio.windows-ai-studio) and [AI Dev Gallery](https://apps.microsoft.com/detail/9n9pn1mm3bd5) are the "how do I build this?" follow-ups. See [How the five resources fit](#how-the-five-resources-fit).

---

## The demo in four acts

| Tab | Story | Proof point |
|---|---|---|
| **1 · Readiness** | "Is this PC ready for on-device AI?" Live inventory of every stack, NPU/GPU/CPU, execution providers, models | Readiness meter, one-click *Prepare* / *Download EPs* / *Start Foundry Local* |
| **2 · Model Arena** | Same prompt, 2–4 engines side by side (built-in model vs Foundry Local NPU/GPU/CPU vs Edge) | Time to first token, tokens/s, "fastest" badge |
| **3 · Private Doc Desk** | A confidential scanned claim form → OCR (NPU via Windows AI, or the built-in Windows OCR engine on CPU when no NPU is available) → summary, JSON fields, translation into a language you pick, Q&A, image description | Word boxes coloured by OCR confidence; works with Wi-Fi **off** |
| **4 · Silicon Lab** | A ResNet-50 built with `winml build` classifies a photo or live camera on NPU vs GPU vs CPU; then `winml perf` benchmarks the same file | "11× faster on NPU than CPU" headline, same answer on every device |

A **Privacy guard** pill shows *Cloud calls: 0*. The bridge listens on loopback only, and every outbound HTTP call goes through a guard that only allows localhost. An **Activity** drawer logs every on-device call with its engine, device and latency.

---

## Architecture

```
 Browser (Edge / Chrome)            Local AI Showroom bridge (.NET 10, loopback only)            Runtimes on this PC
 ─────────────────────────          ───────────────────────────────────────────────────          ─────────────────────────────
  index.html + js/*.js   ── fetch/SSE ──►  /api/windows-ai/*  ── Windows App SDK 2.6 ──────────►  Phi Silica / Aion, OCR, image description (NPU)
  (no CDNs, no npm)                        /api/windows-ml/*  ── Windows ML + EP catalog ────────►  QNN / OpenVINO / VitisAI / TensorRT-RTX / DirectML / CPU
                                           /api/foundry/*     ── HTTP (localhost only) ──────────►  Foundry Local service (OpenAI-compatible /v1)
  Edge Prompt API (in-browser model)       /api/winml-cli/*   ── process ───────────────────────►  winml sys / winml perf
                                           /api/aion/*        ── Aion preview SDK (optional) ───►  Aion Instruct framework package (NPU)
```

* The bridge runs with **package identity** (required by Windows AI APIs) through the **winapp CLI** integration: a plain `dotnet run` registers the build output as a loose-layout dev package and launches it in your terminal.
* Every stack is probed independently. A missing piece shows as "not available" and never breaks the rest of the demo.

```
local-ai-showroom/
├─ README.md                  you are here
├─ VERSION                    app version (shown in the UI header); bump with scripts\set-version.ps1
├─ CHANGELOG.md               what changed in each version
├─ DEMO-SCRIPT.md             12-minute talk track, checklist and Q&A prep
├─ scripts/
│  ├─ setup.ps1               one-time PC setup (winget: .NET 10, winapp, Windows App Runtime 2, Foundry Local, winml-cli)
│  ├─ prepare-models.ps1      downloads Foundry Local models, builds ResNet-50 with winml-cli
│  ├─ start-demo.ps1          starts Foundry Local + the bridge (or -Simulate for rehearsals)
│  ├─ build-offline-bundle.ps1  builds a self-contained bundle for PCs without internet
│  ├─ start-offline.ps1       launcher shipped inside the offline bundle
│  ├─ setup-openvino-sideload.ps1  installs the optional OpenVINO sideload (portable Python + newer OpenVINO provider)
│  └─ set-version.ps1         sets the version everywhere (VERSION, UI, package manifest)
├─ sideload/openvino/         worker.py: runs Foundry's cached OpenVINO NPU/GPU models with onnxruntime-genai
├─ bridge/                    .NET 10 ASP.NET Core bridge
│  ├─ Program.cs              endpoints + local-only guards
│  ├─ Services/               WindowsAIService, WindowsMLService, FoundryLocalService, OpenVinoSideloadService, WinmlCliService, AionService, StatusService
│  ├─ Infrastructure/         SSE streaming, egress guard, process runner, machine info
│  ├─ Package.appxmanifest    identity + systemAIModels capability
│  ├─ offline/                package manifest template used by the offline bundle
│  ├─ appsettings.json        configuration (see below)
│  ├─ nuget-local/            drop-in feed for the optional Aion SDK .nupkg
│  └─ wwwroot/                the web app (HTML/CSS/vanilla JS modules)
├─ tests/FoundryLocal.Tests/  regression checks for Foundry Local loading, catalog, downloads and chat
└─ mock/
   ├─ mock-bridge.mjs         simulation bridge: same API, canned answers (rehearse anywhere)
   └─ selftest.mjs            API smoke test for the real or simulated bridge
```

---

## Requirements

| Tier | Machine | What works |
|---|---|---|
| **Full demo** | Copilot+ PC (Snapdragon X, Intel Core Ultra 200V, AMD Ryzen AI 300), Windows 11 24H2+, Developer Mode on | Everything |
| **Partial** | Any Windows 11 PC | Foundry Local (CPU/GPU), Windows ML on CPU/GPU (DirectML), Edge built-in AI if supported. Windows AI APIs show "not supported". |
| **Rehearsal** | Any OS with Node.js 20+ | Full UI with canned answers and a "Simulation mode" banner |

Software (installed by `scripts\setup.ps1`): .NET 10 SDK, winapp CLI, Windows App Runtime 2, Foundry Local, and optionally uv + Python 3.11 x64 + winml-cli.

---

## Quick start

```powershell
# from the repo root, in PowerShell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

.\scripts\setup.ps1            # once per PC (turns on Developer Mode if run as admin)
.\scripts\prepare-models.ps1   # once, on good Wi-Fi: Foundry Local models + ResNet-50 via winml-cli
.\scripts\start-demo.ps1       # every time: opens http://127.0.0.1:5080
```

Rehearse on any laptop (no Windows AI needed): `node mock/mock-bridge.mjs`, then open http://127.0.0.1:5080.
Smoke-test a running bridge: `node mock/selftest.mjs`.

Foundry Local compatibility regression checks (no AI runtime or model downloads needed):
`dotnet run --project tests\FoundryLocal.Tests\FoundryLocal.Tests.csproj`.

The Foundry Local card lists downloaded or loaded models. Click **Browse model catalog** to search
available variants, filter by device or task, and inspect size, license and download state.
Choose **Download & load** (internet required), or **Load** for a cached variant. Downloads can take
several minutes; the dialog shows the current phase and reports download and load errors separately.
Downloaded models stay cached even if loading fails. Only choose GPU/NPU variants compatible with
your hardware and drivers. The CLI alternative is `foundry model download <alias>`.

### Manual steps (if you prefer)

1. **Bridge with identity:** `cd bridge` then `dotnet run`. The `Microsoft.Windows.SDK.BuildTools.WinApp` package makes `dotnet run` register a loose-layout package from `Package.appxmanifest` (identity + `systemAIModels`) and launch it in your terminal. Developer Mode is required.
   *Without identity* (Foundry Local + Windows ML only): `dotnet run -p:EnableWinAppRunSupport=false`.
2. **Foundry Local:** `winget install Microsoft.FoundryLocal`, then `foundry server start --port 39839 --idle-timeout 0` and `foundry model download phi-4-mini`. The bridge discovers the endpoint with `foundry server status`, or set `FoundryLocal:Endpoint`.
3. **Windows ML model:** with winml-cli (Python 3.11 x64 + uv):
   ```powershell
   uv pip install winml-cli
   winml sys --list-device --list-ep
   winml build -m microsoft/resnet-50 -o $env:USERPROFILE\LocalAIShowroom\models\resnet50
   winml perf -m $env:USERPROFILE\LocalAIShowroom\models\resnet50\model.onnx --device auto --iterations 50
   ```
   Labels come from a Hugging Face `config.json` (`id2label`) or a `labels.txt` next to the model. `prepare-models.ps1` downloads it if it's missing. Any image-classification ONNX model works, including one converted with Foundry Toolkit's **Model Conversion**: point `WindowsML:ModelPath` at it.
4. **Edge built-in AI (bonus lane):** in Edge Canary/Dev, enable `edge://flags` → *Prompt API for on-device language model*. For Aion-1.0-Instruct (Edge 150.0.4070+), also enable *Enable prerelease on-device language model*. Check `edge://on-device-internals`.
   When the Prompt, Summarizer or Language Detector API shows *downloadable*, click its **Download** button on the Edge card (internet needed once; Edge requires a click and the model can be several GB). A progress bar shows the download; failures stay visible and can be retried.
   The **Translator API** panel on the Readiness tab lets you pick any source and target language (and swap direction). When a pair shows *downloadable*, click **Download … language pack** (needs internet once; the browser requires a click), then translate fully on-device. Pairs the browser doesn't support show *unavailable*.
5. **Aion Instruct Preview (optional):** install the framework MSIX from the [sample's releases](https://github.com/microsoft/Aion-Instruct-Preview-Sample) and copy `AionInstructPreview.Text.Framework.1.1.0.nupkg` into `bridge/nuget-local`. Then run `dotnet run -p:EnableAion=true` (or `start-demo.ps1 -Aion`). Supported on Snapdragon (QNN) and Intel Lunar Lake (OpenVINO); there is no CPU fallback. **The preview is for developer validation only under its pre-release EULA. Don't hand it to customers.**

---

## Offline / air-gapped PCs

`start-demo.ps1` builds the bridge, which restores NuGet packages and fails without internet (`NU1301`). For an offline PC, build a bundle **once on a connected PC**:

```powershell
.\scripts\build-offline-bundle.ps1                                   # app + Windows App Runtime installers + Silicon Lab model
.\scripts\build-offline-bundle.ps1 -IncludeFoundryModels -Zip        # also the downloaded Foundry Local models
.\scripts\build-offline-bundle.ps1 -IncludeFoundryModels -IncludeOpenVinoSideload   # plus the OpenVINO sideload (Intel NPU/GPU laptops)
.\scripts\build-offline-bundle.ps1 -Architecture arm64               # for a Snapdragon X target
```

Copy `dist\LocalAIShowroom-offline` to the offline PC and run `.\start-offline.ps1 -InstallPrerequisites` (first time) or `.\start-offline.ps1`.
The bundle is self-contained: no .NET SDK, NuGet, winget or internet is needed to run it. Models are the only large downloads; download them on the connected PC first.
Windows AI features still need a supported Copilot+ PC with its Windows components already provisioned, and Foundry Local installed on the target (`-IncludeFoundryInstaller` bundles its installer). See `README-OFFLINE.txt` inside the bundle.

---

## OpenVINO sideload (optional, Intel NPU / GPU)

Foundry Local 0.12/0.13 can't load `-openvino-npu` / `-openvino-gpu` variants (upstream bug, see Troubleshooting) and has no setting to use another provider. The **OpenVINO sideload** works around that: a small Python worker (`onnxruntime-genai` 0.19.0 + the newer `onnxruntime-ep-openvino` 1.8.1) runs the **same model folders from Foundry's cache** outside Foundry Local. Nothing about Foundry is changed.

```powershell
.\scripts\setup-openvino-sideload.ps1        # once, online: creates %USERPROFILE%\LocalAIShowroom\openvino-sideload (about 400 MB, portable)
```

Then restart the bridge. The Readiness tab shows an **OpenVINO sideload** card listing the OpenVINO variants found in the Foundry cache; click **Load**. After a Foundry load error for an OpenVINO model, the Foundry card and the catalog also offer **Run it with the OpenVINO sideload instead**. A loaded model appears as an `openvino:` engine in the Arena and Doc Desk pickers, with the same streaming and metrics as the Foundry lanes. Only one sideload model is loaded at a time. Offline, add `-IncludeOpenVinoSideload` when building the bundle; `start-offline.ps1` wires it up.

**Honest status:** the mechanism was verified here with a real model on the OpenVINO **CPU** device. The NPU and GPU paths need Intel hardware and drivers that this build PC doesn't have, so they are untested. If it fails on your laptop, click **Copy diagnostics** (it includes the sideload state); the Python error is also shown on the card. The sideload is x64 only.

---

## Configuration (`bridge/appsettings.json`, overrides in `appsettings.local.json` or `--Key=value`)

| Key | Default | Meaning |
|---|---|---|
| `Showroom:Url` | `http://127.0.0.1:5080` | Must be a loopback address (enforced) |
| `Showroom:OpenBrowser` | `true` | Open the UI when the bridge starts |
| `FoundryLocal:Endpoint` | *(empty = auto-discover)* | e.g. `http://127.0.0.1:39839` |
| `FoundryLocal:Port` | `39839` | Used by the *Start Foundry Local* button |
| `FoundryLocal:Cli` | `foundry` | CLI command or executable path; required for model loading and cached/loaded inventory on Foundry Local 0.13 |
| `FoundryLocal:CliTimeoutSeconds` | `90` | How long a `foundry model list` call may take before it counts as failed (slow PCs while a model loads) |
| `FoundryLocal:InventoryTtlSeconds` | `15` | How long the model list is reused before it is refreshed in the background |
| `FoundryLocal:LoadTtlSeconds` | `3600` | Model lifetime for the legacy HTTP load API; newer versions use the CLI's lifecycle defaults |
| `WindowsML:ModelPath` | `%USERPROFILE%\LocalAIShowroom\models\resnet50` | `.onnx` file or a winml-cli output folder |
| `WindowsML:Mean` / `Std` | ImageNet values | Input normalisation |
| `OpenVinoSideload:Root` | next to the app (`..\openvino-sideload`), else `%USERPROFILE%\LocalAIShowroom\openvino-sideload` | Folder made by `setup-openvino-sideload.ps1`; when set explicitly it is used as is |
| `OpenVinoSideload:ModelRoots` | *(empty)* | Extra model cache folders to scan (`;`-separated), besides `foundry cache location` |
| `OpenVinoSideload:IncludeGenericModels` | `false` | Also list non-OpenVINO models (for testing the worker) |
| `WinmlCli:Command` | `%USERPROFILE%\LocalAIShowroom\winml-cli\.venv\Scripts\winml.exe` | Or `winml` on PATH; `PrefixArgs` supports `uv` + `run winml` |

---

## Bridge API (all JSON; streams are `text/event-stream` with `start` / `delta` / `done` / `error` events)

| Method | Path | Does |
|---|---|---|
| GET | `/api/status` | Machine, identity, Windows AI readiness, Windows ML EPs/devices/model, Foundry Local models, privacy counters |
| POST | `/api/windows-ai/prepare` | `EnsureReadyAsync` for `languageModel`, `textRecognizer` or `imageDescription` |
| POST | `/api/windows-ai/generate` | Streams the built-in language model |
| POST | `/api/windows-ai/summarize`, `/rewrite` | Text intelligence skills (`TextSummarizer`, `TextRewriter`) |
| POST | `/api/windows-ai/ocr` | `TextRecognizer`: lines, words, polygons and confidence |
| POST | `/api/windows-ai/describe` | `ImageDescriptionGenerator` (brief, detailed, accessible, diagram) |
| GET | `/api/foundry/models` | Endpoint + cached/loaded models (device inferred from the model id) |
| GET | `/api/foundry/catalog` | Available CLI catalog variants with alias, device, task, size, license and cached/loaded state |
| POST | `/api/foundry/download` | Download a variant by `{ "model": "<variant-id>" }` and verify it is cached (up to one hour) |
| POST | `/api/foundry/start`, `/load` | Start the service, load a model |
| POST | `/api/foundry/chat` | Streams `/v1/chat/completions` |
| GET | `/api/openvino/models` | OpenVINO sideload state and the OpenVINO model variants found in the Foundry cache |
| POST | `/api/openvino/load`, `/unload` | Load a model (`{ "model": "<id>" }`) into the sideload worker, or unload it |
| POST | `/api/openvino/chat` | Streams a chat reply from the loaded sideload model (same events as Foundry) |
| POST | `/api/windows-ml/prepare-eps` | `ExecutionProviderCatalog.EnsureAndRegisterCertifiedAsync()` |
| POST | `/api/windows-ml/classify` | Runs the ONNX model on `auto` (PREFER_NPU policy), `npu`, `gpu` or `cpu` with timed runs |
| POST | `/api/winml-cli/sys`, `/perf` | Runs `winml sys` / `winml perf` and parses the result |
| POST | `/api/aion/generate` | Streams Aion Instruct Preview (when built with `EnableAion`) |

---

## How the five resources fit

| Resource | Role in the showroom |
|---|---|
| **winml-cli** | Builds the Silicon Lab model (`winml build`), shows the device/EP inventory (`winml sys`) and benchmarks the same file (`winml perf`) from the UI. |
| **Windows App SDK** | The bridge's engine: Windows AI APIs (language model, `TextSummarizer`, `TextRewriter`, `TextRecognizer`, `ImageDescriptionGenerator`) and Windows ML (ONNX Runtime + EP catalog). Version 2.6.4+ no longer needs the Language Model LAF unlock. |
| **Foundry Toolkit for VS Code** | The "build it yourself" story. Browse and try Foundry Local models in the playground, convert/quantize Hugging Face models for NPU with **Model Conversion** (drop the ONNX into Silicon Lab), profile Windows ML workloads, and train **LoRA adapters for Aion Instruct**. |
| **Aion Instruct Preview Sample** | Source of the optional Aion lane. The bridge uses the same SDK and the same dynamic-dependency pattern as the sample's unpackaged console. Aion Instruct begins replacing Phi Silica on Insider builds in Nov 2026 and on retail devices in Jan 2027. |
| **AI Dev Gallery** | The follow-up after the demo: each act maps to Gallery samples (language model chat, text recognition, image description, Foundry Local, Windows ML) whose C# source can be exported as a Visual Studio project. |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Windows AI cards say *Needs package identity* | Start the bridge with `dotnet run` (or `start-demo.ps1`), not by double-clicking the .exe. Developer Mode must be on. |
| *Not declared by app* / `CapabilityMissing` | Keep `systemAIModels` and `MaxVersionTested="10.0.26226.0"` (or later) in `Package.appxmanifest`, then rebuild. |
| `NotSupportedOnCurrentSystem` | Not a Copilot+ PC, or the feature isn't offered in your region. Use the Foundry Local lanes. |
| `NotReady` | Click **Prepare**. Windows downloads the model through Windows Update (see Settings › Windows Update). |
| Foundry Local *not running* | `foundry server start --port 39839 --idle-timeout 0`, or set `FoundryLocal:Endpoint`. Older 0.x CLIs use `foundry service start`. |
| Foundry Local Load returns 404 for `/openai/load/...` | Rebuild and restart the bridge. On Foundry Local 0.13, the bridge uses `foundry model load` when the legacy HTTP load route is unavailable, and reads cached/loaded variants through the CLI. Keep the CLI on PATH (or set `FoundryLocal:Cli`) and point the bridge at the same local daemon managed by that CLI. |
| NPU button disabled in Silicon Lab | Readiness → **Download & register EPs**, and check vendor NPU drivers. |
| Execution provider catalog says `Value cannot be null. (Parameter 'source')` | Rebuild and restart. Some runtime/device combinations return a null provider collection; the bridge now treats this as no additional certified providers, not an error. CPU and registered DirectML devices still work. A VM with Hyper-V/remote display adapters and no exposed NPU cannot gain one by downloading EPs. Actual catalog errors are shown separately with their details. |
| First NPU run is slow | The execution provider compiles the model once and caches it. Run each device once before the audience arrives. |
| winml-cli not found | Run `setup.ps1`, or set `WinmlCli:Command` in `appsettings.local.json`. |
| Port 5080 busy | `start-demo.ps1 -Port 5090` |
| NuGet restore complains about versions | Run `winapp init` in `bridge/` to refresh the Windows App SDK / BuildTools references, keeping this `Package.appxmanifest`. |
| `NU1301` / TLS error reaching `api.nuget.org` | The PC can't reach NuGet. Use the offline bundle (see *Offline / air-gapped PCs*), or if packages are already cached run `dotnet run --no-restore` in `bridge/`. |
| Foundry Local Load fails with `PluginExecutionProviderFactory::CreateProvider` (e.g. an `-openvino-npu` / `-openvino-gpu` / `-qnn-npu` variant) | Two different causes. **If the message contains `NOT_IMPLEMENTED … OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available in this build`:** this is a known upstream Foundry Local bug (microsoft/foundry-local #1237, #1241, #1261): the OpenVINO provider package that Foundry Local gets from Windows ML (1.8.95.0) does not match its ONNX Runtime 1.31, so OpenVINO NPU/GPU variants fail even though the provider is installed, online or offline. Foundry Local has **no setting to use a different provider**, so a newer one can't be put inside it; the **OpenVINO sideload** (above) runs the model outside Foundry with the newer provider instead. On 5 Nov 2026 a Microsoft maintainer said a fix was planned within about two weeks and suggested downgrading to Foundry Local **0.11.0** meanwhile (`winget install --id Microsoft.FoundryLocal --version 0.11.0.0 --exact`). A user confirmed that 0.11.0 loads these models with this app; the catalog/cache commands the app uses are written for 0.13, so if the catalog is empty on 0.11.0 use `foundry model download`. The Foundry Local card shows the installed version and flags 0.12/0.13. Use the **Load the CPU variant instead** button (or any `generic-cpu` variant) now, and update (`winget upgrade Microsoft.FoundryLocal`) once a fixed release is published. A reporter on #1261 found that the newer Python provider `onnxruntime-ep-openvino` 1.8.1 with `onnxruntime-genai` 0.19.0 runs the same cached model folders outside Foundry Local. **Otherwise** the provider is missing: while online click **Download & register EPs** on the Windows ML card and load again. The catalog and model list tag these variants "needs … EP". |
| Windows AI cards say `NotReady` (offline) | The models aren't on this PC yet. Click **Prepare** while online: Windows downloads them once through Windows Update, then they work offline. They can't be bundled. |
| Edge built-in AI row says `timeout` (often the Language Detector) | Edge didn't answer `availability()` within 8 s. The row updates by itself if Edge answers later; otherwise click **Re-check** or restart Edge. Use a current Edge Dev/Canary with the matching flag enabled, and check `edge://on-device-internals` and `edge://components`. The Language Detector's small model must be downloaded once while online. |
| Edge model download fails with `Model not available` | Edge cannot provide that model on this PC. Prompt/Summarizer need Edge Dev/Canary 138+, about 20 GB free on the profile drive and a device performance class of High (see `edge://on-device-internals`); the app shows the exact error and these requirements next to the row. Downloads must start from a click. |
| Windows AI cards say `NotSupportedOnCurrentSystem` although the PC has an NPU | The Windows AI APIs need a Copilot+ PC (NPU of 40+ TOPS, Windows 11 24H2+, supported region); smaller or older NPUs don't qualify. Foundry Local / Windows ML can still use such an NPU through vendor execution providers. |
| Something fails on another PC and the cause isn't clear | Run `scripts\diagnose.ps1` (it is in the offline bundle too) or click **Copy diagnostics** on the Readiness tab, and share the output. |
| Bridge log: `'foundry model list ...' did not finish within 30 s` / the Foundry card is empty while a model loads | Fixed in 3.1.3: the status page answers from the last known list and refreshes in the background, loads run one at a time, and the card shows "Loading …" while Foundry is busy. On a slow PC, raise `FoundryLocal:CliTimeoutSeconds` if the list still can't refresh; the warning on the card says when the list is stale. |
| Bridge exits with code `0xC0000409` right after clicking **GPU** / **Race all devices** (Silicon Lab), or `winml perf` crashes | The only GPU on this PC is virtual (Hyper-V video, Microsoft Basic Render Driver), and ONNX Runtime/DirectML aborts the process on it. Fixed in 3.1.4: virtual/software GPUs are treated as "no GPU" and the GPU button is disabled. Use CPU, or run on a PC with a real GPU or NPU. |
| `Port 5080 is already in use` / "Failed to bind to address" | Another copy (for example the dev bridge and the offline bundle) is running. Stop it, open its URL, or choose another port (`start-demo.ps1 -Port 5090`; `start-offline.ps1` picks the next free port itself). |
| Aion restore fails | Put the `.nupkg` in `bridge/nuget-local`, or build without `-p:EnableAion=true`. |

---

## Security and privacy notes

* Kestrel binds to loopback only (enforced at start-up). Requests with a non-local `Host` or a cross-site `Origin` are rejected (DNS-rebinding and CSRF guard).
* The web app loads nothing from the internet: no CDN, fonts or analytics. Model output is rendered with an escaping Markdown renderer that never creates links or images.
* Model catalog refreshes and explicit model / execution-provider downloads can use the internet through Foundry Local, Windows Update or the EP catalog. The catalog dialog makes this explicit; inference remains local. The bridge's egress counters cover its HTTP client calls, not network traffic performed by these external runtimes or CLI processes.
* The sample claim form is synthetic. Contoso is a fictitious company.

---

## MVP status and roadmap

**Tested:** the web app (all four tabs) and the API contract against the simulation bridge, plus the compiled .NET bridge on a Windows 11 VM (Windows ML, Foundry Local 0.13 loading/catalog/chat, the offline bundle) and regression checks in `tests\FoundryLocal.Tests`.
**Not verified on real Copilot+ hardware here:** the Windows AI language model / OCR / image description on an NPU, vendor execution providers (OpenVINO, QNN, …), and Edge's built-in AI downloads. Expect to adjust details there.

Next ideas:
1. **Voice memo → minutes**: record in the browser, transcribe with Foundry Local Whisper (`/v1/audio/transcriptions`), summarize on the NPU.
2. **Structured output**: use `LanguageModel.GenerateStructuredJsonResponseAsync` (WinAppSDK 2.3+) for the JSON extraction.
3. **LoRA A/B lane**: compare base Aion Instruct vs an adapter trained in Foundry Toolkit.
4. **Semantic file search** with App Content Search (Limited Access Feature).
5. **WebNN lane**: run the same ONNX model directly in the browser.
6. **One-click install**: package the bridge as a signed MSIX with `winapp pack`.
