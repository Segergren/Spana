import type { QueryClient, QueryKey } from '@tanstack/react-query';

// Keeps the last known data in IndexedDB so the app opens instantly and refreshes quietly behind it.
const DB = 'spana';
const STORE = 'queries';
const PERSISTED = new Set(['bootstrap', 'catalog', 'sport', 'suggestions', 'guideNow', 'library', 'channelArt']);

interface Entry {
  hash: string;
  key: QueryKey;
  data: unknown;
  updatedAt: number;
}

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: 'hash' });
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function shouldPersist(key: QueryKey) {
  const head = key[0];
  if (typeof head !== 'string' || !PERSISTED.has(head)) return false;
  // Movies and series are large and only fetched when enabled; channels are what makes start-up feel instant.
  return head !== 'catalog' || key[1] === 'live';
}

export async function hydrate(client: QueryClient, timeoutMs = 1200) {
  const work = (async () => {
    const db = await open();
    const entries = await new Promise<Entry[]>((resolve, reject) => {
      const req = db.transaction(STORE, 'readonly').objectStore(STORE).getAll();
      req.onsuccess = () => resolve(req.result as Entry[]);
      req.onerror = () => reject(req.error);
    });
    // Kept until fresh data replaces it, however old, so the app never starts empty.
    for (const e of entries) client.setQueryData(e.key, e.data, { updatedAt: e.updatedAt });
    // Shown right away, then refreshed in the background.
    for (const e of entries) client.invalidateQueries({ queryKey: e.key, exact: true, refetchType: 'none' });
    return db;
  })();

  let db: IDBDatabase | null = null;
  try {
    db = await Promise.race([work, new Promise<null>((r) => setTimeout(() => r(null), timeoutMs))]);
  } catch {
    db = null;
  }
  if (!db) {
    try {
      db = await open();
    } catch {
      return;
    }
  }

  const pending = new Map<string, Entry>();
  let timer: number | undefined;
  const flush = () => {
    timer = undefined;
    const batch = [...pending.values()];
    pending.clear();
    try {
      const store = db!.transaction(STORE, 'readwrite').objectStore(STORE);
      for (const e of batch) store.put(e);
    } catch {
      /* storage unavailable */
    }
  };

  client.getQueryCache().subscribe((event) => {
    if (event.type !== 'updated' || event.action.type !== 'success') return;
    const q = event.query;
    if (!shouldPersist(q.queryKey) || q.state.data === undefined) return;
    pending.set(q.queryHash, { hash: q.queryHash, key: q.queryKey, data: q.state.data, updatedAt: q.state.dataUpdatedAt });
    if (timer === undefined) timer = window.setTimeout(flush, 1500);
  });
}
