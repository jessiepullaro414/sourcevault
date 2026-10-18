// DEVELOPMENT ONLY: an in-page stand-in for the native editor host in vault mode, for browser review
// (index.html?fake-vault) and node tests. Not shipped in the native editor's assets.
//
// It follows the real host's bridge v2 rules (BridgePolicy): requests that are malformed in any way get no response,
// just as the real host ignores them. Vault semantics mirror EditorVaultController: per-file conflicts, Locked while
// locked, unlock re-check of opened files. All content is fictional.

const REQUEST_TYPE = 'sourcevault.vault.request';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const ARGS = {
  listProjects: [], createProject: ['name'], deleteProject: ['projectId', 'name'], listFiles: ['projectId'], openDocument: ['projectId', 'fileId'],
  closeDocument: ['projectId', 'fileId'], saveDocument: ['projectId', 'fileId', 'text'], createFile: ['projectId', 'name', 'text'],
  lock: [], unlock: [],
};
const MAX_TEXT_BYTES = 256 * 1024;
const encoder = new TextEncoder();
const bytes = text => encoder.encode(text).length;
const isId = v => typeof v === 'string' && UUID.test(v) && v !== '00000000-0000-0000-0000-000000000000';
const isName = v => typeof v === 'string' && v.length > 0 && v.length <= 260 && ![...v].some(c => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127) && v.isWellFormed();
const isText = v => typeof v === 'string' && v.isWellFormed() && bytes(v) <= MAX_TEXT_BYTES;
// Vault paths: '/'-separated segments, each a valid Windows file name (as RevisionStore checks).
const validSegment = s => s.length > 0 && s.length <= 128 && !/[\\/:<>"|?*]/.test(s) && s !== '.' && s !== '..' && !/[ .]$/.test(s);
const validFileName = name => name.split('/').length <= 32 && name.split('/').every(validSegment);

const SEED = [
  { name: 'Synthetic demo 1', files: [
    ['README.md', '# Synthetic demo project\n\nFictional content created by the SourceVault lab service. Not real source.\n'],
    ['main.ts', "import { greet } from './util';\n\nexport function main(): string {\n  return greet('synthetic');\n}\n"],
    ['util.ts', 'export function greet(name: string): string {\n  return `hello, ${name}`;\n}\n'],
  ] },
  { name: 'Lab client check', files: [['check.txt', 'SourceVault lab client check\nnonce: fictional\n']] },
];

export class FakeVaultHost {
  locked = true;
  failNextUnlock = null;   // e.g. 'ServiceNotRunning' to rehearse unlock errors
  latencyMs;
  #projects = new Map();   // projectId -> { name, files: Map(fileId -> { name, text, rev }) }
  #opened = new Map();     // `${projectId}/${fileId}` -> rev the editor's copy is based on
  #listeners = new Set();
  requests = [];           // operations received (names only), for tests

  constructor({ latencyMs = 0, seed = SEED } = {}) {
    this.latencyMs = latencyMs;
    for (const project of seed) {
      const files = new Map(project.files.map(([name, text]) => [crypto.randomUUID(), { name, text, rev: 1 }]));
      this.#projects.set(crypto.randomUUID(), { name: project.name, files });
    }
  }

  /** The transport VaultSession expects: { send, subscribe }. */
  get transport() {
    return {
      send: message => setTimeout(() => this.#handle(message), this.latencyMs),
      subscribe: handler => { this.#listeners.add(handler); return () => this.#listeners.delete(handler); },
    };
  }
  #emit(message) { for (const handler of this.#listeners) handler(structuredClone(message)); }

  /** Someone else edits a file (another editor or the lab client). Defaults to the first opened file. */
  externalEdit(projectId, fileId, text = '// changed by someone else\n') {
    const key = projectId && fileId ? `${projectId}/${fileId}` : this.#opened.keys().next().value;
    if (!key) return false;
    const [p, f] = key.split('/');
    const file = this.#projects.get(p)?.files.get(f);
    if (!file) return false;
    file.text = text; file.rev++;
    return true;
  }
  /** Host-side lock: Windows lock (`windows`), session expiry (`expired`) or a dropped connection (`closed`). */
  simulateLock(reason = 'windows') { this.locked = true; this.#emit({ type: 'sourcevault.vault.locked', reason }); }
  projectIdByName(name) { for (const [id, p] of this.#projects) if (p.name === name) return id; return null; }
  textOf(projectId, fileId) { return this.#projects.get(projectId)?.files.get(fileId)?.text; }

  #handle(message) {
    // Exactly the real host's envelope: anything else is silently ignored.
    if (!message || typeof message !== 'object' || Object.keys(message).sort().join() !== 'args,operation,requestId,type') return;
    const { type, requestId, operation, args } = message;
    if (type !== REQUEST_TYPE || !isId(requestId) || !Object.hasOwn(ARGS, operation)) return;
    if (!args || typeof args !== 'object' || Array.isArray(args) || Object.keys(args).sort().join() !== [...ARGS[operation]].sort().join()) return;
    if (('projectId' in args && !isId(args.projectId)) || ('fileId' in args && !isId(args.fileId))) return;
    if (('name' in args && !isName(args.name)) || ('text' in args && !isText(args.text))) return;
    this.requests.push(operation);
    const reply = (code, result = null) => this.#emit({ type: 'sourcevault.vault.response', requestId, code, result });
    if (operation === 'unlock') return reply(...this.#unlock());
    if (operation === 'lock') { const was = this.locked; this.locked = true; reply('Ok', {}); if (!was) this.#emit({ type: 'sourcevault.vault.locked', reason: 'user' }); return; }
    if (this.locked) return reply('Locked');
    const project = 'projectId' in args ? this.#projects.get(args.projectId) : null;
    if ('projectId' in args && !project) return reply('NotFound');
    const key = `${args.projectId}/${args.fileId}`;
    switch (operation) {
      case 'listProjects':
        return reply('Ok', { projects: [...this.#projects].map(([projectId, p]) => ({ projectId, name: p.name, fileCount: p.files.size })) });
      case 'createProject': {
        if ([...this.#projects.values()].some(p => p.name.toLowerCase() === args.name.toLowerCase())) return reply('AlreadyExists');
        const projectId = crypto.randomUUID();
        this.#projects.set(projectId, { name: args.name, files: new Map() });
        return reply('Ok', { projectId, name: args.name });
      }
      case 'deleteProject': {
        if (args.name !== project.name) return reply('InvalidRequest'); // exact, case-sensitive confirmation
        this.#projects.delete(args.projectId);
        for (const k of [...this.#opened.keys()]) if (k.startsWith(`${args.projectId}/`)) this.#opened.delete(k);
        return reply('Ok', { filesRemoved: true });
      }
      case 'listFiles':
        return reply('Ok', { files: [...project.files].map(([fileId, f]) => ({ fileId, name: f.name, byteLength: bytes(f.text) })) });
      case 'openDocument': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        this.#opened.set(key, file.rev);
        return reply('Ok', { fileId: args.fileId, name: file.name, text: file.text });
      }
      case 'closeDocument': this.#opened.delete(key); return reply('Ok', {});
      case 'saveDocument': {
        if (!this.#opened.has(key)) return reply('NotOpen');
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (file.rev !== this.#opened.get(key)) return reply('Conflict');
        file.text = args.text; file.rev++; this.#opened.set(key, file.rev);
        return reply('Ok', { byteLength: bytes(args.text) });
      }
      case 'createFile': {
        if (!validFileName(args.name)) return reply('InvalidRequest');
        if ([...project.files.values()].some(f => f.name.toLowerCase() === args.name.toLowerCase())) return reply('AlreadyExists');
        const fileId = crypto.randomUUID();
        project.files.set(fileId, { name: args.name, text: args.text, rev: 1 });
        this.#opened.set(`${args.projectId}/${fileId}`, 1);
        return reply('Ok', { fileId, name: args.name });
      }
    }
  }

  #unlock() {
    if (this.failNextUnlock) { const code = this.failNextUnlock; this.failNextUnlock = null; return [code, null]; }
    this.locked = false;
    const changed = [];
    for (const [key, rev] of this.#opened) {
      const [projectId, fileId] = key.split('/');
      if (this.#projects.get(projectId)?.files.get(fileId)?.rev !== rev) changed.push({ projectId, fileId });
    }
    return ['Ok', { changed }];
  }
}

/** A small floating panel for browser review: simulate the events a real host and service produce. */
export function installFakeHostPanel(fake, getActive) {
  const panel = document.createElement('div');
  panel.className = 'fake-host-panel';
  panel.innerHTML = '<strong>Fake host · browser review</strong><span>No service, no encryption. Fictional data.</span>';
  const actions = [
    ['Someone else edits this file', () => { const a = getActive(); return fake.externalEdit(a?.projectId, a?.fileId) ? 'Edited elsewhere; your next save will conflict.' : 'Open a file first.'; }],
    ['Windows lock', () => { fake.simulateLock('windows'); return 'Simulated Windows lock.'; }],
    ['Session expiry', () => { fake.simulateLock('expired'); return 'Simulated 30-minute expiry.'; }],
    ['Next unlock: service not running', () => { fake.failNextUnlock = 'ServiceNotRunning'; return 'The next unlock will fail.'; }],
  ];
  const note = document.createElement('em');
  for (const [label, act] of actions) {
    const button = document.createElement('button');
    button.textContent = label;
    button.onclick = () => { note.textContent = act(); };
    panel.append(button);
  }
  panel.append(note);
  document.body.append(panel);
}
