// Vault-mode session state for the renderer (docs/editor-vault-design.md, bridge v2).
//
// Pure ES module: no DOM. Talks to the editor host through a transport { send(message), subscribe(handler) }:
// in the native editor that is window.chrome.webview; in tests and browser review it is the fake host.
// The renderer never sees capabilities or snapshot/revision ids; it works with project/file ids, names and text.
// Document text is held only for opened files and dropped when a file is closed. On lock, unsaved drafts stay in
// memory behind the lock screen (owner decision) and are re-checked against the vault on unlock.

import { merge3 } from './merge.mjs';

export const REQUEST_TYPE = 'sourcevault.vault.request';
export const RESPONSE_TYPE = 'sourcevault.vault.response';
export const LOCKED_TYPE = 'sourcevault.vault.locked';
const MAX_DIRTY = 32;          // host status-message bounds
const MAX_REVISIONS = 1000000;

const LANGUAGES = { ts: 'typescript', tsx: 'typescript', mts: 'typescript', js: 'javascript', mjs: 'javascript', jsx: 'javascript',
  json: 'json', md: 'markdown', css: 'css', html: 'html', xml: 'xml', cs: 'csharp', py: 'python', sql: 'sql', yml: 'yaml', yaml: 'yaml' };
export const languageFor = name => LANGUAGES[String(name).split('.').at(-1).toLowerCase()] ?? 'plaintext';

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
    } else if (message?.type === LOCKED_TYPE) this.#onLocked(String(message.reason));
  }
  request(operation, args = {}) {
    const requestId = crypto.randomUUID();
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.#pending.delete(requestId); reject(new VaultError('Timeout')); }, this.#timeoutMs);
      this.#pending.set(requestId, { resolve, timer });
      this.#transport.send({ type: REQUEST_TYPE, requestId, operation, args });
    });
  }
  dispose() { this.#unsubscribe?.(); for (const { timer } of this.#pending.values()) clearTimeout(timer); this.#pending.clear(); }
}

export class VaultSession {
  projects = [];
  project = null;          // { projectId, name, fileCount }
  files = new Map();       // fileId -> { id, path, language, byteLength, loaded, text, saved, revision, conflict }
  openIds = [];
  activeId = null;
  locked = true;
  lockReason = 'start';    // start | user | windows | closed | expired
  savedCount = 0;
  #bridge; #listeners = new Set();

  constructor(transport, { timeoutMs } = {}) {
    this.#bridge = new BridgeClient(transport, { timeoutMs, onLocked: reason => { this.#setLocked(reason); this.#emit(); } });
  }
  /** Called after any state change the UI did not initiate itself (host lock events). */
  onChange(listener) { this.#listeners.add(listener); return () => this.#listeners.delete(listener); }
  #emit() { for (const listener of this.#listeners) listener(); }
  #setLocked(reason) { this.locked = true; this.lockReason = reason; }

  async #call(operation, args) {
    const { code, result } = await this.#bridge.request(operation, args);
    if (code === 'Ok') return result ?? {};
    if (code === 'Locked') this.#setLocked(this.locked ? this.lockReason : 'closed');
    throw new VaultError(code);
  }

  file(id) { const f = this.files.get(id); if (!f) throw new VaultError('UnknownFile'); return f; }
  // A pending merge counts as unsaved even if its text happens to equal the last saved text.
  dirty(id) { const f = this.files.get(id); return !!f && f.loaded && (f.text !== f.saved || !!f.mergedOnto); }
  get dirtyIds() { return [...this.files.values()].filter(f => this.dirty(f.id)).map(f => f.id); }
  get metadata() {
    return { locked: this.locked, dirtyCount: Math.min(MAX_DIRTY, this.dirtyIds.length), revisionCount: Math.min(MAX_REVISIONS, this.savedCount) };
  }

  /** Opens a session. Returns the ids of opened files that changed in the vault while locked (now marked conflict). */
  async unlock() {
    const result = await this.#call('unlock');
    this.locked = false; this.lockReason = null;
    const changed = (result.changed ?? []).filter(c => c.projectId === this.project?.projectId).map(c => c.fileId);
    // A draft already merged onto their version is checked again by its save, so it is not flagged here.
    for (const id of changed) { const f = this.files.get(id); if (f && !f.mergedOnto) f.conflict = true; }
    await this.refreshProjects();
    if (this.project) await this.#refreshFiles();
    return changed;
  }

  async lock() {
    await this.#call('lock');
    this.#setLocked('user');
  }

  async refreshProjects() {
    this.projects = (await this.#call('listProjects')).projects ?? [];
    if (this.project) this.project = this.projects.find(p => p.projectId === this.project.projectId) ?? this.project;
  }

  async createProject(name) {
    if (this.dirtyIds.length) throw new VaultError('UnsavedChanges'); // check before creating anything on the host
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
    if (confirmName !== target.name) throw new VaultError('NameMismatch');
    const current = this.project?.projectId === projectId;
    if (current && this.dirtyIds.length) throw new VaultError('UnsavedChanges');
    const result = await this.#call('deleteProject', { projectId, name: confirmName });
    if (current) { this.project = null; this.files = new Map(); this.openIds = []; this.activeId = null; }
    await this.refreshProjects();
    return result.filesRemoved !== false;
  }

  /** Switches project. Refused with unsaved edits; documents of the previous project are closed on the host. */
  async selectProject(projectId) {
    if (this.dirtyIds.length) throw new VaultError('UnsavedChanges');
    if (this.project) for (const id of [...this.openIds]) await this.#call('closeDocument', { projectId: this.project.projectId, fileId: id });
    this.project = this.projects.find(p => p.projectId === projectId) ?? null;
    if (!this.project) throw new VaultError('NotFound');
    this.files = new Map(); this.openIds = []; this.activeId = null;
    await this.#refreshFiles();
  }

  // Merges the listing: new files appear, deleted ones vanish unless they hold a draft, loaded text is kept.
  async #refreshFiles() {
    const listed = (await this.#call('listFiles', { projectId: this.project.projectId })).files ?? [];
    const next = new Map();
    for (const info of listed) {
      const existing = this.files.get(info.fileId);
      next.set(info.fileId, existing ? { ...existing, path: info.name, language: languageFor(info.name), byteLength: info.byteLength, deleted: false }
        : { id: info.fileId, path: info.name, language: languageFor(info.name), byteLength: info.byteLength, loaded: false, text: null, saved: null, revision: 0, conflict: false, deleted: false });
    }
    // Someone else deleted a file this editor holds a draft of: keep the draft, marked, so it can be saved as a new file.
    for (const [id, f] of this.files) if (!next.has(id) && this.dirty(id)) next.set(id, { ...f, deleted: true });
    this.files = next;
    this.openIds = this.openIds.filter(id => this.files.has(id));
    if (this.activeId && !this.files.has(this.activeId)) this.activeId = this.openIds.at(-1) ?? null;
  }

  async open(id) {
    const f = this.file(id);
    if (!f.loaded) {
      const doc = await this.#call('openDocument', { projectId: this.project.projectId, fileId: id });
      f.text = f.saved = doc.text; f.loaded = true; f.conflict = false;
    }
    if (!this.openIds.includes(id)) this.openIds.push(id);
    this.activeId = id;
  }

  /** Discards local edits and loads the vault's current version (after a conflict). */
  async reload(id) {
    const f = this.file(id);
    const doc = await this.#call('openDocument', { projectId: this.project.projectId, fileId: id });
    f.text = f.saved = doc.text; f.loaded = true; f.conflict = false; f.mergedOnto = null;
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
    if (this.locked) throw new VaultError('Locked');
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
    const merged = f.mergedOnto;
    try {
      const result = merged
        ? await this.#call('saveMerged', { projectId: this.project.projectId, fileId: id, text: sent, revisionId: merged })
        : await this.#call('saveDocument', { projectId: this.project.projectId, fileId: id, text: sent });
      f.saved = sent; f.byteLength = result.byteLength; f.revision++; f.conflict = false; f.mergedOnto = null; this.savedCount++;
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
    return { ...merge3(sources.base, f.text, sources.theirs), theirsRevisionId: sources.theirsRevisionId };
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
    await this.#call('renameFile', { projectId: this.project.projectId, fileId: id, newName });
    f.path = newName; f.language = languageFor(newName);
  }

  /** Renames every file under `folder` ("src/app") to `newFolder`. Returns how many files moved. */
  async renameFolder(folder, newFolder) {
    const result = await this.#call('renameFolder', { projectId: this.project.projectId, name: folder, newName: newFolder });
    await this.#refreshFiles();
    return result.renamed;
  }

  /** Deletes a file from the vault. Refused with unsaved edits; Conflict if someone else changed it since it was opened. */
  async deleteFile(id) {
    const f = this.file(id);
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

  #forget(id) {
    this.files.delete(id);
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
    if (this.project) this.project = { ...this.project, fileCount: Math.max(0, this.project.fileCount - 1) };
  }

  async createFile(name, text = '') {
    const created = await this.#call('createFile', { projectId: this.project.projectId, name, text });
    this.files.set(created.fileId, { id: created.fileId, path: created.name, language: languageFor(created.name),
      byteLength: new TextEncoder().encode(text).length, loaded: true, text, saved: text, revision: 0, conflict: false, deleted: false });
    this.openIds.push(created.fileId); this.activeId = created.fileId;
    if (this.project) this.project = { ...this.project, fileCount: this.project.fileCount + 1 };
    return created.fileId;
  }

  dispose() { this.#bridge.dispose(); this.#listeners.clear(); }
}
