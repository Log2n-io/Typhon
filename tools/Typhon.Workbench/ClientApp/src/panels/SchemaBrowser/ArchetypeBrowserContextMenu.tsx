import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';
import type { ArchetypeInfo } from '@/hooks/schema/types';
import { openDataBrowser } from '@/shell/commands/openSchemaBrowser';

interface Props {
  archetype: ArchetypeInfo;
  children: ReactNode;
}

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Non-secure contexts / clipboard API unavailable — silently fail, matches Schema Browser.
  }
}

/**
 * Right-click menu for an Archetype Browser row. Mirrors SchemaBrowserContextMenu's shape — copy
 * helpers + stubs for cross-module actions that haven't shipped yet.
 */
export default function ArchetypeBrowserContextMenu({ archetype, children }: Props) {
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-60">
        <ContextMenuItem onSelect={() => copyToClipboard(archetype.archetypeId)}>
          Copy Archetype ID
        </ContextMenuItem>
        <ContextMenuItem onSelect={() => copyToClipboard(archetype.componentTypes.join('\n'))}>
          Copy Component Type Names
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem onSelect={() => openDataBrowser(archetype.archetypeId)}>
          Open in Data Browser
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem disabled className="text-muted-foreground">
          #{archetype.archetypeId} · {archetype.componentTypes.length} components
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>
  );
}
