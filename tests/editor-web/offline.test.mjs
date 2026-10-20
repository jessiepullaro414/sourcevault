// Working offline (owner decision 2026-10-20): when the service can't be reached the editor may keep working, red, on
// what it already holds in memory. Nothing that needs the vault is sent; saves wait until it is back.
import test from 'node:test';
import assert from 'node:assert/strict';
import { VaultSession, VaultError } from '../../src/SourceVault.Editor.Web/vault-session.mjs';
import { DemoVaultHost } from '../../src/SourceVault.Editor.Web/demo-host.mjs';

const rejectsWith = (promise, code) => assert.rejects(promise, e => e instanceof VaultError && e.code === code);
const throwsWith = (fn, code) => assert.throws(fn, e => e instanceof VaultError && e.code === code);

async function connectionLost() {
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  await session.unlock();
  await session.selectProject(host.projectIdByName('Synthetic demo 1'));
  const byName = name => [...session.files.values()].find(f => f.path === name);
  await session.open(byName('main.ts').id);
  host.simulateLock('closed'); await new Promise(r => setTimeout(r, 10));
  host.failNextUnlock = 'ServiceNotRunning';
  await rejectsWith(session.unlock(), 'ServiceNotRunning');
  return { host, session, byName, projectId: session.project.projectId };
}

test('offline starts only when chosen, after the service could not be reached', async () => {
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  throwsWith(() => session.goOffline(), 'InvalidRequest'); // nothing failed yet
  host.failNextUnlock = 'AccessDenied';
  await rejectsWith(session.unlock(), 'AccessDenied');
  throwsWith(() => session.goOffline(), 'InvalidRequest'); // not an operator: no offline work
  host.failNextUnlock = 'ServiceNotRunning';
  await rejectsWith(session.unlock(), 'ServiceNotRunning');
  assert.equal(session.locked, true, 'still locked until chosen');
  session.goOffline();
  assert.equal(session.offline, true); assert.equal(session.locked, false);
});

test('connection lost: open files stay editable; nothing needing the vault is sent; saves wait and go through when it is back', async () => {
  const { host, session, byName, projectId } = await connectionLost();
  session.goOffline();
  const main = byName('main.ts'), util = byName('util.ts');
  const sentBefore = host.requests.length;
  session.edit(main.id, 'export const offline = 1;\n');
  await rejectsWith(session.open(util.id), 'Offline'); // not loaded: needs the vault
  await rejectsWith(session.search('greet'), 'Offline');
  await rejectsWith(session.listHistory(main.id), 'Offline');
  const scratch = await session.createFile('notes/offline.md', '# made offline\n');
  assert.ok(session.files.get(scratch).pendingCreate && session.dirty(scratch));
  assert.equal(session.queueSave(main.id), true); assert.equal(session.queueSave(scratch), true);
  assert.equal(host.requests.length, sentBefore, 'nothing was sent to the host while offline');
  assert.equal(host.textOf(projectId, main.id), "import { greet } from './util';\n\nexport function main(): string {\n  return greet('synthetic');\n}\n", 'the vault is unchanged');
  await session.unlock(); // the service is back
  assert.equal(session.offline, false);
  const result = await session.saveQueued();
  assert.deepEqual(result.saved.sort(), ['main.ts', 'notes/offline.md']);
  assert.equal(host.textOf(projectId, main.id), 'export const offline = 1;\n');
  const [[oldId, newId]] = result.replaced;
  assert.ok(oldId === scratch && !session.files.has(scratch) && host.textOf(projectId, newId) === '# made offline\n', 'the new file is in the vault under its vault id');
  assert.equal(session.dirtyIds.length, 0);
});

test('someone else changed a file while offline: it is left as a conflict to merge, not saved over theirs', async () => {
  const { host, session, byName, projectId } = await connectionLost();
  session.goOffline();
  const main = byName('main.ts');
  session.edit(main.id, 'mine\n'); session.queueSave(main.id);
  host.externalEdit(projectId, main.id, 'theirs\n');
  await session.unlock();
  const result = await session.saveQueued();
  assert.deepEqual(result.failed, [{ path: 'main.ts', code: 'Conflict' }]);
  assert.equal(host.textOf(projectId, main.id), 'theirs\n');
  assert.ok(session.files.get(main.id).conflict && session.dirty(main.id), 'the offline edits are kept to merge');
});

test('started offline without a project: new files wait, then go into the project chosen once connected', async () => {
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  host.failNextUnlock = 'ServiceNotRunning';
  await rejectsWith(session.unlock(), 'ServiceNotRunning');
  session.goOffline();
  assert.equal(session.project.projectId, null);
  const id = await session.createFile('draft.cs', 'class Offline { }\n');
  session.queueSave(id);
  await session.unlock();
  assert.equal(session.project, null, 'the project list is shown');
  assert.equal((await session.saveQueued()).waiting, 1, 'waits for a project');
  await session.selectProject(host.projectIdByName('Synthetic C# demo')); // offline work goes along
  assert.ok(session.files.get(id)?.pendingCreate);
  const result = await session.saveQueued();
  assert.deepEqual(result.saved, ['draft.cs']);
  assert.equal(host.textOf(session.project.projectId, result.replaced[0][1]), 'class Offline { }\n');
});

test('a Windows lock ends offline work: everything is hidden and locked again', async () => {
  const { host, session, byName } = await connectionLost();
  session.goOffline();
  session.edit(byName('main.ts').id, 'kept\n');
  host.simulateLock('windows'); await new Promise(r => setTimeout(r, 10));
  assert.equal(session.offline, false); assert.equal(session.locked, true); assert.equal(session.lockReason, 'windows');
  throwsWith(() => session.edit(byName('main.ts').id, 'more'), 'Locked');
  assert.equal(byName('main.ts').text, 'kept\n', 'drafts stay in memory behind the lock, as always');
});
