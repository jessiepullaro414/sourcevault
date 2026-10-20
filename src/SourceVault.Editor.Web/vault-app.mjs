// Vault mode UI (docs/editor-vault-design.md). The host owns the service connection; this page sees project/file ids,
// names and the text of files it opens. Nothing here logs or posts document text anywhere but the host bridge.
import { VaultSession, languageFor as languageOf, isOpenable, badgeOf, MAX_EDITOR_TEXT_BYTES } from './vault-session.mjs';
import { resolve } from './merge.mjs';
import { $, announce, postStatus, languageLabel, loadMonaco, createEditor, createDiffEditor, createLogViewer, wireChrome, showLoadFailure, clearInternalClipboard, setFileDropHandler } from './shell.mjs';
import { touchRecent, counterpart, isCFamily, outline } from './workbench.mjs';
import { createWorkbench } from './workbench-ui.mjs';

const UNLOCK_FAILURES = {
  ServiceNotInstalled: 'The SourceVault service is not installed on this computer.',
  ServiceNotRunning: 'The SourceVault service is not running. Ask an administrator to start it, then unlock.',
  AccessDenied: "This Windows sign-in is not in 'SourceVault Operators'. After being added, sign out and back in.",
  Busy: 'All service connections are in use. Try again shortly.',
  ServerUntrusted: 'The vault pipe is not owned by the SourceVault service. The editor refused to connect.',
  ClientRejected: 'The service only accepts the installed SourceVault editor.',
  Timeout: 'The editor host did not answer.',
};
// The service can't be reached: the window turns red and says so (owner request 2026-10-20). Nothing is opened, saved or
// decrypted until it is back; there is no unencrypted fallback. [eyebrow, title, what it means and what to do]
const NOT_CONNECTED = {
  ServiceNotRunning: ['SERVICE NOT RUNNING', 'The SourceVault service isn’t running', 'Ask your administrator to start it (Start menu → SourceVault Administration → Check SourceVault), then click Try again.'],
  ServiceNotInstalled: ['NOT INSTALLED', 'The SourceVault service isn’t installed on this computer', 'This editor only works with an installed SourceVault service. Ask your administrator.'],
  Timeout: ['NO ANSWER', 'The vault didn’t answer', 'The service or this editor’s connection to it isn’t responding. Wait a moment, then click Try again.'],
  ServerUntrusted: ['NOT CONNECTED', 'The editor refused to connect', 'Something other than the SourceVault service answered on its connection. Nothing was sent to it. Tell your administrator.'],
};
const NOT_CONNECTED_NOTE = 'Nothing can be opened or saved, and nothing is decrypted, until the vault is connected. Unsaved edits already in this window stay here.';
// Working offline (owner decision 2026-10-20): chosen with "Work offline" on that screen. Memory only, never the disk.
const OFFLINE_NOTE = 'You can keep editing the files that are open and make new ones. They stay in this window’s memory only: nothing is saved or encrypted until the vault is back, and closing the editor loses them. Save marks a file to be saved then.';
const KEPT = 'Unsaved edits stay in this editor’s memory and are checked against the vault when you unlock. Locking hides them; it does not guarantee memory erasure.';
const LOCK_SCREENS = {
  start: ['VAULT', 'Connecting to the vault', 'Documents are decrypted by the SourceVault service, and only for files you open.'],
  user: ['SESSION LOCKED', 'Your workspace is locked', KEPT],
  windows: ['LOCKED WITH WINDOWS', 'Locked when Windows locked', KEPT],
  expired: ['SESSION EXPIRED', 'Your vault session expired', 'Sessions end after 5 idle minutes or 30 minutes in total. ' + KEPT],
  closed: ['DISCONNECTED', 'The vault connection closed', KEPT],
};
const ACTIONS = { created: 'Created', edited: 'Saved', imported: 'Imported', restored: 'Restored', reverted: 'Reverted', 'before-history': 'Version when history began' };
const STATE_TEXT = { queued: 'queued', running: 'running', succeeded: 'succeeded', failed: 'failed', timedOut: 'timed out', rejected: 'refused', unavailable: 'could not run' };
const BUILD_REASONS = {
  ContainerNotRunning: 'the build container is not running', ContainerNotFound: 'the build container does not exist',
  EngineUnavailable: 'the Docker engine could not be reached', EngineFailed: 'the Docker engine reported an error',
  NetworkNotDisabled: 'the container has network access, which the build configuration does not allow',
  PlatformMismatch: 'the container is not the configured platform', CleanupFailed: 'the container folders could not be emptied',
  TimedOut: 'the build ran longer than allowed and was stopped', Artifacts: 'the output was refused (links, unsafe names, or too large)',
  NotRunning: 'the SourceVault build service is not running', ConfigInvalid: 'the build configuration is invalid; ask your administrator',
  UnknownTarget: 'that build target no longer exists', VaultError: 'the vault could not read the project',
  SourceUnavailable: 'a file in the vault failed its integrity check while it was copied; nothing was built from it',
  CopyTimedOut: 'copying the project into the container took longer than 30 minutes',
  EngineUntrusted: 'something other than the Docker engine answered on its connection, so nothing was sent to it; tell your administrator',
  ArtifactFolderUntrusted: 'the output folder, or a folder above it, could be changed by people other than administrators (or is a link), so the build was not run; tell your administrator',
};
const duration = (from, to) => { const s = Math.max(0, (new Date(to) - new Date(from)) / 1000); return s < 60 ? `${s.toFixed(1)} s` : `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`; };
const formatBytes = n => n < 1024 ? `${n} bytes` : n < 1048576 ? `${(n / 1024).toFixed(1)} KiB` : n < 1073741824 ? `${(n / 1048576).toFixed(1)} MiB` : `${(n / 1073741824).toFixed(2)} GiB`;
const FAILURES = {
  NotFound: 'That item no longer exists in the vault.',
  LimitExceeded: 'Over a vault limit: the editor opens and saves text up to 1 MiB; files can be up to 1 GiB, 25,000 files and 8 GiB per project, and the vault keeps 4 GiB of disk free.',
  NotText: 'That version is binary, so the editor cannot show it.',
  NotOpenable: 'The editor shows only text up to 1 MiB.',
  UploadExpired: 'The file you chose is no longer held by the service (it keeps it for 2 minutes). Choose it again.',
  SourceChanged: 'The file on disk changed while it was being read. Nothing was changed in the vault; try again.',
  FileUnreadable: 'The editor could not read that file from disk. Nothing was changed in the vault.',
  NothingToAdd: 'Nothing that the vault can hold was dropped: links, excluded folders (.git, bin, obj…) and names the vault can\u2019t hold are skipped.',
  InvalidRequest: 'The vault rejected that name or content.',
  AlreadyExists: 'That name is already taken (names are not case-sensitive).',
  UnsavedChanges: 'Save or reload the edited files first.',
  NameMismatch: 'The typed name does not match the project name exactly. Nothing was deleted.',
  Timeout: 'The editor host did not answer. Nothing was changed.',
  Offline: 'Not connected: that needs the vault. The open files and new files still work; everything else is back once the vault is (Try again).',
  NoProject: 'Open a project first: new files made offline are saved into it.',
};

/** `onSession` is passed only by the browser-review fake host, so its panel can target the open file. */
export async function startVault(transport, metadata, onSession) {
  const session = new VaultSession(transport);
  onSession?.(session);
  const models = new Map();     // fileId -> Monaco model (only for opened files)
  const viewStates = new Map();
  const keepEditing = new Set(); // files whose conflict banner was dismissed
  let editor, suppress = false, busy = false, picking = false, unlockError = null;
  let notConnected = null; // a NOT_CONNECTED code while the service can't be reached
  let deleting = null; // { projectId, typed } while a delete confirmation is open in the picker
  let renaming = null; // { projectId, value } while a project rename is open in the picker
  let history = null, diffEditor = null; // the open history view: { fileId, path, language, deleted, revisions, more, page, selected, latestText, models }
  let buildsView = null, buildTimer = null, logViewer = null, logModel = null; // the open builds view
  let merging = null; // the open merge view: { id, path, segments, conflicts, choices, theirsRevisionId }
  let showDeleted = false, deletedFiles = [], restoring = null; // the deleted-files view; restoring: { fileId, value } when the old path is taken
  let fileMenu = null; // { id, mode: menu | rename | folder | delete, value } for the explorer row whose ⋯ panel is open
  let info = null; // the info panel of a file the editor doesn't show (binary, or text over 1 MiB): { id, details }
  let importing = null; // "Import a folder" waiting for its name and a confirmation: { plan, name }
  const notMergeable = new Map(); // fileId -> their revision, when a conflict can't be merged as text (keep mine / keep theirs)
  // Split editor: two Monaco editors share the models. `focused` is the side that has the keyboard; session.activeId
  // is always the focused side's file and paneIds[other] the file on the other side.
  let editor2 = null, split = false, focused = 0, paneIds = [null, null];
  let recent = [], recentProject = null, lastActive = null; // recently opened files (ids, in memory only)
  let comparing = null, compareDiff = null; // the open "compare with saved" view: { id, path, models }
  let sidebar = 'explorer', chordUntil = 0;
  const chordKeys = new WeakSet(); // second keys of Ctrl+K chords, which are not shortcuts of their own
  const active = () => (session.activeId ? session.files.get(session.activeId) : null);
  const focusedEditor = () => (split && focused === 1 ? editor2 : editor);

  const demo = !!metadata.demo; // demo mode: the in-page demo vault, nothing encrypted (shown in red)
  document.title = demo ? 'SourceVault — DEMO (not encrypted)' : 'SourceVault — Vault editor';
  document.body.classList.add('mode-vault');
  document.body.classList.toggle('mode-demo', demo);
  document.body.classList.toggle('mode-red', demo); // the red look; also while the service can't be reached
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
    const offline = locked && !!notConnected; // the red lock screen: the service can't be reached
    const working = session.offline;          // working offline: red, editable, nothing saved until the vault is back
    const [eyebrow, title, detail] = offline ? NOT_CONNECTED[notConnected] : LOCK_SCREENS[session.lockReason] ?? LOCK_SCREENS.closed;
    $('locked-view').hidden = !locked;
    $('locked-eyebrow').textContent = eyebrow; $('locked-title').textContent = title;
    $('locked-detail').textContent = offline ? `${detail} ${NOT_CONNECTED_NOTE}` : unlockError ?? (demo ? `${detail} ${DEMO_NOTE}` : detail);
    $('unlock').disabled = busy;
    $('unlock-label').textContent = offline ? 'Try again' : 'Unlock vault';
    $('work-offline').hidden = !offline; $('work-offline').disabled = busy;
    document.body.classList.toggle('mode-red', demo || offline || working);
    document.body.classList.toggle('mode-offline', offline || working);
    $('offline-banner').hidden = !offline && !working;
    $('offline-label').textContent = working ? 'OFFLINE · NOT SAVED' : 'NOT CONNECTED';
    $('offline-retry').hidden = !working; $('offline-retry').disabled = busy;
    if (offline) $('offline-text').textContent = `${title}. ${NOT_CONNECTED_NOTE}`;
    if (working) $('offline-text').textContent = `${(NOT_CONNECTED[notConnected] ?? NOT_CONNECTED.ServiceNotRunning)[1]}. ${OFFLINE_NOTE}`;
    $('project-picker').hidden = locked || hasProject;
    if (!locked && !hasProject) renderPicker();
    $('no-file').hidden = locked || !hasProject || !!f || !!history || showDeleted || !!merging || !!buildsView || !!comparing || !!(info && session.files.has(info.id));
    if (session.project?.projectId !== recentProject) { recent = []; recentProject = session.project?.projectId ?? null; }
    if (session.activeId && session.activeId !== lastActive) recent = touchRecent(recent, session.activeId);
    lastActive = session.activeId;
    $('workspace-name').textContent = $('breadcrumb-root').textContent = session.project?.name ?? 'No project';
    $('project-heading').textContent = (session.project?.name ?? 'No project').toUpperCase();
    $('new-file').disabled = $('add-files').disabled = $('switch-project').disabled = $('deleted-files').disabled = $('builds').disabled = locked || busy || !hasProject;
    $('lock').disabled = locked || busy;

    $('tabs').replaceChildren(); $('source-files').replaceChildren(); $('root-files').replaceChildren();
    $('explorer').toggleAttribute('data-file-drop', hasProject && !locked);
    if (hasProject) {
      for (const file of [...session.files.values()].sort((a, b) => a.path.localeCompare(b.path))) {
        const button = document.createElement('button');
        const shownInfo = !!info && info.id === file.id && !f;
        button.className = `file-button${file.id === session.activeId || shownInfo ? ' active' : ''}`;
        button.disabled = locked || busy; button.title = file.path;
        const icon = document.createElement('span'); icon.className = 'file-type'; icon.textContent = typeIcon(file.language);
        const label = document.createElement('span'); label.textContent = file.path;
        button.append(icon, label);
        const badge = badgeOf(file);
        if (badge && !file.loaded) {
          const tag = document.createElement('span'); tag.className = 'file-badge'; tag.textContent = badge;
          tag.title = `${badge === 'binary' ? 'Binary file' : 'Text over 1 MiB'}, ${formatBytes(file.byteLength)}: shown as file details, not in the editor`;
          button.append(tag);
        }
        if (file.conflict) { const mark = document.createElement('span'); mark.className = 'file-conflict'; mark.textContent = '!'; mark.setAttribute('aria-label', 'Changed by someone else'); button.append(mark); }
        else if (session.dirty(file.id)) { const dot = document.createElement('span'); dot.className = 'file-dirty'; dot.setAttribute('aria-label', 'Unsaved changes'); button.append(dot); }
        button.onclick = () => openFile(file.id);
        const more = document.createElement('button'); more.className = 'file-more'; more.textContent = '⋯'; more.disabled = locked || busy;
        more.title = `Actions for ${file.path}`; more.setAttribute('aria-label', `Rename, move or delete ${file.path}`);
        more.setAttribute('aria-expanded', String(fileMenu?.id === file.id));
        more.onclick = () => { fileMenu = fileMenu?.id === file.id ? null : { id: file.id, mode: 'menu' }; render(); };
        const row = document.createElement('div'); row.className = 'file-row'; row.append(button, more);
        if (file.path.includes('/')) row.dataset.folder = file.path.slice(0, file.path.lastIndexOf('/')); // dropping on a row adds to its folder
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
    $('document-state').textContent = !f ? '' : f.saveWhenBack ? 'Offline · saves when the vault is back' : f.pendingCreate ? (working ? 'New file · offline, not saved' : 'New file · not saved yet')
      : working && session.dirty(f.id) ? 'Offline · unsaved edits' : f.deleted ? 'Deleted by someone else · Save creates it again' : f.conflict ? 'Changed by someone else' : f.mergedOnto ? 'Merged · not saved yet' : session.dirty(f.id) ? 'Unsaved edits' : demo ? 'Saved in the demo vault · not encrypted' : 'Saved in vault';
    $('conflict-banner').hidden = locked || !f || !f.conflict || keepEditing.has(f.id);
    $('save').disabled = !editor || locked || busy || !f || !session.dirty(f.id);
    const unsaved = locked ? 0 : session.dirtyIds.length;
    $('save-all').hidden = unsaved < 2; $('save-all').disabled = busy;
    $('unsaved-status').hidden = !unsaved; $('unsaved-status').textContent = `● ${unsaved} unsaved`; $('unsaved-status').disabled = busy;
    $('compare-saved').hidden = locked || !f || !session.dirty(f.id) || !!comparing;
    $('revision-count').textContent = String(session.savedCount).padStart(2, '0');
    $('files-detail').textContent = hasProject ? `${session.files.size} in project` : '—';
    $('language-detail').textContent = $('language-status').textContent = f ? languageLabel(f.language) : '—';
    document.body.classList.toggle('locked', locked);
    renderHistory(); renderDeleted(); renderMerge(); renderBuilds(); renderCompare(); renderInfo(); updateStatusDetails();
    const choice = !!f && notMergeable.has(f.id);
    $('conflict-text').textContent = choice
      ? 'Someone else changed this file, and one of the versions is binary or over 1 MiB, so they can\u2019t be merged as text. Keep mine saves your text over theirs; keep theirs discards your edits. The other version stays in History either way.'
      : 'Someone else changed this file since you opened it. Saving will not overwrite their version.';
    for (const id of ['conflict-merge', 'conflict-reload', 'conflict-keep']) $(id).hidden = choice;
    for (const id of ['conflict-mine', 'conflict-theirs']) { $(id).hidden = !choice; $(id).disabled = busy; }
    $('editor-shell').classList.toggle('split', split);
    $('editor-2').hidden = !split;
    $('editor').classList.toggle('focused', split && focused === 0); $('editor-2').classList.toggle('focused', split && focused === 1);
    postStatus(session.metadata); // four numeric/boolean fields only; also drives the host's keep-alive
  }

  // Status bar: cursor and selection, indentation, line endings and size of the focused side's file.
  function updateStatusDetails() {
    const ed = focusedEditor(), model = ed?.getModel(), f = active();
    if (model) {
      const position = ed.getPosition(), selected = (ed.getSelections() ?? []).reduce((n, s) => n + model.getValueLengthInRange(s), 0);
      $('cursor-position').textContent = position ? `Ln ${position.lineNumber}, Col ${position.column}${selected ? ` (${selected.toLocaleString()} selected)` : ''}` : '';
      const options = model.getOptions();
      $('indent-status').textContent = options.insertSpaces ? `Spaces: ${options.tabSize}` : `Tab size: ${options.tabSize}`;
      $('eol-status').textContent = model.getEOL() === '\r\n' ? 'CRLF' : 'LF';
    } else $('cursor-position').textContent = $('indent-status').textContent = $('eol-status').textContent = '';
    const bytes = f?.loaded ? utf8Length(f.text) : null;
    $('size-status').textContent = bytes == null ? '' : formatBytes(bytes);
    $('size-status').classList.toggle('near-limit', bytes != null && bytes > 0.9 * MAX_EDITOR_TEXT_BYTES);
    $('size-status').title = bytes != null && bytes > 0.9 * MAX_EDITOR_TEXT_BYTES ? 'Close to the 1 MiB limit for text the editor opens and saves' : 'The editor opens and saves text up to 1 MiB';
  }

  function renderPicker() {
    const list = $('project-list');
    list.replaceChildren();
    if (!session.projects.length) { const p = document.createElement('p'); p.className = 'project-empty'; p.textContent = 'No projects yet. Create one below.'; list.append(p); }
    renderImport();
    if (deleting && !session.projects.some(p => p.projectId === deleting.projectId)) deleting = null;
    for (const project of session.projects) {
      const row = document.createElement('div'); row.className = 'project-row';
      const button = document.createElement('button'); button.className = 'project-option'; button.disabled = busy;
      const name = document.createElement('strong'); name.textContent = project.name;
      const count = document.createElement('span');
      const offline = project.available === false; // failed its integrity check: offline until an administrator looks
      count.textContent = offline ? 'offline: failed its integrity check. Ask your administrator; it can only be deleted.' : `${project.fileCount} file${project.fileCount === 1 ? '' : 's'}`;
      if (offline) { button.disabled = true; button.classList.add('project-offline'); button.title = 'This project failed its integrity check when the service started. Nothing in it can be opened.'; }
      button.append(name, count);
      button.onclick = () => act(async () => {
        if (session.project?.projectId !== project.projectId) { await session.selectProject(project.projectId); disposeAllModels(); keepEditing.clear(); }
        picking = false; deleting = null;
        return `Opened project "${project.name}".${await saveOfflineWork()}`;
      });
      const remove = document.createElement('button'); remove.className = 'project-delete'; remove.textContent = 'Delete…'; remove.disabled = busy;
      remove.title = `Delete ${project.name}`; remove.setAttribute('aria-label', `Delete project ${project.name}`);
      remove.onclick = () => { renaming = null; deleting = { projectId: project.projectId, typed: '' }; render(); $('delete-confirm-name')?.focus(); };
      const rename = document.createElement('button'); rename.className = 'project-delete'; rename.textContent = 'Rename…'; rename.disabled = busy || offline;
      rename.setAttribute('aria-label', `Rename project ${project.name}`);
      rename.onclick = () => { deleting = null; renaming = { projectId: project.projectId, value: project.name }; render(); $('rename-project-name')?.select(); };
      row.append(button, rename, remove);
      list.append(row);
      if (renaming?.projectId === project.projectId) list.append(renameProjectForm(project));
      if (deleting?.projectId === project.projectId) list.append(deleteForm(project));
    }
  }

  // ---- import a folder as a new project (the host scans and reads it; the page sees names and counts) -------------
  const progressText = (verb, done, total) => `${verb}… ${total ? Math.min(100, Math.floor(done * 100 / total)) : 0}% (${formatBytes(done)} of ${formatBytes(total)})`;
  function renderImport() {
    const panel = $('import-panel');
    $('import-folder').disabled = busy || !!importing;
    panel.hidden = !importing;
    panel.replaceChildren();
    if (!importing) return;
    const { plan } = importing;
    const heading = document.createElement('p');
    heading.textContent = demo ? `Demo: a made-up folder "${plan.folder}" (nothing is read from this computer). ` : '';
    heading.append(`${plan.files.toLocaleString()} file${plan.files === 1 ? '' : 's'} (${formatBytes(plan.bytes)}) from "${plan.folder}" become a new project. The service checks every file after the import.`);
    panel.append(heading);
    if (plan.problem) { const problem = document.createElement('p'); problem.className = 'danger-text'; problem.textContent = plan.problem.replace('with --exclude', 'by choosing a smaller folder'); panel.append(problem); }
    if (plan.skippedCount) {
      const skipped = document.createElement('details');
      const summary = document.createElement('summary'); summary.textContent = `${plan.skippedCount.toLocaleString()} skipped`; skipped.append(summary);
      const list = document.createElement('ul');
      for (const s of plan.skipped.slice(0, 50)) { const li = document.createElement('li'); li.textContent = `${s.path} — ${s.reason}`; list.append(li); }
      if (plan.skippedCount > 50) { const li = document.createElement('li'); li.textContent = `…and ${(plan.skippedCount - 50).toLocaleString()} more`; list.append(li); }
      skipped.append(list); panel.append(skipped);
    }
    const form = document.createElement('form');
    const input = document.createElement('input'); input.maxLength = 64; input.autocomplete = 'off'; input.spellcheck = false; input.value = importing.name; input.setAttribute('aria-label', 'Name of the new project');
    input.oninput = () => { importing.name = input.value; };
    const ok = document.createElement('button'); ok.type = 'submit'; ok.textContent = 'Import'; ok.disabled = busy || !!plan.problem || !plan.files;
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.textContent = 'Cancel'; cancel.disabled = busy;
    cancel.onclick = () => act(async () => { await session.cancelImport(); importing = null; return 'Import cancelled. Nothing was changed.'; });
    form.onsubmit = event => {
      event.preventDefault();
      const name = input.value.trim();
      if (!name || plan.problem) return;
      act(async () => {
        const before = session.project?.projectId;
        const result = await session.importFolder(name, (done, total) => { $('footer-status').textContent = progressText('Importing', done, total); });
        importing = null;
        if (session.project?.projectId !== before) { disposeAllModels(); keepEditing.clear(); picking = false; }
        return `Imported ${result.imported.toLocaleString()} file${result.imported === 1 ? '' : 's'} into "${name}". ${demo ? 'Demo: made-up files, nothing encrypted.' : `The service decrypted and checked all ${result.verified.toLocaleString()}.`}`
          + (session.project?.projectId === result.projectId ? '' : ' Save or reload your open files, then open it from the list.');
      });
    };
    const actions = document.createElement('div'); actions.className = 'delete-actions'; actions.append(ok, cancel);
    form.append(input, actions);
    panel.append(form);
  }
  $('import-folder').onclick = () => act(async () => {
    const plan = await session.chooseImportFolder();
    if (!plan) return 'No folder chosen.';
    importing = { plan, name: plan.folder.slice(0, 64) };
    return plan.problem ? `"${plan.folder}" can't be imported as it is: ${plan.problem}` : `Scanned "${plan.folder}": ${plan.files.toLocaleString()} file${plan.files === 1 ? '' : 's'} ready. Check the name, then Import.`;
  });

  // ---- dropping files and folders from Explorer onto the explorer (the host reads them; the page never does) -------
  setFileDropHandler((files, target) => {
    if (session.locked || !session.project || picking) { announce('Open a project first, then drop files on the explorer.'); return; }
    const folder = target.closest('[data-folder]')?.dataset.folder ?? '';
    const where = folder ? ` to ${folder}/` : '';
    act(async () => {
      const result = await session.dropFiles(folder, files, (done, total) => { $('footer-status').textContent = progressText('Adding', done, total); });
      const added = `${demo ? 'Demo: the dropped files were not read; added ' : 'Added '}${result.added.toLocaleString()} file${result.added === 1 ? '' : 's'}${where}${demo ? ' (made up)' : ', encrypted in the vault'}`;
      const skipped = result.skipped ? ` ${result.skipped.toLocaleString()} skipped (links, excluded folders like .git, or names the vault can't hold).` : '';
      const stopped = result.stopped ? ` It stopped part-way: ${FAILURES[result.stopped] ?? result.stopped}` : '';
      return `${added}.${skipped}${stopped}`;
    });
  });

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
        const replace = button('Replace from disk…', () => replaceFile(file.id));
        replace.disabled = busy || session.dirty(file.id) || !!file.deleted;
        actions.append(replace);
        if (folder) actions.append(button(`Rename folder ${folder}/`, () => { fileMenu = { id: file.id, mode: 'folder' }; render(); }));
        if (folder) actions.append(button(`Add files from disk to ${folder}/`, () => addFiles(folder)));
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
        const confirm = button('Delete file', () => act(async () => { await session.deleteFile(file.id); dropModel(file.id); fileMenu = null; if (info?.id === file.id) info = null; return `Deleted ${file.path}.`; }), 'danger');
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
      // Binary or over 1 MiB: nothing to compare with, so versions are listed without a diff.
      const latest = first.revisions[0];
      const latestText = !latest ? '' : isOpenable(latest) ? await session.openRevision(fileId, latest.revisionId) : null;
      closeHistory(); closeCompare();
      history = { fileId, path, language, deleted, revisions: first.revisions, more: first.more, page: 0, selected: null, latestText, confirmRevert: false };
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
    history.confirmRevert = false;
    if (!isOpenable(revision) || history.latestText == null) {
      // No diff: a binary or large version, or a current version that is.
      diffEditor?.setModel(null);
      for (const model of history.models ?? []) model.dispose();
      history.models = []; history.selected = revision.revisionId; history.comparable = false;
      renderHistory();
      return;
    }
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
      history.selected = revision.revisionId; history.comparable = true;
    }, () => renderHistory());
  }
  function renderHistory() {
    const open = !!history && !session.locked && !picking && !!session.project;
    $('history-view').hidden = !open;
    if (!open) return;
    $('history-title').textContent = history.path + (history.deleted ? ' (deleted)' : '');
    const chosen = history.revisions.find(r => r.revisionId === history.selected);
    $('history-caption').textContent = !history.selected
      ? (history.latestText == null ? 'Binary and large versions are listed with their size and SHA-256; they are not shown here.' : 'Choose a version to compare it with the current saved version.')
      : history.comparable ? 'Left: the selected version. Right: the current saved version. Read-only.' : 'This version can’t be compared here. Revert makes it the current version again.';
    $('history-diff').hidden = !!history.selected && !history.comparable;
    $('history-detail').hidden = !history.selected || !!history.comparable;
    if (chosen && !history.comparable) {
      $('history-detail').textContent = [
        `${chosen.kind === 'binary' ? 'Binary' : 'Text'}, ${formatBytes(chosen.byteLength)} (${chosen.byteLength.toLocaleString()} bytes).`,
        chosen.sha256 ? `SHA-256 ${chosen.sha256}.` : null,
        !isOpenable(chosen) ? (chosen.kind === 'binary' ? 'Binary versions are not shown or downloaded.' : 'Text over 1 MiB is not opened in the editor.') : 'The current version is binary or over 1 MiB, so there is nothing to compare it with.',
      ].filter(Boolean).join(' ');
    }
    const list = $('history-list'); list.replaceChildren();
    for (const revision of history.revisions) {
      const item = document.createElement('li');
      const button = document.createElement('button'); button.className = `history-item${revision.revisionId === history.selected ? ' active' : ''}`; button.disabled = busy;
      const when = document.createElement('strong'); when.textContent = revision.savedAt ? new Date(revision.savedAt).toLocaleString() : 'Before history began';
      const detail = document.createElement('span');
      detail.textContent = [ACTIONS[revision.action] ?? revision.action, revision.savedBy, `${revision.byteLength.toLocaleString()} bytes`, badgeOf(revision),
        revision.sha256 && !isOpenable(revision) ? `SHA-256 ${revision.sha256.slice(0, 12)}…` : null, revision.name !== history.path ? `as ${revision.name}` : null].filter(Boolean).join(' · ');
      button.append(when, detail);
      button.onclick = () => selectRevision(revision);
      item.append(button); list.append(item);
    }
    $('history-more').hidden = !history.more;
    const file = session.files.get(history.fileId);
    // Text: restore puts the old text in the editor. Anything else: revert makes the old version current (with a confirmation).
    const textRestore = !history.selected || (!!history.comparable && !!file && isOpenable(file));
    $('history-restore').hidden = !textRestore; $('history-revert').hidden = textRestore;
    $('history-restore').disabled = busy || !history.selected || history.deleted || !file;
    $('history-restore').title = history.deleted ? 'Restore the deleted file first (Deleted files).' : 'Put this version into the editor as unsaved edits';
    $('history-revert').disabled = busy || history.deleted || !file || (chosen && chosen.revisionId === history.revisions[0]?.revisionId);
    $('history-revert').textContent = history.confirmRevert ? 'Yes, make it current' : 'Revert to this version';
    $('history-revert').title = history.deleted ? 'Restore the deleted file first (Deleted files).' : 'Make this version the current one again. The current version stays in history.';
  }
  $('history-revert').onclick = () => {
    if (!history?.selected) return;
    if (!history.confirmRevert) { history.confirmRevert = true; renderHistory(); announce('Make this version current again? The version it replaces stays in history. Choose again to confirm.'); return; }
    const { fileId, selected, path } = history;
    act(async () => {
      await session.revertFile(fileId, selected);
      closeHistory();
      if (info?.id === fileId) info.details = await session.info(fileId);
      return `Reverted ${path} to the chosen version. The version it replaced is still in its history.`;
    });
  };
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
      if (!result.mergeable) {
        notMergeable.set(f.id, result.theirsRevisionId);
        return 'These versions can\u2019t be merged as text (binary or over 1 MiB). Choose keep mine or keep theirs.';
      }
      if (result.conflicts === 0) {
        session.applyMerge(f.id, resolve(result.segments, []), result.theirsRevisionId);
        keepEditing.delete(f.id);
        return `Merged their changes into ${f.path}; nothing overlapped. Review the result, then Save.`;
      }
      closeHistory(); closeCompare(); showDeleted = false;
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
  $('conflict-mine').onclick = () => {
    const f = active();
    if (!f || !notMergeable.has(f.id) || session.locked) return;
    act(async () => {
      session.applyMerge(f.id, f.text, notMergeable.get(f.id)); // saved on top of their version, if it is still current
      notMergeable.delete(f.id); keepEditing.delete(f.id);
      await session.save(f.id);
      return `Saved your version of ${f.path} over theirs. Theirs is in History.`;
    });
  };
  $('conflict-theirs').onclick = () => {
    const f = active();
    if (!f || !notMergeable.has(f.id) || session.locked) return;
    let shown = true;
    act(async () => {
      shown = await session.reload(f.id);
      notMergeable.delete(f.id); keepEditing.delete(f.id);
      return shown ? `Took their version of ${f.path}; your edits were discarded.` : `Took their version of ${f.path}; your edits were discarded. It is binary or over 1 MiB, so its details are shown.`;
    }, () => { if (!shown) openInfo(f.id); });
  };
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
      closeHistory(); closeCompare(); showDeleted = false; merging = null;
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
    $('builds-caption').textContent = buildsView.note ?? (demo
      ? 'Demo builds are simulated: nothing is compiled, no container runs and no output files are written. The log below is made up.'
      : 'Builds use the saved files in the vault (not unsaved edits). Output files go to the folder your administrator set; the build log stays encrypted here.');
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
      if (selected.artifactFolder) {
        const code = document.createElement('code'); code.textContent = selected.artifactFolder;
        const open = document.createElement('button'); open.type = 'button'; open.className = 'link-button'; open.textContent = 'Open folder'; open.disabled = busy;
        open.title = 'Open the output folder in Explorer (ordinary, unencrypted files)';
        open.onclick = () => demo ? announce('Demo builds write no files. In the vault, this opens the output folder in Explorer.')
          : act(async () => { await session.openBuildFolder(selected.buildNumber); return 'Opened the output folder in Explorer. Its files are ordinary, unencrypted files.'; });
        const value = document.createElement('span'); value.append(code, ' ', open);
        line(`Output (${selected.artifactFiles} file${selected.artifactFiles === 1 ? '' : 's'}, ${formatBytes(selected.artifactBytes)})`, value);
      }
      if (selected.artifactsDropped > 0) line('Left out', `${selected.artifactsDropped} file${selected.artifactsDropped === 1 ? '' : 's'} that can reveal source (source maps, debug symbols); listed at the end of the log`);
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
      const when = document.createElement('span'); when.textContent = `${file.kind === 'binary' ? 'binary · ' : ''}deleted ${new Date(file.deletedAt).toLocaleString()}${file.deletedBy ? ` by ${file.deletedBy}` : ''}`;
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
  $('deleted-files').onclick = () => act(async () => { deletedFiles = await session.listDeleted(); closeHistory(); closeBuilds(); closeCompare(); showDeleted = true; restoring = null; });
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
  // Models of files the session unloaded (their version became binary or over 1 MiB).
  function dropStaleModels() { for (const id of [...models.keys()]) if (!session.files.get(id)?.loaded) dropModel(id); }
  function syncModel() {
    if (!editor) return;
    dropStaleModels();
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
    const visible = !session.locked && !picking;
    paneIds[focused] = session.activeId;
    const other = 1 - focused;
    if (!session.openIds.includes(paneIds[other])) paneIds[other] = session.activeId; // its file was closed: show the same file
    setPane(0, visible && paneIds[0] ? models.get(paneIds[0]) ?? null : null);
    if (split) setPane(1, visible && paneIds[1] ? models.get(paneIds[1]) ?? null : null);
  }
  // View states (scroll, cursor, folding) are kept per side and file.
  function setPane(index, model) {
    const ed = index === 0 ? editor : editor2, current = ed.getModel();
    if (current === model) return;
    if (current) viewStates.set(`${index}|${current.uri}`, ed.saveViewState());
    ed.setModel(model);
    if (model) { const saved = viewStates.get(`${index}|${model.uri}`); if (saved) ed.restoreViewState(saved); if (index === focused || !split) ed.focus(); }
  }
  function dropModel(id) {
    const model = models.get(id);
    if (!model) return;
    for (const ed of [editor, editor2]) if (ed?.getModel() === model) ed.setModel(null);
    for (const index of [0, 1]) viewStates.delete(`${index}|${model.uri}`);
    model.dispose(); models.delete(id);
  }
  // Another project: no files carry over, so the split, the comparison and search results go too.
  function disposeAllModels() {
    editor?.setModel(null); editor2?.setModel(null);
    for (const id of [...models.keys()]) dropModel(id);
    split = false; focused = 0; paneIds = [null, null];
    info = null; notMergeable.clear();
    closeCompare(); wb.onLock();
  }

  // ---- actions ---------------------------------------------------------------------------------------------------
  function unlock() {
    act(async () => {
      try { var changed = await session.unlock(); unlockError = null; notConnected = null; }
      catch (e) {
        notConnected = NOT_CONNECTED[e?.code] ? e.code : null;
        unlockError = UNLOCK_FAILURES[e?.code] ?? `Could not connect to the vault (${e?.code ?? 'InternalError'}).`;
        return notConnected ? `Not connected. ${NOT_CONNECTED[notConnected][1]}.` : 'Still locked.';
      }
      for (const id of changed) { keepEditing.delete(id); notMergeable.delete(id); }
      if (info) { try { info.details = await session.info(info.id); } catch (e) { if (e?.code !== 'NotFound') throw e; info = null; } }
      const queued = await saveOfflineWork();
      return (changed.length ? `Unlocked. ${changed.length} open file(s) changed in the vault while locked; saving them will conflict until you reload.` : demo ? 'Demo vault ready (simulated, not encrypted).' : 'Connected to the vault.') + queued;
    }, () => { wb.renderSearch(); if (!session.locked && !session.project) $('new-project-name').focus(); });
  }
  // What Save did offline, saved now that the vault is back. Returns a sentence for the status bar ('' if nothing).
  async function saveOfflineWork() {
    const result = await session.saveQueued();
    for (const [oldId] of result.replaced) dropModel(oldId); // new files now have their vault ids; models follow
    const parts = [];
    if (result.saved.length) parts.push(`Saved ${result.saved.length} file${result.saved.length === 1 ? '' : 's'} you saved offline: ${result.saved.join(', ')}.`);
    if (result.failed.length) parts.push(`Not saved, needs your choice: ${result.failed.map(f => `${f.path} (${f.code === 'Conflict' ? 'changed by someone else: Merge' : f.code === 'AlreadyExists' ? 'that name is taken: rename it' : f.code})`).join(', ')}.`);
    if (result.waiting) parts.push(`${result.waiting} new file${result.waiting === 1 ? '' : 's'} made offline will be saved into the project you open.`);
    return parts.length ? ' ' + parts.join(' ') : '';
  }
  function save() {
    const f = active();
    if (!f || session.locked || !session.dirty(f.id)) return;
    if (session.offline) { session.queueSave(f.id); render(); announce(`Offline: ${f.path} will be saved when the vault is back. Until then it is only in this window.`); return; }
    if (f.pendingCreate) { act(async () => { const id = await session.savePending(f.id); dropModel(f.id); return `Saved ${session.files.get(id).path} to the vault.`; }); return; }
    if (f.deleted) { act(async () => { await session.saveAsNew(f.id); dropModel(f.id); return `${f.path} was deleted by someone else; saved your version as a new file.`; }); return; }
    act(async () => (await session.save(f.id)) ? (demo ? `Saved ${f.path} to the demo vault (in memory, not encrypted).` : `Saved ${f.path} to the vault as a new encrypted revision.`) : 'No unsaved edits.');
  }
  // ---- workbench: open and reveal, compare with saved, save all, split, focus mode, header/source --------------
  const available = () => !!editor && !session.locked && !!session.project && !picking;
  /** Opens a file on the focused side; `match` ({ line, column, length }) selects a search result in it. */
  function openFile(id, match) {
    if (!session.files.has(id)) { announce('That file is no longer in the project.'); return; }
    const file = session.files.get(id);
    if (!file.loaded && !isOpenable(file)) { openInfo(id); return; }
    closeHistory(); closeBuilds(); closeCompare(); showDeleted = false; fileMenu = null;
    act(async () => { await closeInfo(); await session.open(id); }, () => {
      const ed = focusedEditor();
      if (!match || !ed?.getModel()) return;
      const range = new monaco.Range(match.line, match.column, match.line, match.column + match.length);
      ed.setSelection(range); ed.revealRangeInCenterIfOutsideViewport(range); ed.focus();
    });
  }
  // ---- info panel: files the editor doesn't show (binary, or text over 1 MiB) --------------------------------------
  // Name, kind, size, last change and SHA-256; rename, delete, history, replace from disk. Never the content.
  function openInfo(id) {
    closeHistory(); closeBuilds(); closeCompare(); showDeleted = false; merging = null; fileMenu = null;
    act(async () => {
      if (info?.id !== id) await closeInfo();
      info = { id, details: await session.info(id) };
      session.activeId = null; // the editor shows nothing while the panel is open
    });
  }
  async function closeInfo() {
    const was = info;
    info = null;
    if (was && !session.locked) await session.closeInfo(was.id);
  }
  function renderInfo() {
    const file = info && session.files.get(info.id);
    if (info && !file && !busy) info = null; // deleted
    const open = !!file && !session.locked && !picking && !!session.project && !history && !showDeleted && !merging && !buildsView && !comparing && !active();
    $('info-view').hidden = !open;
    if (!open) return;
    const d = info.details, badge = badgeOf(file);
    $('info-eyebrow').textContent = badge === 'binary' ? 'BINARY FILE' : badge === 'large' ? 'LARGE TEXT FILE' : 'FILE';
    $('info-title').textContent = file.path;
    $('info-caption').textContent = (badge === 'binary'
      ? `Binary files are stored${demo ? '' : ' encrypted'}, versioned and included in builds. The editor never shows or downloads them.`
      : badge === 'large' ? `Text over 1 MiB is stored${demo ? '' : ' encrypted'}, versioned, searched and included in builds, but not opened in the editor.`
      : 'This file opens in the editor.') + (demo ? ' Demo: “from disk” makes up a file; nothing is read from this computer.' : '');
    const list = $('info-details'); list.replaceChildren();
    const row = (label, value) => { const dt = document.createElement('dt'); dt.textContent = label; const dd = document.createElement('dd'); dd.append(value); list.append(dt, dd); };
    row('Kind', file.kind === 'binary' ? 'Binary' : 'Text');
    row('Size', `${formatBytes(file.byteLength)} (${file.byteLength.toLocaleString()} bytes)`);
    row('Last change', [d?.savedAt ? new Date(d.savedAt).toLocaleString() : 'Before history began', d?.savedBy].filter(Boolean).join(' · '));
    const hash = document.createElement('code'); hash.textContent = file.sha256 ?? 'not recorded (saved before any-file storage)';
    row('SHA-256', hash);
    $('info-conflict').hidden = !file.pending;
    for (const id of ['info-replace', 'info-history', 'info-rename', 'info-delete', 'info-close']) $(id).disabled = busy || file.pending;
    for (const id of ['info-mine', 'info-theirs']) $(id).disabled = busy;
  }
  function replaceFile(id) {
    const file = session.files.get(id);
    if (!file) return;
    if (session.dirty(id)) { announce(`Save or reload ${file.path} before replacing it.`); return; }
    act(async () => {
      try {
        if (!(await session.replaceFromDisk(id))) return 'Nothing was chosen. The file is unchanged.';
      } catch (e) {
        if (e?.code === 'Conflict' && !session.locked) {
          // The choice is made in the info panel (an open text file's tab stays open behind it).
          fileMenu = null; info = { id, details: info?.id === id ? info.details : null }; session.activeId = null;
          return `Someone else changed ${file.path} meanwhile. Choose keep mine or keep theirs.`;
        }
        throw e;
      }
      fileMenu = null;
      dropStaleModels();
      if (info?.id === id || !file.loaded) info = { id, details: await session.info(id) };
      return demo ? `Replaced ${file.path} with a made-up demo file (${formatBytes(file.byteLength)}). Nothing was read from this computer.` : `Replaced ${file.path} with the chosen file (${formatBytes(file.byteLength)}), encrypted as a new revision.`;
    });
  }
  function addFiles(folder) {
    act(async () => {
      const names = await session.addFromDisk(folder);
      fileMenu = null;
      if (!names.length) return 'Nothing was chosen.';
      const where = folder ? ` to ${folder}/` : '';
      return demo ? `Added ${names.length} made-up demo file${names.length === 1 ? '' : 's'}${where}: ${names.join(', ')}.`
        : `Added ${names.length} file${names.length === 1 ? '' : 's'}${where}, encrypted in the vault: ${names.slice(0, 5).join(', ')}${names.length > 5 ? '…' : ''}.`;
    });
  }
  $('add-files').onclick = () => addFiles('');
  $('info-replace').onclick = () => info && replaceFile(info.id);
  $('info-history').onclick = () => { const file = info && session.files.get(info.id); if (file) openHistory(file.id, file.path, file.language); };
  $('info-rename').onclick = () => { if (info) { fileMenu = { id: info.id, mode: 'rename' }; render(); } };
  $('info-delete').onclick = () => { if (info) { fileMenu = { id: info.id, mode: 'delete' }; render(); } };
  $('info-close').onclick = () => act(async () => { await closeInfo(); });
  $('info-mine').onclick = () => { const id = info?.id; if (id) act(async () => { await session.keepMine(id); dropStaleModels(); info = { id, details: await session.info(id) }; return 'Kept your file. Their version is in History.'; }); };
  $('info-theirs').onclick = () => { const id = info?.id; if (id) act(async () => { await session.keepTheirs(id); info = { id, details: await session.info(id) }; return 'Kept their version. Your file was discarded.'; }); };

  function goToLine(n) {
    const ed = focusedEditor(), model = ed?.getModel();
    if (!model) { announce('Open a file first.'); return; }
    const line = Math.min(Math.max(1, n), model.getLineCount());
    ed.setPosition({ lineNumber: line, column: 1 }); ed.revealLineInCenter(line); ed.focus();
  }
  function runEditorAction(id) {
    const ed = focusedEditor(), f = active();
    if (!ed?.getModel() || !f) { announce('Open a file first.'); return; }
    ed.focus();
    const action = ed.getAction(id);
    if (!action?.isSupported()) { announce(unsupported(id, f.language)); return; }
    action.run();
  }
  const unsupported = (id, language) => id === 'editor.action.formatDocument' ? `This editor has no formatter for ${languageLabel(language)} files.`
    : id === 'editor.action.quickOutline' ? `This editor has no outline for ${languageLabel(language)} files.` : 'That command is not available here.';

  function openCompare() {
    const f = active();
    if (!available() || !f) return;
    if (!session.dirty(f.id)) { announce(`${f.path} has no unsaved edits.`); return; }
    if (merging) { announce('Finish or cancel the merge first.'); return; }
    closeHistory(); closeBuilds(); closeCompare(); showDeleted = false;
    compareDiff ??= createDiffEditor($('compare-diff'), 'Saved version (left) compared with your unsaved edits (right)');
    // TypeScript/JavaScript copies would join the language worker's project (duplicate declarations); show them as text.
    const language = /^(typescript|javascript)$/.test(f.language) ? 'plaintext' : f.language;
    const original = monaco.editor.createModel(f.saved ?? '', language), modified = monaco.editor.createModel(f.text ?? '', language);
    compareDiff.setModel({ original, modified });
    comparing = { id: f.id, path: f.path, models: [original, modified] };
    render();
  }
  function closeCompare() {
    if (!comparing) return;
    compareDiff?.setModel(null);
    for (const model of comparing.models) model.dispose();
    comparing = null;
  }
  function renderCompare() {
    const open = !!comparing && !session.locked && !picking && !!session.project;
    $('compare-view').hidden = !open;
    if (open) $('compare-title').textContent = comparing.path;
  }
  $('compare-saved').onclick = openCompare;
  $('compare-close').onclick = () => { closeCompare(); render(); focusedEditor()?.focus(); };
  $('compare-revert').onclick = () => {
    const f = comparing && session.files.get(comparing.id), model = comparing && models.get(comparing.id);
    if (!f || !model || session.locked) return;
    // An edit, not setValue, so Ctrl+Z brings the unsaved edits back.
    model.pushStackElement();
    model.pushEditOperations([], [{ range: model.getFullModelRange(), text: f.saved }], () => null);
    model.pushStackElement();
    closeCompare(); render();
    announce(`Reverted ${f.path} to the saved version. Ctrl+Z in the editor brings your edits back.`);
  };

  function saveAll() {
    const ids = session.dirtyIds;
    if (!available()) return;
    if (!ids.length) { announce('No unsaved edits.'); return; }
    if (session.offline) { for (const id of ids) session.queueSave(id); render(); announce(`Offline: ${ids.length} file${ids.length === 1 ? '' : 's'} will be saved when the vault is back.`); return; }
    act(async () => {
      let saved = 0; const left = [];
      for (const id of ids) {
        const f = session.files.get(id);
        if (!f) continue;
        if (f.deleted || f.conflict) { left.push(f.path); continue; } // these need a choice: open each one
        try { if (f.pendingCreate) { await session.savePending(id); dropModel(id); saved++; } else if (await session.save(id)) saved++; }
        catch (e) { if (session.locked) throw e; if (e?.code === 'Conflict') keepEditing.delete(id); left.push(f.path); }
      }
      const done = `Saved ${saved} file${saved === 1 ? '' : 's'} to the vault.`;
      return left.length ? `${done} Not saved (changed or deleted by someone else, or refused): ${left.join(', ')}.` : done;
    });
  }
  $('save-all').onclick = $('unsaved-status').onclick = saveAll;

  function toggleSplit() {
    if (!available()) return;
    if (!split) {
      if (!session.activeId) { announce('Open a file first.'); return; }
      if (!editor2) {
        editor2 = createEditor(demo ? 'Demo editor, right side. Nothing is encrypted.' : 'Vault source editor, right side. Saved changes become encrypted revisions in the vault.', $('editor-2'));
        wireEditor(editor2, 1);
      }
      split = true; $('editor-2').hidden = false;
      paneIds = [session.activeId, session.activeId]; focused = 1;
      applySettings(wb.settings, false);
      announce('Split editor. Click a side, then a tab, to choose its file. Ctrl+\\ goes back to one editor.');
    } else {
      setPane(1, null);
      split = false; focused = 0; paneIds = [session.activeId, null];
    }
    syncModel(); render();
    focusedEditor()?.focus();
  }
  function focusSide(index) {
    if (!split || focused === index) return;
    paneIds[focused] = session.activeId;
    focused = index;
    if (paneIds[index] && session.openIds.includes(paneIds[index])) session.activeId = paneIds[index];
    render();
  }

  function toggleFocusMode() {
    const on = document.body.classList.toggle('focus-mode');
    announce(on ? 'Focus mode. Ctrl+K Z shows the panels again.' : 'Panels shown.');
    focusedEditor()?.focus();
  }

  function switchHeaderSource() {
    const f = active();
    if (!available() || !f) return;
    if (!isCFamily(f.path)) { announce('Switching between header and source is for C and C++ files.'); return; }
    const files = [...session.files.values()];
    const path = counterpart(f.path, files.map(x => x.path));
    if (!path) { announce(`No matching header or source file for ${f.path}.`); return; }
    openFile(files.find(x => x.path === path).id);
  }

  function showSidebar(name, toggle = false) {
    sidebar = toggle && sidebar === name ? null : name;
    $('explorer').hidden = sidebar !== 'explorer'; $('search-panel').hidden = sidebar !== 'search';
    for (const [id, panel] of [['explorer-toggle', 'explorer'], ['find', 'search']]) { $(id).classList.toggle('selected', sidebar === panel); $(id).setAttribute('aria-pressed', String(sidebar === panel)); }
    if (sidebar === 'search') wb.renderSearch();
  }

  const THEMES = { dark: demo ? 'sourcevault-demo' : 'sourcevault', light: 'vs', highContrast: 'hc-black' };
  function applySettings(s, retab) {
    if (!editor) return;
    monaco.editor.setTheme(THEMES[s.theme] ?? 'sourcevault');
    const options = { fontSize: s.fontSize, lineHeight: Math.round(s.fontSize * 1.75), wordWrap: s.wordWrap ? 'on' : 'off', minimap: { enabled: s.minimap },
      renderWhitespace: s.renderWhitespace ? 'all' : 'selection', lineNumbers: s.lineNumbers ? 'on' : 'off', tabSize: s.tabSize, insertSpaces: s.insertSpaces };
    for (const ed of [editor, editor2]) ed?.updateOptions(options);
    if (retab) for (const model of models.values()) model.updateOptions({ tabSize: s.tabSize, insertSpaces: s.insertSpaces });
    updateStatusDetails();
  }

  const wb = createWorkbench({
    session, act, available, openFile, goToLine, runEditorAction, showSidebar, applySettings,
    editor: focusedEditor, recent: () => recent, typeIcon,
  });

  // Every workbench command: a Monaco action (so it is in the F1 list and works inside the editor) and a key that also
  // works outside the editor (explorer, search box). `code` is the KeyboardEvent.code, which is also Monaco's KeyCode name.
  const COMMANDS = [
    { id: 'quickOpen', label: 'Go to File…', key: { ctrl: true, code: 'KeyP' }, run: () => wb.openPalette('files') },
    { id: 'recent', label: 'Open Recently Opened File…', key: { ctrl: true, code: 'KeyE' }, run: () => wb.openPalette('recent') },
    { id: 'search', label: 'Search All Files', key: { ctrl: true, shift: true, code: 'KeyF' }, run: () => wb.showSearch() },
    { id: 'saveAll', label: 'Save All', key: { ctrl: true, alt: true, code: 'KeyS' }, run: saveAll },
    { id: 'compare', label: 'Compare with Saved', chord: { code: 'KeyD' }, run: openCompare },
    { id: 'split', label: 'Split Editor / Back to One', key: { ctrl: true, code: 'Backslash' }, run: toggleSplit },
    { id: 'focusMode', label: 'Toggle Focus Mode', chord: { code: 'KeyZ' }, run: toggleFocusMode },
    { id: 'settings', label: 'Editor Settings…', key: { ctrl: true, code: 'Comma' }, run: () => wb.openSettings() },
    { id: 'shortcuts', label: 'Keyboard Shortcuts', chord: { ctrl: true, code: 'KeyS' }, run: () => wb.openShortcuts() },
    { id: 'switchHeaderSource', label: 'Switch Header/Source (C/C++)', key: { alt: true, code: 'KeyO' }, run: switchHeaderSource },
  ];
  function wireEditor(ed, index) {
    ed.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, save);
    const { KeyMod, KeyCode } = monaco;
    const binding = k => (k.ctrl ? KeyMod.CtrlCmd : 0) | (k.shift ? KeyMod.Shift : 0) | (k.alt ? KeyMod.Alt : 0) | KeyCode[k.code];
    for (const c of COMMANDS) {
      const keybindings = c.key ? [binding(c.key)] : [KeyMod.chord(KeyMod.CtrlCmd | KeyCode.KeyK, binding(c.chord))];
      ed.addAction({ id: `sourcevault.${c.id}`, label: c.label, keybindings, run: () => c.run() });
    }
    ed.onDidChangeCursorSelection(() => { if (ed === focusedEditor()) updateStatusDetails(); });
    ed.onDidChangeModel(() => { if (ed === focusedEditor()) updateStatusDetails(); });
    ed.onDidFocusEditorWidget(() => focusSide(index));
  }
  // Keys outside the editor run the same commands; inside it Monaco does (and also completes Ctrl+K chords).
  document.addEventListener('keydown', event => {
    const inEditor = !!event.target?.closest?.('.monaco-editor');
    const ctrl = event.ctrlKey || event.metaKey;
    const same = k => !!k.ctrl === ctrl && !!k.alt === event.altKey && !!k.shift === event.shiftKey && k.code === event.code;
    if (Date.now() < chordUntil && !['Control', 'Meta', 'Shift', 'Alt'].includes(event.key)) {
      chordUntil = 0; chordKeys.add(event);
      const command = COMMANDS.find(c => c.chord && !!c.chord.ctrl === ctrl && c.chord.code === event.code && !event.altKey && !event.shiftKey);
      if (!inEditor) { event.preventDefault(); command?.run(); }
      return;
    }
    if (ctrl && !event.altKey && !event.shiftKey && event.code === 'KeyK') { chordUntil = Date.now() + 2000; if (!inEditor) event.preventDefault(); return; }
    if (inEditor) {
      // Monaco ignores these when nothing provides them; say why instead of doing nothing.
      const f = active();
      if (f && event.shiftKey && event.altKey && !ctrl && event.code === 'KeyF' && !focusedEditor()?.getAction('editor.action.formatDocument')?.isSupported()) announce(unsupported('editor.action.formatDocument', f.language));
      if (f && ctrl && event.shiftKey && !event.altKey && event.code === 'KeyO' && !focusedEditor()?.getAction('editor.action.quickOutline')?.isSupported()) announce(unsupported('editor.action.quickOutline', f.language));
      return;
    }
    if (event.code === 'F1' && !ctrl && !event.altKey && !event.shiftKey) { event.preventDefault(); if (available()) runEditorAction('editor.action.quickCommand'); return; }
    const command = COMMANDS.find(c => c.key && same(c.key));
    if (command) { event.preventDefault(); command.run(); }
  }, true);

  $('unlock').onclick = unlock;
  $('offline-retry').onclick = unlock;
  $('work-offline').onclick = () => {
    try { session.goOffline(); } catch { return; }
    syncModel(); render();
    announce('Working offline: nothing is saved or encrypted until the vault is back. Closing the editor loses unsaved work.');
  };
  $('lock').onclick = () => act(async () => { await session.lock(); return 'Locked. The service session ended; unsaved edits are kept in this editor.'; });
  $('save').onclick = save;
  $('conflict-reload').onclick = () => {
    const f = active();
    if (!f) return;
    let shown = true;
    act(async () => {
      shown = await session.reload(f.id); keepEditing.delete(f.id);
      return shown ? `Reloaded ${f.path}: your unsaved edits were discarded.` : `Reloaded ${f.path}: your unsaved edits were discarded. Their version is binary or over 1 MiB, so its details are shown.`;
    }, () => { if (!shown) openInfo(f.id); });
  };
  $('conflict-keep').onclick = () => { const f = active(); if (f) { keepEditing.add(f.id); render(); announce('Keeping your edits. Saving will still conflict; reload to take their version.'); } };
  $('switch-project').onclick = () => {
    if (session.dirtyIds.length) { announce(FAILURES.UnsavedChanges); return; }
    act(async () => { await session.refreshProjects(); await closeInfo(); closeHistory(); closeBuilds(); showDeleted = false; merging = null; picking = true; });
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
    act(async () => { await session.createProject(name); disposeAllModels(); keepEditing.clear(); picking = false; $('new-project-name').value = ''; return `Created project "${name}".${await saveOfflineWork()}`; });
  };
  session.onChange(() => {
    syncModel(); render();
    if (session.locked) { notConnected = null; closeHistory(); closeBuilds(); closeCompare(); wb.onLock(); clearInternalClipboard(); showDeleted = false; merging = null; notMergeable.clear(); render(); announce(`Vault locked (${session.lockReason}). Unsaved edits are kept.`); $('unlock').focus(); }
  });
  wireChrome(focusedEditor, { onSave: save, isLocked: () => session.locked, skipKey: event => chordKeys.has(event) });
  // In vault mode the rail's search button searches all files (Ctrl+F still finds in the open file).
  $('explorer-toggle').onclick = () => showSidebar('explorer', true);
  $('find').onclick = () => { if (sidebar === 'search') showSidebar('search', true); else wb.showSearch(); };
  $('settings-button').onclick = () => wb.openSettings();
  $('shortcuts-button').onclick = () => wb.openShortcuts();

  render();
  try {
    await loadMonaco();
    registerOutlines();
    editor = createEditor(demo ? 'Demo editor. Nothing is encrypted.' : 'Vault source editor. Saved changes become encrypted revisions in the vault.');
    wireEditor(editor, 0);
    await wb.loadSettings(); // display preferences: available before unlocking
    $('loading').hidden = true;
    $('footer-status').textContent = 'Vault editor';
    unlock();
  } catch {
    showLoadFailure();
  }
}

const typeIcon = language => ({ typescript: 'TS', javascript: 'JS', markdown: 'M↓', json: '{}', css: '#', html: '<>', csharp: 'C#', cpp: 'C++', c: 'C',
  python: 'PY', xml: '<>', powershell: 'PS', shell: '$', bat: '>_', yaml: 'YM', sql: 'SQL' })[language] ?? '··';
const utf8Length = s => { let n = 0; for (let i = 0; i < s.length; i++) { const c = s.charCodeAt(i); n += c < 0x80 ? 1 : c < 0x800 ? 2 : c >= 0xd800 && c < 0xdc00 && i + 1 < s.length ? (i++, 4) : 3; } return n; };

// Outlines (Ctrl+Shift+O, @ in Go to File) for languages Monaco only highlights. workbench.mjs explains the limits.
const SYMBOL_KINDS = { namespace: 'Namespace', class: 'Class', struct: 'Struct', interface: 'Interface', enum: 'Enum', function: 'Function', method: 'Method',
  constructor: 'Constructor', property: 'Property', field: 'Field', macro: 'Constant' };
function registerOutlines() {
  const toSymbol = s => {
    const start = Math.min(s.column, s.nameColumn), nameEnd = s.nameColumn + s.nameLength;
    const endColumn = s.endLine > s.line ? s.endColumn : Math.max(s.endColumn, nameEnd);
    return { name: s.name, detail: '', kind: monaco.languages.SymbolKind[SYMBOL_KINDS[s.kind]], tags: [],
      range: new monaco.Range(s.line, start, Math.max(s.endLine, s.line), endColumn), selectionRange: new monaco.Range(s.line, s.nameColumn, s.line, nameEnd),
      children: s.children.map(toSymbol) };
  };
  for (const language of ['csharp', 'cpp', 'c'])
    monaco.languages.registerDocumentSymbolProvider(language, { displayName: 'SourceVault outline', provideDocumentSymbols: model => outline(language, model.getValue()).map(toSymbol) });
}

const DEMO_NOTE = 'DEMO: nothing here is encrypted and no vault is connected. Use made-up code only.';

function relabel(metadata) {
  const demo = !!metadata.demo;
  $('mode-badge').textContent = demo ? 'DEMO · NOT ENCRYPTED' : 'Vault';
  $('demo-banner').hidden = $('demo-status').hidden = !demo;
  $('vault-explorer-actions').hidden = false;
  $('settings-button').hidden = $('shortcuts-button').hidden = false;
  $('find').title = 'Search all files (Ctrl+Shift+F). Ctrl+F finds in the open file.'; $('find').setAttribute('aria-label', 'Search all files');
  $('new-file-name').placeholder = 'src/name.cpp';
  $('folder-row').hidden = true;
  $('explorer-note-label').textContent = demo ? 'DEMO · NOT ENCRYPTED' : 'VAULT';
  $('explorer-note-text').textContent = demo
    ? 'Demo projects live only in this window’s memory and are gone when it closes. Nothing is encrypted; what you type can be left on this computer or captured on screen.'
    : 'Encrypted at rest by the SourceVault service. Text is decrypted only for files you open.';
  $('save-label').textContent = 'Save';
  $('save').title = demo ? 'Save to the demo vault: in memory, NOT encrypted (Ctrl+S)' : 'Save to the vault as a new encrypted revision (Ctrl+S)';
  $('no-file-title').textContent = 'Choose a file';
  $('no-file-detail').textContent = 'Open a file from the explorer, or create one with + New file.';
  $('unlock-label').textContent = 'Unlock vault';
  $('session-title').textContent = demo ? 'Demo vault (simulated)' : 'SourceVault service';
  $('session-subtitle').textContent = demo ? 'No service · no TPM key · no encryption' : 'TPM-protected key · operator session';
  $('source-detail').textContent = demo ? 'Demo, in memory' : 'Vault';
  $('capture-detail').textContent = metadata.captureExclusion ? 'Excluded (best effort)' : demo ? 'Allowed (demo)' : 'Allowed';
  $('revision-label').textContent = 'SAVED THIS SESSION';
  $('revision-copy').textContent = demo ? 'Each save is a new revision in the demo vault, in memory and not encrypted.' : 'Each save is a new immutable revision in the vault.';
  $('memory-text').textContent = demo ? 'NOT ENCRYPTED' : 'Encrypted at rest';
  $('boundary-title').textContent = demo ? 'Demo: spillage can happen' : 'Vault editor';
  $('boundary-text').textContent = demo
    ? 'Nothing typed here is protected. It stays in memory, can reach the page file or crash data, and this window can be screen-captured and shared. Do not type real source code.'
    : 'Text of open files lives in this editor’s memory. Screen-capture exclusion is best effort. Builds are not connected.';
  if (demo) {
    document.querySelector('#settings-dialog .dialog-note').textContent = 'Demo: these choices are kept only until the editor closes. Only display choices, never file names or text.';
    $('project-picker').querySelector('.eyebrow').textContent = 'DEMO PROJECTS · NOT ENCRYPTED';
  }
  $('reset').hidden = true;
  $('activity-message').textContent = demo ? 'Demo mode. Nothing is encrypted.' : 'Vault mode.';
}
