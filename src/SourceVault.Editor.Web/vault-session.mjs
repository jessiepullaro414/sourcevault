// Vault-mode session state for the renderer (docs/editor-vault-design.md, bridge v2).
//
// Pure ES module: no DOM. Talks to the editor host through a transport { send(message), subscribe(handler) }:
// in the native editor that is window.chrome.webview; in tests and browser review it is the fake host.
// The renderer never sees capabilities or snapshot/revision ids; it works with project/file ids, names and text.
// Document text is held only for opened files and dropped when a file is closed. On lock, unsaved drafts stay in
// memory behind the lock screen (owner decision) and are re-checked against the vault on unlock.

import { merge3 } from './merge.mjs';
import { withDrafts, validQuery, validSettings } from './workbench.mjs';

export const REQUEST_TYPE = 'sourcevault.vault.request';
export const RESPONSE_TYPE = 'sourcevault.vault.response';
export const LOCKED_TYPE = 'sourcevault.vault.locked';
export const PROGRESS_TYPE = 'sourcevault.vault.progress';
const MAX_DIRTY = 32;          // host status-message bounds
const MAX_REVISIONS = 1000000;

const LANGUAGES = { ts: 'typescript', tsx: 'typescript', mts: 'typescript', js: 'javascript', mjs: 'javascript', jsx: 'javascript',
  json: 'json', md: 'markdown', css: 'css', html: 'html', xml: 'xml', cs: 'csharp', py: 'python', sql: 'sql', yml: 'yaml', yaml: 'yaml',
  // C and C++ (Monaco's C++ highlighting covers both); .h is treated as C++.
  c: 'c', cpp: 'cpp', cc: 'cpp', cxx: 'cpp', 'c++': 'cpp', cppm: 'cpp', ixx: 'cpp', h: 'cpp', hpp: 'cpp', hh: 'cpp', hxx: 'cpp', 'h++': 'cpp', inl: 'cpp', ipp: 'cpp', tpp: 'cpp',
  // Build and project files.
  csproj: 'xml', vcxproj: 'xml', props: 'xml', targets: 'xml', filters: 'xml', xaml: 'xml', config: 'xml', ps1: 'powershell', psm1: 'powershell',
  psd1: 'powershell', sh: 'shell', bash: 'shell', bat: 'bat', cmd: 'bat', ini: 'ini', dockerfile: 'dockerfile' };
export const languageFor = name => LANGUAGES[String(name).split('.').at(-1).toLowerCase()] ?? 'plaintext';

/** Text the editor opens and saves (docs/vault-any-file-design.md section 4). Other files get the info panel. */
export const MAX_EDITOR_TEXT_BYTES = 1024 * 1024;
/** Whether the editor can show a file or revision ({ kind, byteLength }): text of at most 1 MiB. */
export const isOpenable = f => (f.kind ?? 'text') === 'text' && f.byteLength <= MAX_EDITOR_TEXT_BYTES;
/** The explorer badge: null for openable files, else binary | large. */
export const badgeOf = f => isOpenable(f) ? null : (f.kind === 'binary' ? 'binary' : 'large');
const tooLarge = text => new TextEncoder().encode(text).length > MAX_EDITOR_TEXT_BYTES;

/** A failed vault operation. `code` is a fixed protocol/bridge code, never text from a document. */
export class VaultError extends Error {
  constructor(code) { super(code); this.code = code; }
}

/** Correlates requests and responses by requestId; forwards host lock events. */
export class BridgeClient {
  #transport; #pending = new Map(); #onLocked; #timeoutMs; #unsubscribe;
  constructor(transport, { onLocked = () => {}, timeoutMs = 30000 } = {}) {
    this.#transport = transport; this.#onLocked = onLocked; this.#timeoutMs = timeoutMs;
    this.#unsubscribe = transport.subscribe(message => this.#receive(message));
  }
  #receive(message) {
    if (message?.type === RESPONSE_TYPE && this.#pending.has(message.requestId)) {
      const { resolve, timer } = this.#pending.get(message.requestId);
      this.#pending.delete(message.requestId); clearTimeout(timer);
      resolve({ code: String(message.code), result: message.result ?? null });
    } else if (message?.type === PROGRESS_TYPE && this.#pending.has(message.requestId)) {
      // A long request (import, drop) is still working: restart its timeout and report bytes done.
      const pending = this.#pending.get(message.requestId);
      clearTimeout(pending.timer); pending.timer = pending.arm();
      pending.onProgress?.(Number(message.done) || 0, Number(message.total) || 0);
    } else if (message?.type === LOCKED_TYPE) this.#onLocked(String(message.reason));
  }
  /** `objects`: File objects passed to the host unread (dropped files). `onProgress(done, total)`: bytes uploaded. */
  request(operation, args = {}, { objects = null, onProgress = null, timeoutMs = this.#timeoutMs } = {}) {
    const requestId = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const arm = () => setTimeout(() => { this.#pending.delete(requestId); reject(new VaultError('Timeout')); }, timeoutMs);
      this.#pending.set(requestId, { resolve, timer: arm(), arm, onProgress });
      this.#transport.send({ type: REQUEST_TYPE, requestId, operation, args }, objects);
    });
  }
  dispose() { this.#unsubscribe?.(); for (const { timer } of this.#pending.values()) clearTimeout(timer); this.#pending.clear(); }
}

/** Unlock failures that mean the service can't be reached: only then can the editor work offline. */
export const OFFLINE_CODES = new Set(['ServiceNotRunning', 'ServiceNotInstalled', 'Timeout', 'ServerUntrusted']);
// What still talks to the host offline: connecting again, locking, and display settings. Everything else needs the vault.
const OFFLINE_ALLOWED = new Set(['unlock', 'unlockWithPassword', 'setPassword', 'lock', 'getSettings', 'saveSettings']);
/** The service wants the operator's SourceVault password (2026-10-20): the lock screen shows a password form. */
export const PASSWORD_CODES = new Set(['PasswordRequired', 'PasswordNotSet', 'WrongPassword', 'PasswordLocked', 'PasswordTooWeak']);

export class VaultSession {
  projects = [];
  project = null;          // { projectId, name, fileCount }
  files = new Map();       // fileId -> { id, path, language, byteLength, kind, sha256, loaded, text, saved, revision, conflict, pending }
  openIds = [];
  activeId = null;
  #locked = true;
  /**
   * Offline (owner decision 2026-10-20): the service can't be reached and the user chose to keep working. Files already
   * open stay editable and new files can be made, all in this window's memory only (nothing is written to disk). Saves
   * wait for the vault (saveWhenBack). Everything that needs the service is refused with Offline. Any lock ends it.
   */
  offline = false;
  lastUnlockFailure = null; // the code of the last failed unlock
  lockReason = 'start';    // start | user | windows | closed | expired
  savedCount = 0;
  #bridge; #listeners = new Set();

  constructor(transport, { timeoutMs } = {}) {
    this.#bridge = new BridgeClient(transport, { timeoutMs, onLocked: reason => { this.#setLocked(reason); this.#emit(); } });
  }
  /** Called after any state change the UI did not initiate itself (host lock events). */
  onChange(listener) { this.#listeners.add(listener); return () => this.#listeners.delete(listener); }
  #emit() { for (const listener of this.#listeners) listener(); }
  /** Locked: nothing can be edited. Offline work is not locked: the files in memory stay editable. */
  get locked() { return this.#locked && !this.offline; }
  #setLocked(reason) { this.#locked = true; this.offline = false; this.lockReason = reason; }

  async #call(operation, args, options) {
    if (this.offline && !OFFLINE_ALLOWED.has(operation)) throw new VaultError('Offline'); // needs the vault: never sent
    const { code, result } = await this.#bridge.request(operation, args, options);
    if (code === 'Ok') return result ?? {};
    if (code === 'Locked') this.#setLocked(this.locked ? this.lockReason : 'closed');
    throw new VaultError(code);
  }

  file(id) { const f = this.files.get(id); if (!f) throw new VaultError('UnknownFile'); return f; }
  // A pending merge counts as unsaved even if its text happens to equal the last saved text.
  dirty(id) { const f = this.files.get(id); return !!f && f.loaded && (f.text !== f.saved || !!f.mergedOnto || !!f.pendingCreate); }
  get dirtyIds() { return [...this.files.values()].filter(f => this.dirty(f.id)).map(f => f.id); }
  get metadata() {
    return { locked: this.locked, dirtyCount: Math.min(MAX_DIRTY, this.dirtyIds.length), revisionCount: Math.min(MAX_REVISIONS, this.savedCount) };
  }

  /** Opens a session. Returns the ids of opened files that changed in the vault while locked (now marked conflict). */
  /** `password`: the operator's SourceVault password; `newPassword`: sets the first one (or one an administrator reset). */
  async unlock(password = null, newPassword = null) {
    let result;
    const [operation, args] = newPassword ? ['setPassword', { newPassword }] : password ? ['unlockWithPassword', { password }] : ['unlock', {}];
    try { result = await this.#call(operation, args); this.lastUnlockFailure = null; }
    catch (e) { this.lastUnlockFailure = e.code; throw e; } // offline work, if any, carries on
    this.#locked = false; this.offline = false; this.lockReason = null;
    // Offline work begun without a project: show the project list; the new files go into the project chosen there.
    if (this.project && !this.project.projectId) this.project = null;
    const changed = (result.changed ?? []).filter(c => c.projectId === this.project?.projectId).map(c => c.fileId);
    // A draft already merged onto their version is checked again by its save, so it is not flagged here. An info
    // panel's file (not loaded) is simply shown as it is now. A staged replacement was discarded with the connection.
    for (const f of this.files.values()) f.pending = false;
    for (const id of changed) { const f = this.files.get(id); if (f?.loaded && !f.mergedOnto) f.conflict = true; }
    await this.refreshProjects();
    if (this.project) await this.#refreshFiles();
    return changed;
  }

  async lock() {
    await this.#call('lock');
    this.#setLocked('user');
  }

  /** Changes the operator's SourceVault password (the current one must match). */
  async changePassword(password, newPassword) { await this.#call('changePassword', { password, newPassword }); }

  /**
   * Keeps working while the service can't be reached (after a failed unlock with one of OFFLINE_CODES). Files already
   * open stay editable; new files are kept in memory. Without an open project, a placeholder holds the new files.
   */
  goOffline() {
    if (!this.#locked || !OFFLINE_CODES.has(this.lastUnlockFailure)) throw new VaultError('InvalidRequest');
    this.offline = true;
    if (!this.project) this.project = { projectId: null, name: 'Offline work (not saved yet)', fileCount: 0 };
  }

  /** Offline: Save marks the file; it is saved when the vault is back (saveQueued). */
  queueSave(id) {
    const f = this.file(id);
    if (!this.dirty(id)) return false;
    f.saveWhenBack = true;
    return true;
  }

  /**
   * Saves what was saved offline, now that the vault is back: edits of existing files (a file someone else changed
   * meanwhile is left as a conflict to merge), and new files (when a project is open). Returns { saved: [path],
   * failed: [{ path, code }], waiting: count (new files waiting for a project), replaced: [[oldId, newId]] }.
   */
  async saveQueued() {
    const result = { saved: [], failed: [], waiting: 0, replaced: [] };
    if (this.#locked) return result;
    for (const f of [...this.files.values()].filter(x => x.saveWhenBack)) {
      if (f.pendingCreate && !this.project?.projectId) { result.waiting++; continue; }
      f.saveWhenBack = false;
      try {
        if (f.pendingCreate) result.replaced.push([f.id, await this.savePending(f.id)]);
        else if (f.conflict) { result.failed.push({ path: f.path, code: 'Conflict' }); continue; }
        else await this.save(f.id);
        result.saved.push(f.path);
      } catch (e) {
        if (e.code === 'Locked') throw e;
        result.failed.push({ path: f.path, code: e.code });
      }
    }
    return result;
  }

  /** Creates a file made offline in the open project. Returns its new id (the offline one is gone). */
  async savePending(id) {
    const f = this.file(id);
    if (!f.pendingCreate) throw new VaultError('InvalidRequest');
    if (!this.project?.projectId) throw new VaultError('NoProject');
    const created = await this.#call('createFile', { projectId: this.project.projectId, name: f.path, text: f.text });
    this.files.delete(id);
    const at = this.openIds.indexOf(id);
    this.files.set(created.fileId, { ...f, id: created.fileId, path: created.name, pendingCreate: false, saveWhenBack: false, saved: f.text, byteLength: new TextEncoder().encode(f.text).length });
    if (at >= 0) this.openIds[at] = created.fileId; else this.openIds.push(created.fileId);
    if (this.activeId === id) this.activeId = created.fileId;
    this.savedCount++;
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + 1 };
    return created.fileId;
  }

  async refreshProjects() {
    this.projects = (await this.#call('listProjects')).projects ?? [];
    if (this.project) this.project = this.projects.find(p => p.projectId === this.project.projectId) ?? this.project;
  }

  /** Unsaved edits that stop switching project: everything but new files made offline (those go along). */
  get #blockingDrafts() { return this.dirtyIds.filter(id => !this.files.get(id).pendingCreate); }

  async createProject(name) {
    if (this.#blockingDrafts.length) throw new VaultError('UnsavedChanges'); // check before creating anything on the host
    const created = await this.#call('createProject', { name });
    await this.refreshProjects();
    await this.selectProject(created.projectId);
  }

  /**
   * Deletes a project and every revision in it. `confirmName` must be the project's exact name (checked here and by the
   * service). Deleting the open project drops its files from this editor. Returns false if the service still has to
   * remove some encrypted files later (they were in use); the project is gone from the list either way.
   */
  async deleteProject(projectId, confirmName) {
    const target = this.projects.find(p => p.projectId === projectId);
    if (!target) throw new VaultError('NotFound');
    if (confirmName !== target.name) throw new VaultError('NameMismatch'); // an offline project can still be deleted
    const current = this.project?.projectId === projectId;
    if (current && this.dirtyIds.length) throw new VaultError('UnsavedChanges');
    const result = await this.#call('deleteProject', { projectId, name: confirmName });
    if (current) { this.project = null; this.files = new Map(); this.openIds = []; this.activeId = null; }
    await this.refreshProjects();
    return result.filesRemoved !== false;
  }

  /** Switches project. Refused with unsaved edits; documents of the previous project are closed on the host. */
  async selectProject(projectId) {
    if (this.#blockingDrafts.length) throw new VaultError('UnsavedChanges');
    const carried = [...this.files.values()].filter(f => f.pendingCreate); // new files made offline go into this project
    if (this.project?.projectId) for (const id of this.openIds.filter(id => !this.files.get(id)?.pendingCreate)) await this.#call('closeDocument', { projectId: this.project.projectId, fileId: id });
    this.project = this.projects.find(p => p.projectId === projectId) ?? null;
    if (!this.project) throw new VaultError('NotFound');
    if (this.project.available === false) { this.project = null; throw new VaultError('Unavailable'); } // offline: failed its integrity check
    this.files = new Map(carried.map(f => [f.id, f])); this.openIds = carried.map(f => f.id); this.activeId = this.openIds.at(-1) ?? null;
    await this.#refreshFiles();
  }

  // Merges the listing: new files appear, deleted ones vanish unless they hold a draft, loaded text is kept.
  async #refreshFiles() {
    const listed = (await this.#call('listFiles', { projectId: this.project.projectId })).files ?? [];
    const next = new Map();
    for (const info of listed) {
      const existing = this.files.get(info.fileId);
      const listing = { path: info.name, language: languageFor(info.name), byteLength: info.byteLength, kind: info.kind ?? 'text', sha256: info.sha256 ?? null, deleted: false };
      next.set(info.fileId, existing ? { ...existing, ...listing }
        : { id: info.fileId, ...listing, loaded: false, text: null, saved: null, revision: 0, conflict: false, pending: false });
    }
    // Someone else deleted a file this editor holds a draft of: keep the draft, marked, so it can be saved as a new file.
    // New files made offline aren't in the vault yet: they simply stay.
    for (const [id, f] of this.files) if (!next.has(id) && this.dirty(id)) next.set(id, f.pendingCreate ? f : { ...f, deleted: true });
    this.files = next;
    this.openIds = this.openIds.filter(id => this.files.has(id));
    if (this.activeId && !this.files.has(this.activeId)) this.activeId = this.openIds.at(-1) ?? null;
  }

  async open(id) {
    const f = this.file(id);
    if (!f.loaded && !isOpenable(f)) throw new VaultError('NotOpenable'); // the UI shows the info panel instead
    if (!f.loaded) {
      const doc = await this.#call('openDocument', { projectId: this.project.projectId, fileId: id });
      f.text = f.saved = doc.text; f.loaded = true; f.conflict = false;
    }
    if (!this.openIds.includes(id)) this.openIds.push(id);
    this.activeId = id;
  }

  /**
   * Discards local edits and loads the vault's current version (after a conflict). Returns false when their version is
   * binary or over 1 MiB: the tab is then closed and the file shows its info panel.
   */
  async reload(id) {
    const f = this.file(id);
    try {
      const doc = await this.#call('openDocument', { projectId: this.project.projectId, fileId: id });
      f.text = f.saved = doc.text; f.loaded = true; f.conflict = false; f.mergedOnto = null;
      return true;
    } catch (e) {
      if (e.code !== 'NotText' && e.code !== 'LimitExceeded') throw e;
      await this.#refreshFiles();
      this.#unload(id);
      return false;
    }
  }

  #unload(id) {
    const f = this.files.get(id);
    if (f) { f.loaded = false; f.text = f.saved = null; f.conflict = false; f.mergedOnto = null; }
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
  }

  /** Closes a tab; refused with unsaved edits. The file's text is dropped from memory. */
  async close(id) {
    const f = this.file(id);
    if (this.dirty(id)) throw new VaultError('UnsavedChanges');
    await this.#call('closeDocument', { projectId: this.project.projectId, fileId: id });
    f.loaded = false; f.text = f.saved = null; f.conflict = false;
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
  }

  edit(id, text) {
    if (this.locked) throw new VaultError('Locked'); // offline is not locked: open files stay editable
    if (typeof text !== 'string') throw new TypeError('TextRequired');
    const f = this.file(id);
    if (!f.loaded) throw new VaultError('NotOpen');
    f.text = text;
  }

  /** Saves the whole file. Returns false if there was nothing to save. Conflict: someone else changed it. */
  async save(id) {
    const f = this.file(id);
    if (!this.dirty(id)) return false;
    const sent = f.text; // edits made while the save is in flight stay dirty
    if (tooLarge(sent)) throw new VaultError('LimitExceeded'); // the host would ignore it
    const merged = f.mergedOnto;
    try {
      const result = merged
        ? await this.#call('saveMerged', { projectId: this.project.projectId, fileId: id, text: sent, revisionId: merged })
        : await this.#call('saveDocument', { projectId: this.project.projectId, fileId: id, text: sent });
      f.saved = sent; f.byteLength = result.byteLength; f.kind = 'text'; f.revision++; f.conflict = false; f.mergedOnto = null; this.savedCount++;
      return true;
    } catch (e) {
      if (e.code === 'Conflict') { f.conflict = true; f.mergedOnto = null; } // changed again: merge again from the latest version
      throw e;
    }
  }

  /**
   * Starts a merge after a conflict: fetches the version this editor started from and their current version, and merges
   * them with the draft. Returns { segments, conflicts, theirsRevisionId } (see merge.mjs); nothing changes until
   * applyMerge is called.
   */
  async prepareMerge(id) {
    const f = this.file(id);
    if (!f.loaded) throw new VaultError('NotOpen');
    const sources = await this.#call('mergeSources', { projectId: this.project.projectId, fileId: id });
    // Binary or over 1 MiB on a side: no text merge, only keep mine (save over theirs) or keep theirs (reload).
    if (sources.mergeable === false) return { mergeable: false, theirsRevisionId: sources.theirsRevisionId };
    return { mergeable: true, ...merge3(sources.base, f.text, sources.theirs), theirsRevisionId: sources.theirsRevisionId };
  }

  /** Takes a merge result as the draft. The next save is made on top of their version, if it is still current. */
  applyMerge(id, text, theirsRevisionId) {
    if (this.locked) throw new VaultError('Locked');
    const f = this.file(id);
    f.text = text; f.mergedOnto = theirsRevisionId; f.conflict = false;
  }

  async renameProject(projectId, newName) {
    await this.#call('renameProject', { projectId, newName });
    await this.refreshProjects();
  }

  /** Renames or moves a file. Its text, draft and tab stay as they are; only the path changes. */
  async renameFile(id, newName) {
    const f = this.file(id);
    if (f.pendingCreate) { // not in the vault yet: only its name here changes
      if ([...this.files.values()].some(o => o !== f && o.path.toLowerCase() === newName.toLowerCase())) throw new VaultError('AlreadyExists');
      f.path = newName; f.language = languageFor(newName);
      return;
    }
    await this.#call('renameFile', { projectId: this.project.projectId, fileId: id, newName });
    f.path = newName; f.language = languageFor(newName);
  }

  /** Renames every file under `folder` ("src/app") to `newFolder`. Returns how many files moved. */
  async renameFolder(folder, newFolder) {
    const result = await this.#call('renameFolder', { projectId: this.project.projectId, name: folder, newName: newFolder });
    await this.#refreshFiles();
    return result.renamed;
  }

  // ---- files the editor doesn't show (binary, or text over 1 MiB), and files from disk ----------------------------
  // The host shows its own Open dialog and reads the file: this page never sees a path or the bytes.

  /** Info panel: { fileId, name, kind, byteLength, sha256, savedAt, savedBy }. The host then treats the file as shown. */
  async info(id) {
    const f = this.file(id);
    const result = await this.#call('fileInfo', { projectId: this.project.projectId, fileId: id });
    Object.assign(f, { path: result.name, language: languageFor(result.name), byteLength: result.byteLength, kind: result.kind, sha256: result.sha256 });
    return result;
  }

  /** The info panel closed: the host forgets the file (an opened text file stays open). */
  async closeInfo(id) {
    const f = this.files.get(id);
    if (f && !f.loaded && this.project) await this.#call('closeDocument', { projectId: this.project.projectId, fileId: id });
  }

  #took(f, result) {
    Object.assign(f, { byteLength: result.byteLength, kind: result.kind, sha256: result.sha256, pending: false, conflict: false });
    f.revision++; this.savedCount++;
  }

  /**
   * Replaces a file with one chosen in the host's Open dialog. Returns false if the dialog was cancelled. Conflict:
   * someone else changed the file meanwhile; the upload stays staged for keepMine / keepTheirs (f.pending).
   */
  async replaceFromDisk(id) {
    const f = this.file(id);
    if (this.dirty(id)) throw new VaultError('UnsavedChanges');
    let result;
    try { result = await this.#call('replaceFromDisk', { projectId: this.project.projectId, fileId: id }); }
    catch (e) {
      if (e.code === 'Cancelled') return false;
      if (e.code === 'Conflict') f.pending = true;
      throw e;
    }
    this.#took(f, result);
    if (f.loaded) { if (isOpenable(f)) await this.reload(id); else this.#unload(id); }
    return true;
  }

  /** After a replacement conflicted: my file goes over theirs (theirs stays in history). */
  async keepMine(id) {
    const f = this.file(id);
    try { this.#took(f, await this.#call('keepMine', { projectId: this.project.projectId, fileId: id })); }
    finally { f.pending = false; }
    if (f.loaded) { if (isOpenable(f)) await this.reload(id); else this.#unload(id); }
  }

  /** After a replacement conflicted: my upload is discarded and theirs is shown. */
  async keepTheirs(id) {
    const f = this.file(id);
    try {
      const result = await this.#call('keepTheirs', { projectId: this.project.projectId, fileId: id });
      Object.assign(f, { byteLength: result.byteLength, kind: result.kind, sha256: result.sha256 });
    } finally { f.pending = false; }
  }

  /** Adds files chosen in the host's Open dialog under `folder` ('' for the root). Returns their names ([] if cancelled). */
  async addFromDisk(folder = '') {
    let result;
    try { result = await this.#call('addFromDisk', { projectId: this.project.projectId, folder }); }
    catch (e) { if (e.code === 'Cancelled') return []; throw e; }
    await this.#refreshFiles();
    const added = result.files ?? [];
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + added.length };
    this.savedCount += added.length;
    return added.map(a => a.name);
  }

  /**
   * "Import a folder": the host shows its folder dialog and scans the folder with the importer's rules. Returns
   * { folder, files, bytes, problem, skippedCount, skipped: [{ path, reason }] }, or null if the dialog was cancelled.
   * Nothing is imported until importFolder.
   */
  async chooseImportFolder() {
    try { return await this.#call('chooseImportFolder', {}, { timeoutMs: 10 * 60 * 1000 }); } // the dialog and a large scan take a while
    catch (e) { if (e.code === 'Cancelled') return null; throw e; }
  }

  /**
   * Imports the chosen folder as a new project named `name`, verified by the service; a failure removes the new
   * project again. Returns { projectId, imported, verified } and opens the project (unless drafts are open).
   */
  async importFolder(name, onProgress) {
    const result = await this.#call('importFolder', { name }, { onProgress, timeoutMs: 15 * 60 * 1000 });
    await this.refreshProjects();
    if (!this.dirtyIds.length) await this.selectProject(result.projectId);
    return result;
  }

  async cancelImport() { await this.#call('cancelImport', {}); }

  /**
   * Adds files and folders dropped on the explorer under `folder` ('' for the root). The File objects go to the host
   * unread. Returns { added, skipped, names, stopped } (stopped: the code that ended it part-way, or null).
   */
  async dropFiles(folder, files, onProgress) {
    const result = await this.#call('dropFiles', { projectId: this.project.projectId, folder }, { objects: files, onProgress, timeoutMs: 15 * 60 * 1000 });
    await this.#refreshFiles();
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + result.added };
    this.savedCount += result.added;
    return result;
  }

  /**
   * Makes an older version current again (any kind of file; nothing is decrypted here). Refused with unsaved edits;
   * Conflict if someone else changed the file since the editor showed it.
   */
  async revertFile(id, revisionId) {
    if (this.dirty(id)) throw new VaultError('UnsavedChanges');
    this.file(id);
    await this.#call('revertFile', { projectId: this.project.projectId, fileId: id, revisionId });
    this.savedCount++;
    await this.#refreshFiles();
    const now = this.files.get(id);
    if (now?.loaded) { if (isOpenable(now)) await this.reload(id); else this.#unload(id); }
  }

  /** Deletes a file from the vault. Refused with unsaved edits; Conflict if someone else changed it since it was opened. */
  async deleteFile(id) {
    const f = this.file(id);
    if (f.pendingCreate) { this.files.delete(id); this.openIds = this.openIds.filter(x => x !== id); if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null; return; } // discards it
    if (!f.deleted && this.dirty(id)) throw new VaultError('UnsavedChanges'); // a draft of a file deleted elsewhere may be discarded
    if (!f.deleted) await this.#call('deleteFile', { projectId: this.project.projectId, fileId: id });
    this.#forget(id);
  }

  /** A draft whose file someone else deleted: saves it as a new file at the same path. */
  async saveAsNew(id) {
    const f = this.file(id);
    if (!f.deleted) throw new VaultError('NotDeleted');
    const text = f.text;
    this.#forget(id);
    return this.createFile(f.path, text);
  }

  /** One page of a file's history, newest first: { revisions: [{ revisionId, savedAt, savedBy, byteLength, action, name }], more }. */
  async listHistory(fileId, page = 0) {
    const result = await this.#call('listHistory', { projectId: this.project.projectId, fileId, page });
    return { revisions: result.revisions ?? [], more: !!result.more };
  }

  /** The text of one revision of a live or deleted file. Not kept by the session. */
  async openRevision(fileId, revisionId) {
    return (await this.#call('openRevision', { projectId: this.project.projectId, fileId, revisionId })).text;
  }

  /** Puts an old revision's text into the editor as unsaved edits of the file (opened if needed). Saving makes it current. */
  async restoreRevision(id, revisionId) {
    const text = await this.openRevision(id, revisionId);
    await this.open(id);
    this.edit(id, text);
  }

  /** Deleted files of the open project, most recently deleted first. */
  async listDeleted() {
    return (await this.#call('listDeleted', { projectId: this.project.projectId })).files ?? [];
  }

  /** Restores a deleted file under `name` (its old name, or a new one if that is taken). */
  async restoreFile(fileId, name) {
    await this.#call('restoreFile', { projectId: this.project.projectId, fileId, newName: name });
    await this.#refreshFiles();
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + 1 };
  }

  /**
   * Searches every file of the project for plain text (no regular expressions). The service searches the saved files;
   * files with unsaved edits are searched here, in their draft. Returns { matches: [{ fileId, name, line, column, length,
   * preview, previewOffset, draft? }], truncated, filesSearched, drafts }.
   */
  async search(query, { matchCase = false, wholeWord = false } = {}) {
    if (!validQuery(query)) throw new VaultError('InvalidRequest');
    const result = await this.#call('searchProject', { projectId: this.project.projectId, query, matchCase, wholeWord });
    const drafts = this.dirtyIds.map(id => this.files.get(id)).map(f => ({ id: f.id, path: f.path, text: f.text }));
    return withDrafts(result, drafts, query, { matchCase, wholeWord });
  }

  /** The editor's display preferences (kept by the host per Windows user; works while locked). */
  async getSettings() { return (await this.#call('getSettings', {})).settings; }
  async saveSettings(settings) {
    if (!validSettings(settings)) throw new VaultError('InvalidRequest'); // the host would ignore it
    await this.#call('saveSettings', { settings: { ...settings } });
  }

  // ---- builds: names of the administrator's build targets, queued builds of the saved project, and their logs -------
  async listBuildTargets() { return (await this.#call('listBuildTargets', {})).targets ?? []; }

  /** Queues a build of the project's saved files (unsaved edits are not included). Returns the build number. */
  async requestBuild(target) {
    return (await this.#call('requestBuild', { projectId: this.project.projectId, name: target })).buildNumber;
  }

  /** The project's builds, newest first: { buildNumber, target, state, reason, exitCode, requestedBy, requestedAt, finishedAt, artifactFolder, artifactFiles, artifactBytes, artifactsDropped, logAvailable, logTruncated }. */
  async listBuilds() { return (await this.#call('listBuilds', { projectId: this.project.projectId })).builds ?? []; }

  async buildStatus(buildNumber) { return this.#call('buildStatus', { projectId: this.project.projectId, buildNumber }); }

  /** A finished build's output text. It can contain source, so it is shown only in the editor, like files. */
  async openBuildLog(buildNumber) { return (await this.#call('openBuildLog', { projectId: this.project.projectId, buildNumber })).text; }

  /** Opens a finished build's output folder in Explorer (the host gets the folder from the service). */
  async openBuildFolder(buildNumber) { await this.#call('openBuildFolder', { projectId: this.project.projectId, buildNumber }); }

  // A new file made offline: in memory only, created in the vault by savePending once it is back.
  #createOffline(name, text) {
    if (tooLarge(text)) throw new VaultError('LimitExceeded');
    if ([...this.files.values()].some(f => f.path.toLowerCase() === name.toLowerCase())) throw new VaultError('AlreadyExists');
    const id = crypto.randomUUID();
    this.files.set(id, { id, path: name, language: languageFor(name), byteLength: 0, kind: 'text', sha256: null, loaded: true, text, saved: '',
      revision: 0, conflict: false, deleted: false, pending: false, pendingCreate: true });
    this.openIds.push(id); this.activeId = id;
    return id;
  }

  #forget(id) {
    this.files.delete(id);
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
    if (this.project) this.project = { ...this.project, fileCount: Math.max(0, this.project.fileCount - 1) };
  }

  async createFile(name, text = '') {
    if (this.offline) return this.#createOffline(name, text);
    if (tooLarge(text)) throw new VaultError('LimitExceeded');
    const created = await this.#call('createFile', { projectId: this.project.projectId, name, text });
    this.files.set(created.fileId, { id: created.fileId, path: created.name, language: languageFor(created.name),
      byteLength: new TextEncoder().encode(text).length, kind: 'text', sha256: null, loaded: true, text, saved: text, revision: 0, conflict: false, deleted: false, pending: false });
    this.openIds.push(created.fileId); this.activeId = created.fileId;
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + 1 };
    return created.fileId;
  }

  dispose() { this.#bridge.dispose(); this.#listeners.clear(); }
}
