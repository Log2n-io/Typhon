import type { QueryDefinitionDto } from '@/api/generated/model';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';

/** Build a {@link QueryDefinitionDto} for Query Analyzer tests (fields the view actually reads). */
export function makeDef(opts: {
  kind?: number;
  localId: number;
  target: number;
  totalWallNs: number;
  owners?: number[];
  evaluators?: { fieldIdx: number; op: number; fieldName: string; opDisplay: string }[];
  primaryIndexFieldIdx?: number;
  sortFieldIdx?: number;
  sortDescending?: boolean;
  executionCount?: number;
  p50?: number;
  p95?: number;
  p99?: number;
  selectivity?: number;
  method?: string;
  file?: string;
  line?: number;
}): QueryDefinitionDto {
  const {
    kind = 0,
    localId,
    target,
    totalWallNs,
    owners = [0],
    evaluators = [{ fieldIdx: 0, op: 1, fieldName: 'X', opDisplay: '>=' }],
    primaryIndexFieldIdx = 0,
    sortFieldIdx = -1,
    sortDescending = false,
    executionCount = 4,
    p50 = Math.round(totalWallNs / 8),
    p95 = Math.round(totalWallNs / 4),
    p99 = Math.round(totalWallNs / 2),
    selectivity = 0.5,
    method = `Method${localId}`,
    file = 'src/Game/Queries.cs',
    line = 10 + localId,
  } = opts;
  return {
    instanceId: { kind, localId },
    targetComponentType: target,
    primaryIndexFieldIdx,
    sortFieldIdx,
    sortDescending,
    evaluators,
    fieldDependencies: [],
    ownerSystemIds: owners,
    aggregate: {
      executionCount,
      totalWallNs,
      avgWallNs: Math.round(totalWallNs / Math.max(1, executionCount)),
      p50WallNs: p50,
      p95WallNs: p95,
      p99WallNs: p99,
      totalRowsScanned: 1000,
      totalRowsReturned: 500,
      avgSelectivity: selectivity,
    },
    userSource: { file, line, method },
  } as QueryDefinitionDto;
}

/** Build a {@link QueryExecutionDto} with a default Parse/Iterate/Count phase breakdown. */
export function makeExecution(opts: {
  kind?: number;
  localId?: number;
  tickIndex: number;
  systemId?: number;
  startTs: number;
  endTs: number;
  phases?: { phaseName: string; estimate: number | null; actual: number | null; wallNs: number; notes?: string }[];
}): QueryExecutionDto {
  const {
    kind = 0,
    localId = 1,
    tickIndex,
    systemId = 0,
    startTs,
    endTs,
    phases = [
      { phaseName: 'Parse', estimate: null, actual: null, wallNs: 100, notes: '' },
      { phaseName: 'Iterate', estimate: 1000, actual: 900, wallNs: 500, notes: '' },
      { phaseName: 'Count', estimate: null, actual: 500, wallNs: 50, notes: '' },
    ],
  } = opts;
  return {
    definitionId: { kind, localId },
    spanId: tickIndex * 1000 + systemId,
    parentSpanId: 0,
    tickIndex,
    systemId,
    startTs,
    endTs,
    args: [],
    phases,
  } as QueryExecutionDto;
}
