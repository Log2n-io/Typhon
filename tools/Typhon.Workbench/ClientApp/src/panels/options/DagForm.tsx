import { useViewOptionsStore } from '@/stores/useViewOptionsStore';

/**
 * DAG / Critical Path view options. Unlike the Editor and Profiler categories — which are
 * server-backed `WorkbenchOptions` patched over HTTP — these are pure client UI preferences
 * persisted to localStorage via {@link useViewOptionsStore}. The toggle applies immediately (no
 * Save button); it is shared by the System DAG panel (hides engine-tagged tracks) and the
 * Critical Path panel (drops engine tracks from the track selector and the "All" scope).
 */
export function DagForm(): React.JSX.Element {
  const showEngineSystems = useViewOptionsStore((s) => s.showEngineSystems);
  const setShowEngineSystems = useViewOptionsStore((s) => s.setShowEngineSystems);

  return (
    <section className="max-w-xl space-y-4">
      <header>
        <h2 className="text-[14px] font-semibold text-foreground">DAG &amp; Critical Path</h2>
        <p className="mt-1 text-[12px] text-muted-foreground">
          Shared display options for the System DAG and Critical Path panels.
        </p>
      </header>

      <label className="flex cursor-pointer items-start gap-2">
        <input
          type="checkbox"
          checked={showEngineSystems}
          onChange={(e) => setShowEngineSystems(e.target.checked)}
          className="mt-0.5 h-3.5 w-3.5 cursor-pointer accent-primary"
        />
        <span className="block">
          <span className="block text-[12px] font-medium text-foreground">
            Show engine-related systems &amp; DAGs
          </span>
          <span className="mt-0.5 block text-[11px] text-muted-foreground">
            Reveal the engine-internal tracks (Engine-Pre, Engine-Post / Fence) and their systems.
            Off by default — the engine's own DAGs are infrastructure noise for app-level work. When
            on, the System DAG renders them as their own groups and the Critical Path lists them in
            its track selector. Keyed off the track's <code>engine</code> tag, never its name.
          </span>
        </span>
      </label>
    </section>
  );
}
