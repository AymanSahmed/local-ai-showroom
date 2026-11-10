// Small, safe Markdown renderer for model output: escapes all HTML, never creates links or images.

const escapeHtml = (s) =>
  String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function inline(escaped) {
  return escaped
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?!\w)/g, '$1<em>$2</em>');
}

export function renderMarkdown(source) {
  const lines = String(source ?? '').replace(/\r\n/g, '\n').split('\n');
  let html = '';
  let inCode = false;
  let code = [];
  let list = null;
  let para = [];

  const flushPara = () => {
    if (para.length) {
      html += `<p>${para.map((p) => inline(escapeHtml(p))).join('<br>')}</p>`;
      para = [];
    }
  };
  const flushList = () => {
    if (list) {
      html += `<${list.type}>${list.items.map((i) => `<li>${inline(escapeHtml(i))}</li>`).join('')}</${list.type}>`;
      list = null;
    }
  };

  for (const raw of lines) {
    if (raw.trim().startsWith('```')) {
      if (inCode) {
        html += `<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`;
        code = [];
        inCode = false;
      } else {
        flushPara();
        flushList();
        inCode = true;
      }
      continue;
    }
    if (inCode) {
      code.push(raw);
      continue;
    }

    const line = raw.trimEnd();
    if (!line.trim()) {
      flushPara();
      flushList();
      continue;
    }

    let m;
    if ((m = line.match(/^\s*(#{1,6})\s+(.*)$/))) {
      flushPara();
      flushList();
      const level = Math.min(m[1].length + 2, 6);
      html += `<h${level}>${inline(escapeHtml(m[2]))}</h${level}>`;
    } else if ((m = line.match(/^\s*[-*•]\s+(.*)$/))) {
      flushPara();
      if (!list || list.type !== 'ul') { flushList(); list = { type: 'ul', items: [] }; }
      list.items.push(m[1]);
    } else if ((m = line.match(/^\s*\d+[.)]\s+(.*)$/))) {
      flushPara();
      if (!list || list.type !== 'ol') { flushList(); list = { type: 'ol', items: [] }; }
      list.items.push(m[1]);
    } else {
      flushList();
      para.push(line.trim());
    }
  }

  if (inCode) html += `<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`;
  flushPara();
  flushList();
  return html;
}

/** Pretty JSON with light syntax colouring (input is escaped first). */
export function renderJson(value) {
  const json = escapeHtml(JSON.stringify(value, null, 2));
  return json.replace(
    /(&quot;(?:\\.|[^&]|&(?!quot;))*?&quot;)(\s*:)?|\b(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\b|\b(true|false|null)\b/g,
    (match, str, colon, num, lit) => {
      if (str) return colon ? `<span class="k">${str}</span>${colon}` : `<span class="s">${str}</span>`;
      if (num) return `<span class="n">${num}</span>`;
      if (lit) return `<span class="n">${lit}</span>`;
      return match;
    },
  );
}

/** Finds the first JSON object/array inside a model answer (tolerates code fences and prose). */
export function extractJson(text) {
  const cleaned = String(text ?? '').replace(/```(?:json)?/gi, '').trim();
  for (const [open, close] of [['{', '}'], ['[', ']']]) {
    const start = cleaned.indexOf(open);
    const end = cleaned.lastIndexOf(close);
    if (start >= 0 && end > start) {
      try { return JSON.parse(cleaned.slice(start, end + 1)); } catch { /* try next */ }
    }
  }
  return undefined;
}

export { escapeHtml };
