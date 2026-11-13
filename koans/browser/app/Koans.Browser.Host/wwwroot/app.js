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
  results.set(`${lessonIndex}/${koanIndex}`, result);
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

let shown = null;
function show(lessonIndex, koanIndex) {
  const lesson = course.lessons[lessonIndex];
  const koan = lesson.koans[koanIndex];
  shown = { lessonIndex, koanIndex };
  $('lesson').textContent = lesson.title;
  $('goal').textContent = lesson.goal;
  $('name').textContent = koan.name;
  $('code').value = koan.body;
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
  document.querySelector(`nav button[data-koan="${lessonIndex}/${koanIndex}"]`).dataset.state = r.status === 'passed' ? 'passed' : 'failed';
};
$('stop').onclick = () => stop();
$('next').onclick = () => { const n = following(shown); if (n) show(n.lessonIndex, n.koanIndex); };

course.lessons.forEach((lesson, l) => {
  const h = document.createElement('h2');
  h.textContent = lesson.title;
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
show(0, 0);
const ready = await engine.ready;
window.koansReady = true;
status(`Ready (loaded in ${(ready.readyMs / 1000).toFixed(1)} s). Fill in each blank (__) and press Run.`);
