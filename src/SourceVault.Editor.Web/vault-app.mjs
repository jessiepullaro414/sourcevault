// Vault mode UI (docs/editor-vault-design.md). The host owns the service connection; this page sees project/file ids,
// names and the text of files it opens. Nothing here logs or posts document text anywhere but the host bridge.
import { VaultSession, languageFor as languageOf } from './vault-session.mjs';
import { resolve } from './merge.mjs';
import { $, announce, postStatus, languageLabel, loadMonaco, createEditor, createDiffEditor, createLogViewer, wireChrome, showLoadFailure } from './shell.mjs';

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
const ACTIONS = { created: 'Created', edited: 'Saved', imported: 'Imported', restored: 'Restored', 'before-history': 'Version when history began' };
const STATE_TEXT = { queued: 'queued', running: 'running', succeeded: 'succeeded', failed: 'failed', timedOut: 'timed out', rejected: 'refused', unavailable: 'could not run' };
const BUILD_REASONS = {
  ContainerNotRunning: 'the build container is not running', ContainerNotFound: 'the build container does not exist',
  EngineUnavailable: 'the Docker engine could not be reached', EngineFailed: 'the Docker engine reported an error',
  NetworkNotDisabled: 'the container has network access, which the build configuration does not allow',
  PlatformMismatch: 'the container is not the configured platform', CleanupFailed: 'the container folders could not be emptied',
  TimedOut: 'the build ran longer than allowed and was stopped', Artifacts: 'the output was refused (links, unsafe names, or too large)',
  NotRunning: 'the SourceVault build service is not running', ConfigInvalid: 'the build configuration is invalid; ask your administrator',
  UnknownTarget: 'that build target no longer exists', VaultError: 'the vault could not read the project',
};
const duration = (from, to) => { const s = Math.max(0, (new Date(to) - new Date(from)) / 1000); return s < 60 ? `${s.toFixed(1)} s` : `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`; };
const formatBytes = n => n < 1024 ? `${n} bytes` : n < 1048576 ? `${(n / 1024).toFixed(1)} KiB` : `${(n / 1048576).toFixed(1)} MiB`;
const FAILURES = {
  NotFound: 'That item no longer exists in the vault.',
  LimitExceeded: 'Files are limited to 256 KiB.',
  InvalidRequest: 'The vault rejected that name or content.',
  AlreadyExists: 'That name is already taken (names are not case-sensitive).',
  UnsavedChanges: 'Save or reload the edited files first.',
  NameMismatch: 'The typed name does not match the project name exactly. Nothing was deleted.',
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
  let deleting = null; // { projectId, typed } while a delete confirmation is open in the picker
  let renaming = null; // { projectId, value } while a project rename is open in the picker
  let history = null, diffEditor = null; // the open history view: { fileId, path, language, deleted, revisions, more, page, selected, latestText, models }
  let buildsView = null, buildTimer = null, logViewer = null, logModel = null; // the open builds view
  let merging = null; // the open merge view: { id, path, segments, conflicts, choices, theirsRevisionId }
  let showDeleted = false, deletedFiles = [], restoring = null; // the deleted-files view; restoring: { fileId, value } when the old path is taken
  let fileMenu = null; // { id, mode: menu | rename | folder | delete, value } for the explorer row whose ⋯ panel is open
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
    $('no-file').hidden = locked || !hasProject || !!f || !!history || showDeleted || !!merging || !!buildsView;
    $('workspace-name').textContent = $('breadcrumb-root').textContent = session.project?.name ?? 'No project';
    $('project-heading').textContent = (session.project?.name ?? 'No project').toUpperCase();
    $('new-file').disabled = $('switch-project').disabled = $('deleted-files').disabled = $('builds').disabled = locked || busy || !hasProject;
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
        const more = document.createElement('button'); more.className = 'file-more'; more.textContent = '⋯'; more.disabled = locked || busy;
        more.title = `Actions for ${file.path}`; more.setAttribute('aria-label', `Rename, move or delete ${file.path}`);
        more.setAttribute('aria-expanded', String(fileMenu?.id === file.id));
        more.onclick = () => { fileMenu = fileMenu?.id === file.id ? null : { id: file.id, mode: 'menu' }; render(); };
        const row = document.createElement('div'); row.className = 'file-row'; row.append(button, more);
        $('root-files').append(row);
        if (fileMenu?.id === file.id && !locked) $('root-files').append(filePanel(file));
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
    $('document-state').textContent = !f ? '' : f.deleted ? 'Deleted by someone else · Save creates it again' : f.conflict ? 'Changed by someone else' : f.mergedOnto ? 'Merged · not saved yet' : session.dirty(f.id) ? 'Unsaved edits' : 'Saved in vault';
    $('conflict-banner').hidden = locked || !f || !f.conflict || keepEditing.has(f.id);
    $('save').disabled = !editor || locked || busy || !f || !session.dirty(f.id);
    $('revision-count').textContent = String(session.savedCount).padStart(2, '0');
    $('files-detail').textContent = hasProject ? `${session.files.size} in project` : '—';
    $('language-detail').textContent = $('language-status').textContent = f ? languageLabel(f.language) : '—';
    document.body.classList.toggle('locked', locked);
    renderHistory(); renderDeleted(); renderMerge(); renderBuilds();
    postStatus(session.metadata); // four numeric/boolean fields only; also drives the host's keep-alive
  }

  function renderPicker() {
    const list = $('project-list');
    list.replaceChildren();
    if (!session.projects.length) { const p = document.createElement('p'); p.className = 'project-empty'; p.textContent = 'No projects yet. Create one below.'; list.append(p); }
    if (deleting && !session.projects.some(p => p.projectId === deleting.projectId)) deleting = null;
    for (const project of session.projects) {
      const row = document.createElement('div'); row.className = 'project-row';
      const button = document.createElement('button'); button.className = 'project-option'; button.disabled = busy;
      const name = document.createElement('strong'); name.textContent = project.name;
      const count = document.createElement('span'); count.textContent = `${project.fileCount} file${project.fileCount === 1 ? '' : 's'}`;
      button.append(name, count);
      button.onclick = () => act(async () => {
        if (session.project?.projectId !== project.projectId) { await session.selectProject(project.projectId); disposeAllModels(); keepEditing.clear(); }
        picking = false; deleting = null;
        return `Opened project "${project.name}".`;
      });
      const remove = document.createElement('button'); remove.className = 'project-delete'; remove.textContent = 'Delete…'; remove.disabled = busy;
      remove.title = `Delete ${project.name}`; remove.setAttribute('aria-label', `Delete project ${project.name}`);
      remove.onclick = () => { renaming = null; deleting = { projectId: project.projectId, typed: '' }; render(); $('delete-confirm-name')?.focus(); };
      const rename = document.createElement('button'); rename.className = 'project-delete'; rename.textContent = 'Rename…'; rename.disabled = busy;
      rename.setAttribute('aria-label', `Rename project ${project.name}`);
      rename.onclick = () => { deleting = null; renaming = { projectId: project.projectId, value: project.name }; render(); $('rename-project-name')?.select(); };
      row.append(button, rename, remove);
      list.append(row);
      if (renaming?.projectId === project.projectId) list.append(renameProjectForm(project));
      if (deleting?.projectId === project.projectId) list.append(deleteForm(project));
    }
  }

  // ---- file actions (right-click menus are blocked, so each explorer row has a ⋯ panel) ----------------------------
  function filePanel(file) {
    const panel = document.createElement('div'); panel.className = 'file-panel';
    const folder = file.path.includes('/') ? file.path.slice(0, file.path.lastIndexOf('/')) : null;
    const close = () => { fileMenu = null; render(); };
    const button = (label, onclick, className = '') => { const b = document.createElement('button'); b.type = 'button'; b.textContent = label; b.className = className; b.disabled = busy; b.onclick = onclick; return b; };
    const textForm = (label, initial, submitLabel, submit) => {
      const form = document.createElement('form'); form.className = 'file-form';
      const caption = document.createElement('label'); caption.textContent = label;
      const input = document.createElement('input'); input.value = fileMenu.value ?? initial; input.maxLength = 260; input.autocomplete = 'off'; input.spellcheck = false; input.setAttribute('aria-label', label);
      input.oninput = () => { fileMenu.value = input.value; };
      form.onsubmit = event => { event.preventDefault(); const value = input.value.trim(); if (!value || value === initial) { close(); return; } act(async () => { const message = await submit(value); fileMenu = null; return message; }); };
      const actions = document.createElement('div'); actions.className = 'delete-actions';
      const ok = button(submitLabel, null); ok.type = 'submit'; ok.onclick = null;
      actions.append(ok, button('Cancel', close));
      form.append(caption, input, actions);
      if (!fileMenu.focused) { fileMenu.focused = true; queueMicrotask(() => { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }); }
      return form;
    };
    switch (fileMenu.mode) {
      case 'menu': {
        const actions = document.createElement('div'); actions.className = 'file-menu';
        actions.append(button('History', () => openHistory(file.id, file.path, file.language)));
        actions.append(button('Rename / move', () => { fileMenu = { id: file.id, mode: 'rename' }; render(); }));
        if (folder) actions.append(button(`Rename folder ${folder}/`, () => { fileMenu = { id: file.id, mode: 'folder' }; render(); }));
        actions.append(button('Delete', () => { fileMenu = { id: file.id, mode: 'delete' }; render(); }, 'danger-text'));
        panel.append(actions);
        break;
      }
      case 'rename':
        panel.append(textForm('New path (use / for folders)', file.path, 'Rename', async value => {
          await session.renameFile(file.id, value); retitleModel(file.id);
          return `Renamed to ${value}.`;
        }));
        break;
      case 'folder': {
        const count = [...session.files.values()].filter(f => f.path.toLowerCase().startsWith(folder.toLowerCase() + '/')).length;
        panel.append(textForm(`Rename folder ${folder}/ (${count} file${count === 1 ? '' : 's'})`, folder, 'Rename folder', async value => {
          const moved = await session.renameFolder(folder, value); for (const id of models.keys()) retitleModel(id);
          return `Moved ${moved} file${moved === 1 ? '' : 's'} to ${value}/.`;
        }));
        break;
      }
      case 'delete': {
        const text = document.createElement('p');
        text.textContent = file.deleted ? `Discard your unsaved version of ${file.path}? Someone else already deleted it.` : session.dirty(file.id) ? `Save or reload ${file.path} before deleting it.` : `Delete ${file.path}? It can be restored later from Deleted files, with its history.`;
        const actions = document.createElement('div'); actions.className = 'delete-actions';
        const confirm = button('Delete file', () => act(async () => { await session.deleteFile(file.id); dropModel(file.id); fileMenu = null; return `Deleted ${file.path}.`; }), 'danger');
        confirm.disabled = busy || (!file.deleted && session.dirty(file.id));
        actions.append(confirm, button('Cancel', close));
        panel.classList.add('file-panel-danger'); panel.append(text, actions);
        break;
      }
    }
    return panel;
  }
  // ---- file history: list of saved versions, read-only diff against the current version, restore as a draft -------
  function openHistory(fileId, path, language, deleted = false) {
    act(async () => {
      const first = await session.listHistory(fileId, 0);
      const latestText = first.revisions.length ? await session.openRevision(fileId, first.revisions[0].revisionId) : '';
      closeHistory();
      history = { fileId, path, language, deleted, revisions: first.revisions, more: first.more, page: 0, selected: null, latestText };
      fileMenu = null; showDeleted = false;
    }, () => renderHistory());
  }
  function closeHistory() {
    if (!history) return;
    diffEditor?.setModel(null);
    for (const model of history.models ?? []) model.dispose();
    history = null;
  }
  function selectRevision(revision) {
    act(async () => {
      const text = await session.openRevision(history.fileId, revision.revisionId);
      diffEditor ??= createDiffEditor($('history-diff'), 'Selected version (left) compared with the current saved version (right)');
      diffEditor.setModel(null);
      for (const model of history.models ?? []) model.dispose();
      // Plain text: a typed language would join the old text to the TypeScript/JSON workers' project and diagnostics.
      const original = monaco.editor.createModel(text, 'plaintext');
      const modified = monaco.editor.createModel(history.latestText, 'plaintext');
      history.models = [original, modified];
      diffEditor.setModel({ original, modified });
      history.selected = revision.revisionId;
    }, () => renderHistory());
  }
  function renderHistory() {
    const open = !!history && !session.locked && !picking && !!session.project;
    $('history-view').hidden = !open;
    if (!open) return;
    $('history-title').textContent = history.path + (history.deleted ? ' (deleted)' : '');
    $('history-caption').textContent = history.selected
      ? 'Left: the selected version. Right: the current saved version. Read-only.'
      : 'Choose a version to compare it with the current saved version.';
    const list = $('history-list'); list.replaceChildren();
    for (const revision of history.revisions) {
      const item = document.createElement('li');
      const button = document.createElement('button'); button.className = `history-item${revision.revisionId === history.selected ? ' active' : ''}`; button.disabled = busy;
      const when = document.createElement('strong'); when.textContent = revision.savedAt ? new Date(revision.savedAt).toLocaleString() : 'Before history began';
      const detail = document.createElement('span');
      detail.textContent = [ACTIONS[revision.action] ?? revision.action, revision.savedBy, `${revision.byteLength.toLocaleString()} bytes`, revision.name !== history.path ? `as ${revision.name}` : null].filter(Boolean).join(' · ');
      button.append(when, detail);
      button.onclick = () => selectRevision(revision);
      item.append(button); list.append(item);
    }
    $('history-more').hidden = !history.more;
    const file = session.files.get(history.fileId);
    $('history-restore').disabled = busy || !history.selected || history.deleted || !file;
    $('history-restore').title = history.deleted ? 'Restore the deleted file first (Deleted files).' : 'Put this version into the editor as unsaved edits';
  }
  $('history-close').onclick = () => { closeHistory(); render(); };
  $('history-more').onclick = () => act(async () => {
    const next = await session.listHistory(history.fileId, history.page + 1);
    history.revisions = [...history.revisions, ...next.revisions]; history.more = next.more; history.page++;
  }, () => renderHistory());
  $('history-restore').onclick = () => {
    if (!history?.selected) return;
    const { fileId, selected, path } = history;
    if (session.dirty(fileId)) { announce(`Save or reload ${path} before restoring an older version.`); return; }
    act(async () => {
      await session.restoreRevision(fileId, selected);
      closeHistory();
      return `Restored an older version of ${path} as unsaved edits. Save to make it the current version.`;
    });
  };

  // ---- merge after a conflict: clean changes combine automatically; overlaps are chosen one by one ---------------
  function startMerge() {
    const f = active();
    if (!f || session.locked) return;
    act(async () => {
      const result = await session.prepareMerge(f.id);
      if (result.conflicts === 0) {
        session.applyMerge(f.id, resolve(result.segments, []), result.theirsRevisionId);
        keepEditing.delete(f.id);
        return `Merged their changes into ${f.path}; nothing overlapped. Review the result, then Save.`;
      }
      closeHistory(); showDeleted = false;
      merging = { id: f.id, path: f.path, ...result, choices: new Array(result.conflicts).fill(null) };
      return `${result.conflicts} place${result.conflicts === 1 ? '' : 's'} changed on both sides. Choose for each, then use the merged text.`;
    });
  }
  function renderMerge() {
    const open = !!merging && !session.locked && !picking && !!session.project;
    $('merge-view').hidden = !open;
    if (!open) return;
    const chosen = merging.choices.filter(Boolean).length;
    $('merge-title').textContent = `Merge changes to ${merging.path}`;
    $('merge-caption').textContent = `${chosen} of ${merging.conflicts} chosen. Changes that didn't overlap are already combined. Nothing is saved until you Save.`;
    $('merge-apply').disabled = busy || chosen < merging.conflicts;
    const list = $('merge-list'); list.replaceChildren();
    let n = 0;
    for (const segment of merging.segments) {
      if (segment.kind !== 'conflict') continue;
      const index = n++;
      const card = document.createElement('section'); card.className = 'merge-card';
      const heading = document.createElement('h3'); heading.textContent = `Change ${index + 1} of ${merging.conflicts}`;
      const sides = document.createElement('div'); sides.className = 'merge-sides';
      for (const [label, lines] of [['Yours', segment.mine], ['Theirs', segment.theirs]]) {
        const side = document.createElement('div'); side.className = 'merge-side';
        const caption = document.createElement('span'); caption.className = 'tiny-label'; caption.textContent = label.toUpperCase();
        const pre = document.createElement('pre'); pre.textContent = lines.length ? lines.join('') : '(removed)';
        side.append(caption, pre); sides.append(side);
      }
      const actions = document.createElement('div'); actions.className = 'merge-choices';
      for (const [choice, label] of [['mine', 'Take yours'], ['theirs', 'Take theirs'], ['both', 'Take both (yours first)']]) {
        const button = document.createElement('button'); button.textContent = label; button.disabled = busy;
        button.className = merging.choices[index] === choice ? 'chosen' : '';
        button.setAttribute('aria-pressed', String(merging.choices[index] === choice));
        button.onclick = () => { merging.choices[index] = choice; render(); };
        actions.append(button);
      }
      card.append(heading, sides, actions); list.append(card);
    }
  }
  $('conflict-merge').onclick = startMerge;
  $('merge-cancel').onclick = () => { merging = null; render(); announce('Merge cancelled. Your edits are unchanged.'); };
  $('merge-apply').onclick = () => {
    if (!merging || merging.choices.some(c => !c)) return;
    const { id, path, segments, choices, theirsRevisionId } = merging;
    session.applyMerge(id, resolve(segments, choices), theirsRevisionId);
    keepEditing.delete(id); merging = null;
    syncModel(); render();
    announce(`Merged ${path}. Review the result, then Save.`);
  };

  // ---- builds: run the administrator's build targets on the saved project; output stays in the vault -------------
  function openBuilds() {
    act(async () => {
      closeHistory(); showDeleted = false; merging = null;
      let targets = [], note = null;
      try { targets = await session.listBuildTargets(); }
      catch (e) { if (e?.code === 'Unavailable') note = 'Builds are not set up on this computer, or the build service is not running. Ask your administrator.'; else throw e; }
      buildsView = { targets, note, builds: targets.length ? await session.listBuilds() : [], selected: null, logText: null };
      pollBuilds();
    }, () => renderBuilds());
  }
  function closeBuilds() {
    clearTimeout(buildTimer); buildTimer = null;
    logViewer?.setModel(null); logModel?.dispose(); logModel = null;
    buildsView = null;
  }
  // While a build is queued or running, refresh every 2 seconds (each refresh counts as session activity).
  function pollBuilds() {
    clearTimeout(buildTimer);
    if (!buildsView || !buildsView.builds.some(b => b.state === 'queued' || b.state === 'running')) return;
    buildTimer = setTimeout(async () => {
      if (!buildsView || session.locked) return;
      try {
        const before = buildsView.builds;
        buildsView.builds = await session.listBuilds();
        const done = buildsView.builds.find(b => before.some(o => o.buildNumber === b.buildNumber && (o.state === 'queued' || o.state === 'running')) && b.state !== 'queued' && b.state !== 'running');
        if (done) announce(`Build #${done.buildNumber} ${STATE_TEXT[done.state] ?? done.state}.`);
      } catch { /* the next refresh or an explicit action reports problems */ }
      renderBuilds(); pollBuilds();
    }, 2000);
  }
  function selectBuild(build) {
    act(async () => {
      buildsView.selected = build.buildNumber;
      const text = build.logAvailable ? await session.openBuildLog(build.buildNumber) : null;
      logViewer ??= createLogViewer($('build-log'), 'Build output, read-only');
      logViewer.setModel(null); logModel?.dispose();
      logModel = monaco.editor.createModel(text ?? (build.state === 'queued' || build.state === 'running' ? 'The build has not finished yet.' : 'No output was recorded.'), 'plaintext');
      logViewer.setModel(logModel);
    }, () => renderBuilds());
  }
  function renderBuilds() {
    const open = !!buildsView && !session.locked && !picking && !!session.project;
    $('builds-view').hidden = !open;
    if (!open) return;
    $('builds-title').textContent = `Builds of ${session.project.name}`;
    $('builds-caption').textContent = buildsView.note ?? 'Builds use the saved files in the vault (not unsaved edits). Output files go to the folder your administrator set; the build log stays encrypted here.';
    const select = $('build-target');
    if (select.options.length !== buildsView.targets.length) {
      select.replaceChildren(...buildsView.targets.map(t => { const o = document.createElement('option'); o.value = o.textContent = t; return o; }));
    }
    select.disabled = $('build-start').disabled = busy || !buildsView.targets.length;
    const list = $('builds-list'); list.replaceChildren();
    if (!buildsView.builds.length) { const empty = document.createElement('li'); empty.className = 'project-empty'; empty.textContent = buildsView.targets.length ? 'No builds yet.' : ''; list.append(empty); }
    for (const build of buildsView.builds) {
      const item = document.createElement('li');
      const button = document.createElement('button'); button.className = `history-item${build.buildNumber === buildsView.selected ? ' active' : ''}`; button.disabled = busy;
      const title = document.createElement('strong');
      const state = document.createElement('span'); state.className = `build-state ${build.state}`; state.textContent = STATE_TEXT[build.state] ?? build.state;
      title.append(`#${build.buildNumber} · ${build.target} `, state);
      const detail = document.createElement('span');
      detail.textContent = [new Date(build.requestedAt).toLocaleString(), build.requestedBy, build.finishedAt ? duration(build.requestedAt, build.finishedAt) : null].filter(Boolean).join(' · ');
      button.append(title, detail);
      button.onclick = () => selectBuild(build);
      item.append(button); list.append(item);
    }
    const selected = buildsView.builds.find(b => b.buildNumber === buildsView.selected);
    const summary = $('build-summary'); summary.replaceChildren();
    if (selected) {
      const line = (label, value) => { const p = document.createElement('div'); const s = document.createElement('strong'); s.textContent = label + ': '; p.append(s, value); summary.append(p); };
      line('Result', `${STATE_TEXT[selected.state] ?? selected.state}${selected.exitCode != null ? ` (exit code ${selected.exitCode})` : ''}${selected.reason ? ` — ${BUILD_REASONS[selected.reason] ?? BUILD_REASONS[selected.reason.replace(/^Artifacts.*/, 'Artifacts')] ?? selected.reason}` : ''}`);
      if (selected.artifactFolder) { const code = document.createElement('code'); code.textContent = selected.artifactFolder; line(`Output (${selected.artifactFiles} file${selected.artifactFiles === 1 ? '' : 's'}, ${formatBytes(selected.artifactBytes)})`, code); }
      if (selected.logTruncated) line('Log', 'only the end of the output was kept');
    } else summary.textContent = buildsView.builds.length ? 'Choose a build to see its result and output.' : '';
  }
  $('builds').onclick = openBuilds;
  $('builds-close').onclick = () => { closeBuilds(); render(); };
  $('build-start').onclick = () => {
    const target = $('build-target').value;
    if (!target || !buildsView) return;
    act(async () => {
      const number = await session.requestBuild(target);
      buildsView.builds = await session.listBuilds();
      buildsView.selected = number;
      pollBuilds();
      return `Build #${number} queued on ${target}. It uses the saved files${session.dirtyIds.length ? '; your unsaved edits are not included' : ''}.`;
    });
  };

  // ---- deleted files ---------------------------------------------------------------------------------------------
  function renderDeleted() {
    $('deleted-view').hidden = !showDeleted || session.locked || picking || !session.project;
    if ($('deleted-view').hidden) return;
    const list = $('deleted-list'); list.replaceChildren();
    $('deleted-caption').textContent = deletedFiles.length ? 'Deleted files keep their history and can be restored.' : 'No deleted files in this project.';
    for (const file of deletedFiles) {
      const row = document.createElement('div'); row.className = 'project-row';
      const info = document.createElement('div'); info.className = 'project-option deleted-option';
      const name = document.createElement('strong'); name.textContent = file.name;
      const when = document.createElement('span'); when.textContent = `deleted ${new Date(file.deletedAt).toLocaleString()}${file.deletedBy ? ` by ${file.deletedBy}` : ''}`;
      info.append(name, when);
      const historyButton = document.createElement('button'); historyButton.className = 'project-delete'; historyButton.textContent = 'History'; historyButton.disabled = busy;
      historyButton.onclick = () => openHistory(file.fileId, file.name, languageOf(file.name), true);
      const restore = document.createElement('button'); restore.className = 'project-delete'; restore.textContent = 'Restore'; restore.disabled = busy;
      restore.onclick = () => restoreDeleted(file, file.name);
      row.append(info, historyButton, restore); list.append(row);
      if (restoring?.fileId === file.fileId) {
        const form = document.createElement('form'); form.className = 'delete-confirm rename-confirm';
        const text = document.createElement('p'); text.textContent = `${file.name} is taken now. Restore it under another path:`;
        const input = document.createElement('input'); input.value = restoring.value; input.maxLength = 260; input.autocomplete = 'off'; input.spellcheck = false; input.setAttribute('aria-label', 'Restore under this path');
        input.oninput = () => { restoring.value = input.value; };
        const ok = document.createElement('button'); ok.type = 'submit'; ok.textContent = 'Restore'; ok.disabled = busy;
        const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Cancel'; cancel.onclick = () => { restoring = null; render(); };
        form.onsubmit = event => { event.preventDefault(); const value = input.value.trim(); if (value) restoreDeleted(file, value); };
        const actions = document.createElement('div'); actions.className = 'delete-actions'; actions.append(ok, cancel);
        form.append(text, input, actions); list.append(form);
      }
    }
  }
  function restoreDeleted(file, name) {
    act(async () => {
      try { await session.restoreFile(file.fileId, name); }
      catch (e) { if (e?.code === 'AlreadyExists') { restoring = { fileId: file.fileId, value: name }; return `${name} is taken; choose another path.`; } throw e; }
      restoring = null;
      deletedFiles = await session.listDeleted();
      return `Restored ${name}.`;
    });
  }
  $('deleted-files').onclick = () => act(async () => { deletedFiles = await session.listDeleted(); closeHistory(); closeBuilds(); showDeleted = true; restoring = null; });
  $('deleted-close').onclick = () => { showDeleted = false; restoring = null; render(); };

  // A rename keeps the model (and its undo history); only the language follows the new extension.
  function retitleModel(id) { const model = models.get(id), file = session.files.get(id); if (model && file) monaco.editor.setModelLanguage(model, file.language); }

  function renameProjectForm(project) {
    const form = document.createElement('form'); form.className = 'delete-confirm rename-confirm';
    const input = document.createElement('input'); input.id = 'rename-project-name'; input.maxLength = 64; input.autocomplete = 'off'; input.spellcheck = false;
    input.value = renaming.value; input.setAttribute('aria-label', `New name for ${project.name}`);
    input.oninput = () => { renaming.value = input.value; };
    const ok = document.createElement('button'); ok.type = 'submit'; ok.textContent = 'Rename project'; ok.disabled = busy;
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Cancel'; cancel.onclick = () => { renaming = null; render(); };
    form.onsubmit = event => {
      event.preventDefault();
      const name = input.value.trim();
      if (!name || name === project.name) { renaming = null; render(); return; }
      act(async () => { await session.renameProject(project.projectId, name); renaming = null; return `Renamed project to "${name}".`; });
    };
    const actions = document.createElement('div'); actions.className = 'delete-actions'; actions.append(ok, cancel);
    form.append(input, actions);
    return form;
  }

  // Deleting needs the exact name typed (paste is blocked in this window, so it is always typed).
  function deleteForm(project) {
    const form = document.createElement('form'); form.className = 'delete-confirm';
    const text = document.createElement('p');
    text.textContent = `Permanently delete "${project.name}" and all ${project.fileCount} file${project.fileCount === 1 ? '' : 's'} with their history? This cannot be undone. Type the project name to confirm.`;
    const input = document.createElement('input'); input.id = 'delete-confirm-name'; input.autocomplete = 'off'; input.spellcheck = false;
    input.maxLength = 64; input.setAttribute('aria-label', `Type ${project.name} to confirm`); input.value = deleting.typed;
    const confirm = document.createElement('button'); confirm.type = 'submit'; confirm.className = 'danger'; confirm.textContent = 'Delete project';
    confirm.disabled = busy || input.value !== project.name;
    input.oninput = () => { deleting.typed = input.value; confirm.disabled = busy || input.value !== project.name; };
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Cancel';
    cancel.onclick = () => { deleting = null; render(); };
    form.onsubmit = event => {
      event.preventDefault();
      if (input.value !== project.name) return;
      const wasOpen = session.project?.projectId === project.projectId;
      act(async () => {
        const complete = await session.deleteProject(project.projectId, input.value);
        deleting = null;
        if (wasOpen) { disposeAllModels(); keepEditing.clear(); }
        return complete ? `Deleted project "${project.name}".` : `Deleted project "${project.name}". Some encrypted files were in use; the service removes them later.`;
      });
    };
    const actions = document.createElement('div'); actions.className = 'delete-actions'; actions.append(confirm, cancel);
    form.append(text, input, actions);
    return form;
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
    if (f.deleted) { act(async () => { await session.saveAsNew(f.id); dropModel(f.id); return `${f.path} was deleted by someone else; saved your version as a new file.`; }); return; }
    act(async () => (await session.save(f.id)) ? `Saved ${f.path} to the vault as a new encrypted revision.` : 'No unsaved edits.');
  }
  $('unlock').onclick = unlock;
  $('lock').onclick = () => act(async () => { await session.lock(); return 'Locked. The service session ended; unsaved edits are kept in this editor.'; });
  $('save').onclick = save;
  $('conflict-reload').onclick = () => { const f = active(); if (f) act(async () => { await session.reload(f.id); keepEditing.delete(f.id); return `Reloaded ${f.path}: your unsaved edits were discarded.`; }); };
  $('conflict-keep').onclick = () => { const f = active(); if (f) { keepEditing.add(f.id); render(); announce('Keeping your edits. Saving will still conflict; reload to take their version.'); } };
  $('switch-project').onclick = () => {
    if (session.dirtyIds.length) { announce(FAILURES.UnsavedChanges); return; }
    act(async () => { await session.refreshProjects(); closeHistory(); closeBuilds(); showDeleted = false; merging = null; picking = true; });
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
    if (session.locked) { closeHistory(); closeBuilds(); showDeleted = false; merging = null; render(); announce(`Vault locked (${session.lockReason}). Unsaved edits are kept.`); $('unlock').focus(); }
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
