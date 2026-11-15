// node seal.mjs <site>: names the compiler's references by content, then Brotli- and gzip-compresses every file that has no
// .br/.gz yet (static hosts serve X.br or X.gz when the browser accepts it), and writes files.json:
// every file of the site, for the service worker to store for offline use.
import { readdirSync, statSync, readFileSync, writeFileSync, existsSync, unlinkSync, renameSync } from 'node:fs';
import { join, relative, extname } from 'node:path';
import { brotliCompressSync, gzipSync, constants } from 'node:zlib';
import { createHash } from 'node:crypto';

const site = process.argv[2];

// The compiler's references (refs/) are named by content, <name>.<hash>.<ext>,
// replacing any older copy, and listed in refs/list.txt.
const refs = join(site, 'refs');
if (existsSync(refs)) {
  const hashed = /\.[0-9a-f]{16}(\.\w+)$/;
  for (const name of readdirSync(refs)) {
    if (name === 'list.txt' || hashed.test(name) || name.endsWith('.br') || name.endsWith('.gz')) continue;
    const body = readFileSync(join(refs, name));
    const ext = extname(name), base = name.slice(0, -ext.length);
    for (const other of readdirSync(refs))
      if (other !== name && other.replace(/\.(br|gz)$/, '').replace(hashed, '$1') === name) unlinkSync(join(refs, other));
    renameSync(join(refs, name), join(refs, `${base}.${createHash('sha256').update(body).digest('hex').slice(0, 16)}${ext}`));
  }
  writeFileSync(join(refs, 'list.txt'), readdirSync(refs).filter((n) => hashed.test(n)).sort().join('\n') + '\n');
}
const files = [];
(function walk(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) walk(path);
    else if (!name.endsWith('.br') && !name.endsWith('.gz')) files.push(path);
  }
})(site);

// A file's .br and .gz are (re)made unless they are newer than it, so a site
// sealed again in place never serves an older file's compressed copy.
const compress = (path) => {
  const body = readFileSync(path);
  const stale = (copy) => !existsSync(copy) || statSync(copy).mtimeMs < statSync(path).mtimeMs;
  if (body.length <= 1024) {
    for (const copy of [path + '.br', path + '.gz']) if (existsSync(copy)) unlinkSync(copy);
    return body;
  }
  if (stale(path + '.br'))
    writeFileSync(path + '.br', brotliCompressSync(body, { params: { [constants.BROTLI_PARAM_QUALITY]: 11, [constants.BROTLI_PARAM_SIZE_HINT]: body.length } }));
  if (stale(path + '.gz')) writeFileSync(path + '.gz', gzipSync(body, { level: 9 }));
  return body;
};

const hash = createHash('sha256');
const list = files.map((p) => relative(site, p).split('\\').join('/')).filter((p) => p !== 'files.json').sort();
for (const file of list) hash.update(file).update(compress(join(site, file)));
writeFileSync(join(site, 'files.json'), JSON.stringify({ version: hash.digest('hex').slice(0, 16), files: list }));
compress(join(site, 'files.json'));
console.log(`${list.length} files`);
