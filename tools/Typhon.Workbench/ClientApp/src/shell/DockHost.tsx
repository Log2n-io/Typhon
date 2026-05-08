import { useRef } from 'react';
import { useThemeStore } from '@/stores/useThemeStore';
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
import CriticalPathPanel from '@/panels/CriticalPath/CriticalPathPanel';
import ProfilerPanel from '@/panels/profiler/ProfilerPanel';
import TopSpansPanel from '@/panels/profiler/TopSpansPanel';
import OptionsPanel from '@/panels/options/OptionsPanel';
import SourcePreviewPanel from '@/panels/profiler/SourcePreviewPanel';
import { registerDockApi } from './commands/openSchemaBrowser';
import { registerProfilerDockApi } from './commands/profilerCommands';
import MigrationRequiredBanner from './banners/MigrationRequiredBanner';
import IncompatibleBanner from './banners/IncompatibleBanner';

// Tab component without a close button — applied to structural panels that should not be closable.
const LockedTab: React.FC<IDockviewPanelHeaderProps> = (props) => (
  <DockviewDefaultTab {...(props as IDockviewDefaultTabProps)} hideClose />
);

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
  CriticalPath: CriticalPathPanel,
  Profiler: ProfilerPanel,
  TopSpans: TopSpansPanel,
  Options: OptionsPanel,
  SourcePreview: SourcePreviewPanel,
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
    const saved = getLayout(layoutKey);
    if (saved) {
      try {
        event.api.fromJSON(saved as Parameters<typeof event.api.fromJSON>[0]);
      } catch {
        // Saved layout invalid (version skew) — fall through to default
        buildDefaultLayout(event.api, kind);
      }
    } else {
      const template = getTemplate(kind);
      if (template) {
        try {
          event.api.fromJSON(template as Parameters<typeof event.api.fromJSON>[0]);
        } catch {
          buildDefaultLayout(event.api, kind);
        }
      } else {
        buildDefaultLayout(event.api, kind);
      }
    }

    // Trace-session safety net: profiler lives in the center area and must always be present.
    if (kind === 'trace' && !event.api.getPanel('profiler')) {
      event.api.addPanel({ id: 'profiler', component: 'Profiler', title: 'Profiler', tabComponent: 'locked' });
    }

    // Top spans safety net: panel lives in the bottom edge group.
    if ((kind === 'trace' || kind === 'attach') && !event.api.getPanel('top-spans')) {
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
