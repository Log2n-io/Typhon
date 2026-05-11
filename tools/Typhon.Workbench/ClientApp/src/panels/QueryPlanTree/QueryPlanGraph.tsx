import { useMemo } from 'react';
import { ReactFlow, Background, Controls, MiniMap } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import { buildQueryPlanGraph, type ArchetypeLookup } from './queryPlanLayout';
import {
  FilterNode,
  IndexScanNode,
  PaginationNode,
  ResultNode,
  SortNode,
} from './QueryPlanNodes';

const NODE_TYPES = {
  IndexScan: IndexScanNode,
  Filter: FilterNode,
  Sort: SortNode,
  Pagination: PaginationNode,
  Result: ResultNode,
};

export interface QueryPlanGraphProps {
  definition: QueryDefinitionDto;
  execution: QueryExecutionDto | null;
  archetypeName?: ArchetypeLookup;
}

export default function QueryPlanGraph({ definition, execution, archetypeName }: QueryPlanGraphProps) {
  const { nodes, edges } = useMemo(
    () => buildQueryPlanGraph(definition, execution, archetypeName),
    [definition, execution, archetypeName],
  );
  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={NODE_TYPES as never}
      fitView
      proOptions={{ hideAttribution: true }}
      minZoom={0.3}
      maxZoom={1.6}
      nodesDraggable={false}
      nodesConnectable={false}
      elementsSelectable
    >
      <Background color="var(--border)" gap={16} />
      <Controls showInteractive={false} position="bottom-left" />
      <MiniMap pannable zoomable position="bottom-right" />
    </ReactFlow>
  );
}
