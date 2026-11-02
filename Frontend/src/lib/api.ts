export type MediaKind = 'live' | 'movie' | 'series' | 'episode';

export interface Category {
  id: string;
  name: string;
  count: number;
}

export interface CatalogItem {
  id: string;
  name: string;
  icon?: string;
  iconAlt?: string;
  categoryId: string;
  num: number;
  rating?: number;
  added: number;
  ext?: string;
  epgId?: string;
  archive?: boolean;
  url?: string;
  year?: string;
  genre?: string;
  plot?: string;
  backdrop?: string;
  // Lines of video the provider promises (1080 for "FHD").
  quality?: number;
  country?: string;
  // geo, part-time, backup, offline, hevc, 50fps, 5.1, heading
  tags?: string[];
  userAgent?: string;
  referrer?: string;
}

export interface Catalog {
  fetchedAt: string;
  // The viewer's country code ("se").
  home?: string;
  categories: Category[];
  items: CatalogItem[];
}

export interface Episode {
  id: string;
  season: number;
  number: number;
  title: string;
  ext?: string;
  plot?: string;
  image?: string;
  durationSecs: number;
  rating?: number;
  airDate?: string;
}

export interface Season {
  number: number;
  name: string;
  cover?: string;
  episodes: Episode[];
}

export interface Meta {
  id: string;
  kind: MediaKind;
  name: string;
  poster?: string;
  backdrop?: string;
  plot?: string;
  cast?: string;
  director?: string;
  genre?: string;
  year?: string;
  rating?: number;
  runtime?: string;
  trailer?: string;
  ext?: string;
  seasons?: Season[];
}

export interface EpgEntry {
  title: string;
  description?: string;
  start: number;
  end: number;
  image?: string;
  teams?: string[];
  info?: MatchInfo;
}

export interface LibraryEntry {
  key?: string;
  kind: MediaKind;
  id: string;
  name: string;
  icon?: string;
  seriesId?: string;
  seriesName?: string;
  subtitle?: string;
  ext?: string;
  positionMs?: number;
  durationMs?: number;
  updatedAt?: string;
}

export interface LiveWatch {
  channelId: string;
  channelName: string;
  epgId?: string;
  title?: string;
  minutes: number;
  lastWatched: string;
}

export interface Library {
  favorites: LibraryEntry[];
  history: LibraryEntry[];
  liveWatches: LiveWatch[];
}

export interface AccountInfo {
  status: string;
  expiresAt?: number;
  maxConnections: number;
  activeConnections: number;
  isTrial: boolean;
}

export interface Account {
  id: string;
  name: string;
  kind: 'xtream' | 'm3u';
  server: string;
  username: string;
  info?: AccountInfo;
}

export interface CatalogStatus {
  kind: 'live' | 'movie' | 'series';
  loading: boolean;
  count: number;
  fetchedAt?: string;
  error?: string;
}

export interface AppSettings {
  userAgent: string;
  networkCachingMs: number;
  audioDelayMs: number;
  volume: number;
  hardwareDecoding: boolean;
  liveCacheHours: number;
  vodCacheHours: number;
  hideAdult: boolean;
  showVod: boolean;
  matchData: boolean;
  preferredAudioLanguage: string;
  preferredSubtitleLanguage: string;
  updateChannel: 'stable' | 'beta';
}

export interface Bootstrap {
  version: string;
  activeAccountId?: string;
  accounts: Account[];
  settings: AppSettings;
  catalogs: CatalogStatus[];
}

export interface PlayRequest {
  kind: MediaKind;
  id: string;
  name: string;
  icon?: string;
  ext?: string;
  url?: string;
  seriesId?: string;
  seriesName?: string;
  subtitle?: string;
  startMs?: number;
}

export interface Track {
  id: number;
  name: string;
}

export interface PlayerState {
  status: 'idle' | 'opening' | 'buffering' | 'playing' | 'paused' | 'ended' | 'error';
  item?: PlayRequest;
  timeMs: number;
  lengthMs: number;
  seekable: boolean;
  buffering: number;
  volume: number;
  muted: boolean;
  rate: number;
  error?: string;
  audioTracks: Track[];
  subtitleTracks: Track[];
  audioTrack: number;
  subtitleTrack: number;
  behindLiveMs?: number;
  pausedAt?: number | null;
}

export interface UpdateState {
  current: string;
  channel: 'stable' | 'beta';
  /** Version that is downloaded and ready to install. */
  ready?: string;
}

export interface CastDevice {
  id: string;
  name: string;
  model?: string;
}

export interface CastState {
  devices: CastDevice[];
  active?: CastDevice;
  status: 'idle' | 'ready' | 'connecting' | 'casting';
  error?: string;
  scanning: boolean;
}

/** How far a live stream is behind the broadcast after pausing, still counting while paused. */
export function behindLive(s: PlayerState, now = Date.now()) {
  if (s.item?.kind !== 'live') return 0;
  return (s.behindLiveMs ?? 0) + (s.status === 'paused' && s.pausedAt ? now - s.pausedAt : 0);
}

export interface ProgramView {
  title: string;
  description?: string;
  start: number;
  end: number;
  image?: string;
}

export interface MatchInfo {
  image?: string;
  tournament?: string;
  description?: string;
  homeLogo?: string;
  awayLogo?: string;
  homeScore?: number;
  awayScore?: number;
  status?: string;
  live: boolean;
  finished: boolean;
  finishedAt?: number;
  kickoff?: number;
  broadcaster?: string;
}

export interface Suggestion {
  program: ProgramView;
  channel: CatalogItem;
  score: number;
  reason: string;
  live: boolean;
  teams: string[];
  match: 'team' | 'show' | 'league' | 'top' | 'country' | 'habit';
  info?: MatchInfo;
}

export interface SportEvent {
  program: ProgramView;
  channel: CatalogItem;
  live: boolean;
  sport: string;
  league?: string;
  teams: string[];
  mine: boolean;
  followed: boolean;
  relevance: number;
  tier: number;
  prominence: number;
  reason?: string;
  info?: MatchInfo;
}

export interface ProgramResult {
  program: ProgramView;
  channel: CatalogItem;
  live: boolean;
}

export interface ProgramArt {
  title: string;
  start: number;
  end: number;
  image?: string;
}

export interface ChannelArt {
  now?: ProgramArt;
  next?: ProgramArt;
}

export interface GuideNow {
  title?: string;
  start?: number;
  end?: number;
  image?: string;
  next?: string;
  nextStart?: number;
}

export interface SearchResults {
  live: CatalogItem[];
  movie: CatalogItem[];
  series: CatalogItem[];
  programs: ProgramResult[];
}

const params = new URLSearchParams(location.search);
const port = params.get('port') ?? sessionStorage.getItem('spana.port') ?? '44140';
const token = params.get('t') ?? sessionStorage.getItem('spana.token') ?? '';
const imageKey = params.get('ik') ?? sessionStorage.getItem('spana.ik') ?? '';
sessionStorage.setItem('spana.ik', imageKey);
sessionStorage.setItem('spana.port', port);
sessionStorage.setItem('spana.token', token);

export const apiBase = `http://localhost:${port}`;
export const wsBase = `ws://localhost:${port}`;
export const apiToken = token;

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(apiBase + path, {
    method,
    headers: {
      'X-Spana-Token': token,
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!res.ok) {
    let message = res.statusText;
    try {
      message = (await res.json()).error ?? message;
    } catch {
      /* not json */
    }
    throw new ApiError(res.status, message);
  }
  return res.json() as Promise<T>;
}

export const api = {
  bootstrap: () => request<Bootstrap>('GET', '/api/bootstrap'),
  addAccount: (body: { name: string; url: string; username?: string; password?: string }) =>
    request<Bootstrap>('POST', '/api/accounts', body),
  activateAccount: (id: string) => request<Bootstrap>('POST', `/api/accounts/${id}/activate`),
  removeAccount: (id: string) => request<Bootstrap>('DELETE', `/api/accounts/${id}`),
  updateSettings: (patch: Partial<AppSettings>) => request<Bootstrap>('POST', '/api/settings', patch),
  catalog: (kind: 'live' | 'movie' | 'series') => request<Catalog>('GET', `/api/catalog/${kind}`),
  refresh: () => request('POST', '/api/refresh'),
  search: (q: string, limit = 40) =>
    request<SearchResults>('GET', `/api/search?q=${encodeURIComponent(q)}&limit=${limit}`),
  meta: (kind: MediaKind, id: string) => request<Meta>('GET', `/api/meta/${kind}/${id}`),
  epg: (id: string) => request<EpgEntry[]>('GET', `/api/epg/${id}`),
  guideNow: () => request<Record<string, GuideNow>>('GET', '/api/guide/now'),
  channelArt: (id: string) => request<ChannelArt>('GET', `/api/channel-art/${id}`),
  sport: () => request<SportEvent[]>('GET', '/api/sport'),
  suggestions: (limit = 20) => request<Suggestion[]>('GET', `/api/suggestions?limit=${limit}`),
  clearWatchProfile: () => request('DELETE', '/api/watchprofile'),
  library: () => request<Library>('GET', '/api/library'),
  toggleFavorite: (entry: LibraryEntry) =>
    request<{ favorite: boolean }>('POST', '/api/favorite', entry),
  removeHistory: (key: string) => request('DELETE', `/api/history/${encodeURIComponent(key)}`),
  cacheInfo: () => request<{ imageBytes: number }>('GET', '/api/cache'),
  clearImageCache: () => request('POST', '/api/cache/clear'),
  player: (action: string, extra: Record<string, unknown> = {}) =>
    request('POST', '/api/player', { action, ...extra }),
  applyUpdate: () => request('POST', '/api/update'),
  cast: () => request<CastState>('GET', '/api/cast'),
  castAction: (action: string, extra: Record<string, unknown> = {}) =>
    request<CastState>('POST', '/api/cast', { action, ...extra }),
};

type PlayerPatch = (s: PlayerState) => Partial<PlayerState>;
let patchPlayer: ((patch: PlayerPatch) => void) | undefined;

// The UI shows the result of a click right away; the backend's next state message confirms it.
export function setPlayerPatcher(fn: (patch: PlayerPatch) => void) {
  patchPlayer = fn;
}

export const player = {
  play: (item: PlayRequest) => api.player('play', { item }),
  toggle: () => {
    patchPlayer?.((s) =>
      s.status === 'paused' ? { status: 'playing' } : s.status === 'playing' || s.status === 'buffering' ? { status: 'paused' } : {},
    );
    return api.player('toggle');
  },
  stop: () => api.player('stop'),
  goLive: () => api.player('goLive'),
  seek: (ms: number) => api.player('seek', { ms: Math.round(ms) }),
  seekBy: (ms: number) => api.player('seekBy', { ms }),
  volume: (value: number) => {
    patchPlayer?.(() => ({ volume: value, muted: false }));
    return api.player('volume', { value });
  },
  mute: () => {
    patchPlayer?.((s) => ({ muted: !s.muted }));
    return api.player('mute');
  },
  rate: (rate: number) => api.player('rate', { rate }),
  audio: (value: number) => api.player('audio', { value }),
  subtitle: (value: number) => api.player('subtitle', { value }),
  fullscreen: (enabled: boolean) => api.player('fullscreen', { enabled }),
};

export function img(url?: string | null): string | undefined {
  if (!url || !/^https?:\/\//.test(url)) return undefined;
  return imageKey
    ? `${apiBase}/img?k=${imageKey}&u=${encodeURIComponent(url)}`
    : `${apiBase}/img?t=${token}&u=${encodeURIComponent(url)}`;
}

export function formatTime(ms: number): string {
  if (!isFinite(ms) || ms < 0) ms = 0;
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n: number) => n.toString().padStart(2, '0');
  return h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
}

export function clock(ms: number): string {
  return new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function normalize(s: string): string {
  return s
    .toLowerCase()
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
}

const AFTER_MATCH_MS = 20 * 60_000;
const SOON_MS = 30 * 60_000;

type Timed = { live: boolean; program: ProgramView; info?: MatchInfo };

// The real kick-off when known; guide programmes often start with a studio show well before it.
export function kickoffOf(e: Timed) {
  return e.info?.kickoff ?? e.program.start;
}

// 0: kicking off (30 min before to 20 min after), 1: in progress, 2: just ended, 3: later.
export function phaseOf(e: Timed, now = Date.now()) {
  if (e.info?.finished) return 2;
  const k = kickoffOf(e);
  if (k - now <= SOON_MS && now - k <= AFTER_MATCH_MS) return 0;
  return e.live ? 1 : 3;
}

// A match whose final whistle was more than 20 minutes ago, even if its guide slot is still running.
export function isOver(e: Timed, now = Date.now()) {
  return !!e.info?.finished && now - (e.info.finishedAt ?? 0) >= AFTER_MATCH_MS;
}

// On now, about to start, or ended less than 20 minutes ago.
export function isLiveOrJustEnded(e: Timed, now = Date.now()) {
  if (e.info?.finished) return now - (e.info.finishedAt ?? 0) < AFTER_MATCH_MS;
  const k = kickoffOf(e);
  if (k > now) return k - now <= SOON_MS;
  if (!e.live) return false;
  return now - e.program.end < AFTER_MATCH_MS;
}

// Your interest, then how big the match is, then what is about to happen, then kick-off time.
export function byPriority<T extends Timed & { tier: number; prominence?: number; channel: CatalogItem }>(a: T, b: T, now = Date.now()) {
  return (
    b.tier - a.tier ||
    (b.prominence ?? 0) - (a.prominence ?? 0) ||
    phaseOf(a, now) - phaseOf(b, now) ||
    kickoffOf(a) - kickoffOf(b) ||
    a.channel.name.localeCompare(b.channel.name)
  );
}
