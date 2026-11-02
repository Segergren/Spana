import { useEffect } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import type { Bootstrap, Catalog, GuideNow, Library, SportEvent } from './api';
import { useCatalog, useSport, type Route } from '../Context/AppContext';
import { LIVE_COLUMNS, liveChannels, storedLiveView } from '../Pages/Live';
import { sportSections } from '../Pages/Sport';
import { channelArtQuery, channelCardImages } from '../Components/ChannelNowCard';
import { programCardImages } from '../Components/ProgramCard';
import { logoOf } from '../Components/Cards';

// Loads the images a tab shows on its first screen before it is opened, so they are on screen at once.
const requested = new Set<string>();
const loading = new Set<HTMLImageElement>();

function preload(urls: (string | undefined)[]) {
  for (const url of urls) {
    if (!url || requested.has(url)) continue;
    requested.add(url);
    const image = new Image();
    image.decoding = 'async';
    image.onload = image.onerror = () => loading.delete(image);
    loading.add(image);
    image.src = url;
  }
}

const TOP_BAR = 68;

function liveCount() {
  const cardW = (innerWidth - 64 - 16 * (LIVE_COLUMNS - 1)) / LIVE_COLUMNS;
  const rowH = (cardW * 9) / 16 + 86 + 16;
  return LIVE_COLUMNS * Math.ceil((innerHeight - TOP_BAR - 140) / rowH);
}

function prefetchLive(qc: QueryClient, account: string) {
  const data = qc.getQueryData<Catalog>(['catalog', 'live', account]);
  if (!data) return;
  const lib = qc.getQueryData<Library>(['library', account]);
  const guide = qc.getQueryData<Record<string, GuideNow>>(['guideNow', account]);
  const { cat, alternatives } = storedLiveView();
  for (const c of liveChannels(data, lib, cat, '', alternatives).slice(0, liveCount())) {
    const now = c.epgId ? guide?.[c.epgId] : undefined;
    qc.fetchQuery(channelArtQuery(c.id))
      .then((art) => preload(channelCardImages(art, now, logoOf(c), true)))
      .catch(() => preload(channelCardImages(undefined, now, logoOf(c), true)));
  }
}

function prefetchSport(qc: QueryClient, account: string) {
  const data = qc.getQueryData<SportEvent[]>(['sport', account]);
  if (!data) return;
  const rows = Math.ceil((innerHeight - TOP_BAR - 120) / 530);
  const perRow = Math.ceil((innerWidth - 32) / 336);
  const sections = Object.values(sportSections(data)).filter((s) => s.length > 0).slice(0, rows);
  for (const events of sections) for (const e of events.slice(0, perRow)) preload(programCardImages(e));
}

export function prefetchTab(qc: QueryClient, route: Route['name']) {
  const account = qc.getQueryData<Bootstrap>(['bootstrap'])?.activeAccountId;
  if (!account) return;
  if (route === 'live') prefetchLive(qc, account);
  else if (route === 'sport') prefetchSport(qc, account);
}

/** Warms the TV and Sport tabs once the app is idle, and again when their data changes. */
export function useIdlePrefetch() {
  const qc = useQueryClient();
  const { data: live } = useCatalog('live');
  const { data: sport } = useSport();
  useEffect(() => {
    if (!live && !sport) return;
    const id = requestIdleCallback(
      () => {
        prefetchTab(qc, 'live');
        prefetchTab(qc, 'sport');
      },
      { timeout: 3000 },
    );
    return () => cancelIdleCallback(id);
  }, [qc, live, sport]);
}
