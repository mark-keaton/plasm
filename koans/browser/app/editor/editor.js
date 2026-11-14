// The koan editor: CodeMirror 6 with the course's F# colouring, blanks marked,
// line numbers, completions from the F# compiler (through the page's engine)
// and an optional Vim mode, loaded only when it is turned on. Bundled into the
// site as editor.js (and a chunk for Vim) by stage.sh.

let vimModule = null;
const loadVim = async () => (vimModule ??= await import('@replit/codemirror-vim'));
import { EditorState, Compartment, RangeSetBuilder, Annotation } from '@codemirror/state';
import { EditorView, ViewPlugin, Decoration, keymap, lineNumbers, drawSelection, highlightActiveLine } from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { autocompletion, completionKeymap, startCompletion } from '@codemirror/autocomplete';
import { indentUnit, indentService } from '@codemirror/language';
import { tokens } from '../Koans.Browser.Host/wwwroot/fsharp.js';

const marks = {};
const markFor = (cls) => (marks[cls] ??= cls === 'blank'
  ? Decoration.mark({ tagName: 'mark', class: 'blank' })
  : Decoration.mark({ class: cls }));

/// Colours the code with the same classes as the rest of the page.
const colouring = ViewPlugin.fromClass(class {
  constructor(view) { this.decorations = this.build(view); }
  update(u) { if (u.docChanged) this.decorations = this.build(u.view); }
  build(view) {
    const b = new RangeSetBuilder();
    for (const t of tokens(view.state.doc.toString())) b.add(t.from, t.to, markFor(t.cls));
    return b.finish();
  }
}, { decorations: (p) => p.decorations });

// The compiler's glyphs, as CodeMirror's completion types (which pick the icon).
const typeOf = (glyph) => {
  const g = glyph.toLowerCase();
  if (g.includes('method') || g.includes('extensionmethod')) return 'method';
  if (g.includes('property') || g.includes('field')) return 'property';
  if (g.includes('function')) return 'function';
  if (g.includes('module') || g.includes('namespace')) return 'namespace';
  if (g.includes('union') || g.includes('enummember')) return 'enum';
  if (g.includes('class') || g.includes('struct') || g.includes('type') || g.includes('record') || g.includes('interface')) return 'type';
  if (g.includes('variable') || g.includes('value')) return 'variable';
  if (g.includes('keyword')) return 'keyword';
  return 'text';
};

/// How far a new line is indented: one level more after a line that opens a
/// block (=, ->, do, then, else or an open bracket), else as deep as the
/// deeper of the lines around it (so O and o keep the indent, as in Vim).
const fsharpIndent = indentService.of((context, pos) => {
  // Lines are read through the context, which sees a line break about to be made.
  const line = context.lineAt(pos, 1);
  const depth = (text) => text.length - text.trimStart().length;
  const near = (from, step) => {
    let at = from;
    while (step < 0 ? at > 0 : at < context.state.doc.length) {
      const l = context.lineAt(at, step);
      if (l.text.trim()) return l.text;
      at = step < 0 ? l.from - 1 : l.to + 1;
    }
    return null;
  };
  const above = line.from > 0 ? near(line.from - 1, -1) : null;
  // Below: the lines after the one the break is made in (or the line indented).
  const after = context.state.doc.lineAt(pos).to;
  const below = after < context.state.doc.length ? near(after + 1, 1) : null;
  if (above !== null && /(=|->|\bdo|\bthen|\belse|[([{])\s*$/.test(above.replace(/\/\/.*$/, ''))) return depth(above) + 4;
  return Math.max(above === null ? 0 : depth(above), below === null ? 0 : depth(below));
});

/// Marks a change made by the page (a koan being shown), not by the learner.
const fromPage = Annotation.define();

/// Creates the editor in `parent`. `complete(code, offset)` answers what can
/// follow the cursor; `onEdit(code)` hears the learner's changes; `onRun()`
/// runs the koan; `onVimMode(mode)` hears 'normal', 'insert', 'visual' or null.
export function createEditor(parent, { complete, onEdit, onRun, onVimMode }) {
  const vimSlot = new Compartment();
  let vimOn = false;

  const source = async (context) => {
    const word = context.matchBefore(/[A-Za-z_][\w']*$/);
    const afterDot = context.state.sliceDoc(word ? word.from - 1 : context.pos - 1, word ? word.from : context.pos) === '.';
    if (!context.explicit && !afterDot && !(word && word.to - word.from >= 2)) return null;
    const items = await complete(context.state.doc.toString(), context.pos);
    if (context.aborted || !items?.length) return null;
    return {
      from: word ? word.from : context.pos,
      options: items.map((i) => ({ label: i.name, type: typeOf(i.kind), detail: i.detail })),
      validFor: /^[A-Za-z_][\w']*$/,
    };
  };

  // Ctrl/Cmd+[ and ] move between koans (the page handles them), so the
  // editor's own indent commands on those keys are left out.
  const keys = defaultKeymap.filter((b) => b.key !== 'Mod-[' && b.key !== 'Mod-]');

  const view = new EditorView({
    parent,
    state: EditorState.create({
      doc: '',
      extensions: [
        vimSlot.of([]),
        lineNumbers(),
        history(),
        drawSelection(),
        highlightActiveLine(),
        indentUnit.of('    '),
        fsharpIndent,
        EditorState.tabSize.of(4),
        colouring,
        autocompletion({ override: [source], activateOnTyping: true, icons: true }),
        keymap.of([
          { key: 'Mod-Enter', run: () => { onRun(); return true; } },
          { key: 'Ctrl-Enter', run: () => { onRun(); return true; } },
          { key: 'Ctrl-Space', run: startCompletion },
          ...completionKeymap,
          ...keys,
          ...historyKeymap,
          indentWithTab,
        ]),
        EditorView.updateListener.of((u) => {
          if (u.docChanged && !u.transactions.some((t) => t.annotation(fromPage))) onEdit(u.state.doc.toString());
        }),
        EditorView.contentAttributes.of({ 'aria-label': 'Koan code', spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off' }),
      ],
    }),
  });

  const watchVim = () => {
    const cm = vimModule.getCM(view);
    if (!cm) return;
    onVimMode('normal');
    cm.on('vim-mode-change', (e) => onVimMode(e.mode));
  };

  return {
    view,
    get: () => view.state.doc.toString(),
    /// Shows new code (a koan, or its saved edit): not an edit by the learner.
    set(code) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code }, annotations: fromPage, selection: { anchor: 0 } });
    },
    /// Replaces the code as if the learner typed it.
    type(code) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code }, userEvent: 'input' });
    },
    select(from, to = from) { view.dispatch({ selection: { anchor: from, head: to }, scrollIntoView: true }); },
    selection() { const r = view.state.selection.main; return { from: r.from, to: r.to, head: r.head }; },
    focus() { view.focus(); },
    hasFocus() { return view.hasFocus; },
    get vimOn() { return vimOn; },
    async setVim(on) {
      vimOn = on;
      if (on) await loadVim();
      if (vimOn !== on) return; // turned off again while loading
      view.dispatch({ effects: vimSlot.reconfigure(on ? vimModule.vim() : []) });
      if (on) watchVim(); else onVimMode(null);
    },
    /// In Vim mode: Normal mode with the cursor at `pos` (a newly opened koan).
    normalAt(pos) {
      if (!vimOn || !vimModule) return;
      const cm = vimModule.getCM(view);
      if (cm?.state.vim?.insertMode) vimModule.Vim.exitInsertMode(cm);
      view.dispatch({ selection: { anchor: pos } });
      onVimMode('normal');
    },
  };
}
