import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  api,
  wsBase,
  apiToken,
  setPlayerPatcher,
  type Bootstrap,
  type CastState,
  type CatalogStatus,
  type MediaKind,
  type PlayerState,
  type UpdateState,
} from '../lib/api';

export type Route =
  | { name: 'home' }
  | { name: 'sport' }
  | { name: 'categories' }
  | { name: 'live'; categoryId?: string }
  | { name: 'discover'; kind: 'movie' | 'series'; categoryId?: string }
  | { name: 'detail'; kind: 'movie' | 'series'; id: string }
  | { name: 'player' }
  | { name: 'search'; q: string }
  | { name: 'library' }
  | { name: 'settings' };

interface Nav {
  route: Route;
  direction: number;
  go: (route: Route, replace?: boolean) => void;
  back: () => void;
  canGoBack: boolean;
}

const initialPlayer: PlayerState = {
  status: 'idle',
  timeMs: 0,
  lengthMs: 0,
  seekable: false,
  buffering: 0,
  volume: 80,
  muted: false,
  rate: 1,
  audioTracks: [],
  subtitleTracks: [],
  audioTrack: -1,
  subtitleTrack: -1,
};

const initialCast: CastState = { devices: [], status: 'idle', scanning: false };

const NavContext = createContext<Nav>(null!);
const PlayerContext = createContext<PlayerState>(initialPlayer);
const CastContext = createContext<CastState>(initialCast);
const UpdateContext = createContext<UpdateState>({ current: '', channel: 'stable' });
const FullscreenContext = createContext<{ fullscreen: boolean; setFullscreen: (v: boolean) => void }>(
  null!,
);

export function AppProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [stack, setStack] = useState<Route[]>([{ name: 'home' }]);
  const [direction, setDirection] = useState(1);
  const [playerState, setPlayerState] = useState<PlayerState>(initialPlayer);
  const [cast, setCast] = useState<CastState>(initialCast);
  const [update, setUpdate] = useState<UpdateState>({ current: '', channel: 'stable' });
  const [fullscreen, setFullscreenState] = useState(false);
  const lastFetched = useRef<Record<string, string | undefined>>({});

  useEffect(() => setPlayerPatcher((patch) => setPlayerState((s) => ({ ...s, ...patch(s) }))), []);

  const go = useCallback((route: Route, replace = false) => {
    setDirection(1);
    setStack((s) => {
      const top = s[s.length - 1];
      if (JSON.stringify(top) === JSON.stringify(route)) return s;
      return replace ? [...s.slice(0, -1), route] : [...s.slice(-30), route];
    });
  }, []);
  const back = useCallback(() => {
    setDirection(-1);
    setStack((s) => (s.length > 1 ? s.slice(0, -1) : s));
  }, []);

  useEffect(() => {
    let socket: WebSocket | null = null;
    let closed = false;
    let retry: number | undefined;

    const connect = () => {
      socket = new WebSocket(`${wsBase}/ws?t=${apiToken}`);
      socket.onmessage = (e) => {
        const msg = JSON.parse(e.data) as { type: string; data: any };
        switch (msg.type) {
          case 'player':
            setPlayerState(msg.data);
            break;
          case 'cast':
            setCast(msg.data);
            break;
          case 'update':
            setUpdate(msg.data);
            break;
          case 'fullscreen':
            setFullscreenState(msg.data.enabled);
            break;
          case 'guideUpdated':
            queryClient.invalidateQueries({ queryKey: ['guideNow'] });
            queryClient.invalidateQueries({ queryKey: ['sport'] });
            queryClient.invalidateQueries({ queryKey: ['suggestions'] });
            queryClient.invalidateQueries({ queryKey: ['epg'] });
            // Guides add logos and guide ids to channels; the catalog answers 304 when nothing changed.
            queryClient.invalidateQueries({ queryKey: ['catalog', 'live'] });
            queryClient.invalidateQueries({ queryKey: ['channelArt'] });
            break;
          case 'matchDataUpdated':
            queryClient.invalidateQueries({ queryKey: ['sport'] });
            queryClient.invalidateQueries({ queryKey: ['epg'] });
            queryClient.invalidateQueries({ queryKey: ['catalog', 'live'] });
            queryClient.invalidateQueries({ queryKey: ['suggestions'] });
            break;
          case 'libraryChanged':
            queryClient.invalidateQueries({ queryKey: ['library'] });
            break;
          case 'catalogStatus': {
            const catalogs = msg.data.catalogs as CatalogStatus[];
            queryClient.setQueryData<Bootstrap>(['bootstrap'], (b) =>
              b && b.activeAccountId === msg.data.accountId ? { ...b, catalogs } : b,
            );
            for (const c of catalogs) {
              const key = `${msg.data.accountId}:${c.kind}`;
              if (!c.loading && c.fetchedAt && lastFetched.current[key] !== c.fetchedAt) {
                if (lastFetched.current[key] !== undefined)
                  queryClient.invalidateQueries({ queryKey: ['catalog', c.kind] });
                lastFetched.current[key] = c.fetchedAt;
              }
            }
            break;
          }
        }
      };
      socket.onclose = () => {
        if (!closed) retry = window.setTimeout(connect, 1000);
      };
    };
    connect();
    return () => {
      closed = true;
      window.clearTimeout(retry);
      socket?.close();
    };
  }, [queryClient]);

  const setFullscreen = useCallback((v: boolean) => {
    setFullscreenState(v);
    void api.player('fullscreen', { enabled: v });
  }, []);

  const nav = useMemo(
    () => ({ route: stack[stack.length - 1], direction, go, back, canGoBack: stack.length > 1 }),
    [stack, direction, go, back],
  );

  return (
    <NavContext.Provider value={nav}>
      <FullscreenContext.Provider value={{ fullscreen, setFullscreen }}>
        <PlayerContext.Provider value={playerState}>
          <CastContext.Provider value={cast}>
            <UpdateContext.Provider value={update}>{children}</UpdateContext.Provider>
          </CastContext.Provider>
        </PlayerContext.Provider>
      </FullscreenContext.Provider>
    </NavContext.Provider>
  );
}

export const useNav = () => useContext(NavContext);
export const usePlayer = () => useContext(PlayerContext);
export const useCast = () => useContext(CastContext);
export const useUpdate = () => useContext(UpdateContext);
export const useFullscreen = () => useContext(FullscreenContext);

export function useBootstrap() {
  return useQuery({ queryKey: ['bootstrap'], queryFn: api.bootstrap, staleTime: 60_000 });
}

export function useCatalog(kind: 'live' | 'movie' | 'series', enabled = true) {
  const { data: boot } = useBootstrap();
  return useQuery({
    queryKey: ['catalog', kind, boot?.activeAccountId],
    queryFn: () => api.catalog(kind),
    enabled: enabled && !!boot?.activeAccountId,
    staleTime: Infinity,
    gcTime: Infinity,
  });
}

export function useLibrary() {
  const { data: boot } = useBootstrap();
  return useQuery({
    queryKey: ['library', boot?.activeAccountId],
    queryFn: api.library,
    enabled: !!boot?.activeAccountId,
  });
}

export function useIsFavorite(kind: MediaKind, id: string) {
  const { data } = useLibrary();
  return !!data?.favorites.some((f) => f.kind === kind && f.id === id);
}

export function useGuideNow() {
  const { data: boot } = useBootstrap();
  return useQuery({
    queryKey: ['guideNow', boot?.activeAccountId],
    queryFn: api.guideNow,
    enabled: !!boot?.activeAccountId,
    refetchInterval: 60_000,
    staleTime: 30_000,
  });
}

export function useSuggestions() {
  const { data: boot } = useBootstrap();
  return useQuery({
    queryKey: ['suggestions', boot?.activeAccountId],
    queryFn: () => api.suggestions(24),
    enabled: !!boot?.activeAccountId,
    refetchInterval: 120_000,
    staleTime: 60_000,
  });
}

export function useSport() {
  const { data: boot } = useBootstrap();
  return useQuery({
    queryKey: ['sport', boot?.activeAccountId],
    queryFn: api.sport,
    enabled: !!boot?.activeAccountId,
    refetchInterval: 120_000,
    staleTime: 60_000,
  });
}

export function useShowVod() {
  const { data } = useBootstrap();
  return !!data?.settings.showVod;
}
