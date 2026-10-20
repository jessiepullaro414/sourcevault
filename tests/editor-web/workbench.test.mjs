import test from 'node:test';
import assert from 'node:assert/strict';
import { touchRecent, fuzzyMatch, rankFiles, highlightRuns, searchText, withDrafts, groupMatches, validQuery, counterpart, isCFamily, outline,
  DEFAULT_SETTINGS, validSettings, SHORTCUTS, MAX_SEARCH_MATCHES } from '../../src/SourceVault.Editor.Web/workbench.mjs';
import { VaultSession, VaultError, languageFor } from '../../src/SourceVault.Editor.Web/vault-session.mjs';
import { DemoVaultHost } from '../../src/SourceVault.Editor.Web/demo-host.mjs';

const rejectsWith = (promise, code) => assert.rejects(promise, e => e instanceof VaultError && e.code === code);
const flat = (nodes, depth = 0) => nodes.flatMap(s => [`${'  '.repeat(depth)}${s.kind} ${s.name}`, ...flat(s.children, depth + 1)]);

test('recently opened: most recent first, no duplicates, at most ten', () => {
  let list = [];
  for (const id of ['a', 'b', 'c', 'a']) list = touchRecent(list, id);
  assert.deepEqual(list, ['a', 'c', 'b']);
  for (let i = 0; i < 20; i++) list = touchRecent(list, `f${i}`);
  assert.equal(list.length, 10); assert.equal(list[0], 'f19');
});

test('go to file: fuzzy match prefers file names, word starts and runs', () => {
  assert.equal(fuzzyMatch('xyz', 'src/main.cpp'), null);
  assert.deepEqual(fuzzyMatch('mc', 'src/main.cpp').positions, [4, 9]);
  const files = [{ id: '1', path: 'src/gadget.cpp' }, { id: '2', path: 'include/gadget.h' }, { id: '3', path: 'docs/a-grand-design.md' }, { id: '4', path: 'src/main.cpp' }];
  assert.deepEqual(rankFiles('gadcpp', files).map(r => r.file.path), ['src/gadget.cpp']);
  assert.equal(rankFiles('gad', files)[0].file.path.includes('gadget'), true, 'a run in the file name beats scattered letters');
  assert.deepEqual(rankFiles('GADGET.H', files).map(r => r.file.id), ['2'], 'case does not matter');
  assert.deepEqual(rankFiles('', files, ['4', '2']).map(r => [r.file.id, r.recent]), [['4', true], ['2', true], ['3', false], ['1', false]], 'no query: recent first, then by path');
  assert.deepEqual(highlightRuns('main.cpp', [0, 1, 5]), [{ text: 'ma', mark: true }, { text: 'in.', mark: false }, { text: 'c', mark: true }, { text: 'pp', mark: false }]);
});

test('search text follows the service: lines, columns, case, whole words, previews and offsets', () => {
  const text = 'class Widget {}\r\n    var widgetCount = 1; // Widget\r\n';
  assert.deepEqual(searchText(text, 'widget').map(m => `${m.line}:${m.column}`), ['1:7', '2:9', '2:29']);
  assert.equal(searchText(text, 'Widget', { matchCase: true }).length, 2);
  assert.equal(searchText(text, 'widget', { wholeWord: true }).length, 2);
  const [, second, third] = searchText(text, 'widget');
  assert.equal(second.preview, 'var widgetCount = 1; // Widget'); assert.equal(second.previewOffset, 4); assert.equal(third.previewOffset, 24);
  const far = searchText('a'.repeat(300) + 'needle\n', 'needle')[0];
  assert.equal(far.preview.length, 241); assert.equal(far.preview[0], '…'); assert.equal(far.preview.substr(far.previewOffset, 6), 'needle');
  assert.equal(searchText('x x x', 'x', {}, 2).length, 2, 'limit');
  assert.equal(searchText('aaa', 'aa').length, 2, 'overlapping matches, like IndexOf(at + 1)');
  assert.equal(searchText('STRASSE straße', 'straße').length, 1, 'no case folding that changes lengths');
  assert.equal(searchText('_widget widget2 widget', 'widget', { wholeWord: true }).length, 1, 'letters, digits and _ are word characters');
  for (const bad of ['', 'q'.repeat(201), 'two\nlines', 'tab\there']) assert.equal(validQuery(bad), false);
  assert.equal(validQuery('ok'), true);
});

test('search results: drafts replace the saved matches of their files, sorted and capped at 500', () => {
  const service = { matches: [
    { fileId: 'b', name: 'src/b.cs', line: 1, column: 1, length: 3, preview: 'foo', previewOffset: 0 },
    { fileId: 'a', name: 'A.txt', line: 2, column: 1, length: 3, preview: 'foo', previewOffset: 0 },
  ], truncated: false, filesSearched: 2 };
  const result = withDrafts(service, [{ id: 'b', path: 'src/b.cs', text: 'bar\nfoo foo\n' }], 'foo', {});
  assert.deepEqual(result.matches.map(m => `${m.name}:${m.line}:${m.column}${m.draft ? '*' : ''}`), ['A.txt:2:1', 'src/b.cs:2:1*', 'src/b.cs:2:5*']);
  assert.equal(result.truncated, false); assert.equal(result.filesSearched, 2);
  assert.deepEqual(groupMatches(result.matches).map(g => [g.name, g.matches.length]), [['A.txt', 1], ['src/b.cs', 2]]);
  const big = withDrafts({ matches: [], truncated: false, filesSearched: 1 }, [{ id: 'x', path: 'x.txt', text: 'x '.repeat(600) }], 'x', {});
  assert.equal(big.matches.length, MAX_SEARCH_MATCHES); assert.equal(big.truncated, true);
});

test('C and C++ files: languages, header/source pairs', () => {
  for (const [name, language] of [['a.cpp', 'cpp'], ['a.cc', 'cpp'], ['a.hpp', 'cpp'], ['a.h', 'cpp'], ['a.c', 'c'], ['a.inl', 'cpp'], ['A.CXX', 'cpp'],
    ['App.vcxproj', 'xml'], ['build.ps1', 'powershell'], ['run.sh', 'shell'], ['make.cmd', 'bat'], ['Dockerfile', 'dockerfile'], ['CMakeLists.txt', 'plaintext']])
    assert.equal(languageFor(name), language, name);
  const paths = ['src/gadget.cpp', 'include/gadget.h', 'src/gadget.h', 'src/main.cpp', 'lib/util.c', 'lib/util.h', 'README.md'];
  assert.equal(counterpart('src/gadget.cpp', paths), 'src/gadget.h', 'same folder first');
  assert.equal(counterpart('include/gadget.h', paths), 'src/gadget.cpp', 'then anywhere (include/ and src/ layouts)');
  assert.equal(counterpart('lib/util.h', paths), 'lib/util.c');
  assert.equal(counterpart('src/main.cpp', paths), null);
  assert.equal(counterpart('README.md', paths), null);
  assert.equal(isCFamily('x.hpp'), true); assert.equal(isCFamily('x.cs'), false); assert.equal(isCFamily('Makefile'), false);
});

test('C++ outline: namespaces, types, functions and methods, macros; comments, strings and calls ignored', () => {
  const cpp = [
    '#include <vector>', '#define MAX_WIDGETS 16', '// class Commented {};', 'namespace demo {', '/* struct Hidden {', '}; */', 'class Widget;',
    'template <typename T>', 'class WIDGET_API Gadget : public Base<T> {', '  Q_PROPERTY(int size READ size)', 'public:', '  explicit Gadget(int size);',
    '  virtual ~Gadget();', '  int size() const { return size_; }', '  bool operator==(const Gadget& other) const;',
    '  std::vector<int> values(const char* name = "{") const;', '  int get() const;', 'private:', '  int size_ = 0;', '};',
    'enum class Color { Red, Green };', 'Gadget::Gadget(int size) : size_(size) {', '  if (size > 0) {', '    helper(size);', '  }', '}',
    'void Gadget::draw() const', '{', '  for (int i = 0; i < 3; i++) { call(i); }', '}', 'static int counter(int a,', '                   int b) {', '  return a + b;', '}',
    '}  // namespace demo', 'extern "C" {', 'int c_entry(void);', '}', 'TEST(GadgetTest, Size) {', '  EXPECT_EQ(1, 1);', '}',
  ].join('\r\n');
  assert.deepEqual(flat(outline('cpp', cpp)), [
    'macro MAX_WIDGETS', 'namespace demo', '  class Gadget', '    constructor Gadget', '    constructor ~Gadget', '    method size', '    method operator==',
    '    method values', '    method get', '  enum Color', '  constructor Gadget::Gadget', '  method Gadget::draw', '  function counter', 'function c_entry', 'function TEST',
  ]);
  const gadget = outline('cpp', cpp)[1].children[0];
  assert.deepEqual([gadget.line, gadget.endLine, gadget.nameColumn, gadget.nameLength], [9, 20, 18, 6], 'range and name position');
  assert.deepEqual(outline('c', 'int main(void) {\n  return 0;\n}\n').map(s => `${s.kind} ${s.name} ${s.line}-${s.endLine}`), ['function main 1-3']);
  assert.deepEqual(outline('python', 'def f(): pass'), [], 'other languages have no outline here');
});

test('C# outline: file-scoped namespace, types, members; method bodies are not scanned', () => {
  const cs = [
    'namespace Demo.Widgets;', '[Serializable]', 'public sealed class Widget : IDisposable', '{', '    private readonly List<int> items = new();',
    '    public Widget(int size) : base() { Size = size; }', '    public int Size { get; private set; }', '    public string Name', '    {',
    '        get { return "x{"; }', '    }', '    public int Twice(int x) => x * 2;', '    public async Task<int> RunAsync(CancellationToken ct)', '    {',
    '        var x = Compute(1);', '        int Local() => 1;', '        return await Task.FromResult(x);', '    }', '    private record struct Pair(int A, int B);', '}',
    'public interface IShape', '{', '    double Area();', '}', 'public enum Kind { One = 1, Two }',
  ].join('\n');
  assert.deepEqual(flat(outline('csharp', cs)), [
    'namespace Demo.Widgets', '  class Widget', '    field items', '    constructor Widget', '    property Size', '    property Name', '    method Twice',
    '    method RunAsync', '    struct Pair', '  interface IShape', '    method Area', '  enum Kind',
  ]);
});

test('settings: defaults, validation and the fake host round trip; they work while locked', async () => {
  assert.equal(validSettings({ ...DEFAULT_SETTINGS }), true);
  for (const bad of [{ ...DEFAULT_SETTINGS, theme: 'neon' }, { ...DEFAULT_SETTINGS, fontSize: 40 }, { ...DEFAULT_SETTINGS, tabSize: 3 }, { ...DEFAULT_SETTINGS, wordWrap: 'yes' },
    { ...DEFAULT_SETTINGS, recentFiles: ['a.cpp'] }, (({ minimap, ...rest }) => rest)(DEFAULT_SETTINGS), null, []])
    assert.equal(validSettings(bad), false, JSON.stringify(bad));
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 300 });
  assert.deepEqual(await session.getSettings(), { ...DEFAULT_SETTINGS }, 'readable before unlocking');
  await session.saveSettings({ ...DEFAULT_SETTINGS, theme: 'light', fontSize: 16 });
  assert.equal((await session.getSettings()).fontSize, 16);
  await rejectsWith(session.saveSettings({ ...DEFAULT_SETTINGS, recentFiles: [] }), 'InvalidRequest');
  assert.equal(host.settings.fontSize, 16, 'refused settings change nothing');
  assert.ok(SHORTCUTS.flatMap(([, rows]) => rows).some(([keys]) => keys === 'Alt+O'), 'the shortcut list covers header/source');
});

test('search all files: the service searches saved files, unsaved drafts are searched in the editor', async () => {
  const host = new DemoVaultHost();
  const session = new VaultSession(host.transport, { timeoutMs: 500 });
  await session.unlock();
  await session.selectProject(host.projectIdByName('Synthetic C++ demo'));
  const saved = await session.search('gadget');
  assert.ok(saved.matches.length > 5 && !saved.truncated && saved.filesSearched === 5 && saved.filesSkipped === 1, "the binary icon is skipped");
  assert.deepEqual([...new Set(saved.matches.map(m => m.name))], ['CMakeLists.txt', 'include/gadget.h', 'src/gadget.cpp', 'src/main.cpp']);
  const main = [...session.files.values()].find(f => f.path === 'src/main.cpp');
  await session.open(main.id);
  session.edit(main.id, 'int main() { return 0; } // no match here\n');
  const withDraft = await session.search('gadget');
  assert.ok(!withDraft.matches.some(m => m.fileId === main.id), 'the draft has no match, so the saved matches of main.cpp are gone');
  session.edit(main.id, '// Gadget draft\n');
  const cased = await session.search('Gadget', { matchCase: true });
  assert.deepEqual(cased.matches.filter(m => m.fileId === main.id).map(m => [m.line, m.column, m.draft]), [[1, 4, true]]);
  await rejectsWith(session.search(''), 'InvalidRequest');
  await rejectsWith(session.search('two\nlines'), 'InvalidRequest');
  await session.lock();
  await rejectsWith(session.search('gadget'), 'Locked');
});

test('demo mode: only app.mjs loads the demo vault, and only when the host (or a development browser) asks for it', async () => {
  const { readFile } = await import('node:fs/promises');
  const web = new URL('../../src/SourceVault.Editor.Web/', import.meta.url);
  for (const name of ['vault-app.mjs', 'vault-session.mjs', 'workbench-ui.mjs', 'shell.mjs', 'preview-app.mjs'])
    assert.ok(!(await readFile(new URL(name, web), 'utf8')).includes('demo-host'), `${name} must not load the demo vault`);
  const app = await readFile(new URL('app.mjs', web), 'utf8');
  assert.match(app, /metadata\?\.mode === 'demo'\) return \{ kind: 'demo'/, 'the native host chooses demo mode');
  assert.match(app, /if \(metadata\?\.mode === 'vault'\)/, 'vault mode is checked first and never falls into the demo');
  const css = await readFile(new URL('app.css', web), 'utf8');
  assert.match(css, /body\.mode-red\{[^}]*--accent:#ff6b6b/, 'demo mode, and an editor that cannot reach the service, are red');
});
