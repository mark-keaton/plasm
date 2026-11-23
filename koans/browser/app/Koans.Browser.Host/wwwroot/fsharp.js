// A small F# highlighter (the same one as Plasm's website): comments, strings,
// numbers, keywords, query operations and capitalised type names; each blank
// (__) outside comments and strings is marked. Used for the editor and for the
// code shown under Given.

const keywords = new Set(('let let! do do! return return! yield yield! match match! with fun function if then else elif for in to downto ' +
  'while done type of and rec mutable private internal public open module namespace member static new use use! try finally when as ' +
  'not true false null inline inherit interface abstract override default base lazy upcast downcast exception struct assert global ' +
  'task plasm transaction async seq').split(' '));
const operations = new Set(('where orderBy orderByDescending thenBy thenByDescending take skip ' +
  'select join leftJoin on groupBy having').split(' '));
const token = /(\/\/[^\n]*)|("(?:\\.|[^"\\\n])*"?)|(\b\d[\d_]*(?:\.\d+)?(?:L|m|M)?\b)|([A-Za-z_][\w']*!?)/g;

/// The coloured pieces of some F#: { from, to, cls }, where cls is 'c' (comment),
/// 's' (string), 'n' (number), 'k' (keyword), 'f' (query operation), 't' (type)
/// or 'blank' (a __ to fill in). With `words`, every other word outside comments
/// and strings is given too, as 'w'.
export function* tokens(src, words = false) {
  for (const m of src.matchAll(token)) {
    const [text, comment, string, number, word] = m;
    const from = m.index;
    const to = from + text.length;
    let cls = null;
    if (word === '__' && src[from - 1] !== '.') cls = 'blank';
    else if (comment) cls = 'c';
    else if (string) cls = 's';
    else if (number) cls = 'n';
    else if (keywords.has(word)) cls = 'k';
    else if (operations.has(word)) cls = 'f';
    // Capitalised names are types or union cases, except record field labels (Name = …).
    else if (/^[A-Z]/.test(word) && src[from - 1] !== '.' && !/^\s*=(?!=)/.test(src.slice(to))) cls = 't';
    else if (words && word) cls = 'w';
    if (cls) yield { from, to, cls };
  }
}

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/// Some F# as coloured HTML. Given `line` (a line of the code shown under
/// Given), each name in it (a word that is not a keyword, outside comments and
/// strings) is a span.name with data-line and data-to (the column it ends at),
/// for the compiler to be asked what it is.
export function colour(src, line) {
  const names = line !== undefined;
  let out = '';
  let last = 0;
  for (const t of tokens(src, names)) {
    out += escape(src.slice(last, t.from));
    const text = escape(src.slice(t.from, t.to));
    if (t.cls === 'blank') out += '<mark class="blank">__</mark>';
    // The wildcard and a join's `on` are syntax, not names.
    else if (names && (t.cls === 't' || t.cls === 'f' || t.cls === 'w') && text !== '_' && text !== 'on')
      out += `<span class="name${t.cls === 'w' ? '' : ' ' + t.cls}" data-line="${line}" data-to="${t.to}">${text}</span>`;
    else out += `<span class="${t.cls}">${text}</span>`;
    last = t.to;
  }
  return out + escape(src.slice(last));
}

/// Where the first blank to fill in is: the first __ outside comments and strings, or -1.
export function firstBlank(code) {
  for (const t of tokens(code)) if (t.cls === 'blank') return t.from;
  return -1;
}
