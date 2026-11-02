import { useDeferredValue } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Loader2, SearchX } from 'lucide-react';
import { api } from '../lib/api';
import { useNav, useShowVod } from '../Context/AppContext';
import { ChannelCard, Empty, PosterCard, Row } from '../Components/Cards';
import { openItem } from '../lib/open';
import ProgramCard from '../Components/ProgramCard';
import { playLive } from '../lib/stores';

export default function Search({ q }: { q: string }) {
  const { go } = useNav();
  const showVod = useShowVod();
  const query = useDeferredValue(q.trim());
  const { data, isFetching } = useQuery({
    queryKey: ['search', query],
    queryFn: () => api.search(query, 50),
    enabled: query.length > 0,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
  });

  const total =
    (data?.live.length ?? 0) +
    (data?.programs.length ?? 0) +
    (showVod ? (data?.movie.length ?? 0) + (data?.series.length ?? 0) : 0);
  if (!data && isFetching)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Searching
      </div>
    );
  if (data && total === 0)
    return <Empty icon={<SearchX size={56} />} title={`No results for "${query}"`} text="Try fewer words or a different spelling." />;

  return (
    <div className={`flex flex-col gap-9 pb-16 pt-2 transition-opacity ${isFetching ? 'opacity-70' : ''}`}>
      {!!data?.programs.length && (
        <Row title="Live and upcoming">
          {data.programs.map((x) => (
            <ProgramCard
              key={x.channel.id + x.program.start}
              program={x.program}
              channel={x.channel}
              live={x.live}
              onClick={() => {
                void playLive(x.channel, data.programs.map((y) => y.channel));
                go({ name: 'player' });
              }}
            />
          ))}
        </Row>
      )}
      {!!data?.live.length && (
        <Row title="Channels">
          {data.live.map((c) => (
            <ChannelCard key={c.id} title={c.name} logo={c.icon} onClick={() => openItem(go, 'live', c, data.live)} />
          ))}
        </Row>
      )}
      {showVod && !!data?.movie.length && (
        <Row title="Movies">
          {data.movie.map((m) => (
            <PosterCard key={m.id} title={m.name} poster={m.icon} rating={m.rating} subtitle={m.year} onClick={() => openItem(go, 'movie', m)} />
          ))}
        </Row>
      )}
      {showVod && !!data?.series.length && (
        <Row title="Series">
          {data.series.map((m) => (
            <PosterCard key={m.id} title={m.name} poster={m.icon} rating={m.rating} subtitle={m.year} onClick={() => openItem(go, 'series', m)} />
          ))}
        </Row>
      )}
    </div>
  );
}
