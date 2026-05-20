import { useEffect, useRef, useState } from 'react';
import {
 Menubar,
 MenubarContent,
 MenubarItem,
 MenubarMenu,
 MenubarSeparator,
 MenubarTrigger,
} from '@/components/ui/menubar';
import { useDeleteApiSessionsId } from '@/api/generated/sessions/sessions';
import { usePaletteStore } from '@/stores/usePaletteStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import CommandPalette from './CommandPalette';
import ConnectDialog, { type ConnectTab } from './dialogs/ConnectDialog';
import SaveReplayDialog from './dialogs/SaveReplayDialog';
import NavButtons from './NavButtons';
import PaletteTrigger from './PaletteTrigger';
import {
  toggleViewAccessMatrix,
  toggleViewArchetypeBrowser,
  toggleViewComponentBrowser,
  toggleViewDataFlow,
  toggleViewDetail,
  toggleViewLogs,
  toggleViewOptions,
  toggleViewResourceTree,
  toggleViewSchemaArchetypes,
  toggleViewSchemaIndexes,
  toggleViewSchemaLayout,
  toggleViewSchemaRelationships,
  toggleViewSourcePreview,
  toggleViewSystemDag,
  saveLayoutAsDefault,
  resetLayout,
} from './commands/openSchemaBrowser';
import { toggleViewCallTree, toggleViewCriticalPath, toggleViewProfiler, toggleViewTopSpans, registerOpenSaveReplay } from './commands/profilerCommands';
import { registerOpenConnect } from './commands/baseCommands';
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
 const hasComponentSelection = useSchemaInspectorStore((s) => s.selectedComponentType != null);
 const isProfilerSession = kind === 'attach' || kind === 'trace';

 const [dialogOpen, setDialogOpen] = useState(false);
 const [initialTab, setInitialTab] = useState<ConnectTab>('open');
 const [saveReplayOpen, setSaveReplayOpen] = useState(false);

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
 <MenubarTrigger className="h-7 px-2 text-density-sm">File</MenubarTrigger>
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
 <MenubarTrigger className="h-7 px-2 text-density-sm">Edit</MenubarTrigger>
 <MenubarContent>
 <MenubarItem disabled>Find…</MenubarItem>
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-density-sm">View</MenubarTrigger>
 <MenubarContent>
 <MenubarItem onClick={toggleViewComponentBrowser}>Component Browser</MenubarItem>
 <MenubarItem onClick={toggleViewArchetypeBrowser}>Archetype Browser</MenubarItem>
 <MenubarSeparator />
 <MenubarItem
 disabled={!hasComponentSelection}
 onClick={toggleViewSchemaLayout}
 title={hasComponentSelection ? undefined : 'Select a component first'}
 >
 Component Layout
 </MenubarItem>
 <MenubarItem
 disabled={!hasComponentSelection}
 onClick={toggleViewSchemaArchetypes}
 title={hasComponentSelection ? undefined : 'Select a component first'}
 >
 Component Archetypes
 </MenubarItem>
 <MenubarItem
 disabled={!hasComponentSelection}
 onClick={toggleViewSchemaIndexes}
 title={hasComponentSelection ? undefined : 'Select a component first'}
 >
 Component Indexes
 </MenubarItem>
 <MenubarItem
 disabled={!hasComponentSelection}
 onClick={toggleViewSchemaRelationships}
 title={hasComponentSelection ? undefined : 'Select a component first'}
 >
 Component Relationships
 </MenubarItem>
 <MenubarSeparator />
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewProfiler}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Profiler
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewTopSpans}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Top Spans
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewSystemDag}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 System DAG
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewCriticalPath}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Critical Path
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewCallTree}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Call Tree
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewSourcePreview}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Source Preview
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewDataFlow}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Data Flow
 </MenubarItem>
 <MenubarItem
 disabled={!isProfilerSession}
 onClick={toggleViewAccessMatrix}
 title={isProfilerSession ? undefined : 'Open a profiler trace or attach a session first'}
 >
 Access Matrix
 </MenubarItem>
 <MenubarSeparator />
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
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-density-sm">Session</MenubarTrigger>
 <MenubarContent>
 <MenubarItem disabled={kind === 'none'} onClick={handleCloseSession}>
 Close Session
 </MenubarItem>
 </MenubarContent>
 </MenubarMenu>

 <MenubarMenu>
 <MenubarTrigger className="h-7 px-2 text-density-sm">Help</MenubarTrigger>
 <MenubarContent>
 <MenubarItem disabled>About Typhon Workbench</MenubarItem>
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
 </header>
 );
}
