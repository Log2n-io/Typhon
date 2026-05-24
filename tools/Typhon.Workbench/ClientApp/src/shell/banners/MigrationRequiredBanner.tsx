import { AlertTriangle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { openConnect } from '@/shell/commands/baseCommands';

export default function MigrationRequiredBanner() {
 const diagnostics = useSessionStore((s) => s.schemaDiagnostics);

 return (
 <div
 role="alert"
 className="flex items-start gap-3 border-b border-amber-600/40 bg-amber-500/10 px-4 py-2
 text-fs-lg text-amber-700 dark:text-amber-300"
 >
 <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
 <div className="min-w-0 flex-1">
 <p className="font-semibold">Schema migration required</p>
 <p className="mt-0.5 text-fs-sm opacity-90">
 Some component schemas have changed in a way that requires migration. Data is not loaded
 until you run the migration.
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
 <Button
 variant="outline"
 size="sm"
 className="h-6 shrink-0 text-fs-sm"
 onClick={() => openConnect('open')}
 title="Reopen with binaries matching this database's schema"
 >
 Open another file…
 </Button>
 </div>
 );
}
