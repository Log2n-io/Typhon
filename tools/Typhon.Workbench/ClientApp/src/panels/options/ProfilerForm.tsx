import { useEffect, useState } from 'react';
import { useOptionsStore } from '@/stores/useOptionsStore';

interface WorkspaceRootDto {
  effective: string;
  source: 'configured' | 'auto-detected' | 'cwd-fallback';
}

/**
 * Profiler-related options (issue #293 Phase 4a; #345 Step 9 added the debounce control). The
 * workspace root resolves the "/_/..." paths from trace manifests to absolute paths on disk. When
 * the user leaves it empty, the server auto-detects the repo root by walking up from CWD looking
 * for a `.git` entry. The effective root is fetched from `/api/profiler/workspace-root` and shown
 * so the user can verify what's being used.
 *
 * The view-range debounce (ms) controls how long after the user stops panning/zooming in TimeArea
 * cross-panel consumers (SystemDag, CriticalPath, DataFlow, AccessMatrix) re-aggregate. Default
 * 150 ms; range [0, 5000]. `0` disables debouncing entirely (commit-on-write).
 */
export function ProfilerForm(): React.JSX.Element {
  const profiler = useOptionsStore((s) => s.options.profiler);
  const setProfiler = useOptionsStore((s) => s.setProfiler);
  const [pendingRoot, setPendingRoot] = useState<string>(profiler.workspaceRoot);
  const [pendingDebounceMs, setPendingDebounceMs] = useState<number>(profiler.viewRangeDebounceMs);
  const dirty =
    pendingRoot !== profiler.workspaceRoot ||
    pendingDebounceMs !== profiler.viewRangeDebounceMs;
  const [error, setError] = useState<string | null>(null);
  const [effective, setEffective] = useState<WorkspaceRootDto | null>(null);

  // Reset the local edits whenever the server pushes new options (SSE) so we don't accidentally
  // overwrite another window's changes.
  useEffect(() => {
    setPendingRoot(profiler.workspaceRoot);
    setPendingDebounceMs(profiler.viewRangeDebounceMs);
  }, [profiler.workspaceRoot, profiler.viewRangeDebounceMs]);

  useEffect(() => {
    let cancelled = false;
    fetch('/api/profiler/workspace-root')
      .then((r) => (r.ok ? r.json() : null))
      .then((dto: WorkspaceRootDto | null) => {
        if (!cancelled && dto) setEffective(dto);
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [profiler.workspaceRoot]);

  async function handleSave(): Promise<void> {
    setError(null);
    try {
      // Clamp on the client to match the server's [Range(0, 5000)] guard — gives the user a
      // predictable bound rather than letting the server 400 on out-of-range input.
      const clampedMs = Math.max(0, Math.min(5000, Math.round(pendingDebounceMs)));
      await setProfiler({ workspaceRoot: pendingRoot, viewRangeDebounceMs: clampedMs });
    } catch (err) {
      setError((err as Error).message);
    }
  }

  return (
    <section className="max-w-xl space-y-4">
      <header>
        <h2 className="text-[14px] font-semibold text-foreground">Profiler</h2>
        <p className="mt-1 text-[12px] text-muted-foreground">
          Used to resolve repo-relative source paths from trace files, and to tune cross-panel
          update latency during TimeArea pan/zoom.
        </p>
      </header>

      <label className="block">
        <span className="block text-[12px] font-medium text-foreground">Workspace root</span>
        <input
          type="text"
          value={pendingRoot}
          onChange={(e) => setPendingRoot(e.target.value)}
          placeholder="C:\Dev\github\Typhon"
          className="mt-1 w-full rounded border border-border bg-background px-2 py-1 font-mono text-[12px]"
        />
        <span className="mt-1 block text-[11px] text-muted-foreground">
          Absolute path of the repo this trace was recorded against. Leave empty to auto-detect via the
          nearest <code>.git</code> directory.
        </span>
      </label>

      {effective && (
        <div className="rounded border border-border bg-muted/30 px-2 py-1 text-[11px]">
          <span className="text-muted-foreground">Effective root ({effective.source}): </span>
          <span className="font-mono text-foreground">{effective.effective}</span>
        </div>
      )}

      <label className="block">
        <span className="block text-[12px] font-medium text-foreground">View range debounce (ms)</span>
        <input
          type="number"
          min={0}
          max={5000}
          step={10}
          value={pendingDebounceMs}
          onChange={(e) => setPendingDebounceMs(Number(e.target.value))}
          className="mt-1 w-32 rounded border border-border bg-background px-2 py-1 font-mono text-[12px] tabular-nums"
        />
        <span className="mt-1 block text-[11px] text-muted-foreground">
          How long the profiler waits after you stop panning/zooming the TimeArea before the System
          DAG, Critical Path, Data Flow, and Access Matrix panels re-aggregate. Default <code>150</code> ms.
          Set to <code>0</code> for synchronous updates (degraded pan fluidity); higher values give
          smoother scrubbing at the cost of slower consumer reaction. Range <code>[0, 5000]</code>.
        </span>
      </label>

      <div className="flex items-center gap-2">
        <button
          type="button"
          onClick={handleSave}
          disabled={!dirty}
          className="rounded border border-border bg-primary px-3 py-1 text-[12px] text-primary-foreground hover:opacity-90 disabled:opacity-50"
        >
          Save
        </button>
        {error && <span className="text-[12px] text-destructive">{error}</span>}
      </div>
    </section>
  );
}
