// Three-way line merge for the vault editor (docs/files-history-builds-design.md, section 3).
//
// Pure ES module, no dependencies, no DOM. merge3(base, mine, theirs) splits the three texts into lines (each keeping
// its line ending), diffs base against each side with Myers' algorithm, and walks the stable lines common to all
// three. Between two stable lines a change on one side only is taken; the same change on both sides is taken once;
// different changes to the same lines are a conflict for the person to resolve.

/** Splits text into lines that keep their line endings, so joining them gives back the exact text. */
export function splitLines(text) {
  const lines = [];
  let start = 0;
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) { lines.push(text.slice(start, i + 1)); start = i + 1; }
  if (start < text.length) lines.push(text.slice(start));
  return lines;
}

/**
 * Longest common subsequence of two line arrays as [indexInA, indexInB] pairs, increasing in both. Myers' O((N+M)D)
 * algorithm after trimming the common prefix and suffix; the trace keeps only the visited diagonals, O(D²) memory.
 */
export function matchLines(a, b) {
  const pairs = [];
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) { pairs.push([start, start]); start++; }
  let endA = a.length, endB = b.length;
  const suffix = [];
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; suffix.push([endA, endB]); }
  const n = endA - start, m = endB - start;
  if (n > 0 && m > 0) {
    // Lines become small integers so the inner loop compares numbers.
    const ids = new Map();
    const id = line => { let v = ids.get(line); if (v === undefined) { v = ids.size; ids.set(line, v); } return v; };
    const x = Int32Array.from({ length: n }, (_, i) => id(a[start + i]));
    const y = Int32Array.from({ length: m }, (_, i) => id(b[start + i]));
    const max = n + m, offset = max + 1, v = new Int32Array(2 * max + 3), trace = [];
    let found = -1;
    for (let d = 0; d <= max && found < 0; d++) {
      for (let k = -d; k <= d; k += 2) {
        let i = (k === -d || (k !== d && v[offset + k - 1] < v[offset + k + 1])) ? v[offset + k + 1] : v[offset + k - 1] + 1;
        let j = i - k;
        while (i < n && j < m && x[i] === y[j]) { i++; j++; }
        v[offset + k] = i;
        if (i >= n && j >= m) { found = d; break; }
      }
      if (found < 0) trace.push(v.slice(offset - d, offset + d + 1)); // furthest reach on diagonals -d..d after step d
    }
    // Walk back from (n, m): each step undoes one insertion or deletion, preceded by a run of matching lines.
    const middle = [];
    let i = n, j = m;
    for (let d = found; d > 0; d--) {
      const previous = trace[d - 1];
      const at = k => previous[k + d - 1];
      const k = i - j;
      const prevK = (k === -d || (k !== d && at(k - 1) < at(k + 1))) ? k + 1 : k - 1;
      const prevI = at(prevK), prevJ = prevI - prevK;
      while (i > prevI && j > prevJ) { i--; j--; middle.push([start + i, start + j]); }
      i = prevI; j = prevJ;
    }
    while (i > 0 && j > 0) { i--; j--; middle.push([start + i, start + j]); }
    pairs.push(...middle.reverse());
  }
  pairs.push(...suffix.reverse());
  return pairs;
}

const same = (p, q) => p.length === q.length && p.every((line, i) => line === q[i]);

/**
 * Merges `mine` and `theirs`, both edited from `base`. Returns { segments, conflicts }: segments are
 * { kind: 'ok', lines } or { kind: 'conflict', base, mine, theirs } (line arrays); conflicts counts the latter.
 */
export function merge3(base, mine, theirs) {
  const b = splitLines(base), m = splitLines(mine), t = splitLines(theirs);
  const toMine = new Int32Array(b.length).fill(-1), toTheirs = new Int32Array(b.length).fill(-1);
  for (const [i, j] of matchLines(b, m)) toMine[i] = j;
  for (const [i, j] of matchLines(b, t)) toTheirs[i] = j;
  const segments = [];
  const ok = lines => { if (!lines.length) return; const last = segments.at(-1); if (last?.kind === 'ok') last.lines.push(...lines); else segments.push({ kind: 'ok', lines: [...lines] }); };
  let bi = 0, mi = 0, ti = 0, conflicts = 0;
  for (;;) {
    let k = bi;
    while (k < b.length && (toMine[k] < 0 || toTheirs[k] < 0)) k++;
    const mEnd = k < b.length ? toMine[k] : m.length, tEnd = k < b.length ? toTheirs[k] : t.length;
    const baseChunk = b.slice(bi, k), mineChunk = m.slice(mi, mEnd), theirsChunk = t.slice(ti, tEnd);
    if (same(mineChunk, theirsChunk)) ok(mineChunk);           // unchanged, or the same change on both sides
    else if (same(mineChunk, baseChunk)) ok(theirsChunk);      // only they changed this
    else if (same(theirsChunk, baseChunk)) ok(mineChunk);      // only you changed this
    else { segments.push({ kind: 'conflict', base: baseChunk, mine: mineChunk, theirs: theirsChunk }); conflicts++; }
    if (k >= b.length) break;
    ok([b[k]]);
    bi = k + 1; mi = mEnd + 1; ti = tEnd + 1;
  }
  return { segments, conflicts };
}

/**
 * The merged text, with each conflict resolved by `choices[n]` ('mine' | 'theirs' | 'both', in conflict order).
 * Throws if a conflict has no choice. 'both' keeps your lines, then theirs, adding a line break between if needed.
 */
export function resolve(segments, choices) {
  let n = 0, out = '';
  for (const segment of segments) {
    if (segment.kind === 'ok') { out += segment.lines.join(''); continue; }
    const choice = choices[n++];
    if (choice === 'mine') out += segment.mine.join('');
    else if (choice === 'theirs') out += segment.theirs.join('');
    else if (choice === 'both') {
      const mineText = segment.mine.join('');
      out += mineText + (mineText && !mineText.endsWith('\n') && segment.theirs.length ? '\n' : '') + segment.theirs.join('');
    } else throw new Error('Unresolved conflict');
  }
  return out;
}
