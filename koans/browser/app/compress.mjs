// node compress.mjs <site>: Brotli- and gzip-compresses every file that has no
// .br/.gz yet (static hosts serve X.br or X.gz when the browser accepts it), and writes files.json:
// every file of the site, for the service worker to store for offline use.
import { readdirSync, statSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join, relative } from 'node:path';
import { brotliCompressSync, gzipSync, constants } from 'node:zlib';
import { createHash } from 'node:crypto';

const site = process.argv[2];
const files = [];
(function walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path);
    else if (!name.endsWith('.br') && !name.endsWith('.gz')) files.push(path);
  }
})(site);

const hash = createHash('sha256');
for (const path of files.sort()) {
  const body = readFileSync(path);
  hash.update(relative(site, path)).update(body);
  if (body.length <= 1024) continue;
  if (!existsSync(path + '.br'))
    writeFileSync(path + '.br', brotliCompressSync(body, { params: { [constants.BROTLI_PARAM_QUALITY]: 11, [constants.BROTLI_PARAM_SIZE_HINT]: body.length } }));
  if (!existsSync(path + '.gz')) writeFileSync(path + '.gz', gzipSync(body, { level: 9 }));
}
const list = files.map((p) => relative(site, p).split('\\').join('/')).filter((p) => p !== 'files.json');
writeFileSync(join(site, 'files.json'), JSON.stringify({ version: hash.digest('hex').slice(0, 16), files: list }));
console.log(`${list.length} files`);
