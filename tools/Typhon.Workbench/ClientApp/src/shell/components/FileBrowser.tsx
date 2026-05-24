import { useEffect, useMemo, useRef, useState } from 'react';
import { Activity, ChevronDown, ChevronUp, Database, FileText, Folder } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { useGetApiFsHome, useGetApiFsList } from '@/api/generated/files/files';
import type { FileEntryDto } from '@/api/generated/model';
import { formatDateTime, formatFileSize } from '@/lib/formatters';
import { getRecentLocations, useRecentFilesStore, type RecentFileKind } from '@/stores/useRecentFilesStore';

export interface FileBrowserProps {
  /**
   * Extension filter — include only files whose name ends with any of these (case-insensitive).
   * Directories are always shown. Example: ['.typhon'] or ['.schema.dll'].
   */
  extensionFilter?: string[];
  multiSelect?: boolean;
  /** Initial directory; falls back to the most-recent matching location, then the server's $HOME. */
  initialPath?: string;
  /**
   * Restricts the recent-locations history dropdown (and the default starting directory) to directories
   * a file of this kind was loaded from. Omit to include every recent location.
   */
  recentKind?: RecentFileKind;
  /** Called whenever the selection changes. Paths are full absolute paths. */
  onSelectionChange?: (paths: string[]) => void;
  /** Double-click / Enter on a file — used by single-select flows ("Open" on Enter). */
  onActivate?: (path: string) => void;
  /** Called whenever the browser navigates into a different directory (initial load + each ascend/descend). */
  onPathChange?: (path: string) => void;
}

export default function FileBrowser({
  extensionFilter,
  multiSelect = false,
  initialPath,
  recentKind,
  onSelectionChange,
  onActivate,
  onPathChange,
}: FileBrowserProps) {
  // Always enabled — cheap, cached forever, and needed as the fallback after a stale directory is pruned.
  const homeQuery = useGetApiFsHome({ query: { staleTime: Infinity } });

  // Recent-locations history: parent directories of recent files, derived from the persisted store.
  const entries = useRecentFilesStore((s) => s.entries);
  const recentLocations = useMemo(() => getRecentLocations(entries, recentKind), [entries, recentKind]);

  // Seed synchronously from the most-recent matching location so the first paint already shows it.
  const [path, setPath] = useState<string | null>(
    () => initialPath ?? getRecentLocations(useRecentFilesStore.getState().entries, recentKind)[0]?.dir ?? null,
  );
  const [historyOpen, setHistoryOpen] = useState(false);

  // Re-seed whenever the path is cleared (initial mount with no recents, or after a prune): prefer the
  // next recent location, then $HOME.
  useEffect(() => {
    if (path) {
      return;
    }
    const next = recentLocations[0]?.dir ?? homeQuery.data?.data.fullPath ?? null;
    if (next) {
      setPath(next);
    }
  }, [path, recentLocations, homeQuery.data]);

  const listing = useGetApiFsList(
    { path: path ?? '' },
    { query: { enabled: !!path } },
  );

  // A recent location that no longer exists answers 404 — prune the recent files under it and re-seed.
  // Scoped to recent locations on purpose: a manually-typed bad path keeps its value so the user can fix it.
  useEffect(() => {
    if (!path || !listing.isError) {
      return;
    }
    const status = (listing.error as { status?: number } | null)?.status;
    const isRecentLocation = recentLocations.some((l) => l.dir.toLowerCase() === path.toLowerCase());
    if (status === 404 && isRecentLocation) {
      useRecentFilesStore.getState().removeUnderDirectory(path);
      setPath(null);
    }
  }, [listing.isError, listing.error, path, recentLocations]);

  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [activeIndex, setActiveIndex] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const entriesView = useMemo(() => {
    const raw = listing.data?.data?.entries ?? [];
    if (!extensionFilter || extensionFilter.length === 0) return raw;
    return raw.filter((e: FileEntryDto) => {
      if (e.kind === 'dir') return true;
      const name = (e.name ?? '').toLowerCase();
      return extensionFilter.some((ext) => name.endsWith(ext.toLowerCase()));
    });
  }, [listing.data, extensionFilter]);

  useEffect(() => {
    onSelectionChange?.(Array.from(selected));
  }, [selected, onSelectionChange]);

  // Reset selection + highlight when directory changes; notify the parent so save flows can track the current dir.
  useEffect(() => {
    setSelected(new Set());
    setActiveIndex(0);
    if (path) onPathChange?.(path);
  }, [path, onPathChange]);

  const descend = (entry: FileEntryDto) => {
    if (entry.kind === 'dir') {
      setPath(entry.fullPath ?? null);
    } else if (entry.fullPath) {
      if (multiSelect) {
        setSelected((prev) => {
          const next = new Set(prev);
          if (next.has(entry.fullPath as string)) next.delete(entry.fullPath as string);
          else next.add(entry.fullPath as string);
          return next;
        });
      } else {
        setSelected(new Set([entry.fullPath as string]));
        onActivate?.(entry.fullPath as string);
      }
    }
  };

  const ascend = () => {
    const parent = listing.data?.data?.parent;
    if (parent) setPath(parent);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (entriesView.length === 0) return;
    switch (e.key) {
      case 'ArrowDown':
        e.preventDefault();
        setActiveIndex((i) => Math.min(i + 1, entriesView.length - 1));
        break;
      case 'ArrowUp':
        e.preventDefault();
        setActiveIndex((i) => Math.max(i - 1, 0));
        break;
      case 'Home':
        e.preventDefault();
        setActiveIndex(0);
        break;
      case 'End':
        e.preventDefault();
        setActiveIndex(entriesView.length - 1);
        break;
      case 'Enter':
        e.preventDefault();
        descend(entriesView[activeIndex]);
        break;
      case 'Backspace':
        e.preventDefault();
        ascend();
        break;
    }
  };

  return (
    <div className="flex h-full flex-col overflow-hidden rounded border border-border bg-background">
      {/* Breadcrumb / path input */}
      <div className="flex shrink-0 items-center gap-1 border-b border-border px-2 py-1">
        <button
          className="rounded p-1 text-muted-foreground hover:bg-muted disabled:opacity-40"
          onClick={ascend}
          disabled={!listing.data?.data?.parent}
          title="Up"
        >
          <ChevronUp className="h-3.5 w-3.5" />
        </button>
        <Input
          value={path ?? ''}
          onChange={(e) => setPath(e.target.value)}
          className="h-6 border-0 bg-transparent text-fs-sm shadow-none focus-visible:ring-0 focus-visible:ring-offset-0"
          placeholder="Path…"
        />
        <Popover open={historyOpen} onOpenChange={setHistoryOpen}>
          <PopoverTrigger asChild>
            <button
              className="flex shrink-0 items-center gap-0.5 rounded px-1.5 py-1 text-fs-sm
                text-muted-foreground hover:bg-muted"
              title="Recent locations"
            >
              Recent
              <ChevronDown className="h-3.5 w-3.5" />
            </button>
          </PopoverTrigger>
          <PopoverContent align="end" className="w-80 p-1">
            <div className="px-2 py-1 text-fs-xs uppercase tracking-wide text-muted-foreground">
              Recent locations
            </div>
            {recentLocations.length === 0 && (
              <div className="px-2 py-2 text-fs-sm text-muted-foreground">
                No recent locations yet — open a file to add one.
              </div>
            )}
            {recentLocations.map((loc) => {
              const Icon = loc.kind === 'trace' ? Activity : Database;
              const iconClass = loc.kind === 'trace' ? 'text-violet-500' : 'text-muted-foreground';
              return (
                <button
                  key={loc.dir}
                  onClick={() => {
                    setPath(loc.dir);
                    setHistoryOpen(false);
                  }}
                  className="flex w-full items-center gap-1.5 rounded px-2 py-1 text-left text-fs-sm hover:bg-muted"
                  title={loc.dir}
                >
                  <Icon className={`h-3 w-3 shrink-0 ${iconClass}`} />
                  <span className="min-w-0 flex-1 truncate">{loc.dir}</span>
                </button>
              );
            })}
          </PopoverContent>
        </Popover>
      </div>

      {/* List */}
      <div
        ref={listRef}
        tabIndex={0}
        onKeyDown={onKeyDown}
        className="min-h-0 flex-1 overflow-auto focus:outline-none"
        role="listbox"
        aria-label="Files"
      >
        {listing.isLoading && (
          <p className="px-3 py-2 text-fs-sm text-muted-foreground">Loading…</p>
        )}
        {listing.isError && (
          <p className="px-3 py-2 text-fs-sm text-destructive">Failed to read directory</p>
        )}
        {!listing.isLoading && entriesView.length === 0 && listing.data && (
          <p className="px-3 py-2 text-fs-sm text-muted-foreground">Empty</p>
        )}
        {entriesView.map((e: FileEntryDto, i: number) => {
          const isActive = i === activeIndex;
          const isSelected = e.fullPath && selected.has(e.fullPath as string);
          const Icon = e.kind === 'dir' ? Folder : FileText;
          return (
            <div
              key={e.fullPath ?? `${i}-${e.name}`}
              role="option"
              aria-selected={isSelected || undefined}
              onClick={() => {
                setActiveIndex(i);
                descend(e);
              }}
              onDoubleClick={() => descend(e)}
              className={`flex cursor-pointer items-center gap-1.5 px-2 py-0.5 text-fs-sm leading-relaxed
                ${isActive ? 'bg-muted' : ''}
                ${isSelected ? 'border-l-2 border-accent bg-muted text-foreground' : 'text-foreground hover:bg-muted/60'}`}
            >
              <Icon className="h-3 w-3 shrink-0 text-muted-foreground" />
              <span className="min-w-0 flex-1 truncate">{e.name}</span>
              {e.isSchemaDll && (
                <span className="shrink-0 rounded bg-accent/20 px-1 text-fs-xs uppercase text-accent">
                  schema
                </span>
              )}
              {/* Size + last-modified columns. Directory entries carry null for both — rendered blank. */}
              <span className="w-20 shrink-0 text-right tabular-nums text-fs-xs text-muted-foreground">
                {formatFileSize(e.size)}
              </span>
              <span className="w-32 shrink-0 text-right tabular-nums text-fs-xs text-muted-foreground">
                {formatDateTime(e.lastWriteUtc)}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}
