// Keeps the koans site for offline use: on request ('store') it caches every
// file listed in files.json. A page is served from that cache only when it
// holds the host's current staging (files.json's version) or the host cannot
// be reached; otherwise from the host, revalidating what the browser kept, so
// a page's files always come from one staging.
const versionNow = async () => (await (await fetch('files.json', { cache: 'no-store' })).json());

// Whether to serve from the cache, decided once per page load.
let fromCache = null;
const decide = () => (fromCache = (async () => {
  let list;
  try { list = await versionNow(); } catch { return true; } // offline
  const cache = await caches.open('plasm-koans-' + list.version);
  return (await cache.match('./')) !== undefined; // './' is stored last
})());

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));

self.addEventListener('message', (e) => {
  if (e.data !== 'store') return;
  e.waitUntil((async () => {
    try {
      const list = await versionNow();
      const name = 'plasm-koans-' + list.version;
      for (const key of await caches.keys()) if (key !== name) await caches.delete(key);
      const cache = await caches.open(name);
      const missing = [];
      for (const f of list.files) if (!(await cache.match(f))) missing.push(f);
      for (let i = 0; i < missing.length; i += 16)
        await cache.addAll(missing.slice(i, i + 16).map((f) => new Request(f, { cache: 'no-cache' })));
      await cache.put('./', await cache.match('index.html'));
      e.source.postMessage('offline-ready');
    } catch { /* offline use stays unavailable */ }
  })());
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin) return;
  if (e.request.mode === 'navigate' || fromCache === null) decide();
  e.respondWith((async () => {
    if (await fromCache) {
      const cached = await caches.match(e.request, { ignoreSearch: true });
      if (cached) return cached;
    }
    return e.request.mode === 'navigate' ? fetch(url.href, { cache: 'no-cache' }) : fetch(new Request(e.request, { cache: 'no-cache' }));
  })());
});
