import test from 'node:test';
import assert from 'node:assert/strict';
import { splitLines, matchLines, merge3, resolve } from '../../src/SourceVault.Editor.Web/merge.mjs';

const lines = (...items) => items.map(s => s + '\n').join('');

test('splitLines keeps line endings so joining is exact', () => {
  for (const text of ['', 'a', 'a\n', 'a\nb', 'a\r\nb\r\n', '\n\n', 'x\ny\n\nz']) assert.equal(splitLines(text).join(''), text);
  assert.deepEqual(splitLines('a\r\nb'), ['a\r\n', 'b']);
});

// Reference LCS length by dynamic programming, for checking Myers on small random inputs.
function lcsLength(a, b) {
  const dp = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--) for (let j = b.length - 1; j >= 0; j--)
    dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  return dp[0][0];
}

test('matchLines finds a longest common subsequence (randomized against a reference)', () => {
  let seed = 7;
  const rand = n => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed % n; };
  for (let round = 0; round < 400; round++) {
    const a = Array.from({ length: rand(14) }, () => 'l' + rand(5));
    const b = Array.from({ length: rand(14) }, () => 'l' + rand(5));
    const pairs = matchLines(a, b);
    for (let p = 0; p < pairs.length; p++) {
      const [i, j] = pairs[p];
      assert.equal(a[i], b[j], 'pairs match equal lines');
      if (p) assert.ok(i > pairs[p - 1][0] && j > pairs[p - 1][1], 'pairs strictly increase');
    }
    assert.equal(pairs.length, lcsLength(a, b), `optimal for ${JSON.stringify([a, b])}`);
  }
});

test('changes to different lines merge cleanly', () => {
  const base = lines('one', 'two', 'three', 'four', 'five');
  const mine = lines('one (mine)', 'two', 'three', 'four', 'five');
  const theirs = lines('one', 'two', 'three', 'four (theirs)', 'five', 'six');
  const { segments, conflicts } = merge3(base, mine, theirs);
  assert.equal(conflicts, 0);
  assert.equal(resolve(segments, []), lines('one (mine)', 'two', 'three', 'four (theirs)', 'five', 'six'));
});

test('the same change on both sides is taken once; deletions on one side apply', () => {
  const base = lines('a', 'b', 'c', 'd');
  const mine = lines('a', 'B', 'c');           // changed b, deleted d
  const theirs = lines('zero', 'a', 'B', 'c', 'd'); // same change to b, added a first line
  const { segments, conflicts } = merge3(base, mine, theirs);
  assert.equal(conflicts, 0);
  assert.equal(resolve(segments, []), lines('zero', 'a', 'B', 'c'));
  // Deleting a line right where the other side appends after it is a conflict (as in git).
  assert.equal(merge3(base, mine, lines('a', 'B', 'c', 'd', 'e')).conflicts, 1);
});

test('overlapping changes are conflicts resolved by choice', () => {
  const base = lines('keep', 'value = 1', 'end');
  const mine = lines('keep', 'value = 2', 'end');
  const theirs = lines('keep', 'value = 3', 'end');
  const { segments, conflicts } = merge3(base, mine, theirs);
  assert.equal(conflicts, 1);
  const conflict = segments.find(s => s.kind === 'conflict');
  assert.deepEqual(conflict, { kind: 'conflict', base: ['value = 1\n'], mine: ['value = 2\n'], theirs: ['value = 3\n'] });
  assert.equal(resolve(segments, ['mine']), lines('keep', 'value = 2', 'end'));
  assert.equal(resolve(segments, ['theirs']), lines('keep', 'value = 3', 'end'));
  assert.equal(resolve(segments, ['both']), lines('keep', 'value = 2', 'value = 3', 'end'));
  assert.throws(() => resolve(segments, []), /Unresolved/);
});

test('edge cases: empty texts, no trailing newline, identical sides, CRLF', () => {
  assert.equal(resolve(merge3('', '', '').segments, []), '');
  assert.equal(resolve(merge3('', 'mine\n', '').segments, []), 'mine\n');
  assert.equal(resolve(merge3('x', 'x', 'y').segments, []), 'y');
  const both = merge3('a', 'b', 'c');
  assert.equal(both.conflicts, 1);
  assert.equal(resolve(both.segments, ['both']), 'b\nc', 'a line break is added between sides without one');
  const crlf = merge3('a\r\nb\r\nc\r\n', 'A\r\nb\r\nc\r\n', 'a\r\nb\r\nC\r\n');
  assert.equal(resolve(crlf.segments, []), 'A\r\nb\r\nC\r\n');
  const same = 'line\n'.repeat(5);
  assert.deepEqual(merge3(same, same, same), { segments: [{ kind: 'ok', lines: splitLines(same) }], conflicts: 0 });
});

test('large files with scattered edits merge without conflicts', () => {
  const base = Array.from({ length: 6000 }, (_, i) => `line ${i}\n`);
  const mine = [...base]; const theirs = [...base];
  for (let i = 100; i < 6000; i += 400) mine[i] = `mine ${i}\n`;
  for (let i = 300; i < 6000; i += 400) theirs[i] = `theirs ${i}\n`;
  theirs.splice(5000, 0, 'inserted by them\n');
  const started = performance.now();
  const { segments, conflicts } = merge3(base.join(''), mine.join(''), theirs.join(''));
  assert.equal(conflicts, 0);
  const merged = splitLines(resolve(segments, []));
  assert.equal(merged.length, 6001);
  assert.equal(merged[100], 'mine 100\n'); assert.equal(merged[300], 'theirs 300\n'); assert.equal(merged[5000], 'inserted by them\n');
  assert.ok(performance.now() - started < 2000, 'fast enough for 256 KiB files');
});
