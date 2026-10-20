// Shared by both modes: export blocking, offline Monaco loading, layout toggles and status messages.

export const $ = id => document.getElementById(id);

// Block clipboard and drag export before Monaco loads. preventDefault alone is not enough: a canceled
// copy event is how a page supplies its own clipboard data, and Monaco's textarea handlers fill and read
// clipboardData themselves, so they must never run. Monaco also calls the async Clipboard API directly,
// which fires no events, so that API is removed from this page.
//
// In-editor clipboard (owner decision 2026-10-20): copy and cut inside SourceVault keep the text in this page's memory
// only, and put CLIPBOARD_NOTICE on the Windows clipboard instead. Pasting the notice pastes the kept text; pasting
// anything else brings text in from other programs. So copying inside the editor works, and nothing copied here ever
// reaches the Windows clipboard. Drag-and-drop, the context menu and the async Clipboard API stay blocked.
export const CLIPBOARD_NOTICE = '[Copied inside SourceVault. It can be pasted only in the SourceVault editor; it is not on the Windows clipboard.]';
let kept = null; // { text, lines: string[] | null (one per cursor), wholeLine }

/** Forgets the in-editor clipboard (on lock). */
export function clearInternalClipboard() { kept = null; }

/** What a paste inserts: the kept copy when the Windows clipboard holds the notice, else the pasted text itself. */
export function pasteSource(clipboardText) {
  if (clipboardText === CLIPBOARD_NOTICE) return kept;
  return clipboardText ? { text: clipboardText, lines: null, wholeLine: false } : null;
}

/** The text a copy keeps from a Monaco editor: the selections, or the cursor's whole line when nothing is selected. */
export function copyFromEditor(editor) {
  const model = editor.getModel(), selections = editor.getSelections();
  if (!model || !selections?.length) return null;
  if (selections.every(s => s.isEmpty())) {
    // Like Monaco and VS Code: with no selection, copy the cursor's line, and paste it as a whole line.
    const lines = [...new Set(selections.map(s => s.startLineNumber))].sort((a, b) => a - b).map(n => model.getLineContent(n) + model.getEOL());
    return { text: lines.join(''), lines: null, wholeLine: true };
  }
  const parts = [...selections].sort((a, b) => a.startLineNumber - b.startLineNumber || a.startColumn - b.startColumn).map(s => model.getValueInRange(s));
  return { text: parts.join(model.getEOL()), lines: parts.length > 1 ? parts : null, wholeLine: false };
}

function focusedMonaco(target) {
  const editors = globalThis.monaco?.editor?.getEditors?.() ?? [];
  return editors.find(ed => ed.getDomNode()?.contains(target)) ?? null;
}

function isTextInput(el) {
  return el instanceof HTMLTextAreaElement || (el instanceof HTMLInputElement && /^(text|search|url|email|tel|)$/.test(el.type));
}

function onClipboard(event) {
  event.preventDefault(); event.stopImmediatePropagation(); // Monaco's own handlers never run
  const target = event.target, editor = target instanceof Node ? focusedMonaco(target) : null;
  if (event.type === 'paste') {
    const source = pasteSource(event.clipboardData?.getData('text/plain') ?? '');
    if (!source) return;
    if (editor) {
      if (editor.getOption(globalThis.monaco.editor.EditorOption.readOnly)) return;
      editor.trigger('keyboard', 'paste', { text: source.text, pasteOnNewLine: source.wholeLine, multicursorText: source.lines });
    } else if ((isTextInput(target) || (target instanceof HTMLInputElement && target.type === 'password')) && !target.readOnly && !target.disabled) {
      const text = target instanceof HTMLInputElement ? source.text.replace(/\r?\n/g, ' ') : source.text;
      target.setRangeText(text, target.selectionStart ?? target.value.length, target.selectionEnd ?? target.value.length, 'end');
      target.dispatchEvent(new Event('input', { bubbles: true }));
    }
    return;
  }
  // copy or cut
  let copied = null;
  if (editor) {
    copied = copyFromEditor(editor);
    if (copied && event.type === 'cut' && !editor.getOption(globalThis.monaco.editor.EditorOption.readOnly)) editor.trigger('keyboard', 'cut', null);
  } else if (isTextInput(target) && target.selectionStart !== target.selectionEnd) {
    copied = { text: target.value.slice(target.selectionStart, target.selectionEnd), lines: null, wholeLine: false };
    if (event.type === 'cut' && !target.readOnly && !target.disabled) {
      target.setRangeText('', target.selectionStart, target.selectionEnd, 'end');
      target.dispatchEvent(new Event('input', { bubbles: true }));
    }
  }
  if (!copied) return;
  kept = copied;
  event.clipboardData?.setData('text/plain', CLIPBOARD_NOTICE); // the only thing that reaches the Windows clipboard
}

// Dropping files from Explorer (owner request 2026-10-20): only on an element marked data-file-drop (the vault
// explorer), only files, and only when a handler is set. The page never reads them: it hands the File objects to the
// host (vault mode), which reads them, or to the demo vault, which makes files up instead. Every other drop, and every
// drag out of the page, stays blocked.
let fileDropHandler = null; // (files: File[], target: Element) => void
export function setFileDropHandler(handler) { fileDropHandler = handler; }
const dropZone = event => (fileDropHandler && event.target instanceof Element && event.dataTransfer?.types?.includes('Files') && event.target.closest('[data-file-drop]')) || null;

export function installExportBlocks() {
  for (const name of ['copy', 'cut', 'paste']) window.addEventListener(name, onClipboard, true);
  for (const name of ['dragstart', 'contextmenu'])
    window.addEventListener(name, event => { event.preventDefault(); event.stopImmediatePropagation(); }, true);
  for (const name of ['dragenter', 'dragover'])
    window.addEventListener(name, event => {
      event.preventDefault(); event.stopImmediatePropagation();
      if (event.dataTransfer) event.dataTransfer.dropEffect = dropZone(event) ? 'copy' : 'none';
    }, true);
  window.addEventListener('drop', event => {
    event.preventDefault(); event.stopImmediatePropagation();
    const zone = dropZone(event), files = [...(event.dataTransfer?.files ?? [])];
    if (zone && files.length) fileDropHandler(files, event.target);
  }, true);
  window.addEventListener('beforeinput', event => {
    if (/^(insertFromPaste|insertFromDrop|insertFromYank|deleteByCut|deleteByDrag)/.test(event.inputType)) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: false });
}

// Network APIs the editor never uses. The page CSP, the native host's request filter and its dead-proxy browser
// arguments already block their traffic; removing them first is one more layer, and the only page-level one for
// WebRTC, whose STUN/peer traffic CSP does not cover. Same-origin fetch/XHR stay: Monaco loads its own files.
// This is page script, not a boundary: Monaco's workers keep their own globals (CSP and the host still apply there).
export const blockedNetworkGlobals = ['WebSocket', 'WebSocketStream', 'WebTransport', 'EventSource', 'RTCPeerConnection', 'webkitRTCPeerConnection',
  'RTCDataChannel', 'RTCIceTransport', 'SharedWorker', 'PresentationRequest'];

export function installNetworkBlocks(scope = globalThis) {
  const lock = (target, name, value) => Object.defineProperty(target, name, { value, writable: false, configurable: false });
  for (const name of blockedNetworkGlobals) lock(scope, name, undefined);
  lock(scope, 'open', () => null); // Monaco's link opener calls window.open; the host refuses new windows as well
  const nav = scope.navigator;
  if (nav) {
    lock(nav, 'sendBeacon', () => false);
    lock(nav, 'serviceWorker', undefined);
  }
}

let activityCount = 1;
/** Status bar and activity panel. Messages are fixed UI text and names, never document content. */
export function announce(message) {
  $('footer-status').textContent = message;
  $('activity-message').textContent = message;
  $('activity-count').textContent = ++activityCount;
}

/** The four-field status message the host accepts (PreviewPolicy): no names, no text. */
export function postStatus(metadata, type = 'sourcevault.preview.status') {
  window.chrome?.webview?.postMessage({ type, locked: metadata.locked, dirtyCount: metadata.dirtyCount, revisionCount: metadata.revisionCount });
}

export const languageLabel = id => ({ typescript: 'TypeScript', javascript: 'JavaScript', markdown: 'Markdown', json: 'JSON', css: 'CSS', html: 'HTML', plaintext: 'Plain text',
  csharp: 'C#', cpp: 'C++', c: 'C', python: 'Python', xml: 'XML', yaml: 'YAML', sql: 'SQL', powershell: 'PowerShell', shell: 'Shell script', bat: 'Batch', ini: 'INI', dockerfile: 'Dockerfile' })[id] ?? id;

/** Loads the bundled Monaco (no CDN) and its local TypeScript workers. Resolves to the global monaco. */
export async function loadMonaco() {
  await new Promise((resolve, reject) => {
    const start = () => {
      if (!window.require) { reject(new Error('Offline loader unavailable')); return; }
      window.require.config({ paths: { vs: './vendor/monaco/vs' } });
      window.require(['vs/editor/editor.main'], resolve, reject);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true }); else start();
  });
  monaco.editor.defineTheme('sourcevault', { base: 'vs-dark', inherit: true, rules: [{ token: 'comment', foreground: '60798A', fontStyle: 'italic' }, { token: 'keyword', foreground: 'B49ADF' }, { token: 'string', foreground: 'A7CB9C' }, { token: 'type.identifier', foreground: '79C6C1' }], colors: { 'editor.background': '#11151C', 'editor.foreground': '#BBC8D9', 'editorLineNumber.foreground': '#3F5064', 'editorLineNumber.activeForeground': '#ABC0D6', 'editor.lineHighlightBackground': '#1A222D', 'editor.selectionBackground': '#294650', 'editorCursor.foreground': '#7CE5CF', 'editorIndentGuide.background1': '#24303D', 'editorWidget.background': '#1A2430', 'editorWidget.border': '#344653', 'editorGutter.background': '#11151C', 'scrollbarSlider.background': '#3A48525A' } });
  // Demo mode: the same theme with a red cursor, line and selection, so a demo is never mistaken for the vault.
  monaco.editor.defineTheme('sourcevault-demo', { base: 'vs-dark', inherit: true, rules: [{ token: 'comment', foreground: '8A6F6F', fontStyle: 'italic' }, { token: 'keyword', foreground: 'E79AB4' }, { token: 'string', foreground: 'A7CB9C' }, { token: 'type.identifier', foreground: 'E0B57D' }], colors: { 'editor.background': '#170F12', 'editor.foreground': '#D9C7CB', 'editorLineNumber.foreground': '#644048', 'editorLineNumber.activeForeground': '#F0A0A8', 'editor.lineHighlightBackground': '#2A1519', 'editor.selectionBackground': '#6B2730', 'editorCursor.foreground': '#FF5C5C', 'editorIndentGuide.background1': '#3A2328', 'editorWidget.background': '#26161A', 'editorWidget.border': '#6B2D35', 'editorGutter.background': '#170F12', 'minimap.background': '#170F12', 'scrollbarSlider.background': '#6B2D355A' } });
  // No library CDN: language workers and their compiler are bundled with Monaco.
  // Monaco 0.57 exposes defaults through its contribution module; the old
  // languages.typescript namespace is now a registration function.
  const typescript = await new Promise((resolve, reject) => window.require(['vs/language/typescript/monaco.contribution'], resolve, reject));
  const ts = typescript.typescriptDefaults;
  ts.setCompilerOptions({ strict: true, target: typescript.ScriptTarget.ES2022, module: typescript.ModuleKind.ESNext, moduleResolution: typescript.ModuleResolutionKind.NodeJs });
  // Imports can target unopened files; synchronize every in-memory model to
  // the local language worker, rather than only the currently attached model.
  ts.setEagerModelSync(true);
  return monaco;
}

export function createEditor(ariaLabel, container = $('editor')) {
  // Use Monaco's textarea input path for Windows accessibility and desktop
  // automation compatibility instead of Chromium's newer EditContext API.
  return monaco.editor.create(container, { theme: 'sourcevault', model: null, automaticLayout: true, editContext: false, fontFamily: 'Cascadia Code, Consolas, monospace', fontSize: 13, lineHeight: 23, padding: { top: 12, bottom: 24 }, minimap: { enabled: true, renderCharacters: false, maxColumn: 80, scale: 1 }, scrollBeyondLastLine: false, contextmenu: false, dragAndDrop: false, dropIntoEditor: { enabled: false }, links: false, quickSuggestions: true, tabSize: 2, insertSpaces: true, bracketPairColorization: { enabled: true }, renderLineHighlight: 'line', overviewRulerBorder: false, hideCursorInOverviewRuler: true, smoothScrolling: true, fixedOverflowWidgets: true, ariaLabel });
}

/** A read-only side-by-side diff (file history). Same blocked context menu and drag paths as the main editor. */
export function createDiffEditor(container, ariaLabel) {
  return monaco.editor.createDiffEditor(container, { theme: 'sourcevault', readOnly: true, originalEditable: false, automaticLayout: true, editContext: false, renderSideBySide: true, fontFamily: 'Cascadia Code, Consolas, monospace', fontSize: 13, lineHeight: 21, minimap: { enabled: false }, scrollBeyondLastLine: false, contextmenu: false, dragAndDrop: false, dropIntoEditor: { enabled: false }, links: false, renderOverviewRuler: false, ariaLabel });
}

/** A read-only plain-text viewer (build output), with the same blocked context menu and drag paths. */
export function createLogViewer(container, ariaLabel) {
  return monaco.editor.create(container, { theme: 'sourcevault', model: null, readOnly: true, automaticLayout: true, editContext: false, fontFamily: 'Cascadia Code, Consolas, monospace', fontSize: 12, lineHeight: 19, minimap: { enabled: false }, scrollBeyondLastLine: false, contextmenu: false, dragAndDrop: false, dropIntoEditor: { enabled: false }, links: false, wordWrap: 'on', renderLineHighlight: 'none', ariaLabel });
}

/**
 * Explorer/inspector/activity panel toggles, find, and blocked browser shortcuts (Ctrl+S calls onSave). `skipKey`
 * returns true for a key that belongs to a chord (Ctrl+K Ctrl+S), which is not a save.
 */
export function wireChrome(getEditor, { onSave, isLocked, skipKey = () => false }) {
  $('find').onclick = () => { if (!isLocked()) getEditor()?.getAction('actions.find').run(); };
  $('explorer-toggle').onclick = () => { const hidden = !$('explorer').hidden; $('explorer').hidden = hidden; $('explorer-toggle').setAttribute('aria-pressed', String(!hidden)); $('explorer-toggle').classList.toggle('selected', !hidden); getEditor()?.layout(); };
  $('inspector-toggle').onclick = () => { const hidden = !$('inspector').hidden; $('inspector').hidden = hidden; $('inspector-toggle').setAttribute('aria-pressed', String(!hidden)); getEditor()?.layout(); };
  $('activity-toggle').onclick = () => { const expanded = $('activity-toggle').getAttribute('aria-expanded') !== 'true'; $('activity-toggle').setAttribute('aria-expanded', String(expanded)); $('activity').classList.toggle('collapsed', !expanded); $('activity-content').hidden = !expanded; $('activity-chevron').textContent = expanded ? '⌄' : '⌃'; getEditor()?.layout(); };
  document.addEventListener('keydown', event => {
    const mod = event.ctrlKey || event.metaKey;
    if (mod && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 's' && !skipKey(event)) { event.preventDefault(); onSave(); }
    if (mod && ['p', 'o'].includes(event.key.toLowerCase())) event.preventDefault();
  }, true);
}

export function showLoadFailure() {
  $('loading').querySelector('h1').textContent = 'Offline editor could not load';
  $('loading').querySelector('p').textContent = 'Serve this folder over localhost with its bundled Monaco assets.';
  $('footer-status').textContent = 'Editor unavailable';
}
