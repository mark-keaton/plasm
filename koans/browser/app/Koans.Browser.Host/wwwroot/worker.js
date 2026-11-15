// The koans' engine, in a Web Worker: PGlite (PostgreSQL) and .NET (with the
// F# compiler). The page sends {type: 'run', ...} messages and gets results.
import { dotnet } from './_framework/dotnet.js';
import { PGlite } from './pglite/index.js';

const post = (m) => self.postMessage(m);
const t0 = performance.now();
const since = () => Math.round(performance.now() - t0);

// Two of Mono's interpreter optimizations make the F# compiler loop forever
// after a few compiles (a list walk that stops advancing, in
// NameResolution.AddTyconRefsToNameEnv); with them off it compiles indefinitely.
const dotnetStarting = dotnet.withEnvironmentVariable('MONO_INTERPRETER_OPTIONS', '-bblocks,-ssa').create();
const db = await PGlite.create();
post({ type: 'progress', what: 'the database', ms: since() });

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


const { setModuleImports, getAssemblyExports, getConfig } = await dotnetStarting;
setModuleImports('worker.js', { pglite: { query, execSync } });
const exports = (await getAssemblyExports(getConfig().mainAssemblyName)).KoansHost;
post({ type: 'progress', what: 'the .NET libraries', ms: since() });

// The compiler's reference assemblies. Their files are named by content
// (<name>.<hash>.dll), so a browser's copy is never one from an older staging.
const names = (await (await fetch('refs/list.txt', { cache: 'no-cache' })).text()).split('\n').filter((n) => n);
await Promise.all(names.map(async (n) => {
  exports.AddReference(n.replace(/\.[0-9a-f]{16}(\.\w+)$/, '$1'), new Uint8Array(await (await fetch('refs/' + n)).arrayBuffer()));
}));
post({ type: 'progress', what: 'the compiler', ms: since() });

await exports.Init();
post({ type: 'ready', ms: since() });

self.onmessage = async (e) => {
  const m = e.data;
  if (m.type === 'evaluate') {
    const result = JSON.parse(await exports.Evaluate(m.code, m.sandboxed));
    post({ type: 'result', id: m.id, result });
    return;
  }
  if (m.type === 'tables') {
    // The starting data of the given tables, read before any koan runs.
    const tables = {};
    for (const name of m.names) {
      const cols = await db.query(
        'select column_name, data_type, is_nullable from information_schema.columns ' +
        'where table_schema = current_schema() and table_name = $1 order by ordinal_position', [name]);
      const rows = await db.query(`select * from "${name.replace(/"/g, '""')}" order by 1, 2`, [], { rowMode: 'array', parsers: textParsers });
      tables[name] = {
        columns: cols.rows.map((c) => ({ name: c.column_name, type: c.data_type, nullable: c.is_nullable === 'YES' })),
        rows: rows.rows,
      };
    }
    post({ type: 'result', id: m.id, result: tables });
    return;
  }
  if (m.type === 'complete') {
    try {
      const result = JSON.parse(await exports.Complete(m.helpers, m.isTask, m.framed, m.body, m.offset));
      post({ type: 'result', id: m.id, result });
    } catch (err) {
      post({ type: 'result', id: m.id, result: [] });
    }
    return;
  }
  if (m.type === 'hover') {
    try {
      post({ type: 'result', id: m.id, result: JSON.parse(await exports.Hover(m.helpers, m.isTask, m.framed, m.body, m.offset)) });
    } catch (err) {
      post({ type: 'result', id: m.id, result: {} });
    }
    return;
  }
  if (m.type === 'diagnose') {
    try {
      post({ type: 'result', id: m.id, result: JSON.parse(await exports.Diagnose(m.helpers, m.isTask, m.framed, m.body)) });
    } catch (err) {
      post({ type: 'result', id: m.id, result: [] });
    }
    return;
  }
  if (m.type !== 'run') return;
  try {
    const result = await exports.RunKoan(m.title, m.name, m.helpers, m.isTask, m.framed, m.body);
    post({ type: 'result', id: m.id, result: JSON.parse(result) });
  } catch (err) {
    post({ type: 'result', id: m.id, result: { status: 'threw', text: String(err?.stack ?? err) } });
  }
};
