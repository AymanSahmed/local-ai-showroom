// One interface over every on-device text engine the demo can use.
import { postSSE } from './api.js';
import * as edge from './edge-ai.js';

/**
 * Builds the engine list from the bridge status and Edge availability.
 * Engine ids: 'winai' | 'aion' | 'foundry:<model id>' | 'edge'.
 */
export function buildEngines(status, edgeState, broken = new Set()) {
  const engines = [];

  const lm = status?.windowsAI?.features?.find((f) => f.id === 'languageModel');
  engines.push({
    id: 'winai',
    group: 'Windows AI APIs',
    label: 'Built-in language model (Phi Silica / Aion)',
    device: 'NPU',
    available: lm?.state === 'Ready',
    reason: lm ? (lm.state === 'Ready' ? '' : lm.error || lm.state) : 'bridge offline',
  });

  if (status?.aion?.compiled) {
    engines.push({
      id: 'aion',
      group: 'Aion Instruct Preview',
      label: 'Aion Instruct (preview SDK)',
      device: 'NPU',
      available: !status.aion.error,
      reason: status.aion.error || '',
    });
  }

  const foundry = status?.foundry;
  for (const m of foundry?.models ?? []) {
    const isBroken = broken.has(m.id);
    engines.push({
      id: `foundry:${m.id}`,
      group: 'Foundry Local',
      label: m.id + (m.loaded ? '  ●' : ''),
      device: m.device || 'Auto',
      available: !!foundry.running && !isBroken,
      loaded: !!m.loaded,
      reason: isBroken
        ? 'Failed to load on this PC (execution provider problem). Use a CPU variant instead.'
        : foundry.running ? '' : foundry.error || 'not running',
    });
  }

  const ov = status?.openvino;
  if (ov?.installed) {
    for (const m of ov.models ?? []) {
      engines.push({
        id: `openvino:${m.id}`,
        group: 'OpenVINO (sideload)',
        label: m.id + (m.loaded ? '  ●' : ''),
        device: m.device || 'Auto',
        available: true,
        loaded: !!m.loaded,
        reason: '',
      });
    }
  }

  if (edge.promptUsable(edgeState)) {
    engines.push({
      id: 'edge',
      group: 'Microsoft Edge built-in AI',
      label: 'Prompt API (Phi-4-mini / Aion-1.0)',
      device: 'GPU/CPU',
      available: true,
      reason: edgeState.prompt === 'available' ? '' : 'model downloads on first use',
    });
  }

  return engines;
}

/** Picks a sensible default engine: built-in model, then an NPU/GPU Foundry model, then anything. */
export function defaultEngine(engines, skip = []) {
  const usable = engines.filter((e) => e.available && !skip.includes(e.id));
  return (
    usable.find((e) => e.id === 'winai') ||
    usable.find((e) => e.id.startsWith('foundry:') && e.loaded && e.device === 'NPU') ||
    usable.find((e) => e.id.startsWith('foundry:') && e.loaded) ||
    usable.find((e) => e.id.startsWith('openvino:') && e.loaded) ||
    usable.find((e) => e.id.startsWith('foundry:')) ||
    usable[0] ||
    engines[0]
  )?.id;
}

/**
 * Runs a prompt on an engine and streams deltas.
 * Resolves with { text, status, ttftMs, totalMs, chunks, tokens, tokensEstimated, tokensPerSec, meta }.
 */
export async function runEngine(engineId, { system, user, temperature, maxTokens } = {}, { onDelta, onStart, signal } = {}) {
  let t0 = performance.now();
  let ttft = null;
  let text = '';
  let chunks = 0;
  let usage = null;
  let status = 'Complete';
  let meta = {};

  const push = (delta) => {
    if (!delta) return;
    if (ttft === null) ttft = performance.now() - t0;
    text += delta;
    chunks += 1;
    onDelta?.(delta, text);
  };

  if (engineId === 'edge') {
    meta = { engine: 'Microsoft Edge', model: 'Built-in model', device: 'GPU/CPU' };
    onStart?.(meta);
    for await (const d of edge.promptStream(system, user, { signal })) push(d);
  } else {
    let path;
    let body;
    if (engineId === 'winai') {
      path = '/api/windows-ai/generate';
      body = { prompt: user, system, temperature };
    } else if (engineId === 'aion') {
      path = '/api/aion/generate';
      body = { prompt: user, system };
    } else if (engineId?.startsWith('foundry:')) {
      path = '/api/foundry/chat';
      const messages = [];
      if (system) messages.push({ role: 'system', content: system });
      messages.push({ role: 'user', content: user });
      body = { model: engineId.slice('foundry:'.length), messages, temperature, maxTokens };
    } else if (engineId?.startsWith('openvino:')) {
      path = '/api/openvino/chat';
      const messages = [];
      if (system) messages.push({ role: 'system', content: system });
      messages.push({ role: 'user', content: user });
      body = { model: engineId.slice('openvino:'.length), messages, temperature, maxTokens };
    } else {
      throw new Error(`Unknown engine '${engineId}'.`);
    }

    let error = null;
    await postSSE(path, body, {
      signal,
      onEvent: (e) => {
        if (e.type === 'start') {
          meta = e;
          if (e.loadMs) t0 += e.loadMs; // time spent loading the model into memory is not generation time
          onStart?.(e);
        } else if (e.type === 'delta') {
          push(e.text);
        } else if (e.type === 'done') {
          usage = e.usage ?? null;
          status = e.status || 'Complete';
          if (e.complete === false && status === 'Complete') status = 'Incomplete';
        } else if (e.type === 'error') {
          error = e.message || 'Unknown error';
        }
      },
    });
    if (error) throw new Error(error);
  }

  const totalMs = performance.now() - t0;
  const reported = usage?.completion_tokens ?? usage?.completionTokens ?? null;
  const tokens = reported ?? Math.max(1, Math.round(text.length / 4));
  const genMs = ttft !== null ? Math.max(1, totalMs - ttft) : Math.max(1, totalMs);
  return {
    text,
    status,
    ttftMs: ttft,
    totalMs,
    chunks,
    tokens,
    tokensEstimated: reported == null,
    tokensPerSec: chunks > 1 ? tokens / (genMs / 1000) : null,
    meta,
  };
}
