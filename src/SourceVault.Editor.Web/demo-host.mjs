// The demo vault: an in-page stand-in for the native editor host in vault mode. It runs the editor's demo mode
// (`SourceVault.Editor.exe --demo`, owner request 2026-10-19), browser review (index.html?demo) and the node tests.
// NOTHING HERE IS ENCRYPTED OR STORED: projects live in this page's memory and are gone when it closes. The vault
// editor never loads this module; the host decides the mode, and demo mode never connects to the service.
//
// It follows the real host's bridge v2 rules (BridgePolicy): requests that are malformed in any way get no response,
// just as the real host ignores them. Vault semantics mirror EditorVaultController: per-file conflicts, Locked while
// locked, unlock re-check of opened files. All content is fictional.

import { searchText, MAX_SEARCH_MATCHES, DEFAULT_SETTINGS, validSettings } from './workbench.mjs';

const REQUEST_TYPE = 'sourcevault.vault.request';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const ARGS = {
  listProjects: [], createProject: ['name'], deleteProject: ['projectId', 'name'], renameProject: ['projectId', 'newName'],
  renameFile: ['projectId', 'fileId', 'newName'], renameFolder: ['projectId', 'name', 'newName'], deleteFile: ['projectId', 'fileId'],
  listHistory: ['projectId', 'fileId', 'page'], openRevision: ['projectId', 'fileId', 'revisionId'], listDeleted: ['projectId'],
  restoreFile: ['projectId', 'fileId', 'newName'], mergeSources: ['projectId', 'fileId'], saveMerged: ['projectId', 'fileId', 'text', 'revisionId'],
  listBuildTargets: [], requestBuild: ['projectId', 'name'], listBuilds: ['projectId'], buildStatus: ['projectId', 'buildNumber'], openBuildLog: ['projectId', 'buildNumber'], listFiles: ['projectId'], openDocument: ['projectId', 'fileId'],
  closeDocument: ['projectId', 'fileId'], saveDocument: ['projectId', 'fileId', 'text'], createFile: ['projectId', 'name', 'text'],
  searchProject: ['projectId', 'query', 'matchCase', 'wholeWord'], getSettings: [], saveSettings: ['settings'],
  lock: [], unlock: [],
  fileInfo: ['projectId', 'fileId'], replaceFromDisk: ['projectId', 'fileId'], keepMine: ['projectId', 'fileId'], keepTheirs: ['projectId', 'fileId'],
  addFromDisk: ['projectId', 'folder'], revertFile: ['projectId', 'fileId', 'revisionId'],
};
const isQuery = q => typeof q === 'string' && q.length > 0 && q.length <= 200 && ![...q].some(c => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127);
const MAX_TEXT_BYTES = 1024 * 1024; // the editor's text limit
const encoder = new TextEncoder();
const bytes = text => encoder.encode(text).length;
const hex = buffer => [...new Uint8Array(buffer)].map(b => b.toString(16).padStart(2, '0')).join('');
const openable = r => r.kind === 'text' && r.length <= MAX_TEXT_BYTES;
// Made-up binary content for the demo's "from disk" actions: nothing is ever read from this computer.
const madeUpBinary = (seed, length) => Uint8Array.from({ length }, (_, i) => (i < 4 ? [0, 0, 1, 0][i] : (i * 31 + seed * 17) & 255));
const largeLog = () => Array.from({ length: 22000 }, (_, i) => `[${String(i).padStart(5, '0')}] fictional build step ${i % 97}: compiled synthetic unit ${i}\n`).join('');
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
  { name: 'Synthetic C++ demo', files: [
    ['include/gadget.h', '#pragma once\n#include <string>\n\nnamespace demo {\n\n/// A fictional gadget.\nclass Gadget {\npublic:\n    explicit Gadget(int size);\n    int size() const;\n    std::string label() const;\n\nprivate:\n    int size_ = 0;\n};\n\n}  // namespace demo\n'],
    ['src/gadget.cpp', '#include "gadget.h"\n\nnamespace demo {\n\nGadget::Gadget(int size) : size_(size) {}\n\nint Gadget::size() const {\n    return size_;\n}\n\nstd::string Gadget::label() const {\n    return "gadget-" + std::to_string(size_);\n}\n\n}  // namespace demo\n'],
    ['src/main.cpp', '#include <iostream>\n#include "gadget.h"\n\nint main() {\n    demo::Gadget gadget(3);\n    std::cout << gadget.label() << "\\n";\n    return 0;\n}\n'],
    ['CMakeLists.txt', 'cmake_minimum_required(VERSION 3.20)\nproject(synthetic_gadget CXX)\nadd_executable(gadget src/main.cpp src/gadget.cpp)\ntarget_include_directories(gadget PRIVATE include)\n'],
    // Files the editor doesn't show: a binary icon and a text log over 1 MiB (both made up).
    ['res/gadget.ico', () => madeUpBinary(1, 2238)],
    ['logs/build-history.log', largeLog],
  ] },
  { name: 'Synthetic C# demo', files: [
    ['Widgets.csproj', '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n'],
    ['src/Widget.cs', 'namespace Demo.Widgets;\n\n/// <summary>A fictional widget.</summary>\npublic sealed class Widget\n{\n    private readonly List<string> tags = new();\n\n    public Widget(string name) { Name = name; }\n\n    public string Name { get; }\n    public int TagCount => tags.Count;\n\n    public void Tag(string tag)\n    {\n        if (!tags.Contains(tag)) tags.Add(tag);\n    }\n\n    public override string ToString() => $"{Name} ({TagCount} tags)";\n}\n'],
    ['src/Program.cs', 'using Demo.Widgets;\n\nvar widget = new Widget("sample");\nwidget.Tag("fictional");\nConsole.WriteLine(widget);\n'],
  ] },
];

export class DemoVaultHost {
  locked = true;
  failNextUnlock = null;   // e.g. 'ServiceNotRunning' to rehearse unlock errors
  latencyMs;
  #projects = new Map();   // projectId -> { name, files: Map(fileId -> file), deleted: Map(fileId -> { file, deletedAt, deletedBy }) }
                           // file: { name, text, kind, length, sha256, rev, history: [{ revisionId, rev, text, kind, length, sha256, savedAt, savedBy, action, name }] }
                           // text is null for binary content; sha256 is a promise of the hex digest
  #pending = new Map();    // `${projectId}/${fileId}` -> made-up content staged by a replacement that conflicted
  #madeUp = 0;
  actor = 'DEMO\\operator';
  #opened = new Map();     // `${projectId}/${fileId}` -> rev the editor's copy is based on
  #listeners = new Set();
  requests = [];           // operations received (names only), for tests
  buildTargets = ['linux', 'windows']; // [] rehearses 'builds not set up'
  buildMs = 1500;          // how long a demo build runs
  buildFails = false;
  #builds = new Map();     // projectId -> [build], newest last
  settings = { ...DEFAULT_SETTINGS }; // the host's editor-settings.json

  constructor({ latencyMs = 0, seed = SEED } = {}) {
    this.latencyMs = latencyMs;
    for (const project of seed) {
      const files = new Map(project.files.map(([name, text]) => [crypto.randomUUID(), this.#newFile(name, typeof text === 'function' ? text() : text, 'created')]));
      this.#projects.set(crypto.randomUUID(), { name: project.name, files, deleted: new Map() });
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
    this.#save(file, text, 'edited', 'DEMO\\someone-else');
    return true;
  }
  #newFile(name, content, action) { const file = { name, text: null, rev: 0, history: [] }; this.#save(file, content, action); return file; }
  // content: a string (text) or a Uint8Array (binary, from the made-up "disk").
  #save(file, content, action, by = this.actor) {
    const text = typeof content === 'string' ? content : null;
    const data = text === null ? content : encoder.encode(text);
    const version = { kind: text === null ? 'binary' : 'text', length: data.length, sha256: crypto.subtle.digest('SHA-256', data).then(hex) };
    Object.assign(file, { text }, version); file.rev++;
    file.history.push({ revisionId: crypto.randomUUID(), rev: file.rev, text, ...version, savedAt: new Date().toISOString(), savedBy: by, action, name: file.name });
  }
  async #listed(fileId, f) { return { fileId, name: f.name, byteLength: f.length, kind: f.kind, sha256: await f.sha256 }; }
  #madeUpFile() { this.#madeUp++; return madeUpBinary(this.#madeUp, 1024 * (3 + (this.#madeUp * 7) % 40)); }
  /** Someone else replaces a file with made-up binary content. */
  externalReplace(projectId, fileId) {
    const key = projectId && fileId ? `${projectId}/${fileId}` : this.#opened.keys().next().value;
    if (!key) return false;
    const [p, f] = key.split('/');
    const file = this.#projects.get(p)?.files.get(f);
    if (!file) return false;
    this.#save(file, this.#madeUpFile(), 'edited', 'DEMO\\someone-else');
    return true;
  }
  /** Host-side lock: Windows lock (`windows`), session expiry (`expired`) or a dropped connection (`closed`). */
  simulateLock(reason = 'windows') { this.locked = true; this.#emit({ type: 'sourcevault.vault.locked', reason }); }
  projectIdByName(name) { for (const [id, p] of this.#projects) if (p.name === name) return id; return null; }
  textOf(projectId, fileId) { return this.#projects.get(projectId)?.files.get(fileId)?.text; }

  async #handle(message) {
    // Exactly the real host's envelope: anything else is silently ignored.
    if (!message || typeof message !== 'object' || Object.keys(message).sort().join() !== 'args,operation,requestId,type') return;
    const { type, requestId, operation, args } = message;
    if (type !== REQUEST_TYPE || !isId(requestId) || !Object.hasOwn(ARGS, operation)) return;
    if (!args || typeof args !== 'object' || Array.isArray(args) || Object.keys(args).sort().join() !== [...ARGS[operation]].sort().join()) return;
    if (('projectId' in args && !isId(args.projectId)) || ('fileId' in args && !isId(args.fileId))) return;
    if (('name' in args && !isName(args.name)) || ('text' in args && !isText(args.text))) return;
    this.requests.push(operation);
    const reply = (code, result = null) => this.#emit({ type: 'sourcevault.vault.response', requestId, code, result });
    if (operation === 'searchProject' && (!isQuery(args.query) || typeof args.matchCase !== 'boolean' || typeof args.wholeWord !== 'boolean')) return;
    if (operation === 'saveSettings' && !validSettings(args.settings)) return;
    // Display preferences need no vault session.
    if (operation === 'getSettings') return reply('Ok', { settings: { ...this.settings } });
    if (operation === 'saveSettings') { this.settings = { ...args.settings }; return reply('Ok', {}); }
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
        this.#projects.set(projectId, { name: args.name, files: new Map(), deleted: new Map() });
        return reply('Ok', { projectId, name: args.name });
      }
      case 'deleteProject': {
        if (args.name !== project.name) return reply('InvalidRequest'); // exact, case-sensitive confirmation
        this.#projects.delete(args.projectId);
        for (const k of [...this.#opened.keys()]) if (k.startsWith(`${args.projectId}/`)) this.#opened.delete(k);
        return reply('Ok', { filesRemoved: true });
      }
      case 'renameProject': {
        if (args.newName.length > 64 || args.newName.trim() !== args.newName) return reply('InvalidRequest');
        if ([...this.#projects].some(([id, p]) => id !== args.projectId && p.name.toLowerCase() === args.newName.toLowerCase())) return reply('AlreadyExists');
        project.name = args.newName;
        return reply('Ok', {});
      }
      case 'renameFile': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (!validFileName(args.newName)) return reply('InvalidRequest');
        if (!this.#treeOk(project, new Map([[args.fileId, args.newName]]))) return reply('AlreadyExists');
        file.name = args.newName;
        return reply('Ok', {});
      }
      case 'renameFolder': {
        if (!validFileName(args.name) || !validFileName(args.newName)) return reply('InvalidRequest');
        const prefix = args.name.toLowerCase() + '/';
        const moved = new Map([...project.files].filter(([, f]) => f.name.toLowerCase().startsWith(prefix)).map(([id, f]) => [id, args.newName + f.name.slice(args.name.length)]));
        if (!moved.size) return reply('NotFound');
        if ([...moved.values()].some(n => n.length > 260 || !validFileName(n))) return reply('InvalidRequest');
        if (!this.#treeOk(project, moved)) return reply('AlreadyExists');
        for (const [id, name] of moved) project.files.get(id).name = name;
        return reply('Ok', { renamed: moved.size });
      }
      case 'deleteFile': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (this.#opened.has(key) && this.#opened.get(key) !== file.rev) return reply('Conflict');
        project.files.delete(args.fileId); this.#opened.delete(key);
        project.deleted.set(args.fileId, { file, deletedAt: new Date().toISOString(), deletedBy: this.actor });
        return reply('Ok', {});
      }
      case 'listHistory': {
        if (!Number.isInteger(args.page) || args.page < 0) return;
        const file = project.files.get(args.fileId) ?? project.deleted.get(args.fileId)?.file;
        if (!file) return reply('NotFound');
        const revisions = args.page > 0 ? [] : await Promise.all([...file.history].reverse().map(async ({ revisionId, savedAt, savedBy, length, kind, sha256, action, name }) =>
          ({ revisionId, savedAt, savedBy, byteLength: length, action, name, kind, sha256: await sha256 })));
        return reply('Ok', { revisions, more: false });
      }
      case 'openRevision': {
        if (!isId(args.revisionId)) return;
        const file = project.files.get(args.fileId) ?? project.deleted.get(args.fileId)?.file;
        const revision = file?.history.find(r => r.revisionId === args.revisionId);
        if (!revision) return reply('NotFound');
        return revision.kind !== 'text' ? reply('NotText') : revision.length > MAX_TEXT_BYTES ? reply('LimitExceeded') : reply('Ok', { text: revision.text });
      }
      case 'listDeleted':
        return reply('Ok', { files: [...project.deleted].reverse().map(([fileId, d]) => ({ fileId, name: d.file.name, byteLength: d.file.length, deletedAt: d.deletedAt, deletedBy: d.deletedBy, kind: d.file.kind })) });
      case 'restoreFile': {
        const entry = project.deleted.get(args.fileId);
        if (!entry) return reply('NotFound');
        if (!validFileName(args.newName)) return reply('InvalidRequest');
        project.files.set(args.fileId, entry.file);
        if (!this.#treeOk(project, new Map([[args.fileId, args.newName]]))) { project.files.delete(args.fileId); return reply('AlreadyExists'); }
        project.deleted.delete(args.fileId);
        entry.file.name = args.newName;
        entry.file.history.push({ ...entry.file.history.at(-1), savedAt: new Date().toISOString(), savedBy: this.actor, action: 'restored', name: args.newName });
        return reply('Ok', {});
      }
      case 'revertFile': {
        if (!isId(args.revisionId)) return;
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (this.#opened.has(key) && this.#opened.get(key) !== file.rev) return reply('Conflict');
        const old = file.history.find(r => r.revisionId === args.revisionId);
        if (!old) return reply('NotFound');
        // Like the service: the file points at that version again; nothing is re-encrypted.
        file.rev++;
        Object.assign(file, { text: old.text, kind: old.kind, length: old.length, sha256: old.sha256 });
        file.history.push({ ...old, rev: file.rev, savedAt: new Date().toISOString(), savedBy: this.actor, action: 'reverted', name: file.name });
        if (this.#opened.has(key)) this.#opened.set(key, file.rev);
        return reply('Ok', {});
      }
      case 'fileInfo': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        this.#opened.set(key, file.rev);
        const last = file.history.at(-1);
        return reply('Ok', { ...(await this.#listed(args.fileId, file)), savedAt: last.savedAt, savedBy: last.savedBy });
      }
      case 'replaceFromDisk': {
        // The real host shows its Open dialog and reads the chosen file; the demo makes one up.
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        this.#pending.delete(key);
        const content = this.#madeUpFile();
        if (this.#opened.has(key) && this.#opened.get(key) !== file.rev) { this.#pending.set(key, content); return reply('Conflict'); }
        this.#save(file, content, 'edited');
        if (this.#opened.has(key)) this.#opened.set(key, file.rev);
        return reply('Ok', await this.#listed(args.fileId, file));
      }
      case 'keepMine':
      case 'keepTheirs': {
        const file = project.files.get(args.fileId);
        const content = this.#pending.get(key);
        this.#pending.delete(key);
        if (!file) return reply('NotFound');
        if (operation === 'keepMine') {
          if (!content) return reply('UploadExpired');
          this.#save(file, content, 'edited');
        }
        this.#opened.set(key, file.rev);
        return reply('Ok', await this.#listed(args.fileId, file));
      }
      case 'addFromDisk': {
        const folder = args.folder;
        if (typeof folder !== 'string' || (folder && (!isName(folder) || folder.startsWith('/') || folder.endsWith('/')))) return;
        const count = 1 + (this.#madeUp % 2);
        const names = Array.from({ length: count }, (_, i) => `${folder ? folder + '/' : ''}demo-asset-${this.#madeUp + 1}-${i + 1}.bin`);
        if (names.some(n => !validFileName(n))) return reply('InvalidRequest');
        if (names.some(n => [...project.files.values()].some(f => f.name.toLowerCase() === n.toLowerCase()))) return reply('AlreadyExists');
        const added = [];
        for (const name of names) {
          const fileId = crypto.randomUUID();
          project.files.set(fileId, this.#newFile(name, this.#madeUpFile(), 'created'));
          added.push(await this.#listed(fileId, project.files.get(fileId)));
        }
        return reply('Ok', { files: added });
      }
      case 'mergeSources': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (!this.#opened.has(key)) return reply('NotOpen');
        const base = file.history.find(r => r.rev === this.#opened.get(key));
        const theirsRevisionId = file.history.at(-1).revisionId;
        if (!openable(file) || (base && !openable(base))) return reply('Ok', { mergeable: false, theirsRevisionId });
        return reply('Ok', { mergeable: true, base: base?.text ?? '', theirs: file.text, theirsRevisionId });
      }
      case 'saveMerged': {
        if (!isId(args.revisionId)) return;
        if (!this.#opened.has(key)) return reply('NotOpen');
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (file.history.at(-1).revisionId !== args.revisionId) return reply('Conflict');
        this.#save(file, args.text, 'edited'); this.#opened.set(key, file.rev);
        return reply('Ok', { byteLength: bytes(args.text) });
      }
      case 'listBuildTargets':
        return this.buildTargets.length ? reply('Ok', { targets: this.buildTargets }) : reply('Unavailable');
      case 'requestBuild': {
        if (!this.buildTargets.includes(args.name)) return reply('NotFound');
        const list = this.#builds.get(args.projectId) ?? []; this.#builds.set(args.projectId, list);
        const build = { buildNumber: list.length + 1, target: args.name, state: 'running', reason: null, exitCode: null, requestedBy: this.actor,
          requestedAt: new Date().toISOString(), finishedAt: null, artifactFolder: null, artifactFiles: 0, artifactBytes: 0, artifactsDropped: 0, logAvailable: false, logTruncated: false, log: null };
        list.push(build);
        const files = [...project.files.values()];
        // Like the build service: source maps and debug symbols are left out and listed at the end of the log.
        const dropped = files.filter(f => /\.(map|pdb)$/i.test(f.name)), kept = files.filter(f => !dropped.includes(f));
        const notice = dropped.length ? `\n[SourceVault] ${dropped.length} output file(s) were not published because they can reveal source:\n${dropped.map(f => `  ${f.name}  (${/\.map$/i.test(f.name) ? 'SourceMap' : 'DebugSymbols'})\n`).join('')}` : '';
        const total = kept.reduce((n, f) => n + f.length, 0);
        setTimeout(() => Object.assign(build, this.buildFails
          ? { state: 'failed', exitCode: 1, finishedAt: new Date().toISOString(), logAvailable: true, log: `$ ./build.sh\nerror: fictional failure in ${files[0]?.name ?? 'nothing'}\n` }
          : { state: 'succeeded', exitCode: 0, finishedAt: new Date().toISOString(), logAvailable: true,
              artifactFolder: ['D:', 'SourceVault', 'artifacts', args.projectId, `${build.buildNumber}-${args.name}`].join('\\'),
              artifactFiles: kept.length, artifactBytes: total, artifactsDropped: dropped.length,
              log: `$ ./build.sh\ncompiling ${files.length} fictional file(s)\ndone\n${notice}` }), this.buildMs);
        return reply('Ok', { buildNumber: build.buildNumber });
      }
      case 'listBuilds':
        return reply('Ok', { builds: [...(this.#builds.get(args.projectId) ?? [])].reverse().map(({ log, ...b }) => b) });
      case 'buildStatus':
      case 'openBuildLog': {
        if (!Number.isInteger(args.buildNumber) || args.buildNumber < 1) return;
        const build = (this.#builds.get(args.projectId) ?? []).find(b => b.buildNumber === args.buildNumber);
        if (!build) return reply('NotFound');
        if (operation === 'buildStatus') { const { log, ...b } = build; return reply('Ok', b); }
        return build.logAvailable ? reply('Ok', { text: build.log }) : reply('NotFound');
      }
      case 'searchProject': {
        // Like the service: saved text only (binary files are skipped and counted), the first 500 matches, then
        // sorted by name, line and column.
        const matches = [];
        let truncated = false, skipped = 0;
        for (const [fileId, f] of project.files) {
          if (f.kind !== 'text') { skipped++; continue; }
          const found = searchText(f.text, args.query, args, MAX_SEARCH_MATCHES - matches.length + 1);
          if (matches.length + found.length > MAX_SEARCH_MATCHES) { truncated = true; found.length = MAX_SEARCH_MATCHES - matches.length; }
          matches.push(...found.map(m => ({ fileId, name: f.name, ...m })));
          if (truncated) break;
        }
        matches.sort((a, b) => (a.name.toUpperCase() < b.name.toUpperCase() ? -1 : a.name.toUpperCase() > b.name.toUpperCase() ? 1 : 0) || a.line - b.line || a.column - b.column);
        return reply('Ok', { matches, truncated, filesSearched: project.files.size - skipped, filesSkipped: skipped });
      }
      case 'listFiles':
        return reply('Ok', { files: await Promise.all([...project.files].map(([fileId, f]) => this.#listed(fileId, f))) });
      case 'openDocument': {
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (file.kind !== 'text') return reply('NotText');
        if (file.length > MAX_TEXT_BYTES) return reply('LimitExceeded');
        this.#opened.set(key, file.rev);
        return reply('Ok', { fileId: args.fileId, name: file.name, text: file.text });
      }
      case 'closeDocument': this.#opened.delete(key); return reply('Ok', {});
      case 'saveDocument': {
        if (!this.#opened.has(key)) return reply('NotOpen');
        const file = project.files.get(args.fileId);
        if (!file) return reply('NotFound');
        if (file.rev !== this.#opened.get(key)) return reply('Conflict');
        this.#save(file, args.text, 'edited'); this.#opened.set(key, file.rev);
        return reply('Ok', { byteLength: bytes(args.text) });
      }
      case 'createFile': {
        if (!validFileName(args.name)) return reply('InvalidRequest');
        if ([...project.files.values()].some(f => f.name.toLowerCase() === args.name.toLowerCase())) return reply('AlreadyExists');
        const fileId = crypto.randomUUID();
        project.files.set(fileId, this.#newFile(args.name, args.text, 'created'));
        this.#opened.set(`${args.projectId}/${fileId}`, 1);
        return reply('Ok', { fileId, name: args.name });
      }
    }
  }

  // Names after the given renames: unique (case-insensitive) and no path that is both a file and a folder.
  #treeOk(project, renames) {
    const names = [...project.files].map(([id, f]) => (renames.get(id) ?? f.name).toLowerCase());
    const set = new Set(names);
    return set.size === names.length && !names.some(n => n.split('/').slice(0, -1).some((_, i, parts) => set.has(parts.slice(0, i + 1).join('/'))));
  }
  /** Someone else deletes a file (another editor). */
  externalDelete(projectId, fileId) {
    const project = this.#projects.get(projectId), file = project?.files.get(fileId);
    if (!file) return false;
    project.files.delete(fileId);
    project.deleted.set(fileId, { file, deletedAt: new Date().toISOString(), deletedBy: 'DEMO\\someone-else' });
    return true;
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

/**
 * The demo controls: a small floating panel that makes the events a real service and other people produce, so every
 * feature can be shown (conflicts and merge, a file deleted elsewhere, lock screens, a failed unlock, a failed build).
 */
export function installDemoPanel(demo, getActive) {
  const panel = document.createElement('section');
  panel.className = 'demo-panel';
  panel.setAttribute('aria-label', 'Demo controls');
  const head = document.createElement('button');
  head.type = 'button'; head.className = 'demo-panel-head'; head.setAttribute('aria-expanded', 'true');
  const title = document.createElement('strong'); title.textContent = 'Demo controls';
  const chevron = document.createElement('span'); chevron.textContent = '⌄';
  head.append(title, chevron);
  const body = document.createElement('div'); body.className = 'demo-panel-body';
  const intro = document.createElement('span'); intro.textContent = 'Simulate what other people and the service do. Nothing here is encrypted.';
  body.append(intro);
  const actions = [
    ['Someone else edits this file', () => { const a = getActive(); return demo.externalEdit(a?.projectId, a?.fileId) ? 'Edited elsewhere: the next save conflicts (then Merge).' : 'Open a file first.'; }],
    ['Someone else deletes this file', () => { const a = getActive(); return a && demo.externalDelete(a.projectId, a.fileId) ? 'Deleted elsewhere: edit it and lock/unlock to see the notice, or open Deleted files.' : 'Open a file first.'; }],
    ['Someone else replaces this file with a binary', () => { const a = getActive(); return demo.externalReplace(a?.projectId, a?.fileId) ? 'Replaced elsewhere with a made-up binary: the next save offers keep mine / keep theirs.' : 'Open a file first.'; }],
    ['Windows lock', () => { demo.simulateLock('windows'); return 'Simulated a Windows lock.'; }],
    ['Session expiry', () => { demo.simulateLock('expired'); return 'Simulated the 30-minute expiry.'; }],
    ['Next unlock fails', () => { demo.failNextUnlock = 'ServiceNotRunning'; return 'The next unlock reports the service as not running.'; }],
    ['Next build fails', () => { demo.buildFails = !demo.buildFails; return demo.buildFails ? 'Builds now fail (click again to undo).' : 'Builds succeed again.'; }],
  ];
  const note = document.createElement('em');
  for (const [label, act] of actions) {
    const button = document.createElement('button');
    button.type = 'button'; button.textContent = label;
    button.onclick = () => { note.textContent = act(); };
    body.append(button);
  }
  body.append(note);
  head.onclick = () => { const open = body.hidden; body.hidden = !open; head.setAttribute('aria-expanded', String(open)); chevron.textContent = open ? '⌄' : '⌃'; };
  panel.append(head, body);
  document.body.append(panel);
}
