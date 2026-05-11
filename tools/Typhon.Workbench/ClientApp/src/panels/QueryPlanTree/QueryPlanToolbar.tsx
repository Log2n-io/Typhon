import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import { Button } from '@/components/ui/button';
import { ExternalLink } from 'lucide-react';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { toNumber } from '../QueryCatalog/numeric';
import { useQueryPlanStore, type QueryPlanMode } from './useQueryPlanStore';

interface QueryPlanToolbarProps {
  definition: QueryDefinitionDto;
  archetypeName: string;
}

/**
 * Header bar for the Plan Tree panel: query identity (Kind#LocalId), archetype name, mode toggle,
 * and a "Defined at" link that calls into the existing editor-launcher (issue #10).
 */
export default function QueryPlanToolbar({ definition, archetypeName }: QueryPlanToolbarProps) {
  const mode = useQueryPlanStore((s) => s.mode);
  const hasExecution = useQueryPlanStore((s) => s.selectedExecution !== null);
  const setMode = useQueryPlanStore((s) => s.setMode);
  const openInEditor = useOptionsStore((s) => s.openInEditor);

  const kindLabel = definition.instanceId.kind === 0 ? 'View' : 'EcsQuery';
  const idLabel = `${kindLabel}#${definition.instanceId.localId}`;
  const src = definition.userSource;
  const sourceLine = toNumber(src.line);
  const canOpen = src.file != null && src.file.length > 0 && sourceLine > 0;

  return (
    <div className="flex items-center gap-3 border-b border-border bg-card px-3 py-1.5">
      <div className="flex items-baseline gap-2">
        <span className="font-mono text-[12px] font-semibold text-foreground">{idLabel}</span>
        <span className="text-[11px] text-muted-foreground">on</span>
        <span className="font-mono text-[12px] text-foreground">{archetypeName}</span>
      </div>

      <div className="flex-1" />

      <ModeToggle
        mode={mode}
        hasExecution={hasExecution}
        onChange={setMode}
      />

      {canOpen && (
        <Button
          size="sm"
          variant="ghost"
          className="h-7 gap-1 text-[11px]"
          onClick={() => openInEditor(src.file ?? '', sourceLine)}
          title={`${src.file}:${sourceLine}`}
        >
          <ExternalLink className="h-3 w-3" />
          Defined at
        </Button>
      )}
    </div>
  );
}

function ModeToggle({
  mode,
  hasExecution,
  onChange,
}: {
  mode: QueryPlanMode;
  hasExecution: boolean;
  onChange: (m: QueryPlanMode) => void;
}) {
  return (
    <div className="flex rounded-md border border-border">
      <button
        type="button"
        className={`px-2 py-0.5 text-[11px] ${mode === 'structural' ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
        onClick={() => onChange('structural')}
      >
        Structural
      </button>
      <button
        type="button"
        className={`px-2 py-0.5 text-[11px] ${mode === 'execution' ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'} disabled:opacity-40`}
        onClick={() => onChange('execution')}
        disabled={!hasExecution}
        title={hasExecution ? '' : 'Open from Execution Inspector to view per-run stats'}
      >
        Execution
      </button>
    </div>
  );
}
