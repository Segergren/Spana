import { useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, Loader2, Search, Tv } from 'lucide-react';
import { type CatalogItem } from '../lib/api';
import { useCatalog, useGuideNow, useLibrary, useNav } from '../Context/AppContext';
import { GENRES, genreMatcher } from '../lib/genres';
import { Empty, logoOf } from '../Components/Cards';
import ChannelNowCard from '../Components/ChannelNowCard';
import { playLive } from '../lib/stores';

const ALL = '_all';
const FAV = '_fav';
const RECENT = '_recent';
const PAGE = 48;

// The backend takes these out of names; they stay here for catalogs cached before it did.
const VARIANT_WORDS = new Set(['hd', 'fhd', 'sd', 'hevc', 'h265', '50fps']);

const wordCache = new Map<string, string[]>();

// Letters of every script count, so Cyrillic or Greek names do not all collapse into one; "+1" and "TV3+" stay apart.
function words(name: string) {
  let w = wordCache.get(name);
  if (!w) {
    w = name
      .toLowerCase()
      .normalize('NFD')
      .replace(/\p{M}/gu, '')
      .replace(/&/g, ' and ')
      .replace(/\+\s?(\d)/g, ' +$1')
      .replace(/\+(?!\d)/g, ' plus ')
      .split(/[^\p{L}\p{N}+]+/u)
      .filter(Boolean);
    if (wordCache.size > 50_000) wordCache.clear();
    wordCache.set(name, w);
  }
  return w;
}

// "SVT1", "SVT1 HD" and "SVT1 FHD" are the same channel.
function channelKey(name: string) {
  const w = [...words(name)];
  while (w.length > 1 && VARIANT_WORDS.has(w[0])) w.shift();
  while (w.length > 1 && VARIANT_WORDS.has(w[w.length - 1])) w.pop();
  return w.join(' ');
}

const LINES: [string, number][] = [
  ['4k', 2160],
  ['uhd', 2160],
  ['fhd', 1080],
  ['hd', 720],
  ['sd', 576],
];

// Lines of video; untagged streams are often 720p or better, but a stream labelled HD is the surer pick.
function quality(c: CatalogItem) {
  if (c.quality) return c.quality;
  const w = new Set(words(c.name));
  return LINES.find(([tag]) => w.has(tag))?.[1] ?? 700;
}

// A stream that plays beats a sharper one: offline, geo-blocked and part-time versions come last.
const PENALTY: Record<string, number> = {
  offline: 8000,
  geo: 4000,
  'part-time': 2000,
  backup: 100,
  hevc: 1,
};

function rank(c: CatalogItem) {
  let r = quality(c);
  for (const t of c.tags ?? []) r -= PENALTY[t] ?? 0;
  return c.tags?.includes('50fps') ? r + 2 : r;
}

// Channels without a country code take the one most of their group has, so "NRK 1 FHD" meets "NRK 1 HD NO".
function groupCountries(list: CatalogItem[]) {
  const sizes = new Map<string, number>();
  const counts = new Map<string, Map<string, number>>();
  for (const c of list) {
    sizes.set(c.categoryId, (sizes.get(c.categoryId) ?? 0) + 1);
    if (!c.country) continue;
    const m = counts.get(c.categoryId) ?? new Map<string, number>();
    m.set(c.country, (m.get(c.country) ?? 0) + 1);
    counts.set(c.categoryId, m);
  }
  const result = new Map<string, string>();
  for (const [cat, m] of counts) {
    const [country, n] = [...m].sort((a, b) => b[1] - a[1])[0];
    if (n * 2 > (sizes.get(cat) ?? 0)) result.set(cat, country);
  }
  return result;
}

// Eurosport 1 from Sweden and from Germany are different channels; "NRK 1 NO" and "NRK 1" are one.
function versionKey(c: CatalogItem, countries?: Map<string, string>) {
  const country = c.country ?? countries?.get(c.categoryId) ?? '';
  let key = channelKey(c.name);
  if (country && key.startsWith(`${country} `)) key = key.slice(country.length + 1);
  else if (country && key.endsWith(` ${country}`)) key = key.slice(0, -country.length - 1);
  return `${country}|${key}`;
}

function bestVersions(list: CatalogItem[]) {
  const countries = groupCountries(list);
  const best = new Map<string, CatalogItem>();
  for (const c of list) {
    const key = versionKey(c, countries);
    const have = best.get(key);
    if (!have || rank(c) > rank(have)) best.set(key, c);
  }
  return [...best.values()];
}

const TAG_LABELS: Record<string, string> = {
  geo: 'Geo-blocked',
  'part-time': 'Not 24/7',
  backup: 'Backup',
  offline: 'Offline',
  hevc: 'HEVC',
  '50fps': '50 fps',
  '5.1': '5.1',
};

// Same names from different countries get the country, except the viewer's own; alternatives also say what sets each version apart.
function displayNames(list: CatalogItem[], alternatives: boolean, home?: string) {
  const countries = new Map<string, Set<string>>();
  for (const c of list) {
    const k = channelKey(c.name);
    const s = countries.get(k) ?? new Set<string>();
    s.add(c.country ?? '');
    countries.set(k, s);
  }
  return (c: CatalogItem) => {
    let name = c.name;
    if (c.country && c.country !== home && (countries.get(channelKey(c.name))?.size ?? 0) > 1)
      name += ` (${c.country.toUpperCase()})`;
    if (!alternatives) return name;
    const parts = [name];
    if (c.quality) parts.push(c.quality >= 2160 ? '4K' : `${c.quality}p`);
    for (const t of c.tags ?? []) if (TAG_LABELS[t]) parts.push(TAG_LABELS[t]);
    return parts.join(' · ');
  };
}

const SEPARATOR = /^\s*[#=\-*~•★_]{3,}/;

// Providers put heading rows like "SWEDEN" or "------ DE: MUSIK ------" between channels.
function headingFilter(items: CatalogItem[]) {
  const stats = new Map<string, { all: number; guide: number }>();
  for (const i of items) {
    const s = stats.get(i.categoryId ?? '') ?? { all: 0, guide: 0 };
    s.all++;
    if (i.epgId) s.guide++;
    stats.set(i.categoryId ?? '', s);
  }
  return (i: CatalogItem) => {
    if (i.tags?.includes('heading') || SEPARATOR.test(i.name)) return false;
    if (i.epgId) return true;
    // A country code, quality tag or stream note in the provider's name marks a real channel.
    if (i.country || i.quality || i.tags?.length) return true;
    const s = stats.get(i.categoryId ?? '');
    const guided = s && s.all >= 10 && s.guide / s.all >= 0.7;
    return !(guided && words(i.name).length <= 2 && !/\d/.test(i.name));
  };
}

function readStored(key: string, fallback: string) {
  try {
    // Keys were named setv.* before the app became Spana.
    return localStorage.getItem(key) ?? localStorage.getItem(key.replace(/^spana\./, 'setv.')) ?? fallback;
  } catch {
    return fallback;
  }
}

function store(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* storage unavailable */
  }
}

export default function Live({ categoryId }: { categoryId?: string }) {
  const { go } = useNav();
  const { data, isLoading } = useCatalog('live');
  const { data: lib } = useLibrary();
  const { data: guideNow } = useGuideNow();
  const [cat, setCat] = useState(categoryId ?? readStored('spana.liveCat', ALL));
  const [filter, setFilter] = useState('');
  const [alternatives, setAlternatives] = useState(readStored('spana.liveAlternatives', '0') === '1');
  const [shown, setShown] = useState(PAGE);
  const more = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (categoryId) setCat(categoryId);
  }, [categoryId]);

  useEffect(() => store('spana.liveCat', cat), [cat]);
  useEffect(() => store('spana.liveAlternatives', alternatives ? '1' : '0'), [alternatives]);
  useEffect(() => setShown(PAGE), [cat, filter, alternatives]);

  const byId = useMemo(() => new Map(data?.items.map((i) => [i.id, i]) ?? []), [data]);
  const matcher = useMemo(() => genreMatcher(data?.categories ?? []), [data]);
  const isChannel = useMemo(() => headingFilter(data?.items ?? []), [data]);
  const genres = useMemo(
    () => GENRES.filter((g) => (data?.items ?? []).some((i) => matcher(g, i))),
    [data, matcher],
  );

  const channels = useMemo(() => {
    let list: CatalogItem[];
    const genre = cat.startsWith('g:') ? GENRES.find((g) => `g:${g.id}` === cat) : undefined;
    const fromLibrary = (entries?: { kind: string; id: string }[]) =>
      (entries ?? []).filter((f) => f.kind === 'live').map((f) => byId.get(f.id)).filter(Boolean) as CatalogItem[];
    if (cat === FAV) list = fromLibrary(lib?.favorites);
    else if (cat === RECENT) list = fromLibrary(lib?.history);
    else if (genre) list = (data?.items ?? []).filter((i) => matcher(genre, i));
    else if (cat === ALL) list = data?.items ?? [];
    else list = (data?.items ?? []).filter((i) => i.categoryId === cat);
    const tokens = words(filter);
    list = list.filter(
      (i) => isChannel(i) && tokens.every((t) => words(i.name).join(' ').includes(t)),
    );
    return alternatives ? list : bestVersions(list);
  }, [data, cat, filter, lib, byId, matcher, alternatives, isChannel]);
  const label = useMemo(
    () => displayNames(channels, alternatives, data?.home),
    [channels, alternatives, data?.home],
  );

  useEffect(() => {
    const el = more.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) setShown((n) => Math.min(n + PAGE, channels.length));
      },
      { rootMargin: '600px' },
    );
    io.observe(el);
    return () => io.disconnect();
  }, [channels.length, shown]);

  if (isLoading)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Loading channels
      </div>
    );
  if (!data?.items.length) return <Empty icon={<Tv size={56} />} title="No channels" text="This account has no live channels." />;

  const chip = (id: string, label: string) => (
    <button
      key={id}
      onClick={() => setCat(id)}
      className={`h-8 shrink-0 rounded-full px-4 text-sm transition ${
        cat === id ? 'bg-white text-base-300' : 'bg-white/[0.07] text-white/75 hover:text-white'
      }`}
    >
      {label}
    </button>
  );

  const isGroup = cat !== ALL && cat !== FAV && cat !== RECENT && !cat.startsWith('g:');
  const visible = channels.slice(0, shown);

  return (
    <div className="flex flex-col gap-6 pb-16">
      <div className="flex items-center gap-6 px-8">
        <h1 className="text-2xl font-bold">TV</h1>
        <div className="flex items-center gap-3">
          <label className="flex h-9 w-64 items-center gap-2 rounded-full bg-white/[0.07] px-4">
            <Search size={14} className="text-white opacity-40" />
            <input
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
              placeholder="Filter channels"
              className="w-full bg-transparent text-sm outline-none placeholder:text-white/35"
            />
          </label>
          <label className="flex h-9 cursor-pointer items-center gap-2.5 rounded-full bg-white/[0.07] pl-4 pr-3 text-sm text-white/75">
            Show alternatives
            <input
              type="checkbox"
              checked={alternatives}
              onChange={(e) => setAlternatives(e.target.checked)}
              className="toggle toggle-primary toggle-sm"
            />
          </label>
        </div>
      </div>

      <div className="no-scrollbar -mt-2 flex shrink-0 items-center gap-2 overflow-x-auto px-8">
        {chip(ALL, 'All')}
        {chip(FAV, 'Favorites')}
        {chip(RECENT, 'Recently watched')}
        {genres.map((g) => chip(`g:${g.id}`, g.name))}
        <div className="relative shrink-0">
          <select
            value={isGroup ? cat : ''}
            onChange={(e) => e.target.value && setCat(e.target.value)}
            className={`h-8 w-44 cursor-pointer appearance-none truncate rounded-full pl-4 pr-9 text-sm outline-none transition ${
              isGroup ? 'bg-white text-base-300' : 'bg-white/[0.07] text-white/75 hover:text-white'
            }`}
          >
            <option value="" disabled>
              Groups
            </option>
            {data.categories.map((c) => (
              <option key={c.id} value={c.id} className="bg-base-200 text-white">
                {c.name} ({c.count})
              </option>
            ))}
          </select>
          <ChevronDown size={14} className={`pointer-events-none absolute right-3.5 top-1/2 -translate-y-1/2 ${isGroup ? 'text-base-300' : 'text-white/60'}`} />
        </div>
      </div>

      {channels.length === 0 ? (
        <div className="px-8 py-16 text-center text-sm text-muted">No channels here.</div>
      ) : (
        <div className="grid grid-cols-4 gap-4 px-8">
          {visible.map((c) => (
            <ChannelNowCard
              key={c.id}
              channelId={c.id}
              name={label(c)}
              logo={logoOf(c)}
              logoAlt={c.icon}
              now={c.epgId ? guideNow?.[c.epgId] : undefined}
              className="w-full"
              onClick={() => {
                void playLive(c, channels);
                go({ name: 'player' });
              }}
            />
          ))}
        </div>
      )}
      {shown < channels.length && <div ref={more} className="h-px" />}
    </div>
  );
}
