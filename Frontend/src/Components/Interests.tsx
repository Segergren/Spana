import { useQuery, useQueryClient } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { api, type InterestList, type InterestView } from '../lib/api';

const GROUPS: { key: keyof InterestList; title: string }[] = [
  { key: 'teams', title: 'Teams' },
  { key: 'leagues', title: 'Leagues' },
  { key: 'shows', title: 'Shows' },
  { key: 'countries', title: 'Countries' },
  { key: 'sports', title: 'Sports' },
];

// What the suggestions learned from your viewing, each removable on its own.
export default function Interests() {
  const qc = useQueryClient();
  const { data } = useQuery({ queryKey: ['interests'], queryFn: api.interests, staleTime: 0 });

  const forget = async (group: keyof InterestList, item: InterestView) => {
    qc.setQueryData<InterestList>(['interests'], (d) => d && { ...d, [group]: d[group].filter((i) => i !== item) });
    qc.setQueryData(['interests'], await api.forgetInterest(item.keys));
    for (const key of ['suggestions', 'sport']) qc.invalidateQueries({ queryKey: [key] });
  };

  const groups = GROUPS.filter((g) => data?.[g.key].length);

  return (
    <section className="rounded-2xl bg-white/[0.04] p-6">
      <h2 className="text-lg font-semibold">What you follow</h2>
      <p className="mb-5 mt-1 text-xs text-muted">
        Learned from what you watch on live TV. Removing one forgets the viewing behind it; watching it again teaches Spana again.
      </p>
      {!data ? null : groups.length === 0 ? (
        <div className="text-sm text-muted">Nothing yet. Watch some live TV and suggestions will start to follow your taste.</div>
      ) : (
        <div className="flex flex-col gap-5">
          {groups.map((g) => (
            <div key={g.key}>
              <div className="mb-2 text-[11px] font-semibold uppercase tracking-wider text-white/45">{g.title}</div>
              <div className="flex flex-wrap gap-2">
                {data[g.key].map((item) => (
                  <span
                    key={item.keys.join('|')}
                    className="flex h-8 items-center gap-1.5 rounded-full bg-white/[0.07] pl-3.5 pr-1 text-sm text-white/85"
                  >
                    <span className="max-w-64 truncate">{item.name}</span>
                    {item.detail && <span className="text-xs text-white/40">{item.detail}</span>}
                    <button
                      onClick={() => void forget(g.key, item)}
                      aria-label="Remove"
                      className="flex h-6 w-6 items-center justify-center rounded-full text-white/50 transition hover:bg-white/10 hover:text-white"
                    >
                      <X size={14} />
                    </button>
                  </span>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
