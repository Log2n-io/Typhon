import { useState } from 'react';
import { FolderPlus, FolderSearch, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import FileBrowser from '@/shell/components/FileBrowser';
import { useOptionsStore } from '@/stores/useOptionsStore';

/**
 * Schema-directory options (ADR-055 Phase 2). Lists the directories the Workbench searches — at priority
 * 2, above its own bundled binaries — when resolving a database's recorded schema assemblies on open.
 * Register a directory here (e.g. a custom build, or a schema recompiled from an older git commit) and any
 * database whose manifest names an assembly found there loads it from there, no per-DB copy required.
 *
 * Add/remove apply immediately (optimistic PATCH, rollback on error) — "register" reads as an action, not a
 * pending edit. Paths are validated as absolute client-side; the server further normalizes + de-duplicates.
 */

// Absolute-path heuristic covering Windows (`C:\…`, UNC `\\…`) and POSIX (`/…`). Mirrors the server's
// Path.IsPathRooted gate so the user gets immediate feedback rather than a silently-dropped entry.
function isAbsolutePath(p: string): boolean {
  return /^(?:[a-zA-Z]:[\\/]|\\\\|\/)/.test(p.trim());
}

export function SchemaForm(): React.JSX.Element {
  const directories = useOptionsStore((s) => s.options.schema.directories);
  const setSchema = useOptionsStore((s) => s.setSchema);

  const [pendingPath, setPendingPath] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [browseOpen, setBrowseOpen] = useState(false);
  const [browseDir, setBrowseDir] = useState<string | null>(null);

  async function addDirectory(dir: string): Promise<void> {
    const trimmed = dir.trim();
    if (!trimmed) {
      return;
    }
    if (!isAbsolutePath(trimmed)) {
      setError('Enter an absolute directory path.');
      return;
    }
    if (directories.some((d) => d.toLowerCase() === trimmed.toLowerCase())) {
      setError('That directory is already registered.');
      return;
    }
    setError(null);
    try {
      await setSchema({ directories: [...directories, trimmed] });
      setPendingPath('');
      setBrowseOpen(false);
    } catch (err) {
      setError((err as Error).message);
    }
  }

  async function removeDirectory(dir: string): Promise<void> {
    setError(null);
    try {
      await setSchema({ directories: directories.filter((d) => d !== dir) });
    } catch (err) {
      setError((err as Error).message);
    }
  }

  return (
    <section className="max-w-xl space-y-4">
      <header>
        <h2 className="text-fs-xl font-semibold text-foreground">Schema directories</h2>
        <p className="mt-1 text-fs-base text-muted-foreground">
          Directories searched for a database's schema assemblies when it's opened, before the Workbench's own
          bundled binaries. Register a custom or recompiled-from-git schema build here to load it without copying
          DLLs next to the database.
        </p>
      </header>

      {/* Registered list. */}
      {directories.length === 0 ? (
        <p className="rounded border border-dashed border-border px-3 py-4 text-center text-fs-sm text-muted-foreground">
          No directories registered. The Workbench resolves schemas from its own binaries (and, for legacy
          databases, a copy beside the file).
        </p>
      ) : (
        <ul className="divide-y divide-border rounded border border-border">
          {directories.map((dir) => (
            <li key={dir} className="flex items-center gap-2 px-2 py-1.5">
              <span className="min-w-0 flex-1 truncate font-mono text-fs-sm text-foreground" title={dir}>
                {dir}
              </span>
              <button
                type="button"
                onClick={() => void removeDirectory(dir)}
                className="shrink-0 rounded p-1 text-muted-foreground hover:bg-muted hover:text-destructive"
                title="Remove this directory"
                aria-label={`Remove ${dir}`}
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </li>
          ))}
        </ul>
      )}

      {/* Add: paste an absolute path, or browse to a folder. */}
      <div className="flex items-center gap-2">
        <input
          type="text"
          value={pendingPath}
          onChange={(e) => setPendingPath(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              void addDirectory(pendingPath);
            }
          }}
          placeholder="C:\Dev\github\Typhon\…\bin\Debug\net10.0"
          className="min-w-0 flex-1 rounded border border-border bg-background px-2 py-1 font-mono text-fs-base"
        />
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="h-8 shrink-0 gap-1 text-fs-base"
          onClick={() => void addDirectory(pendingPath)}
          disabled={pendingPath.trim().length === 0}
        >
          <FolderPlus className="h-3.5 w-3.5" />
          Add
        </Button>

        <Popover open={browseOpen} onOpenChange={setBrowseOpen}>
          <PopoverTrigger asChild>
            <Button type="button" variant="outline" size="sm" className="h-8 shrink-0 gap-1 text-fs-base">
              <FolderSearch className="h-3.5 w-3.5" />
              Browse…
            </Button>
          </PopoverTrigger>
          <PopoverContent align="end" className="w-[32rem] p-2">
            <div className="flex h-80 flex-col gap-2">
              <p className="text-fs-sm text-muted-foreground">
                Navigate into the folder containing the schema assembly, then register it. Assemblies
                (<code>.dll</code>) are listed for confirmation; you register the <em>folder</em>, not a file.
              </p>
              <div className="min-h-0 flex-1">
                <FileBrowser extensionFilter={['.dll']} onPathChange={setBrowseDir} />
              </div>
              <div className="flex items-center justify-between gap-2">
                <span className="min-w-0 flex-1 truncate font-mono text-fs-xs text-muted-foreground" title={browseDir ?? ''}>
                  {browseDir ?? '—'}
                </span>
                <Button
                  type="button"
                  size="sm"
                  className="h-7 shrink-0 text-fs-sm"
                  onClick={() => browseDir && void addDirectory(browseDir)}
                  disabled={!browseDir}
                >
                  Register this folder
                </Button>
              </div>
            </div>
          </PopoverContent>
        </Popover>
      </div>

      {error && <p className="text-fs-base text-destructive">{error}</p>}
    </section>
  );
}
