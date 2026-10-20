// Workbench helpers for vault mode (docs/editor-host.md "Editor features"): quick-open ranking, recently opened files,
// plain-text search of unsaved drafts (the same rules as the service's SearchProject), outlines for C#, C and C++,
// and C/C++ header/source switching. Pure ES module: no DOM and no Monaco, so the node tests cover all of it.
// Nothing here stores anything; recently opened files live in memory only and are never written to disk.

export const MAX_RECENT = 10;
export const MAX_SEARCH_MATCHES = 500, MAX_QUERY_CHARS = 200, PREVIEW_CHARS = 240;

/** EditorSettings on the host (editor-settings.json): exactly these fields. Display choices only, never names or text. */
export const DEFAULT_SETTINGS = Object.freeze({ theme: 'dark', fontSize: 13, wordWrap: false, minimap: true, tabSize: 2, insertSpaces: true, renderWhitespace: false, lineNumbers: true });
export const validSettings = s => !!s && typeof s === 'object' && !Array.isArray(s) && Object.keys(s).sort().join() === Object.keys(DEFAULT_SETTINGS).sort().join()
  && ['dark', 'light', 'highContrast'].includes(s.theme) && Number.isInteger(s.fontSize) && s.fontSize >= 10 && s.fontSize <= 28 && [2, 4, 8].includes(s.tabSize)
  && ['wordWrap', 'minimap', 'insertSpaces', 'renderWhitespace', 'lineNumbers'].every(k => typeof s[k] === 'boolean');

/** The keyboard shortcut list (Ctrl+K Ctrl+S), also in docs/user-guide.md. */
export const SHORTCUTS = [
  ['Files', [
    ['Ctrl+P', 'Go to a file by name (start with : for a line, @ for a symbol, > for a command)'],
    ['Ctrl+E', 'Recently opened files'],
    ['Alt+O', 'Switch between a C or C++ header and its source file'],
    ['Ctrl+S', 'Save the file'],
    ['Ctrl+Alt+S', 'Save all files'],
    ['Ctrl+K D', 'Compare the file with its saved version'],
  ]],
  ['Search and navigation', [
    ['Ctrl+F / Ctrl+H', 'Find / replace in the file'],
    ['Ctrl+Shift+F', 'Search all files'],
    ['Ctrl+Shift+O', 'Go to a symbol in the file (outline)'],
    ['Ctrl+G', 'Go to a line'],
    ['F12', 'Go to definition (TypeScript and JavaScript)'],
    ['F1', 'All commands'],
  ]],
  ['Editing', [
    ['Shift+Alt+F', 'Format the file (languages that have a formatter)'],
    ['Ctrl+/', 'Comment or uncomment lines'],
    ['Alt+Up / Alt+Down', 'Move lines up or down'],
    ['Shift+Alt+Down', 'Duplicate lines'],
    ['Ctrl+D', 'Add the next match to the selection'],
    ['Alt+Click', 'Add a cursor'],
  ]],
  ['Layout', [
    ['Ctrl+\\', 'Split the editor, or back to one'],
    ['Ctrl+K Z', 'Focus mode'],
    ['Ctrl+,', 'Editor settings'],
    ['Ctrl+K Ctrl+S', 'This list'],
  ]],
];

/** The recently opened list with `id` moved to the front. */
export function touchRecent(list, id, max = MAX_RECENT) { return [id, ...list.filter(x => x !== id)].slice(0, max); }

// ---- quick open ----------------------------------------------------------------------------------------------------

/**
 * Fuzzy match: every query character in order, ignoring case and spaces. Returns { score, positions } or null.
 * Matches in the file name, at the start of a path segment or word, and runs of consecutive characters score higher.
 */
export function fuzzyMatch(query, path) {
  const q = query.replace(/\s+/g, '').toLowerCase();
  if (!q) return { score: 0, positions: [] };
  const p = path.toLowerCase(), base = path.lastIndexOf('/') + 1;
  let best = null;
  for (let start = p.indexOf(q[0]); start >= 0; start = p.indexOf(q[0], start + 1)) {
    const positions = [start];
    for (let k = 1, i = start; k < q.length; k++) { i = p.indexOf(q[k], i + 1); if (i < 0) break; positions.push(i); }
    if (positions.length < q.length) break; // a later start cannot match either
    let score = 0;
    positions.forEach((at, k) => {
      const prev = path[at - 1];
      score += 1;
      if (k > 0 && positions[k - 1] === at - 1) score += 5;
      if (at === 0 || at === base || '/._- '.includes(prev)) score += 4;
      else if (path[at] !== path[at].toLowerCase() && prev === prev.toLowerCase()) score += 3; // camelCase hump
      if (at >= base) score += 2;
    });
    score -= path.length / 1000; // shorter paths win ties
    if (!best || score > best.score) best = { score, positions };
  }
  return best;
}

/**
 * Files for the quick-open list: [{ file, positions, recent }]. With no query, recently opened files come first, then
 * the rest by path. `files` are { id, path }; `recent` is a list of ids, most recent first.
 */
export function rankFiles(query, files, recent = [], limit = 50) {
  const recentIndex = id => { const i = recent.indexOf(id); return i < 0 ? Infinity : i; };
  if (!query.trim()) {
    return [...files].sort((a, b) => recentIndex(a.id) - recentIndex(b.id) || comparePaths(a.path, b.path)).slice(0, limit)
      .map(file => ({ file, positions: [], recent: recent.includes(file.id) }));
  }
  return files.map(file => ({ file, match: fuzzyMatch(query, file.path) })).filter(x => x.match)
    .sort((a, b) => b.match.score - a.match.score || recentIndex(a.file.id) - recentIndex(b.file.id) || comparePaths(a.file.path, b.file.path))
    .slice(0, limit).map(({ file, match }) => ({ file, positions: match.positions, recent: recent.includes(file.id) }));
}

/** Splits text into [{ text, mark }] runs for highlighting the given character positions. */
export function highlightRuns(text, positions) {
  const marked = new Set(positions), runs = [];
  for (let i = 0; i < text.length; i++) {
    const mark = marked.has(i), last = runs.at(-1);
    if (last && last.mark === mark) last.text += text[i]; else runs.push({ text: text[i], mark });
  }
  return runs;
}

// ---- search (the service's SearchProject rules, applied to unsaved drafts in this editor) ----------------------------

/** What the host accepts: 1 to 200 characters, no control characters. */
export const validQuery = q => typeof q === 'string' && q.length > 0 && q.length <= MAX_QUERY_CHARS && !/[\u0000-\u001f\u007f]/.test(q);

// OrdinalIgnoreCase compares upper-cased UTF-16 units; a unit whose upper case is longer (ß) stays as it is, so
// positions never shift.
const fold = s => /^[\x00-\x7f]*$/.test(s) ? s.toUpperCase() : s.replace(/[\s\S]/g, c => { const u = c.toUpperCase(); return u.length === 1 ? u : c; });
const wordChar = (s, i) => i >= 0 && i < s.length && /[\p{L}\p{Nd}_]/u.test(s[i]);
const compareNames = (a, b) => { const x = fold(a), y = fold(b); return x < y ? -1 : x > y ? 1 : 0; };
const comparePaths = (a, b) => compareNames(a, b) || (a < b ? -1 : a > b ? 1 : 0);

function preview(line, at) {
  const start = Math.max(0, Math.min(at - 60, line.length - PREVIEW_CHARS));
  const window = line.substr(start, Math.min(PREVIEW_CHARS, line.length - start));
  if (start > 0) return { preview: '…' + window, previewOffset: at - start + 1 };
  const trimmed = window.trimStart();
  return { preview: trimmed, previewOffset: Math.max(0, at - (window.length - trimmed.length)) };
}

/** Plain-text matches in one text: [{ line, column, length, preview, previewOffset }], 1-based like the service. */
export function searchText(text, query, { matchCase = false, wholeWord = false } = {}, limit = MAX_SEARCH_MATCHES) {
  const matches = [], needle = matchCase ? query : fold(query);
  const lines = text.split('\n');
  for (let n = 0; n < lines.length && matches.length < limit; n++) {
    const line = lines[n].endsWith('\r') ? lines[n].replace(/\r+$/, '') : lines[n];
    const hay = matchCase ? line : fold(line);
    for (let at = hay.indexOf(needle); at >= 0 && matches.length < limit; at = hay.indexOf(needle, at + 1)) {
      if (wholeWord && (wordChar(line, at - 1) || wordChar(line, at + query.length))) continue;
      matches.push({ line: n + 1, column: at + 1, length: query.length, ...preview(line, at) });
    }
  }
  return matches;
}

/**
 * The service searched the saved files; for files with unsaved edits this editor's text is what the user sees, so
 * their matches are replaced by matches in the drafts ({ id, path, text }). Sorted by name, line and column.
 */
export function withDrafts(result, drafts, query, options) {
  const draftIds = new Set(drafts.map(d => d.id));
  const matches = (result.matches ?? []).filter(m => !draftIds.has(m.fileId));
  let truncated = !!result.truncated;
  for (const d of drafts) {
    const found = searchText(d.text, query, options, MAX_SEARCH_MATCHES + 1);
    if (found.length > MAX_SEARCH_MATCHES) truncated = true;
    for (const m of found.slice(0, MAX_SEARCH_MATCHES)) matches.push({ fileId: d.id, name: d.path, ...m, draft: true });
  }
  matches.sort((a, b) => compareNames(a.name, b.name) || a.line - b.line || a.column - b.column);
  if (matches.length > MAX_SEARCH_MATCHES) truncated = true;
  return { matches: matches.slice(0, MAX_SEARCH_MATCHES), truncated, filesSearched: result.filesSearched ?? 0, drafts: drafts.length };
}

/** Matches grouped by file, in order: [{ fileId, name, matches }]. */
export function groupMatches(matches) {
  const groups = [];
  for (const m of matches) {
    if (groups.at(-1)?.fileId !== m.fileId) groups.push({ fileId: m.fileId, name: m.name, matches: [] });
    groups.at(-1).matches.push(m);
  }
  return groups;
}

// ---- C/C++ header and source ---------------------------------------------------------------------------------------

const HEADERS = ['h', 'hpp', 'hh', 'hxx', 'h++', 'inl', 'ipp', 'tpp'], SOURCES = ['cpp', 'cc', 'cxx', 'c++', 'c', 'cppm', 'ixx'];
const split = path => { const slash = path.lastIndexOf('/'), dot = path.lastIndexOf('.'); return dot > slash + 1 ? { folder: path.slice(0, slash + 1).toLowerCase(), stem: path.slice(slash + 1, dot).toLowerCase(), ext: path.slice(dot + 1).toLowerCase() } : null; };

/** The matching header for a source file, or source for a header: same name, same folder first, then anywhere. */
export function counterpart(path, paths) {
  const me = split(path);
  const wanted = !me ? null : HEADERS.includes(me.ext) ? SOURCES : SOURCES.includes(me.ext) ? HEADERS : null;
  if (!wanted) return null;
  const candidates = paths.map(p => ({ p, s: split(p) })).filter(({ p, s }) => p !== path && s && s.stem === me.stem && wanted.includes(s.ext))
    .sort((a, b) => (a.s.folder === me.folder ? 0 : 1) - (b.s.folder === me.folder ? 0 : 1) || wanted.indexOf(a.s.ext) - wanted.indexOf(b.s.ext) || comparePaths(a.p, b.p));
  return candidates[0]?.p ?? null;
}
export const isCFamily = path => { const s = split(path); return !!s && (HEADERS.includes(s.ext) || SOURCES.includes(s.ext)); };

// ---- outlines for C#, C and C++ ---------------------------------------------------------------------------------------
// Monaco has outlines for TypeScript and JavaScript (its language worker) but only highlighting for C# and C/C++. This
// is a best-effort, line-based outline: namespaces, types, functions/methods, C# properties and fields, C/C++ macros.
// It is not a compiler; unusual layouts (several declarations on one line, multi-line raw strings) can be missed.

const NOT_NAMES = new Set(['if', 'for', 'foreach', 'while', 'switch', 'catch', 'return', 'sizeof', 'alignof', 'decltype', 'typeof', 'nameof',
  'new', 'delete', 'using', 'lock', 'fixed', 'default', 'case', 'throw', 'else', 'do', 'static_assert', 'void', 'int', 'char', 'bool', 'float',
  'double', 'long', 'short', 'unsigned', 'signed', 'auto', 'var', 'await', 'yield', 'goto', 'co_return', 'co_await', 'co_yield', 'in', 'is', 'as',
  'checked', 'unchecked', 'stackalloc', 'when', 'and', 'or', 'not', 'operator']);
const NOT_TYPES = new Set(['return', 'else', 'new', 'delete', 'throw', 'case', 'goto', 'await', 'yield', 'using', 'co_return', 'co_await', 'co_yield', 'typedef', 'friend', 'is', 'as', 'in', 'out', 'ref']);
const CALLABLE = new Set(['function', 'method', 'constructor', 'property']);
const TYPES = new Set(['class', 'struct', 'interface', 'enum']);

// Comments, string and character literals and preprocessor lines become spaces, keeping every column where it was.
function blankCode(lines, csharp) {
  let block = false, continued = false;
  return lines.map(line => {
    if (continued || (!block && /^\s*#/.test(line))) { continued = /\\\s*$/.test(line); return ' '.repeat(line.length); }
    let out = '', i = 0;
    while (i < line.length) {
      if (block) {
        const end = line.indexOf('*/', i);
        if (end < 0) { out += ' '.repeat(line.length - i); i = line.length; } else { out += ' '.repeat(end + 2 - i); i = end + 2; block = false; }
        continue;
      }
      const c = line[i], d = line[i + 1];
      if (c === '/' && d === '/') { out += ' '.repeat(line.length - i); break; }
      if (c === '/' && d === '*') { block = true; out += '  '; i += 2; continue; }
      if (c === '"' || c === "'") {
        const verbatim = csharp && c === '"' && /@\$?$|\$@$/.test(line.slice(Math.max(0, i - 2), i));
        let j = i + 1;
        while (j < line.length) {
          if (!verbatim && line[j] === '\\') { j += 2; continue; }
          if (line[j] === c) { if (verbatim && line[j + 1] === c) { j += 2; continue; } break; }
          j++;
        }
        const end = Math.min(j, line.length);
        out += c + ' '.repeat(Math.max(0, end - i - 1)) + (j < line.length ? c : '');
        i = Math.min(j + 1, line.length);
        continue;
      }
      out += c; i++;
    }
    return out;
  });
}

// From a position, the first '{' or ';' (or C# '=>') outside parentheses, looking ahead a few lines.
function terminator(code, line, column) {
  let depth = 0;
  for (let n = line; n < Math.min(code.length, line + 12); n++) {
    for (let i = n === line ? column : 0; i < code[n].length; i++) {
      const c = code[n][i];
      if (c === '(' || c === '[') depth++;
      else if (c === ')' || c === ']') depth = Math.max(0, depth - 1);
      else if (depth === 0) {
        if (c === '{') return '{';
        if (c === ';') return ';';
        if (c === '=' && code[n][i + 1] === '>') return '=>';
      }
    }
  }
  return null;
}

// True when a '{' directly follows the parameter list whose '(' is at `column` (same line, or alone on the next one).
function bodyFollows(code, line, column) {
  let depth = 0;
  for (let n = line; n < Math.min(code.length, line + 12); n++) {
    for (let i = n === line ? column : 0; i < code[n].length; i++) {
      if (code[n][i] === '(') depth++;
      else if (code[n][i] === ')' && --depth === 0) {
        const rest = code[n].slice(i + 1).trim();
        if (rest) return rest.startsWith('{');
        const next = code.slice(n + 1).find(l => l.trim());
        return !!next && next.trim().startsWith('{');
      }
    }
  }
  return false;
}

const CS_MODS = '(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|ref|virtual|override|async|extern|const|volatile|required|event)\\s+)*';
const CS_ATTRS = '(?:\\[[^\\]]*\\]\\s*)*';
const CS_TYPE = '[\\w.]+(?:\\s*<[^()]*?>)?(?:\\s*\\[[,\\s]*\\])*\\??';
const RE = {
  csNamespace: /^\s*namespace\s+([\w.]+)/d,
  csType: new RegExp(`^\\s*${CS_ATTRS}${CS_MODS}(class|struct|interface|enum|record(?:\\s+(?:class|struct))?)\\s+([A-Za-z_]\\w*)`, 'd'),
  csMethod: new RegExp(`^\\s*${CS_ATTRS}${CS_MODS}(${CS_TYPE}\\s+)?(~?[A-Za-z_]\\w*)\\s*(?:<[^()]*>)?\\s*\\(`, 'd'),
  csProperty: new RegExp(`^\\s*${CS_ATTRS}${CS_MODS}(${CS_TYPE})\\s+([A-Za-z_]\\w*)\\s*(?:\\{|=>|$)`, 'd'),
  csField: new RegExp(`^\\s*${CS_ATTRS}${CS_MODS}(${CS_TYPE})\\s+([A-Za-z_]\\w*)\\s*(?:;|,|=(?!>))`, 'd'),
  cppNamespace: /^\s*(?:inline\s+)?namespace\b\s*([A-Za-z_][\w:]*)?/d,
  cppType: /^\s*(?:template\s*<.*>\s*)?(?:typedef\s+)?(class|struct|union|enum(?:\s+(?:class|struct))?)\s+(?:(?:\[\[[^\]]*\]\]|alignas\s*\([^)]*\)|__declspec\s*\([^)]*\)|[A-Z_][A-Z0-9_]*(?=\s+\w))\s*)*([A-Za-z_]\w*)/d,
  cppFunction: /^\s*(?:template\s*<.*>\s*)?((?:[\w:<>,*&[\]]+[\s*&]+)*)(~?[A-Za-z_]\w*(?:::~?[A-Za-z_]\w*)*|(?:[A-Za-z_]\w*::)*operator\s*(?:\(\)|[^\s(]+))\s*\(/d,
  define: /^\s*#\s*define\s+([A-Za-z_]\w*)/d,
};

/**
 * The outline of a C#, C or C++ file: a tree of { name, kind, line, column, endLine, endColumn, nameColumn, nameLength,
 * children }, with 1-based lines and columns. Kinds: namespace, class, struct, interface, enum, function, method,
 * constructor, property, field, macro.
 */
export function outline(language, text) {
  const csharp = language === 'csharp';
  if (!csharp && language !== 'cpp' && language !== 'c') return [];
  const lines = text.split('\n').map(l => l.replace(/\r$/, ''));
  const code = blankCode(lines, csharp);
  const root = [], frames = []; // frames: one per open '{': { symbol, code }
  let pending = null, fileNamespace = null;
  const inCode = () => frames.at(-1)?.code ?? false;
  const owner = () => { for (let i = frames.length - 1; i >= 0; i--) if (frames[i].symbol) return frames[i].symbol; return fileNamespace; };
  const enclosingType = () => { const o = owner(); return o && TYPES.has(o.kind) ? o : null; };
  const add = (name, kind, n, match, group) => {
    const [from, to] = match.indices[group];
    const symbol = { name, kind, line: n + 1, column: (code[n].search(/\S/) + 1) || 1, endLine: n + 1, endColumn: lines[n].length + 1, nameColumn: from + 1, nameLength: to - from, children: [] };
    (owner()?.children ?? root).push(symbol);
    return symbol;
  };

  for (let n = 0; n < lines.length; n++) {
    const line = code[n];
    let m;
    if (!csharp && !inCode() && (m = RE.define.exec(lines[n]))) add(m[1], 'macro', n, m, 1);
    else if (!inCode() && line.trim()) {
      if (csharp) {
        if ((m = RE.csNamespace.exec(line))) {
          const end = terminator(code, n, m.indices[0][1]);
          const symbol = add(m[1], 'namespace', n, m, 1);
          if (end === ';') { fileNamespace = symbol; symbol.endLine = lines.length; } else if (end === '{') pending = { symbol, code: false };
        } else if ((m = RE.csType.exec(line))) {
          const end = terminator(code, n, m.indices[0][1]);
          const kind = m[1].startsWith('record') ? (m[1].includes('struct') ? 'struct' : 'class') : m[1];
          const symbol = add(m[2], kind, n, m, 2);
          if (end === '{') pending = { symbol, code: false };
        } else if ((m = RE.csMethod.exec(line)) && !NOT_NAMES.has(m[2]) && (m[1] ? !NOT_TYPES.has(m[1].trim()) : (m[2] === enclosingType()?.name || m[2].startsWith('~')))) {
          const type = enclosingType();
          const end = terminator(code, n, m.indices[0][1] - 1);
          if (end) {
            const symbol = add(m[2], !type ? 'function' : (!m[1] || m[2].startsWith('~')) ? 'constructor' : 'method', n, m, 2);
            if (end === '{') pending = { symbol, code: true };
          }
        } else if ((m = RE.csProperty.exec(line)) && enclosingType() && !NOT_TYPES.has(m[1]) && !NOT_NAMES.has(m[2])) {
          add(m[2], 'property', n, m, 2);
          if (terminator(code, n, m.indices[2][1]) === '{') pending = { symbol: null, code: true };
        } else if ((m = RE.csField.exec(line)) && enclosingType() && !NOT_TYPES.has(m[1]) && !NOT_NAMES.has(m[2]) && enclosingType().kind !== 'enum') {
          add(m[2], 'field', n, m, 2);
        }
      } else if ((m = RE.cppNamespace.exec(line)) && terminator(code, n, m.indices[0][1]) === '{') {
        pending = { symbol: add(m[1] ?? '(anonymous namespace)', 'namespace', n, m, m[1] ? 1 : 0), code: false };
      } else if ((m = RE.cppType.exec(line))) {
        if (terminator(code, n, m.indices[0][1]) === '{') pending = { symbol: add(m[2], m[1].startsWith('enum') ? 'enum' : m[1] === 'class' ? 'class' : 'struct', n, m, 2), code: false };
      } else if ((m = RE.cppFunction.exec(line))) {
        const name = m[2], last = name.split('::').at(-1), prefix = m[1].trim().split(/[\s*&]+/).filter(Boolean);
        const type = enclosingType();
        const end = terminator(code, n, m.indices[0][1] - 1);
        const qualifiedCtor = name.includes('::') && name.split('::').at(-2) === last.replace('~', '');
        const ctor = qualifiedCtor || (type && last.replace('~', '') === type.name);
        // With no return type it must be a constructor, a qualified definition, or a body right after the parameters
        // (TEST(...) { ... }); otherwise it is a macro call such as Q_PROPERTY(...).
        const plausible = !NOT_NAMES.has(last) && !prefix.some(t => NOT_TYPES.has(t))
          && (prefix.length > 0 || ctor || (name.includes('::') && end === '{') || bodyFollows(code, n, m.indices[0][1] - 1));
        if (plausible && (end === '{' || (end === ';' && (prefix.length > 0 || ctor)))) {
          const symbol = add(name, ctor ? 'constructor' : type || name.includes('::') ? 'method' : 'function', n, m, 2);
          if (end === '{') pending = { symbol, code: true };
        }
      }
    }
    for (let i = 0; i < line.length; i++) {
      if (line[i] === '{') {
        if (pending) { frames.push(pending); pending = null; }
        else frames.push({ symbol: null, code: inCode() });
      } else if (line[i] === '}') {
        const frame = frames.pop();
        if (frame?.symbol) { frame.symbol.endLine = n + 1; frame.symbol.endColumn = i + 2; }
      }
    }
  }
  return root;
}
