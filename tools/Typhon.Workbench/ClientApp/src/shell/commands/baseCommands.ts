import { deleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useDensityStore } from '@/stores/useDensityStore';
import { refreshResourceGraph } from '@/hooks/useResourceIndex';
import {
  toggleViewDataBrowser,
  toggleViewDataFlow,
  toggleViewDbMap,
  toggleViewStorageHealth,
  toggleViewDetail,
  toggleViewLogs,
  toggleViewOptions,
  toggleViewPaletteDebug,
  toggleViewResourceTree,
  toggleViewSourcePreview,
  toggleViewSystemDag,
  openSourcePreviewForCurrentSpan,
  saveLayoutAsDefault,
  resetLayout,
} from './openSchemaBrowser';
import { buildProfilerPaletteCommands } from './profilerCommands';
import { isViewActive } from '@/shell/viewRegistry';
import type { ConnectTab } from '@/shell/dialogs/ConnectDialog';

export interface CommandItem {
  id: string;
  label: string;
  keywords?: string;
  action: () => void;
  // Dockview component id of the view this command opens/drives, when it is bound to one. Commands whose
  // view is gated off (Stage 0) are filtered out of the palette; shell commands leave this undefined.
  viewId?: string;
}

/**
 * Connect-dialog opener. MenuBar mounts the dialog and registers its tab-aware open callback here so
 * palette commands can trigger it without prop-drilling. Same pattern as {@link registerOpenSaveReplay}.
 */
let registeredOpenConnect: ((tab: ConnectTab) => void) | null = null;

export function registerOpenConnect(fn: ((tab: ConnectTab) => void) | null): void {
  registeredOpenConnect = fn;
}

/** Open the Connect dialog on a given tab. Exported so blocked-state banners can offer a forward action. */
export function openConnect(tab: ConnectTab): void {
  registeredOpenConnect?.(tab);
}

export function buildBaseCommands(): CommandItem[] {
  const { sessionId, clearSession } = useSessionStore.getState();
  const { toggle: toggleTheme } = useThemeStore.getState();

  const closeSession = () => {
    if (!sessionId) return;
    deleteApiSessionsId(sessionId).then(clearSession).catch(() => {});
  };

  const commands: CommandItem[] = [
    { id: 'open-file',     label: 'Open File…',               keywords: 'open typhon',      action: () => openConnect('open') },
    { id: 'open-recent',   label: 'Open Recent',              keywords: 'recent file',       action: () => openConnect('recent') },
    { id: 'attach',        label: 'Attach…',                  keywords: 'attach engine',     action: () => openConnect('attach') },
    { id: 'open-trace',    label: 'Open Trace…',              keywords: 'trace typhon',      action: () => openConnect('trace') },
    { id: 'close-session', label: 'Close Session',            keywords: 'close disconnect',  action: closeSession },
    { id: 'refresh-graph', label: 'Refresh Resource Graph',   keywords: 'refresh reload tree', action: refreshResourceGraph },
    // (Stage 2 / GAP-02: the schema-archetypes/indexes/relationships toggle commands were removed with the
    //  four Schema* panels — those facts now live in the Component Inspector tabs.)
    { id: 'toggle-view-system-dag',           label: 'Toggle View System DAG',              keywords: 'system dag scheduler topology phases rfc07', action: toggleViewSystemDag, viewId: 'SystemDag' },
    { id: 'toggle-view-data-flow',            label: 'Toggle View Data Flow',               keywords: 'data flow timeline matrix marey tracks granularity bars access heatmap systems components', action: toggleViewDataFlow, viewId: 'DataFlow' },
    { id: 'toggle-view-dbmap',                label: 'Toggle View Database File Map',       keywords: 'database file map storage layout pages hilbert fragmentation disk', action: toggleViewDbMap, viewId: 'DbMap' },
    { id: 'toggle-view-storage-health',       label: 'Open Storage Health',                 keywords: 'storage health dashboard segments occupancy dirty reclaimable fragmentation wal disk aggregate', action: toggleViewStorageHealth, viewId: 'StorageHealth' },
    { id: 'data-browser',                     label: 'Open Data Browser',                   keywords: 'data browser entities components values inspect crud rows', action: () => toggleViewDataBrowser(), viewId: 'DataBrowserEntities' },
    { id: 'toggle-view-resource-tree',        label: 'Toggle View Resource Tree',        keywords: 'resource tree sidebar explorer',              action: toggleViewResourceTree },
    { id: 'toggle-view-detail',               label: 'Toggle View Detail',               keywords: 'detail inspector selection',                  action: toggleViewDetail },
    { id: 'toggle-view-logs',                 label: 'Toggle View Logs',                 keywords: 'logs log console output messages bottom',     action: toggleViewLogs },
    { id: 'toggle-view-options',              label: 'Toggle View Options',              keywords: 'options preferences settings editor',         action: toggleViewOptions },
    { id: 'toggle-view-source-preview',       label: 'Toggle View Source Preview',       keywords: 'source preview code file line attribution profiler', action: toggleViewSourcePreview, viewId: 'SourcePreview' },
    { id: 'show-source-current-span', label: 'Show Source for Current Span', keywords: 'source preview profiler span go to attribution', action: openSourcePreviewForCurrentSpan, viewId: 'SourcePreview' },
    { id: 'save-layout-as-default', label: 'Save Layout as Default', keywords: 'layout default template save', action: saveLayoutAsDefault },
    { id: 'reset-layout', label: 'Reset Layout to Default', keywords: 'reset layout default restore panels dock recover lost', action: resetLayout },
    { id: 'toggle-theme',  label: 'Toggle Dark / Light Mode', keywords: 'theme dark light',  action: toggleTheme },
    { id: 'cycle-density', label: 'Cycle Density (Compact / Normal / Comfortable)', keywords: 'density compact normal comfortable rows spacing font size', action: () => useDensityStore.getState().cycle() },
    // PaletteDebug is a dev-only color-swatch view — present only in DEBUG/dev builds (IA §9.1/§9.4).
    ...(import.meta.env.DEV
      ? [{ id: 'debug-color-palettes', label: 'Debug: Color Palettes', keywords: 'debug color colour palette palettes swatches dev', action: toggleViewPaletteDebug }]
      : []),
    ...buildProfilerPaletteCommands(),
    { id: 'reload',        label: 'Reload',                   keywords: 'refresh',           action: () => location.reload() },
  ];

  // Drop commands bound to a deactivated view (Stage 0 shell frame). Shell commands (viewId undefined) stay.
  return commands.filter((c) => isViewActive(c.viewId));
}
