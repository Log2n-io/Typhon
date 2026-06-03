import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { openQueryConsole } from '@/shell/commands/openQueryConsole';

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Non-secure contexts / clipboard API unavailable — silently fail, matches the Schema Browser menu.
  }
}

/**
 * Right-click menu for an entity row. #386 Phase 1 (AC-17): adds "Query this archetype" — opens the Query Console
 * pre-filled with the entity's archetype (the design §2 cross-link). Archetype id is the current Data-Browser
 * archetype the row belongs to (each list is archetype-scoped).
 */
export default function EntityListContextMenu({ entityId, children }: { entityId: string; children: ReactNode }) {
  const archetypeId = useDataBrowserStore((s) => s.archetypeId);
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-56">
        <ContextMenuItem onSelect={() => copyToClipboard(entityId)}>Copy Entity Id</ContextMenuItem>
        {archetypeId && (
          <ContextMenuItem onSelect={() => openQueryConsole({ fromArchetype: archetypeId })}>
            Query this archetype…
          </ContextMenuItem>
        )}
      </ContextMenuContent>
    </ContextMenu>
  );
}
