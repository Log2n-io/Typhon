import { useEffect, useRef } from 'react';
import { useThemeStore } from '@/stores/useThemeStore';
import { useLogStore, selectUnseenLevel, selectUnseenCount, type LogLevel } from '@/stores/useLogStore';
import { DockviewDefaultTab, DockviewReact, themeDark, themeLight, type DockviewApi, type DockviewReadyEvent, type IDockviewDefaultTabProps, type IDockviewPanelHeaderProps, type IDockviewPanelProps } from 'dockview-react';
import { useDockLayoutStore } from '@/stores/useDockLayoutStore';
import { useSessionStore } from '@/stores/useSessionStore';
import DetailPanel from '@/panels/DetailPanel';
import LogsPanel from '@/panels/LogsPanel';
import ResourceTreePanel from '@/panels/ResourceTreePanel';
import PlaceholderStartHere from '@/panels/PlaceholderStartHere';
import SchemaBrowserPanel from '@/panels/SchemaBrowser/SchemaBrowserPanel';
import ArchetypeBrowserPanel from '@/panels/SchemaBrowser/ArchetypeBrowserPanel';
import SchemaLayoutPanel from '@/panels/SchemaInspector/SchemaLayoutPanel';
import SchemaArchetypePanel from '@/panels/SchemaInspector/SchemaArchetypePanel';
import SchemaIndexPanel from '@/panels/SchemaInspector/SchemaIndexPanel';
import SchemaRelationshipsPanel from '@/panels/SchemaInspector/SchemaRelationshipsPanel';
import SystemDagPanel from '@/panels/SystemDag/SystemDagPanel';
import DataFlowPanel from '@/panels/DataFlow/DataFlowPanel';
import AccessMatrixPanel from '@/panels/AccessMatrix/AccessMatrixPanel';
import CriticalPathPanel from '@/panels/CriticalPath/CriticalPathPanel';
import ProfilerPanel from '@/panels/profiler/ProfilerPanel';
import TopSpansPanel from '@/panels/profiler/TopSpansPanel';
import CallTreePanel from '@/panels/profiler/CallTree';
import OptionsPanel from '@/panels/options/OptionsPanel';
import SourcePreviewPanel from '@/panels/profiler/SourcePreviewPanel';
import QueryCatalogPanel from '@/panels/QueryCatalog/QueryCatalogPanel';
import QueryPlanTreePanel from '@/panels/QueryPlanTree/QueryPlanTreePanel';
import ExecutionInspectorPanel from '@/panels/ExecutionInspector/ExecutionInspectorPanel';
import PaletteDebugPanel from '@/panels/PaletteDebug';
import DbMapPanel from '@/panels/DbMap/DbMapPanel';
import EntityListPanel from '@/panels/DataBrowser/EntityListPanel';
import { registerDockApi, registerResetLayout } from './commands/openSchemaBrowser';
import { registerProfilerDockApi } from './commands/profilerCommands';
import MigrationRequiredBanner from './banners/MigrationRequiredBanner';
import IncompatibleBanner from './banners/IncompatibleBanner';

// Tab component without a close button — applied to structural panels that should not be closable.
const PlainLockedTab: React.FC<IDockviewPanelHeaderProps> = (props) => (
  <DockviewDefaultTab {...(props as IDockviewDefaultTabProps)} hideClose />
);

// Badge color per severity — reuses the status-badge palette so the Workbench reads consistently.
// Light backgrounds (info/warn) take dark text; the red error badge takes white text.
const LOG_BADGE_CLASS: Record<LogLevel, string> = {
  info: 'bg-sky-400 text-slate-900',
  warn: 'bg-amber-400 text-slate-900',
  error: 'bg-red-400 text-white',
};

// Logs tab: a locked tab that also shows an unseen-activity badge. When the panel is hidden and
// logs are published, a badge next to the title shows how many arrived, colored by the most
// critical level among them; it clears once the panel becomes visible again. `dockview`'s
// onDidVisibilityChange covers tab-switch and whole-group hide but NOT edge-group collapse
// (View → Logs), so the group's onDidCollapsedChange is tracked too — effective visibility is
// `isVisible && !group.isCollapsed()`.
const LogsTab: React.FC<IDockviewPanelHeaderProps> = (props) => {
  const { api } = props;
  const setLogsVisible = useLogStore((s) => s.setLogsVisible);
  const unseenLevel = useLogStore(selectUnseenLevel);
  const unseenCount = useLogStore(selectUnseenCount);

  useEffect(() => {
    const sync = () => setLogsVisible(api.isVisible && !api.group.api.isCollapsed());
    sync(); // correct the store's optimistic default against the real layout state
    const visSub = api.onDidVisibilityChange(sync);
    let groupSub = api.group.api.onDidCollapsedChange(sync);
    // Re-bind the collapse subscription if the panel is ever moved to a different group.
    const groupChangeSub = api.onDidGroupChange(() => {
      groupSub.dispose();
      groupSub = api.group.api.onDidCollapsedChange(sync);
      sync();
    });
    return () => {
      visSub.dispose();
      groupSub.dispose();
      groupChangeSub.dispose();
    };
  }, [api, setLogsVisible]);

  return (
    <div className="flex items-center gap-1.5">
      {unseenCount > 0 && (
        <span
          className={
            'pointer-events-none flex h-4 min-w-4 shrink-0 items-center justify-center rounded-full ' +
            `px-1 text-[10px] font-medium tabular-nums ${LOG_BADGE_CLASS[unseenLevel ?? 'info']}`
          }
          title={`${unseenCount} new log entr${unseenCount === 1 ? 'y' : 'ies'} since last viewed`}
        >
          {unseenCount > 99 ? '99+' : unseenCount}
        </span>
      )}
      <DockviewDefaultTab {...(props as IDockviewDefaultTabProps)} hideClose />
    </div>
  );
};

// Locked-tab dispatcher. The Logs panel keeps `tabComponent: 'locked'` (so persisted layouts need
// no migration) and is routed to the activity-dot variant by its component id.
const LockedTab: React.FC<IDockviewPanelHeaderProps> = (props) =>
  props.api.component === 'Logs' ? <LogsTab {...props} /> : <PlainLockedTab {...props} />;

const tabComponents: Record<string, React.FC<IDockviewPanelHeaderProps>> = {
  locked: LockedTab,
};

const SAVE_DEBOUNCE_MS = 1_500;
const EDGE_LEFT_ID = 'edge-left';
const EDGE_RIGHT_ID = 'edge-right';
const EDGE_BOTTOM_ID = 'edge-bottom';

const components: Record<string, React.FC<IDockviewPanelProps>> = {
  ResourceTree: ResourceTreePanel,
  StartHere: PlaceholderStartHere,
  Detail: DetailPanel,
  Logs: LogsPanel,
  SchemaBrowser: SchemaBrowserPanel,
  ArchetypeBrowser: ArchetypeBrowserPanel,
  SchemaLayout: SchemaLayoutPanel,
  SchemaArchetypes: SchemaArchetypePanel,
  SchemaIndexes: SchemaIndexPanel,
  SchemaRelationships: SchemaRelationshipsPanel,
  SystemDag: SystemDagPanel,
  DataFlow: DataFlowPanel,
  AccessMatrix: AccessMatrixPanel,
  CriticalPath: CriticalPathPanel,
  Profiler: ProfilerPanel,
  TopSpans: TopSpansPanel,
  CallTree: CallTreePanel,
  Options: OptionsPanel,
  SourcePreview: SourcePreviewPanel,
  QueryCatalog: QueryCatalogPanel,
  QueryPlanTree: QueryPlanTreePanel,
  ExecutionInspector: ExecutionInspectorPanel,
  PaletteDebug: PaletteDebugPanel,
  DbMap: DbMapPanel,
  DataBrowserEntities: EntityListPanel,
};

function buildDefaultLayout(api: DockviewReadyEvent['api'], kind: 'none' | 'open' | 'attach' | 'trace') {
  if (kind === 'trace' || kind === 'attach') {
    api.addEdgeGroup('right', { id: EDGE_RIGHT_ID, initialSize: 320, minimumSize: 200 });
    api.addEdgeGroup('bottom', { id: EDGE_BOTTOM_ID, initialSize: 200, minimumSize: 100 });

    api.addPanel({ id: 'profiler', component: 'Profiler', title: 'Profiler', tabComponent: 'locked' });

    api.addPanel({
      id: 'detail',
      component: 'Detail',
      title: 'Detail',
      tabComponent: 'locked',
      position: { referenceGroup: EDGE_RIGHT_ID },
    });

    api.addPanel({
      id: 'logs',
      component: 'Logs',
      title: 'Logs',
      tabComponent: 'locked',
      position: { referenceGroup: EDGE_BOTTOM_ID },
    });

    api.addPanel({
      id: 'top-spans',
      component: 'TopSpans',
      title: 'Top spans',
      tabComponent: 'locked',
      position: { referenceGroup: EDGE_BOTTOM_ID },
    });

    // Trace-mode schema panels (v7+ static-data tables in the trace file feed these). Stacked behind the Detail
    // panel in the right edge group so they're discoverable without dominating the layout. Attach mode keeps them
    // available because the wire layout exists today (empty placeholders) — when the engine starts pushing schema
    // over the live socket the same panels light up automatically. Each panel handles the "no schema data" empty
    // state on its own, so showing them costs nothing when the data isn't there.
    if (kind === 'trace') {
      api.addPanel({
        id: 'schema-browser',
        component: 'SchemaBrowser',
        title: 'Components',
        position: { referenceGroup: EDGE_RIGHT_ID },
      });
      api.addPanel({
        id: 'archetype-browser',
        component: 'ArchetypeBrowser',
        title: 'Archetypes',
        position: { referenceGroup: EDGE_RIGHT_ID },
      });
      // Workbench Data Flow module (#327): both panels are reachable via View → Data Flow / Access Matrix and the
      // command palette (Toggle View Data Flow / Toggle View Access Matrix). They stay out of the trace-mode default
      // layout because piling them into the right edge group with Detail+Components+Archetypes collapses every panel
      // there into a thin vertical-label tab strip — bars and matrix cells render at sub-pixel widths, invisible. The
      // toggle commands open them in the center group at a usable width when the user actually wants them.
    }
    return;
  }

  // Open / none layout.
  api.addEdgeGroup('left', { id: EDGE_LEFT_ID, initialSize: 260, minimumSize: 150 });
  api.addEdgeGroup('right', { id: EDGE_RIGHT_ID, initialSize: 320, minimumSize: 200 });
  api.addEdgeGroup('bottom', { id: EDGE_BOTTOM_ID, initialSize: 200, minimumSize: 100 });

  // Add start-here before any edge-group panel so it creates the center group while no
  // edge group is active — otherwise dockview inherits the most-recently-touched group.
  api.addPanel({ id: 'start-here', component: 'StartHere', title: 'Start Here' });

  api.addPanel({
    id: 'resource-tree',
    component: 'ResourceTree',
    title: 'Resources',
    tabComponent: 'locked',
    position: { referenceGroup: EDGE_LEFT_ID },
  });

  api.addPanel({
    id: 'detail',
    component: 'Detail',
    title: 'Detail',
    tabComponent: 'locked',
    position: { referenceGroup: EDGE_RIGHT_ID },
  });

  api.addPanel({
    id: 'logs',
    component: 'Logs',
    title: 'Logs',
    tabComponent: 'locked',
    position: { referenceGroup: EDGE_BOTTOM_ID },
  });
}

export default function DockHost() {
  const filePath = useSessionStore((s) => s.filePath);
  const sessionState = useSessionStore((s) => s.sessionState);
  const kind = useSessionStore((s) => s.kind);
  const layoutKey = filePath ? `${kind}:${filePath}` : `${kind}:default`;
  const getLayout = useDockLayoutStore((s) => s.get);
  const saveLayout = useDockLayoutStore((s) => s.save);
  const getTemplate = useDockLayoutStore((s) => s.getTemplate);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const apiRef = useRef<DockviewApi | null>(null);
  function onReady(event: DockviewReadyEvent) {
    apiRef.current = event.api;
    registerDockApi(event.api);
    registerProfilerDockApi(event.api);
    // Tear down every panel/group and rebuild this session kind's built-in default. The recovery path for both the
    // reset-layout command and a failed restore. api.clear() empties the edge groups but keeps the now-empty group shells,
    // and buildDefaultLayout's addEdgeGroup() throws on a position that still exists — so a partially-applied fromJSON (e.g.
    // a saved layout that references a since-removed panel component) must be fully torn down first for a clean rebuild.
    const rebuildDefault = () => {
      event.api.clear();
      for (const pos of ['left', 'right', 'bottom'] as const) {
        if (event.api.getEdgeGroup(pos)) {
          event.api.removeEdgeGroup(pos);
        }
      }
      buildDefaultLayout(event.api, kind);
    };

    registerResetLayout(rebuildDefault);
    const saved = getLayout(layoutKey);
    if (saved) {
      try {
        event.api.fromJSON(saved as Parameters<typeof event.api.fromJSON>[0]);
      } catch {
        // Saved layout invalid (version skew, or references a removed panel component) — tear down + rebuild default.
        rebuildDefault();
      }
    } else {
      const template = getTemplate(kind);
      if (template) {
        try {
          event.api.fromJSON(template as Parameters<typeof event.api.fromJSON>[0]);
        } catch {
          rebuildDefault();
        }
      } else {
        buildDefaultLayout(event.api, kind);
      }
    }

    // Trace-session safety net: profiler lives in the center area and must always be present.
    if (kind === 'trace' && !event.api.getPanel('profiler')) {
      event.api.addPanel({ id: 'profiler', component: 'Profiler', title: 'Profiler', tabComponent: 'locked' });
    }

    // Bottom-edge-group panel safety net. A stale saved layout can restore without the Logs and/or
    // Top-spans panels (and without the bottom edge group itself) — View → Logs would then have
    // nothing to surface. Re-create whatever is missing; the edge group is added only when a panel
    // actually needs it, so a layout that kept the panels elsewhere isn't given a spurious empty group.
    const needLogs = !event.api.getPanel('logs');
    const needTopSpans = (kind === 'trace' || kind === 'attach') && !event.api.getPanel('top-spans');
    if ((needLogs || needTopSpans) && !event.api.getEdgeGroup('bottom')) {
      event.api.addEdgeGroup('bottom', { id: EDGE_BOTTOM_ID, initialSize: 200, minimumSize: 100 });
    }
    if (needLogs) {
      event.api.addPanel({
        id: 'logs',
        component: 'Logs',
        title: 'Logs',
        tabComponent: 'locked',
        position: { referenceGroup: EDGE_BOTTOM_ID },
      });
    }
    if (needTopSpans) {
      event.api.addPanel({
        id: 'top-spans',
        component: 'TopSpans',
        title: 'Top spans',
        tabComponent: 'locked',
        position: { referenceGroup: EDGE_BOTTOM_ID },
      });
    }

    event.api.onDidLayoutChange(() => {
      clearTimeout(debounceRef.current);
      debounceRef.current = setTimeout(() => {
        saveLayout(layoutKey, event.api.toJSON());
      }, SAVE_DEBOUNCE_MS);
    });
  }

  const theme = useThemeStore((s) => s.theme);
  const showMigration = sessionState === 'MigrationRequired';
  const showIncompatible = sessionState === 'Incompatible';

  return (
    <div className="flex h-full flex-col">
      {showMigration && <MigrationRequiredBanner />}
      {showIncompatible && <IncompatibleBanner />}
      <div className="relative min-h-0 flex-1">
        <DockviewReact
          theme={theme === 'dark' ? themeDark : themeLight}
          className="h-full w-full"
          components={components}
          tabComponents={tabComponents}
          onReady={onReady}
          // Floating groups can be dragged off-screen or behind the window and become unreachable,
          // and the View-menu toggles only act on docked edge groups — so a panel stranded in a
          // floating group can't be recovered. Keep panels docked; rearranging between docked groups
          // still works. View → Reset Layout to Default is the escape hatch if one slips away.
          disableFloatingGroups
        />
        {showIncompatible && (
          <div
            className="pointer-events-auto absolute inset-0 cursor-not-allowed bg-background/40"
            aria-hidden="true"
          />
        )}
      </div>
    </div>
  );
}
