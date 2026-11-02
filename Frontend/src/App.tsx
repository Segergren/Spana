import { useEffect } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { Loader2 } from 'lucide-react';
import { useBootstrap, useFullscreen, useNav, usePlayer } from './Context/AppContext';
import { TopBar } from './Components/Shell';
import VideoLayer from './Components/VideoLayer';
import AddAccount from './Components/AddAccount';
import { CastError } from './Components/Cast';
import { LogoMark } from './Components/Logo';
import Home from './Pages/Home';
import Live from './Pages/Live';
import Discover from './Pages/Discover';
import Detail from './Pages/Detail';
import PlayerPage from './Pages/Player';
import Search from './Pages/Search';
import Library from './Pages/Library';
import Settings from './Pages/Settings';
import Sport from './Pages/Sport';
import Categories from './Pages/Categories';
import { player } from './lib/api';
import { zap } from './lib/stores';
import { useIdlePrefetch } from './lib/prefetch';

const ease = [0.22, 1, 0.36, 1] as const;
const pageVariants = {
  enter: (dir: number) => ({ opacity: 0, x: dir * 28 }),
  center: { opacity: 1, x: 0, transition: { duration: 0.28, ease } },
  exit: (dir: number) => ({ opacity: 0, x: dir * -20, transition: { duration: 0.16, ease } }),
};

function useShortcuts() {
  const state = usePlayer();
  const { route, back, go } = useNav();
  const { fullscreen, setFullscreen } = useFullscreen();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement;
      if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') return;
      const active = state.status !== 'idle';
      const inPlayer = route.name === 'player';
      const live = state.item?.kind === 'live';

      switch (e.key) {
        case ' ':
        case 'k':
          if (active) {
            e.preventDefault();
            void player.toggle();
          }
          break;
        case 'f':
          if (active) {
            if (!inPlayer) go({ name: 'player' });
            setFullscreen(!fullscreen);
          }
          break;
        case 'm':
          if (active) void player.mute();
          break;
        case 'ArrowLeft':
          if (inPlayer && !live) void player.seekBy(e.shiftKey ? -60000 : -10000);
          break;
        case 'ArrowRight':
          if (inPlayer && !live) void player.seekBy(e.shiftKey ? 60000 : 10000);
          break;
        case 'ArrowUp':
          if (inPlayer) {
            e.preventDefault();
            if (live && state.item) zap(state.item.id, -1);
            else void player.volume(state.volume + 5);
          }
          break;
        case 'ArrowDown':
          if (inPlayer) {
            e.preventDefault();
            if (live && state.item) zap(state.item.id, 1);
            else void player.volume(state.volume - 5);
          }
          break;
        case 'PageUp':
          if (live && state.item) zap(state.item.id, -1);
          break;
        case 'PageDown':
          if (live && state.item) zap(state.item.id, 1);
          break;
        case 'Escape':
          if (fullscreen) setFullscreen(false);
          else if (inPlayer) back();
          break;
        case 'Backspace':
          back();
          break;
      }
    };
    const onMouse = (e: MouseEvent) => {
      if (e.button === 3) {
        e.preventDefault();
        if (fullscreen) setFullscreen(false);
        back();
      }
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('mouseup', onMouse);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('mouseup', onMouse);
    };
  }, [state, route, fullscreen]);
}

function Page() {
  const { route } = useNav();
  switch (route.name) {
    case 'home':
      return <Home />;
    case 'live':
      return <Live categoryId={route.categoryId} />;
    case 'sport':
      return <Sport />;
    case 'categories':
      return <Categories />;
    case 'discover':
      return <Discover kind={route.kind} categoryId={route.categoryId} />;
    case 'detail':
      return <Detail kind={route.kind} id={route.id} />;
    case 'search':
      return <Search q={route.q} />;
    case 'library':
      return <Library />;
    case 'settings':
      return <Settings />;
    default:
      return null;
  }
}

export default function App() {
  const { data: boot, isLoading, error } = useBootstrap();
  const { route, direction } = useNav();
  useShortcuts();
  useIdlePrefetch();

  if (isLoading)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="animate-spin" />
      </div>
    );

  if (error || !boot)
    return (
      <div className="flex h-full items-center justify-center text-muted">Could not reach the Spana backend.</div>
    );

  if (boot.accounts.length === 0)
    return (
      <div className="flex h-full items-center justify-center p-6">
        <div className="panel w-full max-w-lg rounded-3xl p-8">
          <div className="mb-6 flex items-center gap-3">
            <LogoMark size={48} />
            <div>
              <h1 className="text-2xl font-bold">Welcome to Spana</h1>
              <p className="text-sm text-muted">Connect your IPTV provider to get started.</p>
            </div>
          </div>
          <AddAccount />
        </div>
      </div>
    );

  const playerRoute = route.name === 'player';
  const pageKey =
    route.name === 'detail'
      ? `detail-${route.id}`
      : route.name === 'discover'
        ? `discover-${route.kind}`
        : route.name === 'search'
          ? 'search'
          : route.name;
  const scrolls = ['search', 'home', 'detail', 'sport', 'categories', 'live'].includes(route.name);

  return (
    <div className="flex h-full flex-col">
      {!playerRoute && <TopBar />}
      <main className="relative min-h-0 flex-1 overflow-hidden">
        <AnimatePresence initial={false} custom={direction}>
          <motion.div
            key={pageKey}
            custom={direction}
            variants={pageVariants}
            initial="enter"
            animate="center"
            exit="exit"
            className={`absolute inset-0 ${scrolls ? 'overflow-y-auto' : ''}`}
          >
            <Page />
          </motion.div>
        </AnimatePresence>
      </main>
      {playerRoute && <PlayerPage />}
      <VideoLayer />
      <CastError />
    </div>
  );
}
