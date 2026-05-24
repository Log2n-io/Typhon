import { useMemo } from 'react';
import { ReactFlow, Background, Controls, MiniMap } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  buildSchemaRelationshipsGraph,
  type SchemaNode,
} from './schemaRelationshipsLayout';
import type { SystemRelationship } from '@/hooks/schema/types';

export interface SchemaRelationshipsGraphProps {
  componentTypeName: string;
  systems: SystemRelationship[];
}

/**
 * Pure data-driven React Flow renderer. Dagre-positioned nodes; no interaction beyond pan/zoom.
 * Keeping this separate from the panel makes it easy to mock or snapshot-test in isolation.
 */
export default function SchemaRelationshipsGraph({
  componentTypeName,
  systems,
}: SchemaRelationshipsGraphProps) {
  const { nodes, edges } = useMemo(
    () => buildSchemaRelationshipsGraph(componentTypeName, systems),
    [componentTypeName, systems],
  );

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={{ default: CustomNode as never }}
      fitView
      proOptions={{ hideAttribution: true }}
      minZoom={0.3}
      maxZoom={1.6}
    >
      <Background color="var(--border)" gap={16} />
      <Controls showInteractive={false} position="bottom-left" />
      <MiniMap pannable zoomable position="bottom-right" />
    </ReactFlow>
  );
}

function CustomNode({ data }: { data: SchemaNode['data'] }) {
  if (data.kind === 'component') {
    return (
      <div className="rounded border-2 border-primary bg-card px-3 py-2 text-center shadow-md">
        <div className="text-fs-xs uppercase tracking-wider text-muted-foreground">component</div>
        <div className="font-mono text-fs-lg font-semibold text-foreground">{data.label}</div>
      </div>
    );
  }
  const accent =
    data.access === 'read' ? 'border-sky-500/60 bg-sky-900/30' : 'border-amber-500/60 bg-amber-900/30';
  return (
    <div className={`rounded border px-3 py-1.5 text-center shadow-sm ${accent}`}>
      <div className="text-fs-xs uppercase tracking-wider text-muted-foreground">
        {data.systemType ?? 'system'} · {data.access ?? ''}
      </div>
      <div className="font-mono text-fs-sm text-foreground">{data.label}</div>
    </div>
  );
}
