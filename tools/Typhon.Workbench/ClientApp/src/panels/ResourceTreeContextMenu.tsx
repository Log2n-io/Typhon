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
import { openComponentInSchema, openDbMapForComponent } from '@/shell/commands/openDbMap';
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
        {/* Cross-view handoffs — present only for a ComponentTable row (PC-6: no disabled stubs). The
            Component Inspector is always-on (shell inspector), so its handoff is unconditional; the File Map
            handoff appears when the map view is active. (GAP-02: the old "Show Component Layout" → SchemaLayout
            handoff is now "Open in Component Inspector", whose Layout tab is the layout's new home.) */}
        {canOpenInSchema && (isViewActive('ComponentInspector') || isViewActive('DbMap')) && <ContextMenuSeparator />}
        {canOpenInSchema && isViewActive('ComponentInspector') && (
          <ContextMenuItem
            onSelect={() => {
              // ComponentTable nodes carry the resource-tree name "ComponentTable_{Definition.Name}"
              // (see ComponentTable's base(...) call in the engine). The server looks up by the raw
              // Definition.Name, so we strip the prefix.
              const typeName = name.startsWith('ComponentTable_') ? name.slice('ComponentTable_'.length) : name;
              openComponentInSchema(typeName);
            }}
          >
            Open in Component Inspector
          </ContextMenuItem>
        )}
        {canOpenInSchema && isViewActive('DbMap') && (
          <ContextMenuItem
            onSelect={() => {
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
