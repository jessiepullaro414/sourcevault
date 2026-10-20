// In-editor clipboard (owner decision 2026-10-20): copies stay in the page; only a fixed notice reaches Windows.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { CLIPBOARD_NOTICE, pasteSource, copyFromEditor, clearInternalClipboard } from '../../src/SourceVault.Editor.Web/shell.mjs';

const sel = (sl, sc, el, ec) => ({ startLineNumber: sl, startColumn: sc, endLineNumber: el, endColumn: ec, isEmpty: () => sl === el && sc === ec });
function fakeEditor(lines, selections) {
  const text = r => r.startLineNumber === r.endLineNumber ? lines[r.startLineNumber - 1].slice(r.startColumn - 1, r.endColumn - 1)
    : [lines[r.startLineNumber - 1].slice(r.startColumn - 1), ...lines.slice(r.startLineNumber, r.endLineNumber - 1), lines[r.endLineNumber - 1].slice(0, r.endColumn - 1)].join('\n');
  return { getModel: () => ({ getValueInRange: text, getLineContent: n => lines[n - 1], getEOL: () => '\n' }), getSelections: () => selections };
}

test('copy keeps selections (one per cursor) or the whole line; paste of the notice gives the kept copy back', () => {
  const lines = ['alpha beta', 'gamma', 'delta'];
  assert.deepEqual(copyFromEditor(fakeEditor(lines, [sel(1, 7, 1, 11)])), { text: 'beta', lines: null, wholeLine: false });
  assert.deepEqual(copyFromEditor(fakeEditor(lines, [sel(3, 1, 3, 6), sel(1, 1, 1, 6)])), { text: 'alpha\ndelta', lines: ['alpha', 'delta'], wholeLine: false }, 'in document order');
  assert.deepEqual(copyFromEditor(fakeEditor(lines, [sel(2, 3, 2, 3)])), { text: 'gamma\n', lines: null, wholeLine: true }, 'empty selection: the line');
  assert.equal(copyFromEditor({ getModel: () => null, getSelections: () => [] }), null);
});

test('pasting: the notice means the kept copy, anything else is text from another program; lock forgets the copy', () => {
  clearInternalClipboard();
  assert.equal(pasteSource(CLIPBOARD_NOTICE), null, 'nothing kept yet');
  assert.deepEqual(pasteSource('from outside'), { text: 'from outside', lines: null, wholeLine: false });
  assert.equal(pasteSource(''), null);
  assert.ok(!CLIPBOARD_NOTICE.includes('\n') && CLIPBOARD_NOTICE.length < 200, 'one short line, no content');
});

test('only the notice is ever written to the clipboard; drag, context menu and the async Clipboard API stay blocked', () => {
  const shell = readFileSync(new URL('../../src/SourceVault.Editor.Web/shell.mjs', import.meta.url), 'utf8');
  const writes = [...shell.matchAll(/setData\(([^)]*)\)/g)].map(m => m[1]);
  assert.deepEqual(writes, ["'text/plain', CLIPBOARD_NOTICE"], 'the only clipboard write');
  assert.match(shell, /for \(const name of \['dragstart', 'contextmenu'\]\)/, 'nothing can be dragged out of the page');
  assert.match(shell, /closest\('\[data-file-drop\]'\)/, 'files drop only on the marked explorer');
  assert.match(shell, /defineProperty\(navigator, 'clipboard', \{ value: undefined, configurable: false \}\)/);
  assert.match(shell, /window\.addEventListener\(name, onClipboard, true\)/, 'capture phase, before Monaco');
});

test('dropped files are passed on unread: no first-party code reads a File', () => {
  for (const name of ['shell.mjs', 'app.mjs', 'vault-app.mjs', 'vault-session.mjs', 'demo-host.mjs', 'workbench-ui.mjs']) {
    const source = readFileSync(new URL(`../../src/SourceVault.Editor.Web/${name}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /FileReader|readAs(Text|ArrayBuffer|DataURL)|\.arrayBuffer\(|\.stream\(|\.text\(\)|\.bytes\(\)|createObjectURL/, name);
  }
});
