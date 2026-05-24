import { ExternalLink, GitBranch } from 'lucide-react';
import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { Button } from '@/components/ui/button';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { openViewQueryPlanTree } from '@/shell/commands/profilerCommands';
import { useQueryPlanStore } from '@/panels/QueryPlanTree/useQueryPlanStore';
import { toNumber } from '../QueryCatalog/numeric';

interface Props {
  definition: QueryDefinitionDto | null;
  execution: QueryExecutionDto | null;
  archetypeName: string;
  systemName: string;
}

/**
 * Top bar for the Execution Inspector — identity (Definition + Tick + System), "Defined at" source
 * link, and "Show tree" hand-off to the Plan Tree panel (P6) in execution mode.
 *
 * <para>"Triggered at" link is deferred until the DTO surface carries execution-site source info
 * (see design doc §5.2). For now we surface the system name as the trigger context.</para>
 */
export function ExecutionInspectorToolbar({ definition, execution, archetypeName, systemName }: Props) {
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const setQueryPlanFocus = useQueryPlanStore((s) => s.setFocus);
  const setQueryPlanExecution = useQueryPlanStore((s) => s.setSelectedExecution);

  if (!definition) {
    return <div className="border-b border-border bg-card px-3 py-1.5 text-fs-base text-muted-foreground">No execution selected.</div>;
  }

  const kindLabel = definition.instanceId.kind === 0 ? 'View' : 'EcsQuery';
  const idLabel = `${kindLabel}#${definition.instanceId.localId}`;
  const src = definition.userSource;
  const sourceLine = toNumber(src.line);
  const canOpen = src.file != null && src.file.length > 0 && sourceLine > 0;

  function onShowTree() {
    if (!execution) return;
    setQueryPlanFocus({
      kind: toNumber(definition!.instanceId.kind),
      localId: toNumber(definition!.instanceId.localId),
    });
    setQueryPlanExecution(execution);
    openViewQueryPlanTree();
  }

  return (
    <div className="flex items-center gap-3 border-b border-border bg-card px-3 py-1.5">
      <div className="flex items-baseline gap-2">
        <span className="font-mono text-fs-base font-semibold text-foreground">{idLabel}</span>
        <span className="text-fs-sm text-muted-foreground">on</span>
        <span className="font-mono text-fs-base text-foreground">{archetypeName || '—'}</span>
      </div>

      {execution && (
        <div className="flex items-baseline gap-2 border-l border-border pl-3">
          <span className="text-fs-sm text-muted-foreground">tick</span>
          <span className="font-mono text-fs-base text-foreground">{toNumber(execution.tickIndex).toLocaleString()}</span>
          <span className="text-fs-sm text-muted-foreground">·</span>
          <span className="text-fs-sm text-muted-foreground">in</span>
          <span className="font-mono text-fs-base text-foreground">{systemName || '<unattributed>'}</span>
        </div>
      )}

      <div className="flex-1" />

      {execution && (
        <Button
          size="sm"
          variant="ghost"
          className="h-7 gap-1 text-fs-sm"
          onClick={onShowTree}
          title="Open the plan tree for this execution"
          data-testid="execution-inspector-show-tree"
        >
          <GitBranch className="h-3 w-3" />
          Show tree
        </Button>
      )}

      {canOpen && (
        <Button
          size="sm"
          variant="ghost"
          className="h-7 gap-1 text-fs-sm"
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
