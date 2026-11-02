import { useRef, useState, type ReactNode } from 'react';
import { ChevronLeft, ChevronRight, Heart, Play, Star, Tv } from 'lucide-react';
import { img } from '../lib/api';

export function logoOf(c?: { icon?: string; iconAlt?: string }) {
  return c?.iconAlt ?? c?.icon;
}

// A plain image that disappears instead of showing the browser's broken-image icon.
export function SafeImg({ src, className }: { src?: string; className?: string }) {
  const [failedSrc, setFailedSrc] = useState<string | null>(null);
  if (!src || failedSrc === src) return null;
  return <img src={src} draggable={false} decoding="async" onError={() => setFailedSrc(src)} className={className} />;
}

export function Art({
  src,
  altSrc,
  alt,
  className,
  fit = 'cover',
  onFail,
  frame,
}: {
  src?: string;
  altSrc?: string;
  alt?: string;
  className?: string;
  fit?: 'cover' | 'contain';
  /** Called once every source has failed, so the caller can show something else. */
  onFail?: () => void;
  /** Ignored: failed images render nothing. Kept so existing callers stay valid. */
  fallback?: ReactNode;
  /** Wraps the image; nothing at all, wrapper included, is rendered without a working image. */
  frame?: (img: ReactNode) => ReactNode;
}) {
  // Remember which URLs failed rather than how many, so a source that arrives later (a fresh logo link) is still tried.
  const [failed, setFailed] = useState<string[]>([]);
  const sources = [src, altSrc].filter((s, i, all) => s && all.indexOf(s) === i) as string[];
  const current = sources.find((s) => !failed.includes(s));
  const url = img(current);
  // Nothing at all when there is no working image; the card keeps its shape and background.
  if (!url) return frame ? null : <div className={className} />;
  const image = (
    <img
      src={url}
      alt={alt}
      loading="lazy"
      decoding="async"
      draggable={false}
      onError={() => {
        if (!current) return;
        setFailed((f) => (f.includes(current) ? f : [...f, current]));
        if (sources.every((s) => s === current || failed.includes(s))) onFail?.();
      }}
      className={`${fit === 'cover' ? 'object-cover' : 'object-contain'} ${className ?? ''}`}
    />
  );
  return frame ? frame(image) : image;
}

export function PosterCard({
  title,
  poster,
  subtitle,
  rating,
  progress,
  favorite,
  onClick,
  width = 'w-40',
}: {
  title: string;
  poster?: string;
  subtitle?: string;
  rating?: number;
  progress?: number;
  favorite?: boolean;
  onClick: () => void;
  width?: string;
}) {
  return (
    <button onClick={onClick} className={`group flex shrink-0 flex-col gap-2 text-left ${width}`}>
      <div className="poster-focus relative aspect-[2/3] w-full overflow-hidden rounded-xl bg-white/5">
        <Art src={poster} alt={title} className="h-full w-full" />
        <div className="absolute inset-0 flex items-center justify-center bg-black/40 opacity-0 transition-opacity group-hover:opacity-100">
          <div className="flex h-12 w-12 items-center justify-center rounded-full bg-primary/90">
            <Play size={22} fill="white" className="ml-0.5 text-white" />
          </div>
        </div>
        {rating ? (
          <div className="absolute left-2 top-2 flex items-center gap-1 rounded-md bg-black/65 px-1.5 py-0.5 text-xs font-medium text-white">
            <Star size={11} className="fill-warning text-warning" />
            {rating.toFixed(1)}
          </div>
        ) : null}
        {favorite && (
          <Heart size={16} className="absolute right-2 top-2 fill-primary text-primary" />
        )}
        {progress !== undefined && progress > 0 && (
          <div className="absolute inset-x-0 bottom-0 h-1 bg-white/20">
            <div className="h-full bg-primary" style={{ width: `${Math.min(100, progress * 100)}%` }} />
          </div>
        )}
      </div>
      <div className="min-w-0 px-0.5">
        <div className="line-clamp-2 text-sm font-medium leading-snug text-white/90">{title}</div>
        {subtitle && <div className="truncate text-xs text-muted">{subtitle}</div>}
      </div>
    </button>
  );
}

export function ChannelCard({
  title,
  logo,
  subtitle,
  active,
  onClick,
  width = 'w-52',
}: {
  title: string;
  logo?: string;
  subtitle?: string;
  active?: boolean;
  onClick: () => void;
  width?: string;
}) {
  return (
    <button onClick={onClick} className={`group flex shrink-0 flex-col gap-2 text-left ${width}`}>
      <div
        className={`poster-focus relative flex aspect-video w-full items-center justify-center overflow-hidden rounded-xl bg-base-100 p-5 ${
          active ? 'outline-primary' : ''
        }`}
      >
        <Art src={logo} alt={title} fit="contain" className="h-full w-full" fallback={<Tv size={32} />} />
      </div>
      <div className="min-w-0 px-0.5">
        <div className="truncate text-sm font-medium text-white/90">{title}</div>
        {subtitle && <div className="truncate text-xs text-muted">{subtitle}</div>}
      </div>
    </button>
  );
}

export function Row({
  title,
  onSeeAll,
  children,
}: {
  title: string;
  onSeeAll?: () => void;
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const scroll = (dir: number) =>
    ref.current?.scrollBy({ left: dir * ref.current.clientWidth * 0.8, behavior: 'smooth' });
  return (
    <section className="group/row relative">
      <div className="mb-3 flex items-end justify-between px-8">
        <h2 className="text-xl font-semibold text-white">{title}</h2>
        {onSeeAll && (
          <button onClick={onSeeAll} className="btn btn-ghost btn-sm rounded-full text-muted">
            See all <ChevronRight size={16} />
          </button>
        )}
      </div>
      <div ref={ref} className="no-scrollbar flex gap-4 overflow-x-auto scroll-smooth px-8 pb-2 pt-1">
        {children}
      </div>
      <button
        onClick={() => scroll(-1)}
        className="absolute left-1 top-1/2 hidden h-12 w-12 items-center justify-center rounded-full bg-black/60 text-white opacity-0 transition hover:bg-black/80 group-hover/row:flex group-hover/row:opacity-100"
      >
        <ChevronLeft />
      </button>
      <button
        onClick={() => scroll(1)}
        className="absolute right-1 top-1/2 hidden h-12 w-12 items-center justify-center rounded-full bg-black/60 text-white opacity-0 transition hover:bg-black/80 group-hover/row:flex group-hover/row:opacity-100"
      >
        <ChevronRight />
      </button>
    </section>
  );
}

export function Empty({ icon, title, text, action }: { icon: ReactNode; title: string; text?: string; action?: ReactNode }) {
  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 p-10 text-center">
      <div className="text-white/20">{icon}</div>
      <div className="text-lg font-semibold text-white/80">{title}</div>
      {text && <div className="max-w-sm text-sm text-muted">{text}</div>}
      {action}
    </div>
  );
}
