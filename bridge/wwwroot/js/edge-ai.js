// Microsoft Edge built-in AI (Prompt API, Writing Assistance, Translator) - runs inside the browser itself.
// Edge Canary/Dev: enable edge://flags "Prompt API for on-device language model"
// (and "Enable prerelease on-device language model" for Aion-1.0-Instruct, Edge 150.0.4070+).

const g = globalThis;
const PROBE_TIMEOUT_MS = 8000;
const TIMED_OUT = Symbol('timed out');

/** Maps the browser API class name to the key used in the status object. */
export const API_KINDS = { LanguageModel: 'prompt', Summarizer: 'summarizer', LanguageDetector: 'languageDetector', Translator: 'translator' };

const lateListeners = new Set();
/** Called as (className, args, state) when a probe that already reported 'timeout' finally answers. */
export function onLateAvailability(listener) {
  lateListeners.add(listener);
}

async function probe(name, args) {
  const api = g[name];
  if (!api || typeof api.availability !== 'function') return 'missing';
  try {
    // Some browser builds are slow or never settle availability(); don't let one API block the whole status page.
    const pending = Promise.resolve(api.availability(args));
    const result = await Promise.race([pending, new Promise((resolve) => setTimeout(() => resolve(TIMED_OUT), PROBE_TIMEOUT_MS))]);
    if (result === TIMED_OUT) {
      pending.then((late) => lateListeners.forEach((listener) => listener(name, args, late)), () => {});
      return 'timeout';
    }
    return result;
  } catch {
    return 'error';
  }
}

/** BCP 47 codes offered in the Translator picker. Support per pair is decided by the browser (see translatorAvailability). */
export const TRANSLATOR_LANGUAGES = [
  ['en', 'English'], ['ar', 'Arabic'], ['bg', 'Bulgarian'], ['zh', 'Chinese (Simplified)'], ['zh-Hant', 'Chinese (Traditional)'],
  ['cs', 'Czech'], ['da', 'Danish'], ['nl', 'Dutch'], ['fi', 'Finnish'], ['fr', 'French'], ['de', 'German'], ['el', 'Greek'],
  ['he', 'Hebrew'], ['hi', 'Hindi'], ['hu', 'Hungarian'], ['id', 'Indonesian'], ['it', 'Italian'], ['ja', 'Japanese'],
  ['ko', 'Korean'], ['pl', 'Polish'], ['pt', 'Portuguese'], ['ro', 'Romanian'], ['ru', 'Russian'], ['es', 'Spanish'],
  ['sv', 'Swedish'], ['th', 'Thai'], ['tr', 'Turkish'], ['uk', 'Ukrainian'], ['vi', 'Vietnamese'], ['bn', 'Bengali'],
];

/** Availability of one Translator language pair: 'available' | 'downloadable' | 'downloading' | 'unavailable' | 'missing' | 'error'. */
export function translatorAvailability(sourceLanguage, targetLanguage) {
  return probe('Translator', { sourceLanguage, targetLanguage });
}

/** Availability per API: 'available' | 'downloadable' | 'downloading' | 'unavailable' | 'missing' | 'error'. */
export async function availability(pair = { source: 'en', target: 'ar' }) {
  const [prompt, summarizer, translator, languageDetector] = await Promise.all([
    probe('LanguageModel'),
    probe('Summarizer'),
    translatorAvailability(pair.source, pair.target),
    probe('LanguageDetector'),
  ]);
  return { prompt, summarizer, translator, languageDetector };
}

/**
 * Creates a Translator for one language pair. When the pair is 'downloadable' this starts the language-pack
 * download, which the browser only allows from a user gesture (call it from a click handler).
 */
async function createTranslator(sourceLanguage, targetLanguage, { signal, onDownloadProgress } = {}) {
  if (!g.Translator) throw new Error('The Translator API is not available in this browser.');
  const options = {
    sourceLanguage,
    targetLanguage,
    monitor(m) {
      m.addEventListener('downloadprogress', (e) => onDownloadProgress?.(e.total ? e.loaded / e.total : e.loaded));
    },
  };
  if (signal) options.signal = signal;
  return g.Translator.create(options);
}

const API_CLASSES = { prompt: 'LanguageModel', summarizer: 'Summarizer', languageDetector: 'LanguageDetector' };

/** Re-checks one API (kind: 'prompt' | 'summarizer' | 'languageDetector'). */
export function probeApi(kind) {
  return probe(API_CLASSES[kind]);
}

const REQUIREMENTS = {
  prompt: 'The Prompt API needs Edge Dev/Canary (138+) with its flag enabled in edge://flags, about 20 GB free on the drive that holds your Edge profile, and a device performance class of High (or the prerelease Aion model on Edge 150.0.4070+ with its flag). Open edge://on-device-internals and check “Device performance class” and “Model Status”.',
  summarizer: 'The Summarizer is part of the Writing Assistance APIs: it needs Edge Dev/Canary (138+) with the matching flag enabled in edge://flags and uses the same on-device model, so the Prompt API hardware and storage requirements apply. Open edge://on-device-internals to see the model status.',
  languageDetector: 'The Language Detector is a recent addition to Edge Dev/Canary: update Edge and enable the matching flag in edge://flags (search “Language Detector” or “built-in AI”). Its small model is downloaded once, so the PC must be online the first time. Check edge://components for updates and edge://on-device-internals.',
};

/** Explains a status that needs attention ('unavailable' | 'timeout' | 'error'); returns '' for healthy states. */
export function statusHint(kind, state) {
  if (state === 'timeout') {
    return `Edge didn't answer in ${PROBE_TIMEOUT_MS / 1000} s. It may still be checking (the row updates if it answers later), the model may be waiting for a download that needs internet, or this build doesn't fully support the API. Click Re-check, or restart Edge. ${REQUIREMENTS[kind] ?? ''}`;
  }
  if (state === 'unavailable') return `Edge reports this model as unavailable on this PC. ${REQUIREMENTS[kind] ?? ''}`;
  if (state === 'error') return `Edge raised an error while checking this API. Click Re-check. ${REQUIREMENTS[kind] ?? ''}`;
  return '';
}

/** Turns a failed create() into an actionable message. */
export function failureHint(kind, error) {
  const name = error?.name ?? '';
  const message = error?.message ?? String(error);
  if (name === 'NotAllowedError') return 'Edge only starts a model download from a click while this page is focused. Click the button again.';
  if (name === 'QuotaExceededError' || /space|quota/i.test(message)) return 'Not enough free disk space on the drive that holds your Edge profile.';
  if (name === 'NetworkError' || /network|offline|fetch/i.test(message)) return 'The model could not be downloaded. Check that this PC is online and that a proxy or firewall is not blocking Edge.';
  if (/not available|unavailable|NotSupported/i.test(`${name} ${message}`)) {
    return `Edge cannot provide this model on this PC (it is not downloaded and Edge refused or could not fetch it). ${REQUIREMENTS[kind] ?? ''}`;
  }
  return REQUIREMENTS[kind] ?? '';
}

/**
 * Downloads the on-device model behind one built-in AI API (kind: 'prompt' | 'summarizer' | 'languageDetector').
 * The browser only allows this from a user gesture, so call it directly from a click handler.
 */
export async function downloadModel(kind, { onDownloadProgress } = {}) {
  const api = g[API_CLASSES[kind]];
  if (!api) throw new Error(`The ${API_CLASSES[kind] ?? kind} API is not available in this browser.`);
  const instance = await api.create({
    monitor(m) {
      m.addEventListener('downloadprogress', (e) => onDownloadProgress?.(e.total ? e.loaded / e.total : e.loaded));
    },
  });
  instance.destroy?.();
}

/** Downloads the language pack for a pair without translating anything. */
export async function downloadTranslator(sourceLanguage, targetLanguage, opts) {
  const translator = await createTranslator(sourceLanguage, targetLanguage, opts);
  translator.destroy?.();
}

/** Translates text on-device (downloads the language pack first when needed). */
export async function translateText(text, sourceLanguage, targetLanguage, opts = {}) {
  const translator = await createTranslator(sourceLanguage, targetLanguage, opts);
  try {
    return await translator.translate(text, opts.signal ? { signal: opts.signal } : undefined);
  } finally {
    translator.destroy?.();
  }
}

export function promptUsable(state) {
  return ['available', 'downloadable', 'downloading'].includes(state?.prompt);
}

/**
 * Streams an answer from the browser's built-in model. Yields text deltas.
 * Handles both delta-style and cumulative-style streaming implementations.
 */
export async function* promptStream(system, user, { signal, onDownloadProgress } = {}) {
  if (!g.LanguageModel) throw new Error('The Prompt API is not available in this browser.');
  const options = {
    monitor(m) {
      m.addEventListener('downloadprogress', (e) => onDownloadProgress?.(e.total ? e.loaded / e.total : e.loaded));
    },
  };
  if (system) options.initialPrompts = [{ role: 'system', content: system }];
  if (signal) options.signal = signal;

  const session = await g.LanguageModel.create(options);
  try {
    const stream = session.promptStreaming(user, signal ? { signal } : undefined);
    let soFar = '';
    for await (const chunk of stream) {
      const text = String(chunk);
      const delta = soFar && text.startsWith(soFar) ? text.slice(soFar.length) : text;
      soFar += delta;
      if (delta) yield delta;
    }
  } finally {
    session.destroy?.();
  }
}
