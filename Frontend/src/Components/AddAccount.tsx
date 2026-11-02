import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Loader2 } from 'lucide-react';
import { api } from '../lib/api';

export default function AddAccount({ onDone, onCancel }: { onDone?: () => void; onCancel?: () => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const hasCredentialsInUrl = /username=/.test(url);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const boot = await api.addAccount({
        name,
        url,
        username: username || undefined,
        password: password || undefined,
      });
      qc.setQueryData(['bootstrap'], boot);
      qc.invalidateQueries({ queryKey: ['catalog'] });
      onDone?.();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <fieldset className="fieldset">
        <legend className="fieldset-legend">Name</legend>
        <input className="input w-full" placeholder="My provider" value={name} onChange={(e) => setName(e.target.value)} />
      </fieldset>
      <fieldset className="fieldset">
        <legend className="fieldset-legend">Server address or playlist link</legend>
        <input
          className="input w-full"
          placeholder="http://provider.tv:8080 or a get.php / .m3u link"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          required
        />
        <p className="label text-xs">Xtream Codes links with username and password fill in the rest for you.</p>
      </fieldset>
      {!hasCredentialsInUrl && (
        <div className="grid grid-cols-2 gap-3">
          <fieldset className="fieldset">
            <legend className="fieldset-legend">Username</legend>
            <input className="input w-full" value={username} onChange={(e) => setUsername(e.target.value)} />
          </fieldset>
          <fieldset className="fieldset">
            <legend className="fieldset-legend">Password</legend>
            <input type="password" className="input w-full" value={password} onChange={(e) => setPassword(e.target.value)} />
          </fieldset>
        </div>
      )}
      {error && <div className="rounded-xl bg-error/15 px-4 py-3 text-sm text-error">{error}</div>}
      <div className="flex justify-end gap-2">
        {onCancel && (
          <button type="button" className="btn btn-ghost rounded-full" onClick={onCancel}>
            Cancel
          </button>
        )}
        <button type="submit" className="btn btn-primary rounded-full px-6" disabled={busy || !url}>
          {busy && <Loader2 size={16} className="animate-spin" />} Connect
        </button>
      </div>
    </form>
  );
}
