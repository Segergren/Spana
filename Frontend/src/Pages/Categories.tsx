import { useMemo, useState } from 'react';
import { Loader2, Search } from 'lucide-react';
import { normalize } from '../lib/api';
import { useCatalog, useNav } from '../Context/AppContext';
import { GENRES, colorFor, genreCounts } from '../lib/genres';

export default function Categories() {
  const { go } = useNav();
  const { data, isLoading } = useCatalog('live');
  const [filter, setFilter] = useState('');

  const genres = useMemo(() => {
    if (!data) return [];
    const counts = genreCounts(data.categories, data.items);
    return GENRES.map((g) => ({ ...g, count: counts.get(g.id) ?? 0 })).filter((g) => g.count > 0);
  }, [data]);

  const groups = useMemo(() => {
    const f = normalize(filter);
    return (data?.categories ?? []).filter((c) => !f || normalize(c.name).includes(f));
  }, [data, filter]);

  if (isLoading)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Loading
      </div>
    );

  return (
    <div className="flex flex-col gap-10 px-8 pb-16">
      <section>
        <h1 className="mb-4 text-2xl font-bold">Categories</h1>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(190px,1fr))] gap-4">
          {genres.map((g) => (
            <button
              key={g.id}
              onClick={() => go({ name: 'live', categoryId: `g:${g.id}` })}
              className="group flex aspect-square flex-col justify-end rounded-xl p-4 text-left transition hover:brightness-110"
              style={{ background: g.color }}
            >
              <span className="text-2xl font-bold text-white">{g.name}</span>
              <span className="text-sm text-white/70">{g.count.toLocaleString()} channels</span>
            </button>
          ))}
        </div>
      </section>

      <section>
        <div className="mb-4 flex items-center gap-4">
          <h2 className="text-xl font-semibold">Countries and providers</h2>
          <label className="ml-auto flex h-9 w-64 items-center gap-2 rounded-full bg-white/[0.07] px-4">
            <Search size={14} className="text-white opacity-40" />
            <input
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder={`Filter ${data?.categories.length ?? 0} groups`}
              className="w-full bg-transparent text-sm outline-none placeholder:text-white/35"
            />
          </label>
        </div>
        <div className="grid grid-cols-[repeat(auto-fill,minmax(220px,1fr))] gap-3">
          {groups.map((c) => (
            <button
              key={c.id}
              onClick={() => go({ name: 'live', categoryId: c.id })}
              className="flex h-20 items-end justify-between gap-2 rounded-xl p-3.5 text-left transition hover:brightness-110"
              style={{ background: colorFor(c.name) }}
            >
              <span className="line-clamp-2 font-semibold leading-tight text-white">{c.name}</span>
              <span className="shrink-0 text-xs text-white/60">{c.count}</span>
            </button>
          ))}
        </div>
      </section>
    </div>
  );
}
