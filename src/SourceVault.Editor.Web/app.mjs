import { PreviewSession } from './state.mjs';

// Block clipboard and drag export before Monaco loads. preventDefault alone is not enough: a canceled
// copy event is how a page supplies its own clipboard data, and Monaco's textarea handlers fill and read
// clipboardData themselves, so they must never run. Monaco also calls the async Clipboard API directly,
// which fires no events, so that API is removed from this page.
for (const name of ['copy', 'cut', 'paste', 'dragstart', 'drop', 'contextmenu'])
  window.addEventListener(name, event => { event.preventDefault(); event.stopImmediatePropagation(); }, true);
window.addEventListener('beforeinput', event => {
  if (/^(insertFromPaste|insertFromDrop|insertFromYank|deleteByCut|deleteByDrag)/.test(event.inputType)) { event.preventDefault(); event.stopImmediatePropagation(); }
}, true);
Object.defineProperty(navigator, 'clipboard', { value: undefined, configurable: false });

const session = new PreviewSession();
const $ = id => document.getElementById(id);
const models = new Map();
const viewStates = new Map();
let editor;
let suppress = false;
let activityCount = 1;
const languageName = id => id === 'markdown' ? 'Markdown' : 'TypeScript';
const fileName = f => f.path.split('/').at(-1);
function postStatus(type = 'sourcevault.preview.status') {
  window.chrome?.webview?.postMessage({ type, ...session.metadata });
}
function announce(message) {
  $('footer-status').textContent = message;
  $('activity-message').textContent = message;
  $('activity-count').textContent = ++activityCount;
}
function render() {
  const active = session.activeId ? session.file(session.activeId) : null;
  $('tabs').replaceChildren(); $('source-files').replaceChildren(); $('root-files').replaceChildren();
  for (const f of session.files.values()) {
    const button = document.createElement('button');
    button.className = `file-button${f.id === session.activeId ? ' active' : ''}`;
    button.disabled = session.locked; button.title = f.path;
    const icon = document.createElement('span'); icon.className = 'file-type'; icon.textContent = f.language === 'markdown' ? 'M↓' : 'TS';
    const label = document.createElement('span'); label.textContent = fileName(f);
    button.append(icon, label);
    if (session.dirty(f.id)) { const dot = document.createElement('span'); dot.className = 'file-dirty'; dot.setAttribute('aria-label', 'Unsaved changes'); button.append(dot); }
    button.onclick = () => openFile(f.id);
    (f.path.startsWith('src/') ? $('source-files') : $('root-files')).append(button);
  }
  for (const id of session.openIds) {
    const f = session.file(id);
    const tab = document.createElement('div'); tab.className = `editor-tab${id === session.activeId ? ' active' : ''}`;
    const choose = document.createElement('button'); choose.className = 'tab-label'; choose.setAttribute('role', 'tab'); choose.setAttribute('aria-selected', String(id === session.activeId)); choose.textContent = fileName(f); choose.disabled = session.locked; choose.onclick = () => openFile(id);
    const icon = document.createElement('span'); icon.className = 'file-type'; icon.textContent = f.language === 'markdown' ? 'M↓' : 'TS';
    const close = document.createElement('button'); close.className = 'tab-close'; close.textContent = session.dirty(id) ? '●' : '×'; close.title = `Close ${fileName(f)}`; close.setAttribute('aria-label', `Close ${fileName(f)}${session.dirty(id) ? ', unsaved changes' : ''}`); close.disabled = session.locked;
    close.onclick = () => { try { session.close(id); syncModel(); render(); } catch (e) { if (e.message === 'UnsavedChanges') announce('Save a session revision before closing this edited file.'); else failed(e); } };
    tab.append(icon, choose, close); $('tabs').append(tab);
  }
  $('breadcrumb-file').textContent = active ? active.path.replaceAll('/', ' › ') : 'No file selected';
  $('document-state').textContent = active && session.dirty(active.id) ? 'Unsaved session edits' : 'Session copy';
  $('save').disabled = !editor || session.locked || !active || !session.dirty(active.id);
  $('revision-count').textContent = String(session.metadata.revisionCount).padStart(2, '0');
  $('language-detail').textContent = $('language-status').textContent = active ? languageName(active.language) : '—';
  $('locked-view').hidden = !session.locked; $('no-file').hidden = session.locked || !!active;
  $('lock').disabled = !editor || session.locked;
  document.body.classList.toggle('locked', session.locked);
  postStatus();
}
function syncModel() {
  if (!editor) return;
  if (editor.getModel()) viewStates.set(editor.getModel().uri.toString(), editor.saveViewState());
  const model = !session.locked && session.activeId ? models.get(session.activeId) : null;
  editor.setModel(model);
  if (model) { const saved = viewStates.get(model.uri.toString()); if (saved) editor.restoreViewState(saved); editor.focus(); }
}
// Mock-service failures. Messages are protocol error codes only, never document text.
function failed(e) {
  if (session.locked) { syncModel(); render(); announce("Mock service session ended. Editor locked; unsaved edits stay in this preview's memory. Unlock to start a new mock session."); $('unlock').focus(); }
  else if (e?.message === 'RevisionConflict') { render(); announce('Revision conflict: the saved revision changed. Nothing was saved; your edits are kept.'); }
  else { render(); announce('The mock service rejected the request. Nothing was changed.'); }
}
function openFile(id) { try { session.open(id); syncModel(); render(); } catch (e) { failed(e); } }
function save() {
  if (session.locked || !session.activeId || !editor) return;
  const f = session.file(session.activeId); const before = f.revision;
  try { session.save(f.id, f.revision); } catch (e) { failed(e); return; }
  render();
  announce(f.revision === before ? 'No unsaved session edits.' : `Revision ${f.revision} saved in memory for this session only (mock service). Refreshing discards it.`);
}
function lock() { session.lock(); syncModel(); render(); announce('Session locked. Mock service session ended; editor hidden; preview state retained in memory.'); $('unlock').focus(); }
let lastTouch = 0;
function touch() {
  const now = Date.now(); if (now - lastTouch < 30000) return; lastTouch = now;
  try { session.keepAlive(); } catch (e) { failed(e); }
}
function createModels() {
  suppress = true;
  for (const model of models.values()) model.dispose(); models.clear(); viewStates.clear();
  for (const f of session.files.values()) {
    const model = monaco.editor.createModel(f.text, f.language, monaco.Uri.parse(`file:///synthetic-workspace/${f.path}`));
    model.onDidChangeContent(() => { if (suppress || session.locked) return; session.edit(f.id, model.getValue()); render(); $('footer-status').textContent = 'Unsaved edits · session memory only'; touch(); });
    models.set(f.id, model);
  }
  suppress = false;
}
$('save').onclick = save; $('lock').onclick = lock;
$('unlock').onclick = () => { try { session.unlock(); } catch { render(); announce('Could not start a new mock session. Still locked.'); return; } syncModel(); render(); announce('New mock session started; synthetic session resumed. No real service connection.'); };
$('find').onclick = () => { if (!session.locked) editor?.getAction('actions.find').run(); };
$('explorer-toggle').onclick = () => { const hidden = !$('explorer').hidden; $('explorer').hidden = hidden; $('explorer-toggle').setAttribute('aria-pressed', String(!hidden)); $('explorer-toggle').classList.toggle('selected', !hidden); editor?.layout(); };
$('inspector-toggle').onclick = () => { const hidden = !$('inspector').hidden; $('inspector').hidden = hidden; $('inspector-toggle').setAttribute('aria-pressed', String(!hidden)); editor?.layout(); };
$('activity-toggle').onclick = () => { const expanded = $('activity-toggle').getAttribute('aria-expanded') !== 'true'; $('activity-toggle').setAttribute('aria-expanded', String(expanded)); $('activity').classList.toggle('collapsed', !expanded); $('activity-content').hidden = !expanded; $('activity-chevron').textContent = expanded ? '⌄' : '⌃'; editor?.layout(); };
$('reset').onclick = () => { if (!editor) return; editor.setModel(null); lastTouch = 0; session.reset(); createModels(); syncModel(); render(); announce('Synthetic workspace reset. Previous session edits and revisions discarded.'); };
window.chrome?.webview?.addEventListener('message', e => { if (e.data?.type === 'sourcevault.preview.lock') lock(); });
document.addEventListener('keydown', event => {
  const mod = event.ctrlKey || event.metaKey;
  if (mod && event.key.toLowerCase() === 's') { event.preventDefault(); save(); }
  if (mod && ['p', 'o'].includes(event.key.toLowerCase())) event.preventDefault();
}, true);
render();
try {
  await new Promise((resolve, reject) => {
    const start = () => {
      if (!window.require) { reject(new Error('Offline loader unavailable')); return; }
      window.require.config({ paths: { vs: './vendor/monaco/vs' } });
      window.require(['vs/editor/editor.main'], resolve, reject);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true }); else start();
  });
  monaco.editor.defineTheme('sourcevault', { base: 'vs-dark', inherit: true, rules: [{ token: 'comment', foreground: '60798A', fontStyle: 'italic' }, { token: 'keyword', foreground: 'B49ADF' }, { token: 'string', foreground: 'A7CB9C' }, { token: 'type.identifier', foreground: '79C6C1' }], colors: { 'editor.background': '#11151C', 'editor.foreground': '#BBC8D9', 'editorLineNumber.foreground': '#3F5064', 'editorLineNumber.activeForeground': '#ABC0D6', 'editor.lineHighlightBackground': '#1A222D', 'editor.selectionBackground': '#294650', 'editorCursor.foreground': '#7CE5CF', 'editorIndentGuide.background1': '#24303D', 'editorWidget.background': '#1A2430', 'editorWidget.border': '#344653', 'editorGutter.background': '#11151C', 'scrollbarSlider.background': '#3A48525A' } });
  // No library CDN: language workers and their compiler are bundled with Monaco.
  // Monaco 0.57 exposes defaults through its contribution module; the old
  // languages.typescript namespace is now a registration function.
  const typescript = await new Promise((resolve, reject) => window.require(['vs/language/typescript/monaco.contribution'], resolve, reject));
  const ts = typescript.typescriptDefaults;
  ts.setCompilerOptions({ strict: true, target: typescript.ScriptTarget.ES2022, module: typescript.ModuleKind.ESNext, moduleResolution: typescript.ModuleResolutionKind.NodeJs });
  // Imports can target unopened samples; synchronize every in-memory model to
  // the local language worker, rather than only the currently attached model.
  ts.setEagerModelSync(true);
  // Use Monaco's textarea input path for Windows accessibility and desktop
  // automation compatibility instead of Chromium's newer EditContext API.
  editor = monaco.editor.create($('editor'), { theme: 'sourcevault', model: null, automaticLayout: true, editContext: false, fontFamily: 'Cascadia Code, Consolas, monospace', fontSize: 13, lineHeight: 23, padding: { top: 12, bottom: 24 }, minimap: { enabled: true, renderCharacters: false, maxColumn: 80, scale: 1 }, scrollBeyondLastLine: false, contextmenu: false, dragAndDrop: false, dropIntoEditor: { enabled: false }, links: false, quickSuggestions: true, tabSize: 2, insertSpaces: true, bracketPairColorization: { enabled: true }, renderLineHighlight: 'line', overviewRulerBorder: false, hideCursorInOverviewRuler: true, smoothScrolling: true, fixedOverflowWidgets: true, ariaLabel: 'Synthetic source editor. Changes are kept in memory for this session only.' });
  createModels(); syncModel();
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
  editor.onDidChangeCursorPosition(e => { $('cursor-position').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`; });
  $('loading').hidden = true; render();
  $('footer-status').textContent = 'Session only · changes are discarded on close';
  postStatus('sourcevault.preview.ready');
} catch {
  $('loading').querySelector('h1').textContent = 'Offline editor could not load';
  $('loading').querySelector('p').textContent = 'Serve this folder over localhost with its bundled Monaco assets.';
  $('footer-status').textContent = 'Editor unavailable · no vault connection';
}
