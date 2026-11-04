import { useEffect, useRef, useState, type MouseEvent } from 'react';
import { createPortal } from 'react-dom';
import { Check, ChevronDown, History } from 'lucide-react';
import { clock, img, languageName, type Broadcast, type CatalogItem, type MatchInfo, type ProgramView } from '../lib/api';
import { Art, SafeImg, logoOf } from './Cards';
import { sportStyle } from '../lib/sports';
import { usePlayingChannel } from '../Context/AppContext';

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

const IMAGE_W = 320;
const TEAM_W = 64;
const CHANNEL_W = 28;

function cardImage(info: MatchInfo | undefined, matchup: boolean, program: ProgramView) {
  // Match art first; a matchup without it shows the team logos rather than a generic guide still.
  return info?.image ?? (matchup && (info?.homeLogo || info?.awayLogo) ? undefined : program.image);
}

/** The image URLs a card will load first, so they can be fetched before it is shown. */
export function programCardImages(e: { program: ProgramView; channel: CatalogItem; teams?: string[]; info?: MatchInfo }) {
  const matchup = e.teams?.length === 2;
  const image = cardImage(e.info, matchup, e.program);
  const urls = [img(logoOf(e.channel), CHANNEL_W)];
  if (image) urls.push(img(image, IMAGE_W));
  if (matchup) urls.push(img(e.info?.homeLogo, TEAM_W), img(e.info?.awayLogo, TEAM_W));
  return urls;
}

function Logo({ src, size = 'h-5 w-5' }: { src?: string; size?: string }) {
  const url = img(src, TEAM_W);
  if (!url) return <span className={`${size} shrink-0`} />;
  return <SafeImg src={url} className={`${size} shrink-0 object-contain`} />;
}

function ChannelLogo({ channel }: { channel: CatalogItem }) {
  return (
    <Art
      src={logoOf(channel)}
      altSrc={channel.icon}
      width={CHANNEL_W}
      fit="contain"
      className="h-full w-full"
      frame={(img) => <span className="flex h-5 w-7 shrink-0 items-center justify-center">{img}</span>}
    />
  );
}

const CHIP = 'flex max-w-full items-center gap-1.5 rounded-md bg-black/70 px-2 py-1 has-[img]:pl-1';

/** The channel the card plays on; with other channels showing the same match it opens a list to switch. */
function ChannelChip({
  channel,
  alternatives,
  onPick,
}: {
  channel: CatalogItem;
  alternatives?: Broadcast[];
  onPick?: (channel: CatalogItem) => void;
}) {
  const trigger = useRef<HTMLButtonElement>(null);
  const [at, setAt] = useState<{ left: number; top: number }>();

  useEffect(() => {
    if (!at) return;
    const close = () => setAt(undefined);
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close();
    window.addEventListener('pointerdown', close);
    window.addEventListener('scroll', close, true);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('keydown', onKey);
    };
  }, [at]);

  if (!alternatives?.length || !onPick)
    return (
      <div className={CHIP}>
        <ChannelLogo channel={channel} />
        <span className="truncate text-[11px] text-white/85">{channel.name}</span>
      </div>
    );

  const toggle = (e: MouseEvent) => {
    e.stopPropagation();
    if (at) return setAt(undefined);
    const r = trigger.current!.getBoundingClientRect();
    setAt({ left: Math.min(r.left, window.innerWidth - 300), top: Math.min(r.bottom + 6, window.innerHeight - 340) });
  };

  const rows: Broadcast[] = [{ channel, program: { title: '', start: 0, end: 0 } }, ...alternatives];

  return (
    <>
      <button
        ref={trigger}
        onClick={toggle}
        onPointerDown={(e) => e.stopPropagation()}
        className={`${CHIP} transition hover:bg-black/90 ${at ? 'ring-1 ring-white/40' : ''}`}
      >
        <ChannelLogo channel={channel} />
        <span className="truncate text-[11px] text-white/85">{channel.name}</span>
        <span className="shrink-0 text-[11px] tabular-nums text-white/50">+{alternatives.length}</span>
        <ChevronDown size={12} className={`shrink-0 text-white/70 transition ${at ? 'rotate-180' : ''}`} />
      </button>
      {at &&
        createPortal(
          <div
            onPointerDown={(e) => e.stopPropagation()}
            className="panel fixed z-50 max-h-80 w-72 overflow-y-auto rounded-box p-1.5 shadow-xl"
            style={{ left: at.left, top: at.top }}
          >
            {rows.map((b, i) => (
              <button
                key={b.channel.id}
                onClick={(e) => {
                  e.stopPropagation();
                  setAt(undefined);
                  onPick(b.channel);
                }}
                className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm transition hover:bg-white/10 ${i === 0 ? 'bg-white/[0.06] text-white' : 'text-white/80'}`}
              >
                <ChannelLogo channel={b.channel} />
                <span className="min-w-0 flex-1 truncate">{b.channel.name}</span>
                {i === 0 ? (
                  <Check size={14} className="shrink-0 text-primary" />
                ) : (
                  <span className="shrink-0 text-xs text-white/45">
                    {[languageName(b.language), b.program.replay ? 'Replay' : undefined, b.program.start ? clock(b.program.start) : undefined].filter(Boolean).join(' · ')}
                  </span>
                )}
              </button>
            ))}
          </div>,
          document.body,
        )}
    </>
  );
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
  language,
  alternatives,
  onPickChannel,
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
  language?: string;
  alternatives?: Broadcast[];
  /** Plays the match on one of the other channels. */
  onPickChannel?: (channel: CatalogItem) => void;
  onClick: () => void;
}) {
  // Switched to another channel for this match: the card shows and plays that one.
  const playingId = usePlayingChannel();
  const playingAlt = playingId && playingId !== channel.id ? alternatives?.find((b) => b.channel.id === playingId) : undefined;
  const shown = playingAlt?.channel ?? channel;
  const others = playingAlt
    ? [{ channel, program, language }, ...(alternatives ?? []).filter((b) => b !== playingAlt)]
    : alternatives;
  const isPlaying = playing || (!!playingId && playingId === shown.id);
  const open = () => (playingAlt && onPickChannel ? onPickChannel(shown) : onClick());
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
  const [broken, setBroken] = useState<string>();
  const wanted = cardImage(info, !!matchup, program);
  // A dead image link falls back to the logos or the sport card instead of an empty frame.
  const image = wanted && wanted !== broken ? wanted : undefined;

  const badge = program.replay ? (
    <span className="flex items-center gap-1 rounded-md bg-black/70 px-1.5 py-0.5 text-[11px] font-semibold text-white/85">
      <History size={12} /> Replay{program.start > now ? ` · ${clock(program.start)}` : ''}
    </span>
  ) : info?.live ? (
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
    <div
      role="button"
      tabIndex={0}
      onClick={open}
      onKeyDown={(e) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          open();
        }
      }}
      className={`group relative cursor-pointer flex w-80 shrink-0 flex-col overflow-hidden rounded-2xl text-left transition ${
        isPlaying
          ? 'bg-base-200 ring-2 ring-primary'
          : highlight
            ? 'bg-base-200 ring-1 ring-accent/60 hover:bg-base-100'
            : 'bg-base-200 hover:bg-base-100'
      }`}
    >
      <div className="relative aspect-video w-full overflow-hidden bg-base-100">
        {image ? (
          <Art
            src={image}
            width={IMAGE_W}
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
          <div className="relative flex h-full flex-col justify-end overflow-hidden p-4" style={{ background: sportLook.color }}>
            <SportIcon className="absolute -bottom-6 -right-4 h-36 w-36 text-white opacity-[0.09]" strokeWidth={1.5} />
            <span className="text-[11px] font-semibold uppercase tracking-wider text-white/60">{tag ?? 'Sport'}</span>
            <span className="mt-1 line-clamp-2 max-w-[80%] text-lg font-bold leading-tight text-white">
              {cleanTournament(info?.tournament) ?? (prefix && !sportPrefix ? prefix : undefined) ?? (matchup ? `${matchup[0]} vs ${matchup[1]}` : rest)}
            </span>
          </div>
        )}
        <div className="absolute right-2 top-2">{badge}</div>
        <div className="absolute left-2 top-2 flex max-w-[58%]">
          <ChannelChip channel={shown} alternatives={others} onPick={onPickChannel} />
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
    </div>
  );
}
