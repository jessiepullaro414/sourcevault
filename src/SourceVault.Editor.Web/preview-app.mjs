// Synthetic preview mode: bundled sample files over the in-renderer protocol mock (state.mjs). Unchanged behavior.
import { PreviewSession } from './state.mjs';
import { $, announce, postStatus as post, languageLabel, loadMonaco, createEditor, wireChrome, showLoadFailure } from './shell.mjs';

export async function startPreview() {
  document.title = 'SourceVault — Editor preview';
  const session = new PreviewSession();
  const models = new Map();
  const viewStates = new Map();
  let editor;
  let suppress = false;
  const fileName = f => f.path.split('/').at(-1);
  const postStatus = (type = 'sourcevault.preview.status') => post(session.metadata, type);

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
    $('language-detail').textContent = $('language-status').textContent = active ? languageLabel(active.language) : '—';
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
  $('reset').onclick = () => { if (!editor) return; editor.setModel(null); lastTouch = 0; session.reset(); createModels(); syncModel(); render(); announce('Synthetic workspace reset. Previous session edits and revisions discarded.'); };
  window.chrome?.webview?.addEventListener('message', e => { if (e.data?.type === 'sourcevault.preview.lock') lock(); });
  wireChrome(() => editor, { onSave: save, isLocked: () => session.locked });
  render();
  try {
    await loadMonaco();
    editor = createEditor('Synthetic source editor. Changes are kept in memory for this session only.');
    createModels(); syncModel();
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
    editor.onDidChangeCursorPosition(e => { $('cursor-position').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`; });
    $('loading').hidden = true; render();
    $('footer-status').textContent = 'Session only · changes are discarded on close';
    postStatus('sourcevault.preview.ready');
  } catch {
    showLoadFailure();
  }
}
