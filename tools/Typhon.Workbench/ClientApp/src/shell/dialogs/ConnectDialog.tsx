import { useEffect, useState } from 'react';
import {
 Dialog,
 DialogContent,
 DialogDescription,
 DialogHeader,
 DialogTitle,
} from '@/components/ui/dialog';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { usePostApiSessionsAttach, usePostApiSessionsFile, usePostApiSessionsTrace } from '@/api/generated/sessions/sessions';
import { useRecentFilesStore, type RecentFileState } from '@/stores/useRecentFilesStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { logError, logInfo, logWarn } from '@/stores/useLogStore';
import { extractDetail } from './connectErrors';
import RecentFilesTab from './tabs/RecentFilesTab';
import OpenFileTab from './tabs/OpenFileTab';
import OpenTraceTab from './tabs/OpenTraceTab';
import AttachTab from './tabs/AttachTab';
import CachedDataTab from './tabs/CachedDataTab';
import { toggleViewProfiler } from '@/shell/commands/profilerCommands';

// Dev Fixture used to be a tab here; it now lives in `panels/DevFixture/DevFixturePanel.tsx`, reachable from the
// View menu and palette ("Open Dev Fixture"). The `'devfixture'` literal stays in the union so any caller still
// passing `initialTab='devfixture'` doesn't break compile (it silently falls through to the default `recent` tab).
export type ConnectTab = 'recent' | 'open' | 'trace' | 'attach' | 'cached' | 'devfixture';

interface Props {
 open: boolean;
 initialTab: ConnectTab;
 onOpenChange: (open: boolean) => void;
}

export default function ConnectDialog({ open, initialTab, onOpenChange }: Props) {
 const [tab, setTab] = useState<ConnectTab>(initialTab);
 const setSession = useSessionStore((s) => s.setSession);
 const recordRecent = useRecentFilesStore((s) => s.record);
 const postFile = usePostApiSessionsFile();
 const postTrace = usePostApiSessionsTrace();
 const postAttach = usePostApiSessionsAttach();

 // Snap to the requested tab every time the dialog opens — the prop may have changed while the
 // dialog was closed (different Welcome button / MenuBar item).
 useEffect(() => {
 if (open) setTab(initialTab);
 }, [open, initialTab]);

 const handleOpen = async (filePath: string, schemaDllPaths: string[]) => {
 logInfo(`Opening database: ${filePath}`, {
 filePath,
 explicitSchemaDlls: schemaDllPaths,
 });
 try {
 const response = await postFile.mutateAsync({
 data: {
 filePath,
 schemaDllPaths: schemaDllPaths.length > 0 ? schemaDllPaths : undefined,
 },
 });
 const dto = response.data;
 setSession(dto);
 recordRecent({
 filePath: dto.filePath ?? filePath,
 schemaDllPaths: (dto.schemaDllPaths as string[] | null | undefined) ?? schemaDllPaths,
 lastOpenedAt: new Date().toISOString(),
 lastState: (dto.state as RecentFileState) ?? 'Ready',
 kind: 'db',
 });

 const resolvedDlls = (dto.schemaDllPaths as string[] | null | undefined) ?? [];
 const diagnostics = (dto.schemaDiagnostics ?? []) as Array<{
 componentName?: string | null;
 kind?: string | null;
 detail?: string | null;
 }>;
 const sessionState = dto.state ?? 'Ready';
 const logLevel = sessionState === 'Incompatible' ? logError : sessionState === 'Ready' ? logInfo : logWarn;
 logLevel(
 `Session opened — state: ${sessionState} (${dto.loadedComponentTypes ?? 0} component types loaded)`,
 {
 sessionId: dto.sessionId,
 filePath: dto.filePath ?? filePath,
 schemaStatus: dto.schemaStatus,
 schemaDllPaths: resolvedDlls,
 diagnostics: diagnostics.length > 0 ? diagnostics : undefined,
 },
 );
 onOpenChange(false);
 } catch (err) {
 logError(`Failed to open database: ${filePath}`, {
 filePath,
 error: extractDetail(err) || String(err),
 });
 throw err;
 }
 };

 const handleOpenTrace = async (filePath: string) => {
 logInfo(`Opening trace: ${filePath}`, { filePath });
 try {
 const response = await postTrace.mutateAsync({ data: { filePath } });
 const dto = response.data;
 setSession(dto);
 recordRecent({
 filePath: dto.filePath ?? filePath,
 schemaDllPaths: [],
 lastOpenedAt: new Date().toISOString(),
 lastState: 'Ready',
 kind: 'trace',
 });
 logInfo(`Trace session opened`, { sessionId: dto.sessionId, filePath: dto.filePath ?? filePath });
 onOpenChange(false);
 toggleViewProfiler();
 } catch (err) {
 logError(`Failed to open trace: ${filePath}`, {
 filePath,
 error: extractDetail(err) || String(err),
 });
 throw err;
 }
 };

 const handleOpenAttach = async (endpoint: string) => {
 logInfo(`Attaching to engine: ${endpoint}`, { endpoint });
 try {
 const response = await postAttach.mutateAsync({ data: { endpointAddress: endpoint } });
 const dto = response.data;
 setSession(dto);
 logInfo(`Attach session opened`, { sessionId: dto.sessionId, endpoint });
 onOpenChange(false);
 toggleViewProfiler();
 } catch (err) {
 logError(`Failed to attach to: ${endpoint}`, {
 endpoint,
 error: extractDetail(err) || String(err),
 });
 // Swallowed — error pill below surfaces postAttach.isError; keep the dialog open for retry.
 }
 };

 return (
 <Dialog open={open} onOpenChange={onOpenChange}>
 <DialogContent className="flex h-[640px] max-w-4xl flex-col gap-3">
 <DialogHeader>
 <DialogTitle className="">Connect</DialogTitle>
 <DialogDescription className="text-fs-lg">
 Open a database, attach to a live engine, or replay a trace.
 </DialogDescription>
 </DialogHeader>

 <Tabs
 value={tab}
 onValueChange={(v) => setTab(v as ConnectTab)}
 className="flex min-h-0 flex-1 flex-col"
 >
 <TabsList className="shrink-0">
 <TabsTrigger value="recent">Recent</TabsTrigger>
 <TabsTrigger value="open">Open File</TabsTrigger>
 <TabsTrigger value="trace">Open Trace</TabsTrigger>
 <TabsTrigger value="attach">Attach</TabsTrigger>
 <TabsTrigger value="cached">Cached Data</TabsTrigger>
 {/* Dev Fixture moved to its own standalone panel (View → Dev Fixture / palette: "Open Dev Fixture").
     The capability probe + `devFixtureAvailable` state stay here only as a hint for the View-menu wiring
     to render; the tab is no longer inside the Connect dialog. */}
 </TabsList>
 <TabsContent value="recent" className="min-h-0 flex-1">
 <RecentFilesTab onOpen={handleOpen} onOpenTrace={handleOpenTrace} />
 </TabsContent>
 <TabsContent value="open" className="min-h-0 flex-1">
 <OpenFileTab onOpen={handleOpen} isOpening={postFile.isPending} />
 </TabsContent>
 <TabsContent value="trace" className="min-h-0 flex-1">
 <OpenTraceTab onOpen={handleOpenTrace} isOpening={postTrace.isPending} />
 </TabsContent>
 <TabsContent value="attach" className="min-h-0 flex-1">
 <AttachTab onAttach={handleOpenAttach} isAttaching={postAttach.isPending} />
 </TabsContent>
 <TabsContent value="cached" className="min-h-0 flex-1">
 <CachedDataTab />
 </TabsContent>
 </Tabs>

 {postFile.isError && (
 <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
 Failed to open session. {extractDetail(postFile.error)}
 </p>
 )}
 {postTrace.isError && (
 <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
 Failed to open trace. {extractDetail(postTrace.error)}
 </p>
 )}
 {postAttach.isError && (
 <p className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive">
 Failed to attach. {extractDetail(postAttach.error)}
 </p>
 )}
 </DialogContent>
 </Dialog>
 );
}
