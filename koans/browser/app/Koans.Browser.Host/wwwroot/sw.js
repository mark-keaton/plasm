// Keeps the koans site for offline use: on request ('store') it caches every
// file listed in files.json, then serves the site from that cache first.
const cacheName = async () => {
  const list = await (await fetch('files.json', { cache: 'no-store' })).json();
  return { name: 'plasm-koans-' + list.version, files: list.files };
};

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));

self.addEventListener('message', (e) => {
  if (e.data !== 'store') return;
  e.waitUntil((async () => {
    try {
      const { name, files } = await cacheName();
      for (const key of await caches.keys()) if (key !== name) await caches.delete(key);
      const cache = await caches.open(name);
      const missing = [];
      for (const f of files) if (!(await cache.match(f))) missing.push(f);
      for (let i = 0; i < missing.length; i += 16) await cache.addAll(missing.slice(i, i + 16));
      await cache.put('./', await cache.match('index.html'));
      e.source.postMessage('offline-ready');
    } catch { /* offline use stays unavailable */ }
  })());
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (e.request.method !== 'GET' || url.origin !== location.origin) return;
  e.respondWith((async () => {
    const cached = await caches.match(e.request, { ignoreSearch: true });
    if (cached) return cached;
    return fetch(e.request);
  })());
});
