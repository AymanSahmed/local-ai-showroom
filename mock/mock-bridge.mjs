#!/usr/bin/env node
// Local AI Showroom - SIMULATION bridge.
// Serves the same web UI and the same API shape as the real .NET bridge, with canned answers and
// plausible timings, so the demo flow can be rehearsed on any machine (no Windows, NPU or models needed).
// The UI shows a "Simulation mode" banner whenever this server is used.
//
//   node mock/mock-bridge.mjs            -> http://127.0.0.1:5080
//   node mock/mock-bridge.mjs 5090       -> custom port

import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', 'bridge', 'wwwroot');
const VERSION = (await readFile(resolve(here, '..', 'VERSION'), 'utf8')).trim();
const PORT = Number(process.argv[2] || process.env.PORT || 5080);
const HOST = '127.0.0.1';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.json': 'application/json; charset=utf-8',
};

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const jitter = (base, spread) => Math.max(0.05, base + (Math.random() * 2 - 1) * spread);

// ------------------------------------------------------------------------------------------- simulated state
const sim = {
  localCalls: 0,
  imageDescription: 'NotReady',
  sessions: new Set(),
  models: [
    { id: 'phi-4-mini-instruct-qnn-npu', device: 'NPU', loaded: true, cached: true },
    { id: 'qwen2.5-1.5b-instruct-generic-gpu', device: 'GPU', loaded: true, cached: true },
    { id: 'qwen2.5-0.5b-instruct-generic-cpu', device: 'CPU', loaded: false, cached: true },
    { id: 'phi-4-mini-instruct-openvino-npu:4', device: 'NPU', loaded: false, cached: true },
  ],
  // OpenVINO sideload: model variants Foundry Local can't load but the sideload can.
  ovInstalled: true,
  ovModels: [
    { id: 'phi-4-mini-instruct-openvino-npu:4', device: 'NPU', loaded: false },
    { id: 'DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2', device: 'GPU', loaded: false },
  ],
};

function openvinoStatus() {
  const loaded = sim.ovModels.find((m) => m.loaded);
  return {
    installed: sim.ovInstalled,
    root: 'C:\\Users\\demo\\LocalAIShowroom\\openvino-sideload',
    pythonFound: sim.ovInstalled,
    packagesFound: sim.ovInstalled,
    workerFound: sim.ovInstalled,
    setup: 'scripts\\setup-openvino-sideload.ps1',
    loaded: loaded?.id,
    busy: false,
    models: sim.ovInstalled ? sim.ovModels.map((m) => ({ ...m })) : [],
  };
}

function catalog() {
  const variants = [
    ...sim.models.map((m) => ({ ...m, alias: m.id.split('-instruct')[0], task: 'Chat', sizeMb: 900, license: 'MIT' })),
    { id: 'qwen3-0.6b-generic-cpu:4', alias: 'qwen3-0.6b', device: 'CPU', task: 'Chat', sizeMb: 600, license: 'apache-2.0' },
    { id: 'phi-4-mini-instruct-generic-cpu:5', alias: 'phi-4-mini', device: 'CPU', task: 'Chat', sizeMb: 4915, license: 'MIT' },
  ];
  return { models: [...new Map(variants.map((m) => [m.id, {
    ...m, cached: sim.models.some((local) => local.id === m.id), loaded: sim.models.some((local) => local.id === m.id && local.loaded),
  }])).values()] };
}

function status() {
  return {
    app: 'Local AI Showroom',
    version: VERSION,
    simulated: true,
    time: new Date().toISOString(),
    machine: {
      os: 'Microsoft Windows 10.0.26200 (simulated)',
      build: 26200,
      osArchitecture: 'Arm64',
      processArchitecture: 'Arm64',
      cpu: 'Snapdragon(R) X Elite - X1E80100 (simulated)',
      logicalCores: 12,
      memoryGb: 31.6,
      dotnet: '.NET 10 (simulated)',
      developerMode: true,
    },
    windowsAI: {
      hasIdentity: true,
      packageFamilyName: 'LocalAIShowroom.Bridge_simulated',
      runtimeVersion: '2.6.5 (simulated)',
      features: [
        { id: 'languageModel', name: 'Language model (Phi Silica / Aion Instruct)', state: 'Ready' },
        { id: 'textRecognizer', name: 'Text recognition (OCR)', state: 'Ready' },
        { id: 'imageDescription', name: 'Image description', state: sim.imageDescription },
      ],
    },
    windowsML: {
      providers: [{ name: 'QNNExecutionProvider', readyState: 'Ready', vendor: 'Qualcomm' }],
      devices: [
        { ep: 'QNNExecutionProvider', type: 'NPU', vendor: 'Qualcomm' },
        { ep: 'DmlExecutionProvider', type: 'GPU', vendor: 'DirectML (any GPU)' },
        { ep: 'CPUExecutionProvider', type: 'CPU', vendor: 'CPU' },
      ],
      hasNpu: true,
      hasGpu: true,
      model: {
        configuredPath: 'C:\\Users\\demo\\LocalAIShowroom\\models\\resnet50',
        path: 'C:\\Users\\demo\\LocalAIShowroom\\models\\resnet50\\model.onnx',
        found: true,
        sizeMb: 24.6,
      },
      sessions: [...sim.sessions].map((d) => ({ device: d, provider: PROVIDER[d], createMs: SESSION_MS[d] })),
    },
    foundry: { running: true, endpoint: 'http://127.0.0.1:39839', cliFound: true, version: '0.13.0', models: sim.models },
    openvino: openvinoStatus(),
    aion: {
      compiled: false,
      loaded: false,
      note: 'Optional. Install the Aion Instruct Preview framework, put the SDK .nupkg in bridge/nuget-local and run: dotnet run -p:EnableAion=true',
    },
    winmlCli: { command: 'winml (simulated)', found: true },
    privacy: { listensOn: 'loopback only', localCalls: sim.localCalls, blockedCloudCalls: 0 },
  };
}

// ------------------------------------------------------------------------------------------- canned content
const CLAIM_JSON = {
  document_type: 'Motor claim notification form',
  reference_numbers: { claim: 'CL-2026-10457', policy: 'MTR-88-301-552', police_report: '7731/2026' },
  people: ['Mona Adel Farouk'],
  organizations: ['Contoso Insurance', 'Contoso partner garage, Nasr City'],
  dates: { incident: '2026-10-28T18:40', signed: '2026-10-29', received: '2026-10-30' },
  amounts: [{ value: 48500, currency: 'EGP', description: 'Estimated repair cost' }],
  location: 'Ring Road, near Exit 12, New Cairo',
  summary: 'Rear-end collision while stopped at a traffic light; rear bumper, trunk lid and left tail light damaged; no injuries.',
  requested_action: 'Approve repair at the Contoso partner garage in Nasr City and provide a replacement car for 5 days.',
};

const ANSWERS = [
  {
    test: /extract these fields/i,
    text: JSON.stringify(CLAIM_JSON),
  },
  {
    test: /apology|email/i,
    text: [
      '**Subject:** Update on your laptop repair',
      '',
      'Dear customer,',
      '',
      'We are sorry for the delay in repairing your laptop. A replacement part is arriving later than planned, so the repair will take 3 more days. We will contact you as soon as your device is ready. Thank you for your patience and understanding.',
      '',
      'Kind regards,',
      'Customer Care Team',
      '',
      '---',
      '',
      '**الموضوع:** تحديث بشأن إصلاح جهازك المحمول',
      '',
      'عزيزي العميل،',
      '',
      'نعتذر عن التأخير في إصلاح جهازك المحمول. نظرًا لتأخر وصول إحدى القطع، سيستغرق الإصلاح ثلاثة أيام إضافية. سنتواصل معك فور جاهزية الجهاز، ونشكرك على صبرك وتفهّمك.',
      '',
      'مع خالص التحيات،',
      'فريق خدمة العملاء',
    ].join('\n'),
  },
  {
    test: /professional translator|translate the user text/i,
    text: [
      'شركة كونتوسو للتأمين — نموذج إخطار مطالبة مركبة',
      '',
      'رقم المطالبة: CL-2026-10457',
      'رقم الوثيقة: MTR-88-301-552',
      'حامل الوثيقة: منى عادل فاروق',
      'تاريخ الحادث: 28 أكتوبر 2026، الساعة 18:40',
      'الموقع: الطريق الدائري، بالقرب من المخرج 12، القاهرة الجديدة',
      'المركبة: تويوتا كورولا 2022، لوحة ABC 1234',
      'التكلفة التقديرية للإصلاح: 48,500 جنيه مصري',
      '',
      'وصف الحادث: أثناء التوقف عند إشارة المرور، اصطدمت شاحنة توصيل بالمركبة المؤمَّن عليها من الخلف. تضرر المصد الخلفي وغطاء صندوق الأمتعة والمصباح الخلفي الأيسر، ولم تُسجَّل أي إصابات. محضر الشرطة رقم 7731/2026 مرفق. يطلب حامل الوثيقة الموافقة على إصلاح السيارة في ورشة كونتوسو المعتمدة بمدينة نصر وتوفير سيارة بديلة لمدة 5 أيام.',
    ].join('\n'),
  },
  {
    test: /ticket/i,
    text: '[{"id":1,"category":"software","urgency":"high"},{"id":2,"category":"access","urgency":"medium"},{"id":3,"category":"software","urgency":"medium"},{"id":4,"category":"hardware","urgency":"low"}]',
  },
  {
    test: /cfo|npu/i,
    text: [
      '- **Lower cost:** AI runs on hardware the company already owns, so everyday features carry no per-token cloud bill.',
      '- **Privacy by default:** prompts and documents stay on the managed laptop, which simplifies compliance for confidential data.',
      '- **All-day battery:** the NPU does AI work at a fraction of the CPU or GPU power draw, so AI features can stay on all day.',
    ].join('\n'),
  },
  {
    test: /training room|sessions? last/i,
    text: [
      '1. Sessions needed: 23 ÷ 8 = 2.9, so **3 sessions**.',
      '2. Session 1: 9:00–9:45, break until 10:00.',
      '3. Session 2: 10:00–10:45, break until 11:00.',
      '4. Session 3: 11:00–11:45.',
      '',
      'The last session ends at **11:45**.',
    ].join('\n'),
  },
  {
    test: /question:/i,
    text: 'The document states an estimated repair cost of **EGP 48,500** for the Toyota Corolla 2022 (plate ABC 1234).',
  },
  {
    test: /generative ai only through approved/i,
    text: [
      '- Use generative AI only through approved, company-managed applications.',
      '- Never put confidential customer data into consumer AI services, and have a person review AI output before it is shared externally.',
      '- Prefer on-device AI for Confidential documents because the data stays on the managed device; violations go to the security team.',
    ].join('\n'),
  },
  {
    test: /summar/i,
    text: [
      '- Motor claim **CL-2026-10457** from **Mona Adel Farouk** (policy MTR-88-301-552).',
      '- Rear-end collision on **28 Oct 2026** on Ring Road near Exit 12, New Cairo; no injuries.',
      '- Rear bumper, trunk lid and left tail light damaged; estimated repair **EGP 48,500**.',
      '- Police report 7731/2026 attached.',
      '- Requests repair at the Contoso partner garage in Nasr City plus a replacement car for 5 days.',
    ].join('\n'),
  },
  {
    test: /rewrite|clear, polite/i,
    text: 'On 28 October 2026 at 18:40, while the insured car was stopped at a traffic light on Ring Road near Exit 12 in New Cairo, a delivery van hit it from behind. The rear bumper, trunk lid and left tail light were damaged, and nobody was injured. Police report 7731/2026 is attached. Ms. Mona Adel Farouk asks Contoso Insurance to approve the repair (estimated at EGP 48,500) at the partner garage in Nasr City and to provide a replacement car for five days.',
  },
];

function cannedAnswer(prompt) {
  const hit = ANSWERS.find((a) => a.test.test(prompt));
  return hit
    ? hit.text
    : 'This is a **simulated** answer. On a Copilot+ PC the real bridge streams this from the selected on-device engine, and the timings below come from the actual run.';
}

const PROFILE = {
  winai: { ttft: 180, perWord: 14 },
  aion: { ttft: 160, perWord: 12 },
  NPU: { ttft: 320, perWord: 20 },
  GPU: { ttft: 240, perWord: 16 },
  CPU: { ttft: 650, perWord: 55 },
  Auto: { ttft: 400, perWord: 30 },
};

// Simulated OCR result that matches the synthetic sample scan drawn by js/sample-scan.js.
function sampleOcr() {
  const lines = [];
  const add = (text, x, baseline, size) => {
    const height = size * 1.15;
    const top = baseline - size * 0.9;
    let cx = x;
    const words = text.split(' ').map((w) => {
      const width = w.length * size * 0.52;
      const box = [cx, top, cx + width, top, cx + width, top + height, cx, top + height];
      cx += width + size * 0.28;
      return { text: w, confidence: Number((0.82 + Math.random() * 0.17).toFixed(3)), box };
    });
    lines.push({ text, box: [x, top, cx, top, cx, top + height, x, top + height], words });
  };
  add('CONTOSO INSURANCE', 185, 135, 46);
  add('Motor Claim Notification Form', 185, 178, 30);
  const fields = [
    ['Claim number:', 'CL-2026-10457'],
    ['Policy number:', 'MTR-88-301-552'],
    ['Policyholder:', 'Mona Adel Farouk'],
    ['Phone:', '+20 100 555 0199'],
    ['Date of incident:', '28 October 2026, 18:40'],
    ['Location:', 'Ring Road, near Exit 12, New Cairo'],
    ['Vehicle:', 'Toyota Corolla 2022, plate ABC 1234'],
    ['Estimated repair cost:', 'EGP 48,500'],
  ];
  let y = 290;
  for (const [label, value] of fields) {
    add(label, 100, y, 28);
    add(value, 470, y, 30);
    y += 72;
  }
  y += 30;
  add('Description of the incident', 100, y, 30);
  y += 50;
  for (const line of [
    'While stopped at a traffic light, the insured vehicle was hit from behind by a delivery van.',
    'The rear bumper, trunk lid and left tail light are damaged. No injuries were reported.',
    'Police report no. 7731/2026 is attached. The policyholder requests approval to repair the car',
    'at the Contoso partner garage in Nasr City and a replacement car for 5 days.',
  ]) {
    add(line, 100, y, 28);
    y += 44;
  }
  y += 70;
  add('Signature of policyholder:', 100, y, 28);
  add('Date: 29/10/2026', 860, y, 28);
  add('RECEIVED', 835, 1578, 34);
  add('30 OCT 2026', 862, 1612, 22);
  return { width: 1240, height: 1754, lines };
}

const PROVIDER = { npu: 'QNNExecutionProvider', gpu: 'DmlExecutionProvider', cpu: 'CPUExecutionProvider', auto: 'Auto (PREFER_NPU policy)' };
const LATENCY = { npu: 1.9, gpu: 4.7, cpu: 21.5, auto: 2.0 };
const SESSION_MS = { npu: 2380, gpu: 910, cpu: 140, auto: 2410 };
const LABEL_SETS = [
  ['coffee mug', 'cup', 'espresso', 'water jug', 'teapot'],
  ['computer keyboard, keypad', 'space bar', 'typewriter keyboard', 'mouse, computer mouse', 'laptop, laptop computer'],
  ['banana', 'lemon', 'corn', 'spaghetti squash', 'orange'],
  ['notebook, notebook computer', 'laptop, laptop computer', 'desktop computer', 'screen, CRT screen', 'monitor'],
];

// ------------------------------------------------------------------------------------------- http helpers
function send(res, code, body, type = 'application/json; charset=utf-8') {
  res.writeHead(code, { 'Content-Type': type, 'Cache-Control': 'no-store' });
  res.end(typeof body === 'string' || Buffer.isBuffer(body) ? body : JSON.stringify(body));
}

async function readBody(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const raw = Buffer.concat(chunks).toString('utf8');
  if (!raw) return {};
  try {
    return JSON.parse(raw);
  } catch {
    const err = new Error('Body must be JSON.');
    err.status = 400;
    throw err;
  }
}

async function streamAnswer(req, res, { engine, model, device, prompt, profile }) {
  res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache' });
  let closed = false;
  req.on('close', () => { closed = true; });
  const write = (o) => { if (!closed) res.write(`data: ${JSON.stringify(o)}\n\n`); };
  const t0 = Date.now();
  write({ type: 'start', engine, model, device });
  await sleep(jitter(profile.ttft, profile.ttft * 0.2));
  const tokens = cannedAnswer(prompt).split(/(\s+)/);
  let count = 0;
  for (const t of tokens) {
    if (closed) return;
    if (!t) continue;
    write({ type: 'delta', text: t });
    if (/\S/.test(t)) {
      count += 1;
      await sleep(jitter(profile.perWord, profile.perWord * 0.3));
    }
  }
  write({
    type: 'done',
    status: 'Complete',
    complete: true,
    usage: engine === 'Foundry Local' ? { prompt_tokens: Math.round(prompt.length / 4), completion_tokens: Math.round(count * 1.3) } : undefined,
    serverMs: Date.now() - t0,
  });
  res.end();
}

// ------------------------------------------------------------------------------------------- routes
async function api(req, res, path) {
  sim.localCalls += 1;
  const body = req.method === 'POST' ? await readBody(req) : {};

  switch (`${req.method} ${path}`) {
    case 'GET /api/ping':
      return send(res, 200, { ok: true, app: 'Local AI Showroom', version: VERSION, simulated: true });

    case 'GET /api/status':
      return send(res, 200, status());

    case 'POST /api/windows-ai/prepare':
      await sleep(1200);
      if (body.feature === 'imageDescription') sim.imageDescription = 'Ready';
      return send(res, 200, { feature: body.feature, result: 'Success', ms: 1200, status: status().windowsAI });

    case 'POST /api/windows-ai/generate':
      if (!body.prompt) return send(res, 400, { error: 'Prompt is required.' });
      return streamAnswer(req, res, {
        engine: 'Windows AI APIs', model: 'Built-in language model', device: 'NPU',
        prompt: `${body.system ?? ''}\n${body.prompt}`, profile: PROFILE.winai,
      });

    case 'POST /api/aion/generate':
      return send(res, 500, { error: 'This bridge was built without the Aion Instruct Preview lane. Rebuild with: dotnet run -p:EnableAion=true' });

    case 'POST /api/windows-ai/summarize':
    case 'POST /api/windows-ai/rewrite': {
      if (!body.text) return send(res, 400, { error: 'Text is required.' });
      const ms = Math.round(jitter(path.endsWith('summarize') ? 900 : 1300, 200));
      await sleep(ms);
      const skill = path.endsWith('summarize') ? 'summarize' : 'rewrite';
      return send(res, 200, {
        engine: `Windows AI · ${skill === 'summarize' ? 'TextSummarizer' : 'TextRewriter'}`,
        device: 'NPU', status: 'Complete', text: cannedAnswer(skill === 'summarize' ? 'summarize' : 'rewrite'), ms,
      });
    }

    case 'POST /api/windows-ai/ocr': {
      if (!body.image) return send(res, 400, { error: 'An image is required.' });
      const ms = Math.round(jitter(140, 30));
      await sleep(ms);
      const o = sampleOcr();
      return send(res, 200, {
        engine: 'Windows AI · TextRecognizer', device: 'NPU', width: o.width, height: o.height, ms,
        lineCount: o.lines.length, text: o.lines.map((l) => l.text).join('\n'), lines: o.lines,
      });
    }

    case 'POST /api/windows-ai/describe': {
      if (sim.imageDescription !== 'Ready') {
        return send(res, 500, { error: 'Image description is not ready yet. Click Prepare on the Readiness tab.' });
      }
      const ms = Math.round(jitter(1600, 300));
      await sleep(ms);
      return send(res, 200, {
        engine: 'Windows AI · ImageDescription', device: 'NPU', kind: body.kind ?? 'brief', status: 'Complete', ms,
        description: 'A scanned motor-insurance claim form from Contoso Insurance. It has a title, eight labelled fields with handwritten-style values, a paragraph describing a rear-end collision, a signature line and a red RECEIVED stamp dated 30 Oct 2026.',
      });
    }

    case 'GET /api/foundry/models':
      return send(res, 200, status().foundry);

    case 'GET /api/foundry/catalog':
      return send(res, 200, catalog());

    case 'POST /api/foundry/download': {
      if (typeof body.model !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:\-]{0,199}$/.test(body.model)) {
        return send(res, 400, { error: 'Invalid model id.' });
      }
      const m = catalog().models.find((x) => x.id === body.model);
      if (!m) return send(res, 400, { error: 'Unknown model.' });
      await sleep(600);
      if (!sim.models.some((x) => x.id === m.id)) sim.models.push({ id: m.id, device: m.device, cached: true, loaded: false });
      return send(res, 200, { model: m.id, cached: true, ms: 600 });
    }

    case 'POST /api/foundry/start':
      await sleep(800);
      return send(res, 200, { started: true, endpoint: 'http://127.0.0.1:39839' });

    case 'POST /api/foundry/load': {
      const m = sim.models.find((x) => x.id === body.model);
      if (!m) return send(res, 400, { error: 'Unknown model.' });
      if (/openvino/i.test(m.id)) {
        await sleep(600);
        return send(res, 500, { error: `Foundry Local could not load '${m.id}' (CLI exit 1): {"error":{"code":"INTERNAL_ERROR","message":"... PluginExecutionProviderFactory::CreateProvider Error creating execution provider: [ONNXRuntimeError] : 9 : NOT_IMPLEMENTED : Experimental function OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available in this build"}} Hint: the OpenVINO execution provider is installed but does not match the ONNX Runtime inside this Foundry Local (known upstream issue: microsoft/foundry-local #1237, #1241, #1261).` });
      }
      await sleep(1500);
      m.loaded = true;
      return send(res, 200, { model: m.id, loaded: true, ms: 1500 });
    }

    case 'POST /api/foundry/chat': {
      const m = sim.models.find((x) => x.id === body.model);
      if (!m) return send(res, 400, { error: 'Pick a Foundry Local model first.' });
      const prompt = (body.messages ?? []).map((x) => x.content).join('\n');
      return streamAnswer(req, res, { engine: 'Foundry Local', model: m.id, device: m.device, prompt, profile: PROFILE[m.device] ?? PROFILE.Auto });
    }

    case 'GET /api/openvino/models':
      return send(res, 200, openvinoStatus());

    case 'POST /api/openvino/load': {
      if (!sim.ovInstalled) return send(res, 500, { error: 'The OpenVINO sideload is not installed. Run scripts\\setup-openvino-sideload.ps1 once (needs internet).' });
      const m = sim.ovModels.find((x) => x.id === body.model);
      if (!m) return send(res, 400, { error: `'${body.model}' is not a downloaded OpenVINO model on this PC.` });
      await sleep(1800);
      for (const other of sim.ovModels) other.loaded = other === m; // one model at a time
      return send(res, 200, { model: m.id, loaded: true, ms: 1800 });
    }

    case 'POST /api/openvino/unload': {
      const was = sim.ovModels.find((x) => x.loaded)?.id;
      for (const m of sim.ovModels) m.loaded = false;
      return send(res, 200, { unloaded: was });
    }

    case 'POST /api/openvino/chat': {
      const m = sim.ovModels.find((x) => x.id === body.model);
      if (!sim.ovInstalled || !m) return send(res, 400, { error: 'Pick an OpenVINO sideload model first.' });
      if (!m.loaded) {
        await sleep(1800);
        for (const other of sim.ovModels) other.loaded = other === m;
      }
      const prompt = (body.messages ?? []).map((x) => x.content).join('\n');
      return streamAnswer(req, res, { engine: 'OpenVINO (sideload)', model: m.id, device: m.device, prompt, profile: PROFILE[m.device] ?? PROFILE.Auto });
    }

    case 'POST /api/windows-ml/prepare-eps':
      await sleep(1500);
      return send(res, 200, { ms: 1500, status: status().windowsML });

    case 'POST /api/windows-ml/classify': {
      if (!body.image) return send(res, 400, { error: 'An image is required.' });
      const device = String(body.device ?? 'auto').toLowerCase();
      if (!PROVIDER[device]) return send(res, 400, { error: 'device must be auto, npu, gpu or cpu.' });
      const iterations = Math.min(200, Math.max(1, Number(body.iterations) || 10));
      const first = !sim.sessions.has(device);
      sim.sessions.add(device);
      const samples = Array.from({ length: iterations }, () => jitter(LATENCY[device], LATENCY[device] * 0.15));
      await sleep(Math.min(1500, (first ? 400 : 0) + samples.reduce((a, b) => a + b, 0)));
      const sorted = [...samples].sort((a, b) => a - b);
      const pct = (p) => sorted[Math.min(sorted.length - 1, Math.max(0, Math.ceil(p * sorted.length) - 1))];
      const labels = LABEL_SETS[String(body.image).length % LABEL_SETS.length];
      const probs = [0.81, 0.09, 0.04, 0.02, 0.01];
      return send(res, 200, {
        engine: 'Windows ML', device, provider: PROVIDER[device], model: 'model.onnx',
        modelPath: status().windowsML.model.path,
        input: { name: 'pixel_values', layout: 'NCHW', width: 224, height: 224 },
        sessionCreateMs: first ? SESSION_MS[device] : null,
        iterations,
        latencyMs: {
          avg: Number((samples.reduce((a, b) => a + b, 0) / samples.length).toFixed(3)),
          min: Number(sorted[0].toFixed(3)),
          p50: Number(pct(0.5).toFixed(3)),
          p90: Number(pct(0.9).toFixed(3)),
          max: Number(sorted[sorted.length - 1].toFixed(3)),
        },
        top: labels.map((label, i) => ({ label, probability: probs[i], index: i })),
      });
    }

    case 'POST /api/winml-cli/sys':
      await sleep(900);
      return send(res, 200, {
        exitCode: 0, ms: 900, command: 'winml sys --list-device --list-ep',
        output: [
          '(simulated output)',
          'Devices',
          '  NPU  Qualcomm(R) Hexagon(TM) NPU',
          '  GPU  Qualcomm(R) Adreno(TM) X1-85 GPU',
          '  CPU  Snapdragon(R) X Elite - X1E80100',
          '',
          'Execution providers',
          '  QNNExecutionProvider   npu, gpu   ready',
          '  DmlExecutionProvider   gpu        ready',
          '  CPUExecutionProvider   cpu        ready',
        ].join('\n'),
      });

    case 'POST /api/winml-cli/perf': {
      const device = String(body.device ?? 'auto').toLowerCase();
      const resolved = device === 'auto' ? 'npu' : device;
      const iterations = Number(body.iterations) || 50;
      await sleep(2200);
      const base = LATENCY[resolved] ?? 2;
      const v = (f) => Number((base * f).toFixed(2));
      const latency = { avg: v(1), p50: v(0.98), p90: v(1.11), p95: v(1.17), p99: v(1.3), min: v(0.92), max: v(1.42), std: v(0.06) };
      const throughput = Number((1000 / base).toFixed(2));
      return send(res, 200, {
        exitCode: 0, device, iterations, ms: 2200,
        command: `winml perf -m "C:\\Users\\demo\\LocalAIShowroom\\models\\resnet50\\model.onnx" --device ${device} --iterations ${iterations}`,
        reportPath: 'C:\\Users\\demo\\AppData\\Local\\Temp\\showroom-perf-simulated.json',
        summary: { device: resolved, latencyMs: latency, throughputPerSec: throughput },
        report: { simulated: true, device: resolved, iterations, latency_ms: latency, throughput_per_sec: throughput },
        output: [
          '(simulated output)',
          `Device:      ${resolved}`,
          'Precision:   auto',
          'Task:        image-classification',
          `Iterations:  ${iterations} (+ 10 warmup)`,
          'Batch Size:  1',
          '',
          'Latency (ms)  Avg    P50    P90    P95    P99    Min    Max    Std',
          `              ${Object.values(latency).map((x) => x.toFixed(2)).join('   ')}`,
          '',
          `Throughput: ${throughput} samples/sec`,
        ].join('\n'),
      });
    }

    default:
      return send(res, 404, { error: `No simulated endpoint for ${req.method} ${path}` });
  }
}

async function serveStatic(res, path) {
  const rel = decodeURIComponent(path === '/' ? '/index.html' : path);
  const file = resolve(webRoot, `.${rel}`);
  if (!file.startsWith(webRoot + sep)) return send(res, 403, 'Forbidden', 'text/plain');
  try {
    const data = await readFile(file);
    return send(res, 200, data, MIME[extname(file).toLowerCase()] ?? 'application/octet-stream');
  } catch {
    return send(res, 404, 'Not found', 'text/plain');
  }
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host || HOST}`);
  try {
    if (url.pathname.startsWith('/api/')) await api(req, res, url.pathname);
    else await serveStatic(res, url.pathname);
  } catch (err) {
    if (!res.headersSent) send(res, err.status || 500, { error: err.message });
    else res.end();
  }
});

server.listen(PORT, HOST, () => {
  console.log(`\n  Local AI Showroom - SIMULATION bridge`);
  console.log(`  UI ............ http://${HOST}:${PORT}`);
  console.log('  Answers and timings are canned; the UI shows a simulation banner.\n');
});
