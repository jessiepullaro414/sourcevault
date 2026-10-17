import test from 'node:test';
import assert from 'node:assert/strict';
import { MockVaultService, LIMITS, ErrorCodes, NIL_UUID, newUuid, parseStrictJson, isCanonicalUuid, seedFiles } from '../../src/SourceVault.Editor.Web/mock-service.mjs';

const MIN = 60 * 1000;
function setup() {
  let t = 1000;
  const clock = { now: () => t, advance: ms => { t += ms; } };
  const svc = new MockVaultService({ now: clock.now });
  const call = (operation, body = {}, extra = {}) => svc.handle({ version: 1, requestId: newUuid(), operation, body, ...extra });
  const begin = () => call('BeginSession').body.capability;
  return { svc, clock, call, begin };
}
function ctx() {
  const s = setup(); const cap = s.begin();
  const projectId = s.call('ListProjects', { capability: cap }).body.projects[0].projectId;
  const files = s.call('ListFiles', { capability: cap, projectId }).body.files;
  const file = files[0];
  const doc = () => s.call('OpenDocument', { capability: cap, projectId, fileId: file.fileId });
  return { ...s, cap, projectId, files, file, doc };
}
const code = r => r.code;

test('responses echo version and requestId and use only defined codes', () => {
  const { svc } = setup(); const requestId = newUuid();
  const r = svc.handle({ version: 1, requestId, operation: 'BeginSession', body: {} });
  assert.deepEqual(Object.keys(r).sort(), ['body', 'code', 'ok', 'requestId', 'version']);
  assert.equal(r.version, 1); assert.equal(r.requestId, requestId); assert.equal(r.ok, true); assert.equal(r.code, 'Ok');
  const bad = svc.handle({ version: 1, requestId, operation: 'Nope', body: {} });
  assert.deepEqual(bad, { version: 1, requestId, ok: false, code: 'UnsupportedOperation', body: null });
  assert.ok(ErrorCodes.includes(bad.code));
});

test('envelope rejection: missing, extra, wrong-typed and non-canonical fields', () => {
  const { svc } = setup(); const ok = () => ({ version: 1, requestId: newUuid(), operation: 'BeginSession', body: {} });
  for (const k of ['version', 'requestId', 'operation', 'body']) { const m = ok(); delete m[k]; assert.equal(svc.handle(m).code, 'InvalidRequest', `missing ${k}`); }
  assert.equal(svc.handle({ ...ok(), extra: 1 }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), version: '1' }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), version: 1.5 }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), version: 2 }).code, 'UnsupportedVersion');
  assert.equal(svc.handle({ ...ok(), version: 0 }).code, 'UnsupportedVersion');
  for (const id of ['', NIL_UUID, newUuid().toUpperCase(), `{${newUuid()}}`, newUuid().replaceAll('-', ''), ` ${newUuid()}`, 5, null]) {
    const r = svc.handle({ ...ok(), requestId: id });
    assert.equal(r.code, 'InvalidRequest', String(id)); assert.equal(r.requestId, NIL_UUID);
  }
  assert.equal(svc.handle({ ...ok(), operation: 5 }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), body: null }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), body: [] }).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), body: { extra: 1 } }).code, 'InvalidRequest');
  for (const v of [null, 5, 'x', [], undefined, () => 1]) assert.equal(svc.handle(v).code, 'InvalidRequest');
  assert.equal(svc.handle({ ...ok(), body: { x: NaN } }).code, 'InvalidRequest');
  const withClass = new (class X { constructor() { this.version = 1; } })(); assert.equal(svc.handle(withClass).code, 'InvalidRequest');
});

test('operation names are exact and case-sensitive; unknown operations are UnsupportedOperation', () => {
  const { call } = setup();
  for (const op of ['beginsession', 'BEGINSESSION', 'BeginSession ', 'DeleteEverything', '', '__proto__', 'constructor', 'toString']) assert.equal(call(op).code, 'UnsupportedOperation', op);
});

test('error responses never carry exception text or request content', () => {
  const { svc } = setup();
  const hostile = { version: 1, requestId: newUuid(), operation: 'BeginSession', body: {} };
  Object.defineProperty(hostile, 'trap', { get() { throw new Error('SECRET-MESSAGE'); }, enumerable: true });
  const r = svc.handle(hostile);
  assert.equal(r.code, 'InvalidRequest'); assert.equal(r.body, null);
  assert.ok(!JSON.stringify(r).includes('SECRET'));
  const s = setup(); const cap = s.begin();
  const rr = s.call('ApplyEdits', { capability: cap, projectId: newUuid(), fileId: newUuid(), expectedRevision: newUuid(), text: 'PRIVATE-TEXT' });
  assert.equal(rr.code, 'NotFound'); assert.ok(!JSON.stringify(rr).includes('PRIVATE-TEXT'));
});

test('size and nesting limits: 1 MiB frame, 256 KiB document, nesting 16', () => {
  const c = ctx();
  const request = body => ({ version: 1, requestId: newUuid(), operation: 'ApplyEdits', body: { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'x', ...body } });
  c.doc();
  assert.equal(c.svc.handle(request({ text: 'a'.repeat(LIMITS.maxDocumentBytes + 1) })).code, 'LimitExceeded');
  assert.equal(c.svc.handle(request({ text: '€'.repeat(Math.floor(LIMITS.maxDocumentBytes / 3) + 1) })).code, 'LimitExceeded', 'bytes, not UTF-16 units');
  assert.equal(c.svc.handle(request({ text: 'a'.repeat(LIMITS.maxDocumentBytes) })).code, 'Ok');
  assert.equal(c.svc.handle({ version: 1, requestId: newUuid(), operation: 'BeginSession', body: { pad: 'p'.repeat(LIMITS.maxFrameBytes) } }).code, 'LimitExceeded');
  let deep = {}; for (let i = 0; i < LIMITS.maxNesting; i++) deep = { n: deep };
  assert.equal(c.svc.handle({ version: 1, requestId: newUuid(), operation: 'BeginSession', body: deep }).code, 'LimitExceeded');
  const cyc = { version: 1, requestId: newUuid(), operation: 'BeginSession', body: {} }; cyc.body.self = cyc;
  assert.equal(c.svc.handle(cyc).code, 'LimitExceeded');
});

test('lone surrogates in document text are rejected, state unchanged', () => {
  const c = ctx(); c.doc();
  const r = c.call('ApplyEdits', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'bad\uD800' });
  assert.equal(r.code, 'InvalidRequest');
  assert.equal(c.doc().body.revisionId, c.file.revisionId);
});

test('BeginSession returns a fresh opaque 256-bit capability; second concurrent session is refused', () => {
  const a = setup(); const r = a.call('BeginSession');
  assert.match(r.body.capability, /^[0-9a-f]{64}$/);
  assert.equal(a.call('BeginSession').code, 'InvalidRequest');
  const caps = new Set(); for (let i = 0; i < 20; i++) { const s = setup(); caps.add(s.begin()); }
  assert.equal(caps.size, 20);
});

test('operations require a valid capability; the capability is not identity by shape', () => {
  const c = ctx();
  const ops = { ListProjects: {}, ListFiles: { projectId: c.projectId }, OpenDocument: { projectId: c.projectId, fileId: c.file.fileId }, CloseDocument: { projectId: c.projectId, fileId: c.file.fileId }, EndSession: {}, ApplyEdits: { projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'x' }, RequestBuild: { projectId: c.projectId, revisionId: newUuid(), profileId: newUuid() }, GetBuildStatus: { buildId: newUuid() } };
  for (const [op, body] of Object.entries(ops)) {
    assert.equal(c.call(op, { capability: '0'.repeat(64), ...body }).code, 'Unauthorized', op);
    assert.equal(c.call(op, { capability: c.cap.slice(0, -1) + (c.cap.endsWith('0') ? '1' : '0'), ...body }).code, 'Unauthorized', op);
    assert.equal(c.call(op, { capability: '', ...body }).code, 'Unauthorized', op);
    assert.equal(c.call(op, body).code, 'InvalidRequest', `${op} missing capability`);
    assert.equal(c.call(op, { capability: 5, ...body }).code, 'InvalidRequest', op);
  }
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Ok');
  const noSession = setup(); assert.equal(noSession.call('ListProjects', { capability: '0'.repeat(64) }).code, 'Unauthorized');
});

test('list/open/close metadata and NotFound handling use canonical UUIDs', () => {
  const c = ctx();
  assert.equal(c.files.length, seedFiles.length);
  for (const f of c.files) { assert.ok(isCanonicalUuid(f.fileId)); assert.ok(isCanonicalUuid(f.revisionId)); }
  assert.ok(isCanonicalUuid(c.projectId));
  assert.equal(c.call('ListFiles', { capability: c.cap, projectId: newUuid() }).code, 'NotFound');
  assert.equal(c.call('ListFiles', { capability: c.cap, projectId: '../../etc' }).code, 'InvalidRequest');
  assert.equal(c.call('OpenDocument', { capability: c.cap, projectId: c.projectId, fileId: newUuid() }).code, 'NotFound');
  assert.equal(c.call('OpenDocument', { capability: c.cap, projectId: c.projectId, fileId: 'C:\\secret.txt' }).code, 'InvalidRequest');
  const d = c.doc();
  assert.equal(d.body.text, seedFiles[0].text); assert.equal(d.body.revisionId, c.file.revisionId);
  assert.equal(c.call('CloseDocument', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId }).code, 'Ok');
  assert.equal(c.call('CloseDocument', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId }).code, 'Ok', 'idempotent');
  assert.equal(c.call('ApplyEdits', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'x' }).code, 'NotFound', 'closed document');
});

test('ApplyEdits creates immutable new revisions; conflicts leave state unchanged', () => {
  const c = ctx(); c.doc();
  const apply = (rev, text) => c.call('ApplyEdits', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: rev, text });
  const r1 = apply(c.file.revisionId, 'first');
  assert.equal(r1.code, 'Ok'); assert.ok(isCanonicalUuid(r1.body.revisionId)); assert.notEqual(r1.body.revisionId, c.file.revisionId);
  assert.equal(c.doc().body.text, 'first');
  const stale = apply(c.file.revisionId, 'stale write');
  assert.deepEqual([stale.ok, stale.code, stale.body], [false, 'RevisionConflict', null]);
  const unknownRev = apply(newUuid(), 'unknown revision');
  assert.equal(unknownRev.code, 'RevisionConflict');
  const after = c.doc().body;
  assert.equal(after.text, 'first'); assert.equal(after.revisionId, r1.body.revisionId);
  const r2 = apply(r1.body.revisionId, 'second');
  assert.equal(r2.code, 'Ok'); assert.notEqual(r2.body.revisionId, r1.body.revisionId);
  assert.equal(apply(r1.body.revisionId, 'again').code, 'RevisionConflict');
  assert.equal(apply(r2.body.revisionId, 12).code, 'InvalidRequest');
  assert.equal(c.doc().body.text, 'second');
});

test('document state persists across sessions of one service instance (preview memory) but sessions are separate', () => {
  const c = ctx(); c.doc();
  const r = c.call('ApplyEdits', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'kept' });
  assert.equal(c.call('EndSession', { capability: c.cap }).code, 'Ok');
  const cap2 = c.begin();
  assert.notEqual(cap2, c.cap);
  const d = c.call('OpenDocument', { capability: cap2, projectId: c.projectId, fileId: c.file.fileId });
  assert.equal(d.body.text, 'kept'); assert.equal(d.body.revisionId, r.body.revisionId);
});

test('build operations are Unavailable after authorization and do not echo input', () => {
  const c = ctx();
  const rb = c.call('RequestBuild', { capability: c.cap, projectId: c.projectId, revisionId: c.file.revisionId, profileId: newUuid() });
  const gb = c.call('GetBuildStatus', { capability: c.cap, buildId: newUuid() });
  for (const r of [rb, gb]) assert.deepEqual([r.ok, r.code, r.body], [false, 'Unavailable', null]);
  assert.equal(c.call('RequestBuild', { capability: c.cap, projectId: c.projectId, revisionId: c.file.revisionId, profileId: newUuid(), command: 'calc' }).code, 'InvalidRequest');
});

test('EndSession invalidates the capability; revoke (lock) does too', () => {
  const c = ctx();
  assert.equal(c.call('EndSession', { capability: c.cap }).code, 'Ok');
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Unauthorized');
  assert.equal(c.call('EndSession', { capability: c.cap }).code, 'Unauthorized');
  const cap2 = c.begin(); assert.equal(c.call('ListProjects', { capability: cap2 }).code, 'Ok');
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Unauthorized', 'old capability stays dead after a new session');
  c.svc.revokeSession();
  assert.equal(c.call('ListProjects', { capability: cap2 }).code, 'Unauthorized');
  const cap3 = c.begin(); assert.notEqual(cap3, cap2); assert.equal(c.call('ListProjects', { capability: cap3 }).code, 'Ok');
});

test('idle expiry is 5 minutes from last authorized activity (fake clock)', () => {
  const c = ctx();
  c.clock.advance(5 * MIN - 1); assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Ok');
  c.clock.advance(5 * MIN - 1); assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Ok', 'activity renews idle timer');
  c.clock.advance(5 * MIN);
  const r = c.call('ListProjects', { capability: c.cap });
  assert.deepEqual([r.ok, r.code, r.body], [false, 'SessionExpired', null]);
  c.clock.advance(-5 * MIN);
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'SessionExpired', 'expiry is sticky');
  assert.equal(c.call('OpenDocument', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId }).code, 'SessionExpired');
  assert.equal(c.call('ApplyEdits', { capability: c.cap, projectId: c.projectId, fileId: c.file.fileId, expectedRevision: c.file.revisionId, text: 'late' }).code, 'SessionExpired');
  const cap2 = c.begin();
  assert.equal(c.call('OpenDocument', { capability: cap2, projectId: c.projectId, fileId: c.file.fileId }).body.text, seedFiles[0].text, 'expired write did not apply');
});

test('absolute expiry is 30 minutes even with continuous activity', () => {
  const c = ctx();
  for (let i = 0; i < 29; i++) { c.clock.advance(MIN); assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Ok', `minute ${i + 1}`); }
  c.clock.advance(MIN - 1); assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Ok');
  c.clock.advance(1);
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'SessionExpired');
});

test('expired session: EndSession reports SessionExpired and clears; new session can begin', () => {
  const c = ctx(); c.clock.advance(5 * MIN);
  assert.equal(c.call('BeginSession').code, 'Ok', 'an expired session can be replaced');
  assert.equal(c.call('ListProjects', { capability: c.cap }).code, 'Unauthorized');
  const d = setup(); const cap = d.begin(); d.clock.advance(31 * MIN);
  assert.equal(d.call('EndSession', { capability: cap }).code, 'SessionExpired');
  assert.equal(d.call('ListProjects', { capability: cap }).code, 'Unauthorized');
});

test('handleJson: strict JSON, duplicate keys, nesting, size, and UTF-8 bounds', () => {
  const { svc } = setup(); const id = newUuid();
  const good = JSON.stringify({ version: 1, requestId: id, operation: 'BeginSession', body: {} });
  const r = JSON.parse(svc.handleJson(good)); assert.equal(r.code, 'Ok'); assert.equal(r.requestId, id);
  for (const text of [
    '', ' ', '{', '[]', 'null', `${good} x`, `\uFEFF${good}`, "{'version':1}", '{"version":1,}', '{"a":01}', '{"a":"\\x"}', '{"a":"\t"}',
    `{"version":1,"version":1,"requestId":"${id}","operation":"BeginSession","body":{}}`,
    `{"version":1,"requestId":"${id}","operation":"BeginSession","body":{"a":1,"a":2}}`,
    `{"version":1,"requestId":"${id}","operation":"BeginSession","body":{},"__proto__":1,"__proto__":2}`
  ]) { const x = JSON.parse(svc.handleJson(text)); assert.equal(x.code, 'InvalidRequest', text); assert.equal(x.requestId, NIL_UUID); assert.equal(x.body, null); }
  assert.equal(JSON.parse(svc.handleJson(`{"version":1,"requestId":"${id}","operation":"BeginSession","body":{},"__proto__":{}}`)).code, 'InvalidRequest', 'extra field, not prototype pollution');
  assert.equal({}.polluted, undefined);
  const deep = '['.repeat(17) + ']'.repeat(17);
  assert.equal(JSON.parse(svc.handleJson(deep)).code, 'LimitExceeded');
  assert.doesNotThrow(() => parseStrictJson('['.repeat(16) + ']'.repeat(16)));
  assert.throws(() => parseStrictJson('['.repeat(17) + ']'.repeat(17)), { code: 'LimitExceeded' });
  assert.equal(JSON.parse(svc.handleJson('"' + 'a'.repeat(LIMITS.maxFrameBytes) + '"')).code, 'LimitExceeded');
  assert.equal(JSON.parse(svc.handleJson('"' + '€'.repeat(Math.floor(LIMITS.maxFrameBytes / 3)) + '"')).code, 'LimitExceeded', 'byte length, not character count');
  assert.equal(JSON.parse(svc.handleJson(`{"version":2,"requestId":"${id}","operation":"BeginSession","body":{}}`)).code, 'UnsupportedVersion');
  assert.deepEqual(parseStrictJson('{"a":[1,-2.5e2,true,null,"\\u00e9\\n"]}').a, [1, -250, true, null, 'é\n']);
});

test('seed data is synthetic and identifiers are canonical UUIDs', () => {
  for (const f of seedFiles) assert.ok(isCanonicalUuid(f.fileId));
  assert.equal(new Set(seedFiles.map(f => f.fileId)).size, seedFiles.length);
});
