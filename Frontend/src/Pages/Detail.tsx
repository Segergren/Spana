import { useEffect, useMemo, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Calendar, Clock, Heart, Loader2, Play, RotateCcw, Star } from 'lucide-react';
import { api, formatTime, img, player, type Episode, type Meta } from '../lib/api';
import { useIsFavorite, useLibrary, useNav } from '../Context/AppContext';
import { Art, SafeImg } from '../Components/Cards';
import { progressOf, resumePosition } from '../lib/open';
import { playEpisode, type EpisodeQueue } from '../lib/stores';

export default function Detail({ kind, id }: { kind: 'movie' | 'series'; id: string }) {
  const { go } = useNav();
  const qc = useQueryClient();
  const { data: meta, isLoading } = useQuery({
    queryKey: ['meta', kind, id],
    queryFn: () => api.meta(kind, id),
    staleTime: 10 * 60_000,
  });
  const { data: lib } = useLibrary();
  const favorite = useIsFavorite(kind, id);

  const history = useMemo(() => new Map(lib?.history.map((h) => [`${h.kind}:${h.id}`, h]) ?? []), [lib]);
  const movieEntry = history.get(`movie:${id}`);

  const allEpisodes = useMemo(() => meta?.seasons?.flatMap((s) => s.episodes) ?? [], [meta]);
  const lastEpisode = useMemo(() => {
    const eps = lib?.history.filter((h) => h.kind === 'episode' && h.seriesId === id) ?? [];
    return eps[0];
  }, [lib, id]);

  const toggleFav = async () => {
    if (!meta) return;
    await api.toggleFavorite({ kind, id, name: meta.name, icon: meta.poster });
    qc.invalidateQueries({ queryKey: ['library'] });
  };

  const queue: EpisodeQueue | null = meta
    ? { seriesId: id, seriesName: meta.name, poster: meta.poster, episodes: allEpisodes }
    : null;

  const playMovie = (startMs: number) => {
    if (!meta) return;
    void player.play({ kind: 'movie', id, name: meta.name, icon: meta.poster, ext: meta.ext, startMs, subtitle: meta.year });
    go({ name: 'player' });
  };

  const playEp = (ep: Episode, startMs = 0) => {
    if (!queue) return;
    void playEpisode(queue, ep, startMs);
    go({ name: 'player' });
  };

  if (isLoading || !meta)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Loading
      </div>
    );

  const backdrop = img(meta.backdrop ?? meta.poster);
  const movieResume = resumePosition(movieEntry);
  const resumeEp = lastEpisode ? allEpisodes.find((e) => e.id === lastEpisode.id) : undefined;
  const resumeEpMs = resumePosition(lastEpisode);
  const nextUp =
    resumeEp && !resumeEpMs ? allEpisodes[allEpisodes.findIndex((e) => e.id === resumeEp.id) + 1] : undefined;

  return (
    <div className="relative h-full overflow-hidden">
      {backdrop && (
        <div className="absolute inset-0">
          <SafeImg src={backdrop} className="h-full w-full object-cover opacity-35" />
          <div className="absolute inset-0 bg-gradient-to-r from-base-300 via-base-300/85 to-base-300/30" />
          <div className="absolute inset-0 bg-gradient-to-t from-base-300 via-transparent to-transparent" />
        </div>
      )}
      <div className="relative flex h-full gap-8 px-10 pb-8">
        <div className="flex min-w-0 flex-1 flex-col justify-end gap-5 overflow-y-auto pb-6 pt-10">
          <div className="flex items-end gap-6">
            <Art src={meta.poster} className="hidden aspect-[2/3] w-44 shrink-0 rounded-xl xl:block" />
            <div className="min-w-0">
              <h1 className="text-4xl font-bold leading-tight text-white drop- xl:text-5xl">{meta.name}</h1>
              <div className="mt-3 flex flex-wrap items-center gap-x-5 gap-y-2 text-sm text-white/75">
                {meta.year && (
                  <span className="flex items-center gap-1.5">
                    <Calendar size={14} /> {meta.year}
                  </span>
                )}
                {meta.runtime && (
                  <span className="flex items-center gap-1.5">
                    <Clock size={14} /> {meta.runtime}
                  </span>
                )}
                {meta.rating ? (
                  <span className="flex items-center gap-1.5 rounded-md bg-warning/90 px-2 py-0.5 font-semibold text-black">
                    <Star size={13} className="fill-black" /> {meta.rating.toFixed(1)}
                  </span>
                ) : null}
                {kind === 'series' && meta.seasons && (
                  <span>
                    {meta.seasons.length} season{meta.seasons.length === 1 ? '' : 's'}
                  </span>
                )}
              </div>
              {meta.genre && (
                <div className="mt-3 flex flex-wrap gap-2">
                  {meta.genre.split(/[,/]/).map((g) => g.trim()).filter(Boolean).map((g) => (
                    <span key={g} className="rounded-full bg-white/10 px-3 py-1 text-xs text-white/80">
                      {g}
                    </span>
                  ))}
                </div>
              )}
            </div>
          </div>

          {meta.plot && <p className="max-w-3xl text-[15px] leading-relaxed text-white/80">{meta.plot}</p>}
          <Credits meta={meta} />

          <div className="flex flex-wrap items-center gap-3">
            {kind === 'movie' ? (
              <>
                <button onClick={() => playMovie(movieResume)} className="btn btn-primary h-12 rounded-full px-7 text-base">
                  <Play size={18} fill="white" />
                  {movieResume ? `Resume from ${formatTime(movieResume)}` : 'Play'}
                </button>
                {movieResume > 0 && (
                  <button onClick={() => playMovie(0)} className="btn h-12 rounded-full border-0 bg-white/10 px-6 hover:bg-white/15">
                    <RotateCcw size={16} /> Start over
                  </button>
                )}
              </>
            ) : (
              allEpisodes.length > 0 && (
                <button
                  onClick={() =>
                    resumeEp && resumeEpMs ? playEp(resumeEp, resumeEpMs) : playEp(nextUp ?? resumeEp ?? allEpisodes[0])
                  }
                  className="btn btn-primary h-12 rounded-full px-7 text-base"
                >
                  <Play size={18} fill="white" />
                  {resumeEp && resumeEpMs
                    ? `Resume S${resumeEp.season} E${resumeEp.number}`
                    : nextUp
                      ? `Next: S${nextUp.season} E${nextUp.number}`
                      : 'Play S' + allEpisodes[0].season + ' E' + allEpisodes[0].number}
                </button>
              )
            )}
            <button
              onClick={toggleFav}
              className={`btn h-12 rounded-full border-0 px-6 ${favorite ? 'bg-primary/25 text-white' : 'bg-white/10 hover:bg-white/15'}`}
            >
              <Heart size={17} className={favorite ? 'fill-primary text-primary' : ''} />
              {favorite ? 'In library' : 'Add to library'}
            </button>
          </div>
          {kind === 'movie' && movieEntry && progressOf(movieEntry) !== undefined && movieResume > 0 && (
            <div className="h-1 w-80 overflow-hidden rounded-full bg-white/15">
              <div className="h-full bg-primary" style={{ width: `${(progressOf(movieEntry) ?? 0) * 100}%` }} />
            </div>
          )}
        </div>

        {kind === 'series' && meta.seasons && (
          <EpisodePanel meta={meta} history={history} onPlay={playEp} initialSeason={resumeEp?.season ?? nextUp?.season} />
        )}
      </div>
    </div>
  );
}

function Credits({ meta }: { meta: Meta }) {
  const cast = meta.cast?.split(',').map((c) => c.trim()).filter(Boolean).slice(0, 6);
  if (!cast?.length && !meta.director) return null;
  return (
    <div className="grid max-w-3xl grid-cols-[90px_1fr] gap-x-4 gap-y-1.5 text-sm">
      {meta.director && (
        <>
          <span className="text-white/45">Director</span>
          <span className="text-white/80">{meta.director}</span>
        </>
      )}
      {cast?.length ? (
        <>
          <span className="text-white/45">Cast</span>
          <span className="text-white/80">{cast.join(', ')}</span>
        </>
      ) : null}
    </div>
  );
}

function EpisodePanel({
  meta,
  history,
  onPlay,
  initialSeason,
}: {
  meta: Meta;
  history: Map<string, import('../lib/api').LibraryEntry>;
  onPlay: (ep: Episode, startMs?: number) => void;
  initialSeason?: number;
}) {
  const seasons = meta.seasons ?? [];
  const [season, setSeason] = useState(initialSeason ?? seasons[0]?.number);
  useEffect(() => {
    if (initialSeason !== undefined) setSeason(initialSeason);
  }, [initialSeason]);
  const current = seasons.find((s) => s.number === season) ?? seasons[0];

  return (
    <aside className="panel my-4 flex w-[440px] shrink-0 flex-col overflow-hidden rounded-2xl">
      <div className="flex items-center gap-2 border-b border-line p-4">
        <select
          value={current?.number}
          onChange={(e) => setSeason(Number(e.target.value))}
          className="select select-sm w-full rounded-full border-0 bg-white/10"
        >
          {seasons.map((s) => (
            <option key={s.number} value={s.number}>
              {s.number === 0 ? 'Specials' : s.name} ({s.episodes.length})
            </option>
          ))}
        </select>
      </div>
      <div className="flex-1 overflow-y-auto p-2">
        {current?.episodes.map((ep) => {
          const h = history.get(`episode:${ep.id}`);
          const progress = progressOf(h);
          const watched = progress !== undefined && progress >= 0.95;
          const resume = resumePosition(h);
          return (
            <button
              key={ep.id}
              onClick={() => onPlay(ep, resume)}
              className="group flex w-full gap-3 rounded-xl p-2 text-left transition hover:bg-white/[0.07]"
            >
              <div className="relative aspect-video w-36 shrink-0 overflow-hidden rounded-lg bg-white/5">
                <Art src={ep.image ?? meta.backdrop} className="h-full w-full" />
                <div className="absolute inset-0 flex items-center justify-center bg-black/40 opacity-0 transition group-hover:opacity-100">
                  <Play size={22} fill="white" className="text-white" />
                </div>
                {progress !== undefined && progress > 0 && (
                  <div className="absolute inset-x-0 bottom-0 h-1 bg-white/20">
                    <div className="h-full bg-primary" style={{ width: `${Math.min(1, progress) * 100}%` }} />
                  </div>
                )}
              </div>
              <div className="min-w-0 flex-1 py-0.5">
                <div className={`line-clamp-2 text-sm font-medium ${watched ? 'text-white/50' : 'text-white'}`}>
                  {ep.number}. {ep.title}
                </div>
                <div className="mt-0.5 flex gap-2 text-xs text-white/45">
                  {ep.airDate && <span>{ep.airDate.slice(0, 10)}</span>}
                  {ep.durationSecs > 0 && <span>{Math.round(ep.durationSecs / 60)} min</span>}
                  {watched && <span className="text-success">Watched</span>}
                </div>
                {ep.plot && <p className="mt-1 line-clamp-2 text-xs text-white/55">{ep.plot}</p>}
              </div>
            </button>
          );
        })}
      </div>
    </aside>
  );
}
