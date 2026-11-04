import { useEffect, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Loader2, RefreshCw, Trash2 } from 'lucide-react';
import {
  api,
  clock,
  type AiModel,
  type AiProvider,
  type AiReasoning,
  type AppSettings,
  type SettingsPatch,
} from '../lib/api';
import { useAiPicks } from '../Context/AppContext';
import Field from './Field';

const PROVIDERS: { id: AiProvider; name: string }[] = [
  { id: 'claude', name: 'Claude' },
  { id: 'openai', name: 'OpenAI' },
  { id: 'deepseek', name: 'DeepSeek' },
];

const REASONING: Record<AiReasoning, string> = {
  off: 'Off',
  low: 'Low',
  medium: 'Medium',
  high: 'High',
};

const cap = (w: string) => w.charAt(0).toUpperCase() + w.slice(1);

// Shown until the provider's model list has loaded.
function modelLabel(id: string) {
  const claude = /^claude-([a-z]+)-(\d+)(?:-(\d{1,2}))?(?!\d)/.exec(id);
  if (claude) return `Claude ${cap(claude[1])} ${claude[2]}${claude[3] ? '.' + claude[3] : ''}`;
  return id
    .split('-')
    .map((w) =>
      w === 'gpt'
        ? 'GPT'
        : w === 'deepseek'
          ? 'DeepSeek'
          : /^o\d$/.test(w) || !/^[a-z]/.test(w)
            ? w
            : cap(w),
    )
    .join(' ');
}

export default function AiSettings({
  settings,
  save,
}: {
  settings: AppSettings;
  save: (patch: SettingsPatch) => Promise<void>;
}) {
  const qc = useQueryClient();
  const ai = settings.ai;
  const [pick, setPick] = useState<AiProvider | ''>(ai.provider);
  const [key, setKey] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const { data: picks, refetch } = useAiPicks();

  const saved = pick ? ai.keys[pick] : undefined;
  // Not waiting for the server keeps the fields from blinking away.
  const active = pick !== '' && !!saved;
  const modelsQuery = (p: AiProvider) => ({
    queryKey: ['aiModels', p],
    queryFn: () => api.aiModels(p),
    staleTime: 3_600_000,
  });
  const models = useQuery({ ...modelsQuery(pick || 'claude'), enabled: active });

  useEffect(() => {
    for (const p of PROVIDERS) if (ai.keys[p.id]) void qc.prefetchQuery(modelsQuery(p.id));
  }, [ai.keys]);

  // Keeps a model the provider no longer lists selectable.
  const model = pick ? ai.models[pick] || ai.defaults[pick] : '';
  const listed = models.data?.models ?? [];
  const options: AiModel[] =
    pick && !listed.some((m) => m.id === model)
      ? [{ id: model, name: modelLabel(model), reasoning: ai.reasoningLevels[pick] }, ...listed]
      : listed;
  const levels = options.find((m) => m.id === model)?.reasoning ?? [];
  const fallback = pick ? ai.reasoningDefaults[pick] : 'low';
  const reasoning: AiReasoning =
    ai.reasoning && levels.includes(ai.reasoning)
      ? ai.reasoning
      : levels.includes(fallback)
        ? fallback
        : (levels.find((l) => l !== 'off') ?? levels[0]);

  const choose = async (p: AiProvider | '') => {
    setPick(p);
    setKey('');
    setError(undefined);
    // A new provider waits for its key.
    if (p === '' || ai.keys[p]) await save({ aiProvider: p });
  };

  const saveKey = async (value: string) => {
    if (!pick) return;
    setBusy(true);
    setError(undefined);
    try {
      qc.setQueryData(['bootstrap'], await api.saveAiKey(pick, value));
      setKey('');
      qc.invalidateQueries({ queryKey: ['aiPicks'] });
      qc.invalidateQueries({ queryKey: ['aiModels', pick] });
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="rounded-2xl bg-white/[0.04] p-6">
      <h2 className="text-lg font-semibold">AI picks</h2>
      <p className="mb-5 mt-1 text-xs text-muted">
        Adds what is worth watching today to the start page. Your guide, what you follow and your
        country go straight to the provider with your own API key. The country is looked up once
        from your IP address with ipapi.co.
      </p>
      <div className="flex flex-col gap-5">
        <Field label="Provider">
          <select
            className="select select-sm w-56 rounded-full"
            value={pick}
            onChange={(e) => void choose(e.target.value as AiProvider | '')}
          >
            <option value="">Off</option>
            {PROVIDERS.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </Field>

        {pick && (
          <>
            <Field
              label="API key"
              hint={
                error ? (
                  <span className="text-error">{error}</span>
                ) : pick === 'deepseek' ? (
                  'DeepSeek cannot search the web, so its picks come from the guide alone'
                ) : (
                  'Checked with the provider before it is saved'
                )
              }
            >
              {saved ? (
                <div className="flex items-center gap-2">
                  <span className="font-mono text-xs text-white/60">{saved}</span>
                  <button
                    className="btn btn-sm rounded-full"
                    disabled={busy}
                    onClick={() => void saveKey('')}
                  >
                    <Trash2 size={14} /> Remove
                  </button>
                </div>
              ) : (
                <form
                  className="flex items-center gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (key.trim()) void saveKey(key);
                  }}
                >
                  <input
                    type="password"
                    autoComplete="off"
                    className="input input-sm w-56 rounded-full font-mono text-xs"
                    placeholder="Paste your key"
                    value={key}
                    onChange={(e) => setKey(e.target.value)}
                  />
                  <button
                    className="btn btn-sm btn-primary rounded-full"
                    disabled={busy || !key.trim()}
                  >
                    {busy ? <Loader2 size={14} className="animate-spin" /> : 'Save'}
                  </button>
                </form>
              )}
            </Field>

            {active && (
              <>
                <Field
                  label="Model"
                  hint={
                    models.data?.error && <span className="text-error">{models.data.error}</span>
                  }
                >
                  <select
                    className="select select-sm w-56 rounded-full"
                    value={model}
                    onChange={(e) => void save({ aiModel: e.target.value })}
                  >
                    {options.map((m) => (
                      <option key={m.id} value={m.id}>
                        {m.name}
                        {m.id === ai.defaults[pick] ? ' (default)' : ''}
                      </option>
                    ))}
                  </select>
                </Field>
                {levels.length > 0 && (
                  <Field
                    label="Reasoning"
                    hint={`More is slower and costs more. ${REASONING[fallback]} is the default`}
                  >
                    <select
                      className="select select-sm w-56 rounded-full"
                      value={reasoning}
                      onChange={(e) => void save({ aiReasoning: e.target.value as AiReasoning })}
                    >
                      {levels.map((r) => (
                        <option key={r} value={r}>
                          {REASONING[r]}
                        </option>
                      ))}
                    </select>
                  </Field>
                )}
                <Field
                  label="Picks"
                  hint={
                    picks?.status === 'loading' ? (
                      'Choosing what is worth watching'
                    ) : picks?.status === 'error' ? (
                      <span className="text-error">{picks.error}</span>
                    ) : picks?.generatedAt ? (
                      `Updated ${clock(Date.parse(picks.generatedAt))}, refreshed every few hours`
                    ) : picks?.status === 'waiting' ? (
                      'Waiting for the program guide'
                    ) : undefined
                  }
                >
                  <button
                    className="btn btn-sm rounded-full"
                    disabled={picks?.status === 'loading'}
                    onClick={async () => {
                      try {
                        qc.setQueriesData({ queryKey: ['aiPicks'] }, await api.refreshAiPicks());
                      } catch {
                        void refetch();
                      }
                    }}
                  >
                    {picks?.status === 'loading' ? (
                      <Loader2 size={14} className="animate-spin" />
                    ) : (
                      <RefreshCw size={14} />
                    )}{' '}
                    Refresh now
                  </button>
                </Field>
              </>
            )}
          </>
        )}
      </div>
    </section>
  );
}
