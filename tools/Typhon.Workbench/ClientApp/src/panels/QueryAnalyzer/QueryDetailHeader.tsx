import { ExternalLink } from 'lucide-react';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { toNumber } from './numeric';
import { openComponentInSchema, revealArchetypeInInspector, revealSystemInDag } from '@/shell/commands/openDbMap';
import { formatNs, formatSelectivity, formatThousands, predicateSummary, queryKindLabel } from './format';

/**
 * Detail header for the focused query (design §4.2): identity (`Kind#LocalId on target`), the
 * predicate shape, owning systems, the pre-resolved source call site (clickable → editor), and the
 * aggregate stat strip. All fields come pre-resolved from {@link QueryDefinitionDto} — no extra fetch.
 *
 * Cross-pillar hand-offs (#376 4D-1, AC3.14): the **target** opens the Component Inspector (component
 * target) or Archetype Inspector (archetype target); each **owner system** reveals in the System DAG;
 * the **source** opens the editor. All reuse the shared reveal commands and write the bus leaf.
 */
interface Props {
  definition: QueryDefinitionDto;
  archetypeName: string;
  ownerNames: string[];
  /** Raw `targetComponentType` id — used to route the archetype-target hand-off. */
  targetId: number;
  /** True when the target is a ComponentType (→ Component Inspector); false = Archetype (→ Archetype Inspector). */
  targetIsComponent: boolean;
}

export function QueryDetailHeader({ definition, archetypeName, ownerNames, targetId, targetIsComponent }: Props) {
  const openInEditor = useOptionsStore((s) => s.openInEditor);
  const agg = definition.aggregate;
  const src = definition.userSource;
  const kind = toNumber(definition.instanceId.kind);
  const localId = toNumber(definition.instanceId.localId);
  const sourceFile = src.file ?? '';
  const sourceLine = toNumber(src.line);
  const hasSource = sourceFile.length > 0 && sourceLine > 0;

  return (
    <div className="border-b border-border bg-card px-3 py-2" data-testid="query-detail-header">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span className="font-mono text-fs-lg font-semibold text-foreground">
          {`${queryKindLabel(kind)} #${localId}`}
        </span>
        <span className="text-fs-sm text-muted-foreground">on</span>
        {archetypeName ? (
          <button
            type="button"
            onClick={() => (targetIsComponent ? openComponentInSchema(archetypeName) : revealArchetypeInInspector(String(targetId)))}
            className="font-mono text-fs-base text-foreground hover:underline"
            title={`Open ${archetypeName} in ${targetIsComponent ? 'Component' : 'Archetype'} Inspector`}
            data-testid="query-detail-target"
          >
            {archetypeName}
          </button>
        ) : (
          <span className="font-mono text-fs-base text-muted-foreground">—</span>
        )}

        <span className="ml-2 text-fs-sm text-muted-foreground">pred</span>
        <span className="font-mono text-fs-sm text-foreground" data-testid="query-detail-predicate">{predicateSummary(definition)}</span>

        <span className="ml-2 text-fs-sm text-muted-foreground">sys</span>
        <span className="text-fs-sm text-foreground" data-testid="query-detail-systems">
          {ownerNames.length === 0 ? (
            <span className="text-muted-foreground">—</span>
          ) : (
            ownerNames.map((n, i) => (
              <span key={n}>
                {i > 0 && <span className="text-muted-foreground">, </span>}
                <button
                  type="button"
                  onClick={() => revealSystemInDag(n)}
                  className="text-foreground hover:underline"
                  title={`Reveal ${n} in System DAG`}
                  data-testid="query-detail-owner"
                >
                  {n}
                </button>
              </span>
            ))
          )}
        </span>

        <span className="ml-2 text-fs-sm text-muted-foreground">src</span>
        {hasSource ? (
          <button
            type="button"
            onClick={() => void openInEditor(sourceFile, sourceLine)}
            className="inline-flex items-center gap-1 text-fs-sm text-foreground hover:underline"
            title={`${sourceFile}:${sourceLine}`}
            data-testid="query-detail-open-in-editor"
          >
            <ExternalLink className="h-3 w-3" />
            <span className="font-mono">{`${src.method || '<unattributed>'} (${shortFile(sourceFile)}:${sourceLine})`}</span>
          </button>
        ) : (
          <span className="text-fs-sm text-muted-foreground">—</span>
        )}
      </div>

      <div className="mt-1.5 flex flex-wrap gap-x-4 gap-y-0.5 text-fs-sm">
        <Stat label="count" value={formatThousands(toNumber(agg.executionCount))} />
        <Stat label="total" value={formatNs(toNumber(agg.totalWallNs))} emphasis />
        <Stat label="p50/p95/p99" value={`${formatNs(toNumber(agg.p50WallNs))} / ${formatNs(toNumber(agg.p95WallNs))} / ${formatNs(toNumber(agg.p99WallNs))}`} />
        <Stat label="selectivity" value={formatSelectivity(toNumber(agg.avgSelectivity))} />
        <Stat label="rows scan→ret" value={`${formatThousands(toNumber(agg.totalRowsScanned))} → ${formatThousands(toNumber(agg.totalRowsReturned))}`} />
      </div>
    </div>
  );
}

function Stat({ label, value, emphasis = false }: { label: string; value: string; emphasis?: boolean }) {
  return (
    <span className="inline-flex items-baseline gap-1">
      <span className="text-muted-foreground">{label}</span>
      <span className={`font-mono tabular-nums ${emphasis ? 'font-semibold text-foreground' : 'text-foreground'}`}>{value}</span>
    </span>
  );
}

/** Trailing path segment so the header stays compact (`src/Game/Queries.cs` → `Queries.cs`). */
function shortFile(file: string): string {
  const i = Math.max(file.lastIndexOf('/'), file.lastIndexOf('\\'));
  return i >= 0 ? file.slice(i + 1) : file;
}
