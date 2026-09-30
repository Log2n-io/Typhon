import { useCanRetryDatabaseOpen } from '@/hooks/useRetryDatabaseOpen';
import RetryOpenButton from '@/shell/components/RetryOpenButton';

/**
 * The confirmation that used to be missing entirely: you registered a directory — here is what to do with it.
 *
 * Schema assemblies are resolved when a database is **opened**, so registering a directory afterwards changes a
 * setting and nothing the user can see. The blocked banner stays up, worded identically, and the correct action reads
 * as having done nothing. Reopening is what applies it.
 *
 * **Its own component, mounted only once a directory has been registered, and that is not cosmetic.** The reopen
 * button holds a TanStack mutation, so keeping it in `SchemaForm` would make every test of that form — five of them,
 * about inline validation and list editing — need a `QueryClientProvider` to assert that a relative path is rejected.
 * Same trap the Query Console's SPATIAL chip hit, and the blocked-state banners after it: a leaf editor should not
 * acquire a network dependency to render a message. Mounted conditionally, the dependency exists only in the state
 * that actually needs it.
 */
export default function SchemaReopenPrompt({ directory }: { directory: string }) {
  const canRetry = useCanRetryDatabaseOpen();

  // Nothing to reopen — no session, or one a reopen cannot improve. The registration still happened and still applies
  // to the next open; saying "reopen to apply" there would invent a problem.
  if (!canRetry) {
    return null;
  }

  return (
    <div className="space-y-1 rounded border border-border bg-muted/40 px-3 py-2">
      <p className="text-fs-base text-foreground">
        Registered <span className="font-mono text-fs-sm">{directory}</span>.
      </p>
      <p className="text-fs-sm text-muted-foreground">
        The open database resolved its schema before this directory was registered, so it has not been tried yet.
        Reopen it to resolve against the directories registered now.
      </p>
      <RetryOpenButton
        label="Reopen the database"
        variant="default"
        className="mt-1 h-7 text-fs-sm"
        title="Close and reopen this database, resolving its schema against the directories registered now"
      />
    </div>
  );
}
