import type { ReactNode } from 'react';
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from '@/components/ui/context-menu';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import {
  openDataBrowser,
  toggleViewSchemaArchetypes,
  toggleViewSchemaIndexes,
  toggleViewSchemaRelationships,
} from '@/shell/commands/openSchemaBrowser';
import type { ComponentSummary } from '@/hooks/schema/types';
import type { ArchetypeInfoDto } from '@/api/generated/model';
import { customFetch } from '@/api/client';
import { useSessionStore } from '@/stores/useSessionStore';

interface Props {
  component: ComponentSummary;
  onOpenInLayout: (typeName: string) => void;
  children: ReactNode;
}

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Non-secure contexts / clipboard API unavailable — silently fail, matches Resource Explorer.
  }
}

/**
 * Right-click menu for a Schema Browser row. Primary action is "Open in Schema Layout" (same as
 * double-clicking). Cross-module actions (Data Browser, Query Console) are stubbed as disabled
 * until their modules ship — symmetric with the Resource Explorer context menu.
 */
export default function SchemaBrowserContextMenu({ component, onOpenInLayout, children }: Props) {
  const selectComponent = useSchemaInspectorStore((s) => s.selectComponent);
  const selectAndOpen = (open: () => void) => () => {
    selectComponent(component.typeName);
    open();
  };

  // Open the Data Browser focused on an archetype that declares this component. A component can belong to several
  // archetypes; v1 picks the first. Resolved on click (not per row render) to avoid a fetch for every menu instance.
  const openInDataBrowser = async () => {
    const sessionId = useSessionStore.getState().sessionId;
    if (!sessionId) {
      openDataBrowser();
      return;
    }
    try {
      const res = await customFetch<{ data: ArchetypeInfoDto[] | undefined }>(
        `/api/sessions/${sessionId}/schema/components/${component.typeName}/archetypes`,
        { method: 'GET' },
      );
      const first = res.data?.[0];
      openDataBrowser(first ? String(first.archetypeId) : undefined);
    } catch {
      openDataBrowser();
    }
  };

  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <ContextMenuContent className="w-60">
        <ContextMenuItem onSelect={() => onOpenInLayout(component.typeName)}>
          Show Component Layout
        </ContextMenuItem>
        <ContextMenuItem onSelect={selectAndOpen(toggleViewSchemaArchetypes)}>
          Show Component Archetypes
        </ContextMenuItem>
        <ContextMenuItem onSelect={selectAndOpen(toggleViewSchemaIndexes)}>
          Show Component Indexes
        </ContextMenuItem>
        <ContextMenuItem onSelect={selectAndOpen(toggleViewSchemaRelationships)}>
          Show Component Relationships
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem onSelect={() => copyToClipboard(component.typeName)}>
          Copy Type Name
        </ContextMenuItem>
        <ContextMenuItem
          onSelect={() => copyToClipboard(component.fullName)}
          disabled={component.fullName === component.typeName}
          title={component.fullName === component.typeName ? 'Same as Type Name' : undefined}
        >
          Copy Fully-Qualified Name
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem onSelect={openInDataBrowser}>Open in Data Browser</ContextMenuItem>
        <ContextMenuItem disabled>Open in Query Console</ContextMenuItem>
        <ContextMenuItem disabled>Reveal in Resource Tree</ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuItem disabled className="text-muted-foreground">
          {component.typeName}
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>
  );
}
