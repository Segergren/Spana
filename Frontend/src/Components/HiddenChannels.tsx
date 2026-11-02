import { useEffect, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { ChevronDown, EyeOff, Plus, X } from 'lucide-react';
import { api, type HiddenPreview } from '../lib/api';

const EXAMPLES = ['AlienPlay: *', 'NO: *', '* 4K', 'Viaplay ?'];

function plural(n: number) {
  return `${n.toLocaleString()} channel${n === 1 ? '' : 's'}`;
}

function useDebounced<T>(value: T, ms: number) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

function Names({ names, more }: { names: string[]; more: number }) {
  return (
    <div className="flex flex-wrap gap-1.5">
      {names.map((n) => (
        <span key={n} className="rounded-md bg-white/[0.06] px-2 py-0.5 text-xs text-white/70">
          {n}
        </span>
      ))}
      {more > 0 && <span className="px-1 py-0.5 text-xs text-white/40">and {more.toLocaleString()} more</span>}
    </div>
  );
}

function Rule({
  pattern,
  info,
  onRemove,
}: {
  pattern: string;
  info?: HiddenPreview['patterns'][number];
  onRemove: () => void;
}) {
  const [open, setOpen] = useState(false);
  const count = info?.count;
  return (
    <div className="rounded-xl bg-white/[0.04]">
      <div className="flex items-center gap-3 py-2 pl-4 pr-2">
        <button
          onClick={() => setOpen(!open)}
          disabled={!count}
          className="flex min-w-0 flex-1 items-center gap-3 text-left disabled:cursor-default"
        >
          <span className="truncate font-mono text-sm text-white">{pattern}</span>
          <span className={`shrink-0 text-xs ${count === 0 ? 'text-warning' : 'text-white/45'}`}>
            {count === undefined ? '' : count === 0 ? 'Matches nothing' : plural(count)}
          </span>
          {!!count && (
            <ChevronDown size={14} className={`ml-auto shrink-0 text-white opacity-40 transition-transform ${open ? 'rotate-180' : ''}`} />
          )}
        </button>
        <button
          onClick={onRemove}
          title="Show these channels again"
          className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-white opacity-50 hover:bg-white/10 hover:opacity-100"
        >
          <X size={14} />
        </button>
      </div>
      {open && info && info.count > 0 && (
        <div className="max-h-48 overflow-y-auto border-t border-white/5 px-4 py-3">
          <Names names={info.names} more={info.more} />
        </div>
      )}
    </div>
  );
}

export default function HiddenChannels({ value, onChange }: { value: string[]; onChange: (v: string[]) => void }) {
  const [text, setText] = useState('');
  const typed = useDebounced(text.trim(), 200);
  const duplicate = value.some((v) => v.toLowerCase() === text.trim().toLowerCase());

  const saved = useQuery({
    queryKey: ['hiddenPreview', value],
    queryFn: () => api.hiddenPreview(value),
    enabled: value.length > 0,
    placeholderData: keepPreviousData,
  });
  const live = useQuery({
    queryKey: ['hiddenPreview', [typed]],
    queryFn: () => api.hiddenPreview([typed]),
    enabled: typed.length > 0,
    placeholderData: keepPreviousData,
  });
  const preview = typed && text.trim() === typed ? live.data?.patterns[0] : undefined;
  const total = value.length > 0 ? saved.data?.total : 0;

  const add = (p = text.trim()) => {
    if (!p || value.some((v) => v.toLowerCase() === p.toLowerCase())) return;
    onChange([...value, p]);
    setText('');
  };

  return (
    <section className="rounded-2xl bg-white/[0.04] p-6">
      <div className="mb-1 flex items-baseline justify-between gap-4">
        <h2 className="text-lg font-semibold">Hidden channels</h2>
        {!!total && (
          <span className="flex items-center gap-1.5 text-sm text-white/60">
            <EyeOff size={14} className="text-white opacity-60" /> {plural(total)} hidden
          </span>
        )}
      </div>
      <p className="mb-5 text-sm text-muted">
        Hide channels by name everywhere in the app. <span className="font-mono text-white/70">*</span> matches any text and{' '}
        <span className="font-mono text-white/70">?</span> one character.
      </p>

      <form
        className="flex gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <input
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => e.key === 'Escape' && setText('')}
          placeholder="Channel name or pattern, like AlienPlay: *"
          spellCheck={false}
          className="input flex-1 rounded-full font-mono text-sm"
        />
        <button type="submit" className="btn btn-primary rounded-full" disabled={!text.trim() || duplicate}>
          <Plus size={16} /> Hide
        </button>
      </form>

      {text.trim() && (
        <div className="mt-3 rounded-xl bg-white/[0.03] p-4">
          {duplicate ? (
            <div className="text-sm text-white/60">This pattern is already in the list.</div>
          ) : !preview ? (
            <div className="text-sm text-white/40">Looking for matches…</div>
          ) : preview.count === 0 ? (
            <div className="text-sm text-warning">No channels match this yet.</div>
          ) : (
            <div className="flex flex-col gap-2.5">
              <div className="text-sm text-white">Would hide {plural(preview.count)}</div>
              <Names names={preview.names.slice(0, 12)} more={preview.more + Math.max(0, preview.names.length - 12)} />
            </div>
          )}
        </div>
      )}

      {value.length > 0 ? (
        <div className="mt-5 flex flex-col gap-2">
          {value.map((p) => (
            <Rule
              key={p}
              pattern={p}
              info={saved.data?.patterns.find((x) => x.pattern.toLowerCase() === p.toLowerCase())}
              onRemove={() => onChange(value.filter((v) => v !== p))}
            />
          ))}
        </div>
      ) : (
        !text && (
          <div className="mt-4 flex flex-wrap items-center gap-2 text-xs text-white/45">
            Try
            {EXAMPLES.map((e) => (
              <button
                key={e}
                onClick={() => setText(e)}
                className="rounded-full bg-white/[0.06] px-2.5 py-1 font-mono text-white/70 hover:bg-white/10 hover:text-white"
              >
                {e}
              </button>
            ))}
          </div>
        )
      )}
    </section>
  );
}
