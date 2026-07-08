import { useEffect, useRef, useState } from 'react';
import {
 Menubar,
 MenubarContent,
 MenubarItem,
 MenubarMenu,
 MenubarRadioGroup,
 MenubarRadioItem,
 MenubarSeparator,
 MenubarSub,
 MenubarSubContent,
 MenubarSubTrigger,
 MenubarTrigger,
} from '@/components/ui/menubar';
import { useDeleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { usePaletteStore } from '@/stores/usePaletteStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { useDensityStore, type Density } from '@/stores/useDensityStore';
import CommandPalette from './CommandPalette';
import ConnectDialog, { type ConnectTab } from './dialogs/ConnectDialog';
import SaveReplayDialog from './dialogs/SaveReplayDialog';
import KeyboardHelpDialog from './help/KeyboardHelpDialog';
import NavButtons from './NavButtons';
import PaletteTrigger from './PaletteTrigger';
import {
  toggleViewDataBrowser,
  toggleViewDbMap,
  toggleViewDataFlow,
  toggleViewDetail,
  toggleViewDevFixture,
  toggleViewLogs,
  toggleViewOptions,
  toggleViewResourceTree,
  toggleViewSchemaExplorer,
  toggleViewSourcePreview,
  toggleViewStorageHealth,
  toggleViewSystemDag,
  toggleViewSystemsQueriesNav,
  saveLayoutAsDefault,
  resetLayout,
} from './commands/openSchemaBrowser';
import { toggleViewCallTree, toggleViewCriticalPath, toggleViewProfiler, toggleViewTopSpans, toggleViewQueryAnalyzer, toggleViewEngineLiveHealth, registerOpenSaveReplay } from './commands/profilerCommands';
import { toggleViewQueryConsole } from './commands/openQueryConsole';
import { registerOpenConnect } from './commands/baseCommands';
import { ANY_ZONE_D_VIEW_ACTIVE, isViewVisible } from './viewRegistry';
import { logError, logInfo } from '@/stores/useLogStore';

export default function MenuBar() {
 const paletteOpen = usePaletteStore((s) => s.open);
 const togglePalette = usePaletteStore((s) => s.toggle);
 const closePalette = usePaletteStore((s) => s.setOpen);
 const triggerRef = useRef<HTMLButtonElement>(null);

 const kind = useSessionStore((s) => s.kind);
 const sessionId = useSessionStore((s) => s.sessionId);
 const clearSession = useSessionStore((s) => s.clearSession);
 const toggleTheme = useThemeStore((s) => s.toggle);
 const toggleLegends = useUiPrefsStore((s) => s.toggleLegends);
 const density = useDensityStore((s) => s.mode);
 const setDensity = useDensityStore((s) => s.setMode);

 const [dialogOpen, setDialogOpen] = useState(false);
 const [initialTab, setInitialTab] = useState<ConnectTab>('open');
 const [saveReplayOpen, setSaveReplayOpen] = useState(false);
 const [kbdHelpOpen, setKbdHelpOpen] = useState(false);

 // Register the dialog openers with their module-level slots so palette commands can trigger them. Mirrors registerProfilerDockApi.
 useEffect(() => {
   registerOpenSaveReplay(() => setSaveReplayOpen(true));
   registerOpenConnect((tab) => { setInitialTab(tab); setDialogOpen(true); });
   return () => {
     registerOpenSaveReplay(null);
     registerOpenConnect(null);
   };
 }, []);

 const openConnect = (tab: ConnectTab) => {
 setInitialTab(tab);
 setDialogOpen(true);
 };

 const deleteSession = useDeleteApiSessionsId();
 const handleCloseSession = async () => {
 if (!sessionId) return;
 const closingId = sessionId;
 try {
 await deleteSession.mutateAsync({ id: closingId });
 clearSession();
 logInfo('Session closed', { sessionId: closingId });
 } catch (err) {
 logError('Failed to close session', { sessionId: closingId, error: String(err) });
 throw err;
 }
 };

 return (
 <header className="relative flex h-10 shrink-0 items-center gap-2 border-b border-border bg-card px-2">
 <Menubar className="border-0 bg-transparent p-0 shadow-none">
 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-fs-lg">File</MenubarTrigger>
 <MenubarContent>
 <MenubarItem onClick={() => openConnect('open')}>Open .typhon File…</MenubarItem>
 <MenubarItem onClick={() => openConnect('trace')}>Open .typhon-trace…</MenubarItem>
 <MenubarItem onClick={() => openConnect('attach')}>Attach to Engine…</MenubarItem>
 <MenubarSeparator />
 <MenubarItem
   disabled={kind !== 'attach'}
   onClick={() => setSaveReplayOpen(true)}
   title={kind === 'attach' ? undefined : 'Available only during a live attach session'}
 >
   Save Session as .typhon-replay…
 </MenubarItem>
 <MenubarSeparator />
 <MenubarItem onClick={() => openConnect('recent')}>Recent Files…</MenubarItem>
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-fs-lg">View</MenubarTrigger>
 <MenubarContent>
 {/* IA §5.1 — the View menu shows ONLY the panels the current session kind can actually open; a view that
     can't run in this mode is absent, never a greyed-out dead entry (the old disabled+tooltip — friction F8).
     Each item is gated on its session kind AND its view-registry feature flag (isViewActive). 'none' (no
     session) therefore shows only the always-on chrome below. Open-mode views first, then profiler-mode. */}
 {/* Schema Explorer is the open-session default navigator; its menu entry is the only recovery path to reopen
     it after closing the tab (besides "Reset Layout", which nukes other customisations) — so it stays visible
     in open mode. Shell-structural at the registry level (viewRegistry.ts), session-kind gated for usefulness. */}
 {isViewVisible('SchemaExplorer', kind) && (
 <MenubarItem onClick={toggleViewSchemaExplorer}>Schema</MenubarItem>
 )}
 {isViewVisible('DataBrowserEntities', kind) && (
 <MenubarItem onClick={() => toggleViewDataBrowser()}>Data Browser</MenubarItem>
 )}
 {isViewVisible('DbMap', kind) && (
 <MenubarItem onClick={toggleViewDbMap}>Database File Map</MenubarItem>
 )}
 {/* Storage Health — aggregate dashboard sibling of DbMap (both surface storage facts of the loaded file). */}
 {isViewVisible('StorageHealth', kind) && (
 <MenubarItem onClick={toggleViewStorageHealth}>Storage Health</MenubarItem>
 )}
 {/* (Stage 2 / GAP-02: the Component Layout/Archetypes/Indexes/Relationships items were removed —
     those facts are now tabs of the Component Inspector, reached by selecting a component.) */}
 {isViewVisible('Profiler', kind) && (
 <MenubarItem onClick={toggleViewProfiler}>Profiler</MenubarItem>
 )}
 {isViewVisible('TopSpans', kind) && (
 <MenubarItem onClick={toggleViewTopSpans}>Top Spans</MenubarItem>
 )}
 {isViewVisible('SystemDag', kind) && (
 <MenubarItem onClick={toggleViewSystemDag}>System DAG</MenubarItem>
 )}
 {isViewVisible('CriticalPath', kind) && (
 <MenubarItem onClick={toggleViewCriticalPath}>Critical Path</MenubarItem>
 )}
 {isViewVisible('CallTree', kind) && (
 <MenubarItem onClick={toggleViewCallTree}>Call Tree</MenubarItem>
 )}
 {isViewVisible('SourcePreview', kind) && (
 <MenubarItem onClick={toggleViewSourcePreview}>Source Preview</MenubarItem>
 )}
 {isViewVisible('DataFlow', kind) && (
 <MenubarItem onClick={toggleViewDataFlow}>Data Flow</MenubarItem>
 )}
 {isViewVisible('QueryAnalyzer', kind) && (
 <MenubarItem onClick={toggleViewQueryAnalyzer}>Query Analyzer</MenubarItem>
 )}
 {/* #386 Phase 1: Query Console — open-session only. Hidden outside an open .typhon file (IA §5.1). */}
 {isViewVisible('QueryConsole', kind) && (
 <MenubarItem onClick={toggleViewQueryConsole}>Query Console</MenubarItem>
 )}
 {isViewVisible('EngineLiveHealth', kind) && (
 <MenubarItem onClick={toggleViewEngineLiveHealth}>Engine Health</MenubarItem>
 )}
 {/* Systems & Queries Navigator — the trace/attach-mode default left-edge navigator (the profiler-mode
     counterpart of Resource Tree). Shown only in a profiler session — the in-mode recovery path to reopen
     the navigator after closing its panel. */}
 {isViewVisible('SystemsQueriesNav', kind) && (
 <MenubarItem onClick={toggleViewSystemsQueriesNav}>Systems &amp; Queries</MenubarItem>
 )}
 {/* Create sample database. Session-independent (scope 'any') — the user generates a sample DB regardless of
     any open session; opening the result establishes a new session. Shipped in Release (#433); the panel still
     shows a "not available" cold state if `/api/fixtures/capability` ever fails to report availability. */}
 {isViewVisible('DevFixture', kind) && (
 <MenubarItem onClick={toggleViewDevFixture}>Create sample database…</MenubarItem>
 )}
 {ANY_ZONE_D_VIEW_ACTIVE && <MenubarSeparator />}
 {/* Resource Tree — the open-session navigator; hidden outside an open .typhon file (no resources to show). */}
 {isViewVisible('ResourceTree', kind) && (
 <MenubarItem onClick={toggleViewResourceTree}>Resource Tree</MenubarItem>
 )}
 <MenubarItem onClick={toggleViewDetail}>Detail</MenubarItem>
 <MenubarItem onClick={toggleViewLogs}>Logs</MenubarItem>
 <MenubarItem onClick={toggleViewOptions}>Options</MenubarItem>
 <MenubarSeparator />
 <MenubarItem onClick={toggleTheme}>Toggle Dark / Light Mode</MenubarItem>
 <MenubarSeparator />
 <MenubarItem disabled={kind === 'none'} onClick={saveLayoutAsDefault}>Save Layout as Default</MenubarItem>
 <MenubarItem onClick={resetLayout}>Reset Layout to Default</MenubarItem>
 <MenubarSeparator />
 {/* DS-1 display density — drives row height, spacing AND the --fs-* font ramp (useDensityStore). */}
 <MenubarSub>
 <MenubarSubTrigger>Density</MenubarSubTrigger>
 <MenubarSubContent>
 <MenubarRadioGroup value={density} onValueChange={(v) => setDensity(v as Density)}>
 <MenubarRadioItem value="compact">Compact</MenubarRadioItem>
 <MenubarRadioItem value="normal">Normal</MenubarRadioItem>
 <MenubarRadioItem value="comfortable">Comfortable</MenubarRadioItem>
 </MenubarRadioGroup>
 </MenubarSubContent>
 </MenubarSub>
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-fs-lg">Session</MenubarTrigger>
 <MenubarContent>
 <MenubarItem disabled={kind === 'none'} onClick={handleCloseSession}>
 Close Session
 </MenubarItem>
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-fs-lg">Help</MenubarTrigger>
 <MenubarContent>
 <MenubarSub>
 <MenubarSubTrigger>Quick Doc</MenubarSubTrigger>
 <MenubarSubContent>
 <MenubarItem onClick={() => setKbdHelpOpen(true)}>Keyboard navigation</MenubarItem>
 </MenubarSubContent>
 </MenubarSub>
 <MenubarSeparator />
 {/* Toggles the app-wide inline "?" help glyphs / legends (useUiPrefsStore.legendsVisible). */}
 <MenubarItem onClick={() => toggleLegends()}>Toggle legend</MenubarItem>
 </MenubarContent>
 </MenubarMenu>
 </Menubar>

 {/* Centered group: NavButtons immediately left of PaletteTrigger. The transform creates a
 stacking context — raise it above dockview's 9999 so the palette dropdown is not buried. */}
 <div className="absolute left-1/2 z-[10000] flex -translate-x-1/2 items-center gap-2">
 <NavButtons />
 <div className="relative">
 <PaletteTrigger triggerRef={triggerRef} onClick={togglePalette} />
 <CommandPalette
 open={paletteOpen}
 onClose={() => closePalette(false)}
 anchorRef={triggerRef}
 />
 </div>
 </div>

 <ConnectDialog open={dialogOpen} initialTab={initialTab} onOpenChange={setDialogOpen} />
 <SaveReplayDialog open={saveReplayOpen} onOpenChange={setSaveReplayOpen} />
 {kbdHelpOpen && <KeyboardHelpDialog onClose={() => setKbdHelpOpen(false)} />}
 </header>
 );
}
