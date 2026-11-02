import { useSyncExternalStore } from 'react';
import type { CatalogItem, Episode, PlayRequest } from './api';
import { player } from './api';

function createStore<T>(initial: T) {
  let value = initial;
  const listeners = new Set<() => void>();
  return {
    get: () => value,
    set: (v: T) => {
      value = v;
      listeners.forEach((l) => l());
    },
    subscribe: (l: () => void) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
  };
}

export type SlotMode = 'full' | 'embed';
export interface Slot {
  id: number;
  el: HTMLElement;
  mode: SlotMode;
}

export const slotStore = createStore<Slot[]>([]);
let slotId = 0;

export function registerSlot(el: HTMLElement, mode: SlotMode) {
  const slot = { id: ++slotId, el, mode };
  slotStore.set([...slotStore.get(), slot]);
  return () => slotStore.set(slotStore.get().filter((s) => s.id !== slot.id));
}

export function useSlots() {
  return useSyncExternalStore(slotStore.subscribe, slotStore.get);
}

// Channel list the current live stream was started from, used for zapping up and down.
export const zapStore = createStore<CatalogItem[]>([]);

export function liveRequest(item: CatalogItem): PlayRequest {
  return { kind: 'live', id: item.id, name: item.name, icon: item.iconAlt ?? item.icon, url: item.url, ext: item.ext };
}

export function playLive(item: CatalogItem, list?: CatalogItem[]) {
  if (list) zapStore.set(list);
  return player.play(liveRequest(item));
}

export function zap(currentId: string, delta: number) {
  const list = zapStore.get();
  const idx = list.findIndex((c) => c.id === currentId);
  if (idx < 0 || list.length === 0) return;
  const next = list[(idx + delta + list.length) % list.length];
  void player.play(liveRequest(next));
}

export interface EpisodeQueue {
  seriesId: string;
  seriesName: string;
  poster?: string;
  episodes: Episode[];
}

// Flattened episode list of the series being watched, so the player can offer the next one.
export const episodeStore = createStore<EpisodeQueue | null>(null);

export function episodeRequest(q: EpisodeQueue, ep: Episode, startMs = 0): PlayRequest {
  return {
    kind: 'episode',
    id: ep.id,
    name: ep.title,
    icon: q.poster ?? ep.image,
    ext: ep.ext,
    seriesId: q.seriesId,
    seriesName: q.seriesName,
    subtitle: `S${ep.season} E${ep.number} · ${ep.title}`,
    startMs,
  };
}

export function playEpisode(q: EpisodeQueue, ep: Episode, startMs = 0) {
  episodeStore.set(q);
  return player.play(episodeRequest(q, ep, startMs));
}

export function nextEpisode(currentId: string): Episode | undefined {
  const q = episodeStore.get();
  if (!q) return undefined;
  const i = q.episodes.findIndex((e) => e.id === currentId);
  return i >= 0 ? q.episodes[i + 1] : undefined;
}
