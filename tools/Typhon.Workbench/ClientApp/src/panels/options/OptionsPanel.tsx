import { useEffect, useState } from 'react';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { DagForm } from './DagForm';
import { EditorForm } from './EditorForm';
import { ProfilerForm } from './ProfilerForm';

/**
 * Workbench Options panel (issue #293, Phase 4a). Sidebar with category list, main area renders
 * the active form. Adding a new category is two lines here + a new `<XxxForm.tsx>`.
 *
 * Design ref: claude/design/observability/10-profiler-source-attribution.md §5.7.6.
 */
type CategoryKey = 'editor' | 'profiler' | 'dag';

interface CategoryDef {
  key: CategoryKey;
  label: string;
}

const CATEGORIES: CategoryDef[] = [
  { key: 'editor', label: 'Editor' },
  { key: 'profiler', label: 'Profiler' },
  { key: 'dag', label: 'DAG' },
];

export default function OptionsPanel(): React.JSX.Element {
  const fetchOptions = useOptionsStore((s) => s.fetch);
  const loaded = useOptionsStore((s) => s.loaded);
  const [active, setActive] = useState<CategoryKey>('editor');

  useEffect(() => {
    if (!loaded) {
      void fetchOptions();
    }
  }, [loaded, fetchOptions]);

  return (
    <div className="flex h-full w-full overflow-hidden bg-background">
      {/* Sidebar with category list. */}
      <nav className="flex w-48 flex-col border-r border-border bg-card">
        <header className="border-b border-border px-3 py-2 text-fs-base font-semibold text-muted-foreground">
          Options
        </header>
        <ul className="flex flex-col py-1">
          {CATEGORIES.map((c) => (
            <li key={c.key}>
              <button
                type="button"
                onClick={() => setActive(c.key)}
                className={`w-full px-3 py-1.5 text-left text-fs-base hover:bg-accent ${
                  active === c.key ? 'bg-accent font-medium text-foreground' : 'text-muted-foreground'
                }`}
              >
                {c.label}
              </button>
            </li>
          ))}
        </ul>
      </nav>

      {/* Active form. Editor / Profiler are server-backed and deferred until options are loaded so
          local pending state initialises from the real server values, not DEFAULT_OPTIONS. The DAG
          category is a client-only preference store — it needs no server fetch, so it renders
          immediately regardless of `loaded`. */}
      <main className="flex-1 overflow-auto p-4">
        {active === 'dag' ? (
          <DagForm />
        ) : !loaded ? (
          <p className="text-fs-base text-muted-foreground">Loading…</p>
        ) : (
          <>
            {active === 'editor' && <EditorForm />}
            {active === 'profiler' && <ProfilerForm />}
          </>
        )}
      </main>
    </div>
  );
}
