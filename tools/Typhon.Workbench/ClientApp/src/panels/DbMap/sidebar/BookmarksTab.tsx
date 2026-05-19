import { useState } from 'react';
import { BookmarkPlus, Check, Pencil, Trash2 } from 'lucide-react';
import type { DbMapBookmark } from '@/libs/dbmap/dbMapBookmarks';

// The side-rail Bookmarks tab (Module 15, §6.4 / §13 A4 AC3). Bookmarks pin a viewport per database and
// persist across sessions (the store owns persistence). Clicking a row flies the camera there; the row can be
// renamed inline or deleted. The panel supplies the list and the handlers.

interface BookmarksTabProps {
  bookmarks: DbMapBookmark[];
  /** Whether a database is open — gates the "bookmark this view" action. */
  hasMap: boolean;
  onAddCurrent: () => void;
  onFlyTo: (bookmark: DbMapBookmark) => void;
  onRemove: (id: string) => void;
  onRename: (id: string, label: string) => void;
}

export function BookmarksTab(props: BookmarksTabProps) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState('');

  const commitRename = (id: string) => {
    const label = draft.trim();
    if (label.length > 0) {
      props.onRename(id, label);
    }
    setEditingId(null);
  };

  return (
    <div className="flex flex-col gap-2 p-2">
      <button
        type="button"
        onClick={props.onAddCurrent}
        disabled={!props.hasMap}
        className="flex items-center gap-1.5 self-start rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground disabled:opacity-40"
        title="Bookmark the current viewport (b)"
        data-testid="dbmap-bookmark-add"
      >
        <BookmarkPlus className="h-3 w-3" /> Bookmark this view
      </button>

      {props.bookmarks.length === 0 ? (
        <p className="text-[11px] text-muted-foreground">
          No bookmarks yet. Press <kbd className="rounded bg-muted px-1">b</kbd> or the button above to pin the
          current viewport — bookmarks are saved per database and survive a restart.
        </p>
      ) : (
        <ul className="flex flex-col gap-0.5" data-testid="dbmap-bookmark-list">
          {props.bookmarks.map((b) => (
            <li key={b.id} className="group flex items-center gap-1">
              {editingId === b.id ? (
                <>
                  <input
                    autoFocus
                    value={draft}
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') commitRename(b.id);
                      else if (e.key === 'Escape') setEditingId(null);
                    }}
                    onBlur={() => commitRename(b.id)}
                    className="min-w-0 flex-1 rounded border border-border bg-background px-1 py-0.5 text-[11px] text-foreground"
                  />
                  <button
                    type="button"
                    onClick={() => commitRename(b.id)}
                    className="rounded p-0.5 text-muted-foreground hover:text-foreground"
                    title="Save name"
                  >
                    <Check className="h-3 w-3" />
                  </button>
                </>
              ) : (
                <>
                  <button
                    type="button"
                    onClick={() => props.onFlyTo(b)}
                    className="min-w-0 flex-1 truncate rounded px-1 py-0.5 text-left text-[11px] text-foreground hover:bg-muted/60"
                    title="Fly the camera to this viewport"
                  >
                    {b.label}
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      setEditingId(b.id);
                      setDraft(b.label);
                    }}
                    className="rounded p-0.5 text-muted-foreground opacity-0 hover:text-foreground group-hover:opacity-100"
                    title="Rename"
                  >
                    <Pencil className="h-3 w-3" />
                  </button>
                  <button
                    type="button"
                    onClick={() => props.onRemove(b.id)}
                    className="rounded p-0.5 text-muted-foreground opacity-0 hover:text-destructive group-hover:opacity-100"
                    title="Delete bookmark"
                  >
                    <Trash2 className="h-3 w-3" />
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
