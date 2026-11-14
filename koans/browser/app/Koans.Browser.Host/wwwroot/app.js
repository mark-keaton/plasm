// The koans page. The engine (PGlite and .NET with the F# compiler) runs in a
// Web Worker, so the page stays responsive while a koan compiles and runs; a
// koan that does not finish is stopped by replacing the worker.
const $ = (id) => document.getElementById(id);
const status = (text) => { $('status').textContent = text; };

// The engine needs WebAssembly; without it, say so and point at the terminal version.
if (typeof WebAssembly !== 'object') {
  $('status').innerHTML = 'The koans run in your browser with WebAssembly, which this browser does not provide. ' +
    'You can <a href="terminal.html">run the koans in a terminal</a> instead.';
  throw new Error('WebAssembly is not available');
}


status('Loading the database, the .NET libraries and the compiler…');
const course = await (await fetch('course.json')).json();

function startEngine() {
  const worker = new Worker('worker.js', { type: 'module' });
  const engine = { worker, pending: new Map() };
  engine.ready = new Promise((resolve, reject) => {
    worker.onmessage = (e) => {
      const m = e.data;
      if (m.type === 'progress') status(`Loading… ${m.what} ready.`);
      else if (m.type === 'ready') { engine.readyMs = m.ms; resolve(engine); }
      else if (m.type === 'result') { engine.pending.get(m.id)?.(m.result); engine.pending.delete(m.id); }
    };
    worker.onerror = (e) => reject(new Error(e.message));
  });
  return engine;
}

let engine = startEngine();
let ids = 0;
const results = new Map(); // "lesson/koan" -> result

// Progress and edits are kept in this browser (localStorage), per koan.
const store = {
  load(key, fallback) { try { return JSON.parse(localStorage.getItem('plasm-koans:' + key)) ?? fallback; } catch { return fallback; } },
  save(key, value) { try { localStorage.setItem('plasm-koans:' + key, JSON.stringify(value)); } catch { /* private mode */ } },
};
const passed = new Set(store.load('passed', []));   // "lesson/koan" of passed koans
const edits = store.load('edits', {});              // "lesson/koan" -> code
const keyOf = (l, k) => `${l}/${k}`;

/// Runs a koan (with the given code, or its original code) in the engine.
async function runKoan(lessonIndex, koanIndex, body) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  const current = await engine.ready;
  const id = ++ids;
  const result = await new Promise((resolve) => {
    current.pending.set(id, resolve);
    current.worker.postMessage({ type: 'run', id, title: lesson.title, name: koan.name, helpers: lesson.helpers,
                                 isTask: koan.isTask, lambda: koan.lambda, body: body ?? koan.body });
  });
  results.set(keyOf(lessonIndex, koanIndex), result);
  if (result.status === 'passed') passed.add(keyOf(lessonIndex, koanIndex)); else passed.delete(keyOf(lessonIndex, koanIndex));
  store.save('passed', [...passed]);
  updateProgress();
  return result;
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

// A small F# highlighter (the same one as Plasm's website): comments, strings,
// numbers, keywords, query operations and capitalised type names; each blank
// (__) outside comments and strings is marked.
const keywords = new Set(('let let! do do! return return! yield match match! with fun function if then else elif for in to ' +
  'type of and rec mutable private open module namespace member static new use use! try finally when as not true false null ' +
  'task plasm transaction async seq').split(' '));
const operations = new Set(('where orderBy orderByDescending thenBy thenByDescending take skip limitPerParent preload ' +
  'replacePreload select join leftJoin on groupBy having identityKey key version generated column hasMany hasOne belongsTo manyToMany').split(' '));
const token = /(\/\/[^\n]*)|("(?:\\.|[^"\\\n])*"?)|(\b\d[\d_]*(?:\.\d+)?(?:L|m|M)?\b)|([A-Za-z_][\w']*!?)/g;

function colour(src) {
  let out = '';
  let last = 0;
  for (const m of src.matchAll(token)) {
    out += escape(src.slice(last, m.index));
    const [text, comment, string, number, word] = m;
    let cls = null;
    if (word === '__' && src[m.index - 1] !== '.') { out += '<mark class="blank">__</mark>'; last = m.index + text.length; continue; }
    if (comment) cls = 'c';
    else if (string) cls = 's';
    else if (number) cls = 'n';
    else if (keywords.has(word)) cls = 'k';
    else if (operations.has(word)) cls = 'f';
    // Capitalised names are types or union cases, except record field labels (Name = …).
    else if (/^[A-Z]/.test(word) && src[m.index - 1] !== '.' && !/^\s*=(?!=)/.test(src.slice(m.index + text.length))) cls = 't';
    out += cls ? `<span class="${cls}">${escape(text)}</span>` : escape(text);
    last = m.index + text.length;
  }
  return out + escape(src.slice(last));
}

/// Mirrors the editor's code behind it, coloured, and numbers its lines.
function highlight() {
  const code = $('code');
  $('highlight').innerHTML = colour(code.value) + '\n';
  const lines = code.value.split('\n').length;
  $('gutter').textContent = Array.from({ length: lines }, (_, i) => i + 1).join('\n');
  code.style.height = 'auto';
  code.style.height = `${code.scrollHeight}px`;
  $('highlight').scrollLeft = code.scrollLeft;
}

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
    const button = el('button', { class: 'btn primary', id: 'next' }, `Next koan: ${course.lessons[next.lessonIndex].koans[next.koanIndex].name} →`);
    button.onclick = () => show(next.lessonIndex, next.koanIndex);
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
  box.dataset.status = r.status;
}

function updateProgress() {
  course.lessons.forEach((lesson, l) => {
    const done = lesson.koans.filter((_, k) => passed.has(keyOf(l, k))).length;
    const progress = document.querySelector(`nav h2[data-lesson="${l}"] .progress`);
    if (progress) progress.textContent = `${done} of ${lesson.koans.length}`;
    const ring = document.querySelector(`nav h2[data-lesson="${l}"] .ring`);
    if (ring) { ring.style.setProperty('--p', Math.round(100 * done / lesson.koans.length)); ring.classList.toggle('done', done === lesson.koans.length); }
    lesson.koans.forEach((_, k) => {
      const b = document.querySelector(`nav button[data-koan="${keyOf(l, k)}"]`);
      if (b && passed.has(keyOf(l, k))) b.dataset.state = 'passed';
      else if (b && b.dataset.state === 'passed') delete b.dataset.state;
    });
  });
  const total = course.lessons.reduce((n, l) => n + l.koans.length, 0);
  $('overall').textContent = `${passed.size} of ${total} passed`;
  $('overall-bar').style.width = `${100 * passed.size / total}%`;
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

let shown = null;
function show(lessonIndex, koanIndex) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  shown = { lessonIndex, koanIndex };
  $('lesson').textContent = `Lesson ${lessonIndex + 1} · ${lesson.title} · Koan ${koanIndex + 1} of ${lesson.koans.length}`;
  $('goal').textContent = lesson.goal;
  $('name').textContent = koan.name;
  const [feature, scenario] = [koan.teaches.slice(0, koan.teaches.indexOf(': ')), koan.teaches.slice(koan.teaches.indexOf(': ') + 2)];
  $('teaches').textContent = `${feature}.feature: ${scenario}`;
  $('teaches').href = `spec/${feature}.feature`;
  $('code').value = edits[keyOf(lessonIndex, koanIndex)] ?? koan.body;
  highlight();
  render(results.get(`${lessonIndex}/${koanIndex}`), lessonIndex, koanIndex);
  $('koan').hidden = false;
  highlight(); // measured once visible
  unfold(lessonIndex);
  document.querySelectorAll('nav button[data-koan]').forEach((b) => {
    const current = b.dataset.koan === `${lessonIndex}/${koanIndex}`;
    b.classList.toggle('current', current);
    if (current) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
  });
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
  const r = await runKoan(lessonIndex, koanIndex, $('code').value);
  $('run').disabled = false;
  $('stop').hidden = true;
  if (shown.lessonIndex !== lessonIndex || shown.koanIndex !== koanIndex) return;
  render(r, lessonIndex, koanIndex);
  if (r.status !== 'passed' && r.status !== 'stopped') document.querySelector(`nav button[data-koan="${keyOf(lessonIndex, koanIndex)}"]`).dataset.state = 'failed';
}
$('run').onclick = runShown;
// Ctrl+Enter (or Cmd+Enter) in the editor runs the koan.
$('code').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); runShown(); }
});
$('shortcut').textContent = /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘↵' : 'Ctrl+↵';
$('stop').onclick = () => stop();
$('code').addEventListener('input', () => {
  highlight();
  edits[keyOf(shown.lessonIndex, shown.koanIndex)] = $('code').value;
  store.save('edits', edits);
});
$('code').addEventListener('scroll', () => { $('highlight').scrollLeft = $('code').scrollLeft; });
$('reset').onclick = () => {
  const { lessonIndex, koanIndex } = shown;
  delete edits[keyOf(lessonIndex, koanIndex)];
  store.save('edits', edits);
  $('code').value = course.lessons[lessonIndex].koans[koanIndex].body;
  highlight();
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
    b.onclick = () => show(l, k);
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

// For tests: the course and a way to reach any koan.
window.koans = { course, runKoan, show, stop, evaluate };
updateProgress();
show(0, 0);
const ready = await engine.ready;
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
