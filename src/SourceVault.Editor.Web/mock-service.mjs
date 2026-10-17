// In-memory mock of SourceVault protocol v1 (contracts/protocol-v1.md).
//
// Pure ES module: no DOM, no network, no storage, no logging. Everything here
// is synthetic and lives in this object's memory; it is NOT the real service,
// provides no encryption or durability, and makes no authorization claim beyond
// imitating the envelope semantics so the editor UI can be exercised against
// them. Exception messages are never placed in responses.
//
// Wire notes for the mock (the real named-pipe transport does the framing):
//  - handle(object) takes an already-parsed envelope. handleJson(string)
//    additionally enforces the byte bound, strict JSON, duplicate-key and
//    nesting rejection (see parseStrictJson). Duplicate-key rejection on the
//    real wire remains the native transport's job.
//  - A real transport drops the connection on a malformed frame. The mock
//    returns an error response instead; when the request id cannot be echoed
//    (unparseable or non-canonical) the all-zero UUID is used as a mock-only
//    placeholder. Responses always carry version 1.
//  - Excessive size or nesting returns LimitExceeded.

export const PROTOCOL_VERSION = 1;
export const LIMITS = Object.freeze({
  maxFrameBytes: 1048576,
  maxDocumentBytes: 256 * 1024,
  maxNesting: 16,
  idleExpiryMs: 5 * 60 * 1000,
  absoluteExpiryMs: 30 * 60 * 1000
});
export const ErrorCodes = Object.freeze(['Ok', 'InvalidRequest', 'UnsupportedVersion', 'UnsupportedOperation', 'Unauthorized', 'NotFound', 'RevisionConflict', 'LimitExceeded', 'SessionExpired', 'Canceled', 'Unavailable', 'InternalError']);
export const Operations = Object.freeze(['BeginSession', 'ListProjects', 'ListFiles', 'OpenDocument', 'ApplyEdits', 'CloseDocument', 'RequestBuild', 'GetBuildStatus', 'EndSession']);
export const NIL_UUID = '00000000-0000-0000-0000-000000000000';

const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
const encoder = new TextEncoder();

export const isCanonicalUuid = v => typeof v === 'string' && UUID_PATTERN.test(v) && v !== NIL_UUID;
const utf8Length = s => encoder.encode(s).length;

/** Random canonical lowercase UUID (version 4 layout) from crypto.getRandomValues. */
export function newUuid() {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

class ProtocolError extends Error {
  constructor(code) { super(code); this.code = code; }
}
const fail = code => { throw new ProtocolError(code); };

const isPlain = v => {
  if (v === null || typeof v !== 'object' || Array.isArray(v)) return false;
  const p = Object.getPrototypeOf(v);
  return p === Object.prototype || p === null;
};

// Accepts only JSON data: plain objects/arrays, strings, finite numbers,
// booleans, null; no accessors, symbols or cycles; nesting <= 16.
function checkJsonData(value, depth = 0) {
  if (value === null || typeof value === 'boolean') return;
  if (typeof value === 'string') { if (LONE_SURROGATE.test(value)) fail('InvalidRequest'); return; }
  if (typeof value === 'number') { if (!Number.isFinite(value)) fail('InvalidRequest'); return; }
  const array = Array.isArray(value);
  if (typeof value !== 'object' || (!array && !isPlain(value))) fail('InvalidRequest');
  if (depth + 1 > LIMITS.maxNesting) fail('LimitExceeded');
  for (const key of Reflect.ownKeys(value)) {
    if (typeof key !== 'string') fail('InvalidRequest');
    if (array && key === 'length') continue;
    const d = Object.getOwnPropertyDescriptor(value, key);
    if (!('value' in d) || !d.enumerable) fail('InvalidRequest');
    checkJsonData(d.value, depth + 1);
  }
}

/**
 * Strict JSON parser for the mock transport: UTF-8 byte bound (1..1 MiB),
 * duplicate property names rejected, nesting >16 rejected, no extensions.
 * Returns null-prototype objects so a "__proto__" property is plain data.
 * Throws a ProtocolError whose .code is InvalidRequest or LimitExceeded.
 */
export function parseStrictJson(text) {
  if (typeof text !== 'string') fail('InvalidRequest');
  const bytes = utf8Length(text);
  if (bytes < 1) fail('InvalidRequest');
  if (bytes > LIMITS.maxFrameBytes) fail('LimitExceeded');
  if (LONE_SURROGATE.test(text)) fail('InvalidRequest');
  const n = text.length; let i = 0;
  const ws = () => { while (i < n) { const c = text.charCodeAt(i); if (c === 0x20 || c === 0x0a || c === 0x0d || c === 0x09) i++; else break; } };
  const str = () => {
    i++; let out = '';
    for (;;) {
      if (i >= n) fail('InvalidRequest');
      const ch = text[i++];
      if (ch === '"') return out;
      if (ch < ' ') fail('InvalidRequest');
      if (ch !== '\\') { out += ch; continue; }
      const e = text[i++];
      const simple = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }[e];
      if (simple !== undefined) { out += simple; continue; }
      if (e !== 'u' || !/^[0-9a-fA-F]{4}$/.test(text.slice(i, i + 4))) fail('InvalidRequest');
      out += String.fromCharCode(parseInt(text.slice(i, i + 4), 16)); i += 4;
    }
  };
  const num = /-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/y;
  const value = depth => {
    ws(); if (i >= n) fail('InvalidRequest');
    const ch = text[i];
    if (ch === '{') {
      if (depth + 1 > LIMITS.maxNesting) fail('LimitExceeded');
      i++; const o = Object.create(null); ws();
      if (text[i] === '}') { i++; return o; }
      for (;;) {
        ws(); if (text[i] !== '"') fail('InvalidRequest');
        const k = str(); if (Object.hasOwn(o, k)) fail('InvalidRequest');
        ws(); if (text[i++] !== ':') fail('InvalidRequest');
        o[k] = value(depth + 1); ws();
        const c = text[i++];
        if (c === ',') continue;
        if (c === '}') return o;
        fail('InvalidRequest');
      }
    }
    if (ch === '[') {
      if (depth + 1 > LIMITS.maxNesting) fail('LimitExceeded');
      i++; const a = []; ws();
      if (text[i] === ']') { i++; return a; }
      for (;;) {
        a.push(value(depth + 1)); ws();
        const c = text[i++];
        if (c === ',') continue;
        if (c === ']') return a;
        fail('InvalidRequest');
      }
    }
    if (ch === '"') return str();
    for (const [word, v] of [['true', true], ['false', false], ['null', null]]) if (text.startsWith(word, i)) { i += word.length; return v; }
    num.lastIndex = i; const m = num.exec(text);
    if (!m) fail('InvalidRequest');
    i += m[0].length; const v = Number(m[0]);
    if (!Number.isFinite(v)) fail('InvalidRequest');
    return v;
  };
  const result = value(0); ws();
  if (i !== n) fail('InvalidRequest');
  return result;
}

// Synthetic seed. The file IDs are fixed so that the UI can address them;
// revision IDs are random per service instance.
export const projectSeed = Object.freeze({ projectId: '6f2b8a9c-41d3-4e75-b0a8-2c9d7e5f1a30', name: 'synthetic-workspace' });
export const seedFiles = Object.freeze([
  { key: 'pipeline', fileId: '0b6f3c1e-8d24-4a97-9c51-7e2a40d8b6f1', path: 'src/pipeline.ts', language: 'typescript', text: `import { Session, Revision } from './types';\nimport { createSnapshot } from './snapshot';\n\n/** A synthetic example. No vault connection is made. */\nexport class RevisionPipeline {\n  private revisions: Revision[] = [];\n\n  constructor(private readonly session: Session) {}\n\n  async checkpoint(source: string): Promise<Revision> {\n    const snapshot = createSnapshot(source);\n\n    const revision: Revision = {\n      id: this.revisions.length + 1,\n      label: 'Session checkpoint',\n      snapshot,\n      createdAt: new Date().toISOString(),\n    };\n\n    this.revisions.push(revision);\n    return revision;\n  }\n\n  get history(): readonly Revision[] {\n    return this.revisions;\n  }\n}\n` },
  { key: 'snapshot', fileId: '3d9a5e72-c6b1-4f08-a2d4-91b7c3e60f58', path: 'src/snapshot.ts', language: 'typescript', text: `/** Session-only synthetic snapshot. */\nexport function createSnapshot(source: string): string {\n  return source.replace(/\\r\\n/g, '\\n');\n}\n` },
  { key: 'types', fileId: '8e4c1b07-2a95-4d6e-8f13-b05d6a7c92e4', path: 'src/types.ts', language: 'typescript', text: `export interface Session {\n  readonly id: string;\n  readonly name: string;\n}\n\nexport interface Revision {\n  readonly id: number;\n  readonly label: string;\n  readonly snapshot: string;\n  readonly createdAt: string;\n}\n` },
  { key: 'readme', fileId: 'a17d6f30-5b8e-4c29-b4a6-e3f09c81d7b2', path: 'README.md', language: 'markdown', text: `# SourceVault · synthetic workspace\n\nThis editor preview uses fictional source loaded from bundled assets.\n\n- Edit files and switch between tabs.\n- Ctrl+S creates an in-memory revision for this session.\n- Ctrl+F opens Monaco's find widget.\n- Lock hides the editor; Unlock resumes the same session.\n- Reset restores this synthetic workspace.\n\nRefreshing or closing the application discards all edits and revisions.\nNo vault, key service, TPM, or build broker is connected.\n` }
].map(f => Object.freeze(f)));

const safeEqual = (a, b) => {
  if (typeof a !== 'string' || typeof b !== 'string' || a.length !== b.length) return false;
  let d = 0; for (let i = 0; i < a.length; i++) d |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return d === 0;
};

// Exact body field lists per operation (all required, none optional).
const BODY_FIELDS = Object.freeze({
  BeginSession: [],
  ListProjects: ['capability'],
  ListFiles: ['capability', 'projectId'],
  OpenDocument: ['capability', 'projectId', 'fileId'],
  ApplyEdits: ['capability', 'projectId', 'fileId', 'expectedRevision', 'text'],
  CloseDocument: ['capability', 'projectId', 'fileId'],
  RequestBuild: ['capability', 'projectId', 'revisionId', 'profileId'],
  GetBuildStatus: ['capability', 'buildId'],
  EndSession: ['capability']
});
const UUID_FIELDS = new Set(['projectId', 'fileId', 'expectedRevision', 'revisionId', 'profileId', 'buildId']);

export class MockVaultService {
  #now; #files = new Map(); #session = null;

  /** @param {{now?: () => number}} [options] now: monotonic milliseconds; inject a fake clock in tests. */
  constructor({ now = () => performance.now() } = {}) {
    this.#now = now;
    for (const f of seedFiles) {
      this.#files.set(f.fileId, { fileId: f.fileId, path: f.path, language: f.language, revisions: [Object.freeze({ revisionId: newUuid(), text: f.text })] });
    }
  }

  /** Host-initiated lock/revocation: the current capability stops working (Unauthorized). */
  revokeSession() { this.#session = null; }

  /** Validate and run a JSON string as one frame payload; returns the response as a JSON string. */
  handleJson(text) {
    let request;
    try { request = parseStrictJson(text); } catch (e) { return JSON.stringify(this.#reply(NIL_UUID, e instanceof ProtocolError ? e.code : 'InvalidRequest')); }
    return JSON.stringify(this.handle(request));
  }

  /** Handle one already-parsed request envelope and return the response envelope. */
  handle(request) {
    let requestId = NIL_UUID;
    try {
      checkJsonData(request);
      if (utf8Length(JSON.stringify(request)) > LIMITS.maxFrameBytes) fail('LimitExceeded');
      if (!isPlain(request)) fail('InvalidRequest');
      if (isCanonicalUuid(request.requestId)) requestId = request.requestId;
      if (typeof request.version === 'number' && Number.isInteger(request.version) && request.version !== PROTOCOL_VERSION) fail('UnsupportedVersion');
      const keys = Object.keys(request);
      if (keys.length !== 4 || !['version', 'requestId', 'operation', 'body'].every(k => Object.hasOwn(request, k))) fail('InvalidRequest');
      if (request.version !== PROTOCOL_VERSION || requestId === NIL_UUID || typeof request.operation !== 'string' || !isPlain(request.body)) fail('InvalidRequest');
      if (!Operations.includes(request.operation)) fail('UnsupportedOperation');
      return this.#reply(requestId, 'Ok', this.#dispatch(request.operation, request.body));
    } catch (e) {
      return this.#reply(requestId, e instanceof ProtocolError ? e.code : 'InternalError');
    }
  }

  #reply(requestId, code, body = null) {
    return { version: PROTOCOL_VERSION, requestId, ok: code === 'Ok', code, body: code === 'Ok' ? body : null };
  }

  #authorize(capability, { touch = true } = {}) {
    const s = this.#session;
    if (!s || !safeEqual(s.capability, capability)) fail('Unauthorized');
    const t = this.#now();
    if (s.expired || t - s.lastActivity >= LIMITS.idleExpiryMs || t - s.startedAt >= LIMITS.absoluteExpiryMs) { s.expired = true; fail('SessionExpired'); }
    if (touch) s.lastActivity = t;
    return s;
  }

  #project(body) { if (body.projectId !== projectSeed.projectId) fail('NotFound'); }
  #file(body) { const f = this.#files.get(body.fileId); if (!f) fail('NotFound'); return f; }

  #dispatch(op, body) {
    const fields = BODY_FIELDS[op];
    const keys = Object.keys(body);
    if (keys.length !== fields.length || !fields.every(f => Object.hasOwn(body, f))) fail('InvalidRequest');
    for (const f of fields) {
      if (f === 'capability' || f === 'text') { if (typeof body[f] !== 'string') fail('InvalidRequest'); }
      else if (UUID_FIELDS.has(f) && !isCanonicalUuid(body[f])) fail('InvalidRequest');
    }
    if (op === 'BeginSession') {
      const old = this.#session;
      if (old && !old.expired && this.#now() - old.lastActivity < LIMITS.idleExpiryMs && this.#now() - old.startedAt < LIMITS.absoluteExpiryMs) fail('InvalidRequest');
      const bytes = crypto.getRandomValues(new Uint8Array(32));
      const t = this.#now();
      this.#session = { capability: Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(''), startedAt: t, lastActivity: t, expired: false, openDocs: new Set() };
      return { capability: this.#session.capability, idleExpirySeconds: LIMITS.idleExpiryMs / 1000, absoluteExpirySeconds: LIMITS.absoluteExpiryMs / 1000 };
    }
    if (op === 'EndSession') {
      let expired = false;
      try { this.#authorize(body.capability, { touch: false }); } catch (e) { if (e.code !== 'SessionExpired') throw e; expired = true; }
      this.#session = null;
      if (expired) fail('SessionExpired');
      return {};
    }
    const session = this.#authorize(body.capability);
    switch (op) {
      case 'ListProjects': return { projects: [{ projectId: projectSeed.projectId, name: projectSeed.name }] };
      case 'ListFiles': {
        this.#project(body);
        return { files: [...this.#files.values()].map(f => ({ fileId: f.fileId, path: f.path, language: f.language, revisionId: f.revisions.at(-1).revisionId })) };
      }
      case 'OpenDocument': {
        this.#project(body); const f = this.#file(body); session.openDocs.add(f.fileId);
        const r = f.revisions.at(-1);
        return { fileId: f.fileId, path: f.path, language: f.language, revisionId: r.revisionId, text: r.text };
      }
      case 'ApplyEdits': {
        this.#project(body); const f = this.#file(body);
        if (!session.openDocs.has(f.fileId)) fail('NotFound');
        if (LONE_SURROGATE.test(body.text)) fail('InvalidRequest');
        if (utf8Length(body.text) > LIMITS.maxDocumentBytes) fail('LimitExceeded');
        if (body.expectedRevision !== f.revisions.at(-1).revisionId) fail('RevisionConflict');
        const revision = Object.freeze({ revisionId: newUuid(), text: body.text });
        f.revisions.push(revision);
        return { fileId: f.fileId, revisionId: revision.revisionId };
      }
      case 'CloseDocument': {
        this.#project(body); const f = this.#file(body); session.openDocs.delete(f.fileId); return {};
      }
      default: fail('Unavailable'); // RequestBuild, GetBuildStatus: no build broker exists in this preview.
    }
  }
}
