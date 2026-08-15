import { XCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useSessionStore } from '@/stores/useSessionStore';
import { openConnect } from '@/shell/commands/baseCommands';
import { openOptionsToSchema } from '@/shell/commands/openSchemaBrowser';

/**
 * The blocked-open banner. Two very different failures reach it and they must not read the same:
 *
 *  • `missing_assembly` — the schema DLL was not FOUND. Nothing is incompatible; the binaries are very likely the
 *    right ones, sitting in the app's `bin/` where the Workbench does not look. Recoverable by naming the directory.
 *  • anything else (breaking change, downgrade) — the DLL was found and genuinely does not match the database.
 *
 * Collapsing them cost a user twenty minutes: a database created minutes earlier, by the very binaries in the same
 * tree, was reported "Schema incompatible" and told to "reopen with binaries that match its recorded schema" — advice
 * for a problem they did not have, while the real one (a search path) went unmentioned.
 */
export default function IncompatibleBanner() {
  const diagnostics = useSessionStore((s) => s.schemaDiagnostics);

  // Only-missing is the recoverable case. A mixed set (something missing AND something genuinely incompatible) is
  // reported as incompatible: the stronger, less hopeful message is the honest one when both are true.
  const missing = (diagnostics ?? []).filter((d) => d.kind === 'missing_assembly');
  const onlyMissing = missing.length > 0 && missing.length === (diagnostics?.length ?? 0);
  const missingNames = [...new Set(missing.map((d) => d.componentName))];

  const title = onlyMissing ? 'Schema assembly not found' : 'Schema incompatible';
  const detail = onlyMissing
    ? `This database records that its schema comes from ${missingNames.length === 1 ? '' : 'assemblies '}` +
      `${missingNames.join(', ')}, which could not be found. If you built it yourself the binaries are probably ` +
      `under your project's bin folder — register that directory to continue.`
    : 'This database cannot be opened with the loaded schema DLLs. Reopen it with binaries that match its recorded schema to continue.';

  return (
    <div
      role="alert"
      className="flex items-start gap-3 border-b border-destructive/50 bg-destructive/10 px-4 py-2
                 text-fs-lg text-destructive"
    >
      <XCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-semibold">{title}</p>
        <p className="mt-0.5 text-fs-sm opacity-90">{detail}</p>
        {diagnostics && diagnostics.length > 0 && (
          <ul className="mt-1 list-disc pl-4 text-fs-sm opacity-80">
            {diagnostics.slice(0, 3).map((d, i) => (
              <li key={i}>
                <span className="font-semibold">{d.componentName}</span>
                {d.kind === 'missing_assembly' ? ' — not found' : ` — ${d.kind}`}
              </li>
            ))}
          </ul>
        )}
      </div>
      <div className="flex shrink-0 items-center gap-1.5">
        {/* The fix comes first. Offering "open something else" as the leading action tells a user to give up on a
            problem that is one directory registration away. */}
        <Button
          variant="outline"
          size="sm"
          className="h-6 text-fs-sm"
          onClick={openOptionsToSchema}
          title={
            onlyMissing
              ? `Register the directory containing ${missingNames.join(', ')}`
              : 'Register a directory holding a schema build compatible with this database'
          }
        >
          {onlyMissing ? 'Locate schema assembly…' : 'Manage schema directories…'}
        </Button>
        <Button
          variant="outline"
          size="sm"
          className="h-6 text-fs-sm"
          onClick={() => openConnect('known')}
          title="Leave this database and open a different one"
        >
          Open a different database…
        </Button>
      </div>
    </div>
  );
}
