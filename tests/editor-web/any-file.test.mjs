// Any-file vault in the editor (docs/vault-any-file-design.md section 4): files the editor doesn't show get an info
// panel; replace and add from disk (the demo vault makes the files up); revert; keep mine / keep theirs.
import test from 'node:test';
import assert from 'node:assert/strict';
import { VaultSession, VaultError, isOpenable, badgeOf, MAX_EDITOR_TEXT_BYTES } from '../../src/SourceVault.Editor.Web/vault-session.mjs';
import { DemoVaultHost } from '../../src/SourceVault.Editor.Web/demo-host.mjs';

const rejectsWith = (promise, code) => assert.rejects(promise, e => e instanceof VaultError && e.code === code);
async function cppProject() {
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  await session.unlock();
  await session.selectProject(host.projectIdByName('Synthetic C++ demo'));
  return { host, session, projectId: session.project.projectId, byName: name => [...session.files.values()].find(f => f.path === name) };
}
const SHA = /^[0-9a-f]{64}$/;

test('listing: kinds, sizes and hashes; binary and large text get badges and are not opened', async () => {
  const { session, byName } = await cppProject();
  const icon = byName('res/gadget.ico'), log = byName('logs/build-history.log'), main = byName('src/main.cpp');
  assert.equal(icon.kind, 'binary'); assert.equal(badgeOf(icon), 'binary'); assert.ok(!isOpenable(icon));
  assert.equal(log.kind, 'text'); assert.ok(log.byteLength > MAX_EDITOR_TEXT_BYTES); assert.equal(badgeOf(log), 'large');
  assert.equal(badgeOf(main), null); assert.ok(SHA.test(main.sha256) && SHA.test(icon.sha256));
  await rejectsWith(session.open(icon.id), 'NotOpenable');
  await rejectsWith(session.open(log.id), 'NotOpenable');
  assert.ok(!icon.loaded && icon.text === null, 'no content in the page');
  const details = await session.info(icon.id);
  assert.equal(details.kind, 'binary'); assert.equal(details.byteLength, icon.byteLength); assert.equal(details.sha256, icon.sha256);
  assert.equal(details.savedBy, 'DEMO\\operator'); assert.ok(!('text' in details) && !('contentBase64' in details), 'the info panel never carries content');
  await session.closeInfo(icon.id);
});

test('replace from disk, then revert: history lists versions with kind and SHA-256, and reverting needs no content', async () => {
  const { session, byName } = await cppProject();
  const icon = byName('res/gadget.ico');
  const first = icon.sha256;
  await session.info(icon.id);
  assert.equal(await session.replaceFromDisk(icon.id), true);
  assert.notEqual(icon.sha256, first); assert.equal(icon.kind, 'binary');
  const history = await session.listHistory(icon.id);
  assert.deepEqual(history.revisions.map(r => r.action), ['edited', 'created']);
  assert.ok(history.revisions.every(r => r.kind === 'binary' && SHA.test(r.sha256)));
  await rejectsWith(session.openRevision(icon.id, history.revisions[1].revisionId), 'NotText');
  await session.revertFile(icon.id, history.revisions[1].revisionId);
  assert.equal(session.files.get(icon.id).sha256, first, 'the first version is current again'); // the listing was refreshed
  assert.equal((await session.listHistory(icon.id)).revisions[0].action, 'reverted');
});

test('a replacement that conflicts is kept for keep mine; keep theirs discards it', async () => {
  const { host, session, projectId, byName } = await cppProject();
  const icon = byName('res/gadget.ico');
  await session.info(icon.id);
  host.externalReplace(projectId, icon.id); // someone else, after the panel showed it
  await rejectsWith(session.replaceFromDisk(icon.id), 'Conflict');
  assert.equal(icon.pending, true);
  await session.keepMine(icon.id);
  assert.equal(icon.pending, false);
  assert.deepEqual((await session.listHistory(icon.id)).revisions.map(r => [r.action, r.savedBy]),
    [['edited', 'DEMO\\operator'], ['edited', 'DEMO\\someone-else'], ['created', 'DEMO\\operator']], 'theirs stays in history');
  host.externalReplace(projectId, icon.id);
  await rejectsWith(session.replaceFromDisk(icon.id), 'Conflict');
  const theirs = (await session.listHistory(icon.id)).revisions[0];
  await session.keepTheirs(icon.id);
  assert.equal(icon.pending, false);
  assert.equal((await session.listHistory(icon.id)).revisions[0].revisionId, theirs.revisionId, 'nothing new was saved');
  await rejectsWith(session.keepMine(icon.id), 'UploadExpired');
});

test('add files from disk to a folder or the root; a cancelled dialog changes nothing', async () => {
  const { session } = await cppProject();
  const before = session.files.size, count = session.project.fileCount;
  const added = await session.addFromDisk('res');
  assert.ok(added.length >= 1 && added.every(n => n.startsWith('res/demo-asset-')));
  const atRoot = await session.addFromDisk('');
  assert.ok(atRoot.every(n => !n.includes('/')));
  assert.equal(session.files.size, before + added.length + atRoot.length);
  assert.equal(session.project.fileCount, count + added.length + atRoot.length);
  assert.ok(added.every(n => [...session.files.values()].find(f => f.path === n).kind === 'binary'));
});

test('text conflicts with a binary version: no text merge; keep mine saves over theirs, keep theirs unloads the file', async () => {
  const { host, session, projectId, byName } = await cppProject();
  const main = byName('src/main.cpp');
  await session.open(main.id);
  session.edit(main.id, 'int main() { return 1; }\n');
  host.externalReplace(projectId, main.id);
  await rejectsWith(session.save(main.id), 'Conflict');
  const merge = await session.prepareMerge(main.id);
  assert.equal(merge.mergeable, false);
  session.applyMerge(main.id, main.text, merge.theirsRevisionId); // keep mine
  assert.equal(await session.save(main.id), true);
  assert.equal(host.textOf(projectId, main.id), 'int main() { return 1; }\n');
  assert.equal(main.kind, 'text');
  // Keep theirs: their binary replaces the draft; the tab closes and the file is shown as details.
  session.edit(main.id, 'int main() { return 2; }\n');
  host.externalReplace(projectId, main.id);
  await rejectsWith(session.save(main.id), 'Conflict');
  assert.equal(await session.reload(main.id), false);
  const now = session.files.get(main.id); // the listing was refreshed
  assert.ok(!now.loaded && now.text === null && !session.openIds.includes(main.id));
  assert.equal(now.kind, 'binary');
});

test('the editor saves text up to 1 MiB and refuses more before sending it', async () => {
  const { host, session, projectId } = await cppProject();
  const big = 'x'.repeat(700 * 1024) + '\n';
  const id = await session.createFile('docs/big.txt', big);
  assert.equal(host.textOf(projectId, id), big);
  session.edit(id, big + big);
  await rejectsWith(session.save(id), 'LimitExceeded');
  await rejectsWith(session.createFile('docs/too-big.txt', 'y'.repeat(MAX_EDITOR_TEXT_BYTES + 1)), 'LimitExceeded');
});
