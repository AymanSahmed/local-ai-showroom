// Thin client for the local bridge (same origin). Also runs under Node 20+ for the self-test.

let base = '';

/** Only used by tests/tools that run outside the page (e.g. node mock/selftest.mjs). */
export function setApiBase(url) {
  base = (url || '').replace(/\/$/, '');
}

export class ApiError extends Error {
  constructor(message, status, details) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.details = details;
  }
}

async function toError(res) {
  let text = '';
  try { text = await res.text(); } catch { /* ignore */ }
  try {
    const j = JSON.parse(text);
    return new ApiError(j.error || j.message || `HTTP ${res.status}`, res.status, j);
  } catch {
    return new ApiError(text || `HTTP ${res.status}`, res.status);
  }
}

export async function getJSON(path, { signal } = {}) {
  const res = await fetch(base + path, { signal, headers: { Accept: 'application/json' } });
  if (!res.ok) throw await toError(res);
  return res.json();
}

export async function postJSON(path, body, { signal } = {}) {
  const res = await fetch(base + path, {
    method: 'POST',
    signal,
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body ?? {}),
  });
  if (!res.ok) throw await toError(res);
  return res.json();
}

/**
 * POSTs JSON and consumes a text/event-stream response made of `data: {json}` events.
 * Event types used by the bridge: start | delta | done | error.
 */
export async function postSSE(path, body, { onEvent, signal } = {}) {
  const res = await fetch(base + path, {
    method: 'POST',
    signal,
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify(body ?? {}),
  });
  if (!res.ok) throw await toError(res);

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  const dispatch = (block) => {
    const data = block
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice(5).replace(/^ /, ''))
      .join('\n');
    if (!data) return;
    let evt;
    try { evt = JSON.parse(data); } catch { evt = { type: 'delta', text: data }; }
    onEvent?.(evt);
  };

  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let match;
    while ((match = /\r?\n\r?\n/.exec(buffer))) {
      const block = buffer.slice(0, match.index);
      buffer = buffer.slice(match.index + match[0].length);
      dispatch(block);
    }
  }
  buffer += decoder.decode();
  if (buffer.trim()) dispatch(buffer);
}
