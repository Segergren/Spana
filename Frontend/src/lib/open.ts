import type { CatalogItem, LibraryEntry, MediaKind } from './api';
import { player } from './api';
import { playLive } from './stores';
import type { Route } from '../Context/AppContext';

type Go = (r: Route) => void;

export function openItem(go: Go, kind: MediaKind, item: CatalogItem, list?: CatalogItem[]) {
  if (kind === 'live') {
    void playLive(item, list);
    go({ name: 'player' });
  } else if (kind === 'movie' || kind === 'series') {
    go({ name: 'detail', kind, id: item.id });
  }
}

export function openEntry(go: Go, e: LibraryEntry) {
  if (e.kind === 'live') {
    void player.play({ kind: 'live', id: e.id, name: e.name, icon: e.icon, ext: e.ext });
    go({ name: 'player' });
  } else if (e.kind === 'episode' && e.seriesId) {
    go({ name: 'detail', kind: 'series', id: e.seriesId });
  } else if (e.kind === 'movie' || e.kind === 'series') {
    go({ name: 'detail', kind: e.kind, id: e.id });
  }
}

export function resumeEntry(go: Go, e: LibraryEntry) {
  if (e.kind === 'live') return openEntry(go, e);
  void player.play({
    kind: e.kind,
    id: e.id,
    name: e.name,
    icon: e.icon,
    ext: e.ext,
    seriesId: e.seriesId,
    seriesName: e.seriesName,
    subtitle: e.subtitle,
    startMs: resumePosition(e),
  });
  go({ name: 'player' });
}

export function resumePosition(e?: LibraryEntry): number {
  if (!e?.positionMs || !e.durationMs) return 0;
  const pct = e.positionMs / e.durationMs;
  return e.positionMs > 30_000 && pct < 0.95 ? e.positionMs : 0;
}

export function progressOf(e?: LibraryEntry): number | undefined {
  if (!e?.positionMs || !e.durationMs) return undefined;
  return e.positionMs / e.durationMs;
}
