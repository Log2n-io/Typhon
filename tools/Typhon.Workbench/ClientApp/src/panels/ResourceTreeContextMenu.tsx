import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';
import { useRecentFilesStore } from '@/stores/useRecentFilesStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useResourceGraphStore } from '@/stores/useResourceGraphStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { toggleViewSchemaLayout } from '@/shell/commands/openSchemaBrowser';
import { openDbMapForComponent } from '@/shell/commands/openDbMap';
import { isViewActive } from '@/shell/viewRegistry';

interface Props {
  resourceId: string;          // synthetic uid — used for pin storage (unique)
  naturalId: string;           // engine-native id (display / copy)
  name: string;
  kind: string;                // resource type (e.g., "ComponentTable"); drives contextual action availability
  path: string[];
  onReveal: () => void;        // clears filter + scrolls to row
  onRefreshSubtree: () => void;
  children: ReactNode;
}

export default function ResourceTreeContextMenu({
  resourceId,
  naturalId,
  name,
  kind,
  path,
  onReveal,
  onRefreshSubtree,
  children,
}: Props) {
  const filePath = useSessionStore((s) => s.filePath);
  const pins = useRecentFilesStore((s) => (filePath ? s.getPins(filePath) : []));
  const pinResource = useRecentFilesStore((s) => s.pinResource);
  const unpinResource = useRecentFilesStore((s) => s.unpinResource);
  const clearFilter = useResourceGraphStore((s) => s.setFilter);
  const selectSchemaComponent = useSchemaInspectorStore((s) => s.selectComponent);

  const isPinned = pins.includes(resourceId);
  const pathStr = path.join('/');
  const canOpenInSchema = kind === 'ComponentTable';

  async function copyToClipboard(text: string) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Non-secure contexts / clipboard API unavailable — silently fail for now.
    }
  }

  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-56">
        <ContextMenuItem
          onSelect={() => {
            if (!filePath) return;
            if (isPinned) unpinResource(filePath, resourceId);
            else pinResource(filePath, resourceId);
          }}
          disabled={!filePath}
        >
          {isPinned ? 'Unpin' : 'Pin'}
        </ContextMenuItem>
        <ContextMenuItem onSelect={() => copyToClipboard(pathStr)}>
          Copy Path
        </ContextMenuItem>
        <ContextMenuItem
          onSelect={() => copyToClipboard(`{{ref:${naturalId}}}`)}
          title="DSL format finalized with Query Console"
        >
          Copy as DSL Reference
        </ContextMenuItem>
        <ContextMenuItem
          onSelect={() => {
            clearFilter('');
            onReveal();
          }}
        >
          Reveal in Tree
        </ContextMenuItem>
        <ContextMenuItem onSelect={onRefreshSubtree}>
          Refresh Subtree
        </ContextMenuItem>
        {/* Cross-view handoffs to deep views — present only while the target view is active (gated off in
            Stage 0; they return with their view in later stages). Stub "Open in …" verbs to not-yet-built
            views are omitted entirely rather than shown disabled (PC-6 / no broken affordances). */}
        {(isViewActive('SchemaLayout') || isViewActive('DbMap')) && <ContextMenuSeparator />}
        {isViewActive('SchemaLayout') && (
          <ContextMenuItem
            disabled={!canOpenInSchema}
            onSelect={() => {
              if (!canOpenInSchema) return;
              // ComponentTable nodes carry the resource-tree name "ComponentTable_{Definition.Name}"
              // (see ComponentTable's base(...) call in the engine). The server looks up by the raw
              // Definition.Name, so we strip the prefix.
              const typeName = name.startsWith('ComponentTable_') ? name.slice('ComponentTable_'.length) : name;
              selectSchemaComponent(typeName);
              toggleViewSchemaLayout();
            }}
          >
            Show Component Layout
          </ContextMenuItem>
        )}
        {isViewActive('DbMap') && (
          <ContextMenuItem
            disabled={!canOpenInSchema}
            onSelect={() => {
              if (!canOpenInSchema) return;
              const typeName = name.startsWith('ComponentTable_') ? name.slice('ComponentTable_'.length) : name;
              openDbMapForComponent(typeName);
            }}
          >
            Show in File Map
          </ContextMenuItem>
        )}
        <ContextMenuSeparator />
        <ContextMenuItem disabled className="text-muted-foreground">
          {name}
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>
  );
}
