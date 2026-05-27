import { lazy, Suspense, useEffect, useRef } from 'react';
import { useThemeStore } from '@/stores/useThemeStore';
import { useLogStore, selectUnseenLevel, selectUnseenCount, type LogLevel } from '@/stores/useLogStore';
import { DockviewDefaultTab, DockviewReact, themeDark, themeLight, type DockviewApi, type DockviewReadyEvent, type IDockviewDefaultTabProps, type IDockviewPanelHeaderProps, type IDockviewPanelProps } from 'dockview-react';
import { useDockLayoutStore } from '@/stores/useDockLayoutStore';
import { useSessionStore } from '@/stores/useSessionStore';
// Eagerly imported: the shell + default-layout panels that are always mounted at session open (so lazy-loading
// them would only add a Suspense flash, not save the cold bundle anything). Everything else is lazy (below).
import DetailPanel from '@/panels/DetailPanel';
import LogsPanel from '@/panels/LogsPanel';
import ResourceTreePanel from '@/panels/ResourceTreePanel';
import SchemaExplorerPanel from '@/panels/SchemaExplorer/SchemaExplorerPanel';
import ProfilerPanel from '@/panels/profiler/ProfilerPanel';
import TopSpansPanel from '@/panels/profiler/TopSpansPanel';
import EngineLiveHealthPanel from '@/panels/EngineLiveHealth/EngineLiveHealthPanel';
import SystemsQueriesNavigatorPanel from '@/panels/SystemsQueriesNavigator/SystemsQueriesNavigatorPanel';
import { registerDockApi, registerResetLayout, focusPanelBody } from './commands/openSchemaBrowser';
import { registerProfilerDockApi } from './commands/profilerCommands';
import { isViewActive } from './viewRegistry';
import MigrationRequiredBanner from './banners/MigrationRequiredBanner';
import IncompatibleBanner from './banners/IncompatibleBanner';
import ReconnectBanner from './banners/ReconnectBanner';

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
            `px-1 text-fs-xs font-medium tabular-nums ${LOG_BADGE_CLASS[unseenLevel ?? 'info']}`
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

// A panel body shown while a lazily-loaded panel's chunk is in flight (usually a few ms on first open).
const PanelLoading: React.FC = () => (
  <div className="flex h-full w-full items-center justify-center text-fs-sm text-muted-foreground">Loading…</div>
);

// Wraps a dynamically-imported panel in a Suspense boundary so dockview can mount it directly. Heavy / on-demand
// panels are code-split this way — most importantly the React-Flow + dagre graph panels (System DAG, Data Flow,
// Query Analyzer) — so those libraries stay out of the cold bundle until a view that needs them is first opened.
function lazyPanel(loader: () => Promise<{ default: React.ComponentType<IDockviewPanelProps> }>): React.FC<IDockviewPanelProps> {
  const Lazy = lazy(loader);
  const Wrapped: React.FC<IDockviewPanelProps> = (props) => (
    <Suspense fallback={<PanelLoading />}>
      <Lazy {...props} />
    </Suspense>
  );
  Wrapped.displayName = 'LazyPanel';
  return Wrapped;
}

// The full component registry. Shell + default-layout panels are imported eagerly (above); every on-demand panel is
// lazy so its chunk — and any heavy library it pulls — is fetched only when its view is first opened. Every id stays
// listed here so deactivated views remain compilable (Stage 0 gates, never deletes); `activeComponents` below is what
// dockview actually mounts — gated zone-D ids are filtered out so a stale saved layout referencing one fails fromJSON
// cleanly and hits the rebuildDefault() recovery (see shell-and-dockview.md §5).
const components: Record<string, React.FC<IDockviewPanelProps>> = {
  ResourceTree: ResourceTreePanel,
  Detail: DetailPanel,
  Logs: LogsPanel,
  SchemaExplorer: SchemaExplorerPanel,
  Profiler: ProfilerPanel,
  TopSpans: TopSpansPanel,
  // Stage 4 Phase 1 — the Engine Live Health panel is the attach default surface (per its design); eager
  // (not lazy) so it mounts immediately on attach without a Suspense flash.
  EngineLiveHealth: EngineLiveHealthPanel,
  // Shell navigator (zone C, Trace/Attach) — not a zone-D deep view, so it is never gated.
  SystemsQueriesNavigator: SystemsQueriesNavigatorPanel,
  // Lazy (on-demand) — code-split out of the cold bundle.
  ArchetypeInspector: lazyPanel(() => import('@/panels/ArchetypeInspector/ArchetypeInspectorPanel')),
  ComponentInspector: lazyPanel(() => import('@/panels/ComponentInspector/ComponentInspectorPanel')),
  SystemDag: lazyPanel(() => import('@/panels/SystemDag/SystemDagPanel')),
  DataFlow: lazyPanel(() => import('@/panels/DataFlow/DataFlowPanel')),
  CriticalPath: lazyPanel(() => import('@/panels/CriticalPath/CriticalPathPanel')),
  CallTree: lazyPanel(() => import('@/panels/profiler/CallTree')),
  Options: lazyPanel(() => import('@/panels/options/OptionsPanel')),
  SourcePreview: lazyPanel(() => import('@/panels/profiler/SourcePreviewPanel')),
  QueryAnalyzer: lazyPanel(() => import('@/panels/QueryAnalyzer/QueryAnalyzerPanel')),
  PaletteDebug: lazyPanel(() => import('@/panels/PaletteDebug')),
  DbMap: lazyPanel(() => import('@/panels/DbMap/DbMapPanel')),
  StorageHealth: lazyPanel(() => import('@/panels/StorageHealth/StorageHealthPanel')),
  DataBrowserEntities: lazyPanel(() => import('@/panels/DataBrowser/EntityListPanel')),
  // Dev Fixture: lazy because it's only opened on demand (View → Dev Fixture or palette). Replaces the former
  // tab inside ConnectDialog so the feature has a persistent home — generate, inspect, generate again without
  // re-opening the Connect dialog.
  DevFixture: lazyPanel(() => import('@/panels/DevFixture/DevFixturePanel')),
};

// Only the active (shell + ungated) components are handed to dockview. Gated zone-D ids drop out here.
const activeComponents: Record<string, React.FC<IDockviewPanelProps>> = Object.fromEntries(
  Object.entries(components).filter(([id]) => isViewActive(id)),
);

// Stage 0 default layouts are the shell frame only: edge groups (navigator / inspector / drawer) around a
// neutral, empty center. Every center/zone-D panel is added only when its view is active (`isViewActive`),
// so the deep panels stay out today and re-appear automatically as Stages 2-4 flip them back on.
function buildDefaultLayout(api: DockviewReadyEvent['api'], kind: 'none' | 'open' | 'attach' | 'trace') {
  if (kind === 'trace' || kind === 'attach') {
    // The Profiler timeline is the center workspace — add it FIRST (no position) so it establishes the main
    // grid; the edge groups then wrap around it. A no-position addPanel joins the *active* group, so adding it
    // AFTER an edge panel docks it into that edge instead — the bug that put the timeline in the left edge once
    // Stage 3 Phase 1 un-gated the view. This mirrors the open-mode Schema Explorer ordering below.
    if (isViewActive('Profiler')) {
      api.addPanel({ id: 'profiler', component: 'Profiler', title: 'Profiler', tabComponent: 'locked' });
    }

    api.addEdgeGroup('left', { id: EDGE_LEFT_ID, initialSize: 260, minimumSize: 150 });
    api.addEdgeGroup('right', { id: EDGE_RIGHT_ID, initialSize: 320, minimumSize: 200 });
    api.addEdgeGroup('bottom', { id: EDGE_BOTTOM_ID, initialSize: 200, minimumSize: 100 });

    // Zone C navigator for a profiler session — the trace/attach analogue of the open-mode Resource Tree.
    api.addPanel({
      id: 'systems-queries-nav',
      component: 'SystemsQueriesNavigator',
      title: 'Systems & Queries',
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

    if (isViewActive('TopSpans')) {
      api.addPanel({
        id: 'top-spans',
        component: 'TopSpans',
        title: 'Top spans',
        tabComponent: 'locked',
        position: { referenceGroup: EDGE_BOTTOM_ID },
      });
    }

    // Engine Live Health — attach default surface (#377 Stage 4 Phase 1). Mounted as a tab in the right edge
    // group alongside Detail; the panel itself shows a cold message in trace sessions, but the design only
    // promotes it onto the default layout for attach (where the live SSE stream is meaningful).
    if (kind === 'attach' && isViewActive('EngineLiveHealth')) {
      api.addPanel({
        id: 'engine-live-health',
        component: 'EngineLiveHealth',
        title: 'Engine Health',
        tabComponent: 'locked',
        position: { referenceGroup: EDGE_RIGHT_ID },
      });
    }

    // (Trace-mode schema browsing returns via the Schema Explorer when it is wired for trace sessions — the
    // old SchemaBrowser/ArchetypeBrowser panels were removed in the GAP-02 consolidation, Stage 2.)
    return;
  }

  // Open / none layout — navigator + inspector + drawer around the Schema Explorer workspace.
  // The Schema Explorer is the Open-session default center (J1 lands here) — always-on shell-structural,
  // like the navigators (not a gateable zone-D deep view), so Open never dead-ends on an empty center.
  // It is added FIRST (no position) so it establishes the main grid; the edge groups then wrap around it.
  // (A no-position addPanel joins the active group, so adding it after an edge panel would dock it there.)
  api.addPanel({
    id: 'schema-explorer',
    component: 'SchemaExplorer',
    title: 'Schema',
    tabComponent: 'locked',
  });

  api.addEdgeGroup('left', { id: EDGE_LEFT_ID, initialSize: 260, minimumSize: 150 });
  api.addEdgeGroup('right', { id: EDGE_RIGHT_ID, initialSize: 320, minimumSize: 200 });
  api.addEdgeGroup('bottom', { id: EDGE_BOTTOM_ID, initialSize: 200, minimumSize: 100 });

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

  // Clear the module-level dockview registrations on unmount. Without this, after closing a session
  // the stale `DockviewApi` reference lingers in `openSchemaBrowser.ts` (and the profiler-commands
  // sibling) — `toggleViewDevFixture()` then sees `registeredApi !== null` on the next Welcome screen
  // and routes to the dock-toggle path instead of the modal fallback, silently no-opping (the dock
  // doesn't exist anymore). Same root cause as: "the Welcome-screen Dev Fixture button doesn't open
  // anything after I closed a session". `registerResetLayout(null)` symmetric-nulls the reset hook
  // so the View → Reset Layout menu item doesn't fire against a disposed api either.
  useEffect(() => {
    return () => {
      registerDockApi(null);
      registerProfilerDockApi(null);
      registerResetLayout(null);
      apiRef.current = null;
    };
  }, []);
  function onReady(event: DockviewReadyEvent) {
    apiRef.current = event.api;
    registerDockApi(event.api);
    registerProfilerDockApi(event.api);

    // Focus-follows-navigation (PC-8): when the active panel changes (F6, a bus handoff, a click),
    // move DOM focus into its body so a keyboard user lands where they navigated — never orphaned on
    // <body>. `panel.focus()` alone only activates the group/tab; focusPanelBody also moves DOM focus
    // into the content container (shell-and-dockview §2), which is what makes the focus visible.
    event.api.onDidActivePanelChange((panel) => { if (panel) focusPanelBody(panel); });
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

    // Trace-session safety net: profiler lives in the center area and must always be present — but only while
    // the Profiler view is active (gated off in Stage 0, restored in Stage 3).
    if (kind === 'trace' && isViewActive('Profiler') && !event.api.getPanel('profiler')) {
      event.api.addPanel({ id: 'profiler', component: 'Profiler', title: 'Profiler', tabComponent: 'locked' });
    }

    // Bottom-edge-group panel safety net. A stale saved layout can restore without the Logs and/or
    // Top-spans panels (and without the bottom edge group itself) — View → Logs would then have
    // nothing to surface. Re-create whatever is missing; the edge group is added only when a panel
    // actually needs it, so a layout that kept the panels elsewhere isn't given a spurious empty group.
    const needLogs = !event.api.getPanel('logs');
    const needTopSpans =
      (kind === 'trace' || kind === 'attach') && isViewActive('TopSpans') && !event.api.getPanel('top-spans');
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
      {/* Stage 4 P4 (#377) — reconnect / shutdown banner; self-gates on sessionKind === 'attach' &&
          connectionStatus === 'disconnected', so the conditional here is just "let it decide." */}
      <ReconnectBanner />
      <div className="relative min-h-0 flex-1">
        <DockviewReact
          theme={theme === 'dark' ? themeDark : themeLight}
          className="h-full w-full"
          components={activeComponents}
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
