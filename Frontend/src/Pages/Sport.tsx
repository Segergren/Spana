import { useMemo, useState } from 'react';
import { Loader2, Trophy } from 'lucide-react';
import { useSport } from '../Context/AppContext';
import { byPriority, isLiveOrJustEnded, kickoffOf, type SportEvent } from '../lib/api';
import { Empty } from '../Components/Cards';
import { SportRow } from './Home';

function endOfDay(offsetDays: number) {
  const d = new Date();
  d.setHours(24 * (offsetDays + 1), 0, 0, 0);
  return d.getTime();
}

export default function Sport() {
  const { data, isLoading } = useSport();
  const [sport, setSport] = useState<string>('');

  const counts = useMemo(() => {
    const m = new Map<string, number>();
    for (const e of data ?? []) m.set(e.sport, (m.get(e.sport) ?? 0) + 1);
    return [...m].sort((a, b) => b[1] - a[1]);
  }, [data]);

  const sections = useMemo(() => {
    const list = (data ?? []).filter((e) => !sport || e.sport === sport);
    const byRelevance = (a: SportEvent, b: SportEvent) => byPriority(a, b, now);
    const now = Date.now();
    const soon = now + 3 * 3_600_000;
    const today = endOfDay(0);
    const tomorrow = endOfDay(1);
    return {
      mine: list.filter((e) => e.mine && (!e.live || isLiveOrJustEnded(e))).slice(0, 40),
      leagues: list.filter((e) => e.followed && !e.mine && (!e.live || isLiveOrJustEnded(e))).slice(0, 60),
      live: list.filter((e) => isLiveOrJustEnded(e)).sort(byRelevance).slice(0, 80),
      soon: list.filter((e) => !isLiveOrJustEnded(e) && !e.info?.finished && kickoffOf(e) > now && kickoffOf(e) < soon).sort(byRelevance).slice(0, 80),
      today: list.filter((e) => e.program.start >= soon && e.program.start < today).slice(0, 80),
      tomorrow: list.filter((e) => e.program.start >= today && e.program.start < tomorrow).slice(0, 80),
    };
  }, [data, sport]);

  if (isLoading)
    return (
      <div className="flex h-full items-center justify-center text-muted">
        <Loader2 className="mr-2 animate-spin" /> Loading the sport schedule
      </div>
    );
  if (!data?.length)
    return (
      <Empty
        icon={<Trophy size={56} />}
        title="No sport in the guide"
        text="The sport schedule comes from your provider's program guide. It may still be downloading."
      />
    );

  return (
    <div className="flex flex-col gap-8 pb-16">
      <h1 className="-mb-4 px-8 text-2xl font-bold">Sport</h1>
      <div className="no-scrollbar flex shrink-0 items-center gap-2 overflow-x-auto px-8">
        <button
          onClick={() => setSport('')}
          className={`h-8 shrink-0 rounded-full px-4 text-sm transition ${!sport ? 'bg-white text-base-300' : 'bg-white/[0.07] text-white/75 hover:text-white'}`}
        >
          All
        </button>
        {counts.map(([name, count]) => (
          <button
            key={name}
            onClick={() => setSport(name)}
            className={`h-8 shrink-0 rounded-full px-4 text-sm transition ${
              sport === name ? 'bg-white text-base-300' : 'bg-white/[0.07] text-white/75 hover:text-white'
            }`}
          >
            {name} <span className="ml-1 opacity-50">{count}</span>
          </button>
        ))}
      </div>

      <SportRow title="Your teams" events={sections.mine} />
      <SportRow title="Your leagues" events={sections.leagues} />
      <SportRow title="Live now" events={sections.live} />
      <SportRow title="Starting soon" events={sections.soon} />
      <SportRow title="Later today" events={sections.today} />
      <SportRow title="Tomorrow" events={sections.tomorrow} />
    </div>
  );
}
