import { useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Loader2, Plus, RefreshCw, Trash2 } from 'lucide-react';
import { api, type AppSettings } from '../lib/api';
import { useBootstrap } from '../Context/AppContext';
import AddAccount from '../Components/AddAccount';
import HiddenChannels from '../Components/HiddenChannels';
import { enhanceEnabled, setEnhanceEnabled, subscribeEnhance } from '../lib/videoRenderer';

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="rounded-2xl bg-white/[0.04] p-6">
      <h2 className="mb-5 text-lg font-semibold">{title}</h2>
      <div className="flex flex-col gap-5">{children}</div>
    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-6">
      <div>
        <div className="text-sm font-medium">{label}</div>
        {hint && <div className="text-xs text-muted">{hint}</div>}
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  );
}

function bytes(n: number) {
  if (n < 1024 * 1024) return `${Math.round(n / 1024)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export default function Settings() {
  const qc = useQueryClient();
  const { data: boot } = useBootstrap();
  const [adding, setAdding] = useState(false);
  const [draft, setDraft] = useState<AppSettings | null>(null);
  const cache = useQuery({ queryKey: ['cacheInfo'], queryFn: api.cacheInfo });
  const enhance = useSyncExternalStore(subscribeEnhance, enhanceEnabled);

  useEffect(() => {
    if (boot && !draft) setDraft(boot.settings);
  }, [boot]);

  if (!boot || !draft) return null;

  const save = async (patch: Partial<AppSettings>) => {
    setDraft({ ...draft, ...patch });
    qc.setQueryData(['bootstrap'], await api.updateSettings(patch));
    if ('hideAdult' in patch || 'hiddenChannels' in patch) {
      for (const key of ['catalog', 'sport', 'suggestions', 'search']) qc.invalidateQueries({ queryKey: [key] });
    }
  };

  return (
    <div className="h-full overflow-y-auto px-8 pb-12">
      <div className="mx-auto flex max-w-3xl flex-col gap-6">
        <h1 className="text-2xl font-bold">Settings</h1>

        <Section title="Accounts">
          {boot.accounts.map((a) => {
            const exp = a.info?.expiresAt ? new Date(a.info.expiresAt * 1000) : undefined;
            const status = boot.activeAccountId === a.id ? boot.catalogs : [];
            return (
              <div key={a.id} className="flex items-center gap-4 rounded-xl bg-white/[0.04] p-4">
                <div className="flex h-10 w-10 items-center justify-center rounded-full bg-primary/80 font-bold uppercase">
                  <span className="cap-center">{a.name.slice(0, 1)}</span>
                </div>
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className="truncate font-medium">{a.name}</span>
                    {boot.activeAccountId === a.id && <span className="badge badge-primary badge-sm">In use</span>}
                    {a.info?.status && (
                      <span className={`badge badge-sm ${a.info.status === 'Active' ? 'badge-success' : 'badge-warning'}`}>
                        {a.info.status}
                      </span>
                    )}
                  </div>
                  <div className="truncate text-xs text-muted">
                    {a.kind === 'xtream' ? `${a.server} · ${a.username}` : a.server}
                  </div>
                  <div className="mt-1 flex flex-wrap gap-x-4 text-xs text-white/50">
                    {exp && <span>Expires {exp.toLocaleDateString()}</span>}
                    {a.info && <span>{a.info.maxConnections} connection{a.info.maxConnections === 1 ? '' : 's'}</span>}
                    {status.map((c) => (
                      <span key={c.kind}>
                        {c.loading ? <Loader2 size={11} className="mr-1 inline animate-spin" /> : null}
                        {c.count.toLocaleString()} {c.kind === 'live' ? 'channels' : c.kind === 'movie' ? 'movies' : 'series'}
                        {c.error ? ' (refresh failed)' : ''}
                      </span>
                    ))}
                  </div>
                </div>
                {boot.activeAccountId !== a.id && (
                  <button
                    className="btn btn-sm rounded-full"
                    onClick={async () => qc.setQueryData(['bootstrap'], await api.activateAccount(a.id))}
                  >
                    <Check size={14} /> Use
                  </button>
                )}
                <button
                  className="btn btn-ghost btn-sm btn-circle text-white/50 hover:text-error"
                  onClick={async () => {
                    qc.setQueryData(['bootstrap'], await api.removeAccount(a.id));
                    qc.invalidateQueries({ queryKey: ['catalog'] });
                  }}
                >
                  <Trash2 size={16} />
                </button>
              </div>
            );
          })}
          {adding ? (
            <div className="rounded-xl bg-white/[0.04] p-5">
              <AddAccount onDone={() => setAdding(false)} onCancel={() => setAdding(false)} />
            </div>
          ) : (
            <button className="btn w-fit rounded-full" onClick={() => setAdding(true)}>
              <Plus size={16} /> Add account
            </button>
          )}
        </Section>

        <Section title="Playback">
          <Field label="Network buffer" hint="Higher values smooth out unstable streams but zap slower">
            <select
              className="select select-sm w-40 rounded-full"
              value={draft.networkCachingMs}
              onChange={(e) => save({ networkCachingMs: Number(e.target.value) })}
            >
              {[500, 1000, 1500, 3000, 5000, 10000].map((v) => (
                <option key={v} value={v}>
                  {v / 1000} s
                </option>
              ))}
            </select>
          </Field>
          <Field label="Hardware decoding" hint="Uses the GPU to decode video">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={draft.hardwareDecoding}
              onChange={(e) => save({ hardwareDecoding: e.target.checked })}
            />
          </Field>
          <Field label="Enhance video" hint="Upscales to your screen with AMD FSR and sharpens edges">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={enhance}
              onChange={(e) => setEnhanceEnabled(e.target.checked)}
            />
          </Field>
          <Field label="Audio sync" hint="Delay the sound if it comes before the picture. Bluetooth headphones often need 150 to 250 ms">
            <select
              className="select select-sm w-40 rounded-full"
              value={draft.audioDelayMs ?? 0}
              onChange={(e) => save({ audioDelayMs: Number(e.target.value) })}
            >
              {[-100, -50, -25, 0, 25, 50, 100, 150, 200, 250, 300, 400].map((v) => (
                <option key={v} value={v}>
                  {v === 0 ? 'In sync' : `${v > 0 ? '+' : ''}${v} ms`}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Preferred audio language" hint="Language code, for example sv or en">
            <input
              className="input input-sm w-40 rounded-full"
              value={draft.preferredAudioLanguage}
              onChange={(e) => setDraft({ ...draft, preferredAudioLanguage: e.target.value })}
              onBlur={() => save({ preferredAudioLanguage: draft.preferredAudioLanguage })}
            />
          </Field>
          <Field label="Preferred subtitle language" hint="Leave empty to keep subtitles off">
            <input
              className="input input-sm w-40 rounded-full"
              value={draft.preferredSubtitleLanguage}
              onChange={(e) => setDraft({ ...draft, preferredSubtitleLanguage: e.target.value })}
              onBlur={() => save({ preferredSubtitleLanguage: draft.preferredSubtitleLanguage })}
            />
          </Field>
        </Section>

        <Section title="Catalog and cache">
          <Field label="Refresh channels every" hint="Channel lists are kept on disk and refreshed in the background">
            <select
              className="select select-sm w-40 rounded-full"
              value={draft.liveCacheHours}
              onChange={(e) => save({ liveCacheHours: Number(e.target.value) })}
            >
              {[1, 3, 6, 12, 24].map((v) => (
                <option key={v} value={v}>
                  {v} h
                </option>
              ))}
            </select>
          </Field>
          <Field label="Refresh movies and series every">
            <select
              className="select select-sm w-40 rounded-full"
              value={draft.vodCacheHours}
              onChange={(e) => save({ vodCacheHours: Number(e.target.value) })}
            >
              {[6, 12, 24, 48, 168].map((v) => (
                <option key={v} value={v}>
                  {v < 48 ? `${v} h` : `${v / 24} days`}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Movies and series" hint="Show the on-demand library next to live TV">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={draft.showVod}
              onChange={(e) => save({ showVod: e.target.checked })}
            />
          </Field>
          <Field label="Match data" hint="Images, live scores and team logos from public sports services, fetched through spana.tv">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={draft.matchData}
              onChange={(e) => save({ matchData: e.target.checked })}
            />
          </Field>
          <Field label="Hide adult content">
            <input
              type="checkbox"
              className="toggle toggle-primary"
              checked={draft.hideAdult}
              onChange={(e) => save({ hideAdult: e.target.checked })}
            />
          </Field>
          <Field label="Catalogs" hint="Download fresh lists from the provider now">
            <button className="btn btn-sm rounded-full" onClick={() => api.refresh()}>
              <RefreshCw size={14} /> Refresh now
            </button>
          </Field>
          <Field label="Live suggestions" hint="Forget what you have watched on live TV and start over">
            <button
              className="btn btn-sm rounded-full"
              onClick={async () => {
                await api.clearWatchProfile();
                qc.invalidateQueries({ queryKey: ['suggestions'] });
              }}
            >
              <Trash2 size={14} /> Reset
            </button>
          </Field>
          <Field label="Image cache" hint={cache.data ? `${bytes(cache.data.imageBytes)} of logos and posters on disk` : undefined}>
            <button
              className="btn btn-sm rounded-full"
              onClick={async () => {
                await api.clearImageCache();
                cache.refetch();
              }}
            >
              <Trash2 size={14} /> Clear
            </button>
          </Field>
        </Section>

        <HiddenChannels value={draft.hiddenChannels ?? []} onChange={(hiddenChannels) => save({ hiddenChannels })} />

        <Section title="Network">
          <div>
            <div className="mb-2 text-sm font-medium">User agent</div>
            <input
              className="input input-sm w-full rounded-full font-mono text-xs"
              value={draft.userAgent}
              onChange={(e) => setDraft({ ...draft, userAgent: e.target.value })}
              onBlur={() => save({ userAgent: draft.userAgent })}
            />
          </div>
        </Section>

        <Section title="Updates">
          <Field label="Update channel" hint="Beta gets new features first and may have rough edges">
            <select
              className="select select-sm w-40 rounded-full"
              value={draft.updateChannel ?? 'stable'}
              onChange={(e) => save({ updateChannel: e.target.value as AppSettings['updateChannel'] })}
            >
              <option value="stable">Stable</option>
              <option value="beta">Beta</option>
            </select>
          </Field>
        </Section>

        <div className="text-center text-xs text-white/30">Spana {boot.version}</div>
      </div>
    </div>
  );
}
