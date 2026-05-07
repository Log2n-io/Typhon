import type { IDockviewPanelProps } from 'dockview-react';
import { AlertTriangle, Info } from 'lucide-react';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useSystemRelationships } from '@/hooks/schema/useSystemRelationships';
import AccessChips from './AccessChips';
import SchemaRelationshipsGraph from './SchemaRelationshipsGraph';

/**
 * Relationships panel for the Schema Inspector. Two surfaces:
 *
 *   1. <b>Access chips</b> ({@link AccessChips}) — read/write/snapshot/fresh/side-write
 *      declarations sourced from the topology endpoint (RFC 07 — Unit 6 surfacing).
 *   2. <b>Relationships graph</b> — systems that read or reactively trigger on the focused
 *      component, rendered as a React Flow DAG.
 *
 * <b>Runtime hosting gate</b> — the Workbench does not host a <c>TyphonRuntime</c> in Phase 2.
 * When the server returns <c>runtimeHosted: false</c>, the relationships graph below renders an
 * empty preview (just the component node). The access chips work regardless of runtime hosting:
 * declarations come from the trace v6 wire format / live Init payload, not from a hosted runtime.
 */
export default function SchemaRelationshipsPanel(_props: IDockviewPanelProps) {
  const selectedType = useSchemaInspectorStore((s) => s.selectedComponentType);
  const { response, isLoading, isError } = useSystemRelationships(selectedType);

  if (!selectedType) {
    return (
      <div className="flex h-full items-center justify-center bg-background">
        <p className="text-[12px] text-muted-foreground">No component selected.</p>
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-[12px] font-semibold text-foreground">{selectedType}</h3>
        <span className="text-[11px] text-muted-foreground">system relationships</span>
      </div>

      <AccessChips componentTypeName={selectedType} />

      {!response.runtimeHosted && (
        <div className="flex items-start gap-2 border-b border-amber-700/40 bg-amber-950/30 px-3 py-2 text-[11px] text-amber-200">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <div>
            <div className="font-semibold">Runtime not hosted</div>
            <div className="text-amber-300/90">
              The Workbench does not yet host a <code className="rounded bg-muted/30 px-1">TyphonRuntime</code>, so
              system relationships cannot be reported. This is tracked as post-bootstrap work; the panel will
              populate automatically when runtime hosting lands.
            </div>
          </div>
        </div>
      )}

      {response.runtimeHosted && response.systems.length === 0 && (
        <div className="flex items-start gap-2 border-b border-border bg-muted/20 px-3 py-2 text-[11px] text-muted-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0" />
          <div>No system reads this component via its input view, and no system declares it as a change-filter trigger.</div>
        </div>
      )}

      <div className="flex-1" data-testid="schema-relationships-canvas">
        {isError && <p className="p-3 text-[12px] text-destructive">Failed to load relationships.</p>}
        {isLoading && <p className="p-3 text-[12px] text-muted-foreground">Loading relationships…</p>}
        {!isLoading && !isError && (
          <SchemaRelationshipsGraph componentTypeName={selectedType} systems={response.systems} />
        )}
      </div>
    </div>
  );
}
