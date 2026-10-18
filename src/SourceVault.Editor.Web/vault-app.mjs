// Vault mode UI (docs/editor-vault-design.md). The host owns the service connection; this page sees project/file ids,
// names and the text of files it opens. Nothing here logs or posts document text anywhere but the host bridge.
import { VaultSession } from './vault-session.mjs';
import { $, announce, postStatus, languageLabel, loadMonaco, createEditor, wireChrome, showLoadFailure } from './shell.mjs';

const UNLOCK_FAILURES = {
  ServiceNotInstalled: 'The SourceVault service is not installed on this computer.',
  ServiceNotRunning: 'The SourceVault service is not running. Ask an administrator to start it, then unlock.',
  AccessDenied: "This Windows sign-in is not in 'SourceVault Operators'. After being added, sign out and back in.",
  Busy: 'All service connections are in use. Try again shortly.',
  ServerUntrusted: 'The vault pipe is not owned by the SourceVault service. The editor refused to connect.',
  ClientRejected: 'The service only accepts the installed SourceVault editor.',
  Timeout: 'The editor host did not answer.',
};
const KEPT = 'Unsaved edits stay in this editor’s memory and are checked against the vault when you unlock. Locking hides them; it does not guarantee memory erasure.';
const LOCK_SCREENS = {
  start: ['VAULT', 'Connecting to the vault', 'Documents are decrypted by the SourceVault service, and only for files you open.'],
  user: ['SESSION LOCKED', 'Your workspace is locked', KEPT],
  windows: ['LOCKED WITH WINDOWS', 'Locked when Windows locked', KEPT],
  expired: ['SESSION EXPIRED', 'Your vault session expired', 'Sessions end after 5 idle minutes or 30 minutes in total. ' + KEPT],
  closed: ['DISCONNECTED', 'The vault connection closed', KEPT],
};
const FAILURES = {
  NotFound: 'That item no longer exists in the vault.',
  LimitExceeded: 'Files are limited to 256 KiB.',
  InvalidRequest: 'The vault rejected that name or content.',
  AlreadyExists: 'That name is already taken (names are not case-sensitive).',
  UnsavedChanges: 'Save or reload the edited files first.',
  Timeout: 'The editor host did not answer. Nothing was changed.',
};

/** `onSession` is passed only by the browser-review fake host, so its panel can target the open file. */
export async function startVault(transport, metadata, onSession) {
  const session = new VaultSession(transport);
  onSession?.(session);
  const models = new Map();     // fileId -> Monaco model (only for opened files)
  const viewStates = new Map();
  const keepEditing = new Set(); // files whose conflict banner was dismissed
  let editor, suppress = false, busy = false, picking = false, unlockError = null;
  const active = () => (session.activeId ? session.files.get(session.activeId) : null);

  document.title = 'SourceVault — Vault editor';
  document.body.classList.add('mode-vault');
  relabel(metadata);

  async function act(work, after) {
    if (busy) return;
    busy = true; render();
    try { const message = await work(); if (message) announce(message); }
    catch (e) { failed(e); }
    finally { busy = false; syncModel(); render(); after?.(); }
  }
  function failed(e) {
    const code = e?.code ?? 'InternalError';
    if (session.locked) announce('The vault session is locked. Unsaved edits are kept.');
    else if (code === 'Conflict') { keepEditing.delete(session.activeId); announce('Not saved: someone else changed this file since you opened it.'); }
    else announce(FAILURES[code] ?? `The vault refused the request (${code}). Nothing was changed.`);
  }

  // ---- rendering -------------------------------------------------------------------------------------------------
  function render() {
    const locked = session.locked, hasProject = !!session.project && !picking;
    const f = hasProject ? active() : null; // nothing is "current" while the picker is open
    const [eyebrow, title, detail] = LOCK_SCREENS[session.lockReason] ?? LOCK_SCREENS.closed;
    $('locked-view').hidden = !locked;
    $('locked-eyebrow').textContent = eyebrow; $('locked-title').textContent = title;
    $('locked-detail').textContent = unlockError ?? detail;
    $('unlock').disabled = busy;
    $('project-picker').hidden = locked || hasProject;
    if (!locked && !hasProject) renderPicker();
    $('no-file').hidden = locked || !hasProject || !!f;
    $('workspace-name').textContent = $('breadcrumb-root').textContent = session.project?.name ?? 'No project';
    $('project-heading').textContent = (session.project?.name ?? 'No project').toUpperCase();
    $('new-file').disabled = $('switch-project').disabled = locked || busy || !hasProject;
    $('lock').disabled = locked || busy;

    $('tabs').replaceChildren(); $('source-files').replaceChildren(); $('root-files').replaceChildren();
    if (hasProject) {
      for (const file of [...session.files.values()].sort((a, b) => a.path.localeCompare(b.path))) {
        const button = document.createElement('button');
        button.className = `file-button${file.id === session.activeId ? ' active' : ''}`;
        button.disabled = locked || busy; button.title = file.path;
        const icon = document.createElement('span'); icon.className = 'file-type'; icon.textContent = typeIcon(file.language);
        const label = document.createElement('span'); label.textContent = file.path;
        button.append(icon, label);
        if (file.conflict) { const mark = document.createElement('span'); mark.className = 'file-conflict'; mark.textContent = '!'; mark.setAttribute('aria-label', 'Changed by someone else'); button.append(mark); }
        else if (session.dirty(file.id)) { const dot = document.createElement('span'); dot.className = 'file-dirty'; dot.setAttribute('aria-label', 'Unsaved changes'); button.append(dot); }
        button.onclick = () => act(async () => { await session.open(file.id); });
        $('root-files').append(button);
      }
      for (const id of session.openIds) {
        const file = session.files.get(id); if (!file) continue;
        const dirty = session.dirty(id);
        const tab = document.createElement('div'); tab.className = `editor-tab${id === session.activeId ? ' active' : ''}`;
        const icon = document.createElement('span'); icon.className = 'file-type'; icon.textContent = typeIcon(file.language);
        const choose = document.createElement('button'); choose.className = 'tab-label'; choose.setAttribute('role', 'tab'); choose.setAttribute('aria-selected', String(id === session.activeId)); choose.textContent = file.path; choose.disabled = locked;
        choose.onclick = () => { session.activeId = id; syncModel(); render(); };
        const close = document.createElement('button'); close.className = 'tab-close'; close.textContent = dirty ? '●' : '×'; close.title = `Close ${file.path}`; close.setAttribute('aria-label', `Close ${file.path}${dirty ? ', unsaved changes' : ''}`); close.disabled = locked || busy;
        close.onclick = () => dirty ? announce('Save or reload this file before closing it.') : act(async () => { await session.close(id); dropModel(id); });
        tab.append(icon, choose, close); $('tabs').append(tab);
      }
    }
    $('breadcrumb-file').textContent = f ? f.path : 'No file selected';
    $('document-state').textContent = !f ? '' : f.conflict ? 'Changed by someone else' : session.dirty(f.id) ? 'Unsaved edits' : 'Saved in vault';
    $('conflict-banner').hidden = locked || !f || !f.conflict || keepEditing.has(f.id);
    $('save').disabled = !editor || locked || busy || !f || !session.dirty(f.id);
    $('revision-count').textContent = String(session.savedCount).padStart(2, '0');
    $('files-detail').textContent = hasProject ? `${session.files.size} in project` : '—';
    $('language-detail').textContent = $('language-status').textContent = f ? languageLabel(f.language) : '—';
    document.body.classList.toggle('locked', locked);
    postStatus(session.metadata); // four numeric/boolean fields only; also drives the host's keep-alive
  }

  function renderPicker() {
    const list = $('project-list');
    list.replaceChildren();
    if (!session.projects.length) { const p = document.createElement('p'); p.className = 'project-empty'; p.textContent = 'No projects yet. Create one below.'; list.append(p); }
    for (const project of session.projects) {
      const button = document.createElement('button'); button.className = 'project-option'; button.disabled = busy;
      const name = document.createElement('strong'); name.textContent = project.name;
      const count = document.createElement('span'); count.textContent = `${project.fileCount} file${project.fileCount === 1 ? '' : 's'}`;
      button.append(name, count);
      button.onclick = () => act(async () => {
        if (session.project?.projectId !== project.projectId) { await session.selectProject(project.projectId); disposeAllModels(); keepEditing.clear(); }
        picking = false;
        return `Opened project "${project.name}".`;
      });
      list.append(button);
    }
  }

  // ---- Monaco models: one per opened file; hidden (not disposed) while locked ------------------------------------
  function syncModel() {
    if (!editor) return;
    for (const id of session.openIds) {
      const file = session.files.get(id);
      if (!file?.loaded) continue;
      let model = models.get(id);
      if (!model) {
        model = monaco.editor.createModel(file.text, file.language, monaco.Uri.parse(`file:///vault/${session.project.projectId}/${id}/${encodeURIComponent(file.path)}`));
        model.onDidChangeContent(() => {
          if (suppress || session.locked) return;
          session.edit(id, model.getValue()); render();
          $('footer-status').textContent = 'Unsaved edits · not yet in the vault';
        });
        models.set(id, model);
      } else if (model.getValue() !== file.text) { suppress = true; model.setValue(file.text); suppress = false; } // reload
    }
    if (editor.getModel()) viewStates.set(editor.getModel().uri.toString(), editor.saveViewState());
    const model = !session.locked && !picking && session.activeId ? models.get(session.activeId) ?? null : null;
    if (editor.getModel() !== model) {
      editor.setModel(model);
      if (model) { const saved = viewStates.get(model.uri.toString()); if (saved) editor.restoreViewState(saved); editor.focus(); }
    }
  }
  function dropModel(id) { const model = models.get(id); if (model) { if (editor?.getModel() === model) editor.setModel(null); viewStates.delete(model.uri.toString()); model.dispose(); models.delete(id); } }
  function disposeAllModels() { editor?.setModel(null); for (const id of [...models.keys()]) dropModel(id); }

  // ---- actions ---------------------------------------------------------------------------------------------------
  function unlock() {
    act(async () => {
      try { var changed = await session.unlock(); unlockError = null; }
      catch (e) { unlockError = UNLOCK_FAILURES[e?.code] ?? `Could not connect to the vault (${e?.code ?? 'InternalError'}).`; return 'Still locked.'; }
      for (const id of changed) keepEditing.delete(id);
      return changed.length ? `Unlocked. ${changed.length} open file(s) changed in the vault while locked; saving them will conflict until you reload.` : 'Connected to the vault.';
    }, () => { if (!session.locked && !session.project) $('new-project-name').focus(); });
  }
  function save() {
    const f = active();
    if (!f || session.locked || !session.dirty(f.id)) return;
    act(async () => (await session.save(f.id)) ? `Saved ${f.path} to the vault as a new encrypted revision.` : 'No unsaved edits.');
  }
  $('unlock').onclick = unlock;
  $('lock').onclick = () => act(async () => { await session.lock(); return 'Locked. The service session ended; unsaved edits are kept in this editor.'; });
  $('save').onclick = save;
  $('conflict-reload').onclick = () => { const f = active(); if (f) act(async () => { await session.reload(f.id); keepEditing.delete(f.id); return `Reloaded ${f.path}: your unsaved edits were discarded.`; }); };
  $('conflict-keep').onclick = () => { const f = active(); if (f) { keepEditing.add(f.id); render(); announce('Keeping your edits. Saving will still conflict; reload to take their version.'); } };
  $('switch-project').onclick = () => {
    if (session.dirtyIds.length) { announce(FAILURES.UnsavedChanges); return; }
    act(async () => { await session.refreshProjects(); picking = true; });
  };
  $('new-file').onclick = () => { $('new-file-form').hidden = false; $('new-file-name').value = ''; $('new-file-name').focus(); };
  $('new-file-form').onsubmit = event => {
    event.preventDefault();
    const name = $('new-file-name').value.trim();
    if (!name) { $('new-file-form').hidden = true; return; }
    act(async () => { await session.createFile(name, ''); $('new-file-form').hidden = true; return `Created ${name} in the vault.`; });
  };
  $('new-project-form').onsubmit = event => {
    event.preventDefault();
    const name = $('new-project-name').value.trim();
    if (!name) return;
    act(async () => { await session.createProject(name); disposeAllModels(); keepEditing.clear(); picking = false; $('new-project-name').value = ''; return `Created project "${name}".`; });
  };
  session.onChange(() => {
    syncModel(); render();
    if (session.locked) { announce(`Vault locked (${session.lockReason}). Unsaved edits are kept.`); $('unlock').focus(); }
  });
  wireChrome(() => editor, { onSave: save, isLocked: () => session.locked });

  render();
  try {
    await loadMonaco();
    editor = createEditor('Vault source editor. Saved changes become encrypted revisions in the vault.');
    editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
    editor.onDidChangeCursorPosition(e => { $('cursor-position').textContent = `Ln ${e.position.lineNumber}, Col ${e.position.column}`; });
    $('loading').hidden = true;
    $('footer-status').textContent = 'Vault editor';
    unlock();
  } catch {
    showLoadFailure();
  }
}

const typeIcon = language => ({ typescript: 'TS', javascript: 'JS', markdown: 'M↓', json: '{}', css: '#', html: '<>' })[language] ?? '··';

function relabel(metadata) {
  const fake = !!metadata.fake;
  $('mode-badge').textContent = fake ? 'Vault · fake host' : 'Vault';
  $('vault-explorer-actions').hidden = false;
  $('folder-row').hidden = true;
  $('explorer-note-label').textContent = fake ? 'FAKE HOST' : 'VAULT';
  $('explorer-note-text').textContent = fake
    ? 'Browser review only. Nothing is encrypted and no service is connected.'
    : 'Encrypted at rest by the SourceVault service. Text is decrypted only for files you open.';
  $('save-label').textContent = 'Save';
  $('save').title = 'Save to the vault as a new encrypted revision (Ctrl+S)';
  $('no-file-title').textContent = 'Choose a file';
  $('no-file-detail').textContent = 'Open a file from the explorer, or create one with + New file.';
  $('unlock-label').textContent = 'Unlock vault';
  $('session-title').textContent = fake ? 'Fake host' : 'SourceVault service';
  $('session-subtitle').textContent = fake ? 'Browser review · no encryption' : 'TPM-protected key · operator session';
  $('source-detail').textContent = fake ? 'Fake host' : 'Vault';
  $('capture-detail').textContent = metadata.captureExclusion ? 'Excluded (best effort)' : 'Allowed';
  $('revision-label').textContent = 'SAVED THIS SESSION';
  $('revision-copy').textContent = 'Each save is a new immutable revision in the vault.';
  $('memory-text').textContent = fake ? 'Not encrypted' : 'Encrypted at rest';
  $('boundary-title').textContent = 'Vault editor';
  $('boundary-text').textContent = 'Text of open files lives in this editor’s memory. Screen-capture exclusion is best effort. Builds are not connected.';
  $('reset').hidden = true;
  $('activity-message').textContent = 'Vault mode.';
}
