import {
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  ArrowLeft,
  ChevronDown,
  ChevronUp,
  Expand,
  Languages,
  Loader2,
  Maximize,
  Minimize,
  Pause,
  Play,
  RotateCcw,
  RotateCw,
  Settings,
  Subtitles,
  Volume1,
  Volume2,
  VolumeX,
  X,
  Gauge,
} from 'lucide-react';
import {
  api,
  behindLive,
  clock,
  formatTime,
  img,
  player,
  type MatchInfo,
  type PlayerState,
} from '../lib/api';
import {
  enhanceEnabled,
  setEnhanceEnabled,
  subscribeEnhance,
  VideoRenderer,
  type FrameInfo,
} from '../lib/videoRenderer';
import { SafeImg } from './Cards';
import { CastButton, CastOverlay } from './Cast';
import { useSlots, zap, zapStore } from '../lib/stores';
import { useCast, useFullscreen, useNav, usePlayer } from '../Context/AppContext';

type Rect = { left: number; top: number; width: number; height: number };
const MINI: Rect = { left: 0, top: 0, width: 384, height: 216 };

export default function VideoLayer() {
  const state = usePlayer();
  const cast = useCast();
  const casting = !!cast.active;
  const slots = useSlots();
  const nav = useNav();
  const { fullscreen, setFullscreen } = useFullscreen();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const rendererRef = useRef<VideoRenderer | null>(null);
  const [info, setInfo] = useState<FrameInfo | null>(null);
  const [rect, setRect] = useState<Rect | null>(null);
  const [box, setBox] = useState({ w: 0, h: 0 });
  const boxRef = useRef<HTMLDivElement>(null);

  const slot = slots[slots.length - 1];
  const active = state.status !== 'idle';
  const mode: 'full' | 'embed' | 'mini' | 'hidden' = slot
    ? slot.mode === 'embed' && !active
      ? 'hidden'
      : slot.mode
    : active && nav.route.name !== 'player'
      ? 'mini'
      : 'hidden';

  useEffect(() => {
    if (!canvasRef.current) return;
    const r = new VideoRenderer(canvasRef.current);
    r.onFrameInfo = setInfo;
    rendererRef.current = r;
    return () => r.dispose();
  }, []);

  useEffect(() => {
    if (!active) {
      rendererRef.current?.clear();
      setInfo(null);
    }
  }, [active]);

  useEffect(() => {
    if (!slot) {
      setRect(null);
      return;
    }
    let raf = 0;
    let last = '';
    const poll = () => {
      const r = slot.el.getBoundingClientRect();
      const key = `${r.left}|${r.top}|${r.width}|${r.height}`;
      if (key !== last) {
        last = key;
        setRect({ left: r.left, top: r.top, width: r.width, height: r.height });
      }
      raf = requestAnimationFrame(poll);
    };
    poll();
    return () => cancelAnimationFrame(raf);
  }, [slot]);

  useLayoutEffect(() => {
    const el = boxRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setBox({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const r: Rect | null =
    mode === 'mini'
      ? {
          ...MINI,
          left: window.innerWidth - MINI.width - 24,
          top: window.innerHeight - MINI.height - 24,
        }
      : mode === 'hidden'
        ? null
        : rect;

  const aspect = info?.aspect ?? 16 / 9;
  let cw = box.w;
  let ch = box.w / aspect;
  if (ch > box.h) {
    ch = box.h;
    cw = box.h * aspect;
  }

  return (
    <div
      ref={boxRef}
      className={`fixed z-30 overflow-hidden bg-black ${
        mode === 'full' ? '' : 'rounded-2xl ring-1 ring-white/10'
      } ${mode === 'hidden' ? 'pointer-events-none opacity-0' : ''}`}
      style={
        r
          ? { left: r.left, top: r.top, width: r.width, height: r.height }
          : { left: -10000, top: 0, width: 640, height: 360 }
      }
    >
      <canvas
        ref={canvasRef}
        className="absolute"
        style={{
          width: cw,
          height: ch,
          left: (box.w - cw) / 2,
          top: (box.h - ch) / 2,
          visibility: info && active && !casting ? 'visible' : 'hidden',
        }}
      />
      {active &&
        (casting && state.status !== 'error' ? (
          <CastOverlay cast={cast} state={state} compact={mode === 'mini'} />
        ) : (
          <StatusOverlay state={state} hasFrame={!!info} />
        ))}
      {mode === 'full' && (
        <FullControls state={state} fullscreen={fullscreen} setFullscreen={setFullscreen} />
      )}
      {mode === 'embed' && (
        <EmbedControls
          state={state}
          onExpand={() => nav.go({ name: 'player' })}
          onFullscreen={() => {
            nav.go({ name: 'player' });
            setFullscreen(true);
          }}
        />
      )}
      {mode === 'mini' && (
        <MiniControls state={state} onExpand={() => nav.go({ name: 'player' })} />
      )}
    </div>
  );
}

function StatusOverlay({ state, hasFrame }: { state: PlayerState; hasFrame: boolean }) {
  if (state.status === 'error') {
    return (
      <div className="absolute inset-0 flex flex-col items-center justify-center gap-3 bg-black/70 p-6 text-center">
        <p className="max-w-md text-sm text-white/80">{state.error}</p>
        <button
          className="btn btn-primary btn-sm rounded-full"
          onClick={() => state.item && player.play({ ...state.item, startMs: 0 })}
        >
          <RotateCcw size={16} /> Try again
        </button>
      </div>
    );
  }
  const waiting = state.status === 'opening' || state.status === 'buffering';
  if (!waiting && hasFrame) return null;
  if (state.status === 'ended') return null;
  return (
    <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-3">
      {!hasFrame && state.item?.icon && (
        <SafeImg
          src={img(state.item.icon)}
          className="max-h-24 max-w-48 object-contain opacity-60"
        />
      )}
      {waiting && (
        <div className="flex items-center gap-2 rounded-full bg-black/60 px-4 py-2 text-sm text-white/80">
          <Loader2 size={16} className="animate-spin" />
          {state.status === 'buffering' && state.buffering > 0 && state.buffering < 100
            ? `Buffering ${Math.round(state.buffering)}%`
            : 'Loading'}
        </div>
      )}
    </div>
  );
}

function useIdle(timeout = 2800) {
  const [idle, setIdle] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  const poke = () => {
    setIdle(false);
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setIdle(true), timeout);
  };
  useEffect(() => {
    poke();
    return () => window.clearTimeout(timer.current);
  }, []);
  return { idle, poke };
}

function PlayerMenu() {
  const enhance = useSyncExternalStore(subscribeEnhance, enhanceEnabled);
  return (
    <div className="dropdown dropdown-top dropdown-end" onClick={(e) => e.stopPropagation()}>
      <div
        tabIndex={0}
        role="button"
        className="flex h-10 w-10 items-center justify-center rounded-full text-white hover:bg-white/15"
      >
        <Settings size={20} />
      </div>
      <div tabIndex={0} className="dropdown-content panel z-50 mb-2 w-60 rounded-box p-2">
        <label className="flex items-center justify-between gap-4 rounded-lg px-3 py-2.5 text-sm hover:bg-white/5">
          Enhance video
          <input
            type="checkbox"
            className="toggle toggle-primary toggle-sm"
            checked={enhance}
            onChange={(e) => setEnhanceEnabled(e.target.checked)}
          />
        </label>
      </div>
    </div>
  );
}

function IconBtn({
  onClick,
  children,
  big,
}: {
  onClick: () => void;
  children: ReactNode;
  big?: boolean;
}) {
  return (
    <button
      onClick={(e) => {
        e.stopPropagation();
        onClick();
      }}
      className={`flex items-center justify-center rounded-full text-white transition hover:bg-white/15 ${
        big ? 'h-14 w-14' : 'h-10 w-10'
      }`}
    >
      {children}
    </button>
  );
}

function VolumeControl({ state }: { state: PlayerState }) {
  // While dragging the slider follows the pointer; server echoes would otherwise pull it back.
  const [drag, setDrag] = useState<number | null>(null);
  const pending = useRef<number | null>(null);
  const sending = useRef(false);
  const send = async (value: number) => {
    pending.current = value;
    if (sending.current) return;
    sending.current = true;
    while (pending.current !== null) {
      const v = pending.current;
      pending.current = null;
      await api.player('volume', { value: v }).catch(() => undefined);
    }
    sending.current = false;
  };
  const vol = drag ?? (state.muted ? 0 : state.volume);
  const Icon = vol === 0 ? VolumeX : vol < 50 ? Volume1 : Volume2;
  return (
    <div className="group/vol flex items-center">
      <IconBtn onClick={() => player.mute()}>
        <Icon size={22} />
      </IconBtn>
      <input
        type="range"
        min={0}
        max={150}
        value={vol}
        onClick={(e) => e.stopPropagation()}
        onChange={(e) => {
          const v = Number(e.target.value);
          setDrag(v);
          void send(v);
        }}
        onPointerUp={() => {
          if (drag !== null) player.volume(drag);
          setDrag(null);
        }}
        onKeyUp={() => setDrag(null)}
        className="range-thin w-0 opacity-0 transition-all duration-200 group-hover/vol:ml-1 group-hover/vol:w-24 group-hover/vol:opacity-100"
        style={{ ['--pct' as string]: `${(vol / 150) * 100}%` }}
      />
    </div>
  );
}

function SeekBar({ state }: { state: PlayerState }) {
  const [drag, setDrag] = useState<number | null>(null);
  const [hover, setHover] = useState<{ x: number; ms: number } | null>(null);
  const ref = useRef<HTMLDivElement>(null);
  const length = state.lengthMs || 1;
  const pos = drag ?? state.timeMs;
  const msAt = (clientX: number) => {
    const r = ref.current!.getBoundingClientRect();
    return Math.min(1, Math.max(0, (clientX - r.left) / r.width)) * length;
  };
  return (
    <div className="flex items-center gap-3 text-sm tabular-nums text-white/90">
      <span className="w-16 text-right">{formatTime(pos)}</span>
      <div
        ref={ref}
        className="group/seek relative flex h-5 flex-1 cursor-pointer items-center"
        onClick={(e) => e.stopPropagation()}
        onPointerDown={(e) => {
          e.stopPropagation();
          (e.target as HTMLElement).setPointerCapture(e.pointerId);
          setDrag(msAt(e.clientX));
        }}
        onPointerMove={(e) => {
          setHover({
            x: e.clientX - ref.current!.getBoundingClientRect().left,
            ms: msAt(e.clientX),
          });
          if (drag !== null) setDrag(msAt(e.clientX));
        }}
        onPointerLeave={() => setHover(null)}
        onPointerUp={(e) => {
          if (drag !== null) void player.seek(msAt(e.clientX));
          setDrag(null);
        }}
      >
        <div className="h-1 w-full overflow-hidden rounded-full bg-white/25 transition-all group-hover/seek:h-1.5">
          <div className="h-full bg-primary" style={{ width: `${(pos / length) * 100}%` }} />
        </div>
        <div
          className="absolute h-3.5 w-3.5 -translate-x-1/2 rounded-full bg-white shadow"
          style={{ left: `${(pos / length) * 100}%` }}
        />
        {hover && (
          <div
            className="absolute -top-8 -translate-x-1/2 rounded-md bg-black/80 px-2 py-0.5 text-xs"
            style={{ left: hover.x }}
          >
            {formatTime(hover.ms)}
          </div>
        )}
      </div>
      <span className="w-16">{formatTime(state.lengthMs)}</span>
    </div>
  );
}

// Red while at the broadcast; gray with the delay after pausing, and clicking it jumps back to live.
function LiveBadge({ state }: { state: PlayerState }) {
  const [now, setNow] = useState(Date.now());
  const paused = state.status === 'paused';
  useEffect(() => {
    setNow(Date.now());
    if (!paused) return;
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [paused, state.pausedAt, state.behindLiveMs]);
  const behind = behindLive(state, now);

  if (behind < 1500)
    return (
      <span className="badge badge-error badge-sm shrink-0 gap-1 font-bold text-white">
        <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-white" /> LIVE
      </span>
    );
  return (
    <button
      onClick={(e) => {
        e.stopPropagation();
        void player.goLive();
      }}
      className="badge badge-sm shrink-0 gap-1.5 border-0 bg-white/15 font-bold text-white/70 transition hover:bg-error hover:text-white"
    >
      <span className="h-1.5 w-1.5 rounded-full bg-white/50" /> LIVE
      <span className="font-medium tabular-nums">-{formatTime(behind)}</span>
    </button>
  );
}

function LiveBar({ state }: { state: PlayerState }) {
  const id = state.item?.id;
  const { data: epg, isPending } = useQuery({
    queryKey: ['epg', id],
    queryFn: () => api.epg(id!),
    enabled: !!id,
    refetchInterval: 30_000,
  });
  const now = Date.now();
  const current = epg?.find((e) => e.start <= now && e.end > now);
  // Keep the timeline's space while a new channel's guide loads, so the bar does not jump.
  const loading = !!id && isPending;
  const pct = current
    ? Math.min(100, ((now - current.start) / (current.end - current.start)) * 100)
    : 0;
  const isMatch = !!(current?.teams && current.info);
  return (
    <div className="flex flex-col gap-2.5 text-sm text-white/90">
      <div className="flex h-8 min-w-0 items-center gap-3">
        <LiveBadge state={state} />
        {isMatch ? (
          <MatchScore teams={current!.teams!} info={current!.info!} />
        ) : (
          current && <span className="min-w-0 truncate text-base font-medium">{current.title}</span>
        )}
      </div>
      {(current || loading) && (
        <div className={`flex items-center gap-3 ${current ? '' : 'invisible'}`}>
          <span className="w-12 tabular-nums text-xs text-white/60">
            {current ? clock(current.start) : '00:00'}
          </span>
          <div className="h-1 flex-1 overflow-hidden rounded-full bg-white/20">
            <div className="h-full bg-primary" style={{ width: `${pct}%` }} />
          </div>
          <span className="w-12 text-right tabular-nums text-xs text-white/60">
            {current ? clock(current.end) : '00:00'}
          </span>
        </div>
      )}
    </div>
  );
}

// No score or minute here: the stream runs behind live data, so it would spoil goals before they are shown.
function MatchScore({ teams, info }: { teams: string[]; info: MatchInfo }) {
  const logo = (src?: string) => (
    <span className="flex h-6 w-6 shrink-0 items-center justify-center">
      <SafeImg src={img(src)} className="h-6 w-6 object-contain" />
    </span>
  );
  return (
    <div className="flex h-8 shrink-0 items-center gap-2.5 rounded-lg bg-black/50 px-3">
      <span className="max-w-40 truncate font-semibold">{teams[0]}</span>
      {logo(info.homeLogo)}
      <span className="text-sm text-white/50">vs</span>
      {logo(info.awayLogo)}
      <span className="max-w-40 truncate font-semibold">{teams[1]}</span>
    </div>
  );
}

function TrackMenu({
  icon,
  title,
  tracks,
  current,
  onSelect,
  allowOff,
}: {
  icon: ReactNode;
  title: string;
  tracks: { id: number; name: string }[];
  current: number;
  onSelect: (id: number) => void;
  allowOff?: boolean;
}) {
  if (tracks.length === 0 || (!allowOff && tracks.length <= 1)) return null;
  return (
    <div className="dropdown dropdown-top dropdown-end" onClick={(e) => e.stopPropagation()}>
      <div
        tabIndex={0}
        role="button"
        className="flex h-10 w-10 items-center justify-center rounded-full text-white hover:bg-white/15"
      >
        {icon}
      </div>
      <ul
        tabIndex={0}
        className="menu dropdown-content panel z-50 mb-2 max-h-72 w-60 flex-nowrap overflow-y-auto rounded-box p-2"
      >
        <li className="menu-title text-white/60">{title}</li>
        {allowOff && (
          <li>
            <a className={current === -1 ? 'menu-active' : ''} onClick={() => onSelect(-1)}>
              Off
            </a>
          </li>
        )}
        {tracks.map((t) => (
          <li key={t.id}>
            <a className={current === t.id ? 'menu-active' : ''} onClick={() => onSelect(t.id)}>
              {t.name}
            </a>
          </li>
        ))}
      </ul>
    </div>
  );
}

// Clicks on the control rows stay there instead of pausing or toggling fullscreen on the video behind.
const keepClicks = {
  onClick: (e: React.MouseEvent) => e.stopPropagation(),
  onDoubleClick: (e: React.MouseEvent) => e.stopPropagation(),
};

function FullControls({
  state,
  fullscreen,
  setFullscreen,
}: {
  state: PlayerState;
  fullscreen: boolean;
  setFullscreen: (v: boolean) => void;
}) {
  const nav = useNav();
  const cast = useCast();
  const casting = !!cast.active;
  const { idle, poke } = useIdle();
  const isLive = state.item?.kind === 'live';
  const show = !idle || state.status !== 'playing' || casting;
  const canZap = isLive && zapStore.get().length > 1;

  return (
    <div
      className={`absolute inset-0 ${show ? '' : 'cursor-none'}`}
      onMouseMove={poke}
      onClick={() => player.toggle()}
      onDoubleClick={() => setFullscreen(!fullscreen)}
    >
      <div
        className={`pointer-events-none absolute inset-x-0 top-0 bg-gradient-to-b from-black/80 to-transparent px-5 pb-16 pt-4 transition-opacity duration-300 ${
          show ? 'opacity-100' : 'opacity-0'
        }`}
      >
        <div
          className={`flex w-fit max-w-full items-center gap-3 ${show ? 'pointer-events-auto' : ''}`}
          {...keepClicks}
        >
          <IconBtn
            onClick={() => {
              if (fullscreen) setFullscreen(false);
              nav.back();
            }}
          >
            <ArrowLeft size={22} />
          </IconBtn>
          {state.item?.icon && isLive && (
            <SafeImg src={img(state.item.icon)} className="h-9 w-14 object-contain" />
          )}
          <div className="min-w-0">
            <div className="truncate text-lg font-semibold text-white">
              {state.item?.seriesName ?? state.item?.name}
            </div>
            {state.item?.subtitle && (
              <div className="truncate text-sm text-white/60">{state.item.subtitle}</div>
            )}
          </div>
        </div>
      </div>

      {state.status === 'paused' && (
        <div className="pointer-events-none absolute inset-0 flex items-center justify-center">
          <div className="flex h-20 w-20 items-center justify-center rounded-full bg-black/50">
            <Play size={40} className="ml-1 text-white" fill="white" />
          </div>
        </div>
      )}

      <div
        className={`pointer-events-none absolute inset-x-0 bottom-0 bg-gradient-to-t from-black/85 to-transparent px-6 pb-4 pt-20 transition-opacity duration-300 ${
          show ? 'opacity-100' : 'opacity-0'
        }`}
      >
        <div className={`flex flex-col gap-2 ${show ? 'pointer-events-auto' : ''}`} {...keepClicks}>
          {isLive ? <LiveBar state={state} /> : <SeekBar state={state} />}
          <div className="flex items-center gap-1">
            <IconBtn onClick={() => player.toggle()}>
              {state.status === 'playing' || state.status === 'buffering' ? (
                <Pause size={24} fill="white" />
              ) : (
                <Play size={24} fill="white" />
              )}
            </IconBtn>
            {!isLive && state.seekable && (
              <>
                <IconBtn onClick={() => player.seekBy(-10000)}>
                  <RotateCcw size={20} />
                </IconBtn>
                <IconBtn onClick={() => player.seekBy(30000)}>
                  <RotateCw size={20} />
                </IconBtn>
              </>
            )}
            {canZap && state.item && (
              <>
                <IconBtn onClick={() => zap(state.item!.id, -1)}>
                  <ChevronUp size={22} />
                </IconBtn>
                <IconBtn onClick={() => zap(state.item!.id, 1)}>
                  <ChevronDown size={22} />
                </IconBtn>
              </>
            )}
            {!casting && <VolumeControl state={state} />}
            <div className="flex-1" />
            {!isLive && !casting && (
              <TrackMenu
                icon={<Gauge size={20} />}
                title="Speed"
                tracks={[0.5, 0.75, 1, 1.25, 1.5, 2].map((r) => ({ id: r * 100, name: `${r}x` }))}
                current={Math.round(state.rate * 100)}
                onSelect={(id) => player.rate(id / 100)}
              />
            )}
            <CastButton />
            {!casting && <PlayerMenu />}
            <TrackMenu
              icon={<Languages size={20} />}
              title="Audio"
              tracks={state.audioTracks}
              current={state.audioTrack}
              onSelect={(id) => player.audio(id)}
            />
            <TrackMenu
              icon={<Subtitles size={20} />}
              title="Subtitles"
              tracks={cast.active ? [] : state.subtitleTracks}
              current={state.subtitleTrack}
              onSelect={(id) => player.subtitle(id)}
              allowOff
            />
            <IconBtn onClick={() => setFullscreen(!fullscreen)}>
              {fullscreen ? <Minimize size={20} /> : <Maximize size={20} />}
            </IconBtn>
          </div>
        </div>
      </div>
    </div>
  );
}

function EmbedControls({
  state,
  onExpand,
  onFullscreen,
}: {
  state: PlayerState;
  onExpand: () => void;
  onFullscreen: () => void;
}) {
  if (state.status === 'idle') return null;
  return (
    <div className="group absolute inset-0" onDoubleClick={onFullscreen}>
      <div className="absolute inset-x-0 bottom-0 flex items-center gap-1 bg-gradient-to-t from-black/80 to-transparent px-3 pb-2 pt-10 opacity-0 transition-opacity group-hover:opacity-100">
        <IconBtn onClick={() => player.toggle()}>
          {state.status === 'paused' ? (
            <Play size={20} fill="white" />
          ) : (
            <Pause size={20} fill="white" />
          )}
        </IconBtn>
        <VolumeControl state={state} />
        <div className="flex-1" />
        <CastButton />
        <IconBtn onClick={onExpand}>
          <Expand size={18} />
        </IconBtn>
        <IconBtn onClick={onFullscreen}>
          <Maximize size={18} />
        </IconBtn>
      </div>
    </div>
  );
}

function MiniControls({ state, onExpand }: { state: PlayerState; onExpand: () => void }) {
  return (
    <div className="group absolute inset-0 cursor-pointer" onClick={onExpand}>
      <div className="absolute inset-0 flex flex-col justify-between bg-black/50 p-3 opacity-0 transition-opacity group-hover:opacity-100">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0 text-sm font-medium text-white">
            <div className="truncate">{state.item?.seriesName ?? state.item?.name}</div>
            {state.item?.subtitle && (
              <div className="truncate text-xs text-white/60">{state.item.subtitle}</div>
            )}
          </div>
          <IconBtn onClick={() => player.stop()}>
            <X size={18} />
          </IconBtn>
        </div>
        <div className="flex items-center justify-center">
          <IconBtn big onClick={() => player.toggle()}>
            {state.status === 'paused' ? (
              <Play size={28} fill="white" />
            ) : (
              <Pause size={28} fill="white" />
            )}
          </IconBtn>
        </div>
        <div />
      </div>
    </div>
  );
}
