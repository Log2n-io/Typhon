import { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview';
import { AlertCircle, ChevronDown, ChevronRight, Dice5, FolderOpen, Hourglass, LogOut, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { customFetch } from '@/api/client';
import { deleteApiSessionsId, usePostApiSessionsFile } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useRecentFilesStore, type RecentFileState } from '@/stores/useRecentFilesStore';
import { logError, logInfo, logWarn } from '@/stores/useLogStore';
import { extractDetail } from '@/shell/dialogs/connectErrors';
import {
  BULK_LOAD_AUTO_THRESHOLD,
  CUSTOM_CONFIG_STORAGE_KEY,
  DEFAULT_DATABASE_NAME,
  PLAYERS_PER_GUILD_SHAPE_LABELS,
  PlayersPerGuildShape,
  PRESET_CONFIGS,
  PRESET_LABELS,
  devFixtureFormReducer,
  initialDevFixtureFormState,
  loadOutputDirFromStorage,
  saveCustomConfigToStorage,
  saveDatabaseNameToStorage,
  saveOutputDirToStorage,
  saveUseBulkLoadToStorage,
  toApiFixtureConfig,
  totalEntityCount,
  type DevFixtureFormAction,
  type FixtureBoolKey,
  type FixtureConfig,
  type FixtureFractionKey,
  type FixtureIntKey,
  type PresetId,
} from '@/shell/dialogs/tabs/devFixtureFormReducer';
import { cancelFixtureJob, useFixtureJobPolling } from '@/shell/dialogs/tabs/useFixtureJobPolling';
import { deriveProgressDisplay } from './fixtureProgressDisplay';

interface CapabilityResponse {
  available: boolean;
  outputDirectory: string;
  defaultDatabaseName: string;
}

interface StartJobResponse {
  jobId: string;
}

/**
 * Standalone Dev Fixture panel — same configurability semantics (presets + Advanced form + per-database name
 * + force-recreate) plus an **editable destination folder**: the user can override the server's default
 * `{root}/{databaseName}/` composition with a path of their choosing, persisted to localStorage so it sticks
 * across sessions.
 *
 * <p><b>Gated on no-session</b> — the panel's form renders only when <c>sessionKind === 'none'</c>. With any
 * session open (open / trace / attach), the panel shows a cold state asking the user to close the current
 * session first, with a one-click "Close session" affordance. Reason: fixture generation runs in the default
 * AssemblyLoadContext (it directly references the fixture schema project at compile time), while open sessions
 * load their schema into a per-session collectible ALC. Letting both coexist creates a process-static-registry
 * dance that the engine handles correctly but at meaningful architectural complexity — eliminating the
 * concurrent case by UX gate is simpler and more honest about the constraint.</p>
 *
 * <p>After a successful generation the panel opens the new DB via the same <c>POST /api/sessions/file</c>
 * mutation the Connect dialog uses, so the user lands in a Ready session — at which point the panel itself
 * flips back to the cold "close session first" state until the user closes again.</p>
 *
 * <p>DEBUG-only: the server's <c>/api/fixtures/capability</c> is gated by <c>#if DEBUG</c>; a 404 surfaces a
 * "Dev Fixture is not available in this build" cold state rather than a broken form.</p>
 */
export default function DevFixturePanel(_props: IDockviewPanelProps): React.JSX.Element {
  // Output directory: the server-provided ROOT (`{...}/Fixtures/`); the user's override may replace this entirely.
  const [outputDirectoryRoot, setOutputDirectoryRoot] = useState<string>('');
  const [capabilityProbed, setCapabilityProbed] = useState(false);
  const [capabilityAvailable, setCapabilityAvailable] = useState(false);
  const [outputDirOverride, setOutputDirOverride] = useState<string>(() => loadOutputDirFromStorage() ?? '');
  const [force, setForce] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [jobId, setJobId] = useState<string | null>(null);
  const [form, dispatch] = useReducer(devFixtureFormReducer, undefined, initialDevFixtureFormState);

  const postFile = usePostApiSessionsFile();
  const setSession = useSessionStore((s) => s.setSession);
  const recordRecent = useRecentFilesStore((s) => s.record);
  // Gate the panel's body on `sessionKind === 'none'` — see the class-level docs for the rationale. Reading the
  // kind via a thin selector keeps the component re-render scope minimal (one boolean changes ⇒ one re-render).
  const sessionKind = useSessionStore((s) => s.kind);
  const activeSessionId = useSessionStore((s) => s.sessionId);
  const clearSession = useSessionStore((s) => s.clearSession);
  const [isClosingSession, setIsClosingSession] = useState(false);

  const jobState = useFixtureJobPolling(jobId);
  const isGenerating = jobId !== null && jobState?.state !== 'done' && jobState?.state !== 'error' && jobState?.state !== 'cancelled';
  const isOpening = postFile.isPending;

  // Progress-strip diagnostics: when a job is in flight we want the user to be able to distinguish "slow" from
  // "stuck". Track three values that the pure `deriveProgressDisplay` helper combines into the strip:
  //   - genStartedAtMs — wall-clock at the moment the job id was set; drives elapsed + rate.
  //   - lastCompletedChangeAtMs — bumped every time `jobState.completed` changes; drives stall detection.
  //   - nowTick — a once-per-second self-tick so elapsed / stall flip live even when the job hasn't polled.
  const [genStartedAtMs, setGenStartedAtMs] = useState<number | null>(null);
  const [lastCompletedChangeAtMs, setLastCompletedChangeAtMs] = useState<number | null>(null);
  const lastCompletedRef = useRef<number>(0);
  const [nowTick, setNowTick] = useState<number>(() => Date.now());

  // Reset the wall-clock window when a job starts; clear it when it terminates so a follow-up Generate doesn't
  // inherit the previous run's "elapsed".
  useEffect(() => {
    if (jobId !== null) {
      const now = Date.now();
      setGenStartedAtMs(now);
      setLastCompletedChangeAtMs(now);
      lastCompletedRef.current = 0;
    } else {
      setGenStartedAtMs(null);
      setLastCompletedChangeAtMs(null);
    }
  }, [jobId]);

  // Bump lastCompletedChangeAtMs on any forward movement of `completed`.
  useEffect(() => {
    if (!jobState) return;
    if (jobState.completed !== lastCompletedRef.current) {
      lastCompletedRef.current = jobState.completed;
      setLastCompletedChangeAtMs(Date.now());
    }
  }, [jobState]);

  // 1 Hz self-tick while a job is active — so the elapsed timer + the stall indicator advance without depending
  // on the polling cadence (which itself can stall if the server is busy). Cheap setInterval; cleared on terminate.
  useEffect(() => {
    if (!isGenerating) return;
    const id = window.setInterval(() => setNowTick(Date.now()), 1000);
    return () => window.clearInterval(id);
  }, [isGenerating]);

  const progressDisplay = useMemo(() => {
    if (!jobState || genStartedAtMs === null || lastCompletedChangeAtMs === null) return null;
    return deriveProgressDisplay({
      completed: jobState.completed,
      total: jobState.total,
      genStartedAtMs,
      lastCompletedChangeAtMs,
      nowMs: nowTick,
    });
  }, [jobState, genStartedAtMs, lastCompletedChangeAtMs, nowTick]);

  // Persist Custom config edits on every change while the preset is Custom.
  useEffect(() => {
    if (form.presetId === 'custom') saveCustomConfigToStorage(form.config);
  }, [form.presetId, form.config]);

  // Persist destination-folder override on every change (incl. clear via empty string).
  useEffect(() => {
    saveOutputDirToStorage(outputDirOverride);
  }, [outputDirOverride]);

  // Persist the database name + BulkLoad toggle so the user's last choices survive a dialog re-open.
  useEffect(() => {
    saveDatabaseNameToStorage(form.databaseName);
  }, [form.databaseName]);

  useEffect(() => {
    saveUseBulkLoadToStorage(form.useBulkLoad);
  }, [form.useBulkLoad]);

  // Capability probe — runs ONCE on mount. A 404 (Release build, no /api/fixtures/capability) leaves
  // `capabilityAvailable=false` and the panel renders its "not available" cold state.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const { data } = await customFetch<{ data: CapabilityResponse }>('/api/fixtures/capability', { method: 'GET' });
        if (cancelled) return;
        setCapabilityAvailable(true);
        setOutputDirectoryRoot(data.outputDirectory);
        if (data.defaultDatabaseName && form.databaseName === '') {
          dispatch({ type: 'set-database-name', value: data.defaultDatabaseName });
        }
      } catch {
        /* endpoint absent — capabilityAvailable stays false */
      } finally {
        if (!cancelled) setCapabilityProbed(true);
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Terminal-state handlers: success → open the freshly-generated DB; error/cancel → surface to the UI.
  useEffect(() => {
    if (!jobState) return;
    if (jobState.state === 'done' && jobState.result) {
      void handleOpenGenerated(jobState.result.typhonFilePath);
      setJobId(null);
    } else if (jobState.state === 'error') {
      setError(jobState.error ?? 'Generation failed');
      setJobId(null);
    } else if (jobState.state === 'cancelled') {
      setJobId(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jobState]);

  const handleOpenGenerated = async (filePath: string): Promise<void> => {
    // Mirror of `ConnectDialog.handleOpen` — opens the just-generated DB so the user lands in a Ready session.
    // Inlined (vs. extracted helper) so the panel stays self-contained; the duplication is ~25 lines and bounded.
    logInfo(`Opening database: ${filePath}`, { filePath, source: 'dev-fixture-panel' });
    try {
      const response = await postFile.mutateAsync({ data: { filePath } });
      const dto = response.data;
      setSession(dto);
      recordRecent({
        filePath: dto.filePath ?? filePath,
        schemaDllPaths: (dto.schemaDllPaths as string[] | null | undefined) ?? [],
        lastOpenedAt: new Date().toISOString(),
        lastState: (dto.state as RecentFileState) ?? 'Ready',
        kind: 'db',
      });
      const sessionState = dto.state ?? 'Ready';
      const logLevel = sessionState === 'Incompatible' ? logError : sessionState === 'Ready' ? logInfo : logWarn;
      logLevel(`Session opened — state: ${sessionState} (${dto.loadedComponentTypes ?? 0} component types loaded)`, {
        sessionId: dto.sessionId,
        filePath: dto.filePath ?? filePath,
      });
    } catch (err) {
      logError(`Failed to open database: ${filePath}`, { filePath, error: extractDetail(err) || String(err) });
      setError(`Generated fixture, but failed to open it: ${extractDetail(err) || String(err)}`);
    }
  };

  const handleStart = async (): Promise<void> => {
    setError(null);
    try {
      const body: {
        force: boolean;
        config: ReturnType<typeof toApiFixtureConfig>;
        databaseName: string;
        outputDirectory?: string;
        useBulkLoad: boolean;
      } = {
        force,
        // toApiFixtureConfig is the compile-time guard that the form's config mirrors the server's FixtureConfig
        // record (orval-generated DTO) — a renamed/added server field breaks this call until the mirror is updated.
        config: toApiFixtureConfig(form.config),
        databaseName: form.databaseName,
        useBulkLoad: form.useBulkLoad,
      };
      // Only send `outputDirectory` when the user supplied an override — empty string leaves it undefined so the
      // server falls back to `{root}/{databaseName}/` composition.
      const trimmedOverride = outputDirOverride.trim();
      if (trimmedOverride.length > 0) {
        body.outputDirectory = trimmedOverride;
      }
      const { data } = await customFetch<{ data: StartJobResponse }>('/api/fixtures/create', {
        method: 'POST',
        body: JSON.stringify(body),
      });
      setJobId(data.jobId);
    } catch (e) {
      const detail = (e && typeof e === 'object' && 'detail' in e ? (e as Record<string, unknown>).detail : null);
      setError(typeof detail === 'string' ? detail : String(e));
    }
  };

  const handleCancel = async (): Promise<void> => {
    if (!jobId) return;
    await cancelFixtureJob(jobId);
  };

  const handleResetOutputDir = (): void => {
    setOutputDirOverride('');
  };

  const handleCloseSession = async (): Promise<void> => {
    // Mirror the canonical close path used by the `close-session` palette command (baseCommands.ts):
    // DELETE /api/sessions/{id} then clear the local Zustand store. We don't await the server response
    // in the store-clear path because the local clear must happen even if the network call fails (the
    // session is functionally gone from this client's perspective). `isClosingSession` guards against a
    // double-click during the in-flight window.
    if (activeSessionId === null) {
      return;
    }
    setIsClosingSession(true);
    try {
      await deleteApiSessionsId(activeSessionId);
    } catch {
      /* server-side error logged elsewhere; client-side clear must still proceed */
    } finally {
      clearSession();
      setIsClosingSession(false);
    }
  };

  // ─── Cold states ──────────────────────────────────────────────────────────────────────────────────
  if (!capabilityProbed) {
    return <CenteredMessage>Checking capability…</CenteredMessage>;
  }
  if (!capabilityAvailable) {
    return (
      <CenteredMessage>
        <p className="text-fs-base text-muted-foreground">Dev Fixture is not available in this build.</p>
        <p className="mt-1 text-fs-sm text-muted-foreground">The <code className="rounded bg-muted px-1">/api/fixtures/*</code> endpoints are <code className="rounded bg-muted px-1">#if DEBUG</code>-gated; a Release build of the Workbench omits them.</p>
      </CenteredMessage>
    );
  }

  // ─── No-session gate ──────────────────────────────────────────────────────────────────────────────
  // Fixture generation runs in the default ALC; an open session loads its schema into a collectible ALC.
  // Letting both coexist creates a concurrent-static-state surface the engine handles correctly but at
  // architectural cost — eliminating the case by UX gate is simpler and safer. The cold state below
  // explains the constraint AND surfaces a one-click "Close session" affordance so the user isn't left
  // hunting through menus to comply.
  if (sessionKind !== 'none') {
    return (
      <CenteredMessage>
        <p className="text-fs-base text-foreground">A session is currently open.</p>
        <p className="mt-1 text-fs-sm text-muted-foreground">
          Close the current session before generating a new fixture — fixture creation must run in isolation from
          any open database to avoid schema-loader conflicts between the default and collectible ALCs.
        </p>
        <div className="mt-4 flex justify-center">
          <Button
            onClick={() => void handleCloseSession()}
            disabled={isClosingSession || activeSessionId === null}
            data-testid="devfixture-close-session"
            className="gap-2"
          >
            <LogOut className="h-4 w-4" />
            {isClosingSession ? 'Closing…' : 'Close session'}
          </Button>
        </div>
      </CenteredMessage>
    );
  }

  // ─── Main UI ──────────────────────────────────────────────────────────────────────────────────────
  const dbName = form.databaseName.trim();
  const canClick = form.databaseNameError === null && dbName.length > 0 && !isGenerating && !isOpening;
  const effectiveOutputDir = outputDirOverride.trim().length > 0
    ? outputDirOverride.trim()
    : outputDirectoryRoot.length > 0 ? `${outputDirectoryRoot}/${dbName}` : '';

  // Scale advisory — tiered. Empirically-validated thresholds (4.25 M passes standard path in ~45 s on Zen 4;
  // > 5 M destroy phase thrashes the 512 MB cache; > 15 M effectively never finishes on the standard path).
  // BulkLoad (the engine's opt-in throughput write path) collapses these from hours/never to seconds/minutes.
  // See `claude/scratch/page-cache-backpressure-bulk-writes.md` and `claude/design/Durability/BulkLoad/`.
  const totalEntities = totalEntityCount(form.config);
  const scaleAdvisory: 'small' | 'moderate' | 'bulk-recommended' | 'bulk-required' | null =
    totalEntities >= 15_000_000 ? 'bulk-required'
    : totalEntities >= BULK_LOAD_AUTO_THRESHOLD ? 'bulk-recommended'
    : totalEntities >= 1_000_000 ? 'moderate'
    : null;

  return (
    <div className="flex h-full w-full flex-col overflow-auto bg-background">
      <div className="mx-auto flex w-full max-w-3xl flex-col gap-4 p-4">
        {/* ── Banner ───────────────────────────────────────────────────────────────────────────────── */}
        <div className="rounded-md border border-dashed border-amber-500/40 bg-amber-950/10 p-3 text-fs-base text-muted-foreground">
          <div className="mb-1 flex items-center gap-2 text-foreground">
            <Sparkles className="h-4 w-4 text-amber-400" />
            <span className="font-semibold">Dev fixture database</span>
          </div>
          <p>
            Generates a populated SWG-inspired Typhon database with deterministic data, then opens it. Pick a preset for
            the common shapes, or expand <span className="font-medium">Advanced</span> to tune per-archetype volumetry,
            schema-complexity toggles, the entity-mix distribution, and the RNG seed for targeted test scenarios.
          </p>
        </div>

        {/* ── Preset row ───────────────────────────────────────────────────────────────────────────── */}
        <div className="flex flex-col gap-1">
          <label className="text-fs-lg text-muted-foreground">Preset</label>
          <div className="flex flex-wrap gap-1.5">
            {(Object.keys(PRESET_LABELS) as PresetId[]).map((id) => (
              <button
                key={id}
                type="button"
                data-testid={`devfixture-preset-${id}`}
                data-active={form.presetId === id}
                onClick={() => dispatch({ type: 'select-preset', id })}
                className={
                  'rounded border px-2.5 py-1 text-fs-base transition-colors '
                  + (form.presetId === id
                    ? 'border-primary bg-primary/10 text-foreground'
                    : 'border-border text-muted-foreground hover:border-foreground/40 hover:text-foreground')
                }
              >
                {PRESET_LABELS[id]}
              </button>
            ))}
          </div>
        </div>

        {/* ── Advanced collapse ────────────────────────────────────────────────────────────────────── */}
        <div className="rounded border border-border">
          <button
            type="button"
            data-testid="devfixture-toggle-advanced"
            onClick={() => dispatch({ type: 'toggle-advanced' })}
            className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left text-fs-base font-medium uppercase tracking-wide text-muted-foreground hover:text-foreground"
          >
            {form.showAdvanced ? <ChevronDown className="h-3 w-3" /> : <ChevronRight className="h-3 w-3" />}
            <span>Advanced</span>
          </button>
          {form.showAdvanced && (
            <div className="border-t border-border px-3 py-2">
              <AdvancedForm config={form.config} dispatch={dispatch} />
            </div>
          )}
        </div>

        {/* ── Database name ────────────────────────────────────────────────────────────────────────── */}
        <div className="flex flex-col gap-0.5">
          <label htmlFor="devfixture-dbname" className="text-fs-base text-muted-foreground">Database name</label>
          <input
            id="devfixture-dbname"
            type="text"
            data-testid="devfixture-dbname"
            value={form.databaseName}
            onChange={(e) => dispatch({ type: 'set-database-name', value: e.target.value })}
            aria-invalid={form.databaseNameError !== null}
            aria-describedby={form.databaseNameError ? 'devfixture-dbname-err' : undefined}
            className={
              'w-full rounded border bg-background px-1.5 py-0.5 font-mono text-fs-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring '
              + (form.databaseNameError !== null ? 'border-destructive' : 'border-border')
            }
            spellCheck={false}
          />
          {form.databaseNameError !== null && (
            <p id="devfixture-dbname-err" data-testid="devfixture-dbname-err" className="text-fs-sm text-destructive">
              {form.databaseNameError}
            </p>
          )}
        </div>

        {/* ── Destination folder (editable override) ───────────────────────────────────────────────── */}
        <div className="flex flex-col gap-0.5">
          <label htmlFor="devfixture-outputdir" className="flex items-center gap-1.5 text-fs-base text-muted-foreground">
            <FolderOpen className="h-3 w-3" />
            <span>Destination folder</span>
            {outputDirOverride.length > 0 && (
              <button
                type="button"
                data-testid="devfixture-outputdir-reset"
                onClick={handleResetOutputDir}
                className="ml-1 text-fs-sm text-foreground/70 hover:text-foreground underline-offset-2 hover:underline"
                title="Reset to the server default"
              >
                reset to default
              </button>
            )}
          </label>
          <input
            id="devfixture-outputdir"
            type="text"
            data-testid="devfixture-outputdir"
            value={outputDirOverride}
            onChange={(e) => setOutputDirOverride(e.target.value)}
            placeholder={outputDirectoryRoot ? `(default) ${outputDirectoryRoot}/${dbName}` : 'Resolving default…'}
            className="w-full rounded border border-border bg-background px-1.5 py-0.5 font-mono text-fs-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            spellCheck={false}
          />
          <p className="text-fs-xs text-muted-foreground">
            Effective path: <code data-testid="devfixture-effective-path" className="rounded bg-muted px-1">{effectiveOutputDir || '(unset)'}</code>
          </p>
        </div>

        {/* ── Force checkbox ───────────────────────────────────────────────────────────────────────── */}
        <label className="flex items-center gap-2 text-fs-base text-foreground">
          <input
            type="checkbox"
            data-testid="devfixture-force"
            checked={force}
            onChange={(e) => setForce(e.target.checked)}
            className="h-3.5 w-3.5 accent-primary"
          />
          Force recreation
          <span className="text-fs-sm text-muted-foreground">(wipe + rebuild; otherwise reuse when the config matches)</span>
        </label>

        {/* ── BulkLoad toggle ──────────────────────────────────────────────────────────────────────────
            Opt-in throughput path on the engine. Skips per-row WAL; uses a manifest-paired write path that
            commits the whole bulk atomically at `CompleteBulkLoad`. Auto-checked on preset selection when the
            total exceeds BULK_LOAD_AUTO_THRESHOLD (5 M); user-overridable thereafter. Disabled when the
            generation is in flight so the user can't change their mind mid-run. */}
        <label className="flex items-center gap-2 text-fs-base">
          <input
            type="checkbox"
            data-testid="devfixture-use-bulkload"
            checked={form.useBulkLoad}
            disabled={isGenerating}
            onChange={(e) => dispatch({ type: 'set-use-bulk-load', value: e.target.checked })}
            className="h-3.5 w-3.5 accent-primary"
          />
          Use BulkLoad (throughput mode)
          <span className="text-fs-sm text-muted-foreground">
            (skips per-row WAL; recommended for &gt; {(BULK_LOAD_AUTO_THRESHOLD / 1_000_000).toLocaleString()} M entities)
          </span>
        </label>

        {/* ── Error banner ─────────────────────────────────────────────────────────────────────────── */}
        {error && (
          <div data-testid="devfixture-error" className="flex items-start gap-2 rounded border border-destructive/50 bg-destructive/10 px-2 py-1.5 text-fs-sm text-destructive">
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span className="font-mono">{error}</span>
          </div>
        )}

        {/* ── Scale advisory ──────────────────────────────────────────────────────────────────────────
            Tiered, honest about expectations. The thresholds and tier copy reflect the engine's two write
            paths (standard + BulkLoad) and the empirically-validated 512 MB cache cliff. Shown only when no
            job is currently running so it never overlaps with live progress.

            Tiers (totalEntities):
              ≥ 1 M           "moderate"          — standard path expected to take a few minutes
              ≥ 5 M (auto-on) "bulk-recommended"  — standard path will likely thrash the cache; BulkLoad solves this
              ≥ 15 M          "bulk-required"     — standard path effectively never finishes; BulkLoad is the only viable mode
        */}
        {!isGenerating && scaleAdvisory !== null && (
          <div
            data-testid="devfixture-scale-advisory"
            data-severity={scaleAdvisory}
            className={
              scaleAdvisory === 'bulk-required'
                ? 'flex items-start gap-2 rounded border border-destructive/50 bg-destructive/10 px-2 py-1.5 text-fs-sm text-destructive'
                : scaleAdvisory === 'bulk-recommended'
                  ? 'flex items-start gap-2 rounded border border-amber-500/40 bg-amber-950/10 px-2 py-1.5 text-fs-sm text-amber-300'
                  : 'flex items-start gap-2 rounded border border-border bg-muted/30 px-2 py-1.5 text-fs-sm text-muted-foreground'
            }
          >
            <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
            <span>
              <strong>{totalEntities.toLocaleString()} entities</strong>{' '}
              {scaleAdvisory === 'bulk-required' && (form.useBulkLoad
                ? '— very large generation. BulkLoad mode is on (the only viable path at this scale); expect ~minutes.'
                : '— very large generation. Without BulkLoad the standard path effectively never finishes (cache thrashing on every commit). Enable BulkLoad above.')}
              {scaleAdvisory === 'bulk-recommended' && (form.useBulkLoad
                ? '— large generation. BulkLoad mode is on; expect ~minutes (the manifest-paired path skips per-row WAL).'
                : '— large generation. Standard path will likely take hours and stall progress; BulkLoad (toggle above) collapses this to minutes.')}
              {scaleAdvisory === 'moderate' && '— moderate generation. Standard path expected to take a few minutes.'}
            </span>
          </div>
        )}

        {/* ── Progress + actions ───────────────────────────────────────────────────────────────────────────
            Diagnostic strip: absolute counts (`12,345 / 3,200,000`) + sub-percent precision + elapsed wall-clock
            + instantaneous rate. The stall-detector pings when `completed` hasn't moved in ≥ 10 s — distinguishes
            a saturated-cache slow path from a genuinely wedged job. Without this, sub-percent progress collapses
            to `0%` for large generations and the user can't tell slow from stuck. */}
        {isGenerating && jobState && progressDisplay && (
          <div data-testid="devfixture-progress" className="flex flex-col gap-1">
            <div className="flex items-center justify-between gap-3 text-fs-base">
              <span className="flex items-center gap-1.5 text-foreground">
                <span>{jobState.phase || 'Starting…'}</span>
                {progressDisplay.isStalled && (
                  <span
                    data-testid="devfixture-progress-stall"
                    title="No forward progress for ≥10 s — the engine may be cache-thrashing on a working set larger than the page cache."
                    className="flex items-center gap-1 rounded bg-destructive/15 px-1.5 py-0.5 text-fs-xs text-destructive"
                  >
                    <Hourglass className="h-3 w-3" />
                    stalled
                  </span>
                )}
              </span>
              <span className="flex items-baseline gap-2 font-mono text-fs-sm text-muted-foreground">
                <span data-testid="devfixture-progress-counts">{progressDisplay.countLabel}</span>
                {progressDisplay.percentLabel !== null && (
                  <span data-testid="devfixture-progress-pct" className="text-foreground">{progressDisplay.percentLabel}</span>
                )}
              </span>
            </div>
            <div className="h-1.5 w-full overflow-hidden rounded bg-muted">
              <div
                className="h-full bg-primary transition-[width] duration-200"
                style={{ width: `${progressDisplay.barWidthPct}%` }}
              />
            </div>
            <div className="flex items-center justify-end gap-2 text-fs-xs text-muted-foreground">
              <span data-testid="devfixture-progress-elapsed">Elapsed {progressDisplay.elapsedLabel}</span>
              {progressDisplay.rateLabel !== null && (
                <>
                  <span>•</span>
                  <span data-testid="devfixture-progress-rate">{progressDisplay.rateLabel}</span>
                </>
              )}
            </div>
          </div>
        )}

        <div className="mt-2 flex justify-end gap-2">
          {isGenerating ? (
            <Button variant="secondary" onClick={() => void handleCancel()} data-testid="devfixture-cancel">
              Cancel
            </Button>
          ) : (
            <Button onClick={() => void handleStart()} disabled={!canClick} data-testid="devfixture-start">
              {force ? 'Recreate & Open' : 'Generate & Open'}
            </Button>
          )}
        </div>

        <p className="text-fs-xs text-muted-foreground">
          Custom config persisted under <code className="rounded bg-muted px-1">{CUSTOM_CONFIG_STORAGE_KEY}</code>; the
          default database name is <code className="rounded bg-muted px-1">{DEFAULT_DATABASE_NAME}</code>.
        </p>
      </div>
    </div>
  );
}

function CenteredMessage({ children }: { children: React.ReactNode }): React.JSX.Element {
  return (
    <div className="flex h-full w-full items-center justify-center bg-background p-4 text-center">
      <div className="max-w-md text-fs-base text-muted-foreground">{children}</div>
    </div>
  );
}

// Advanced-form field groups (SWG schema). Labels are display-only; the second tuple element is the C#-mirrored
// FixtureConfig key that the dispatched action targets.
const VOLUMETRY_ROWS: Array<[string, FixtureIntKey]> = [
  ['Resource types', 'resourceTypeCount'],
  ['Guilds', 'guildCount'],
  ['Recipes', 'recipeCount'],
  ['Players', 'playerCount'],
  ['Deposits', 'depositCount'],
  ['Harvesters', 'harvesterCount'],
  ['Factories', 'factoryCount'],
  ['Items', 'itemCount'],
];

const COMPLEXITY_INT_ROWS: Array<[string, FixtureIntKey]> = [
  ['Taxonomy depth', 'resourceTaxonomyDepth'],
  ['Max affixes / item', 'maxAffixesPerItem'],
  ['Professions', 'professionCount'],
];

const COMPLEXITY_BOOL_ROWS: Array<[string, FixtureBoolKey]> = [
  ['Multi-affix items', 'includeMultiAffixItems'],
  ['Factories (polymorphic)', 'includePolymorphicStructure'],
];

const DISTRIBUTION_FRACTION_ROWS: Array<[string, FixtureFractionKey]> = [
  ['Online players', 'onlinePlayerFraction'],
  ['Broken harvesters', 'brokenHarvesterFraction'],
  ['Depleted deposits', 'depletedDepositFraction'],
  ['Idle factories', 'idleFactoryFraction'],
  ['Deleted players', 'deletedPlayerFraction'],
];

/** A titled group within the Advanced form. */
function Section({ title, children }: { title: string; children: React.ReactNode }): React.JSX.Element {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-fs-sm font-medium uppercase tracking-wide text-muted-foreground">{title}</span>
      {children}
    </div>
  );
}

/** A labelled non-negative integer input (volumetry + integer complexity knobs). */
function NumberRow({
  label,
  testid,
  value,
  onChange,
}: {
  label: string;
  testid: string;
  value: number;
  onChange: (value: number) => void;
}): React.JSX.Element {
  return (
    <label className="flex items-center justify-between gap-2 text-fs-base text-foreground">
      <span className="text-muted-foreground">{label}</span>
      <input
        type="number"
        min={0}
        step={1}
        data-testid={testid}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        className="w-28 rounded border border-border bg-background px-1.5 py-0.5 text-right font-mono text-fs-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
    </label>
  );
}

/** A labelled [0,1] fraction slider rendered as an integer-percent range. */
function FractionRow({
  label,
  testid,
  value,
  onChange,
}: {
  label: string;
  testid: string;
  value: number;
  onChange: (value: number) => void;
}): React.JSX.Element {
  const pct = Math.round(value * 100);
  return (
    <label className="flex items-center justify-between gap-2 text-fs-base text-foreground">
      <span className="text-muted-foreground">{label}</span>
      <div className="flex items-center gap-2">
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          data-testid={testid}
          value={pct}
          onChange={(e) => onChange(Number(e.target.value) / 100)}
          className="w-28"
        />
        <span className="w-10 text-right font-mono text-fs-sm">{pct}%</span>
      </div>
    </label>
  );
}

/**
 * Grouped Advanced form (SWG schema), shown when the Advanced collapse is open. Three sections mirror the C#
 * `FixtureConfig` axes: <b>Volumetry</b> (per-archetype counts), <b>Complexity</b> (data-level shape: integer knobs +
 * the two boolean toggles), and <b>Distribution</b> (entity-mix fractions + the players-per-guild shape + RNG seed).
 * Every control dispatches a typed action whose `key` is a compile-time-checked `FixtureConfig` field.
 */
function AdvancedForm({
  config,
  dispatch,
}: {
  config: FixtureConfig;
  dispatch: React.Dispatch<DevFixtureFormAction>;
}): React.JSX.Element {
  return (
    <div className="flex flex-col gap-3">
      <Section title="Volumetry">
        <div className="grid grid-cols-2 gap-x-3 gap-y-1">
          {VOLUMETRY_ROWS.map(([label, key]) => (
            <NumberRow
              key={key}
              label={label}
              testid={`devfixture-count-${key}`}
              value={config[key]}
              onChange={(value) => dispatch({ type: 'set-count', key, value })}
            />
          ))}
        </div>
      </Section>

      <Section title="Complexity">
        <div className="grid grid-cols-2 gap-x-3 gap-y-1">
          {COMPLEXITY_INT_ROWS.map(([label, key]) => (
            <NumberRow
              key={key}
              label={label}
              testid={`devfixture-count-${key}`}
              value={config[key]}
              onChange={(value) => dispatch({ type: 'set-count', key, value })}
            />
          ))}
        </div>
        <div className="mt-1 flex flex-col gap-1">
          {COMPLEXITY_BOOL_ROWS.map(([label, key]) => (
            <label key={key} className="flex items-center gap-2 text-fs-base text-foreground">
              <input
                type="checkbox"
                data-testid={`devfixture-bool-${key}`}
                checked={config[key]}
                onChange={(e) => dispatch({ type: 'set-bool', key, value: e.target.checked })}
                className="h-3.5 w-3.5 accent-primary"
              />
              <span className="text-muted-foreground">{label}</span>
            </label>
          ))}
        </div>
      </Section>

      <Section title="Distribution">
        <div className="flex flex-col gap-1">
          {DISTRIBUTION_FRACTION_ROWS.map(([label, key]) => (
            <FractionRow
              key={key}
              label={label}
              testid={`devfixture-fraction-${key}`}
              value={config[key]}
              onChange={(value) => dispatch({ type: 'set-fraction', key, value })}
            />
          ))}
        </div>

        <label className="flex items-center justify-between gap-2 text-fs-base text-foreground">
          <span className="text-muted-foreground">Players per guild</span>
          <select
            data-testid="devfixture-shape"
            value={config.playersPerGuildShape}
            onChange={(e) => dispatch({ type: 'set-shape', value: Number(e.target.value) })}
            className="w-32 rounded border border-border bg-background px-1.5 py-0.5 text-fs-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {Object.values(PlayersPerGuildShape).map((shape) => (
              <option key={shape} value={shape}>
                {PLAYERS_PER_GUILD_SHAPE_LABELS[shape]}
              </option>
            ))}
          </select>
        </label>

        <label className="flex items-center justify-between gap-2 text-fs-base text-foreground">
          <span className="text-muted-foreground">RNG seed</span>
          <div className="flex items-center gap-1">
            <input
              type="number"
              data-testid="devfixture-seed"
              value={config.seed}
              onChange={(e) => dispatch({ type: 'set-seed', value: Number(e.target.value) })}
              className="w-32 rounded border border-border bg-background px-1.5 py-0.5 text-right font-mono text-fs-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
            <button
              type="button"
              data-testid="devfixture-seed-randomize"
              title="Randomize seed"
              aria-label="Randomize seed"
              onClick={() => dispatch({ type: 'randomize-seed' })}
              className="flex h-6 w-6 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-accent-foreground"
            >
              <Dice5 className="h-3 w-3" />
            </button>
          </div>
        </label>
      </Section>
    </div>
  );
}

// Re-export for parity with the old tab's external surface (some external integrations referenced PRESET_CONFIGS).
export { PRESET_CONFIGS };
