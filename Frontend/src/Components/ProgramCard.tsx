import { useState } from 'react';
import { clock, img, type CatalogItem, type MatchInfo, type ProgramView } from '../lib/api';
import { Art, SafeImg, logoOf } from './Cards';
import { sportStyle } from '../lib/sports';

function startsIn(start: number) {
  const mins = Math.round((start - Date.now()) / 60000);
  if (mins < 60) return `in ${mins} min`;
  const h = Math.floor(mins / 60);
  if (h >= 24) return new Date(start).toLocaleDateString([], { weekday: 'short' });
  return `in ${h} h${mins % 60 ? ` ${mins % 60} min` : ''}`;
}

function cleanReason(reason: string) {
  return reason.replace(/\s*\|\s*[^|]+$/, '').replace(/(watch(?:ed)?) [^:]{1,24}: /, '$1 ');
}

// Guide titles often start with the sport in Swedish ("Fotboll: Allsvenskan"); the card already names the sport.
const SPORT_WORDS = new Set([
  'fotboll', 'fotball', 'football', 'ishockey', 'hockey', 'handboll', 'innebandy', 'basket', 'basketball', 'tennis',
  'golf', 'motorsport', 'boxning', 'cykel', 'friidrott', 'snooker', 'dart', 'padel', 'bandy', 'trav', 'sport',
]);
const isSportWord = (w?: string) => !!w && SPORT_WORDS.has(w.trim().toLowerCase());

// "Fotboll, Allsvenskan (H), Omgång 27" becomes "Allsvenskan".
function cleanTournament(t?: string) {
  if (!t) return undefined;
  const parts = t.split(',').map((x) => x.trim());
  const name = (parts.length > 1 ? parts[1] : parts[0]).replace(/\s*\([^)]*\)\s*$/, '');
  return name && !isSportWord(name) ? name : undefined;
}

function cleanTitle(title: string) {
  return title.replace(/\s*\|\s*[^|]+$/, '');
}

function Logo({ src, size = 'h-5 w-5' }: { src?: string; size?: string }) {
  const url = img(src);
  if (!url) return <span className={`${size} shrink-0`} />;
  return <SafeImg src={url} className={`${size} shrink-0 object-contain`} />;
}

export default function ProgramCard({
  program,
  channel,
  live,
  teams,
  reason,
  tag,
  info,
  highlight,
  playing,
  onClick,
}: {
  program: ProgramView;
  channel: CatalogItem;
  live: boolean;
  teams?: string[];
  reason?: string;
  tag?: string;
  info?: MatchInfo;
  highlight?: boolean;
  playing?: boolean;
  onClick: () => void;
}) {
  const now = Date.now();
  const kickoff = info?.kickoff ?? program.start;
  const sportLook = sportStyle(tag);
  const SportIcon = sportLook.icon;
  const pct = live ? Math.min(100, ((now - program.start) / (program.end - program.start)) * 100) : 0;
  const title = cleanTitle(program.title);
  const prefix = title.includes(': ') && title.indexOf(': ') < 24 ? title.slice(0, title.indexOf(': ')) : undefined;
  const matchup = teams && teams.length === 2 ? teams : undefined;
  const hasScore = info?.homeScore != null && info?.awayScore != null;
  const sportPrefix = isSportWord(prefix);
  const rest = prefix ? title.slice(prefix.length + 2) : title;
  const subtitle = info?.tournament ?? [tag, sportPrefix ? undefined : prefix].filter(Boolean).join(' · ');
  // Match art first; a matchup without it shows the team logos rather than a generic guide still.
  const [broken, setBroken] = useState<string>();
  const wanted = info?.image ?? (matchup && (info?.homeLogo || info?.awayLogo) ? undefined : program.image);
  // A dead image link falls back to the logos or the sport card instead of an empty frame.
  const image = wanted && wanted !== broken ? wanted : undefined;

  const badge = info?.live ? (
    <span className="rounded-md bg-error px-1.5 py-0.5 text-[11px] font-bold tabular-nums text-white">{info.status ?? 'LIVE'}</span>
  ) : info?.finished ? (
    <span className="rounded-md bg-black/70 px-1.5 py-0.5 text-[11px] font-bold text-white">{info.status ?? 'FT'}</span>
  ) : live && kickoff <= now ? (
    <span className="flex items-center gap-1 rounded-md bg-error px-1.5 py-0.5 text-[10px] font-bold text-white">
      <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-white" /> LIVE
    </span>
  ) : (
    // Before kick-off show the time, even while a studio show runs on the channel.
    <span className="rounded-md bg-black/70 px-1.5 py-0.5 text-[11px] font-medium text-white">
      {clock(kickoff)} · {startsIn(kickoff)}
    </span>
  );

  return (
    <button
      onClick={onClick}
      className={`group relative flex w-80 shrink-0 flex-col overflow-hidden rounded-2xl text-left transition ${
        playing
          ? 'bg-primary/20 ring-2 ring-primary'
          : highlight
            ? 'bg-base-200 ring-1 ring-accent/60 hover:bg-base-100'
            : 'bg-base-200 hover:bg-base-100'
      }`}
    >
      <div className="relative aspect-video w-full overflow-hidden bg-base-100">
        {image ? (
          <Art
            src={image}
            onFail={() => setBroken(image)}
            className="h-full w-full transition-transform duration-300 group-hover:scale-[1.02]"
          />
        ) : matchup && (info?.homeLogo || info?.awayLogo) ? (
          <div className="flex h-full items-center justify-center gap-6">
            <Logo src={info.homeLogo} size="h-16 w-16" />
            <span className="text-sm font-semibold text-white/30">vs</span>
            <Logo src={info.awayLogo} size="h-16 w-16" />
          </div>
        ) : (
          // No match image: a flat sport card with the tournament instead of a stretched channel logo.
          <div className="relative flex h-full flex-col justify-start overflow-hidden p-4" style={{ background: sportLook.color }}>
            <SportIcon className="absolute -bottom-6 -right-4 h-36 w-36 text-white opacity-[0.09]" strokeWidth={1.5} />
            <span className="text-[11px] font-semibold uppercase tracking-wider text-white/60">{tag ?? 'Sport'}</span>
            <span className="mt-1 line-clamp-2 max-w-[80%] text-lg font-bold leading-tight text-white">
              {cleanTournament(info?.tournament) ?? (prefix && !sportPrefix ? prefix : undefined) ?? (matchup ? `${matchup[0]} vs ${matchup[1]}` : rest)}
            </span>
          </div>
        )}
        <div className="absolute right-2 top-2">{badge}</div>
        <div className="absolute bottom-2 left-2 flex max-w-[85%] items-center gap-1.5 rounded-md bg-black/70 px-2 py-1 has-[img]:pl-1">
          <Art
            src={logoOf(channel)}
            altSrc={channel.icon}
            fit="contain"
            className="h-full w-full"
            frame={(img) => <span className="flex h-5 w-7 shrink-0 items-center justify-center">{img}</span>}
          />
          <span className="truncate text-[11px] text-white/85">{channel.name}</span>
        </div>
        {live && (
          <div className="absolute inset-x-0 bottom-0 h-1 bg-black/40">
            <div className="h-full bg-primary" style={{ width: `${pct}%` }} />
          </div>
        )}
      </div>

      <div className="flex flex-1 flex-col p-3.5">
        {subtitle && <div className="truncate text-[11px] uppercase tracking-wide text-white/45">{subtitle}</div>}
        {matchup ? (
          <div className="mt-1 flex flex-col gap-1 text-[15px] font-semibold leading-tight text-white">
            {[0, 1].map((i) => (
              <div key={i} className="flex items-center gap-2">
                {(info?.homeLogo || info?.awayLogo) && <Logo src={i === 0 ? info?.homeLogo : info?.awayLogo} />}
                <span className="flex-1 truncate">{matchup[i]}</span>
                {hasScore && <span className="tabular-nums">{i === 0 ? info!.homeScore : info!.awayScore}</span>}
              </div>
            ))}
          </div>
        ) : (
          <div className="mt-1 line-clamp-2 text-[15px] font-semibold leading-tight text-white">
            {rest}
          </div>
        )}
        {reason && <div className="mt-2 truncate text-[11px] text-accent">{cleanReason(reason)}</div>}
      </div>
    </button>
  );
}
