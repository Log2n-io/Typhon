import { useEffect, useState } from 'react';
import { AlertCircle, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { customFetch } from '@/api/client';

interface Props {
  onOpen: (filePath: string, schemaDllPaths: string[]) => void;
  isOpening?: boolean;
}

interface CapabilityResponse {
  available: boolean;
  outputDirectory: string;
}

interface CreateResponse {
  typhonFilePath: string;
  schemaDllPath: string;
  totalEntities: number;
  wasCreated: boolean;
}

/**
 * DEBUG-only "Dev Fixture" tab. Calls the Workbench's internal FixtureDatabase.CreateOrReuse
 * generator to produce (or reuse) a populated test database, then immediately opens it through the
 * shared onOpen handler — same path the Open File tab uses.
 *
 * Parent ConnectDialog gates this tab's render via the capability probe; this component is only
 * instantiated when the server advertises the fixture endpoint. Still queries the capability again
 * locally to grab the default output directory (the server owns the path convention).
 */
export default function DevFixtureTab({ onOpen, isOpening }: Props) {
  const [outputDirectory, setOutputDirectory] = useState<string>('');
  const [force, setForce] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<CreateResponse | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const { data } = await customFetch<{ data: CapabilityResponse }>('/api/fixtures/capability', {
          method: 'GET',
        });
        if (!cancelled) setOutputDirectory(data.outputDirectory);
      } catch {
        /* capability probe fail — parent wouldn't have rendered us, but leave path blank defensively */
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const handleCreate = async () => {
    setBusy(true);
    setError(null);
    try {
      const { data } = await customFetch<{ data: CreateResponse }>('/api/fixtures/create', {
        method: 'POST',
        body: JSON.stringify({ force, outputDirectory }),
      });
      setLastResult(data);
      onOpen(data.typhonFilePath, []);
    } catch (e) {
      const detail = (e && typeof e === 'object' && 'detail' in e ? (e as Record<string, unknown>).detail : null);
      setError(typeof detail === 'string' ? detail : String(e));
    } finally {
      setBusy(false);
    }
  };

  const canClick = outputDirectory.length > 0 && !busy && !isOpening;

  return (
    <div className="flex h-full flex-col gap-4 p-2">
      <div className="rounded-md border border-dashed border-amber-500/40 bg-amber-950/10 p-3 text-fs-base text-muted-foreground">
        <div className="mb-1 flex items-center gap-2 text-foreground">
          <Sparkles className="h-4 w-4 text-amber-400" />
          <span className="font-semibold">Dev fixture database</span>
        </div>
        <p>
          Generates a populated Typhon database with deterministic data across the
          <code className="mx-1 rounded bg-muted px-1">Typhon.Workbench.Fixtures</code>
          archetypes, then opens it. Extend
          <code className="mx-1 rounded bg-muted px-1">FixtureDatabase.Populate</code>
          over time with more shapes as we need to stress-test panels.
        </p>
      </div>

      <div className="flex flex-col gap-1">
        <label className="text-fs-lg text-muted-foreground">Output directory</label>
        <p className="font-mono text-fs-sm text-foreground" title={outputDirectory}>
          {outputDirectory || '(resolving…)'}
        </p>
      </div>

      <label className="flex items-center gap-2 text-fs-base text-foreground">
        <input
          type="checkbox"
          checked={force}
          onChange={(e) => setForce(e.target.checked)}
          className="h-3.5 w-3.5 accent-primary"
        />
        Force recreation
        <span className="text-fs-sm text-muted-foreground">
          (wipe directory + rebuild; otherwise reuse if the database already exists)
        </span>
      </label>

      {error && (
        <div className="flex items-start gap-2 rounded border border-destructive/50 bg-destructive/10 px-2 py-1.5 text-fs-sm text-destructive">
          <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
          <span className="font-mono">{error}</span>
        </div>
      )}

      {lastResult && !error && (
        <p className="text-fs-sm text-muted-foreground">
          {lastResult.wasCreated
            ? `Created ${lastResult.totalEntities.toLocaleString()} entities at`
            : `Reused existing database at`}{' '}
          <span className="font-mono text-foreground">{lastResult.typhonFilePath}</span>
        </p>
      )}

      <div className="mt-auto flex justify-end">
        <Button onClick={handleCreate} disabled={!canClick}>
          {busy ? 'Generating…' : force ? 'Recreate & Open' : 'Create & Open'}
        </Button>
      </div>
    </div>
  );
}
