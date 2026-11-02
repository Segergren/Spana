import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api, clock, type GuideNow } from '../lib/api';
import { Art } from './Cards';

// What is on a channel right now, with the programme image from the match data or the guide when there is one.
export default function ChannelNowCard({
  channelId,
  name,
  logo,
  logoAlt,
  now,
  onClick,
  className = 'w-64 shrink-0',
  smallLogo = false,
}: {
  channelId: string;
  name: string;
  logo?: string;
  logoAlt?: string;
  now?: GuideNow;
  onClick: () => void;
  className?: string;
  smallLogo?: boolean;
}) {
  const { data: art } = useQuery({
    queryKey: ['channelArt', channelId],
    queryFn: () => api.channelArt(channelId),
    staleTime: 5 * 60_000,
    refetchInterval: 5 * 60_000,
  });
  const current = art?.now;
  const title = current?.title ?? now?.title ?? now?.next ?? 'No guide information';
  const start = current?.start ?? now?.start;
  const end = current?.end ?? now?.end;
  const next = art?.next
    ? { title: art.next.title, start: art.next.start }
    : now?.title && now.next && now.nextStart
      ? { title: now.next, start: now.nextStart }
      : undefined;
  const pct = start && end ? Math.min(100, ((Date.now() - start) / (end - start)) * 100) : 0;
  const [broken, setBroken] = useState<string>();
  const wanted = current?.image ?? (current ? undefined : now?.image);
  // A dead programme image falls back to the channel logo.
  const image = wanted && wanted !== broken ? wanted : undefined;

  return (
    <button
      onClick={onClick}
      className={`group flex ${className} flex-col overflow-hidden rounded-2xl bg-base-200 text-left transition hover:bg-base-100`}
    >
      <div className="relative aspect-video overflow-hidden bg-white/[0.04]">
        {image ? (
          <>
            <Art
              src={image}
              onFail={() => setBroken(image)}
              className="h-full w-full transition-transform duration-300 group-hover:scale-[1.02]"
            />
            <Art
              src={logo}
              altSrc={logoAlt}
              fit="contain"
              className="h-auto max-h-16 w-full object-[right_top] drop-shadow-[0_1px_2.5px_rgba(0,0,0,0.6)]"
              frame={(img) => <div className="absolute right-3.5 top-3.5 w-28">{img}</div>}
            />
          </>
        ) : (
          <div className={`flex h-full items-center justify-center ${smallLogo ? 'px-16 py-12' : 'p-8'}`}>
            <Art src={logo} altSrc={logoAlt} fit="contain" className="h-full w-full" />
          </div>
        )}
        {pct > 0 && (
          <div className="absolute inset-x-0 bottom-0 h-1 bg-black/40">
            <div className="h-full bg-primary" style={{ width: `${pct}%` }} />
          </div>
        )}
      </div>
      <div className="flex min-h-[86px] flex-col p-3.5">
        <div className="truncate text-xs text-white/50">{name}</div>
        <div className="mt-0.5 line-clamp-2 text-sm font-semibold leading-snug text-white">{title}</div>
        {next && (
          <div className="mt-auto truncate pt-1 text-[11px] text-white/45">
            {clock(next.start)} {next.title}
          </div>
        )}
      </div>
    </button>
  );
}
