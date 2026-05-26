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
  toggleViewLogs,
  toggleViewOptions,
  toggleViewResourceTree,
  toggleViewSourcePreview,
  toggleViewSystemDag,
  saveLayoutAsDefault,
  resetLayout,
} from './commands/openSchemaBrowser';
import { toggleViewCallTree, toggleViewCriticalPath, toggleViewProfiler, toggleViewTopSpans, toggleViewQueryAnalyzer, toggleViewEngineLiveHealth, registerOpenSaveReplay } from './commands/profilerCommands';
import { registerOpenConnect } from './commands/baseCommands';
import { isViewActive, ANY_ZONE_D_VIEW_ACTIVE } from './viewRegistry';
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
 const isProfilerSession = kind === 'attach' || kind === 'trace';

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
 {/* Deep/workspace (zone-D) views — gated off in Stage 0 (reversible per view via the view registry).
     Stage 1 replaces this flat list with a session-kind-partitioned View menu (IA §5.1). */}
 {isViewActive('DataBrowserEntities') && <MenubarItem onClick={() => toggleViewDataBrowser()}>Data Browser</MenubarItem>}
 {isViewActive('DbMap') && (
 <MenubarItem
 disabled={kind !== 'open'}
 onClick={toggleViewDbMap}
 title={kind === 'open' ? undefined : 'Available only for an open .typhon file'}
 >
 Database File Map
 </MenubarItem>
 )}
 {/* (Stage 2 / GAP-02: the Component Layout/Archetypes/Indexes/Relationships items were removed —
     those facts are now tabs of the Component Inspector, reached by selecting a component.) */}
 {isViewActive('Profiler') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewProfiler}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Profiler
 </MenubarItem>
 )}
 {isViewActive('TopSpans') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewTopSpans}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Top Spans
 </MenubarItem>
 )}
 {isViewActive('SystemDag') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewSystemDag}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 System DAG
 </MenubarItem>
 )}
 {isViewActive('CriticalPath') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewCriticalPath}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Critical Path
 </MenubarItem>
 )}
 {isViewActive('CallTree') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewCallTree}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Call Tree
 </MenubarItem>
 )}
 {isViewActive('SourcePreview') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewSourcePreview}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Source Preview
 </MenubarItem>
 )}
 {isViewActive('DataFlow') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewDataFlow}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Data Flow
 </MenubarItem>
 )}
 {isViewActive('QueryAnalyzer') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewQueryAnalyzer}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Query Analyzer
 </MenubarItem>
 )}
 {isViewActive('EngineLiveHealth') && (
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewEngineLiveHealth}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Engine Health
 </MenubarItem>
 )}
 {ANY_ZONE_D_VIEW_ACTIVE && <MenubarSeparator />}
 <MenubarItem
   disabled={isProfilerSession}
   onClick={toggleViewResourceTree}
   title={isProfilerSession ? 'Not available in profiler sessions' : undefined}
 >
   Resource Tree
 </MenubarItem>
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
