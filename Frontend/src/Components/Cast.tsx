import { useEffect, useState } from 'react';
import { Cast as CastIcon, Loader2, Monitor, Tv, X } from 'lucide-react';
import { api, img, type CastState, type PlayerState } from '../lib/api';
import { useCast } from '../Context/AppContext';
import { SafeImg } from './Cards';

export function CastButton() {
  const cast = useCast();
  const [error, setError] = useState<string | null>(null);
  const active = cast.active;
  const select = async (id: string | null) => {
    setError(null);
    try {
      await api.castAction('select', { id });
      (document.activeElement as HTMLElement | null)?.blur();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="dropdown dropdown-top dropdown-end" onClick={(e) => e.stopPropagation()}>
      <div
        tabIndex={0}
        role="button"
        onFocus={() => void api.cast().catch(() => undefined)}
        className={`flex h-10 w-10 items-center justify-center rounded-full hover:bg-white/15 ${
          active ? 'text-primary' : 'text-white'
        }`}
      >
        {active ? <Tv size={20} /> : <CastIcon size={20} />}
      </div>
      <ul
        tabIndex={0}
        className="menu dropdown-content panel z-50 mb-2 w-72 flex-nowrap rounded-box p-2"
      >
        <li className="menu-title flex-row items-center justify-between text-white/60">
          Play on
          {cast.scanning && <Loader2 size={14} className="animate-spin" />}
        </li>
        <li>
          <a className={!active ? 'menu-active' : ''} onClick={() => void select(null)}>
            <Monitor size={18} /> This computer
          </a>
        </li>
        {cast.devices.map((d) => (
          <li key={d.id}>
            <a
              className={active?.id === d.id ? 'menu-active' : ''}
              onClick={() => void select(d.id)}
            >
              <Tv size={18} />
              <span className="min-w-0 flex-1 truncate">{d.name}</span>
            </a>
          </li>
        ))}
        {cast.devices.length === 0 && (
          <li className="px-3 py-2 text-xs leading-relaxed text-white/50">
            {cast.scanning
              ? 'Looking for TVs on your network'
              : 'No TVs found. Smart TVs with DLNA on the same network show up here.'}
          </li>
        )}
        {(error ?? cast.error) && (
          <li className="px-3 py-2 text-xs leading-relaxed text-error">{error ?? cast.error}</li>
        )}
      </ul>
    </div>
  );
}

function statusText(cast: CastState, state: PlayerState) {
  if (cast.status === 'connecting' || state.status === 'opening') return 'Connecting';
  if (state.status === 'buffering') return 'Buffering';
  if (state.status === 'paused') return 'Paused';
  return 'Playing';
}

export function CastOverlay({
  cast,
  state,
  compact,
}: {
  cast: CastState;
  state: PlayerState;
  compact?: boolean;
}) {
  const device = cast.active!;
  const waiting =
    cast.status === 'connecting' || state.status === 'opening' || state.status === 'buffering';
  return (
    <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center gap-3 bg-gradient-to-b from-base-300 to-black p-4 text-center">
      {!compact && state.item?.icon && (
        <SafeImg
          src={img(state.item.icon)}
          className="max-h-20 max-w-40 object-contain opacity-70"
        />
      )}
      <div
        className={`flex items-center gap-2 text-white ${compact ? 'text-sm' : 'text-lg font-semibold'}`}
      >
        <Tv size={compact ? 16 : 22} />
        <span className="truncate">Playing on {device.name}</span>
      </div>
      {!compact && (
        <div className="flex items-center gap-2 text-sm text-white/60">
          {waiting && <Loader2 size={14} className="animate-spin" />}
          {statusText(cast, state)}
        </div>
      )}
    </div>
  );
}

// Failures end the cast and playback moves back here; this says why.
export function CastError() {
  const cast = useCast();
  const message = !cast.active ? cast.error : undefined;
  useEffect(() => {
    if (!message) return;
    const t = window.setTimeout(
      () => void api.castAction('dismissError').catch(() => undefined),
      12000,
    );
    return () => window.clearTimeout(t);
  }, [message]);
  if (!message) return null;
  return (
    <div className="panel fixed bottom-6 left-6 z-50 flex max-w-md items-start gap-3 rounded-2xl px-4 py-3 text-sm">
      <CastIcon size={18} className="mt-0.5 shrink-0 text-error" />
      <span className="flex-1">{message}</span>
      <button
        className="text-white opacity-60 hover:opacity-100"
        onClick={() => void api.castAction('dismissError').catch(() => undefined)}
      >
        <X size={16} />
      </button>
    </div>
  );
}
