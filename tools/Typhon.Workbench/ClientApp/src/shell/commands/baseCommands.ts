import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { refreshResourceGraph } from '@/hooks/useResourceIndex';
import {
  toggleViewAccessMatrix,
  toggleViewArchetypeBrowser,
  toggleViewComponentBrowser,
  toggleViewDataFlow,
  toggleViewDbMap,
  toggleViewDetail,
  toggleViewLogs,
  toggleViewOptions,
  toggleViewPaletteDebug,
  toggleViewResourceTree,
  toggleViewSchemaArchetypes,
  toggleViewSchemaIndexes,
  toggleViewSchemaRelationships,
  toggleViewSourcePreview,
  toggleViewSystemDag,
  openSourcePreviewForCurrentSpan,
  saveLayoutAsDefault,
  resetLayout,
} from './openSchemaBrowser';
import { buildProfilerPaletteCommands } from './profilerCommands';
import type { ConnectTab } from '@/shell/dialogs/ConnectDialog';

export interface CommandItem {
  id: string;
  label: string;
  keywords?: string;
  action: () => void;
}

/**
 * Connect-dialog opener. MenuBar mounts the dialog and registers its tab-aware open callback here so
 * palette commands can trigger it without prop-drilling. Same pattern as {@link registerOpenSaveReplay}.
 */
let registeredOpenConnect: ((tab: ConnectTab) => void) | null = null;

export function registerOpenConnect(fn: ((tab: ConnectTab) => void) | null): void {
  registeredOpenConnect = fn;
}

function openConnectDialog(tab: ConnectTab): void {
  registeredOpenConnect?.(tab);
}

export function buildBaseCommands(): CommandItem[] {
  const { sessionId, clearSession } = useSessionStore.getState();
  const { toggle: toggleTheme } = useThemeStore.getState();

  const closeSession = () => {
    if (!sessionId) return;
    deleteApiSessionsId(sessionId).then(clearSession).catch(() => {});
  };

  return [
    { id: 'open-file',     label: 'Open File…',               keywords: 'open typhon',      action: () => openConnectDialog('open') },
    { id: 'open-recent',   label: 'Open Recent',              keywords: 'recent file',       action: () => openConnectDialog('recent') },
    { id: 'attach',        label: 'Attach…',                  keywords: 'attach engine',     action: () => openConnectDialog('attach') },
    { id: 'open-trace',    label: 'Open Trace…',              keywords: 'trace typhon',      action: () => openConnectDialog('trace') },
    { id: 'close-session', label: 'Close Session',            keywords: 'close disconnect',  action: closeSession },
    { id: 'refresh-graph', label: 'Refresh Resource Graph',   keywords: 'refresh reload tree', action: refreshResourceGraph },
    { id: 'toggle-view-component-browser',    label: 'Toggle View Component Browser',    keywords: 'schema components inspector #schema browser', action: toggleViewComponentBrowser },
    { id: 'toggle-view-archetype-browser',    label: 'Toggle View Archetype Browser',    keywords: 'archetypes list schema cluster legacy',       action: toggleViewArchetypeBrowser },
    { id: 'toggle-view-schema-archetypes',    label: 'Toggle View Component Archetypes', keywords: 'schema archetypes cluster storage',           action: toggleViewSchemaArchetypes },
    { id: 'toggle-view-schema-indexes',       label: 'Toggle View Component Indexes',    keywords: 'schema indexes btree fields',                 action: toggleViewSchemaIndexes },
    { id: 'toggle-view-schema-relationships', label: 'Toggle View Component Relationships', keywords: 'schema systems relationships',             action: toggleViewSchemaRelationships },
    { id: 'toggle-view-system-dag',           label: 'Toggle View System DAG',              keywords: 'system dag scheduler topology phases rfc07', action: toggleViewSystemDag },
    { id: 'toggle-view-data-flow',            label: 'Toggle View Data Flow',               keywords: 'data flow timeline marey tracks granularity bars', action: toggleViewDataFlow },
    { id: 'toggle-view-access-matrix',        label: 'Toggle View Access Matrix',           keywords: 'access matrix heatmap systems components touch grid', action: toggleViewAccessMatrix },
    { id: 'toggle-view-dbmap',                label: 'Toggle View Database File Map',       keywords: 'database file map storage layout pages hilbert fragmentation disk', action: toggleViewDbMap },
    { id: 'toggle-view-resource-tree',        label: 'Toggle View Resource Tree',        keywords: 'resource tree sidebar explorer',              action: toggleViewResourceTree },
    { id: 'toggle-view-detail',               label: 'Toggle View Detail',               keywords: 'detail inspector selection',                  action: toggleViewDetail },
    { id: 'toggle-view-logs',                 label: 'Toggle View Logs',                 keywords: 'logs log console output messages bottom',     action: toggleViewLogs },
    { id: 'toggle-view-options',              label: 'Toggle View Options',              keywords: 'options preferences settings editor',         action: toggleViewOptions },
    { id: 'toggle-view-source-preview',       label: 'Toggle View Source Preview',       keywords: 'source preview code file line attribution profiler', action: toggleViewSourcePreview },
    { id: 'show-source-current-span', label: 'Show Source for Current Span', keywords: 'source preview profiler span go to attribution', action: openSourcePreviewForCurrentSpan },
    { id: 'save-layout-as-default', label: 'Save Layout as Default', keywords: 'layout default template save', action: saveLayoutAsDefault },
    { id: 'reset-layout', label: 'Reset Layout to Default', keywords: 'reset layout default restore panels dock recover lost', action: resetLayout },
    { id: 'toggle-theme',  label: 'Toggle Dark / Light Mode', keywords: 'theme dark light',  action: toggleTheme },
    { id: 'debug-color-palettes', label: 'Debug: Color Palettes', keywords: 'debug color colour palette palettes swatches dev', action: toggleViewPaletteDebug },
    ...buildProfilerPaletteCommands(),
    { id: 'reload',        label: 'Reload',                   keywords: 'refresh',           action: () => location.reload() },
    { id: 'about',         label: 'About Typhon Workbench',   keywords: 'version info',      action: () => {} },
  ];
}
