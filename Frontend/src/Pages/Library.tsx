import { useMemo, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Library as LibraryIcon, X } from 'lucide-react';
import { api, type LibraryEntry } from '../lib/api';
import { useLibrary, useNav } from '../Context/AppContext';
import { ChannelCard, Empty, PosterCard } from '../Components/Cards';
import { openEntry, progressOf, resumeEntry } from '../lib/open';

type Tab = 'favorites' | 'history';
type Filter = 'all' | 'live' | 'movie' | 'series';

export default function Library() {
  const { go } = useNav();
  const qc = useQueryClient();
  const { data } = useLibrary();
  const [tab, setTab] = useState<Tab>('favorites');
  const [filter, setFilter] = useState<Filter>('all');

  const list = useMemo(() => {
    const src = (tab === 'favorites' ? data?.favorites : data?.history) ?? [];
    if (filter === 'all') return src;
    return src.filter((e) => (filter === 'series' ? e.kind === 'series' || e.kind === 'episode' : e.kind === filter));
  }, [data, tab, filter]);

  const remove = async (e: LibraryEntry) => {
    if (tab === 'favorites') await api.toggleFavorite(e);
    else if (e.key) await api.removeHistory(e.key);
    qc.invalidateQueries({ queryKey: ['library'] });
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-3 px-8 pb-5">
        <h1 className="mr-2 text-2xl font-bold">Library</h1>
        <div className="join rounded-full bg-white/[0.06] p-0.5">
          {(['favorites', 'history'] as Tab[]).map((t) => (
            <button
              key={t}
              onClick={() => setTab(t)}
              className={`join-item h-8 rounded-full px-4 text-sm capitalize ${tab === t ? 'bg-primary text-white' : 'text-white/70 hover:text-white'}`}
            >
              {t === 'favorites' ? 'Favorites' : 'Watch history'}
            </button>
          ))}
        </div>
        <div className="ml-auto flex gap-1">
          {(['all', 'live', 'movie', 'series'] as Filter[]).map((f) => (
            <button
              key={f}
              onClick={() => setFilter(f)}
              className={`h-8 rounded-full px-4 text-sm ${filter === f ? 'bg-white/15 text-white' : 'text-white/60 hover:text-white'}`}
            >
              {{ all: 'All', live: 'Channels', movie: 'Movies', series: 'Series' }[f]}
            </button>
          ))}
        </div>
      </div>
      <div className="flex-1 overflow-y-auto px-8 pb-10">
        {list.length === 0 ? (
          <Empty
            icon={<LibraryIcon size={56} />}
            title={tab === 'favorites' ? 'No favorites yet' : 'Nothing watched yet'}
            text={tab === 'favorites' ? 'Add channels, movies and series to your library to find them here.' : undefined}
          />
        ) : (
          <div className="grid grid-cols-[repeat(auto-fill,minmax(160px,1fr))] gap-5">
            {list.map((e) => (
              <div key={e.key} className="group/item relative">
                {e.kind === 'live' ? (
                  <ChannelCard width="w-full" title={e.name} logo={e.icon} onClick={() => openEntry(go, e)} />
                ) : (
                  <PosterCard
                    width="w-full"
                    title={e.seriesName ?? e.name}
                    subtitle={e.subtitle}
                    poster={e.icon}
                    progress={tab === 'history' ? progressOf(e) : undefined}
                    onClick={() => (tab === 'history' && e.kind !== 'series' ? resumeEntry(go, e) : openEntry(go, e))}
                  />
                )}
                <button
                  onClick={() => remove(e)}
                  className="absolute right-2 top-2 hidden h-7 w-7 items-center justify-center rounded-full bg-black/70 text-white hover:bg-error group-hover/item:flex"
                >
                  <X size={14} />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
