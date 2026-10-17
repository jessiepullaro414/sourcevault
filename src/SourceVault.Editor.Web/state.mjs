export const seedFiles = Object.freeze([
  { id: 'pipeline', path: 'src/pipeline.ts', language: 'typescript', text: `import { Session, Revision } from './types';\nimport { createSnapshot } from './snapshot';\n\n/** A synthetic example. No vault connection is made. */\nexport class RevisionPipeline {\n  private revisions: Revision[] = [];\n\n  constructor(private readonly session: Session) {}\n\n  async checkpoint(source: string): Promise<Revision> {\n    const snapshot = createSnapshot(source);\n\n    const revision: Revision = {\n      id: this.revisions.length + 1,\n      label: 'Session checkpoint',\n      snapshot,\n      createdAt: new Date().toISOString(),\n    };\n\n    this.revisions.push(revision);\n    return revision;\n  }\n\n  get history(): readonly Revision[] {\n    return this.revisions;\n  }\n}\n` },
  { id: 'snapshot', path: 'src/snapshot.ts', language: 'typescript', text: `/** Session-only synthetic snapshot. */\nexport function createSnapshot(source: string): string {\n  return source.replace(/\\r\\n/g, '\\n');\n}\n` },
  { id: 'types', path: 'src/types.ts', language: 'typescript', text: `export interface Session {\n  readonly id: string;\n  readonly name: string;\n}\n\nexport interface Revision {\n  readonly id: number;\n  readonly label: string;\n  readonly snapshot: string;\n  readonly createdAt: string;\n}\n` },
  { id: 'readme', path: 'README.md', language: 'markdown', text: `# SourceVault · synthetic workspace\n\nThis editor preview uses fictional source loaded from bundled assets.\n\n- Edit files and switch between tabs.\n- Ctrl+S creates an in-memory revision for this session.\n- Ctrl+F opens Monaco's find widget.\n- Lock hides the editor; Unlock resumes the same session.\n- Reset restores this synthetic workspace.\n\nRefreshing or closing the application discards all edits and revisions.\nNo vault, key service, TPM, or build broker is connected.\n` }
]);

export class PreviewSession {
  constructor() { this.reset(); }
  reset() {
    this.files = new Map(seedFiles.map(f => [f.id, { ...f, saved: f.text, revision: 0, history: [] }]));
    this.openIds = ['pipeline', 'types']; this.activeId = 'pipeline'; this.locked = false;
  }
  file(id) { const f = this.files.get(id); if (!f) throw new Error('UnknownFile'); return f; }
  requireUnlocked() { if (this.locked) throw new Error('SessionLocked'); }
  open(id) { this.requireUnlocked(); this.file(id); if (!this.openIds.includes(id)) this.openIds.push(id); this.activeId = id; }
  close(id) {
    this.requireUnlocked(); const f = this.file(id);
    if (f.text !== f.saved) throw new Error('UnsavedChanges');
    this.openIds = this.openIds.filter(x => x !== id);
    if (this.activeId === id) this.activeId = this.openIds.at(-1) ?? null;
  }
  edit(id, text) { this.requireUnlocked(); if (typeof text !== 'string') throw new TypeError('TextRequired'); this.file(id).text = text; }
  dirty(id) { const f = this.file(id); return f.text !== f.saved; }
  save(id, expectedRevision) {
    this.requireUnlocked(); const f = this.file(id);
    if (expectedRevision !== f.revision) throw new Error('RevisionConflict');
    if (!this.dirty(id)) return f.revision;
    f.saved = f.text; f.revision++; f.history.push(Object.freeze({ revision: f.revision, text: f.text })); return f.revision;
  }
  lock() { this.locked = true; }
  unlock() { this.locked = false; }
  get metadata() { return { locked: this.locked, dirtyCount: [...this.files.keys()].filter(id => this.dirty(id)).length, revisionCount: [...this.files.values()].reduce((n, f) => n + f.revision, 0) }; }
}
