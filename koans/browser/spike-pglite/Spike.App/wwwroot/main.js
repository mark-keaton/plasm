// Starts PGlite and the .NET runtime, and gives .NET the PGlite bridge.
import { dotnet } from './_framework/dotnet.js';
import { PGlite } from './pglite/index.js';

const timings = {};
const results = [];
window.spikeResults = results;
window.spikeTimings = timings;
const t0 = performance.now();

const list = document.getElementById('results');
const status = document.getElementById('status');

function report(name, passed, detail) {
  results.push({ name, passed, detail });
  console.log(`${passed ? 'PASS' : 'FAIL'} ${name}: ${detail}`);
  const li = document.createElement('li');
  li.className = passed ? 'pass' : 'fail';
  li.innerHTML = `<strong></strong> <code></code>`;
  li.querySelector('strong').textContent = name;
  li.querySelector('code').textContent = detail;
  list.appendChild(li);
}

// ---------------------------------------------------------------------------
// PGlite
// ---------------------------------------------------------------------------

// PGlite and the .NET runtime download and start in parallel.
const dotnetStarting = dotnet.create().then((rt) => {
  timings.dotnetReady = Math.round(performance.now() - t0);
  return rt;
});
const db = await PGlite.create(); // in memory
timings.pgliteReady = Math.round(performance.now() - t0);

// Values cross the boundary as PostgreSQL text: every result value is left as
// text (the .NET side converts by type OID) and text parameters pass through.
const textParsers = {};
for (const k of Object.keys(db.parsers)) textParsers[k] = (x) => x;
const textSerializers = {};
for (const [k, f] of Object.entries(db.serializers)) {
  textSerializers[k] = (x) => (typeof x === 'string' ? x : f(x));
}

function errorOf(e) {
  return {
    code: e?.code ?? null,
    constraint: e?.constraint ?? null,
    message: e?.message ?? String(e),
    detail: e?.detail ?? null,
  };
}

async function query(sql, paramsJson, typesJson) {
  try {
    const params = JSON.parse(paramsJson);
    const paramTypes = JSON.parse(typesJson);
    const res = await db.query(sql, params, {
      rowMode: 'array',
      parsers: textParsers,
      serializers: textSerializers,
      paramTypes,
    });
    return JSON.stringify({
      fields: res.fields.map((f) => [f.name, f.dataTypeID]),
      rows: res.rows,
      rowCount: res.rowCount ?? res.affectedRows ?? 0,
    });
  } catch (e) {
    return JSON.stringify({ error: errorOf(e) });
  }
}

// Synchronous simple-protocol query (for transaction control, which ADO.NET's
// DbTransaction API calls synchronously). Returns "" or {"error": ...}.
const encoder = new TextEncoder();
const decoder = new TextDecoder();

function execSync(sql) {
  const body = encoder.encode(sql);
  const msg = new Uint8Array(1 + 4 + body.length + 1);
  msg[0] = 0x51; // 'Q'
  new DataView(msg.buffer).setInt32(1, 4 + body.length + 1);
  msg.set(body, 5);
  const out = db.execProtocolRawSync(msg);
  const view = new DataView(out.buffer, out.byteOffset, out.byteLength);
  let i = 0;
  while (i + 5 <= out.length) {
    const type = out[i];
    const len = view.getInt32(i + 1);
    if (type === 0x45) { // 'E' ErrorResponse
      const fields = {};
      let j = i + 5;
      const end = i + 1 + len;
      while (j < end && out[j] !== 0) {
        const code = String.fromCharCode(out[j++]);
        const start = j;
        while (out[j] !== 0) j++;
        fields[code] = decoder.decode(out.subarray(start, j));
        j++;
      }
      return JSON.stringify({
        error: { code: fields.C ?? null, constraint: fields.n ?? null, message: fields.M ?? 'error', detail: fields.D ?? null },
      });
    }
    i += 1 + len;
  }
  return '';
}

// ---------------------------------------------------------------------------
// .NET
// ---------------------------------------------------------------------------

const { setModuleImports, runMain } = await dotnetStarting;
setModuleImports('main.js', { pglite: { query, execSync }, report });
status.textContent = 'Running...';

try {
  await runMain();
} catch (e) {
  report('runMain', false, String(e?.stack ?? e));
}
timings.done = Math.round(performance.now() - t0);
const failed = results.filter((r) => !r.passed).length;
status.textContent = `${results.length - failed} passed, ${failed} failed (${timings.done} ms)`;
window.spikeDone = true;
