// A small Vim mode for the koan editor (a <textarea>): Normal and Insert modes,
// the everyday motions (h j k l w b e 0 ^ $ gg G, with counts) and edits
// (x dd dw de d$ D cw ce c$ cc C r yy p P u Ctrl+R; i a I A o O). Edits go
// through the browser's own editing commands, so undo, the editor's colouring
// and the saved edit all follow them. In Normal mode the cursor is a one-
// character selection, drawn as a block.

const isWord = (c) => /[A-Za-z0-9_]/.test(c);
// The kind of a character, for word motions: none, space, word or punctuation.
const kind = (c) => (c === undefined ? -1 : /\s/.test(c) ? 0 : isWord(c) ? 1 : 2);

export function vim(ta, onMode) {
  let on = false;
  let mode = 'insert';
  let pending = '';
  let count = '';
  let register = { text: '', lines: false };

  const text = () => ta.value;
  const lineStart = (p) => text().lastIndexOf('\n', p - 1) + 1;
  const lineEnd = (p) => { const i = text().indexOf('\n', p); return i < 0 ? text().length : i; };
  const lastChar = (p) => Math.max(lineStart(p), lineEnd(p) - 1);
  const firstNonBlank = (p) => {
    let i = lineStart(p);
    const e = lineEnd(p);
    while (i < e && /[ \t]/.test(text()[i])) i++;
    return i;
  };
  const indentOf = (p) => text().slice(lineStart(p), firstNonBlank(p));
  const pos = () => ta.selectionStart;

  /// Normal mode: the cursor sits on a character, never past the end of a line.
  const place = (p) => {
    p = Math.max(0, Math.min(p, text().length));
    if (p > lineStart(p) && p >= lineEnd(p)) p = lineEnd(p) - 1;
    ta.setSelectionRange(p, p < lineEnd(p) ? p + 1 : p);
  };

  const setMode = (m) => { mode = m; onMode(on ? mode : null); };
  const insertAt = (p) => { setMode('insert'); ta.setSelectionRange(p, p); };

  /// Replaces from..to with str as one undoable edit.
  const edit = (from, to, str) => {
    ta.setSelectionRange(from, to);
    if (str) document.execCommand('insertText', false, str);
    else if (from !== to) document.execCommand('delete');
  };

  // The end (exclusive) of the word under p, for cw and ce.
  const wordEnd = (p) => {
    const t = text();
    const k = kind(t[p]);
    if (k <= 0) return Math.min(p + 1, lineEnd(p));
    let i = p;
    while (i + 1 < t.length && kind(t[i + 1]) === k) i++;
    return i + 1;
  };

  const motions = {
    h: (p) => Math.max(lineStart(p), p - 1),
    l: (p) => Math.min(lastChar(p), p + 1),
    j: (p) => {
      const e = lineEnd(p);
      if (e >= text().length) return p;
      const s = e + 1;
      return Math.min(s + (p - lineStart(p)), Math.max(s, lineEnd(s) - 1));
    },
    k: (p) => {
      const s = lineStart(p);
      if (s === 0) return p;
      const above = lineStart(s - 1);
      return Math.min(above + (p - s), Math.max(above, s - 2));
    },
    w: (p) => {
      const t = text();
      let i = p;
      const k = kind(t[i]);
      if (k > 0) while (i < t.length && kind(t[i]) === k) i++;
      while (i < t.length && kind(t[i]) === 0) i++;
      return i;
    },
    b: (p) => {
      const t = text();
      let i = p - 1;
      while (i > 0 && kind(t[i]) === 0) i--;
      const k = kind(t[i]);
      while (i > 0 && kind(t[i - 1]) === k) i--;
      return Math.max(0, i);
    },
    e: (p) => {
      const t = text();
      let i = p + 1;
      while (i < t.length && kind(t[i]) === 0) i++;
      const k = kind(t[i]);
      while (i + 1 < t.length && kind(t[i + 1]) === k) i++;
      return Math.min(i, Math.max(0, t.length - 1));
    },
    0: lineStart,
    '^': firstNonBlank,
    $: lastChar,
    G: () => firstNonBlank(text().length),
    gg: () => firstNonBlank(0),
  };

  const deleteLine = (p) => {
    const s = lineStart(p);
    const e = lineEnd(p);
    register = { text: text().slice(s, e), lines: true };
    if (e < text().length) { edit(s, e + 1, ''); return s; }
    if (s > 0) { const above = lineStart(s - 1); edit(s - 1, e, ''); return above; }
    edit(s, e, '');
    return 0;
  };

  const paste = (p, before) => {
    if (register.lines) {
      if (before) { const s = lineStart(p); edit(s, s, register.text + '\n'); place(firstNonBlank(s)); }
      else { const e = lineEnd(p); edit(e, e, '\n' + register.text); place(firstNonBlank(e + 1)); }
    } else if (register.text) {
      const at = before || p >= lineEnd(p) ? p : p + 1;
      edit(at, at, register.text);
      place(at + register.text.length - 1);
    }
  };

  /// Runs a key typed in Normal mode.
  const feed = (key) => {
    if (pending === 'r') {
      const p = pos();
      if (p < lineEnd(p)) { edit(p, p + 1, key); place(p); }
      pending = ''; count = '';
      return;
    }
    if (pending === '' && (/[1-9]/.test(key) || (count && key === '0'))) { count += key; return; }
    const n = Math.max(1, parseInt(count || '1', 10));
    const seq = pending + key;
    if (['d', 'c', 'y', 'g', 'r'].includes(seq)) { pending = seq; return; }
    pending = '';
    count = '';
    const p = pos();
    const repeat = (f) => { let q = p; for (let i = 0; i < n; i++) q = f(q); return q; };

    if (motions[seq]) { place(repeat(motions[seq])); return; }
    switch (seq) {
      case 'x': {
        const end = Math.min(p + n, lineEnd(p));
        if (end > p) { register = { text: text().slice(p, end), lines: false }; edit(p, end, ''); }
        place(p);
        return;
      }
      case 'dd': case 'yy': {
        if (seq === 'yy') { register = { text: text().slice(lineStart(p), lineEnd(p)), lines: true }; return; }
        let q = p;
        for (let i = 0; i < n; i++) q = deleteLine(q);
        place(firstNonBlank(q));
        return;
      }
      case 'dw': case 'de': case 'd$': case 'D': {
        const to = seq === 'dw' ? Math.min(repeat(motions.w), lineEnd(p))
          : seq === 'de' ? Math.min(repeat(motions.e) + 1, lineEnd(p))
          : lineEnd(p);
        register = { text: text().slice(p, to), lines: false };
        edit(p, to, '');
        place(p);
        return;
      }
      case 'cw': case 'ce': case 'c$': case 'C': {
        let to = seq === 'c$' || seq === 'C' ? lineEnd(p) : wordEnd(p);
        if (seq === 'cw' || seq === 'ce') for (let i = 1; i < n; i++) to = wordEnd(motions.w(to));
        register = { text: text().slice(p, to), lines: false };
        edit(p, to, '');
        insertAt(p);
        return;
      }
      case 'cc': {
        const s = lineStart(p);
        const indent = indentOf(p);
        edit(s, lineEnd(p), indent);
        insertAt(s + indent.length);
        return;
      }
      case 'i': insertAt(p); return;
      case 'a': insertAt(Math.min(p + 1, lineEnd(p))); return;
      case 'I': insertAt(firstNonBlank(p)); return;
      case 'A': insertAt(lineEnd(p)); return;
      case 'o': {
        const e = lineEnd(p);
        const indent = indentOf(p);
        edit(e, e, '\n' + indent);
        insertAt(e + 1 + indent.length);
        return;
      }
      case 'O': {
        const s = lineStart(p);
        const indent = indentOf(p);
        edit(s, s, indent + '\n');
        insertAt(s + indent.length);
        return;
      }
      case 'p': paste(p, false); return;
      case 'P': paste(p, true); return;
      case 'u': document.execCommand('undo'); place(pos()); return;
      default: // not a command this Vim knows
    }
  };

  const named = { ArrowLeft: 'h', ArrowRight: 'l', ArrowUp: 'k', ArrowDown: 'j', Backspace: 'h', Enter: 'j', Home: '0', End: '$' };

  ta.addEventListener('keydown', (e) => {
    if (!on || e.metaKey || e.altKey) return;
    const escape = e.key === 'Escape' || (e.ctrlKey && e.code === 'BracketLeft');
    if (mode === 'insert') {
      if (!escape) return;
      // Ctrl+[ leaves Insert mode here; it opens the previous koan only from Normal mode.
      e.preventDefault();
      e.stopPropagation();
      const p = pos();
      setMode('normal');
      place(p > lineStart(p) ? p - 1 : p);
      return;
    }
    if (e.ctrlKey) {
      if (e.key === 'r') { e.preventDefault(); document.execCommand('redo'); place(pos()); }
      return; // Ctrl+Enter runs the koan; Ctrl+] and Ctrl+[ move between koans
    }
    if (e.key === 'Escape') { e.preventDefault(); pending = ''; count = ''; return; }
    const key = named[e.key] ?? e.key;
    if (key.length !== 1) return; // Shift, Tab and the like
    e.preventDefault();
    feed(key);
  });
  // A click in Normal mode puts the block cursor where it lands.
  ta.addEventListener('mouseup', () => { if (on && mode === 'normal' && ta.selectionStart === ta.selectionEnd) place(pos()); });

  return {
    get on() { return on; },
    /// Turns Vim mode on (in Normal mode) or off.
    enable(flag) {
      on = flag;
      pending = '';
      count = '';
      if (on) { setMode('normal'); if (document.activeElement === ta) place(pos()); }
      else { setMode('insert'); ta.setSelectionRange(pos(), pos()); }
    },
    /// Normal mode with the cursor at p (a newly opened koan).
    normalAt(p) { if (!on) return; pending = ''; count = ''; setMode('normal'); place(p); },
  };
}
