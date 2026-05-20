import { Activity, Database, Trash2 } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useGetApiFsStat } from '@/api/generated/files/files';
import { formatFileSize, formatRelativeAge } from '@/lib/formatters';
import {
  useRecentFilesStore,
  getRecentFileKind,
  type RecentFile,
  type RecentFileState,
} from '@/stores/useRecentFilesStore';

interface Props {
  onOpen: (filePath: string, schemaDllPaths: string[]) => void;
  onOpenTrace: (filePath: string) => void;
}

const stateStyles: Record<RecentFileState, string> = {
  Ready: 'bg-green-500/15 text-green-600 dark:text-green-400',
  MigrationRequired: 'bg-amber-500/15 text-amber-700 dark:text-amber-300',
  Incompatible: 'bg-destructive/15 text-destructive',
};

export default function RecentFilesTab({ onOpen, onOpenTrace }: Props) {
  const entries = useRecentFilesStore((s) => s.entries);

  if (entries.length === 0) {
    return (
      <div className="flex h-full items-center justify-center text-density-sm text-muted-foreground">
        No recent files. Open a database from the <b className="px-1">Open File</b> tab,
        or a trace from the <b className="px-1">Open Trace</b> tab.
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col gap-1 overflow-auto p-1">
      {entries.map((e) => (
        <RecentFileRow key={e.filePath} entry={e} onOpen={onOpen} onOpenTrace={onOpenTrace} />
      ))}
    </div>
  );
}

function RecentFileRow({ entry, onOpen, onOpenTrace }: { entry: RecentFile } & Props) {
  const remove = useRecentFilesStore((s) => s.remove);
  const name = entry.filePath.split(/[\\/]/).pop() ?? entry.filePath;
  const kind = getRecentFileKind(entry);
  const isTrace = kind === 'trace';
  const Icon = isTrace ? Activity : Database;
  const iconClass = isTrace ? 'text-violet-500' : 'text-muted-foreground';
  const kindLabel = isTrace ? 'TRACE' : 'DB';
  const kindBadgeClass = isTrace
    ? 'bg-violet-500/15 text-violet-600 dark:text-violet-400'
    : 'bg-muted text-muted-foreground';

  // Live file metadata — recent entries persist only the path, so size + modified time are
  // re-statted on render. A moved/deleted file simply 404s and the parenthetical is omitted;
  // `silenceErrors` opts this query out of the global Logs-panel error stream so a stale recent
  // doesn't spam one "Query failed" entry per render.
  const stat = useGetApiFsStat(
    { path: entry.filePath },
    { query: { staleTime: 30_000, meta: { silenceErrors: true } } },
  );
  const meta = stat.data?.data;
  const size = formatFileSize(meta?.size);
  const age = formatRelativeAge(meta?.lastWriteUtc);
  const detail = size && age ? `${size} · ${age}` : size || age;

  const handleActivate = () => {
    if (isTrace) {
      onOpenTrace(entry.filePath);
    } else {
      onOpen(entry.filePath, entry.schemaDllPaths);
    }
  };

  return (
    <div
      className="group flex items-center gap-2 rounded border border-transparent px-2 py-1
        hover:border-border hover:bg-muted"
    >
      <Icon className={`h-3.5 w-3.5 shrink-0 ${iconClass}`} />
      <button onClick={handleActivate} className="min-w-0 flex-1 text-left">
        <div className="flex items-baseline gap-2">
          <span className="truncate text-density-sm font-semibold">{name}</span>
          {detail && (
            <span className="shrink-0 text-[10px] text-muted-foreground">({detail})</span>
          )}
          <span className={`shrink-0 rounded px-1 text-[10px] uppercase ${kindBadgeClass}`}>
            {kindLabel}
          </span>
          {!isTrace && (
            <span className={`shrink-0 rounded px-1 text-[10px] uppercase ${stateStyles[entry.lastState]}`}>
              {entry.lastState}
            </span>
          )}
        </div>
        <div className="truncate text-[10px] text-muted-foreground">
          {entry.filePath}
        </div>
      </button>
      <Button
        variant="ghost"
        size="sm"
        className="h-6 w-6 shrink-0 p-0 opacity-0 group-hover:opacity-100"
        onClick={() => remove(entry.filePath)}
        aria-label="Remove from recents"
        title="Remove from recents"
      >
        <Trash2 className="h-3 w-3" />
      </Button>
    </div>
  );
}
