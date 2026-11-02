import { useEffect, useState } from 'react';
import { Play, SkipForward } from 'lucide-react';
import { useNav, usePlayer } from '../Context/AppContext';
import VideoSlot from '../Components/VideoSlot';
import { episodeStore, nextEpisode, playEpisode } from '../lib/stores';
import { Art } from '../Components/Cards';

export default function PlayerPage() {
  const state = usePlayer();
  const { back } = useNav();
  const [countdown, setCountdown] = useState<number | null>(null);
  const next = state.item?.kind === 'episode' ? nextEpisode(state.item.id) : undefined;

  useEffect(() => {
    if (state.status === 'idle') {
      const t = window.setTimeout(back, 300);
      return () => window.clearTimeout(t);
    }
  }, [state.status]);

  useEffect(() => {
    if (state.status !== 'ended' || !next) {
      setCountdown(null);
      return;
    }
    setCountdown(8);
    const iv = window.setInterval(() => setCountdown((c) => (c === null ? null : c - 1)), 1000);
    return () => window.clearInterval(iv);
  }, [state.status, next?.id]);

  useEffect(() => {
    if (countdown === 0 && next) {
      const q = episodeStore.get();
      if (q) void playEpisode(q, next);
    }
  }, [countdown]);

  return (
    <div className="fixed inset-0 bg-black">
      <VideoSlot mode="full" className="absolute inset-0" />
      {state.status === 'ended' && next && (
        <div className="absolute bottom-28 right-8 z-40 flex w-96 gap-4 rounded-2xl bg-black/80 p-4 ring-1 ring-white/10">
          <Art src={next.image} className="aspect-video w-32 shrink-0 rounded-lg" />
          <div className="min-w-0 flex-1">
            <div className="text-xs uppercase tracking-wide text-white/50">Up next</div>
            <div className="line-clamp-2 text-sm font-medium">
              S{next.season} E{next.number} · {next.title}
            </div>
            <div className="mt-2 flex gap-2">
              <button
                className="btn btn-primary btn-sm rounded-full"
                onClick={() => {
                  const q = episodeStore.get();
                  if (q) void playEpisode(q, next);
                }}
              >
                <SkipForward size={14} /> Play {countdown !== null && countdown > 0 ? `in ${countdown}` : ''}
              </button>
              <button className="btn btn-ghost btn-sm rounded-full" onClick={() => setCountdown(null)}>
                Cancel
              </button>
            </div>
          </div>
        </div>
      )}
      {state.status === 'ended' && !next && (
        <div className="absolute inset-0 z-40 flex items-center justify-center">
          <button className="btn btn-primary rounded-full" onClick={back}>
            <Play size={16} /> Done
          </button>
        </div>
      )}
    </div>
  );
}
