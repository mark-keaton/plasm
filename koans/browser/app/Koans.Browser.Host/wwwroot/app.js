// The koans page. The engine (PGlite and .NET with the F# compiler) runs in a
// Web Worker, so the page stays responsive while a koan compiles and runs; a
// koan that does not finish is stopped by replacing the worker.
import { createEditor } from './editor.js';
import { colour, firstBlank } from './fsharp.js';

const $ = (id) => document.getElementById(id);
const status = (text) => { $('status').textContent = text; };

// The engine needs WebAssembly; without it, say so and point at the terminal version.
if (typeof WebAssembly !== 'object') {
  $('status').innerHTML = 'The koans run in your browser with WebAssembly, which this browser does not provide. ' +
    'You can <a href="terminal.html">run the koans in a terminal</a> instead.';
  throw new Error('WebAssembly is not available');
}


status('Loading the database, the .NET libraries and the compiler…');
const course = await (await fetch('course.json', { cache: 'no-cache' })).json();

function startEngine() {
  const worker = new Worker('worker.js', { type: 'module' });
  const engine = { worker, pending: new Map() };
  engine.ready = new Promise((resolve, reject) => {
    worker.onmessage = (e) => {
      const m = e.data;
      if (m.type === 'progress') status(`Loading… ${m.what} ready.`);
      else if (m.type === 'ready') { engine.readyMs = m.ms; resolve(engine); }
      else if (m.type === 'failed') reject(new Error(m.message));
      else if (m.type === 'result') { engine.pending.get(m.id)?.(m.result); engine.pending.delete(m.id); }
    };
    worker.onerror = (e) => reject(new Error(e.message || 'the engine stopped'));
  });
  // Said where the learner is looking, once the page can say it.
  engine.ready.catch((e) => { if (typeof cannotLoad === 'function') cannotLoad(e.message); });
  return engine;
}

let engine = startEngine();
let ids = 0;
const results = new Map(); // "lesson/koan" -> result

// Progress and edits are kept in this browser (localStorage).
const store = {
  load(key, fallback) { try { return JSON.parse(localStorage.getItem('plasm-koans:' + key)) ?? fallback; } catch { return fallback; } },
  save(key, value) { try { localStorage.setItem('plasm-koans:' + key, JSON.stringify(value)); } catch { /* private mode */ } },
};
// Each koan's progress is kept by its name, with the code the koan had then:
// name -> { base, passed?, edit? }. A koan whose code has changed since starts
// afresh, and rearranging the course moves nothing onto another koan.
// (Pages before this kept progress by position, under 'passed' and 'edits'.)
try { localStorage.removeItem('plasm-koans:passed'); localStorage.removeItem('plasm-koans:edits'); } catch { /* private mode */ }
const saved = store.load('koans', {});
const keyOf = (l, k) => `${l}/${k}`;
const mac = /Mac|iPhone|iPad/.test(navigator.platform);
const keys = { run: mac ? '⌘↵' : 'Ctrl+↵', next: mac ? '⌘]' : 'Ctrl+]', previous: mac ? '⌘[' : 'Ctrl+[' };
const koanAt = (l, k) => course.lessons[l].koans[k];
const savedFor = (l, k) => { const s = saved[koanAt(l, k).name]; return s && s.base === koanAt(l, k).body ? s : null; };
const isPassed = (l, k) => !!savedFor(l, k)?.passed;
const keep = (l, k, change) => {
  const s = savedFor(l, k) ?? { base: koanAt(l, k).body };
  change(s);
  if (s.passed || s.edit !== undefined) saved[koanAt(l, k).name] = s; else delete saved[koanAt(l, k).name];
  store.save('koans', saved);
};

/// Runs a koan (with the given code, or its original code) in the engine.
async function runKoan(lessonIndex, koanIndex, body) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  const current = await engine.ready;
  const id = ++ids;
  const result = await new Promise((resolve) => {
    current.pending.set(id, resolve);
    current.worker.postMessage({ type: 'run', id, title: lesson.title, name: koan.name, helpers: lesson.helpers,
                                 isTask: koan.isTask, framed: koan.framed, body: body ?? koan.body });
  });
  results.set(keyOf(lessonIndex, koanIndex), result);
  keep(lessonIndex, koanIndex, (s) => { if (result.status === 'passed') s.passed = true; else delete s.passed; });
  updateProgress();
  return result;
}

/// The engine could not start: most often a page open across a deploy, whose
/// files the new site no longer serves. A reload fetches the site as it is now.
function couldNotLoad(why) {
  return `The course could not load (${why}). The site may have been updated while this page was open; reload it to get the current one.`;
}
function reloadButton() {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = 'Reload';
  b.onclick = () => location.reload();
  return b;
}
function cannotLoad(why) {
  $('status').replaceChildren(couldNotLoad(why) + ' ', reloadButton());
}

/// Stops a running koan: the worker is replaced and its pending runs end as stopped.
function stop() {
  const old = engine;
  old.worker.terminate();
  for (const resolve of old.pending.values()) resolve({ status: 'stopped', text: 'Stopped. The engine was restarted.' });
  old.pending.clear();
  engine = startEngine();
}

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const el = (tag, attrs = {}, ...children) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  e.append(...children.filter((c) => c != null));
  return e;
};
const part = (cls, label, text) => el('div', { class: `part ${cls}` }, el('div', { class: 'label' }, label), el('pre', {}, text));

const headlines = {
  passed: () => 'Passed',
  unanswered: (r) => r.blankLine ? `Fill in the blank (__) on line ${r.blankLine}` : 'Fill in the blank (__)',
  failed: () => 'Not yet: the koan does not hold',
  threw: () => 'Not yet: the code threw an exception',
  compile: () => 'The code does not compile',
  stopped: () => 'Stopped. The engine was restarted.',
};

/// Shows a koan's result: what happened, then the detail, the SQL and what it teaches.
function render(r, lessonIndex, koanIndex) {
  const box = $('result');
  box.replaceChildren();
  if (!r) { delete box.dataset.status; return; }
  const koan = course.lessons[lessonIndex].koans[koanIndex];
  const headline = el('div', { class: 'headline' }, el('span', { class: 'badge', 'aria-hidden': 'true' }), (headlines[r.status] ?? (() => r.text))(r));
  const next = following({ lessonIndex, koanIndex });
  if (r.status === 'passed' && next) {
    const button = el('button', { class: 'btn primary', id: 'next' }, `Next koan: ${course.lessons[next.lessonIndex].koans[next.koanIndex].name} →`,
      el('span', { class: 'kbd' }, keys.next));
    button.onclick = () => go(next);
    headline.append(button);
  }
  box.append(headline);
  if (r.status === 'failed') box.append(el('div', { class: 'part compare' }, ...['expected', 'actual'].map((k) =>
    el('div', { class: k }, el('div', { class: 'label' }, k === 'expected' ? 'Expected' : 'Actual'), el('pre', {}, r[k])))));
  if (r.status === 'threw') box.append(part('threw', 'The exception', r.threw));
  if (r.status === 'compile') box.append(part('errors', r.errors.length === 1 ? 'The error' : 'The errors',
    r.errors.map((e) => `Line ${e.line}, column ${e.column}: ${e.code} ${e.message}`).join('\n')));
  if (r.shownSql?.length) box.append(part('sql', r.shownSql.length === 1 ? 'Plasm generated 1 command' : `Plasm generated ${r.shownSql.length} commands`, r.shownSql.join('\n\n')));
  if (r.status !== 'stopped' && r.status !== 'compile' && koan.discovers)
    box.append(el('div', { class: 'meditate' }, el('strong', {}, 'Meditate on this'), el('span', {}, koan.discovers)));
  // The hint waits for a wrong answer: a blank not yet tried gets none.
  if (['failed', 'threw', 'compile'].includes(r.status) && koan.hint)
    box.append(el('div', { class: 'meditate hint' }, el('strong', {}, 'A hint'), el('span', {}, koan.hint)));
  box.dataset.status = r.status;
}

function updateProgress() {
  course.lessons.forEach((lesson, l) => {
    const done = lesson.koans.filter((_, k) => isPassed(l, k)).length;
    const progress = document.querySelector(`nav h2[data-lesson="${l}"] .progress`);
    if (progress) progress.textContent = `${done} of ${lesson.koans.length}`;
    const ring = document.querySelector(`nav h2[data-lesson="${l}"] .ring`);
    if (ring) { ring.style.setProperty('--p', Math.round(100 * done / lesson.koans.length)); ring.classList.toggle('done', done === lesson.koans.length); }
    lesson.koans.forEach((_, k) => {
      const b = document.querySelector(`nav button[data-koan="${keyOf(l, k)}"]`);
      if (b && isPassed(l, k)) b.dataset.state = 'passed';
      else if (b && b.dataset.state === 'passed') delete b.dataset.state;
    });
  });
  const total = course.lessons.reduce((n, l) => n + l.koans.length, 0);
  const done = course.lessons.reduce((n, l, li) => n + l.koans.filter((_, k) => isPassed(li, k)).length, 0);
  $('overall').textContent = `${done} of ${total} passed`;
  $('overall-bar').style.width = `${100 * done / total}%`;
}

/// Unfolds one lesson in the list; the others show only their title and progress.
function unfold(lessonIndex) {
  document.querySelectorAll('#lessons .lesson').forEach((section) => {
    const open = Number(section.dataset.index) === lessonIndex;
    section.classList.toggle('open', open);
    section.querySelector('.koans').hidden = !open;
    section.querySelector('.lesson-toggle').setAttribute('aria-expanded', open);
  });
}

// The data every koan starts from, read from the engine's database once, before
// any koan runs (every koan's changes are rolled back, so it never changes).
let startingData = null; // table -> { columns: [{ name, type, nullable }], rows: [[text|null]] }
(async () => {
  const current = await engine.ready;
  const id = ++ids;
  startingData = await new Promise((resolve) => {
    current.pending.set(id, resolve);
    current.worker.postMessage({ type: 'tables', id, names: course.tables });
  });
  $('given').dataset.state = 'ready';
  if (shown) renderGiven(course.lessons[shown.lessonIndex].koans[shown.koanIndex]);
})();

/// A table of starting rows; NULL is shown as NULL, never as an empty cell.
function tableOf(name, t) {
  const head = el('tr', {}, ...t.columns.map((c) =>
    el('th', { scope: 'col', 'data-column': c.name, title: c.type }, c.name, c.nullable ? el('small', {}, 'may be NULL') : null)));
  const rows = t.rows.map((r) => el('tr', {}, ...r.map((v) => (v === null ? el('td', { class: 'null' }, el('span', {}, 'NULL')) : el('td', {}, v)))));
  return el('div', { class: 'table', 'data-table': name },
    el('div', { class: 'table-name' }, `${name} · ${t.rows.length} row${t.rows.length === 1 ? '' : 's'}`),
    el('div', { class: 'table-scroll' }, el('table', {}, el('thead', {}, head), el('tbody', {}, ...rows))));
}

/// Given: the tables a koan reads, with their starting rows, and the
/// definitions it uses, with the fields it reads marked. Hidden when it needs neither.
function renderGiven(koan) {
  const g = koan.given ?? { tables: [], definitions: [] };
  const any = g.tables.length + g.definitions.length > 0;
  $('given').hidden = !any;
  $('koan').classList.toggle('has-given', any);
  if (!any) return;

  const all = $('all-tables').getAttribute('aria-pressed') === 'true';
  const box = $('given-tables');
  box.replaceChildren();
  if (!startingData) box.append(el('p', { class: 'table-name' }, 'Reading the data…'));
  else {
    for (const name of course.tables) {
      if (!startingData[name]) continue;
      const t = tableOf(name, startingData[name]);
      t.hidden = !all && !g.tables.includes(name);
      box.append(t);
    }
    if (!all && g.tables.length === 0) box.append(el('p', { class: 'table-name' }, 'This koan reads no tables.'));
  }

  const chips = $('given-chips');
  const defs = $('given-definitions');
  chips.replaceChildren();
  defs.replaceChildren();
  $('given-definitions-card').hidden = g.definitions.length === 0;
  g.definitions.forEach((d, i) => {
    const pre = el('pre');
    pre.innerHTML = d.code.split('\n').map((line, n) => (d.marked.includes(n) ? `<span class="marked">${colour(line)}</span>` : colour(line))).join('\n');
    const panel = el('div', { class: 'definition', 'data-name': d.name }, el('div', { class: 'definition-head' }, el('span', { class: 'source' }, d.source === 'every koan' ? `${d.name}, given to every koan` : `${d.name}, from ${d.source}`),
      el('span', { class: 'kind', 'data-kind': d.kind }, d.kind)), pre);
    panel.hidden = i !== 0;
    const chip = el('button', { class: 'chip', type: 'button', 'aria-pressed': String(i === 0) }, d.name);
    chip.addEventListener('click', () => {
      defs.querySelectorAll('.definition').forEach((p) => { p.hidden = p !== panel; });
      chips.querySelectorAll('.chip').forEach((c) => c.setAttribute('aria-pressed', String(c === chip)));
    });
    chips.append(chip);
    defs.append(panel);
  });
}

$('all-tables').addEventListener('click', () => {
  const b = $('all-tables');
  b.setAttribute('aria-pressed', String(b.getAttribute('aria-pressed') !== 'true'));
  b.textContent = b.getAttribute('aria-pressed') === 'true' ? 'Only this koan\'s tables' : 'All tables';
  if (shown) renderGiven(course.lessons[shown.lessonIndex].koans[shown.koanIndex]);
});

let shown = null;
function show(lessonIndex, koanIndex) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  shown = { lessonIndex, koanIndex };
  $('lesson').textContent = `Lesson ${lessonIndex + 1} · ${lesson.title} · Koan ${koanIndex + 1} of ${lesson.koans.length}`;
  $('goal').textContent = lesson.goal;
  $('name').textContent = koan.name;
  const [feature, scenario] = [koan.teaches.slice(0, koan.teaches.indexOf(': ')), koan.teaches.slice(koan.teaches.indexOf(': ') + 2)];
  // The orientation lesson teaches F# itself, not a scenario of Plasm.
  if (feature === 'F#') { $('teaches').textContent = koan.teaches; $('teaches').removeAttribute('href'); }
  else { $('teaches').textContent = `${feature}.feature: ${scenario}`; $('teaches').href = `spec/${feature}.feature`; }
  renderGiven(koan);
  // The address always names the koan shown (go() adds the history entry first).
  const url = new URL(location.href);
  url.searchParams.set('koan', addressOf(koan));
  if (url.href !== location.href) window.history.replaceState(null, '', url);
  editor.set(savedFor(lessonIndex, koanIndex)?.edit ?? koan.body);
  render(results.get(`${lessonIndex}/${koanIndex}`), lessonIndex, koanIndex);
  $('koan').hidden = false;
  unfold(lessonIndex);
  document.querySelectorAll('nav button[data-koan]').forEach((b) => {
    const current = b.dataset.koan === `${lessonIndex}/${koanIndex}`;
    b.classList.toggle('current', current);
    if (current) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
  });
}

function preceding({ lessonIndex, koanIndex }) {
  if (koanIndex > 0) return { lessonIndex, koanIndex: koanIndex - 1 };
  if (lessonIndex > 0) return { lessonIndex: lessonIndex - 1, koanIndex: course.lessons[lessonIndex - 1].koans.length - 1 };
  return null;
}

/// A koan's address (?koan=...) is its name in lower case, words joined by '-'.
const addressOf = (koan) => koan.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

/// The koan the address names, or the first koan.
function addressed() {
  const wanted = new URLSearchParams(location.search).get('koan');
  for (const [l, lesson] of course.lessons.entries()) {
    const k = lesson.koans.findIndex((koan) => addressOf(koan) === wanted);
    if (k >= 0) return { lessonIndex: l, koanIndex: k };
  }
  return { lessonIndex: 0, koanIndex: 0 };
}

/// Opens a koan ready to type: the editor has the focus, with its first blank selected.
/// With 'push' the koan gets a history entry of its own, so Back returns to the one before.
function go(to, history = 'push') {
  if (!to) return;
  const url = new URL(location.href);
  url.searchParams.set('koan', addressOf(course.lessons[to.lessonIndex].koans[to.koanIndex]));
  if (history === 'push' && url.href !== location.href) window.history.pushState(null, '', url);
  show(to.lessonIndex, to.koanIndex);
  editor.focus();
  const blank = firstBlank(editor.get());
  if (editor.vimOn) editor.normalAt(Math.max(0, blank));
  else if (blank >= 0) editor.select(blank, blank + 2);
  else editor.select(0);
}

function following({ lessonIndex, koanIndex }) {
  if (koanIndex + 1 < course.lessons[lessonIndex].koans.length) return { lessonIndex, koanIndex: koanIndex + 1 };
  if (lessonIndex + 1 < course.lessons.length) return { lessonIndex: lessonIndex + 1, koanIndex: 0 };
  return null;
}

async function runShown() {
  if ($('run').disabled) return;
  const { lessonIndex, koanIndex } = shown;
  $('result').replaceChildren(el('div', { class: 'headline' }, 'Running…'));
  delete $('result').dataset.status;
  $('run').disabled = true;
  $('stop').hidden = false;
  let r;
  try {
    r = await runKoan(lessonIndex, koanIndex, editor.get());
  } catch (e) {
    $('run').disabled = false;
    $('stop').hidden = true;
    $('result').replaceChildren(el('div', { class: 'headline' }, couldNotLoad(e.message)), reloadButton());
    return;
  }
  $('run').disabled = false;
  $('stop').hidden = true;
  if (shown.lessonIndex !== lessonIndex || shown.koanIndex !== koanIndex) return;
  render(r, lessonIndex, koanIndex);
  if (r.status !== 'passed' && r.status !== 'stopped') document.querySelector(`nav button[data-koan="${keyOf(lessonIndex, koanIndex)}"]`).dataset.state = 'failed';
}
$('run').onclick = runShown;
$('shortcut').textContent = keys.run;
// The editor: Ctrl+Enter (or Cmd+Enter) runs the koan, and what can follow the
// cursor comes from the F# compiler in the engine.
const editor = createEditor($('code'), {
  complete: (code, offset) => (shown ? complete(shown.lessonIndex, shown.koanIndex, code, offset) : []),
  hover: (code, offset) => (shown ? askEngine('hover', shown.lessonIndex, shown.koanIndex, code, offset) : {}),
  diagnose: (code) => (shown ? askEngine('diagnose', shown.lessonIndex, shown.koanIndex, code, 0) : []),
  onEdit: (code) => keep(shown.lessonIndex, shown.koanIndex, (s) => { s.edit = code; }),
  onRun: () => runShown(),
  onVimMode: (mode) => {
    $('vim-mode').hidden = !mode;
    if (mode) { $('vim-mode').dataset.mode = mode; $('vim-mode').textContent = mode.toUpperCase(); }
  },
});
// Vim keys in the editor, for those who want them; the choice is kept in this browser.
const setVim = async (on) => {
  await editor.setVim(on);
  $('vim').setAttribute('aria-pressed', String(on));
  store.save('vim', on);
};
$('vim').onclick = async () => { await setVim(!editor.vimOn); editor.focus(); if (editor.vimOn) editor.normalAt(editor.selection().head); };
// Vim mode loads with the page when it was left on.
if (store.load('vim', false) === true) await setVim(true);
// Ctrl+] (or Cmd+]) opens the next koan and Ctrl+[ (or Cmd+[) the previous one, from anywhere on the page.
document.addEventListener('keydown', (e) => {
  if (e.defaultPrevented || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || !shown) return;
  if (e.code === 'BracketRight') { e.preventDefault(); go(following(shown)); }
  else if (e.code === 'BracketLeft') { e.preventDefault(); go(preceding(shown)); }
});
$('stop').onclick = () => stop();
$('reset').onclick = () => {
  const { lessonIndex, koanIndex } = shown;
  keep(lessonIndex, koanIndex, (s) => { delete s.edit; });
  editor.set(course.lessons[lessonIndex].koans[koanIndex].body);
};

course.lessons.forEach((lesson, l) => {
  const toggle = el('button', { class: 'lesson-toggle', 'aria-expanded': 'false' },
    el('span', { class: 'ring', 'aria-hidden': 'true' }), el('span', { class: 'title' }, lesson.title), el('span', { class: 'progress' }));
  const koans = el('div', { class: 'koans' });
  koans.hidden = true;
  const section = el('section', { class: 'lesson', 'data-index': l }, el('h2', { 'data-lesson': l }, toggle), koans);
  toggle.onclick = () => {
    const open = koans.hidden;
    koans.hidden = !open;
    section.classList.toggle('open', open);
    toggle.setAttribute('aria-expanded', open);
  };
  lesson.koans.forEach((koan, k) => {
    const b = el('button', { 'data-koan': `${l}/${k}` }, koan.name);
    b.onclick = () => go({ lessonIndex: l, koanIndex: k });
    koans.append(b);
  });
  $('lessons').append(section);
});

/// For checks: runs F# code (the body of `fun (repo: Repo) -> Task<string>`) in the engine.
async function evaluate(code, sandboxed = true) {
  const current = await engine.ready;
  const id = ++ids;
  return new Promise((resolve) => {
    current.pending.set(id, resolve);
    current.worker.postMessage({ type: 'evaluate', id, code, sandboxed });
  });
}

/// Asks the engine's F# checker about a koan's code at `offset`: 'complete'
/// (what can follow: [{name, kind, detail}]) or 'hover' (what is there:
/// {name, signature, doc}).
async function askEngine(type, lessonIndex, koanIndex, body, offset) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  const current = await engine.ready;
  const id = ++ids;
  return new Promise((resolve) => {
    current.pending.set(id, resolve);
    current.worker.postMessage({ type, id, helpers: lesson.helpers, isTask: koan.isTask, framed: koan.framed, body, offset });
  });
}
const complete = (lessonIndex, koanIndex, body, offset) => askEngine('complete', lessonIndex, koanIndex, body, offset);
const hover = (lessonIndex, koanIndex, body, offset) => askEngine('hover', lessonIndex, koanIndex, body, offset);

// For tests: the course and a way to reach any koan.
window.koans = { course, runKoan, show, stop, evaluate, addressOf, complete, hover, editor };
updateProgress();
go(addressed(), 'replace');
window.addEventListener('popstate', () => go(addressed(), 'none'));
let ready;
try {
  ready = await engine.ready;
} catch (e) {
  cannotLoad(e.message);
  throw e;
}
window.koansReady = true;

// Once the koans are ready, store the site for offline use in the background
// (it would otherwise compete with loading). Works on HTTPS and localhost.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.addEventListener('message', (e) => { if (e.data === 'offline-ready') window.koansOffline = true; });
  navigator.serviceWorker.register('sw.js').then(async () => {
    (await navigator.serviceWorker.ready).active.postMessage('store');
  }).catch(() => { /* offline use is optional */ });
}
status(`Ready (loaded in ${(ready.readyMs / 1000).toFixed(1)} s). Fill in each blank (__) and press Run.`);
