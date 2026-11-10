#!/usr/bin/env node
// Smoke test for the bridge API (real or simulated). Uses the same client code as the web UI.
//
//   node mock/selftest.mjs                         -> tests http://127.0.0.1:5080
//   node mock/selftest.mjs http://127.0.0.1:5090
//
// On a real Copilot+ PC some checks may report SKIP (e.g. a stack that is not installed); FAIL means broken.

import { deflateSync } from 'node:zlib';
import { setApiBase, getJSON, postJSON } from '../bridge/wwwroot/js/api.js';
import { buildEngines, defaultEngine, runEngine } from '../bridge/wwwroot/js/engines.js';
import { renderMarkdown, extractJson, renderJson } from '../bridge/wwwroot/js/markdown.js';

const base = process.argv[2] || 'http://127.0.0.1:5080';
setApiBase(base);
const { APP_VERSION } = await import('../bridge/wwwroot/js/version.js');
const fileVersion = (await import('node:fs')).readFileSync(new URL('../VERSION', import.meta.url), 'utf8').trim();

const results = [];
const record = (name, outcome, detail = '') => {
  results.push({ name, outcome, detail });
  const icon = outcome === 'PASS' ? '✔' : outcome === 'SKIP' ? '•' : '✖';
  console.log(`${icon} ${outcome.padEnd(4)} ${name}${detail ? `  —  ${detail}` : ''}`);
};

async function check(name, fn, { skipWhen } = {}) {
  try {
    const detail = await fn();
    record(name, 'PASS', detail);
  } catch (err) {
    if (skipWhen?.(err)) record(name, 'SKIP', err.message);
    else record(name, 'FAIL', err.message);
  }
}

const assert = (cond, msg) => {
  if (!cond) throw new Error(msg);
};

// A small gradient PNG, generated without dependencies.
function makePng(width = 96, height = 96) {
  const crcTable = Array.from({ length: 256 }, (_, n) => {
    let c = n;
    for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    return c >>> 0;
  });
  const crc = (buf) => {
    let c = 0xffffffff;
    for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
    return (c ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const c = Buffer.alloc(4);
    c.writeUInt32BE(crc(td));
    return Buffer.concat([len, td, c]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 2; // RGB
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y += 1) {
    raw[y * (width * 3 + 1)] = 0;
    for (let x = 0; x < width; x += 1) {
      const i = y * (width * 3 + 1) + 1 + x * 3;
      raw[i] = (x * 255) / width;
      raw[i + 1] = (y * 255) / height;
      raw[i + 2] = 160;
    }
  }
  const png = Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw)),
    chunk('IEND', Buffer.alloc(0)),
  ]);
  return `data:image/png;base64,${png.toString('base64')}`;
}

const notInstalled = (err) => /not (available|ready|running|found|installed)|no onnx model|needs package identity|identity|unknown engine|without the aion/i.test(err.message);

console.log(`\nLocal AI Showroom self-test against ${base}\n`);

// ---- pure helpers (no server needed)
await check('markdown renderer escapes HTML', async () => {
  const html = renderMarkdown('# Title\n- **bold** item\n<script>alert(1)</script>');
  assert(html.includes('<strong>bold</strong>'), 'bold not rendered');
  assert(!html.includes('<script>'), 'HTML was not escaped');
  return 'ok';
});
await check('JSON extraction tolerates fences', async () => {
  const v = extractJson('Here you go:\n```json\n{"a": 1, "b": [true, null]}\n```');
  assert(v && v.a === 1, 'JSON not extracted');
  assert(renderJson(v).includes('class="k"'), 'JSON not highlighted');
  return 'ok';
});

// ---- bridge
let status = null;
await check('GET /api/status', async () => {
  status = await getJSON('/api/status');
  assert(status.app === 'Local AI Showroom', 'unexpected payload');
  return `${status.simulated ? 'SIMULATED' : 'real'} · ${status.machine?.cpu ?? 'unknown CPU'}`;
});

await check('version matches (VERSION file, UI files, bridge)', async () => {
  assert(fileVersion === APP_VERSION, `VERSION is ${fileVersion} but wwwroot/js/version.js is ${APP_VERSION} (run scripts/set-version.ps1)`);
  const ping = await getJSON('/api/ping');
  assert(ping.version === fileVersion, `bridge reports ${ping.version} on /api/ping, expected ${fileVersion} (rebuild and restart the bridge)`);
  assert(status?.version === fileVersion, `bridge reports ${status?.version} on /api/status, expected ${fileVersion}`);
  return `v${fileVersion}`;
});

if (!status) {
  console.log('\nThe bridge is not reachable; nothing else to test.\n');
  process.exit(1);
}

const engines = buildEngines(status, null);
await check('engine list', async () => {
  assert(engines.length > 0, 'no engines');
  return engines.map((e) => `${e.id}${e.available ? '' : ' (off)'}`).join(', ');
});

await check('Foundry Local version', async () => {
  let f = status.foundry;
  if (!f?.running) throw new Error('not installed: Foundry Local is not running');
  // The real bridge reads `foundry --version` in the background, so it can appear on the second status call.
  for (let i = 0; i < 6 && !f.version; i++) {
    await new Promise((r) => setTimeout(r, 1500));
    f = (await getJSON('/api/status')).foundry;
  }
  assert(/^\d+\.\d+(\.\d+)?$/.test(f.version ?? ''), `no version in the status (got ${JSON.stringify(f.version)})`);
  return f.version;
}, { skipWhen: notInstalled });

await check('Foundry catalog metadata', async () => {
  const catalog = await getJSON('/api/foundry/catalog');
  assert(Array.isArray(catalog.models) && catalog.models.length > 0, 'empty catalog');
  for (const model of catalog.models) {
    assert(typeof model.id === 'string' && typeof model.task === 'string', 'missing model metadata');
    assert(typeof model.cached === 'boolean' && typeof model.loaded === 'boolean', 'missing cache/load state');
  }
  return `${catalog.models.length} variants`;
}, { skipWhen: notInstalled });

if (status.simulated) {
  await check('catalog download and load updates inventory', async () => {
    const catalog = await getJSON('/api/foundry/catalog');
    const model = catalog.models.find((m) => !m.cached) || catalog.models[0];
    assert(model, 'simulation catalog is empty');
    const downloaded = await postJSON('/api/foundry/download', { model: model.id });
    assert(downloaded.cached === true, 'download did not confirm cached state');
    const loaded = await postJSON('/api/foundry/load', { model: model.id });
    assert(loaded.loaded === true, 'load did not confirm loaded state');
    const inventory = await getJSON('/api/foundry/models');
    assert(inventory.models.some((m) => m.id === model.id && m.cached && m.loaded), 'inventory was not updated');
    return model.id;
  });
}

await check('invalid model download is rejected', async () => {
  try {
    await postJSON('/api/foundry/download', { model: '--help' });
  } catch (err) {
    assert(err.status === 400, `expected 400, got ${err.status}`);
    return '400 as expected';
  }
  throw new Error('CLI option was accepted as a model ID');
});

await check('OpenVINO sideload status', async () => {
  const ov = status.openvino;
  assert(ov && typeof ov.installed === 'boolean' && Array.isArray(ov.models) && typeof ov.setup === 'string', 'missing or malformed openvino status');
  return ov.installed ? `${ov.models.length} model(s), loaded: ${ov.loaded ?? 'none'}` : 'not set up';
});

await check('OpenVINO sideload rejects unsafe model ids', async () => {
  try {
    await postJSON('/api/openvino/load', { model: '--help' });
  } catch (err) {
    assert(err.status === 400, `expected 400, got ${err.status}`);
    return '400 as expected';
  }
  throw new Error('a CLI-style model id was accepted');
});

await check('OpenVINO sideload chat (stream)', async () => {
  const ov = status.openvino;
  if (!ov?.installed || !ov.models?.length) throw new Error('not installed or no models');
  const id = ov.models[0].id;
  const r = await runEngine(`openvino:${id}`, { system: 'Be brief.', user: 'Name one benefit of local AI.' });
  assert(r.text.trim().length > 0, 'empty answer');
  return `${id} · first token ${Math.round(r.ttftMs)} ms · ${r.tokens} tokens`;
}, { skipWhen: notInstalled });

await check('Windows AI language model (stream)', async () => {
  const winai = engines.find((e) => e.id === 'winai');
  if (!winai?.available) throw new Error(`not available: ${winai?.reason || 'unknown'}`);
  const r = await runEngine('winai', { user: 'Reply with five words about on-device AI.' });
  assert(r.status === 'Complete', `status ${r.status}`);
  assert(r.text.trim().length > 0, 'empty answer');
  return `first token ${Math.round(r.ttftMs)} ms, total ${Math.round(r.totalMs)} ms`;
}, { skipWhen: notInstalled });

await check('Foundry Local chat (stream)', async () => {
  const id = defaultEngine(engines.filter((e) => e.id.startsWith('foundry:')));
  if (!id) throw new Error('not running or no models found');
  const r = await runEngine(id, { system: 'Be brief.', user: 'Name one benefit of local AI.' });
  assert(r.text.trim().length > 0, 'empty answer');
  return `${id} · first token ${Math.round(r.ttftMs)} ms · ${r.tokens} tokens`;
}, { skipWhen: notInstalled });

const image = makePng();
await check('Windows AI OCR', async () => {
  const r = await postJSON('/api/windows-ai/ocr', { image });
  assert(typeof r.lineCount === 'number', 'no lineCount');
  return `${r.lineCount} lines in ${r.ms} ms`;
}, { skipWhen: notInstalled });

await check('Windows ML classify (auto)', async () => {
  const r = await postJSON('/api/windows-ml/classify', { image, device: 'auto', iterations: 3 });
  assert(Array.isArray(r.top) && r.top.length > 0, 'no predictions');
  return `${r.provider} · top-1 "${r.top[0].label}" · avg ${r.latencyMs.avg} ms`;
}, { skipWhen: notInstalled });

await check('bad request is rejected', async () => {
  try {
    await postJSON('/api/windows-ml/classify', { image, device: 'quantum' });
  } catch (err) {
    assert(err.status === 400, `expected 400, got ${err.status}`);
    return '400 as expected';
  }
  throw new Error('invalid device was accepted');
});

const failed = results.filter((r) => r.outcome === 'FAIL').length;
const skipped = results.filter((r) => r.outcome === 'SKIP').length;
console.log(`\n${results.length - failed - skipped} passed · ${skipped} skipped · ${failed} failed\n`);
process.exit(failed ? 1 : 0);
