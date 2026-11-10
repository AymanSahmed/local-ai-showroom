# Changelog

The version is shown in the UI header (`v3.x.y`). If the UI and the bridge differ, the header turns amber:
restart the bridge and hard-refresh (Ctrl+F5). Bump it for every change with `.\scripts\set-version.ps1 <x.y.z>`.

## 3.2.2
- **New:** the Foundry Local card shows the installed Foundry Local version (and a warning on 0.12/0.13, where OpenVINO NPU/GPU variants fail). It is also in **Copy diagnostics**, `diagnose.ps1` and `/api/status` (`foundry.version`).
- The OpenVINO load-error hint now lists the fixes that work: Foundry Local 0.11.0 (confirmed by a user), the OpenVINO sideload, or a `generic-cpu` variant.

## 3.2.1
- **Fix:** `start-offline.ps1` failed with "The process has no package identity" when the bundle sat in a OneDrive (cloud-synced) folder. Such folders now run from a local copy under `%LOCALAPPDATA%\LocalAIShowroom\offline-app` (the models and the sideload stay where they are). If the app still starts without identity from any other folder, the script retries from a local copy automatically. `-LocalCopy` forces it.

## 3.2.0
- **New: OpenVINO sideload.** Foundry Local 0.12/0.13 can't load `-openvino-npu` / `-openvino-gpu` models (upstream bug) and has no setting for another provider. The sideload runs the same cached model folders in a small Python worker (`onnxruntime-genai` 0.19.0 + `onnxruntime-ep-openvino` 1.8.1) started and managed by the bridge. Set it up once with `scripts\setup-openvino-sideload.ps1` (portable, about 400 MB), then use the new **OpenVINO sideload** card on the Readiness tab, or the **Run it with the OpenVINO sideload instead** button that appears after a Foundry OpenVINO load error (also in the model catalog).
- Loaded sideload models show up as `openvino:` engines in the Arena and Doc Desk, with the same streaming and metrics as Foundry. New endpoints `/api/openvino/models`, `/load`, `/unload`, `/chat`; the status and **Copy diagnostics** include the sideload.
- Offline bundle: `build-offline-bundle.ps1 -IncludeOpenVinoSideload` packs it; `start-offline.ps1` configures it.
- Verified here on the OpenVINO CPU device with a real model (load, streaming, cancel, concurrency, restart after a killed worker, unload). The NPU and GPU paths could not be tested on this PC (no Intel NPU/GPU) and need a try on real hardware.
- **Fix:** a status refresh that was already running when you loaded/unloaded something could leave the page showing the old state; a follow-up refresh now always runs.

## 3.1.5
- The OpenVINO load-failure message now carries what upstream has said: the provider package Foundry gets from Windows ML doesn't match its ONNX Runtime, Foundry Local has no setting for a different provider (so none can be sideloaded into it), Microsoft planned a fix within about two weeks of 5 Nov 2026 and suggested Foundry Local 0.11.0 meanwhile, and a newer Python provider was reported to run these models outside Foundry.

## 3.1.4
- **Fix (crash):** clicking **GPU** (or **Race all devices**) in Silicon Lab on a PC whose only GPU is virtual (Hyper-V video, Microsoft Basic Render Driver) killed the whole bridge with exit code `0xC0000409`: creating an ONNX Runtime/DirectML session on that adapter aborts the process natively. Such adapters are now detected (vendor id `0x1414`) and treated as "no GPU": the GPU button is disabled with an explanation, the race skips it, and the API refuses it with a clear message. Real GPUs are unaffected.
- **Fix:** `winml perf` with device `auto` could crash winml-cli's Python process for the same reason. On such a PC `auto` now runs on the CPU (and says so), `gpu` is refused, and a crash exit code gets an explanatory hint. The perf device list only offers hardware this PC really has.
- **Fix:** starting a second copy on a busy port (for example the offline bundle while the dev bridge runs) used to end in an unhandled-exception stack trace. The bridge now says which port is in use and how to pick another, and `start-offline.ps1` automatically uses the next free port.
- Windows ML devices in `/api/status` now include the hardware vendor and a `virtualGpu` flag, and `virtualGpuOnly` for the PC.

## 3.1.3
- **Fix:** `foundry model list ... did not finish within 30 s` and an empty or broken Foundry card while models load. The status page no longer runs Foundry CLI commands on every refresh: it answers from the last known model list and refreshes it in the background, so a slow or busy Foundry can no longer blank the list or block the page. If a refresh fails the last list stays, with a "showing the last known list" note.
- **Fix:** loads are serialized. Two Arena lanes (or two Load clicks) used to load two models at the same moment; now they queue, which is faster and safer on PCs with little memory.
- **Fix:** while a model loads or downloads, the Foundry card shows "Loading …" (status Busy) instead of querying the busy service.
- **Fix:** a model that loaded successfully is no longer reported as failed just because the follow-up check timed out. The CLI timeout for model lists is now 90 s (`FoundryLocal:CliTimeoutSeconds`), the list cache lasts 15 s (`FoundryLocal:InventoryTtlSeconds`), and the unavailable legacy load API is only tried once.

## 3.1.2
- Foundry models that fail to load because of an execution-provider problem (such as the OpenVINO issue) are now marked unavailable in the Arena and Doc Desk engine pickers for the rest of the browser session, so they stop being chosen automatically. They're cleared as soon as one loads successfully.

## 3.1.1
- **Fix:** the OpenVINO NPU/GPU load error (`OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available in this build`) was explained wrongly as a missing provider. It is a known upstream Foundry Local issue (ONNX Runtime 1.31.0 vs the OpenVINO provider; microsoft/foundry-local #1237, #1241, #1261), so the message now says that and no longer sends you to download providers.
- **New:** after a hardware variant fails to load, a one-click **Load the CPU variant instead** button (downloads it first if needed), on the Foundry card and in the model catalog.
- OpenVINO variants in the lists carry a tooltip about the known issue.

## 3.1.0
- **New:** `diagnose.ps1` (also inside the offline bundle) writes a report of the PC (OS build, NPU and driver, Windows App Runtime, Windows AI components, execution providers, Foundry Local version/cache/models/log, and the bridge status) with your user and PC names removed. Add `-TryPrepare` to also try preparing the Windows AI features.
- **New:** "Copy diagnostics" button on the Readiness tab.
- Windows AI `NotSupportedOnCurrentSystem` now explains that an NPU alone isn't enough: the PC must be a Copilot+ PC.
- Foundry: an old CLI (before 0.13) gets a clear "update Foundry Local" hint; `start-offline.ps1` shows the Foundry version and the models it can see, and restarts the daemon when it switches to the bundled model cache.

## 3.0.3
- Edge built-in AI rows now explain themselves. A row that is *unavailable*, *timeout* or *error* shows what Edge needs (version, flag, disk space, device performance class, `edge://on-device-internals`) and has a **Re-check** button.
- A probe that timed out and answers later (for example after Edge finishes checking) now updates its row automatically.
- Failed model downloads show Edge's exact error plus a hint (for example for "Model not available") and appear immediately instead of after the next refresh.
- The Translator panel also gets a hint and Re-check when Edge doesn't answer.

## 3.0.2
- **Fix:** the status page no longer hangs on "Bridge: checking…" when a browser's built-in AI API never answers; each Edge probe now times out after 8 s and the bridge status shows immediately.
- **Fix:** `start-offline.ps1` handles a stale or older package registration instead of failing.
- Windows error messages that Windows repeats twice are shown once; the Doc Desk text no longer claims OCR always runs on the NPU.

## 3.0.1
- **Fix:** Model Arena / Doc Desk no longer fail on a downloaded Foundry model that isn't loaded yet. The bridge loads it once and retries; load time is excluded from first-token timings.
- **Fix:** Document OCR works on PCs without an NPU (or before Windows AI is provisioned) by falling back to the built-in Windows OCR engine on the CPU.
- **New:** Doc Desk translation language picker (was Arabic only).
- Smoke test checks that the VERSION file, the UI files and the bridge agree.

## 3.0.0
- Version number in the UI header (UI version, plus the bridge version with a mismatch warning).
- Foundry model catalog browser with search, filters, download and load; works with Foundry Local 0.13.
- Clear diagnostics for missing execution providers and un-provisioned Windows AI features; "needs … EP" tags.
- Edge built-in AI: Download buttons for the Prompt, Summarizer and Language Detector models; Translator panel with any language pair.
- Execution provider catalog no longer reports a null-source error.
- Offline bundle (`scripts\build-offline-bundle.ps1`, `start-offline.ps1`): self-contained, no NuGet/SDK needed on the target PC.
- Browsers revalidate the UI files instead of reusing stale JavaScript.
