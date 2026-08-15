import { AlertTriangle, Database, Loader2, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { formatRelativeAge } from '@/lib/formatters';
import { useKnownDatabases } from '@/hooks/useKnownDatabases';
import {
  describeRegistryState,
  missingCount,
  orderForDisplay,
  parentDirectoryOf,
  type KnownDatabase,
} from '@/libs/databases/knownDatabases';

interface Props {
  onOpen: (filePath: string, schemaDllPaths: string[]) => void;
  /** The file path currently being opened, or null when idle. The matching row shows a spinner + disables itself. */
  openingPath?: string | null;
  /** Held false until the tab is shown, so opening the dialog on another tab costs no request. */
  active?: boolean;
}

/**
 * Databases this **machine** has opened (#622, design D-7) — as opposed to the Recent tab, which is what *you* opened
 * in the Workbench.
 *
 * The two lists answer different questions and neither subsumes the other: Recent carries your schema-DLL choices,
 * resource pins and remembered profiler viewport and is Workbench-scoped; this one is written by the engine, so a
 * database created by a game server three directories away shows up here having never been opened in the Workbench at
 * all. That is the entire point of the feature.
 */
export default function KnownDatabasesTab({ onOpen, openingPath, active = true }: Props) {
  const { list, isLoading, isError, error, forget, pruneMissing } = useKnownDatabases(active);
  const notice = describeRegistryState(list);
  const entries = list ? orderForDisplay(list.entries) : [];
  const missing = list ? missingCount(list.entries) : 0;
  const mutationError = forget.error ?? pruneMissing.error;

  if (isLoading && !list) {
    return (
      <div className="flex h-full items-center justify-center text-fs-lg text-muted-foreground">
        <Loader2 className="mr-2 h-4 w-4 animate-spin" aria-hidden="true" /> Reading the database registry…
      </div>
    );
  }

  // Three states that would otherwise render identically as an empty panel, and mean completely different things:
  // the request failed, the registry is switched off, and nothing has been recorded yet. Collapsing them is the
  // failure mode this feature is most exposed to — see the disabled banner below for the same argument.
  if (isError && !list) {
    return (
      <div
        data-testid="registry-error"
        className="flex h-full items-center justify-center px-6 text-center text-fs-lg text-destructive"
      >
        Could not read the database registry. {error instanceof Error ? error.message : ''}
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col gap-1">
      {/* A switched-off registry must never render as an empty list: the user would conclude the feature is useless
          rather than that it is disabled, and stop looking — the exact failure D-7 argues against. The reason names
          the responsible switch so the state is undoable. */}
      {notice.kind === 'disabled' && (
        <div
          data-testid="registry-disabled"
          className="flex shrink-0 items-start gap-2 rounded border border-amber-500/40 bg-amber-500/10 px-2 py-1 text-fs-sm"
        >
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600 dark:text-amber-400" />
          <span>
            <b>New databases are not being recorded.</b> {notice.reason}
          </span>
        </div>
      )}

      {missing > 0 && (
        <div className="flex shrink-0 items-center justify-between gap-2 px-1 text-fs-xs text-muted-foreground">
          <span>
            {missing} {missing === 1 ? 'entry points' : 'entries point'} at a database that is no longer there.
          </span>
          <Button
            variant="outline"
            size="sm"
            className="h-6"
            data-testid="prune-missing"
            disabled={pruneMissing.isPending}
            onClick={() => pruneMissing.mutate()}
          >
            Prune missing ({missing})
          </Button>
        </div>
      )}

      {entries.length === 0 ? (
        <div className="flex h-full items-center justify-center px-6 text-center text-fs-lg text-muted-foreground">
          {notice.kind === 'disabled' ? (
            <span>Nothing was recorded before the registry was switched off.</span>
          ) : (
            <span>
              No databases recorded yet. Any Typhon application that opens a database registers it here — including this
              one, so opening a database from the <b className="px-1">Database</b> tab will populate the list.
            </span>
          )}
        </div>
      ) : (
        <div className="flex min-h-0 flex-1 flex-col gap-1 overflow-auto p-1">
          {entries.map((e) => (
            <KnownDatabaseRow
              key={e.bundlePath}
              entry={e}
              onOpen={onOpen}
              onForget={() => forget.mutate(e.bundlePath)}
              opening={openingPath === e.bundlePath}
              // While any database is opening the dialog is mid-transition; a second open would race the first.
              disabled={openingPath != null}
            />
          ))}
        </div>
      )}

      {/* A forget or prune that failed must say so. Silently leaving the row on screen would read as "the click did
          nothing", which is how the same class of defect hid in the profile-attach flow (§1.4). */}
      {mutationError && (
        <p
          data-testid="registry-mutation-error"
          className="shrink-0 rounded border border-destructive/50 bg-destructive/10 px-2 py-1 text-fs-sm text-destructive"
        >
          {mutationError instanceof Error ? mutationError.message : 'The registry could not be updated.'}
        </p>
      )}

      {list && (
        <div className="shrink-0 truncate px-1 pb-1 text-fs-xs text-muted-foreground" title={list.registryDirectory}>
          {list.registryDirectory}
        </div>
      )}
    </div>
  );
}

function KnownDatabaseRow({
  entry,
  onOpen,
  onForget,
  opening,
  disabled,
}: {
  entry: KnownDatabase;
  onOpen: Props['onOpen'];
  onForget: () => void;
  opening: boolean;
  disabled: boolean;
}) {
  const age = formatRelativeAge(entry.lastOpenedUtc);
  const detail = [age, entry.lastOpenedBy].filter(Boolean).join(' · ');

  return (
    <div
      data-testid={`known-db-${entry.name}`}
      className={`group flex items-center gap-2 rounded border border-transparent px-2 py-1
        ${entry.exists ? '' : 'opacity-60'}
        ${disabled ? '' : 'hover:border-border hover:bg-muted'}`}
    >
      {opening ? (
        <Loader2 className="h-3.5 w-3.5 shrink-0 animate-spin text-foreground" aria-hidden="true" />
      ) : (
        <Database className={`h-3.5 w-3.5 shrink-0 ${entry.exists ? 'text-muted-foreground' : 'text-amber-500'}`} />
      )}
      <button
        // The registry records paths, not the schema assemblies a session was opened with — that is Recent's job. An
        // empty list here means "resolve the schema the usual way", which is what an Open File click does too.
        onClick={() => onOpen(entry.bundlePath, [])}
        disabled={disabled || !entry.exists}
        className="min-w-0 flex-1 text-left disabled:cursor-default"
      >
        <div className="flex items-baseline gap-2">
          <span className="truncate text-fs-lg font-semibold">{entry.name}</span>
          {opening ? (
            <span className="shrink-0 text-fs-xs text-muted-foreground">Opening…</span>
          ) : (
            detail && <span className="shrink-0 text-fs-xs text-muted-foreground">({detail})</span>
          )}
          {!entry.exists && (
            <span
              data-testid={`known-db-missing-${entry.name}`}
              className="shrink-0 rounded bg-amber-500/15 px-1 text-fs-xs uppercase text-amber-700 dark:text-amber-300"
            >
              Missing
            </span>
          )}
        </div>
        <div className="truncate text-fs-xs text-muted-foreground">{parentDirectoryOf(entry.bundlePath)}</div>
      </button>
      <Button
        variant="ghost"
        size="sm"
        className="h-6 w-6 shrink-0 p-0 opacity-0 group-hover:opacity-100"
        onClick={onForget}
        aria-label={`Forget ${entry.name}`}
        title="Forget this database (the database itself is not deleted)"
      >
        <Trash2 className="h-3 w-3" />
      </Button>
    </div>
  );
}
