import { useEffect, useState } from 'react';
import { Clock, Database, Plug, Sparkles } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { customFetch } from '@/api/client';
import ConnectDialog, { type ConnectTab } from './dialogs/ConnectDialog';
import { toggleViewDevFixture } from './commands/openSchemaBrowser';
import { openIntegrity } from './commands/openIntegrity';

export default function WelcomeScreen() {
 const [dialogOpen, setDialogOpen] = useState(false);
 const [initialTab, setInitialTab] = useState<ConnectTab>('open');
 // Dev fixture button visibility — same capability probe ConnectDialog uses. /api/fixtures/capability
 // only exists in DEBUG builds; a 404 here means Release → hide the button.
 const [devFixtureAvailable, setDevFixtureAvailable] = useState(false);

 useEffect(() => {
 let cancelled = false;
 (async () => {
 try {
 await customFetch('/api/fixtures/capability', { method: 'GET' });
 if (!cancelled) setDevFixtureAvailable(true);
 } catch {
 /* Release build — leave the button hidden */
 }
 })();
 return () => { cancelled = true; };
 }, []);

 const openDialog = (tab: ConnectTab) => {
 setInitialTab(tab);
 setDialogOpen(true);
 };

 return (
 <div className="flex h-full flex-col items-center justify-center gap-8 bg-background">
 <div className="text-center">
 <h1 className="mb-1 text-xl font-semibold text-foreground">Typhon Workbench</h1>
 <p className="text-fs-lg text-muted-foreground">
 Open a database, trace file, or attach to a running engine
 </p>
 </div>

 <div className="flex gap-3">
 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-fs-lg"
 onClick={() => openDialog('open')}
 >
 <Database className="h-5 w-5" />
 <span>Open .typhon File</span>
 </Button>

 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-fs-lg"
 onClick={() => openDialog('attach')}
 >
 <Plug className="h-5 w-5" />
 <span>Attach to Engine</span>
 </Button>

 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-fs-lg"
 onClick={() => openDialog('recent')}
 >
 <Clock className="h-5 w-5" />
 <span>Recent Files</span>
 </Button>

 {devFixtureAvailable && (
 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 border-amber-500/40 px-6 py-4 text-fs-lg"
 onClick={() => toggleViewDevFixture()}
 title="Create a populated sample database to explore Typhon immediately"
 >
 <Sparkles className="h-5 w-5 text-amber-400" />
 <span>Sample database</span>
 </Button>
 )}
 </div>

 {/* #729 — a recovery path, not a primary one, so it reads as a sentence rather than competing with the
     "get started" buttons above. Phrased around the symptom because that is what someone arriving here
     with a broken database actually knows: the file won't open. They don't yet know the word "integrity". */}
 <p className="text-fs-base text-muted-foreground">
 Database won&apos;t open, or you suspect damage?{' '}
 <button
 type="button"
 onClick={() => openIntegrity()}
 data-testid="welcome-check-integrity"
 className="rounded text-foreground underline underline-offset-2 hover:text-primary"
 >
 Check its integrity
 </button>
 </p>

 <ConnectDialog open={dialogOpen} initialTab={initialTab} onOpenChange={setDialogOpen} />
 </div>
 );
}
