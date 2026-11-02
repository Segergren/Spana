import { useEffect, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion } from 'framer-motion';
import { ArrowDownToLine, ArrowLeft, Check, ChevronDown, Loader2, RefreshCw, Search, Settings, X } from 'lucide-react';
import { api } from '../lib/api';
import { LogoMark, Wordmark } from './Logo';
import { useBootstrap, useNav, useShowVod, useUpdate, type Route } from '../Context/AppContext';

interface Tab {
  label: string;
  route: Route;
  active: (r: Route) => boolean;
}

const LIVE_TABS: Tab[] = [
  { label: 'Start', route: { name: 'home' }, active: (r) => r.name === 'home' },
  { label: 'TV', route: { name: 'live' }, active: (r) => r.name === 'live' },
  { label: 'Sport', route: { name: 'sport' }, active: (r) => r.name === 'sport' },
  { label: 'Categories', route: { name: 'categories' }, active: (r) => r.name === 'categories' },
];

const VOD_TABS: Tab[] = [
  {
    label: 'Movies',
    route: { name: 'discover', kind: 'movie' },
    active: (r) => (r.name === 'discover' || r.name === 'detail') && r.kind === 'movie',
  },
  {
    label: 'Series',
    route: { name: 'discover', kind: 'series' },
    active: (r) => (r.name === 'discover' || r.name === 'detail') && r.kind === 'series',
  },
  { label: 'Library', route: { name: 'library' }, active: (r) => r.name === 'library' },
];

export function TopBar() {
  const { route, go, back, canGoBack } = useNav();
  const showVod = useShowVod();
  const tabs = showVod ? [...LIVE_TABS, ...VOD_TABS] : LIVE_TABS;

  return (
    <header className="z-40 flex h-[68px] shrink-0 items-center gap-3 px-6">
      <button
        onClick={back}
        disabled={!canGoBack}
        className="btn btn-circle btn-ghost btn-sm text-white/70 disabled:opacity-25"
      >
        <ArrowLeft size={18} />
      </button>
      <button onClick={() => go({ name: 'home' })} className="mr-4 flex items-center gap-2">
        <LogoMark size={28} />
        <Wordmark className="text-[22px] leading-none text-white" />
      </button>

      <nav className="flex items-center gap-1">
        {tabs.map((tab) => {
          const active = tab.active(route);
          return (
            <button
              key={tab.label}
              onClick={() => go(tab.route)}
              className={`relative h-9 rounded-full px-4 text-sm font-medium transition-colors ${
                active ? 'text-base-300' : 'text-white/70 hover:text-white'
              }`}
            >
              {active && (
                <motion.span
                  layoutId="nav-pill"
                  className="absolute inset-0 rounded-full bg-white"
                  transition={{ type: 'spring', stiffness: 500, damping: 40 }}
                />
              )}
              <span className="relative">{tab.label}</span>
            </button>
          );
        })}
      </nav>

      <div className="flex-1" />
      <SearchBox />
      <div className="flex items-center gap-1">
        <RefreshButton />
        <UpdateButton />
        <button
          onClick={() => go({ name: 'settings' })}
          className={`btn btn-circle btn-ghost btn-sm ${route.name === 'settings' ? 'bg-white/10 text-white' : 'text-white/70'}`}
        >
          <Settings size={18} />
        </button>
        <AccountMenu />
      </div>
    </header>
  );
}

function SearchBox() {
  const { route, go, back } = useNav();
  const [q, setQ] = useState(route.name === 'search' ? route.q : '');
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (route.name !== 'search') setQ('');
  }, [route.name]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = (e.target as HTMLElement)?.tagName === 'INPUT';
      if ((e.key === '/' && !typing) || (e.key.toLowerCase() === 'k' && (e.ctrlKey || e.metaKey))) {
        e.preventDefault();
        input.current?.focus();
        input.current?.select();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const update = (value: string) => {
    setQ(value);
    if (value.trim().length > 0) go({ name: 'search', q: value }, route.name === 'search');
    else if (route.name === 'search') back();
  };

  return (
    <label className="flex h-10 w-80 items-center gap-2.5 rounded-full bg-white/[0.07] px-4 transition focus-within:w-[26rem] focus-within:bg-white/10 xl:w-96">
      <Search size={16} className="shrink-0 text-white/50" />
      <input
        ref={input}
        value={q}
        onChange={(e) => update(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            update('');
            input.current?.blur();
          }
        }}
        placeholder="Search channels, matches and shows"
        className="w-full bg-transparent text-sm text-white outline-none placeholder:text-white/40"
      />
      {q ? (
        <button onClick={() => update('')} className="text-white/50 hover:text-white">
          <X size={15} />
        </button>
      ) : (
        <kbd className="kbd kbd-xs border-white/10 bg-white/5 text-white/40">/</kbd>
      )}
    </label>
  );
}

// Shows once an update is downloaded; clicking installs it and restarts, like Discord.
function UpdateButton() {
  const { ready } = useUpdate();
  const [installing, setInstalling] = useState(false);
  return (
    <AnimatePresence>
      {ready && (
        <motion.button
          key="update"
          onClick={() => {
            setInstalling(true);
            void api.applyUpdate().catch(() => setInstalling(false));
          }}
          initial={{ scale: 0, opacity: 0, rotate: -90 }}
          animate={{ scale: 1, opacity: 1, rotate: 0 }}
          exit={{ scale: 0, opacity: 0 }}
          transition={{ type: 'spring', stiffness: 420, damping: 18 }}
          className="relative flex h-8 w-8 items-center justify-center rounded-full text-primary hover:bg-primary/15"
        >
          <motion.span
            className="absolute inset-0 rounded-full bg-primary/40"
            initial={{ scale: 0.6, opacity: 0.8 }}
            animate={{ scale: 1.8, opacity: 0 }}
            transition={{ duration: 1.2, repeat: 2, ease: 'easeOut' }}
          />
          {installing ? <Loader2 size={18} className="animate-spin" /> : <ArrowDownToLine size={18} strokeWidth={2.4} />}
        </motion.button>
      )}
    </AnimatePresence>
  );
}

function RefreshButton() {
  const { data: boot } = useBootstrap();
  const loading = boot?.catalogs.some((c) => c.loading);
  return (
    <button onClick={() => api.refresh()} className="btn btn-circle btn-ghost btn-sm text-white/70">
      {loading ? <Loader2 size={18} className="animate-spin" /> : <RefreshCw size={17} />}
    </button>
  );
}

function AccountMenu() {
  const { data: boot } = useBootstrap();
  const qc = useQueryClient();
  const { go } = useNav();
  const active = boot?.accounts.find((a) => a.id === boot.activeAccountId);

  return (
    <div className="flex items-center">
      <div className="dropdown dropdown-end">
        <div
          tabIndex={0}
          role="button"
          className="btn btn-ghost btn-sm gap-1 rounded-full pl-1.5 pr-2 text-white/60 hover:text-white"
        >
          <span className="flex h-6 w-6 items-center justify-center rounded-full bg-primary text-[11px] font-bold uppercase text-white">
            <span className="cap-center">{active?.name.slice(0, 1) ?? '?'}</span>
          </span>
          <ChevronDown size={14} />
        </div>
        <ul tabIndex={0} className="menu dropdown-content panel z-50 mt-2 w-64 rounded-box p-2">
          {boot?.accounts.map((a) => (
            <li key={a.id}>
              <a
                onClick={async () => {
                  const b = await api.activateAccount(a.id);
                  qc.setQueryData(['bootstrap'], b);
                  (document.activeElement as HTMLElement)?.blur();
                }}
              >
                <span className="flex-1 truncate">{a.name}</span>
                {a.id === boot.activeAccountId && <Check size={16} className="text-primary" />}
              </a>
            </li>
          ))}
          <li className="mt-1 border-t border-line pt-1">
            <a
              onClick={() => {
                go({ name: 'settings' });
                (document.activeElement as HTMLElement)?.blur();
              }}
            >
              Manage accounts
            </a>
          </li>
        </ul>
      </div>
    </div>
  );
}
