import test from 'node:test';
import assert from 'node:assert/strict';
import { PreviewSession, seedFiles } from '../../src/SourceVault.Editor.Web/state.mjs';

test('sample files and session copies do not share mutable state', () => {
  const a = new PreviewSession(), b = new PreviewSession();
  assert.equal(a.files.size, 4);
  a.edit('pipeline', 'independent');
  assert.equal(b.file('pipeline').text, seedFiles[0].text);
  assert.equal(b.dirty('pipeline'), false);
});
test('dirty status follows text equality, including undo to saved content', () => {
  const s = new PreviewSession(), original = s.file('pipeline').text;
  s.edit('pipeline', original + '\n'); assert.equal(s.dirty('pipeline'), true);
  s.edit('pipeline', original); assert.equal(s.dirty('pipeline'), false);
  assert.equal(s.metadata.dirtyCount, 0);
});
test('save is an immutable in-memory revision and unchanged save is a no-op', () => {
  const s = new PreviewSession(); s.edit('pipeline', 'first');
  assert.equal(s.save('pipeline', 0), 1);
  assert.equal(s.dirty('pipeline'), false);
  s.edit('pipeline', 'second');
  assert.deepEqual(s.file('pipeline').history, [{ revision: 1, text: 'first' }]);
  assert.equal(s.save('pipeline', 1), 2);
  assert.equal(s.save('pipeline', 2), 2);
  assert.equal(s.metadata.revisionCount, 2);
  assert.throws(() => { s.file('pipeline').history[0].text = 'mutated'; }, TypeError);
});
test('stale revision conflicts leave current text, saved copy, and history unchanged', () => {
  const s = new PreviewSession(); s.edit('types', 'one'); s.save('types', 0); s.edit('types', 'two');
  assert.throws(() => s.save('types', 0), /RevisionConflict/);
  assert.equal(s.file('types').text, 'two'); assert.equal(s.file('types').saved, 'one');
  assert.equal(s.file('types').revision, 1); assert.equal(s.dirty('types'), true);
});
test('lock denies edits, save and file lifecycle while retaining session for resume', () => {
  const s = new PreviewSession(); s.edit('pipeline', 'session edit'); s.lock();
  for (const op of [() => s.edit('pipeline', 'lost'), () => s.save('pipeline', 0), () => s.open('snapshot'), () => s.close('types')]) assert.throws(op, /SessionLocked/);
  assert.equal(s.file('pipeline').text, 'session edit');
  assert.deepEqual(s.metadata, { locked: true, dirtyCount: 1, revisionCount: 0 });
  s.unlock(); assert.equal(s.save('pipeline', 0), 1);
});
test('switching preserves independent buffers; closing dirty files refuses silently losing edits', () => {
  const s = new PreviewSession(); s.edit('pipeline', 'pending'); s.open('snapshot'); s.open('snapshot');
  assert.equal(s.openIds.filter(id => id === 'snapshot').length, 1);
  assert.equal(s.file('pipeline').text, 'pending');
  assert.throws(() => s.close('pipeline'), /UnsavedChanges/);
  s.close('snapshot'); assert.equal(s.activeId, 'types');
  s.close('types'); assert.equal(s.activeId, 'pipeline');
  s.save('pipeline', 0); s.close('pipeline'); assert.equal(s.activeId, null);
});
test('unknown IDs and non-text edits fail; reset restores bundled defaults and discards checkpoints', () => {
  const s = new PreviewSession();
  assert.throws(() => s.open('../../secret'), /UnknownFile/);
  assert.throws(() => s.edit('pipeline', null), TypeError);
  s.edit('pipeline', 'old'); s.save('pipeline', 0); s.lock(); s.reset();
  assert.equal(s.file('pipeline').text, seedFiles[0].text);
  assert.deepEqual(s.metadata, { locked: false, dirtyCount: 0, revisionCount: 0 });
  assert.deepEqual(s.openIds, ['pipeline', 'types']);
});
