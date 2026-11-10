# Demo script: "Your AI, on your PC" (12 minutes)

**Audience:** business and IT decision makers, ISVs and app owners.
**Message:** Windows now runs real AI workloads *on the device*, on the NPU, GPU or CPU. The data stays on the PC, and developers have four complementary ways to use it.

---

## Checklist

**The day before (good Wi-Fi)**
- [ ] `.\scripts\setup.ps1` and `.\scripts\prepare-models.ps1` finished without warnings.
- [ ] Readiness tab shows **5 / 5** (or at least Windows AI, Windows ML and Foundry Local ready).
- [ ] Image description is *Ready* (click **Prepare** if not; the model comes via Windows Update).
- [ ] Run every act once. This warms the NPU caches: first NPU sessions compile the model.
- [ ] Have a mug, banana or keyboard on the desk for the camera.

**One hour before**
- [ ] Plug in power and set the power mode to *Best performance*.
- [ ] `.\scripts\start-demo.ps1` opens the browser at `http://127.0.0.1:5080`.
- [ ] Browser zoom 110–125% for the projector. Dark theme for projectors, light for small rooms (◐ button).
- [ ] Close the Activity drawer and open it during the demo as a reveal.
- [ ] Plan B: if anything breaks, `.\scripts\start-demo.ps1 -Simulate` gives the same UI with canned answers (it shows a banner, so say so).

---

## Opening (1 min)

> "Everything you're about to see runs on this laptop. There's no Azure subscription in this demo and no internet needed. I'll prove that by turning Wi-Fi off halfway through."

Point at the **Cloud calls: 0** pill.

## Act 1 · Readiness (2 min)

1. Show the **meter** ("4 of 5 stacks ready") and the machine card: processor, NPU, Windows build.
2. Walk the cards left to right:
   * **Windows AI APIs**: "Built into Windows. No model to ship: language model, OCR, image description. From January, Aion Instruct replaces Phi Silica behind the same API, so apps keep working."
   * **Windows ML**: "Bring your own model. Windows downloads the right accelerator driver (the execution provider) for this chip: Qualcomm, Intel, AMD or NVIDIA."
   * **Foundry Local**: "Open models such as Phi, Qwen and DeepSeek behind the same OpenAI API developers already use, just on localhost."
   * **Edge built-in AI**: "Even the browser has a model."
3. *(Optional, App Assure angle)* "This page is also a quick readiness check for your fleet: which devices can run what."

## Act 2 · Model Arena (3 min)

1. Lanes: **Windows AI built-in model** vs **Foundry Local · Phi-4-mini (NPU)**. Add a third lane on CPU for contrast.
2. Preset **"NPU for a CFO"** → **Run**.
3. Point at **First token** and **tokens/s**, and the green *fastest first token* badge.
   > "Same question, three engines, all local. The NPU gives near-instant answers at a fraction of the power."
4. Preset **"Customer email (EN + AR)"**. Show the Arabic output. Local models handle multiple languages.

## Act 3 · Private Doc Desk (4 min), the hero moment

1. **Turn Wi-Fi off now** (Quick Settings). Say it out loud.
2. **Sample scan** → **Extract text · Windows AI OCR**. Point at the coloured word boxes (green means high confidence) and the timing (*lines · words · ms on NPU*).
3. **Summarize** (engine: Windows AI). This uses the built-in *TextSummarizer* skill.
4. Switch the engine to **Foundry Local** → **Extract fields → JSON**.
   > "That's structured data from a scanned form, ready for your claims system, and it never left the laptop."
5. **Translate to Arabic** → right-to-left output.
6. Ask: *"What is the estimated repair cost?"* → answered from the document only.
7. **Describe the image** (accessible). Mention accessibility use cases.
8. Open the **Activity** drawer: every call, engine, device and latency. Wi-Fi is still off.

## Act 4 · Silicon Lab (2 min)

1. **Use camera** and point it at the mug. **Classify** on **NPU**.
2. **Race all devices** shows the headline *"N× faster on NPU than on CPU"* and *"Same top answer on every device"*.
   > "This model was prepared with one command, `winml build`, and the same file runs on any vendor's NPU, GPU or CPU."
3. Optional: tick **Live classification** to show continuous on-device inference.
4. Optional: **Run winml perf** shows the official benchmark of the same file (P50/P90, throughput).

## Close (1 min)

> "Four ways to add AI to your Windows apps: **Windows AI APIs** when you want zero model management, **Foundry Local** for open models with an OpenAI-compatible API, **Windows ML** for your own models on any silicon, and the **browser APIs** for web apps. To build it, use **AI Dev Gallery** for samples and **Foundry Toolkit** for models, conversion and fine-tuning."

Turn Wi-Fi back on.

---

## Q&A prep

| Question | Answer |
|---|---|
| Does any data leave the PC? | No. The bridge only listens on and talks to localhost, and the page loads nothing from the internet. Only first-time model downloads use the network. |
| Which PCs support this? | Windows AI APIs need a Copilot+ PC (40+ TOPS NPU). Foundry Local and Windows ML also run on GPUs and CPUs, so any Windows 11 PC gets something. |
| What happens to Phi Silica? | Aion Instruct starts replacing it on Insider builds in Nov 2026 and retail devices in Jan 2027, behind the same `LanguageModel` API. Validate prompts and retrain LoRAs (Foundry Toolkit) for Aion. |
| Do we need a licence or token? | The Language Model API no longer needs a Limited Access Feature token (Windows App SDK 2.6.4+). Foundry Local and Windows ML are free to use; check each model's licence. |
| How accurate is the OCR or translation? | Good for drafts and triage, with a person reviewing the result. Content moderation is built into the Windows AI APIs. |
| Can we use our own model? | Yes. Convert it with winml-cli or Foundry Toolkit (Model Conversion), then run it with Windows ML. That's the Silicon Lab path. |
| How do we ship this in our app? | Package with MSIX (identity + `systemAIModels`), call the Windows App SDK APIs, and fall back to Foundry Local or the cloud where the hardware is missing. |

## If something goes wrong

| Problem | Recovery |
|---|---|
| A lane errors | Switch that lane to another engine. The other stacks are independent. |
| NPU is slow the first time | "It's compiling the model for this NPU, once." Then run it again. |
| Camera blocked | Use **Upload image** instead. |
| Bridge won't start | `start-demo.ps1 -Simulate` and say it's a recorded walkthrough of the same UI. |
