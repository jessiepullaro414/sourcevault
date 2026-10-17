import { MockVaultService, PROTOCOL_VERSION, newUuid, seedFiles } from './mock-service.mjs';

export { seedFiles };

// Preview session state. Draft text, tabs and dirty tracking are renderer-local;
// every open/save/close/lock/unlock goes through the in-memory protocol-v1 mock
// (mock-service.mjs). The mock's capability is held in a private field and is
// never exposed through metadata, the DOM or the host bridge.
const defaultOpen = ['pipeline', 'types'];
const keyByFileId = new Map(seedFiles.map(f => [f.fileId, f.key]));
const lostSession = new Set(['SessionExpired', 'Unauthorized']);

export class PreviewSession {
  #service; #capability = null; #projectId = null; #now;
  /** @param {{now?: () => number}} [options] now: clock for the mock's session expiry (tests). */
  constructor({ now } = {}) { this.#now = now; this.reset(); }

  #call(operation, body = {}) {
    const response = this.#service.handle({ version: PROTOCOL_VERSION, requestId: newUuid(), operation, body: this.#capability ? { capability: this.#capability, ...body } : body });
    if (response.ok) return response.body;
    if (lostSession.has(response.code)) { this.#capability = null; this.locked = true; }
    throw new Error(response.code);
  }
  #begin() { this.#capability = this.#call('BeginSession').capability; }
  #endQuietly() {
    if (!this.#service || !this.#capability) return;
    try { this.#call('EndSession'); } catch { /* best effort; the mock session is discarded either way */ }
    this.#capability = null;
  }

  reset() {
    this.#endQuietly();
    this.#service = new MockVaultService(this.#now ? { now: this.#now } : undefined);
    this.#begin();
    this.#projectId = this.#call('ListProjects').projects[0].projectId;
    this.files = new Map(); this.locked = false;
    for (const info of this.#call('ListFiles', { projectId: this.#projectId }).files) {
      const id = keyByFileId.get(info.fileId);
      const doc = this.#call('OpenDocument', { projectId: this.#projectId, fileId: info.fileId });
      this.files.set(id, { id, fileId: info.fileId, path: info.path, language: info.language, text: doc.text, saved: doc.text, revision: 0, revisionIds: [doc.revisionId], history: [] });
      if (!defaultOpen.includes(id)) this.#call('CloseDocument', { projectId: this.#projectId, fileId: info.fileId });
    }
    this.openIds = [...defaultOpen]; this.activeId = 'pipeline';
  }
  file(id) { const f = this.files.get(id); if (!f) throw new Error('UnknownFile'); return f; }
  requireUnlocked() { if (this.locked) throw new Error('SessionLocked'); }
  open(id) {
    this.requireUnlocked(); const f = this.file(id);
    if (!this.openIds.includes(id)) { this.#call('OpenDocument', { projectId: this.#projectId, fileId: f.fileId }); this.openIds.push(id); }
    this.activeId = id;
  }
  close(id) {
    this.requireUnlocked(); const f = this.file(id);
    if (f.text !== f.saved) throw new Error('UnsavedChanges');
    this.#call('CloseDocument', { projectId: this.#projectId, fileId: f.fileId });
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
  }
  edit(id, text) { this.requireUnlocked(); if (typeof text !== 'string') throw new TypeError('TextRequired'); this.file(id).text = text; }
  dirty(id) { const f = this.file(id); return f.text !== f.saved; }
  /** `expectedRevision` is the revision ordinal the caller last saw; the mock decides conflicts by immutable revision ID. */
  save(id, expectedRevision) {
    this.requireUnlocked(); const f = this.file(id);
    const expectedId = f.revisionIds[expectedRevision];
    if (expectedId === undefined) throw new Error('RevisionConflict');
    if (expectedRevision === f.revision && !this.dirty(id)) return f.revision;
    const result = this.#call('ApplyEdits', { projectId: this.#projectId, fileId: f.fileId, expectedRevision: expectedId, text: f.text });
    f.saved = f.text; f.revision++; f.revisionIds.push(result.revisionId);
    f.history.push(Object.freeze({ revision: f.revision, text: f.text }));
    return f.revision;
  }
  /** Activity ping so an actively edited (but unsaved) session is not idle-expired by the mock. */
  keepAlive() { if (!this.locked) this.#call('ListProjects'); }
  lock() {
    if (this.locked) return;
    this.#endQuietly(); this.locked = true;
  }
  unlock() {
    if (!this.locked) return;
    this.#begin();
    try {
      for (const id of this.openIds) this.#call('OpenDocument', { projectId: this.#projectId, fileId: this.file(id).fileId });
    } catch (e) { this.#endQuietly(); this.locked = true; throw e; }
    this.locked = false;
  }
  get metadata() { return { locked: this.locked, dirtyCount: [...this.files.keys()].filter(id => this.dirty(id)).length, revisionCount: [...this.files.values()].reduce((n, f) => n + f.revision, 0) }; }
}
