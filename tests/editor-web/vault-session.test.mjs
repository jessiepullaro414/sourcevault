import test from 'node:test';
import assert from 'node:assert/strict';
import { VaultSession, BridgeClient, VaultError, languageFor } from '../../src/SourceVault.Editor.Web/vault-session.mjs';
import { FakeVaultHost } from '../../src/SourceVault.Editor.Web/dev/fake-host.mjs';

async function opened(options = {}) {
  const host = new FakeVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500, ...options });
  await session.unlock();
  await session.selectProject(host.projectIdByName('Synthetic demo 1'));
  return { host, session, projectId: session.project.projectId, byName: name => [...session.files.values()].find(f => f.path === name) };
}
const rejectsWith = (promise, code) => assert.rejects(promise, e => e instanceof VaultError && e.code === code);

test('starts locked; unlock opens a session and lists projects; nothing is loaded until opened', async () => {
  const host = new FakeVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  assert.equal(session.locked, true);
  assert.deepEqual(await session.unlock(), []);
  assert.deepEqual(session.projects.map(p => p.name), ['Synthetic demo 1', 'Lab client check']);
  await session.selectProject(host.projectIdByName('Synthetic demo 1'));
  assert.equal(session.files.size, 3);
  assert.ok([...session.files.values()].every(f => !f.loaded && f.text === null), 'no text before open');
});

test('open, edit, save; per-file saves never conflict each other; text round-trips', async () => {
  const { host, session, projectId, byName } = await opened();
  const main = byName('main.ts'), util = byName('util.ts');
  await session.open(main.id); await session.open(util.id);
  assert.equal(main.language, 'typescript');
  session.edit(main.id, 'export const a = "é ✓ 𝄞";\n');
  assert.equal(session.dirty(main.id), true);
  assert.equal(await session.save(main.id), true);
  session.edit(util.id, 'export const b = 2;\n');
  assert.equal(await session.save(util.id), true, 'saving util after main does not conflict');
  assert.equal(host.textOf(projectId, main.id), 'export const a = "é ✓ 𝄞";\n');
  assert.equal(session.dirty(main.id), false);
  assert.equal(await session.save(main.id), false, 'nothing to save');
  assert.deepEqual(session.metadata, { locked: false, dirtyCount: 0, revisionCount: 2 });
});

test('someone else changing the file is a conflict; reload takes their version; other files still save', async () => {
  const { host, session, projectId, byName } = await opened();
  const main = byName('main.ts'), readme = byName('README.md');
  await session.open(main.id); await session.open(readme.id);
  host.externalEdit(projectId, main.id, '// theirs\n');
  session.edit(main.id, '// mine\n');
  await rejectsWith(session.save(main.id), 'Conflict');
  assert.equal(main.conflict, true);
  assert.equal(main.text, '// mine\n', 'local edits kept after a conflict');
  session.edit(readme.id, '# still mine\n');
  assert.equal(await session.save(readme.id), true);
  await session.reload(main.id);
  assert.equal(main.text, '// theirs\n'); assert.equal(main.conflict, false); assert.equal(session.dirty(main.id), false);
  session.edit(main.id, '// after reload\n');
  assert.equal(await session.save(main.id), true);
});

test('closing drops text; closing with unsaved edits is refused', async () => {
  const { session, byName } = await opened();
  const util = byName('util.ts');
  await session.open(util.id);
  session.edit(util.id, 'changed');
  await rejectsWith(session.close(util.id), 'UnsavedChanges');
  await session.save(util.id);
  await session.close(util.id);
  assert.equal(util.loaded, false); assert.equal(util.text, null); assert.equal(session.openIds.includes(util.id), false);
});

test('create file and project; duplicate names are AlreadyExists; switching projects with drafts is refused', async () => {
  const { session, byName } = await opened();
  const id = await session.createFile('notes.md', '# notes\n');
  assert.equal(session.activeId, id); assert.equal(session.file(id).language, 'markdown');
  await rejectsWith(session.createFile('NOTES.md'), 'AlreadyExists');
  await rejectsWith(session.createFile('a/../b.ts'), 'InvalidRequest');
  assert.ok(await session.createFile('src/nested/deep.ts', 'x'), 'folder paths are allowed');
  session.edit(byName('notes.md').id, '# draft\n');
  await rejectsWith(session.createProject('Second'), 'UnsavedChanges');
  await session.save(id);
  await session.createProject('Second');
  assert.equal(session.project.name, 'Second'); assert.equal(session.files.size, 0);
  await rejectsWith(session.createProject('second'), 'AlreadyExists');
});

test('deleting a project needs its exact name, refuses drafts in it, and clears it when open', async () => {
  const { host, session, projectId, byName } = await opened();
  const other = host.projectIdByName('Lab client check');
  await rejectsWith(session.deleteProject(other, 'lab client check'), 'NameMismatch');
  assert.equal(await session.deleteProject(other, 'Lab client check'), true);
  assert.ok(!session.projects.some(p => p.projectId === other)); assert.equal(host.projectIdByName('Lab client check'), null);
  await rejectsWith(session.deleteProject(other, 'Lab client check'), 'NotFound');
  const main = byName('main.ts');
  await session.open(main.id);
  session.edit(main.id, '// draft\n');
  await rejectsWith(session.deleteProject(projectId, session.project.name), 'UnsavedChanges');
  session.edit(main.id, main.saved);
  await session.deleteProject(projectId, 'Synthetic demo 1');
  assert.equal(session.project, null); assert.equal(session.files.size, 0); assert.equal(session.activeId, null);
  assert.deepEqual(session.projects, []);
  await session.createProject('Fresh');
  assert.equal(session.project.name, 'Fresh');
});

test('user lock keeps drafts hidden; unlock reports files changed meanwhile as conflicts', async () => {
  const { host, session, projectId, byName } = await opened();
  const main = byName('main.ts');
  await session.open(main.id);
  session.edit(main.id, '// draft kept while locked\n');
  await session.lock();
  assert.equal(session.locked, true); assert.equal(session.lockReason, 'user');
  assert.throws(() => session.edit(main.id, 'x'), e => e.code === 'Locked');
  host.externalEdit(projectId, main.id, '// edited while you were away\n');
  assert.deepEqual(await session.unlock(), [main.id]);
  assert.equal(main.text, '// draft kept while locked\n', 'draft survived the lock');
  assert.equal(main.conflict, true);
  await rejectsWith(session.save(main.id), 'Conflict');
});

test('host lock events (Windows lock, expiry) lock the session and notify the UI', async () => {
  const { host, session } = await opened();
  let notified = 0; session.onChange(() => notified++);
  host.simulateLock('windows');
  await new Promise(r => setTimeout(r, 10));
  assert.equal(session.locked, true); assert.equal(session.lockReason, 'windows'); assert.equal(notified, 1);
  await rejectsWith(session.refreshProjects(), 'Locked');
  await session.unlock();
  host.simulateLock('expired');
  await new Promise(r => setTimeout(r, 10));
  assert.equal(session.lockReason, 'expired');
});

test('unlock failures surface their code and leave the session locked', async () => {
  const host = new FakeVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  host.failNextUnlock = 'AccessDenied';
  await rejectsWith(session.unlock(), 'AccessDenied');
  assert.equal(session.locked, true);
  assert.deepEqual(await session.unlock(), []);
});

test('requests match the host bridge exactly: a malformed request gets no answer and times out', async () => {
  const host = new FakeVaultHost();
  const client = new BridgeClient(host.transport, { timeoutMs: 100 });
  assert.equal((await client.request('unlock')).code, 'Ok');
  await rejectsWith(client.request('unlock', { extra: 1 }), 'Timeout');
  await rejectsWith(client.request('openDocument', { projectId: 'NOT-A-UUID', fileId: crypto.randomUUID() }), 'Timeout');
  await rejectsWith(client.request('deleteProject'), 'Timeout');
  assert.deepEqual(host.requests, ['unlock'], 'only the well-formed request reached the vault');
  client.dispose();
});

test('responses for other requests are ignored; metadata never carries text and stays within host bounds', async () => {
  const { session, byName } = await opened();
  const main = byName('main.ts');
  await session.open(main.id);
  session.edit(main.id, 'SECRET-LOOKING DRAFT');
  const metadata = session.metadata;
  assert.deepEqual(Object.keys(metadata).sort(), ['dirtyCount', 'locked', 'revisionCount']);
  assert.ok(!JSON.stringify(metadata).includes('SECRET'));
  session.savedCount = 5000000;
  assert.equal(session.metadata.revisionCount, 1000000);
  assert.equal(languageFor('x.unknown'), 'plaintext'); assert.equal(languageFor('A.TS'), 'typescript');
});
