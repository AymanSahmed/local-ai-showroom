// Local AI Showroom - UI. Vanilla JS modules, no external dependencies (works offline).
import { getJSON, postJSON } from './api.js';
import { buildEngines, defaultEngine, runEngine } from './engines.js';
import * as edge from './edge-ai.js';
import { renderMarkdown, renderJson, extractJson } from './markdown.js';
import { makeSampleScan } from './sample-scan.js';
import { APP_VERSION } from './version.js';

// =============================================================================================== helpers
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props ?? {})) {
    if (value == null || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key === 'html') el.innerHTML = value; // only ever used with escaped markup
    else if (key.startsWith('on') && typeof value === 'function') el.addEventListener(key.slice(2), value);
    else if (value === true) el.setAttribute(key, '');
    else el.setAttribute(key, String(value));
  }
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const fmtMs = (ms) => {
  if (ms == null || Number.isNaN(ms)) return '—';
  if (ms < 10) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${Math.round(ms)} ms`;
  return `${(ms / 1000).toFixed(2)} s`;
};

const deviceClass = (d) => {
  const v = String(d ?? '').toLowerCase();
  if (v.includes('npu')) return 'npu';
  if (v.includes('gpu')) return 'gpu';
  if (v.includes('cpu')) return 'cpu';
  return 'muted';
};

const deviceOfModel = (id) => {
  const s = String(id).toLowerCase();
  if (/npu|qnn|vitis/.test(s)) return 'NPU';
  if (/gpu|cuda|dml|trt|rtx/.test(s)) return 'GPU';
  if (/cpu/.test(s)) return 'CPU';
  return 'Auto';
};

/** Hardware variants need a vendor execution provider (EP) that is downloaded once while online; generic CPU/DirectML variants do not. */
const epNeededFor = (id) => [[/openvino/i, 'OpenVINO'], [/qnn/i, 'Qualcomm QNN'], [/vitis/i, 'AMD VitisAI'], [/trt|tensorrt/i, 'NVIDIA TensorRT-RTX']]
  .find(([re]) => re.test(String(id)))?.[1] ?? null;
const epChip = (id) => {
  const ep = epNeededFor(id);
  if (!ep) return null;
  const known = ep === 'OpenVINO' ? ' Known Foundry Local issue (microsoft/foundry-local #1237, #1241, #1261): OpenVINO variants fail to load on 0.12/0.13 (Microsoft planned a fix and suggested 0.11.0 meanwhile); use a generic-cpu variant.' : '';
  return h('span', { class: 'chip warn', title: `Needs the ${ep} execution provider, downloaded once while online (Windows ML card > Download & register EPs). Without it, loading fails offline.${known}` }, `needs ${ep} EP`);
};

/** A CPU variant of the same model (same alias) to fall back to when a hardware variant won't load; downloaded ones first. */
const pickCpuAlternative = (models, failedId) => {
  const failed = models.find((m) => m.id === failedId);
  if (!failed) return null;
  return models
    .filter((m) => m.alias === failed.alias && m.device === 'CPU' && m.id !== failedId)
    .sort((a, b) => Number(b.cached) - Number(a.cached))[0] ?? null;
};
const looksLikeProviderFailure = (id, message) => !!epNeededFor(id) || /CreateProvider|execution provider|NOT_IMPLEMENTED/i.test(String(message));

/**
 * Models that failed to load because of an execution-provider problem. They are marked unavailable in the engine
 * pickers (so the Arena and Doc Desk don't keep choosing them) for the rest of this browser session.
 */
const brokenModels = (() => {
  try { return new Set(JSON.parse(sessionStorage.getItem('showroom-broken-models') ?? '[]')); } catch { return new Set(); }
})();
const saveBrokenModels = () => { try { sessionStorage.setItem('showroom-broken-models', JSON.stringify([...brokenModels])); } catch { /* storage unavailable */ } };
function markModelBroken(id, message) {
  if (!looksLikeProviderFailure(id, message) || brokenModels.has(id)) return;
  brokenModels.add(id);
  saveBrokenModels();
  rebuildEngines();
}
function clearModelBroken(id) {
  if (brokenModels.delete(id)) saveBrokenModels();
}

const OK_STATES = ['ready', 'available', 'complete', 'running', 'on', 'yes', 'found', 'success', 'built', 'loaded', 'local only'];
const WARN_STATES = ['notready', 'not ready', 'notpresent', 'downloadable', 'downloading', 'pending', 'not running', 'busy',
  'no models', 'no model', 'no devices', 'not found', 'blocked calls'];
const MUTED_STATES = ['missing', 'off', 'unknown', '', 'optional', 'not checked', 'not set up'];

function stateClass(state) {
  const v = String(state ?? '').toLowerCase();
  if (OK_STATES.includes(v)) return 'ok';
  if (WARN_STATES.includes(v)) return 'warn';
  if (MUTED_STATES.includes(v)) return 'muted';
  return 'bad';
}

const chip = (text, cls) => h('span', { class: `chip ${cls ?? stateClass(text)}` }, text);
const kv = (k, v) => h('div', { class: 'kv' }, h('div', { class: 'k' }, k), h('div', { class: 'v' }, v));
const stat = (k, v) => h('div', { class: 'stat' }, h('div', { class: 'k' }, k), h('div', { class: 'v' }, v));
const prettyLabel = (label) => String(label).split(',')[0].trim();
const shortPath = (p) => (p ? String(p).split(/[\\/]/).slice(-3).join('\\') : '');

function toast(message, kind = '') {
  const el = h('div', { class: `toast ${kind}` }, message);
  $('#toasts').append(el);
  setTimeout(() => el.remove(), kind === 'bad' ? 7000 : 4000);
}

async function withBusy(button, fn) {
  if (button) {
    button.disabled = true;
    button.classList.add('busy');
  }
  try {
    return await fn();
  } finally {
    if (button) {
      button.disabled = false;
      button.classList.remove('busy');
    }
  }
}

/** Re-renders streamed Markdown at most once per animation frame. */
function streamRenderer(el) {
  let pending = null;
  let frame = 0;
  return {
    push(text) {
      pending = text;
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        el.innerHTML = renderMarkdown(pending);
        el.classList.add('caret');
      });
    },
    finish(text) {
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
      el.classList.remove('caret');
      if (text != null) el.innerHTML = renderMarkdown(text);
    },
  };
}

function barRow(label, fraction, valueText, cls, title) {
  const pct = Math.max(1, Math.min(100, fraction * 100)).toFixed(1);
  return h('div', { class: 'bar-row', title: title || label },
    h('span', { class: 'label' }, label),
    h('div', { class: `bar ${cls || ''}` }, h('span', { style: `width:${pct}%` })),
    h('span', { class: 'val' }, valueText));
}

// =============================================================================================== state
const state = { status: null, statusError: null, edge: null, engines: [], bridgeUp: false, winmlSysOutput: null };

// Edge Translator picker. The panel is built once and reused so typing and focus survive the periodic status refresh.
const translator = { source: 'en', target: 'ar', progress: null, busy: false, token: 0, panel: null };
try {
  const saved = JSON.parse(localStorage.getItem('showroom-translator-pair') ?? 'null');
  const known = new Set(edge.TRANSLATOR_LANGUAGES.map(([code]) => code));
  if (saved && known.has(saved.source) && known.has(saved.target) && saved.source !== saved.target) {
    translator.source = saved.source;
    translator.target = saved.target;
  }
} catch { /* ignore unreadable preferences */ }

// =============================================================================================== activity
let activityCount = 0;

function logActivity({ stack, action, device, ms, ok = true }) {
  activityCount += 1;
  const list = $('#activity-list');
  const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  list.prepend(h('li', {},
    h('span', { class: 't' }, time),
    h('span', { class: 'stack' }, stack ?? ''),
    h('span', { class: 'what' }, action ?? ''),
    h('span', { class: 'dev' }, device ? chip(device, deviceClass(device)) : ''),
    h('span', { class: 'ms' }, ok ? fmtMs(ms) : 'failed')));
  while (list.children.length > 60) list.lastChild.remove();
  $('#activity-count').textContent = String(activityCount);
}

// =============================================================================================== tabs & theme
const TABS = ['readiness', 'arena', 'docdesk', 'lab'];

function showTab(name) {
  if (!TABS.includes(name)) name = 'readiness';
  for (const b of $$('.tabs [data-tab]')) b.setAttribute('aria-selected', String(b.dataset.tab === name));
  for (const p of $$('.tab-panel')) p.hidden = p.id !== `tab-${name}`;
  history.replaceState(null, '', `#${name}`);
  if (name !== 'lab' && lab.live) setLive(false);
  if (name === 'docdesk') requestAnimationFrame(drawOcrOverlay);
}

function initTabs() {
  for (const b of $$('.tabs [data-tab]')) b.addEventListener('click', () => showTab(b.dataset.tab));
  showTab(location.hash.slice(1) || 'readiness');
}

function initTheme() {
  const saved = localStorage.getItem('showroom-theme');
  if (saved) document.documentElement.dataset.theme = saved;
  $('#theme-toggle').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
    document.documentElement.dataset.theme = next;
    localStorage.setItem('showroom-theme', next);
  });
}

// =============================================================================================== status
let refreshing = null;
let refreshAgain = null;

function refreshStatus() {
  if (refreshing) {
    // A refresh is already running and may have read the status before the caller's change (a load, unload, prepare…).
    // Run one more right after it, shared by everyone who asks meanwhile, so the page always shows the new state.
    refreshAgain ??= refreshing.then(() => {
      refreshAgain = null;
      return refreshStatus();
    });
    return refreshAgain;
  }
  refreshing = (async () => {
    const pair = { source: translator.source, target: translator.target };
    const edgeProbe = edge.availability(pair).catch(() => null);
    try {
      state.status = await getJSON('/api/status');
      state.bridgeUp = true;
      state.statusError = null;
    } catch (err) {
      state.status = null;
      state.bridgeUp = false;
      state.statusError = err.message;
    }
    // Show the bridge status right away; the Edge probes can be slow and fill in afterwards.
    renderAll();

    state.edge = await edgeProbe;
    if (state.edge && (pair.source !== translator.source || pair.target !== translator.target)) {
      // The user changed the language pair while the probe was running.
      state.edge.translator = await edge.translatorAvailability(translator.source, translator.target);
    }
    renderAll();
  })().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

/** A plain-text report of this page and the bridge status, for sharing when something doesn't work. */
function buildDiagnostics() {
  const s = state.status;
  const lines = [
    `Local AI Showroom diagnostics · UI v${APP_VERSION} · bridge v${s?.version ?? 'not reached'}${s?.simulated ? ' (SIMULATION)' : ''}`,
    `Time: ${new Date().toISOString()}`,
    `Browser: ${navigator.userAgent}`,
    `Online: ${navigator.onLine}`,
    '',
  ];
  if (!s) {
    lines.push(`Bridge: not reachable (${state.statusError ?? 'no response'})`);
  } else {
    const m = s.machine ?? {};
    lines.push(
      `Machine: ${m.os ?? '?'} build ${m.build ?? '?'} · ${m.osArchitecture ?? '?'} · ${m.cpu ?? '?'} · ${m.memoryGb ?? '?'} GB · Developer Mode ${m.developerMode ? 'on' : 'off'}`,
      `Bridge process: ${m.processArchitecture ?? '?'} · ${m.dotnet ?? '?'} · package identity: ${s.windowsAI?.hasIdentity} (${s.windowsAI?.packageFamilyName ?? 'none'}) · Windows App Runtime ${s.windowsAI?.runtimeVersion ?? '?'}`,
      '', 'Windows AI features:',
      ...(s.windowsAI?.features ?? []).map((f) => `  ${f.name}: ${f.state}${f.error ? ` -> ${f.error}` : ''}`),
      ...(s.windowsAI?.error ? [`  error: ${s.windowsAI.error}`] : []),
      ...(state.windowsAiPrepareError ? [`  last Prepare error: ${state.windowsAiPrepareError.feature}: ${state.windowsAiPrepareError.message}`] : []),
      '', 'Windows ML:',
      `  devices: ${(s.windowsML?.devices ?? []).map((d) => `${d.type}/${d.ep}`).join(', ') || 'none'}`,
      `  providers: ${(s.windowsML?.providers ?? []).map((p) => `${p.name}=${p.readyState}`).join(', ') || 'none'}`,
      `  catalog error: ${s.windowsML?.catalogError ?? ''}  registration error: ${s.windowsML?.registrationError ?? ''}`,
      `  model: found=${s.windowsML?.model?.found} ${s.windowsML?.model?.path ?? ''}`,
      ...(s.windowsML?.error ? [`  error: ${s.windowsML.error}`] : []),
      '', 'Foundry Local:',
      `  running: ${s.foundry?.running} · version: ${s.foundry?.version ?? 'unknown'} · endpoint: ${s.foundry?.endpoint ?? ''} · CLI found: ${s.foundry?.cliFound}`,
      ...(s.foundry?.error ? [`  error: ${s.foundry.error}`] : []),
      ...(s.foundry?.warning ? [`  warning: ${s.foundry.warning}`] : []),
      ...(s.foundry?.busy ? [`  busy: ${s.foundry.operation ?? 'yes'}`] : []),
      ...(state.foundryLoadError ? [`  last load error: ${state.foundryLoadError}`] : []),
      ...(s.foundry?.models ?? []).map((x) => `  model: ${x.id} · ${x.device} · cached=${x.cached} loaded=${x.loaded}`),
      '', 'OpenVINO sideload:',
      `  installed: ${s.openvino?.installed} · root: ${s.openvino?.root ?? ''} · loaded: ${s.openvino?.loaded ?? 'none'}`,
      ...(s.openvino?.versions ? [`  versions: ${JSON.stringify(s.openvino.versions)}`] : []),
      ...(s.openvino?.error ? [`  error: ${s.openvino.error}`] : []),
      ...(state.ovLoadError ? [`  last load error: ${state.ovLoadError}`] : []),
      ...(s.openvino?.models ?? []).map((m) => `  model: ${m.id} · ${m.device} · loaded=${m.loaded}`),
      '', `winml-cli: ${JSON.stringify(s.winmlCli ?? {})}`,
    );
  }
  lines.push('', `Edge built-in AI: ${JSON.stringify(state.edge ?? {})}`);
  return lines.join('\n');
}

async function copyDiagnostics() {
  const text = buildDiagnostics();
  try {
    await navigator.clipboard.writeText(text);
    toast('Diagnostics copied. Paste them into your message.', 'ok');
  } catch {
    // Clipboard blocked: show the text so it can be copied by hand.
    const box = h('textarea', { rows: '16', readonly: true, 'aria-label': 'Diagnostics' }, text);
    const dialog = h('dialog', { class: 'catalog-dialog' }, h('h2', {}, 'Diagnostics'), h('p', { class: 'small muted' }, 'Select all (Ctrl+A) and copy (Ctrl+C).'), box,
      h('div', { class: 'row end' }, h('button', { class: 'btn small', onclick: () => dialog.close() }, 'Close')));
    dialog.addEventListener('close', () => dialog.remove());
    document.body.append(dialog);
    dialog.showModal();
    box.select();
  }
}

function rebuildEngines() {
  state.engines = buildEngines(state.status, state.edge, brokenModels);
  refreshEngineSelects();
}

function renderAll() {
  state.engines = buildEngines(state.status, state.edge, brokenModels);
  renderHeader();
  renderReadiness();
  refreshEngineSelects();
  renderLabDevices();
}

function setPill(el, text, cls) {
  el.textContent = text;
  el.className = `pill ${cls}`;
}

function renderVersion() {
  const pill = $('#pill-version');
  const bridge = state.status?.version;
  if (bridge && bridge !== APP_VERSION) {
    setPill(pill, `UI v${APP_VERSION} · bridge v${bridge}`, 'warn');
    pill.title = 'The web UI and the bridge are different versions. Restart the bridge and hard-refresh the page (Ctrl+F5).';
  } else {
    setPill(pill, `v${APP_VERSION}`, 'muted');
    pill.title = bridge ? `Local AI Showroom ${APP_VERSION} (UI and bridge match)` : `Web UI version ${APP_VERSION} (bridge not reached yet)`;
  }
}

function renderHeader() {
  const s = state.status;
  renderVersion();
  if (!state.bridgeUp) setPill($('#pill-bridge'), 'Bridge: offline', 'bad');
  else if (s.simulated) setPill($('#pill-bridge'), 'Simulation mode', 'warn');
  else setPill($('#pill-bridge'), 'Bridge: connected', 'ok');
  $('#sim-banner').hidden = !s?.simulated;

  const ml = s?.windowsML;
  const types = new Set((ml?.devices ?? []).filter((d) => !d.virtualGpu).map((d) => d.type));
  const silicon = ['NPU', 'GPU', 'CPU'].map((t) => `${t} ${types.has(t) ? '✓' : '–'}`).join(' · ');
  setPill($('#pill-silicon'), ml && !ml.error ? silicon : 'Silicon: unknown', types.has('NPU') ? 'ok' : 'muted');

  const p = s?.privacy;
  const privacy = $('#pill-privacy');
  if (p?.blockedCloudCalls > 0) setPill(privacy, `Cloud calls blocked: ${p.blockedCloudCalls}`, 'warn');
  else setPill(privacy, 'Cloud calls: 0', 'ok');
  if (p) privacy.title = `Calls to local runtimes: ${p.localCalls}. The bridge listens on ${p.listensOn} and can only call services on this PC.`;
}

// =============================================================================================== 1 · readiness
function stackReadiness(s) {
  const ml = s?.windowsML;
  return {
    windowsAI: (s?.windowsAI?.features ?? []).some((f) => f.state === 'Ready'),
    windowsML: !!(ml?.model?.found && (ml?.devices?.length ?? 0) > 0),
    foundry: !!(s?.foundry?.running && s.foundry.models?.length),
    edge: state.edge?.prompt === 'available',
    winmlCli: s?.winmlCli?.found === true,
  };
}

function renderMeter() {
  const values = Object.values(stackReadiness(state.status));
  const on = values.filter(Boolean).length;
  $('#ready-meter').replaceChildren(
    h('div', { class: 'meter-label' }, h('strong', {}, `${on} / ${values.length}`), ' local-AI stacks ready'),
    h('div', { class: 'segments' }, values.map((v) => h('span', { class: v ? 'on' : null }))));
}

function stackCard({ title, subtitle, status, body, foot }) {
  return h('div', { class: 'card stack-card' },
    h('div', { class: 'stack-title' },
      h('div', {}, h('h3', {}, title), h('div', { class: 'stack-sub' }, subtitle)),
      chip(status, `${stateClass(status)} dot`)),
    h('div', { class: 'stack-body' }, body),
    foot?.length ? h('div', { class: 'stack-foot' }, foot) : null);
}

function renderReadiness() {
  const s = state.status;
  const machine = $('#machine-card');
  const cards = $('#stack-cards');

  if (!s) {
    machine.replaceChildren(h('div', {},
      h('h3', {}, 'The local bridge is not reachable'),
      h('p', { class: 'muted' }, 'Start it from the bridge folder with ', h('code', {}, 'dotnet run'),
        ' (or rehearse with ', h('code', {}, 'node mock/mock-bridge.mjs'), '), then press Refresh.'),
      state.statusError ? h('p', { class: 'error-line' }, state.statusError) : null));
    cards.replaceChildren(edgeCard());
    renderMeter();
    return;
  }

  const m = s.machine ?? {};
  machine.replaceChildren(
    kv('Processor', m.cpu || '—'),
    kv('Windows', m.build ? `Build ${m.build} · ${m.osArchitecture ?? ''}` : m.os || '—'),
    kv('Memory', m.memoryGb ? `${m.memoryGb} GB` : '—'),
    kv('Bridge process', `${m.processArchitecture ?? '?'} · ${m.dotnet ?? ''}`),
    kv('Developer Mode', m.developerMode ? 'On' : 'Off'),
    kv('Package identity', s.windowsAI?.hasIdentity ? 'Yes' : 'No'));

  const active = document.activeElement;
  cards.replaceChildren(windowsAICard(s), windowsMLCard(s), foundryCard(s), openvinoCard(s), edgeCard(), winmlCliCard(s), aionCard(s), privacyCard(s));
  // The reused translator panel is detached and re-attached above, which drops focus.
  if (active && active !== document.body && document.contains(active)) active.focus({ preventScroll: true });
  renderMeter();
}

function windowsAICard(s) {
  const w = s.windowsAI ?? {};
  const body = [];
  if (w.error) body.push(h('div', { class: 'error-line' }, w.error));
  for (const f of w.features ?? []) {
    body.push(h('div', { class: 'feature-row' },
      h('div', {}, h('div', { class: 'feature-name' }, f.name), f.error ? h('div', { class: 'small muted' }, f.error) : null),
      chip(f.state),
      f.state === 'NotReady'
        ? h('button', { class: 'btn small', onclick: (e) => prepareFeature(f.id, e.currentTarget) }, 'Prepare')
        : h('span')));
  }
  const ready = (w.features ?? []).some((f) => f.state === 'Ready');
  if ((w.features ?? []).some((f) => f.state === 'NotReady')) {
    body.push(h('div', { class: 'howto' }, 'NotReady means the model is not on this PC yet. Click Prepare while online: Windows downloads it once through Windows Update, and it then works offline. Until then, document OCR uses the built-in Windows OCR engine on the CPU.'));
  }
  if ((w.features ?? []).some((f) => f.state === 'NotSupportedOnCurrentSystem')) {
    body.push(h('div', { class: 'howto' }, 'NotSupportedOnCurrentSystem: Windows says this PC cannot run the feature, even if it has an NPU. Phi Silica, text recognition and image description need a Copilot+ PC: an NPU of 40+ TOPS (Snapdragon X, Intel Core Ultra 200V, AMD Ryzen AI 300), Windows 11 24H2 or later with current updates, and a supported region. Smaller or older NPUs (for example first-generation Intel Core Ultra) do not qualify. Foundry Local and Windows ML can still use such an NPU through vendor execution providers.'));
  }
  if (state.windowsAiPrepareError) body.push(h('div', { class: 'error-line' }, `${state.windowsAiPrepareError.feature}: ${state.windowsAiPrepareError.message}`));
  const status = w.error ? 'Error' : ready ? 'Ready' : w.hasIdentity === false ? 'No identity' : 'Not ready';
  return stackCard({
    title: 'Windows AI APIs',
    subtitle: 'Windows App SDK · built-in models on the NPU',
    status,
    body,
    foot: [
      w.runtimeVersion ? h('span', { class: 'small muted' }, `Windows App Runtime ${w.runtimeVersion}`) : null,
      h('span', { class: 'small muted' }, 'Phi Silica → Aion Instruct: Insiders Nov 2026, retail Jan 2027'),
    ].filter(Boolean),
  });
}

async function prepareFeature(feature, button) {
  await withBusy(button, async () => {
    toast('Preparing the model. Windows may download it through Windows Update, which can take a few minutes.');
    try {
      const r = await postJSON('/api/windows-ai/prepare', { feature });
      state.windowsAiPrepareError = r.error ? { feature, message: [r.error, r.hresult, r.hint].filter(Boolean).join(' ') } : null;
      toast(r.error ? `Not ready: ${r.error}` : `${feature}: ${r.result} in ${fmtMs(r.ms)}`, r.error ? 'bad' : 'ok');
      logActivity({ stack: 'Windows AI', action: `EnsureReadyAsync · ${feature}`, ms: r.ms, ok: !r.error });
    } catch (e) {
      toast(e.message, 'bad');
    }
    await refreshStatus();
  });
}

function windowsMLCard(s) {
  const ml = s.windowsML ?? {};
  const body = [];
  if (ml.error) body.push(h('div', { class: 'error-line' }, ml.error));

  const devices = ml.devices ?? [];
  body.push(
    h('div', { class: 'small muted' }, 'Execution devices registered with ONNX Runtime'),
    h('div', { class: 'chips' }, devices.length
      ? devices.map((d) => h('span', {
        class: `chip ${d.virtualGpu ? 'muted' : deviceClass(d.type)}`,
        title: d.virtualGpu ? 'Virtual or software adapter (Hyper-V video / Basic Render Driver): not used, ONNX Runtime crashes on it.' : (d.vendor || ''),
      }, `${d.type} · ${String(d.ep).replace('ExecutionProvider', '')}${d.virtualGpu ? ' (virtual, unused)' : ''}`))
      : chip('none yet', 'muted')));

  const providers = ml.providers ?? [];
  if (providers.length) {
    body.push(
      h('div', { class: 'small muted' }, 'Execution provider catalog'),
      h('div', { class: 'chips' }, providers.map((p) =>
        h('span', { class: `chip ${stateClass(p.readyState)}`, title: p.vendor || '' }, `${String(p.name).replace('ExecutionProvider', '')}: ${p.readyState}`))));
  } else if (!ml.catalogError) {
    body.push(h('div', { class: 'small muted' }, 'No additional certified execution providers reported for this PC. CPU and any registered DirectML device remain usable.'));
  }
  if (ml.catalogError) body.push(h('div', { class: 'error-line' }, `Execution provider catalog: ${ml.catalogError}`));
  if (ml.hasNpu === false) body.push(h('div', { class: 'small muted' }, 'No NPU execution device is registered. Downloading EPs cannot add NPU hardware or expose hardware hidden by a VM.'));

  const model = ml.model;
  body.push(h('div', { class: 'feature-row' },
    h('div', {}, h('div', { class: 'feature-name' }, 'Demo model (ONNX)'), h('div', { class: 'small muted mono' }, shortPath(model?.path || model?.configuredPath) || '—')),
    chip(model?.found ? 'Found' : 'Missing', model?.found ? 'ok' : 'warn'),
    h('span')));
  if (ml.registrationError) body.push(h('div', { class: 'error-line' }, ml.registrationError));

  const status = ml.error ? 'Error' : model?.found && devices.length ? 'Ready' : model?.found ? 'No devices' : 'No model';
  return stackCard({
    title: 'Windows ML',
    subtitle: 'ONNX Runtime + EP catalog · your own models (winml-cli, Foundry Toolkit)',
    status,
    body,
    foot: [
      h('button', { class: 'btn small', onclick: (e) => prepareEps(e.currentTarget) }, 'Download & register EPs'),
      h('button', { class: 'btn small ghost', onclick: () => showTab('lab') }, 'Open Silicon Lab'),
    ],
  });
}

async function prepareEps(button) {
  await withBusy(button, async () => {
    toast('Downloading and registering certified execution providers. The first time can take a few minutes.');
    try {
      const r = await postJSON('/api/windows-ml/prepare-eps', {});
      toast(`Execution providers ready in ${fmtMs(r.ms)}`, 'ok');
      logActivity({ stack: 'Windows ML', action: 'EnsureAndRegisterCertifiedAsync', ms: r.ms });
    } catch (e) {
      toast(e.message, 'bad');
    }
    await refreshStatus();
  });
}

function foundryCard(s) {
  const f = s.foundry ?? {};
  const body = [];
  if (f.error) body.push(h('div', { class: 'error-line' }, f.error));
  if (state.foundryLoadError) body.push(h('div', { class: 'error-line' }, state.foundryLoadError));
  if (state.foundryLoadError && state.foundryAlt) {
    const alt = state.foundryAlt;
    body.push(h('div', {}, h('button', { class: 'btn small', type: 'button', onclick: (e) => useCpuAlternative(alt, e.currentTarget) },
      alt.cached ? `Load the CPU variant instead (${alt.id})` : `Download & load the CPU variant instead (${alt.sizeMb == null ? '?' : Math.round(alt.sizeMb)} MB, needs internet)`)));
  }
  // An OpenVINO model Foundry can't load can still run through the sideload.
  const sideload = s.openvino ?? {};
  const sideloadModel = state.foundryLoadError && state.foundryLoadModel && sideload.installed
    ? (sideload.models ?? []).find((m) => m.id === state.foundryLoadModel) : null;
  if (sideloadModel) {
    body.push(h('div', {}, h('button', { class: 'btn small', type: 'button', onclick: (e) => loadOpenVinoModel(sideloadModel.id, e.currentTarget) },
      'Run it with the OpenVINO sideload instead')));
  } else if (state.foundryLoadError && state.foundryLoadModel && epNeededFor(state.foundryLoadModel) === 'OpenVINO' && !sideload.installed) {
    body.push(h('div', { class: 'small muted' }, `Tip: the OpenVINO sideload can run this model outside Foundry Local. Set it up once: ${sideload.setup || 'scripts\\setup-openvino-sideload.ps1'}`));
  }
  if (f.running) body.push(kv('OpenAI-compatible endpoint', h('span', { class: 'mono' }, `${f.endpoint}/v1`)));
  if (f.version) {
    const affected = /^0\.1[23]\./.test(`${f.version}.`);
    body.push(kv('Version', h('span', { class: 'row tight' }, h('span', { class: 'mono' }, f.version),
      affected ? chip('OpenVINO NPU/GPU variants fail on this version', 'warn') : null)));
  }
  if (f.busy) body.push(h('div', { class: 'howto' }, `${f.operation || 'Foundry Local is busy'}… This can take a minute or more on a slower PC. The model list refreshes when it finishes.`));
  if (f.warning && !f.busy) body.push(h('div', { class: 'small muted' }, f.warning));

  const models = f.models ?? [];
  if (models.length) {
    body.push(h('div', { class: 'small muted' }, `Models on this PC (${models.length})`));
    for (const m of models) {
      body.push(h('div', { class: 'feature-row' },
        h('div', { class: 'feature-name mono small' }, m.id),
        h('span', { class: 'row tight' }, chip(m.device, deviceClass(m.device)), epChip(m.id)),
        m.loaded ? chip('Loaded', 'ok') : h('button', { class: 'btn small', onclick: (e) => loadFoundryModel(m.id, e.currentTarget) }, 'Load')));
    }
  } else if (f.running && !f.busy) {
    body.push(h('div', { class: 'howto' }, 'No models cached yet. Browse the model catalog to download one.'));
  }

  const status = f.running ? (models.length ? 'Running' : f.busy ? 'Busy' : 'No models') : f.cliFound === false ? 'Not installed' : 'Not running';
  const foot = [];
  if (!f.running) foot.push(h('button', { class: 'btn small', onclick: (e) => startFoundry(e.currentTarget) }, 'Start Foundry Local'));
  foot.push(h('button', { class: 'btn small', onclick: () => browseFoundryCatalog() }, 'Browse model catalog'));
  foot.push(h('span', { class: 'small muted' }, 'Phi, Qwen, DeepSeek, Mistral, gpt-oss, Whisper…'));
  return stackCard({ title: 'Foundry Local', subtitle: 'Open models on NPU / GPU / CPU · OpenAI-compatible API', status, body, foot });
}

async function browseFoundryCatalog() {
  const search = h('input', { type: 'text', 'aria-label': 'Search models', placeholder: 'Search name, variant or license' });
  const device = h('select', { 'aria-label': 'Filter by device' },
    ['All devices', 'CPU', 'GPU', 'NPU'].map((value) => h('option', { value }, value)));
  const task = h('select', { 'aria-label': 'Filter by task' },
    ['All tasks', 'Chat', 'Speech', 'Embedding'].map((value) => h('option', { value }, value)));
  const list = h('div', { class: 'catalog-list' });
  const message = h('div', { class: 'small muted', role: 'status', 'aria-live': 'polite' }, 'Loading catalog...');
  const error = h('div', { class: 'error-line', role: 'alert' });
  let models = [];
  let busy = false;
  const close = h('button', { class: 'btn small', onclick: () => dialog.close() }, 'Close');
  const reload = h('button', { class: 'btn small', onclick: () => fetchCatalog() }, 'Refresh catalog');
  const dialog = h('dialog', { class: 'catalog-dialog', 'aria-labelledby': 'catalog-title' },
    h('div', { class: 'card-head' }, h('h2', { id: 'catalog-title' }, 'Foundry model catalog'), close),
    h('p', { class: 'small muted' }, 'Downloads use the internet and disk space shown below. Inference stays local. Review each model license before use. GPU/NPU variants require compatible hardware and drivers.'),
    h('div', { class: 'row catalog-filters' }, search, device, task, reload), message, error, list);

  function render() {
    const query = search.value.trim().toLowerCase();
    const filtered = models.filter((m) =>
      `${m.alias} ${m.id} ${m.license}`.toLowerCase().includes(query)
      && (device.value === 'All devices' || m.device === device.value)
      && (task.value === 'All tasks' || m.task.toLowerCase() === task.value.toLowerCase()));
    list.replaceChildren(...filtered.map((m) => h('div', { class: 'catalog-model' },
      h('div', {},
        h('div', { class: 'feature-name' }, m.alias),
        h('div', { class: 'small mono muted' }, m.id),
        h('div', { class: 'row tight' }, chip(m.device, deviceClass(m.device)), chip(m.task, 'muted'), epChip(m.id),
          h('span', { class: 'small muted' }, `${m.sizeMb == null ? 'Size unknown' : `${m.sizeMb.toLocaleString()} MB`} · License: ${m.license || 'not specified'}`))),
      m.loaded ? chip('Loaded', 'ok') : h('button', {
        class: 'btn small', disabled: busy,
        'aria-label': `${m.cached ? 'Load' : 'Download and load'} ${m.id}`,
        onclick: () => acquire(m),
      }, m.cached ? 'Load' : 'Download & load'))));
    if (!filtered.length && !busy) list.append(h('p', { class: 'small muted' }, 'No matching models.'));
    if (!busy) message.textContent = `${filtered.length} of ${models.length} model variants`;
  }

  async function fetchCatalog() {
    busy = true;
    close.disabled = reload.disabled = true;
    error.textContent = '';
    message.textContent = 'Loading catalog...';
    render();
    try {
      models = (await getJSON('/api/foundry/catalog')).models;
    } catch (e) {
      error.textContent = e.message;
    } finally {
      busy = false;
      close.disabled = reload.disabled = false;
      render();
    }
  }

  async function acquire(model) {
    busy = true;
    close.disabled = reload.disabled = true;
    error.textContent = '';
    render();
    let downloaded = model.cached;
    try {
      if (!model.cached) {
        message.textContent = `Downloading ${model.alias}... This may take several minutes.`;
        await postJSON('/api/foundry/download', { model: model.id });
        downloaded = model.cached = true;
      }
      message.textContent = `Loading ${model.alias} into memory...`;
      const result = await postJSON('/api/foundry/load', { model: model.id });
      model.loaded = true;
      clearModelBroken(model.id);
      logActivity({ stack: 'Foundry Local', action: `load ${model.id}`, device: model.device, ms: result.ms });
      toast(`Loaded ${model.alias}`, 'ok');
    } catch (e) {
      error.textContent = `${downloaded ? 'Load failed (model remains downloaded)' : 'Download failed'}: ${e.message}`;
      if (downloaded) markModelBroken(model.id, e.message);
      const alt = downloaded && looksLikeProviderFailure(model.id, e.message) ? pickCpuAlternative(models, model.id) : null;
      if (alt) {
        error.append(' ', h('button', { class: 'btn small', type: 'button', onclick: () => acquire(alt) },
          alt.cached ? `Load the CPU variant instead (${alt.id})` : `Download & load the CPU variant instead (${alt.sizeMb == null ? '?' : Math.round(alt.sizeMb)} MB, needs internet)`));
      }
      if (downloaded && state.status?.openvino?.installed && epNeededFor(model.id) === 'OpenVINO') {
        error.append(' ', h('button', {
          class: 'btn small', type: 'button',
          onclick: async (ev) => { await loadOpenVinoModel(model.id, ev.currentTarget); dialog.close(); },
        }, 'Run it with the OpenVINO sideload instead'));
      }
      logActivity({ stack: 'Foundry Local', action: `acquire ${model.id}`, device: model.device, ok: false });
    } finally {
      busy = false;
      close.disabled = reload.disabled = false;
      render();
      await refreshStatus();
    }
  }

  search.addEventListener('input', render);
  device.addEventListener('change', render);
  task.addEventListener('change', render);
  dialog.addEventListener('cancel', (event) => { if (busy) event.preventDefault(); });
  dialog.addEventListener('close', () => dialog.remove());
  document.body.append(dialog);
  dialog.showModal();
  await fetchCatalog();
}

async function startFoundry(button) {
  await withBusy(button, async () => {
    try {
      const r = await postJSON('/api/foundry/start', {});
      toast(r.started ? `Foundry Local is running at ${r.endpoint}` : `Could not start Foundry Local: ${r.error}`, r.started ? 'ok' : 'bad');
    } catch (e) {
      toast(e.message, 'bad');
    }
    await refreshStatus();
  });
}

async function loadFoundryModel(model, button) {
  await withBusy(button, async () => {
    try {
      const r = await postJSON('/api/foundry/load', { model });
      state.foundryLoadError = null;
      state.foundryAlt = null;
      state.foundryLoadModel = null;
      clearModelBroken(model);
      toast(`Loaded ${model} in ${fmtMs(r.ms)}`, 'ok');
      logActivity({ stack: 'Foundry Local', action: `load ${model}`, device: deviceOfModel(model), ms: r.ms });
    } catch (e) {
      state.foundryLoadError = e.message;
      state.foundryLoadModel = model;
      state.foundryAlt = null;
      markModelBroken(model, e.message);
      if (looksLikeProviderFailure(model, e.message)) {
        try { state.foundryAlt = pickCpuAlternative((await getJSON('/api/foundry/catalog')).models, model); } catch { /* no alternative offered */ }
      }
      toast('Could not load the model. Details are shown on the Foundry Local card.', 'bad');
    }
    await refreshStatus();
  });
}

/** Loads the CPU variant offered after a hardware variant failed (downloads it first when it isn't on this PC). */
async function useCpuAlternative(alt, button) {
  await withBusy(button, async () => {
    try {
      if (!alt.cached) {
        toast(`Downloading ${alt.id} ... this can take several minutes.`);
        await postJSON('/api/foundry/download', { model: alt.id });
      }
      const r = await postJSON('/api/foundry/load', { model: alt.id });
      state.foundryLoadError = null;
      state.foundryAlt = null;
      clearModelBroken(alt.id);
      toast(`Loaded ${alt.id} in ${fmtMs(r.ms)}`, 'ok');
      logActivity({ stack: 'Foundry Local', action: `load ${alt.id}`, device: 'CPU', ms: r.ms });
    } catch (e) {
      state.foundryLoadError = e.message;
    }
    await refreshStatus();
  });
}

function openvinoCard(s) {
  const o = s.openvino ?? {};
  const models = o.models ?? [];
  const body = [h('div', { class: 'small muted' },
    'Runs the OpenVINO NPU/GPU model variants outside Foundry Local, with a newer OpenVINO provider. A workaround for the Foundry Local load error “OrtEpApi_ReleaseEpContextConfig_SinceV28 is not available”.')];

  if (!o.installed) {
    const missing = [!o.pythonFound && 'portable Python', !o.packagesFound && 'packages', !o.workerFound && 'worker.py'].filter(Boolean);
    body.push(h('div', { class: 'howto' }, 'Not set up. Run ', h('code', {}, o.setup || 'scripts\\setup-openvino-sideload.ps1'),
      ' once while online (about 400 MB). For an offline PC, copy the folder it creates, or build the offline bundle with -IncludeOpenVinoSideload.'));
    if (o.root) {
      body.push(h('div', { class: 'small muted' }, missing.length && missing.length < 3
        ? `Incomplete installation at ${o.root}: missing ${missing.join(', ')}.`
        : `Looked in ${o.root}`));
    }
  } else {
    if (models.length) {
      body.push(h('div', { class: 'small muted' }, `OpenVINO models on this PC (${models.length})`));
      for (const m of models) {
        body.push(h('div', { class: 'feature-row' },
          h('div', { class: 'feature-name mono small' }, m.id),
          chip(m.device, deviceClass(m.device)),
          m.loaded
            ? h('button', { class: 'btn small ghost', type: 'button', onclick: (e) => unloadOpenVino(e.currentTarget) }, 'Unload')
            : h('button', { class: 'btn small', type: 'button', disabled: !!o.busy, onclick: (e) => loadOpenVinoModel(m.id, e.currentTarget) }, 'Load')));
      }
    } else {
      body.push(h('div', { class: 'howto' }, 'No OpenVINO models downloaded yet. Use “Browse model catalog” on the Foundry Local card and download an -openvino-npu or -openvino-gpu variant; Foundry can download it even though it can’t load it.'));
    }
    if (o.busy) body.push(h('div', { class: 'howto' }, `${o.operation || 'Working'}… The first load of an NPU/GPU model can take several minutes.`));
  }
  if (o.error) body.push(h('div', { class: 'error-line' }, o.error));
  if (state.ovLoadError) body.push(h('div', { class: 'error-line' }, state.ovLoadError));

  const status = !o.installed ? 'Not set up' : o.busy ? 'Busy' : models.length ? 'Ready' : 'No models';
  const foot = [h('span', { class: 'small muted' }, 'NPU/GPU variants need an Intel NPU/GPU with current drivers.')];
  return stackCard({ title: 'OpenVINO sideload', subtitle: 'Newer OpenVINO provider · runs models Foundry Local can’t load', status, body, foot });
}

async function loadOpenVinoModel(model, button) {
  await withBusy(button, async () => {
    toast(`Loading ${model} with the OpenVINO sideload. NPU/GPU models can take several minutes the first time.`);
    try {
      const r = await postJSON('/api/openvino/load', { model });
      state.ovLoadError = null;
      state.foundryLoadError = null;
      state.foundryAlt = null;
      state.foundryLoadModel = null;
      toast(`Loaded ${model} in ${fmtMs(r.ms)}`, 'ok');
      logActivity({ stack: 'OpenVINO sideload', action: `load ${model}`, device: deviceOfModel(model), ms: r.ms });
    } catch (e) {
      state.ovLoadError = e.message;
      toast('Could not load the model with the OpenVINO sideload. Details are on its card.', 'bad');
      logActivity({ stack: 'OpenVINO sideload', action: `load ${model}`, device: deviceOfModel(model), ok: false });
    }
    await refreshStatus();
  });
}

async function unloadOpenVino(button) {
  await withBusy(button, async () => {
    try {
      await postJSON('/api/openvino/unload', {});
      state.ovLoadError = null;
      toast('OpenVINO model unloaded.', 'ok');
    } catch (e) {
      toast(e.message, 'bad');
    }
    await refreshStatus();
  });
}

const langName = (code) => edge.TRANSLATOR_LANGUAGES.find(([c]) => c === code)?.[1] ?? code;

function buildTranslatorPanel() {
  const options = () => edge.TRANSLATOR_LANGUAGES.map(([code, name]) => h('option', { value: code }, `${name} (${code})`));
  const from = h('select', { 'aria-label': 'Translate from', id: 'tr-from' }, options());
  const to = h('select', { 'aria-label': 'Translate to', id: 'tr-to' }, options());
  const swap = h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Swap languages', title: 'Swap languages' }, '⇄');
  const info = h('div', { class: 'row tight' });
  const progress = h('progress', { max: '1', value: '0', 'aria-label': 'Language pack download progress' });
  const input = h('textarea', { id: 'tr-input', rows: '3', 'aria-label': 'Text to translate', placeholder: 'Type text to translate on-device…' });
  const run = h('button', { class: 'btn small', type: 'button' }, 'Translate');
  const output = h('div', { class: 'tr-output', role: 'status', 'aria-live': 'polite', dir: 'auto' });
  const error = h('div', { class: 'error-line', role: 'alert' });

  const probePair = async () => {
    const token = ++translator.token;
    const result = await edge.translatorAvailability(translator.source, translator.target);
    if (token !== translator.token) return; // a newer selection superseded this probe
    state.edge = { ...state.edge, translator: result };
    updateTranslatorPanel();
  };

  const setPair = (source, target) => {
    translator.source = source;
    translator.target = target;
    translator.progress = null;
    output.textContent = '';
    error.textContent = '';
    try { localStorage.setItem('showroom-translator-pair', JSON.stringify({ source, target })); } catch { /* storage unavailable */ }
    from.value = source;
    to.value = target;
    state.edge = { ...state.edge, translator: null };
    updateTranslatorPanel();
    probePair();
  };

  // Picking the language that is already on the other side swaps the pair instead of creating an invalid en→en pair.
  from.addEventListener('change', () => setPair(from.value, from.value === translator.target ? translator.source : translator.target));
  to.addEventListener('change', () => setPair(to.value === translator.source ? translator.target : translator.source, to.value));
  swap.addEventListener('click', () => setPair(translator.target, translator.source));

  async function download() {
    const { source, target } = translator;
    translator.busy = true;
    translator.progress = 0;
    error.textContent = '';
    updateTranslatorPanel();
    try {
      await edge.downloadTranslator(source, target, {
        onDownloadProgress: (fraction) => { translator.progress = fraction; updateTranslatorPanel(); },
      });
      toast(`${langName(source)} → ${langName(target)} language pack is ready`, 'ok');
    } catch (e) {
      error.textContent = `Download failed: ${e.message}`;
    } finally {
      translator.busy = false;
      translator.progress = null;
      await probePair();
    }
  }

  async function translate() {
    const text = input.value.trim();
    if (!text) { error.textContent = 'Enter some text to translate.'; return; }
    const { source, target } = translator;
    translator.busy = true;
    error.textContent = '';
    output.textContent = '';
    updateTranslatorPanel();
    const t0 = performance.now();
    try {
      const result = await edge.translateText(text, source, target, {
        onDownloadProgress: (fraction) => { translator.progress = fraction; updateTranslatorPanel(); },
      });
      output.setAttribute('lang', target);
      output.textContent = result;
      logActivity({ stack: 'Edge built-in AI', action: `Translator ${source} → ${target}`, device: 'GPU/CPU', ms: performance.now() - t0 });
    } catch (e) {
      error.textContent = e.message;
      logActivity({ stack: 'Edge built-in AI', action: `Translator ${source} → ${target}`, ok: false });
    } finally {
      translator.busy = false;
      translator.progress = null;
      await probePair();
    }
  }

  run.addEventListener('click', translate);
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && !run.disabled) translate(); });

  const panel = h('div', { class: 'translator' },
    h('div', { class: 'feature-name' }, 'Translator API'),
    h('div', { class: 'row tight tr-pair' }, from, swap, to),
    info, input,
    h('div', { class: 'row tight' }, run, h('span', { class: 'small muted' }, 'Ctrl+Enter')),
    output, error);
  panel.refs = { from, to, swap, info, progress, input, run };
  panel.download = download;
  panel.recheck = async () => {
    state.edge = { ...state.edge, translator: null };
    updateTranslatorPanel();
    await probePair();
  };
  return panel;
}

function updateTranslatorPanel() {
  const panel = translator.panel;
  if (!panel) return;
  const { from, to, swap, info, progress, input, run } = panel.refs;
  const st = state.edge?.translator ?? 'unknown';
  const label = `${langName(translator.source)} → ${langName(translator.target)}`;
  const downloading = translator.progress !== null || st === 'downloading';
  const apiMissing = st === 'missing';

  from.value = translator.source;
  to.value = translator.target;
  for (const el of [from, to, swap]) el.disabled = translator.busy || apiMissing;
  input.disabled = apiMissing;

  const parts = [chip(st === 'unknown' ? 'checking…' : st, st === 'unavailable' ? 'bad' : st === 'unknown' ? 'muted' : undefined)];
  if (st === 'downloadable' && !downloading) {
    parts.push(h('button', { class: 'btn small', type: 'button', disabled: translator.busy, onclick: () => panel.download?.() }, `Download ${label} language pack`));
  } else if (downloading) {
    const fraction = Math.max(0, Math.min(1, translator.progress ?? 0));
    progress.value = fraction;
    parts.push(progress, h('span', { class: 'small muted' }, translator.progress === null ? 'Downloading…' : `${Math.round(fraction * 100)}%`));
  } else if (st === 'available') {
    parts.push(h('span', { class: 'small muted' }, `${label} runs on this PC`));
  } else if (st === 'unavailable') {
    parts.push(h('span', { class: 'small muted' }, `${label} isn’t supported by this browser. Try another pair or swap direction.`));
  } else if (st === 'timeout' || st === 'error') {
    parts.push(
      h('span', { class: 'small muted' }, edge.statusHint('translator', st)),
      h('button', { class: 'btn small ghost', type: 'button', disabled: translator.busy, onclick: () => panel.recheck?.() }, 'Re-check'));
  } else if (apiMissing) {
    parts.push(h('span', { class: 'small muted' }, 'Translator API not available in this browser.'));
  }
  info.replaceChildren(...parts);
  run.disabled = translator.busy || !['available', 'downloadable'].includes(st);
  run.classList.toggle('busy', translator.busy);
}

// Edge Prompt / Summarizer / Language Detector rows are persistent too, so download progress updates in place.
const edgeApis = { busy: null, progress: {}, errors: {}, rows: null };
const EDGE_API_ROWS = [['prompt', 'Prompt API'], ['summarizer', 'Summarizer API'], ['languageDetector', 'Language Detector API']];

function buildEdgeApiRows() {
  const refs = {};
  const nodes = EDGE_API_ROWS.map(([id, label]) => {
    const status = h('span');
    const action = h('span', { class: 'row tight' });
    const error = h('div', { class: 'error-line', role: 'alert' });
    const help = h('div', { class: 'small muted' });
    refs[id] = { status, action, error, help, progress: h('progress', { max: '1', value: '0', 'aria-label': `${label} model download progress` }) };
    return h('div', { class: 'edge-api' }, h('div', { class: 'feature-row' }, h('div', { class: 'feature-name' }, label), status, action), error, help);
  });
  return { nodes, refs };
}

/** Applies a (possibly late) availability answer for one API to the UI. */
function setEdgeState(kind, value) {
  state.edge = { ...state.edge, [kind]: value };
  renderAll();
}

async function recheckEdgeApi(id) {
  setEdgeState(id, 'unknown');
  setEdgeState(id, await edge.probeApi(id));
}

// A probe that timed out may still answer later (for example when Edge finishes checking its model): show it when it does.
edge.onLateAvailability((className, args, value) => {
  const kind = edge.API_KINDS[className];
  if (!kind) return;
  if (kind === 'translator' && (args?.sourceLanguage !== translator.source || args?.targetLanguage !== translator.target)) return;
  if (state.edge?.[kind] === 'timeout') setEdgeState(kind, value);
});

async function downloadEdgeModel(id, label) {
  edgeApis.busy = id;
  edgeApis.progress[id] = 0;
  edgeApis.errors[id] = '';
  updateEdgeApiRows();
  const t0 = performance.now();
  try {
    await edge.downloadModel(id, { onDownloadProgress: (fraction) => { edgeApis.progress[id] = fraction; updateEdgeApiRows(); } });
    toast(`${label} model is ready`, 'ok');
    logActivity({ stack: 'Edge built-in AI', action: `${label} model download`, device: 'GPU/CPU', ms: performance.now() - t0 });
  } catch (e) {
    const detail = [e?.name, e?.message].filter(Boolean).join(': ');
    edgeApis.errors[id] = `Download failed: ${detail || e}. ${edge.failureHint(id, e)}`;
    logActivity({ stack: 'Edge built-in AI', action: `${label} model download`, ok: false });
  } finally {
    edgeApis.busy = null;
    edgeApis.progress[id] = null;
    updateEdgeApiRows();
    await refreshStatus();
  }
}

function updateEdgeApiRows() {
  if (!edgeApis.rows) return;
  for (const [id, label] of EDGE_API_ROWS) {
    const { status, action, error, help, progress } = edgeApis.rows.refs[id];
    const st = state.edge?.[id] ?? 'unknown';
    const fraction = edgeApis.progress[id];
    const downloading = fraction !== null && fraction !== undefined || st === 'downloading';
    const needsAttention = ['unavailable', 'timeout', 'error'].includes(st);
    status.replaceChildren(chip(st === 'unknown' ? 'checking…' : st, st === 'unknown' ? 'muted' : st === 'timeout' ? 'warn' : undefined));
    if (downloading) {
      progress.value = Math.max(0, Math.min(1, fraction ?? 0));
      action.replaceChildren(progress, h('span', { class: 'small muted' }, fraction == null ? 'Downloading…' : `${Math.round(progress.value * 100)}%`));
    } else if (st === 'downloadable') {
      action.replaceChildren(h('button', {
        class: 'btn small', type: 'button', disabled: edgeApis.busy !== null,
        'aria-label': `Download the ${label} model`,
        onclick: () => downloadEdgeModel(id, label),
      }, 'Download'));
    } else if (needsAttention) {
      action.replaceChildren(h('button', {
        class: 'btn small ghost', type: 'button', disabled: edgeApis.busy !== null,
        'aria-label': `Re-check the ${label}`,
        onclick: () => recheckEdgeApi(id),
      }, 'Re-check'));
    } else {
      action.replaceChildren();
    }
    error.textContent = edgeApis.errors[id] ?? '';
    help.textContent = needsAttention ? edge.statusHint(id, st) : '';
  }
}

function edgeCard() {
  const e = state.edge ?? {};
  edgeApis.rows ??= buildEdgeApiRows();
  updateEdgeApiRows();
  const rows = edgeApis.rows.nodes;

  if (!translator.panel) {
    translator.panel = buildTranslatorPanel();
  }
  updateTranslatorPanel();

  const usable = edge.promptUsable(e);
  const howto = usable ? null : h('div', { class: 'howto' }, 'Enable in Edge Canary or Dev:', h('ol', {},
    h('li', {}, 'Open ', h('code', {}, 'edge://flags'), ' and enable “Prompt API for on-device language model”.'),
    h('li', {}, 'Optional (Edge 150.0.4070+): enable “Enable prerelease on-device language model” for Aion-1.0-Instruct.'),
    h('li', {}, 'Restart Edge and check ', h('code', {}, 'edge://on-device-internals'), '.')));

  return stackCard({
    title: 'Edge built-in AI',
    subtitle: 'Web APIs backed by a model inside the browser',
    status: usable ? (e.prompt === 'available' ? 'Available' : 'Downloadable') : 'Missing',
    body: [rows, translator.panel, howto],
  });
}

function winmlCliCard(s) {
  const c = s.winmlCli ?? {};
  const status = c.found === true ? 'Found' : c.found === false ? 'Not found' : 'Not checked';
  return stackCard({
    title: 'winml-cli',
    subtitle: 'Build, analyze and benchmark ONNX models for Windows ML',
    status,
    body: [
      kv('Command', h('span', { class: 'mono small' }, c.command || 'winml')),
      state.winmlSysOutput ? h('pre', { class: 'console' }, state.winmlSysOutput) : null,
    ],
    foot: [h('button', { class: 'btn small', onclick: (e) => runWinmlSys(e.currentTarget) }, 'Run winml sys')],
  });
}

async function runWinmlSys(button) {
  await withBusy(button, async () => {
    try {
      const r = await postJSON('/api/winml-cli/sys', {});
      state.winmlSysOutput = r.output || '(no output)';
      logActivity({ stack: 'winml-cli', action: 'winml sys --list-device --list-ep', ms: r.ms, ok: r.exitCode === 0 });
    } catch (e) {
      state.winmlSysOutput = e.message;
      toast(e.message, 'bad');
    }
    await refreshStatus();
  });
}

function aionCard(s) {
  const a = s.aion ?? {};
  const body = [h('div', { class: 'small muted' }, a.note || '')];
  if (a.error) body.push(h('div', { class: 'error-line' }, a.error));
  const status = a.compiled ? (a.error ? 'Error' : a.loaded ? 'Loaded' : 'Built') : 'Optional';
  return stackCard({
    title: 'Aion Instruct Preview',
    subtitle: 'The next on-device model behind the Windows AI language model API',
    status,
    body,
  });
}

function privacyCard(s) {
  const p = s.privacy ?? {};
  const blocked = p.blockedCloudCalls > 0;
  return stackCard({
    title: 'Privacy guard',
    subtitle: 'What leaves this PC during the demo',
    status: blocked ? 'Blocked calls' : 'Local only',
    body: [
      kv('Bridge listens on', p.listensOn || 'loopback only'),
      kv('Calls to local runtimes', String(p.localCalls ?? 0)),
      kv('Calls to the cloud', blocked ? `${p.blockedCloudCalls} attempted, all blocked (last: ${p.lastBlockedHost})` : '0'),
      h('div', { class: 'howto' }, 'Inference works offline with prepared models. Catalog refreshes and explicit model or execution-provider downloads may use the internet through external runtimes; those transfers are not counted by this bridge HTTP guard.'),
    ],
  });
}

// =============================================================================================== engine pickers
function fillEngineSelect(select, preferred) {
  const current = preferred ?? select.value;
  select.replaceChildren();
  const groups = new Map();
  for (const e of state.engines) {
    if (!groups.has(e.group)) groups.set(e.group, h('optgroup', { label: e.group }));
    groups.get(e.group).append(h('option', { value: e.id, disabled: !e.available, title: e.reason || null },
      `${e.label} · ${e.device}${e.available ? '' : ' (unavailable)'}`));
  }
  for (const g of groups.values()) select.append(g);
  if (!state.engines.length) select.append(h('option', { value: '' }, 'No engines yet: start the bridge'));

  const available = state.engines.filter((e) => e.available).map((e) => e.id);
  select.value = available.includes(current) ? current : defaultEngine(state.engines) ?? '';
}

function refreshEngineSelects() {
  const taken = [];
  for (const lane of arena.lanes) {
    fillEngineSelect(lane.select, lane.select.value || defaultEngine(state.engines, taken));
    if (lane.select.value) taken.push(lane.select.value);
    updateLaneBadges(lane);
  }
  const docSelect = $('#doc-engine');
  fillEngineSelect(docSelect, docSelect.value || undefined);
}

// =============================================================================================== 2 · model arena
const PRESETS = [
  {
    label: 'NPU for a CFO',
    system: 'You are a concise business advisor.',
    prompt: 'In 3 bullet points, explain to a CFO why running AI on the NPU of employee laptops matters (cost, privacy, battery life). Keep it under 90 words.',
  },
  {
    label: 'Customer email (EN + AR)',
    system: 'You write polite, professional customer emails.',
    prompt: 'Write a short apology email to a customer whose laptop repair is delayed by 3 days. Then give the same email in Arabic.',
  },
  {
    label: 'Tickets → JSON',
    system: 'Reply with valid JSON only.',
    prompt: 'Classify each support ticket by category (hardware, software, access, other) and urgency (low, medium, high). Return a JSON array of {"id","category","urgency"}.\n1. "Laptop won\'t boot after the update"\n2. "Need access to the finance SharePoint site"\n3. "Outlook crashes when I attach a file"\n4. "Requesting a second monitor"',
  },
  {
    label: 'Summarize a policy',
    system: 'You summarize documents for busy executives.',
    prompt: 'Summarize in 3 bullets:\n"Employees may use generative AI only through approved, company-managed applications. Confidential customer data must not be entered into consumer AI services. AI-generated content must be reviewed by a person before it is shared externally. Teams should prefer on-device AI for documents classified Confidential or above, because the data then never leaves the managed device. Violations are reported to the security team."',
  },
  {
    label: 'Quick reasoning',
    system: '',
    prompt: 'A training room fits 8 people. 23 people need training. Each session lasts 45 minutes, with a 15-minute break between sessions, starting at 9:00. When does the last session end? Show the steps briefly.',
  },
];

const arena = { lanes: [], running: false };

function metricCells(r) {
  const cell = (k, v) => h('div', { class: 'metric' }, h('div', { class: 'k' }, k), h('div', { class: 'v' }, v));
  return [
    cell('First token', fmtMs(r.ttftMs)),
    cell('Total', fmtMs(r.totalMs)),
    cell(r.tokensEstimated ? '≈ tokens/s' : 'tokens/s', r.tokensPerSec ? r.tokensPerSec.toFixed(1) : '—'),
    cell(r.tokensEstimated ? '≈ tokens' : 'tokens', r.tokens ?? '—'),
  ];
}

function updateLaneBadges(lane) {
  const e = state.engines.find((x) => x.id === lane.select.value);
  lane.badges.replaceChildren(...(e ? [chip(e.group, 'muted'), chip(e.device, deviceClass(e.device))] : []));
}

function addLane(preferred) {
  if (arena.lanes.length >= 4) {
    toast('Up to four lanes.');
    return;
  }
  const select = h('select', { 'aria-label': 'Engine' });
  const badges = h('div', { class: 'lane-badges' });
  const output = h('div', { class: 'output', dir: 'auto' }, h('div', { class: 'placeholder small' }, 'The answer streams here.'));
  const metrics = h('div', { class: 'metrics' }, metricCells({}));
  const lane = { select, badges, output, metrics, controller: null, el: null };
  const remove = h('button', { class: 'icon-btn', title: 'Remove lane', 'aria-label': 'Remove lane', onclick: () => removeLane(lane) }, '✕');
  lane.el = h('div', { class: 'card lane' }, h('div', { class: 'lane-head' }, select, remove), badges, output, metrics);
  arena.lanes.push(lane);
  $('#arena-lanes').append(lane.el);

  const taken = arena.lanes.map((l) => l.select.value).filter(Boolean);
  fillEngineSelect(select, preferred ?? defaultEngine(state.engines, taken));
  select.addEventListener('change', () => updateLaneBadges(lane));
  updateLaneBadges(lane);
}

function removeLane(lane) {
  lane.controller?.abort();
  lane.el.remove();
  arena.lanes = arena.lanes.filter((l) => l !== lane);
}

function stopArena() {
  arena.running = false;
  for (const lane of arena.lanes) lane.controller?.abort();
}

async function runLane(lane, system, user) {
  const engineId = lane.select.value;
  const engine = state.engines.find((e) => e.id === engineId);
  lane.controller = new AbortController();
  lane.el.classList.remove('fastest');
  updateLaneBadges(lane);
  lane.output.replaceChildren(h('span', { class: 'spinner' }), h('span', { class: 'muted small' }, '  waiting for the first token…'));
  lane.metrics.replaceChildren(...metricCells({}));
  const view = streamRenderer(lane.output);

  try {
    const r = await runEngine(engineId, { system, user }, {
      signal: lane.controller.signal,
      onDelta: (_, full) => view.push(full),
    });
    if (r.status === 'Complete') {
      view.finish(r.text || '(empty answer)');
    } else {
      view.finish(null);
      lane.output.replaceChildren(h('div', { class: 'notice' },
        `No final answer (${r.status}). Partial text was discarded, as recommended for blocked or failed responses.`));
    }
    lane.metrics.replaceChildren(...metricCells(r));
    logActivity({ stack: engine?.group ?? engineId, action: `Arena · ${engine?.label ?? engineId}`, device: engine?.device, ms: r.totalMs });
    return { lane, ...r };
  } catch (err) {
    view.finish(null);
    if (err.name === 'AbortError') {
      lane.output.replaceChildren(h('div', { class: 'notice' }, 'Stopped.'));
    } else {
      lane.output.replaceChildren(h('div', { class: 'error-line' }, err.message));
      if (engineId?.startsWith('foundry:')) markModelBroken(engineId.slice('foundry:'.length), err.message);
      logActivity({ stack: engine?.group ?? engineId, action: 'Arena', device: engine?.device, ok: false });
    }
    return null;
  } finally {
    lane.controller = null;
  }
}

async function runArena() {
  if (arena.running) return;
  const user = $('#arena-prompt').value.trim();
  if (!user) {
    toast('Type a prompt first.');
    return;
  }
  const system = $('#arena-system').value.trim() || undefined;
  const lanes = arena.lanes.filter((l) => l.select.value);
  if (!lanes.length) {
    toast('Pick an engine in at least one lane.');
    return;
  }

  arena.running = true;
  $('#arena-run').disabled = true;
  const results = [];
  try {
    if ($('#arena-parallel').checked) {
      results.push(...(await Promise.all(lanes.map((l) => runLane(l, system, user)))));
    } else {
      for (const l of lanes) {
        if (!arena.running) break;
        results.push(await runLane(l, system, user));
      }
    }
  } finally {
    arena.running = false;
    $('#arena-run').disabled = false;
  }

  const finished = results.filter((r) => r && r.ttftMs != null);
  if (finished.length > 1) {
    const best = finished.reduce((a, b) => (a.ttftMs <= b.ttftMs ? a : b));
    best.lane.el.classList.add('fastest');
    best.lane.badges.append(chip('fastest first token', 'ok'));
  }
}

function initArena() {
  const presets = $('#arena-presets');
  for (const p of PRESETS) {
    presets.append(h('button', {
      type: 'button',
      onclick: () => {
        $('#arena-system').value = p.system || '';
        $('#arena-prompt').value = p.prompt;
        $('details.system').open = !!p.system;
      },
    }, p.label));
  }
  $('#arena-system').value = PRESETS[0].system;
  $('#arena-prompt').value = PRESETS[0].prompt;
  $('#arena-add').addEventListener('click', () => addLane());
  $('#arena-run').addEventListener('click', runArena);
  $('#arena-stop').addEventListener('click', stopArena);
  $('#arena-prompt').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      runArena();
    }
  });
  addLane();
  addLane();
}

// =============================================================================================== images
function loadImage(src) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Could not read that image.'));
    img.src = src;
  });
}

/** Re-encodes an image (applies EXIF orientation, caps the longest side). */
async function normalizeImage(src, maxSide, type = 'image/jpeg', quality = 0.92) {
  const img = await loadImage(src);
  const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
  const width = Math.max(1, Math.round(img.naturalWidth * scale));
  const height = Math.max(1, Math.round(img.naturalHeight * scale));
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const g = canvas.getContext('2d');
  g.fillStyle = '#ffffff';
  g.fillRect(0, 0, width, height);
  g.drawImage(img, 0, 0, width, height);
  return { dataUrl: canvas.toDataURL(type, quality), width, height };
}

function readFile(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

// =============================================================================================== 3 · private doc desk
const doc = { image: null, ocr: null };

const DOC_TASKS = {
  summarize: {
    title: 'Summary',
    system: 'You summarize documents for busy professionals. Reply with 3 to 5 short bullet points using only facts from the document.',
    user: (text) => `Summarize this document:\n\n${text}`,
  },
  rewrite: {
    title: 'Clear rewrite',
    system: 'Rewrite the text so it is clear, polite and professional. Keep every fact. Reply with the rewritten text only.',
    user: (text) => text,
  },
  extract: {
    title: 'Key fields (JSON)',
    system: 'You extract structured data from documents. Reply with one JSON object only: no prose, no code fences. Use null for unknown values.',
    user: (text) => `Extract these fields: document_type, reference_numbers, people, organizations, dates, amounts (with currency), location, summary (one sentence), requested_action.\n\nDocument:\n${text}`,
  },
  translate: {
    title: 'Arabic translation',
    system: 'You are a professional translator. Translate the user text into Modern Standard Arabic. Reply with the translation only.',
    user: (text) => text,
  },
  ask: {
    title: 'Answer',
    system: 'Answer using only the document. If the document does not contain the answer, say so. Be brief.',
    user: (text, question) => `Document:\n"""\n${text}\n"""\n\nQuestion: ${question}`,
  },
};

async function setDocImage(img) {
  doc.image = img.dataUrl;
  doc.ocr = null;
  const image = h('img', { id: 'doc-img', src: img.dataUrl, alt: 'Document image' });
  const overlay = h('canvas', { id: 'doc-overlay', class: 'overlay' });
  image.addEventListener('load', drawOcrOverlay);
  $('#doc-stage').replaceChildren(h('div', { class: 'doc-wrap' }, image, overlay));
  $('#doc-ocr-stats').textContent = '';
}

function clearDoc() {
  doc.image = null;
  doc.ocr = null;
  $('#doc-stage').replaceChildren(h('div', { class: 'placeholder' }, 'Upload a scan or photo, or load the sample claim form.'));
  $('#doc-text').value = '';
  $('#doc-ocr-stats').textContent = '';
  $('#doc-results').replaceChildren();
}

function drawOcrOverlay() {
  const img = $('#doc-img');
  const canvas = $('#doc-overlay');
  if (!img || !canvas) return;
  const rect = img.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.round(rect.width * dpr));
  canvas.height = Math.max(1, Math.round(rect.height * dpr));
  canvas.style.width = `${rect.width}px`;
  canvas.style.height = `${rect.height}px`;
  const g = canvas.getContext('2d');
  g.clearRect(0, 0, canvas.width, canvas.height);
  const ocr = doc.ocr;
  if (!ocr?.lines?.length || !ocr.width || !ocr.height) return;

  const sx = canvas.width / ocr.width;
  const sy = canvas.height / ocr.height;
  g.lineWidth = 1.5 * dpr;
  for (const line of ocr.lines) {
    const boxes = line.words?.length ? line.words : [{ box: line.box, confidence: 1 }];
    for (const w of boxes) {
      const b = w.box;
      if (!b || b.length < 8) continue;
      g.beginPath();
      g.moveTo(b[0] * sx, b[1] * sy);
      g.lineTo(b[2] * sx, b[3] * sy);
      g.lineTo(b[4] * sx, b[5] * sy);
      g.lineTo(b[6] * sx, b[7] * sy);
      g.closePath();
      const c = w.confidence ?? 1;
      g.strokeStyle = c >= 0.67 ? 'rgba(52, 211, 153, 0.95)' : c >= 0.33 ? 'rgba(251, 191, 36, 0.95)' : 'rgba(248, 113, 113, 0.95)';
      g.fillStyle = 'rgba(56, 189, 248, 0.10)';
      g.fill();
      g.stroke();
    }
  }
}

async function runOcr(button) {
  if (!doc.image) {
    toast('Upload an image or load the sample scan first.');
    return;
  }
  await withBusy(button, async () => {
    try {
      const r = await postJSON('/api/windows-ai/ocr', { image: doc.image });
      doc.ocr = r;
      $('#doc-text').value = r.text || '';
      const words = (r.lines ?? []).reduce((n, l) => n + (l.words?.length || 0), 0);
      $('#doc-ocr-stats').textContent = `${r.lineCount} lines · ${words} words · ${fmtMs(r.ms)} on ${r.device}${r.fallback ? ' · built-in Windows OCR' : ''}`;
      drawOcrOverlay();
      logActivity({ stack: r.fallback ? 'Windows OCR' : 'Windows AI', action: r.fallback ? 'OCR · built-in engine' : 'OCR · TextRecognizer', device: r.device, ms: r.ms });
      if (r.note) toast(r.note);
      if (!r.text) toast('No text found in this image.');
    } catch (e) {
      toast(e.message, 'bad');
      logActivity({ stack: 'Windows AI', action: 'OCR · TextRecognizer', device: 'NPU', ok: false });
    }
  });
}

function resultCard(title, engineLabel, device) {
  const body = h('div', { class: 'output', dir: 'auto' }, h('span', { class: 'spinner' }));
  const meta = h('span', { class: 'small muted' }, engineLabel);
  let text = '';
  const copy = h('button', {
    class: 'btn ghost small',
    onclick: async () => {
      try {
        await navigator.clipboard.writeText(text || body.innerText);
        toast('Copied.', 'ok');
      } catch {
        toast('Copy failed.', 'bad');
      }
    },
  }, 'Copy');
  const card = h('div', { class: 'result' },
    h('div', { class: 'result-head' }, h('strong', {}, title), device ? chip(device, deviceClass(device)) : null, meta, h('span', { class: 'spacer' }), copy),
    body);
  $('#doc-results').prepend(card);
  return { card, body, meta, setText: (t) => { text = t ?? ''; } };
}

async function describeDocImage(button) {
  if (!doc.image) {
    toast('Upload an image or load the sample scan first.');
    return;
  }
  const kind = $('#doc-describe-kind').value;
  const card = resultCard(`Image description (${kind})`, 'Windows AI · ImageDescription', 'NPU');
  await withBusy(button, async () => {
    try {
      const small = await normalizeImage(doc.image, 1024, 'image/jpeg', 0.9);
      const r = await postJSON('/api/windows-ai/describe', { image: small.dataUrl, kind });
      if (r.status !== 'Complete' || !r.description) {
        card.body.replaceChildren(h('div', { class: 'notice' }, `No description (${r.status}).`));
      } else {
        card.body.innerHTML = renderMarkdown(r.description);
      }
      card.setText(r.description);
      card.meta.append(` · ${fmtMs(r.ms)}`);
      logActivity({ stack: 'Windows AI', action: `ImageDescription · ${kind}`, device: 'NPU', ms: r.ms });
    } catch (e) {
      card.body.replaceChildren(h('div', { class: 'error-line' }, e.message));
      logActivity({ stack: 'Windows AI', action: 'ImageDescription', device: 'NPU', ok: false });
    }
  });
}

async function runDocAction(action, button) {
  if (action === 'describe') {
    await describeDocImage(button);
    return;
  }
  const text = $('#doc-text').value.trim();
  if (!text) {
    toast('Extract or paste some text first.');
    return;
  }
  const question = $('#doc-question').value.trim();
  if (action === 'ask' && !question) {
    toast('Type a question first.');
    return;
  }
  const engineId = $('#doc-engine').value;
  if (!engineId) {
    toast('No engine available yet. Check the Readiness tab.');
    return;
  }

  const engine = state.engines.find((e) => e.id === engineId);
  const task = DOC_TASKS[action];
  const targetLanguage = action === 'translate' ? langName($('#doc-translate-lang').value) : null;
  const system = targetLanguage
    ? `You are a professional translator. Translate the user text into ${targetLanguage}. Reply with the translation only.`
    : task.system;
  const useSkill = engineId === 'winai' && (action === 'summarize' || action === 'rewrite');
  const label = useSkill
    ? `Windows AI · ${action === 'summarize' ? 'TextSummarizer' : 'TextRewriter'} skill`
    : `${engine?.group ?? ''} · ${engine?.label ?? engineId}`;
  const device = useSkill ? 'NPU' : engine?.device;
  const title = targetLanguage ? `${targetLanguage} translation` : task.title;
  const card = resultCard(action === 'ask' ? `Q: ${question}` : title, label, device);

  await withBusy(button, async () => {
    try {
      let output;
      let ms;
      let status;
      if (useSkill) {
        const r = await postJSON(`/api/windows-ai/${action}`, { text });
        status = r.status;
        output = r.text ?? '';
        ms = r.ms;
      } else {
        const view = streamRenderer(card.body);
        const r = await runEngine(engineId, {
          system,
          user: task.user(text, question),
          temperature: action === 'extract' ? 0.1 : undefined,
        }, { onDelta: (_, full) => view.push(full) });
        view.finish(null);
        status = r.status;
        output = r.text;
        ms = r.totalMs;
      }

      if (status !== 'Complete') {
        card.body.replaceChildren(h('div', { class: 'notice' }, `No final answer (${status}).`));
      } else if (action === 'extract') {
        const json = extractJson(output);
        if (json !== undefined) {
          card.body.innerHTML = `<pre class="json">${renderJson(json)}</pre>`;
        } else {
          card.body.innerHTML = renderMarkdown(output);
          card.body.prepend(h('div', { class: 'notice' }, 'The model did not return valid JSON, so the raw answer is shown.'));
        }
      } else {
        card.body.innerHTML = renderMarkdown(output);
      }
      if (action === 'translate') {
        const rtl = ['ar', 'he'].includes($('#doc-translate-lang').value);
        card.body.setAttribute('dir', rtl ? 'rtl' : 'ltr');
        card.body.setAttribute('lang', $('#doc-translate-lang').value);
      }
      card.setText(output);
      card.meta.append(` · ${fmtMs(ms)}`);
      logActivity({ stack: useSkill ? 'Windows AI' : engine?.group ?? engineId, action: `Doc Desk · ${action}`, device, ms });
    } catch (e) {
      card.body.replaceChildren(h('div', { class: 'error-line' }, e.message));
      if (!useSkill && engineId?.startsWith('foundry:')) markModelBroken(engineId.slice('foundry:'.length), e.message);
      logActivity({ stack: useSkill ? 'Windows AI' : engine?.group ?? engineId, action: `Doc Desk · ${action}`, device, ok: false });
    }
  });
}

function initDocDesk() {
  $('#doc-translate-lang').replaceChildren(...edge.TRANSLATOR_LANGUAGES.map(([code, name]) => h('option', { value: code, selected: code === 'ar' }, name)));
  $('#doc-file').addEventListener('change', async (e) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    try {
      await setDocImage(await normalizeImage(await readFile(file), 2000));
    } catch (err) {
      toast(err.message, 'bad');
    }
  });
  $('#doc-sample').addEventListener('click', async () => {
    await setDocImage(await normalizeImage(makeSampleScan(), 2000, 'image/png'));
    toast('Loaded a fictional claim form. Next: “Extract text”.');
  });
  $('#doc-clear').addEventListener('click', clearDoc);
  $('#doc-ocr').addEventListener('click', (e) => runOcr(e.currentTarget));
  for (const b of $$('[data-doc-action]')) b.addEventListener('click', () => runDocAction(b.dataset.docAction, b));
  $('#doc-ask').addEventListener('click', (e) => runDocAction('ask', e.currentTarget));
  $('#doc-question').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      runDocAction('ask', $('#doc-ask'));
    }
  });
  new ResizeObserver(() => drawOcrOverlay()).observe($('#doc-stage'));
}

// =============================================================================================== 4 · silicon lab
const DEVICES = [
  { id: 'auto', name: 'Auto', sub: 'Windows ML picks · NPU first' },
  { id: 'npu', name: 'NPU', sub: 'Neural processing unit' },
  { id: 'gpu', name: 'GPU', sub: 'Graphics processor' },
  { id: 'cpu', name: 'CPU', sub: 'Always available' },
];

const lab = { device: 'auto', image: null, stream: null, live: false, looping: false };

function renderLabDevices() {
  const ml = state.status?.windowsML;
  const has = { auto: state.bridgeUp, cpu: state.bridgeUp, npu: !!ml?.hasNpu, gpu: !!ml?.hasGpu };
  if (!has[lab.device]) lab.device = 'auto';
  $('#lab-devices').replaceChildren(...DEVICES.map((d) => h('button', {
    type: 'button',
    role: 'radio',
    'aria-checked': String(lab.device === d.id),
    disabled: !has[d.id],
    title: has[d.id] ? null : (d.id === 'gpu' && ml?.virtualGpuOnly
      ? 'The only GPU on this PC is virtual (Hyper-V video / Basic Render Driver). Running AI models on it crashes ONNX Runtime, so it is disabled.'
      : `No ${d.name} execution provider is registered`),
    onclick: () => {
      lab.device = d.id;
      renderLabDevices();
    },
  }, h('div', { class: 'd' }, d.name), h('div', { class: 's' }, d.sub))));

  const model = ml?.model;
  // winml-cli can only be told to use hardware this PC really has (a crash in its Python process is not recoverable).
  for (const option of $$('#perf-device option')) {
    const usable = option.value === 'auto' || option.value === 'cpu' || has[option.value];
    option.disabled = !usable;
    option.title = usable ? '' : 'Not available on this PC';
  }
  if ($('#perf-device').selectedOptions[0]?.disabled) $('#perf-device').value = 'cpu';
  $('#lab-model').textContent = model?.found
    ? `Model: ${shortPath(model.path)}${model.sizeMb ? ` (${model.sizeMb} MB)` : ''}`
    : 'No model yet: see README → Silicon Lab';
}

async function setLabImage(img) {
  if (lab.stream) stopCamera();
  lab.image = img.dataUrl;
  const el = $('#lab-img');
  el.src = img.dataUrl;
  el.hidden = false;
  $('#lab-placeholder').hidden = true;
}

async function toggleCamera() {
  if (lab.stream) {
    stopCamera();
    return;
  }
  try {
    lab.stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false });
    const video = $('#lab-video');
    video.srcObject = lab.stream;
    await video.play();
    video.hidden = false;
    $('#lab-img').hidden = true;
    $('#lab-placeholder').hidden = true;
    $('#lab-camera').textContent = 'Stop camera';
  } catch (e) {
    lab.stream = null;
    toast(`Camera unavailable: ${e.message}`, 'bad');
  }
}

function stopCamera() {
  setLive(false);
  lab.stream?.getTracks().forEach((t) => t.stop());
  lab.stream = null;
  const video = $('#lab-video');
  video.srcObject = null;
  video.hidden = true;
  $('#lab-camera').textContent = 'Use camera';
  $('#lab-live-label').hidden = true;
  if (lab.image) $('#lab-img').hidden = false;
  else $('#lab-placeholder').hidden = false;
}

function captureFrame() {
  const video = $('#lab-video');
  if (!lab.stream || !video.videoWidth) return null;
  const side = Math.min(video.videoWidth, video.videoHeight);
  const canvas = document.createElement('canvas');
  canvas.width = 448;
  canvas.height = 448;
  canvas.getContext('2d').drawImage(video, (video.videoWidth - side) / 2, (video.videoHeight - side) / 2, side, side, 0, 0, 448, 448);
  return canvas.toDataURL('image/jpeg', 0.9);
}

function classify(device, iterations) {
  const image = lab.stream ? captureFrame() : lab.image;
  if (!image) return Promise.reject(new Error('Upload a photo or start the camera first.'));
  return postJSON('/api/windows-ml/classify', { image, device, iterations });
}

function renderLabResult(r) {
  const top = r.top ?? [];
  const best = top[0];
  $('#lab-result').replaceChildren(
    h('div', { class: 'headline' }, best ? [h('span', { class: 'big' }, prettyLabel(best.label)), `  ${(best.probability * 100).toFixed(1)}%`] : 'No result'),
    h('div', { class: 'small muted' }, `${r.provider} · ${r.model} · input ${r.input?.width}×${r.input?.height} ${r.input?.layout ?? ''}`),
    h('div', { class: 'bars' }, top.map((t) => barRow(prettyLabel(t.label), t.probability, `${(t.probability * 100).toFixed(1)}%`, deviceClass(r.device), t.label))),
    h('div', { class: 'stat-grid' },
      stat('Device', String(r.device).toUpperCase()),
      stat('Average', fmtMs(r.latencyMs?.avg)),
      stat('P50', fmtMs(r.latencyMs?.p50)),
      stat('P90', fmtMs(r.latencyMs?.p90)),
      stat('Fastest', fmtMs(r.latencyMs?.min)),
      stat('Timed runs', String(r.iterations)),
      r.sessionCreateMs != null ? stat('Session setup', fmtMs(r.sessionCreateMs)) : null));
}

async function runLab(button) {
  await withBusy(button, async () => {
    try {
      const r = await classify(lab.device, Number($('#lab-iterations').value));
      renderLabResult(r);
      logActivity({ stack: 'Windows ML', action: `Classify · ${r.provider}`, device: String(r.device).toUpperCase(), ms: r.latencyMs?.avg });
    } catch (e) {
      $('#lab-result').replaceChildren(h('div', { class: 'error-line' }, e.message));
      logActivity({ stack: 'Windows ML', action: 'Classify', device: lab.device.toUpperCase(), ok: false });
    }
  });
}

function renderRace(results) {
  const ok = results.filter((r) => !r.error);
  const slowest = Math.max(0.001, ...ok.map((r) => r.latencyMs.avg));
  const cpu = ok.find((r) => r.device === 'cpu');
  const fastest = ok.length ? ok.reduce((a, b) => (a.latencyMs.avg <= b.latencyMs.avg ? a : b)) : null;
  const labels = new Set(ok.map((r) => prettyLabel(r.top?.[0]?.label ?? '')));

  const rows = results.map((r) => (r.error
    ? h('div', { class: 'bar-row' }, h('span', { class: 'label' }, r.device.toUpperCase()), h('span', { class: 'error-line' }, r.error), h('span'))
    : barRow(`${r.device.toUpperCase()} · ${String(r.provider).replace('ExecutionProvider', '')}`, r.latencyMs.avg / slowest, fmtMs(r.latencyMs.avg), deviceClass(r.device))));

  $('#lab-race-result').replaceChildren(
    h('div', { class: 'card-head' }, h('h3', {}, 'Device race · average latency (lower is better)')),
    fastest && cpu && fastest !== cpu
      ? h('div', { class: 'headline' }, h('span', { class: 'big' }, `${(cpu.latencyMs.avg / fastest.latencyMs.avg).toFixed(1)}×`), ` faster on ${fastest.device.toUpperCase()} than on CPU`)
      : null,
    h('div', { class: 'bars' }, rows),
    ok.length > 1
      ? h('div', { class: 'small muted' }, labels.size === 1
        ? `Same top answer on every device: ${[...labels][0]}.`
        : 'Top answers differ slightly between devices (quantized models can do that).')
      : null);
}

async function raceDevices(button) {
  const ml = state.status?.windowsML;
  const devices = ['npu', 'gpu', 'cpu'].filter((d) => d === 'cpu' || (d === 'npu' ? ml?.hasNpu : ml?.hasGpu));
  const iterations = Math.max(10, Number($('#lab-iterations').value));
  await withBusy(button, async () => {
    const results = [];
    $('#lab-race-result').replaceChildren(h('div', { class: 'small muted' }, `Racing ${devices.map((d) => d.toUpperCase()).join(' vs ')} · ${iterations} timed runs each…`));
    for (const d of devices) {
      try {
        const r = await classify(d, iterations);
        results.push(r);
        logActivity({ stack: 'Windows ML', action: `Race · ${r.provider}`, device: d.toUpperCase(), ms: r.latencyMs.avg });
      } catch (e) {
        results.push({ device: d, error: e.message });
      }
      renderRace(results);
    }
  });
}

function setLive(on) {
  if (on && !lab.stream) {
    toast('Turn on the camera first.');
    on = false;
  }
  lab.live = on;
  $('#lab-live').checked = on;
  if (on) {
    liveLoop();
  } else {
    $('#lab-live-label').hidden = true;
    $('#lab-live-stats').textContent = '';
  }
}

async function liveLoop() {
  if (lab.looping) return;
  lab.looping = true;
  let frames = 0;
  const t0 = performance.now();
  try {
    while (lab.live && lab.stream) {
      try {
        const r = await classify(lab.device, 1);
        frames += 1;
        if (!lab.live) break;
        const best = r.top?.[0];
        const label = $('#lab-live-label');
        label.hidden = false;
        label.textContent = best
          ? `${prettyLabel(best.label)} · ${(best.probability * 100).toFixed(0)}% · ${fmtMs(r.latencyMs.avg)} on ${String(r.device).toUpperCase()}`
          : '…';
        $('#lab-live-stats').textContent = `${(frames / ((performance.now() - t0) / 1000)).toFixed(1)} frames/s (round trip)`;
      } catch (e) {
        toast(e.message, 'bad');
        setLive(false);
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 120));
    }
  } finally {
    lab.looping = false;
  }
}

async function runPerf(button) {
  const device = $('#perf-device').value;
  const iterations = Number($('#perf-iterations').value);
  const out = $('#perf-result');
  await withBusy(button, async () => {
    out.replaceChildren(h('div', { class: 'small muted' }, 'Running winml perf… The first NPU run compiles the model and can take a while.'));
    try {
      const r = await postJSON('/api/winml-cli/perf', { device, iterations });
      const s = r.summary;
      const latency = s?.latencyMs ?? {};
      out.replaceChildren(
        h('div', { class: 'small muted mono' }, r.command),
        s
          ? h('div', { class: 'stat-grid' },
            Object.entries(latency).map(([k, v]) => stat(k.toUpperCase(), `${v} ms`)),
            s.throughputPerSec != null ? stat('Throughput', `${s.throughputPerSec}/s`) : null,
            s.device ? stat('Device', s.device) : null)
          : h('div', { class: 'notice' }, r.exitCode === 0 ? 'Finished. See the console output below.' : `winml perf exited with code ${r.exitCode}.`),
        r.note ? h('div', { class: 'notice' }, r.note) : null,
        r.hint ? h('div', { class: 'error-line' }, r.hint) : null,
        h('details', {}, h('summary', { class: 'small muted' }, 'Console output'), h('pre', { class: 'console' }, r.output || '')),
        r.report != null
          ? h('details', {}, h('summary', { class: 'small muted' }, `JSON report${r.reportPath ? ` · ${r.reportPath}` : ''}`), h('pre', { class: 'console', html: renderJson(r.report) }))
          : null);
      logActivity({ stack: 'winml-cli', action: `winml perf --device ${device}`, device: String(s?.device || device).toUpperCase(), ms: r.ms, ok: r.exitCode === 0 });
    } catch (e) {
      out.replaceChildren(h('div', { class: 'error-line' }, e.message));
    }
  });
}

function initLab() {
  $('#lab-file').addEventListener('change', async (e) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file) return;
    try {
      await setLabImage(await normalizeImage(await readFile(file), 1024));
    } catch (err) {
      toast(err.message, 'bad');
    }
  });
  $('#lab-camera').addEventListener('click', toggleCamera);
  $('#lab-live').addEventListener('change', (e) => setLive(e.target.checked));
  $('#lab-run').addEventListener('click', (e) => runLab(e.currentTarget));
  $('#lab-race').addEventListener('click', (e) => raceDevices(e.currentTarget));
  $('#perf-run').addEventListener('click', (e) => runPerf(e.currentTarget));
}

// =============================================================================================== start
async function init() {
  renderVersion();
  initTheme();
  initArena();
  initDocDesk();
  initLab();
  initTabs();
  $('#btn-refresh').addEventListener('click', (e) => withBusy(e.currentTarget, refreshStatus));
  $('#btn-diagnostics').addEventListener('click', copyDiagnostics);
  renderLabDevices();
  await refreshStatus();
  setInterval(() => {
    if (!document.hidden && !arena.running) refreshStatus();
  }, 30000);
}

init();
