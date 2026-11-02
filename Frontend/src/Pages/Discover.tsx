import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { ChevronDown, Clapperboard, Loader2, Search } from 'lucide-react';
import { normalize, type CatalogItem } from '../lib/api';
import { useCatalog, useLibrary, useNav } from '../Context/AppContext';
import { Empty, PosterCard } from '../Components/Cards';
import { openItem } from '../lib/open';

type Sort = 'added' | 'rating' | 'name' | 'year';
const SORTS: { id: Sort; label: string }[] = [
  { id: 'added', label: 'Recently added' },
  { id: 'rating', label: 'Rating' },
  { id: 'year', label: 'Release year' },
  { id: 'name', label: 'Name' },
];

const CARD_W = 160;
const GAP = 20;

export default function Discover({ kind, categoryId }: { kind: 'movie' | 'series'; categoryId?: string }) {
  const { go } = useNav();
  const { data, isLoading } = useCatalog(kind);
  const { data: lib } = useLibrary();
  const [cat, setCat] = useState<string>(categoryId ?? '');
  const [sort, setSort] = useState<Sort>('added');
  const [filter, setFilter] = useState('');
  const [catSearch, setCatSearch] = useState('');
  const scrollRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(1000);

  useEffect(() => {
    setCat(categoryId ?? '');
    setFilter('');
  }, [kind, categoryId]);

  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setWidth(el.clientWidth - 64));
    ro.observe(el);
    return () => ro.disconnect();
  }, [isLoading]);

  const favs = useMemo(() => new Set(lib?.favorites.filter((f) => f.kind === kind).map((f) => f.id)), [lib, kind]);
  const catName = data?.categories.find((c) => c.id === cat)?.name ?? 'All genres';

  const items = useMemo(() => {
    let list: CatalogItem[] = data?.items ?? [];
    if (cat) list = list.filter((i) => i.categoryId === cat);
    const f = normalize(filter);
    if (f) {
      const tokens = f.split(' ');
      list = list.filter((i) => {
        const n = normalize(i.name);
        return tokens.every((t) => n.includes(t));
      });
    }
    const sorted = [...list];
    switch (sort) {
      case 'added':
        sorted.sort((a, b) => b.added - a.added);
        break;
      case 'rating':
        sorted.sort((a, b) => (b.rating ?? 0) - (a.rating ?? 0));
        break;
      case 'year':
        sorted.sort((a, b) => Number(b.year ?? 0) - Number(a.year ?? 0));
        break;
      case 'name':
        sorted.sort((a, b) => a.name.localeCompare(b.name));
        break;
    }
    return sorted;
  }, [data, cat, filter, sort]);

  const cols = Math.max(2, Math.floor((width + GAP) / (CARD_W + GAP)));
  const cardW = Math.floor((width - GAP * (cols - 1)) / cols);
  const rowH = Math.round(cardW * 1.5) + 62;
  const rows = Math.ceil(items.length / cols);

  const virt = useVirtualizer({
    count: rows,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => rowH + GAP,
    overscan: 3,
  });

  useEffect(() => {
    virt.measure();
  }, [rowH]);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: 0 });
  }, [cat, sort, filter, kind]);

  if (isLoading)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Loading {kind === 'movie' ? 'movies' : 'series'}
      </div>
    );
  if (!data?.items.length)
    return <Empty icon={<Clapperboard size={56} />} title={`No ${kind === 'movie' ? 'movies' : 'series'}`} />;

  const cats = data.categories.filter((c) => !catSearch || normalize(c.name).includes(normalize(catSearch)));

  return (
    <div className="flex h-full flex-col">
      <div className="flex flex-wrap items-center gap-3 px-8 pb-4">
        <h1 className="mr-2 text-2xl font-bold">{kind === 'movie' ? 'Movies' : 'Series'}</h1>
        <div className="dropdown">
          <div tabIndex={0} role="button" className="btn btn-sm h-9 gap-2 rounded-full border-0 bg-white/[0.08] font-normal hover:bg-white/[0.12]">
            <span className="max-w-56 truncate">{catName}</span> <ChevronDown size={14} />
          </div>
          <div tabIndex={0} className="dropdown-content panel z-50 mt-2 flex max-h-[60vh] w-72 flex-col rounded-box p-2">
            <input
              value={catSearch}
              onChange={(e) => setCatSearch(e.target.value)}
              placeholder="Find a genre"
              className="input input-sm mb-2 w-full bg-white/5"
            />
            <ul className="menu w-full flex-nowrap overflow-y-auto p-0">
              <li>
                <a className={!cat ? 'menu-active' : ''} onClick={() => { setCat(''); (document.activeElement as HTMLElement)?.blur(); }}>
                  All genres <span className="ml-auto text-xs opacity-50">{data.items.length}</span>
                </a>
              </li>
              {cats.map((c) => (
                <li key={c.id}>
                  <a className={cat === c.id ? 'menu-active' : ''} onClick={() => { setCat(c.id); (document.activeElement as HTMLElement)?.blur(); }}>
                    <span className="truncate">{c.name}</span>
                    <span className="ml-auto text-xs opacity-50">{c.count}</span>
                  </a>
                </li>
              ))}
            </ul>
          </div>
        </div>
        <div className="join rounded-full bg-white/[0.06] p-0.5">
          {SORTS.map((s) => (
            <button
              key={s.id}
              onClick={() => setSort(s.id)}
              className={`join-item h-8 rounded-full px-4 text-sm transition ${sort === s.id ? 'bg-primary text-white' : 'text-white/70 hover:text-white'}`}
            >
              {s.label}
            </button>
          ))}
        </div>
        <label className="ml-auto flex h-9 w-64 items-center gap-2 rounded-full bg-white/[0.06] px-4">
          <Search size={14} className="text-white/40" />
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={`Filter ${items.length.toLocaleString()} titles`}
            className="w-full bg-transparent text-sm outline-none placeholder:text-white/35"
          />
        </label>
      </div>

      <div ref={scrollRef} className="flex-1 overflow-y-auto px-8 pb-10">
        {items.length === 0 && <div className="p-10 text-center text-muted">Nothing matches that filter.</div>}
        <div style={{ height: virt.getTotalSize(), position: 'relative' }}>
          {virt.getVirtualItems().map((row) => (
            <div
              key={row.index}
              className="absolute left-0 right-0 flex"
              style={{ top: row.start, gap: GAP, height: rowH }}
            >
              {items.slice(row.index * cols, row.index * cols + cols).map((m) => (
                <div key={m.id} style={{ width: cardW }}>
                  <PosterCard
                    width="w-full"
                    title={m.name}
                    poster={m.icon}
                    rating={m.rating}
                    subtitle={m.year}
                    favorite={favs.has(m.id)}
                    onClick={() => openItem(go, kind, m)}
                  />
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
