import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { refreshResourceGraph } from '@/hooks/useResourceIndex';
import {
  openArchetypeBrowser,
  openDetailPanel,
  openOptions,
  openSchemaArchetypes,
  openSchemaBrowser,
  openSchemaIndexes,
  openSchemaRelationships,
  openSourcePreviewForCurrentSpan,
} from './openSchemaBrowser';
import { buildProfilerPaletteCommands } from './profilerCommands';

export interface CommandItem {
  id: string;
  label: string;
  keywords?: string;
  action: () => void;
}

export function buildBaseCommands(): CommandItem[] {
  const { sessionId, clearSession } = useSessionStore.getState();
  const { toggle: toggleTheme } = useThemeStore.getState();

  const closeSession = () => {
    if (!sessionId) return;
    deleteApiSessionsId(sessionId).then(clearSession).catch(() => {});
  };

  return [
    { id: 'open-file',     label: 'Open File…',               keywords: 'open typhon',      action: () => {} },
    { id: 'open-recent',   label: 'Open Recent',              keywords: 'recent file',       action: () => {} },
    { id: 'attach',        label: 'Attach…',                  keywords: 'attach engine',     action: () => {} },
    { id: 'open-trace',    label: 'Open Trace…',              keywords: 'trace typhon',      action: () => {} },
    { id: 'close-session', label: 'Close Session',            keywords: 'close disconnect',  action: closeSession },
    { id: 'refresh-graph', label: 'Refresh Resource Graph',   keywords: 'refresh reload tree', action: refreshResourceGraph },
    { id: 'schema-browser', label: 'Open Component Browser',       keywords: 'schema components inspector #schema browser', action: openSchemaBrowser },
    { id: 'archetype-browser', label: 'Open Archetype Browser',    keywords: 'archetypes list schema cluster legacy',       action: openArchetypeBrowser },
    { id: 'schema-archetypes', label: 'Open Component Archetypes', keywords: 'schema archetypes cluster storage',           action: openSchemaArchetypes },
    { id: 'schema-indexes', label: 'Open Component Indexes',       keywords: 'schema indexes btree fields',                 action: openSchemaIndexes },
    { id: 'schema-relationships', label: 'Open Component Relationships', keywords: 'schema systems relationships',          action: openSchemaRelationships },
    { id: 'open-detail',   label: 'Open Detail Panel',        keywords: 'detail inspector selection', action: openDetailPanel },
    { id: 'open-options',  label: 'Open Options',             keywords: 'options preferences settings editor', action: openOptions },
    { id: 'show-source-current-span', label: 'Show Source for Current Span', keywords: 'source preview profiler span go to attribution', action: openSourcePreviewForCurrentSpan },
    { id: 'toggle-theme',  label: 'Toggle Dark / Light Mode', keywords: 'theme dark light',  action: toggleTheme },
    ...buildProfilerPaletteCommands(),
    { id: 'reload',        label: 'Reload',                   keywords: 'refresh',           action: () => location.reload() },
    { id: 'about',         label: 'About Typhon Workbench',   keywords: 'version info',      action: () => {} },
  ];
}
