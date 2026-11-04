import { useMemo } from 'react';
import { Tv } from 'lucide-react';
import { byPriority, isLiveOrJustEnded, isOver, normalize, type CatalogItem, type SportEvent, type Suggestion } from '../lib/api';
import {
  useAiPicks,
  useCatalog,
  useGuideNow,
  useLibrary,
  useNav,
  useShowVod,
  useSport,
  useSuggestions,
} from '../Context/AppContext';
import { Empty, PosterCard, Row, logoOf } from '../Components/Cards';
import ProgramCard from '../Components/ProgramCard';
import ChannelNowCard from '../Components/ChannelNowCard';
import { openItem, progressOf, resumeEntry } from '../lib/open';
import { playLive } from '../lib/stores';

function recent(items: CatalogItem[] | undefined, n = 30) {
  if (!items) return [];
  return [...items].sort((a, b) => b.added - a.added).slice(0, n);
}

// Channels spell the same match differently ("SK Slavia Prag", "Slavia Prag"), so any shared detail at the same kick-off counts.
const CLUB_AFFIXES = new Set(['fc', 'sk', 'ssc', 'afc', 'cf', 'ac', 'sc', 'if', 'ff', 'bk', 'fk', 'sv', 'as', 'rb']);
const club = (team: string) =>
  normalize(team)
    .split(' ')
    .filter((w) => !CLUB_AFFIXES.has(w))
    .join(' ');

function showKeys(s: Suggestion) {
  const at = s.info?.kickoff ?? s.program.start;
  const keys = [`${at}|${normalize(s.program.title)}`];
  if (s.info?.homeLogo) keys.push(`${at}|${s.info.homeLogo}`);
  if (s.teams.length) keys.push(`${at}|${s.teams.map(club).join('|')}`);
  return keys;
}

export function SportRow({ title, events, onSeeAll }: { title: string; events: SportEvent[]; onSeeAll?: () => void }) {
  const { go } = useNav();
  if (events.length === 0) return null;
  return (
    <Row title={title} onSeeAll={onSeeAll}>
      {events.map((e) => (
        <ProgramCard
          key={e.channel.id + e.program.start}
          program={e.program}
          channel={e.channel}
          live={e.live}
          teams={e.teams}
          tag={e.sport}
          reason={e.reason}
          info={e.info}
          highlight={e.mine}
          language={e.language}
          alternatives={e.alternatives}
          onPickChannel={(c) => {
            void playLive(c, events.map((x) => x.channel));
            go({ name: 'player' });
          }}
          onClick={() => {
            void playLive(e.channel, events.map((x) => x.channel));
            go({ name: 'player' });
          }}
        />
      ))}
    </Row>
  );
}

export default function Home() {
  const { go } = useNav();
  const showVod = useShowVod();
  const live = useCatalog('live');
  const movies = useCatalog('movie', showVod);
  const series = useCatalog('series', showVod);
  const { data: lib } = useLibrary();
  const { data: suggestions } = useSuggestions();
  const { data: sport } = useSport();
  const { data: guideNow } = useGuideNow();
  const { data: picks } = useAiPicks();
  const worthWatching = useMemo(() => {
    const now = Date.now();
    return (picks?.live ?? []).filter((s) => !isOver(s, now));
  }, [picks]);
  const picking = picks?.status === 'loading';
  const forYou = useMemo(() => {
    const rank = { team: 4, show: 3, league: 3, top: 2, country: 1, habit: 0 } as Record<string, number>;
    const now = Date.now();
    const picked = new Set(worthWatching.flatMap(showKeys));
    return (suggestions ?? []).filter((s) => !isOver(s, now) && !showKeys(s).some((k) => picked.has(k))).sort(
      (a, b) => (rank[b.match] ?? 0) - (rank[a.match] ?? 0) || Number(b.live) - Number(a.live) || a.program.start - b.program.start,
    );
  }, [suggestions, worthWatching]);

  const byId = useMemo(() => new Map(live.data?.items.map((i) => [i.id, i]) ?? []), [live.data]);

  // Favorites first, then the channels you watch the most.
  const yourChannels = useMemo(() => {
    const ids: string[] = [];
    for (const f of lib?.favorites ?? []) if (f.kind === 'live') ids.push(f.id);
    const minutes = new Map<string, number>();
    for (const w of lib?.liveWatches ?? []) minutes.set(w.channelId, (minutes.get(w.channelId) ?? 0) + w.minutes);
    for (const h of lib?.history ?? []) if (h.kind === 'live' && !minutes.has(h.id)) minutes.set(h.id, 0.1);
    for (const [id] of [...minutes].sort((a, b) => b[1] - a[1])) if (!ids.includes(id)) ids.push(id);
    return ids.map((id) => byId.get(id)).filter(Boolean).slice(0, 20) as CatalogItem[];
  }, [lib, byId]);

  const liveSport = useMemo(() => {
    const list = (sport ?? []).filter((e) => e.teams.length === 2 && isLiveOrJustEnded(e));
    // Importance, then matches kicking off, then kick-off time; cards stay put when scores and images arrive.
    const now = Date.now();
    const relevant = list.filter((e) => e.tier > 0).sort((a, b) => byPriority(a, b, now));
    const rest = list.filter((e) => e.tier === 0).sort((a, b) => byPriority(a, b, now));
    return (relevant.length >= 4 ? relevant : [...relevant, ...rest]).slice(0, 24);
  }, [sport]);

  const continueWatching = useMemo(
    () =>
      showVod
        ? (lib?.history ?? []).filter((h) => {
            const p = progressOf(h);
            return (h.kind === 'movie' || h.kind === 'episode') && p !== undefined && p > 0.01 && p < 0.95;
          })
        : [],
    [lib, showVod],
  );

  if (!live.isLoading && !live.data?.items.length)
    return <Empty icon={<Tv size={56} />} title="No channels yet" text="Your provider returned no channels. Try refreshing." />;

  return (
    <div className="flex flex-col gap-9 pb-16 pt-2">
      {(!!worthWatching.length || picking) && (
        <Row title="Worth watching today" busy={picking}>
          {worthWatching.map((x) => (
            <ProgramCard
              key={x.channel.id + x.program.start}
              program={x.program}
              channel={x.channel}
              live={x.live}
              teams={x.teams}
              reason={x.reason}
              info={x.info}
              language={x.language}
              alternatives={x.alternatives}
              onPickChannel={(c) => {
                void playLive(c, worthWatching.map((y) => y.channel));
                go({ name: 'player' });
              }}
              onClick={() => {
                void playLive(x.channel, worthWatching.map((y) => y.channel));
                go({ name: 'player' });
              }}
            />
          ))}
        </Row>
      )}

      {!!forYou.length && (
        <Row title="For you">
          {forYou.map((x) => (
            <ProgramCard
              key={x.channel.id + x.program.start}
              program={x.program}
              channel={x.channel}
              live={x.live}
              teams={x.teams}
              reason={x.reason}
              info={x.info}
              language={x.language}
              alternatives={x.alternatives}
              onPickChannel={(c) => {
                void playLive(c, forYou.map((y) => y.channel));
                go({ name: 'player' });
              }}
              onClick={() => {
                void playLive(x.channel, forYou.map((y) => y.channel));
                go({ name: 'player' });
              }}
            />
          ))}
        </Row>
      )}

      <SportRow title="Live sport" events={liveSport} onSeeAll={() => go({ name: 'sport' })} />

      {yourChannels.length > 0 && (
        <Row title="Your channels" onSeeAll={() => go({ name: 'live', categoryId: '_fav' })}>
          {yourChannels.map((c) => (
            <ChannelNowCard
              key={c.id}
              channelId={c.id}
              name={c.name}
              logo={logoOf(c)}
              logoAlt={c.icon}
              now={c.epgId ? guideNow?.[c.epgId] : undefined}
              onClick={() => {
                void playLive(c, yourChannels);
                go({ name: 'player' });
              }}
            />
          ))}
        </Row>
      )}

      {continueWatching.length > 0 && (
        <Row title="Continue watching">
          {continueWatching.map((h) => (
            <PosterCard
              key={h.key}
              title={h.seriesName ?? h.name}
              subtitle={h.subtitle}
              poster={h.icon}
              progress={progressOf(h)}
              onClick={() => resumeEntry(go, h)}
            />
          ))}
        </Row>
      )}
      {showVod && !!picks?.vod?.length && (
        <Row title="Picked for you">
          {picks.vod.map((p) => (
            <PosterCard
              key={p.kind + p.item.id}
              title={p.item.name}
              poster={p.item.icon}
              rating={p.item.rating}
              note={p.why}
              onClick={() => openItem(go, p.kind, p.item)}
            />
          ))}
        </Row>
      )}
      {showVod && !!movies.data?.items.length && (
        <Row title="New movies" onSeeAll={() => go({ name: 'discover', kind: 'movie' })}>
          {recent(movies.data.items).map((m) => (
            <PosterCard key={m.id} title={m.name} poster={m.icon} rating={m.rating} subtitle={m.year} onClick={() => openItem(go, 'movie', m)} />
          ))}
        </Row>
      )}
      {showVod && !!series.data?.items.length && (
        <Row title="Recently updated series" onSeeAll={() => go({ name: 'discover', kind: 'series' })}>
          {recent(series.data.items).map((m) => (
            <PosterCard key={m.id} title={m.name} poster={m.icon} rating={m.rating} subtitle={m.year} onClick={() => openItem(go, 'series', m)} />
          ))}
        </Row>
      )}
    </div>
  );
}
