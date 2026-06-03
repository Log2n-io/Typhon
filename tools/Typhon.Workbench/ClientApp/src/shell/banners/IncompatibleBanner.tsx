import { XCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { openConnect } from '@/shell/commands/baseCommands';
import { openOptionsToSchema } from '@/shell/commands/openSchemaBrowser';

export default function IncompatibleBanner() {
 const diagnostics = useSessionStore((s) => s.schemaDiagnostics);

 return (
 <div
 role="alert"
 className="flex items-start gap-3 border-b border-destructive/50 bg-destructive/10 px-4 py-2
 text-fs-lg text-destructive"
 >
 <XCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
 <div className="min-w-0 flex-1">
 <p className="font-semibold">Schema incompatible</p>
 <p className="mt-0.5 text-fs-sm opacity-90">
 This database cannot be opened with the loaded schema DLLs. Reopen it with binaries that match
 its recorded schema to continue.
 </p>
 {diagnostics && diagnostics.length > 0 && (
 <ul className="mt-1 list-disc pl-4 text-fs-sm opacity-80">
 {diagnostics.slice(0, 3).map((d, i) => (
 <li key={i}>
 <span className="font-semibold">{d.componentName}</span> — {d.kind}
 </li>
 ))}
 </ul>
 )}
 </div>
 <div className="flex shrink-0 items-center gap-1.5">
 <Button
 variant="outline"
 size="sm"
 className="h-6 text-fs-sm"
 onClick={openOptionsToSchema}
 title="Register a directory holding a schema build compatible with this database"
 >
 Manage schema directories…
 </Button>
 <Button
 variant="outline"
 size="sm"
 className="h-6 text-fs-sm"
 onClick={() => openConnect('open')}
 title="Reopen with binaries matching this database's schema"
 >
 Open another file…
 </Button>
 </div>
 </div>
 );
}
