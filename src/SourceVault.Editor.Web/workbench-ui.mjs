// Vault-mode workbench UI: go to file (Ctrl+P), recently opened (Ctrl+E), search all files (Ctrl+Shift+F), editor
// settings (Ctrl+,) and the shortcut list (Ctrl+K Ctrl+S). Built with textContent only: file names and search
// previews are never parsed as HTML. vault-app.mjs owns the session, the editors and the models and passes in `ctx`:
// { session, act, available(), openFile(id, match?), goToLine(n), runEditorAction(id), editor(), recent(), typeIcon(language),
//   applySettings(settings, retab), showSidebar(name) }.
import { $, announce } from './shell.mjs';
import { rankFiles, highlightRuns, groupMatches, validQuery, MAX_SEARCH_MATCHES, DEFAULT_SETTINGS, validSettings, SHORTCUTS } from './workbench.mjs';

const plural = (n, word) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;

function appendRuns(element, text, positions) {
  for (const run of highlightRuns(text, positions)) {
    if (run.mark) { const mark = document.createElement('mark'); mark.textContent = run.text; element.append(mark); } else element.append(run.text);
  }
}
const span = (className, text) => { const s = document.createElement('span'); s.className = className; s.textContent = text; return s; };

export function createWorkbench(ctx) {
  const { session } = ctx;

  // ---- go to file / recently opened -----------------------------------------------------------------------------
  let palette = null; // { mode: files | recent, items, index }
  function openPalette(mode = 'files') {
    if (!ctx.available()) return;
    closeDialog();
    if (mode === 'recent' && !ctx.recent().some(id => session.files.has(id))) { announce('No recently opened files yet.'); return; }
    palette = { mode, items: [], index: 0 };
    $('palette').hidden = false;
    $('palette-title').textContent = mode === 'recent' ? 'RECENTLY OPENED' : 'GO TO FILE';
    const input = $('palette-input');
    input.value = ''; input.placeholder = mode === 'recent' ? 'Filter recently opened files' : 'File name';
    refreshPalette();
    // The open file is listed first; start on the one before it, so Enter switches back and forth.
    if (palette.items.length > 1 && palette.items[0].file.id === session.activeId) { palette.index = 1; refreshPalette(); }
    input.focus();
  }
  function closePalette(refocus = true) {
    if (!palette) return;
    palette = null; $('palette').hidden = true;
    if (refocus) ctx.editor()?.focus();
  }
  function refreshPalette() {
    const query = $('palette-input').value, special = /^[:@>]/.test(query);
    const recent = ctx.recent();
    const files = [...session.files.values()].filter(f => palette.mode !== 'recent' || recent.includes(f.id)).map(f => ({ id: f.id, path: f.path, language: f.language }));
    palette.items = special ? [] : rankFiles(query, files, recent);
    palette.index = Math.min(palette.index, Math.max(0, palette.items.length - 1));
    const list = $('palette-list'); list.replaceChildren();
    palette.items.forEach((item, i) => {
      const { file, positions } = item, base = file.path.lastIndexOf('/') + 1;
      const li = document.createElement('li'); li.id = `palette-item-${i}`; li.setAttribute('role', 'option');
      li.className = `palette-item${i === palette.index ? ' selected' : ''}`; li.setAttribute('aria-selected', String(i === palette.index));
      const name = span('palette-name', ''); appendRuns(name, file.path.slice(base), positions.filter(p => p >= base).map(p => p - base));
      const folder = span('palette-folder', ''); appendRuns(folder, file.path.slice(0, Math.max(0, base - 1)), positions.filter(p => p < base - 1));
      li.append(span('file-type', ctx.typeIcon(file.language)), name, folder);
      if (session.dirty(file.id)) li.append(span('palette-tag dirty', '● unsaved'));
      else if (item.recent && palette.mode !== 'recent') li.append(span('palette-tag', 'recently opened'));
      li.onmousedown = event => event.preventDefault(); // keep focus in the input
      li.onclick = () => { palette.index = i; choose(); };
      list.append(li);
    });
    $('palette-input').setAttribute('aria-activedescendant', palette.items.length ? `palette-item-${palette.index}` : '');
    list.querySelector('.selected')?.scrollIntoView({ block: 'nearest' });
    const hint = $('palette-hint'); hint.replaceChildren();
    if (query.startsWith(':')) hint.textContent = /^:\d+$/.test(query) ? `Press Enter to go to line ${query.slice(1)}.` : 'Type a line number.';
    else if (!palette.items.length) hint.textContent = query ? 'No file matches.' : 'No files.';
    else {
      hint.append('↑ ↓ to choose · Enter to open · Esc to close');
      if (palette.mode === 'files') for (const [key, text] of [[':', 'line'], ['@', 'symbol'], ['>', 'command']]) { const k = document.createElement('kbd'); k.textContent = key; hint.append(' · ', k, ` ${text}`); }
    }
  }
  function choose() {
    const query = $('palette-input').value;
    if (query.startsWith(':')) { if (/^:\d+$/.test(query)) { closePalette(); ctx.goToLine(Number(query.slice(1))); } return; }
    const item = palette?.items[palette.index];
    if (!item) return;
    closePalette(false);
    ctx.openFile(item.file.id);
  }
  $('palette-input').oninput = () => {
    const query = $('palette-input').value;
    // '@' lists the file's symbols and '>' all commands, as in Visual Studio Code.
    if (palette.mode === 'files' && (query === '@' || query === '>')) { closePalette(); ctx.runEditorAction(query === '@' ? 'editor.action.quickOutline' : 'editor.action.quickCommand'); return; }
    palette.index = 0; refreshPalette();
  };
  $('palette-input').onkeydown = event => {
    if (!palette) return;
    const count = palette.items.length;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); if (count) { palette.index = (palette.index + (event.key === 'ArrowDown' ? 1 : count - 1)) % count; refreshPalette(); } }
    else if (event.key === 'Enter') { event.preventDefault(); choose(); }
    else if (event.key === 'Escape') { event.preventDefault(); closePalette(); }
    else if (event.key === 'Tab') event.preventDefault();
  };
  document.addEventListener('mousedown', event => { if (palette && !$('palette').contains(event.target)) closePalette(false); }, true);

  // ---- search all files ----------------------------------------------------------------------------------------
  let search = { matchCase: false, wholeWord: false, result: null, query: '', collapsed: new Set() };
  function showSearch() {
    if (!ctx.available()) return;
    ctx.showSidebar('search');
    const input = $('search-input');
    const selected = ctx.editor()?.getModel() ? ctx.editor().getModel().getValueInRange(ctx.editor().getSelection()) : '';
    if (selected && !selected.includes('\n') && validQuery(selected)) input.value = selected; // like Visual Studio Code
    renderSearch();
    input.focus(); input.select();
  }
  function runSearch() {
    const query = $('search-input').value;
    if (!query) { search.result = null; renderSearch(); return; }
    if (!validQuery(query)) { announce('Search for 1 to 200 characters on one line.'); return; }
    const options = { matchCase: search.matchCase, wholeWord: search.wholeWord };
    ctx.act(async () => {
      const result = await session.search(query, options);
      search = { ...search, query, result, collapsed: new Set() };
      const files = groupMatches(result.matches).length;
      return result.matches.length ? `Found ${plural(result.matches.length, 'result')} in ${plural(files, 'file')}.` : 'No results.';
    }, renderSearch);
  }
  function renderSearch() {
    const available = ctx.available();
    $('search-input').disabled = !available;
    for (const [id, on] of [['search-case', search.matchCase], ['search-word', search.wholeWord]]) { $(id).setAttribute('aria-pressed', String(on)); $(id).disabled = !available; }
    const results = $('search-results'); results.replaceChildren();
    const r = search.result;
    if (!r || !available) {
      $('search-summary').textContent = available ? 'Press Enter to search. Plain text, in the saved files and in your unsaved edits.' : '';
      return;
    }
    const groups = groupMatches(r.matches);
    $('search-summary').textContent = !r.matches.length ? `No results for "${search.query}" in ${plural(r.filesSearched, 'file')}.`
      : `${plural(r.matches.length, 'result')} in ${plural(groups.length, 'file')}` + (r.truncated ? ` · only the first ${MAX_SEARCH_MATCHES} are shown; search for something more specific` : '') + '.';
    if (r.filesSkipped) $('search-summary').textContent += ` ${plural(r.filesSkipped, 'file')} skipped (binary or too large).`;
    for (const group of groups) {
      const file = session.files.get(group.fileId);
      const head = document.createElement('button'); head.type = 'button'; head.className = 'search-file'; head.title = group.name;
      const collapsed = search.collapsed.has(group.fileId);
      head.setAttribute('aria-expanded', String(!collapsed));
      head.append(span('', collapsed ? '›' : '⌄'), span('file-type', ctx.typeIcon(file?.language ?? 'plaintext')), span('search-name', group.name));
      if (group.matches[0].draft) head.append(span('search-draft', 'unsaved'));
      head.append(span('count', String(group.matches.length)));
      head.onclick = () => { if (collapsed) search.collapsed.delete(group.fileId); else search.collapsed.add(group.fileId); renderSearch(); };
      results.append(head);
      if (collapsed) continue;
      for (const m of group.matches) {
        const row = document.createElement('button'); row.type = 'button'; row.className = 'search-match';
        row.title = `${group.name}, line ${m.line}, column ${m.column}`;
        const text = span('search-text', '');
        const at = Math.min(m.previewOffset, m.preview.length), end = Math.min(at + m.length, m.preview.length);
        const mark = document.createElement('mark'); mark.textContent = m.preview.slice(at, end);
        text.append(m.preview.slice(0, at), mark, m.preview.slice(end));
        row.append(span('search-line', String(m.line)), text);
        row.onclick = () => ctx.openFile(m.fileId, m);
        results.append(row);
      }
    }
  }
  $('search-form').onsubmit = event => { event.preventDefault(); runSearch(); };
  $('search-input').onkeydown = event => { if (event.key === 'Enter') { event.preventDefault(); runSearch(); } else if (event.key === 'Escape') { event.preventDefault(); ctx.editor()?.focus(); } };
  $('search-case').onclick = () => { search.matchCase = !search.matchCase; if (search.result) runSearch(); else renderSearch(); };
  $('search-word').onclick = () => { search.wholeWord = !search.wholeWord; if (search.result) runSearch(); else renderSearch(); };

  // ---- dialogs: settings and shortcuts ----------------------------------------------------------------------------
  let dialog = null, settings = { ...DEFAULT_SETTINGS }, saved = { ...DEFAULT_SETTINGS };
  function showDialog(id) {
    closePalette(false); closeDialog();
    dialog = id; $('dialog-backdrop').hidden = false; $(id).hidden = false;
    $(id).querySelector('select, input, button')?.focus();
  }
  function closeDialog() {
    if (!dialog) return;
    if (dialog === 'settings-dialog') { // closing without Save puts them back
      const retab = settings.tabSize !== saved.tabSize || settings.insertSpaces !== saved.insertSpaces;
      settings = { ...saved }; ctx.applySettings(settings, retab);
    }
    $(dialog).hidden = true; $('dialog-backdrop').hidden = true; dialog = null;
    ctx.editor()?.focus();
  }
  $('dialog-backdrop').onclick = closeDialog;
  for (const id of ['settings-dialog', 'shortcuts-dialog']) $(id).addEventListener('keydown', event => { if (event.key === 'Escape') { event.preventDefault(); closeDialog(); } });

  const fields = { theme: ['set-theme', 'value'], fontSize: ['set-font', 'value'], tabSize: ['set-tab', 'value'], insertSpaces: ['set-spaces', 'checked'],
    wordWrap: ['set-wrap', 'checked'], minimap: ['set-minimap', 'checked'], renderWhitespace: ['set-whitespace', 'checked'], lineNumbers: ['set-lines', 'checked'] };
  function fillSettings() { for (const [key, [id, prop]] of Object.entries(fields)) $(id)[prop] = prop === 'value' ? String(settings[key]) : settings[key]; }
  function readSettings() {
    const next = {};
    for (const [key, [id, prop]] of Object.entries(fields)) next[key] = prop === 'checked' ? $(id).checked : key === 'theme' ? $(id).value : Number($(id).value);
    next.fontSize = Math.min(28, Math.max(10, Math.round(next.fontSize) || DEFAULT_SETTINGS.fontSize));
    return next;
  }
  function openSettings() { fillSettings(); showDialog('settings-dialog'); }
  $('settings-form').oninput = $('settings-form').onchange = () => {
    const next = readSettings();
    const retab = next.tabSize !== settings.tabSize || next.insertSpaces !== settings.insertSpaces;
    settings = next; ctx.applySettings(settings, retab); // live preview; Save keeps it
  };
  $('settings-defaults').onclick = () => {
    const retab = settings.tabSize !== DEFAULT_SETTINGS.tabSize || settings.insertSpaces !== DEFAULT_SETTINGS.insertSpaces;
    settings = { ...DEFAULT_SETTINGS }; fillSettings(); ctx.applySettings(settings, retab);
  };
  $('settings-cancel').onclick = closeDialog;
  $('settings-form').onsubmit = async event => {
    event.preventDefault();
    settings = readSettings();
    try { await session.saveSettings(settings); saved = { ...settings }; closeDialog(); announce('Editor settings saved for this Windows account.'); }
    catch { announce('The editor settings could not be saved on this computer. They apply until the editor closes.'); saved = { ...settings }; closeDialog(); }
  };
  async function loadSettings() {
    try { const loaded = await session.getSettings(); if (validSettings(loaded)) settings = saved = { ...loaded }; } catch { /* defaults */ }
    ctx.applySettings(settings, false);
  }

  const list = $('shortcuts-list');
  for (const [group, rows] of SHORTCUTS) {
    const section = document.createElement('section');
    const heading = document.createElement('h3'); heading.textContent = group;
    const dl = document.createElement('dl');
    for (const [keys, text] of rows) { const dt = document.createElement('dt'); dt.textContent = keys; const dd = document.createElement('dd'); dd.textContent = text; dl.append(dt, dd); }
    section.append(heading, dl); list.append(section);
  }
  $('shortcuts-close').onclick = closeDialog;

  return {
    openPalette, closePalette, showSearch, renderSearch, openSettings, loadSettings,
    openShortcuts: () => showDialog('shortcuts-dialog'), closeDialog,
    get settings() { return settings; },
    /** On lock: drop search results (they hold text from files that may not be open) and close the overlays. */
    onLock() { search.result = null; closePalette(false); if (dialog === 'shortcuts-dialog') closeDialog(); renderSearch(); },
  };
}
