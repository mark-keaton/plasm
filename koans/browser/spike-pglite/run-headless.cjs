// Serves the published app (and PGlite from node_modules) and runs the page in
// headless Chromium through playwright-core, printing each scenario's result.
//
//   node run-headless.cjs [wwwroot] [chromium] [playwright-core]
//
// Defaults: the Release publish output, PLAYWRIGHT_CHROMIUM and PLAYWRIGHT_CORE.
const http = require('http');
const fs = require('fs');
const path = require('path');

const here = __dirname;
const wwwroot = path.resolve(process.argv[2] || path.join(here, 'Spike.App/bin/Release/net11.0/publish/wwwroot'));
const chromium = process.argv[3] || process.env.PLAYWRIGHT_CHROMIUM;
const playwrightCore = process.argv[4] || process.env.PLAYWRIGHT_CORE || 'playwright-core';
const pglite = path.join(here, 'node_modules/@electric-sql/pglite/dist');

const types = {
  '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json',
  '.wasm': 'application/wasm', '.data': 'application/octet-stream', '.dat': 'application/octet-stream',
  '.map': 'application/json', '.gz': 'application/gzip',
};

// Bytes served, to report the download cost (uncompressed and with Brotli where a
// .br file was published next to the file).
const served = { files: 0, bytes: 0, brotli: 0 };

const server = http.createServer((req, res) => {
  const url = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  const file = url.startsWith('/pglite/')
    ? path.join(pglite, url.slice('/pglite/'.length))
    : path.join(wwwroot, url === '/' ? 'index.html' : url.slice(1));
  if (!file.startsWith(pglite) && !file.startsWith(wwwroot)) { res.writeHead(403).end(); return; }
  fs.readFile(file, (err, data) => {
    if (err) { res.writeHead(404).end(); return; }
    served.files++;
    served.bytes += data.length;
    const br = file + '.br';
    served.brotli += fs.existsSync(br) ? fs.statSync(br).size : require('zlib').brotliCompressSync(data).length;
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] ?? 'application/octet-stream' });
    res.end(data);
  });
});

(async () => {
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  const { chromium: launcher } = require(playwrightCore);
  const browser = await launcher.launch({ executablePath: chromium, headless: true });
  const page = await browser.newPage();
  page.on('console', (m) => { if (m.type() === 'error') console.log('[console.error]', m.text()); });
  page.on('pageerror', (e) => console.log('[pageerror]', e.message));
  const started = Date.now();
  await page.goto(`http://127.0.0.1:${port}/`);
  await page.waitForFunction(() => window.spikeDone === true, null, { timeout: 180000 });
  const wall = Date.now() - started;
  const results = await page.evaluate(() => window.spikeResults);
  const timings = await page.evaluate(() => window.spikeTimings);
  for (const r of results) console.log(`${r.passed ? 'PASS' : 'FAIL'}  ${r.name}\n      ${r.detail}`);
  const failed = results.filter((r) => !r.passed).length;
  console.log(`\n${results.length - failed} passed, ${failed} failed`);
  console.log(`timings (ms since page script start): ${JSON.stringify(timings)}; wall clock from navigation: ${wall} ms`);
  const mb = (n) => (n / 1048576).toFixed(1) + ' MB';
  console.log(`downloaded: ${served.files} files, ${mb(served.bytes)} uncompressed, ~${mb(served.brotli)} with Brotli`);
  await browser.close();
  server.close();
  process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(2); });
