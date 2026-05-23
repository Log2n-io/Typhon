import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Non-secure contexts / clipboard API unavailable — silently fail, matches the Schema Browser menu.
  }
}

/** Right-click menu for an entity row. Cross-view handoffs (e.g. Query Console) return with their views in later stages. */
export default function EntityListContextMenu({ entityId, children }: { entityId: string; children: ReactNode }) {
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-56">
        <ContextMenuItem onSelect={() => copyToClipboard(entityId)}>Copy Entity Id</ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>
  );
}
