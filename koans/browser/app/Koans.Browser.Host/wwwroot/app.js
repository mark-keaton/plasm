// The koans page. The engine (PGlite and .NET with the F# compiler) runs in a
// Web Worker, so the page stays responsive while a koan compiles and runs; a
// koan that does not finish is stopped by replacing the worker.
const course = await (await fetch('course.json')).json();
const $ = (id) => document.getElementById(id);
const status = (text) => { $('status').textContent = text; };

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
  if (result.status !== 'passed' && result.status !== 'compile' && result.status !== 'stopped' && koan.discovers)
    result.text = `${result.text}\n\n  Meditate on this: ${koan.discovers}`;
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

function describe(r) {
  if (r.status === 'passed') return '✔ Passed';
  if (r.status === 'compile') return r.errors.map((e) => `Line ${e.line}, column ${e.column}: ${e.code} ${e.message}`).join('\n');
  if (r.status === 'unanswered' && r.blankLine) return r.text.replace('in this cell', `on line ${r.blankLine}`);
  return r.text;
}

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

/// Mirrors the editor's code behind it, with each blank (__) marked.
function highlight() {
  const code = $('code').value;
  $('highlight').innerHTML = code.split('\n').map((line) => {
    const cut = line.indexOf('//');
    const [head, tail] = cut < 0 ? [line, ''] : [line.slice(0, cut), line.slice(cut)];
    return escape(head).replace(/(?<![\w.])__(?!\w)/g, '<mark class="blank">__</mark>') + escape(tail);
  }).join('\n') + '\n';
}

function updateProgress() {
  course.lessons.forEach((lesson, l) => {
    const done = lesson.koans.filter((_, k) => passed.has(keyOf(l, k))).length;
    const el = document.querySelector(`nav h2[data-lesson="${l}"] .progress`);
    if (el) el.textContent = `${done} of ${lesson.koans.length}`;
    lesson.koans.forEach((_, k) => {
      const b = document.querySelector(`nav button[data-koan="${keyOf(l, k)}"]`);
      if (b && passed.has(keyOf(l, k))) b.dataset.state = 'passed';
      else if (b && b.dataset.state === 'passed') delete b.dataset.state;
    });
  });
}

let shown = null;
function show(lessonIndex, koanIndex) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  shown = { lessonIndex, koanIndex };
  $('lesson').textContent = lesson.title;
  $('goal').textContent = lesson.goal;
  $('name').textContent = koan.name;
  const [feature, scenario] = [koan.teaches.slice(0, koan.teaches.indexOf(': ')), koan.teaches.slice(koan.teaches.indexOf(': ') + 2)];
  $('teaches').textContent = `${scenario} (${feature}.feature)`;
  $('teaches').href = `spec/${feature}.feature`;
  $('code').value = edits[keyOf(lessonIndex, koanIndex)] ?? koan.body;
  highlight();
  const previous = results.get(`${lessonIndex}/${koanIndex}`);
  $('result').textContent = previous ? describe(previous) : '';
  if (previous) $('result').dataset.status = previous.status; else delete $('result').dataset.status;
  $('next').hidden = !(previous && previous.status === 'passed');
  $('koan').hidden = false;
  document.querySelectorAll('nav button').forEach((b) => b.classList.toggle('current', b.dataset.koan === `${lessonIndex}/${koanIndex}`));
}

function following({ lessonIndex, koanIndex }) {
  if (koanIndex + 1 < course.lessons[lessonIndex].koans.length) return { lessonIndex, koanIndex: koanIndex + 1 };
  if (lessonIndex + 1 < course.lessons.length) return { lessonIndex: lessonIndex + 1, koanIndex: 0 };
  return null;
}

$('run').onclick = async () => {
  const { lessonIndex, koanIndex } = shown;
  $('result').textContent = 'Running…';
  delete $('result').dataset.status;
  $('run').disabled = true;
  $('stop').hidden = false;
  const r = await runKoan(lessonIndex, koanIndex, $('code').value);
  $('run').disabled = false;
  $('stop').hidden = true;
  if (shown.lessonIndex !== lessonIndex || shown.koanIndex !== koanIndex) return;
  $('result').textContent = describe(r);
  $('result').dataset.status = r.status;
  $('next').hidden = r.status !== 'passed' || !following(shown);
  if (r.status !== 'passed' && r.status !== 'stopped') document.querySelector(`nav button[data-koan="${keyOf(lessonIndex, koanIndex)}"]`).dataset.state = 'failed';
};
$('stop').onclick = () => stop();
$('code').addEventListener('input', () => {
  highlight();
  edits[keyOf(shown.lessonIndex, shown.koanIndex)] = $('code').value;
  store.save('edits', edits);
});
$('code').addEventListener('scroll', () => { $('highlight').scrollTop = $('code').scrollTop; });
$('reset').onclick = () => {
  const { lessonIndex, koanIndex } = shown;
  delete edits[keyOf(lessonIndex, koanIndex)];
  store.save('edits', edits);
  $('code').value = course.lessons[lessonIndex].koans[koanIndex].body;
  highlight();
};
$('next').onclick = () => { const n = following(shown); if (n) show(n.lessonIndex, n.koanIndex); };

course.lessons.forEach((lesson, l) => {
  const h = document.createElement('h2');
  h.dataset.lesson = l;
  h.textContent = lesson.title;
  const progress = document.createElement('span');
  progress.className = 'progress';
  h.append(progress);
  $('lessons').append(h);
  lesson.koans.forEach((koan, k) => {
    const b = document.createElement('button');
    b.textContent = koan.name;
    b.dataset.koan = `${l}/${k}`;
    b.onclick = () => show(l, k);
    $('lessons').append(b);
  });
});

// For tests: the course and a way to reach any koan.
window.koans = { course, runKoan, show, stop };
updateProgress();
show(0, 0);
const ready = await engine.ready;
window.koansReady = true;
status(`Ready (loaded in ${(ready.readyMs / 1000).toFixed(1)} s). Fill in each blank (__) and press Run.`);
