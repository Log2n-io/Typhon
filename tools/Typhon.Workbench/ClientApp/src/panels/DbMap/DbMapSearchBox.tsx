import { ChevronLeft, ChevronRight, Search } from 'lucide-react';

// The Database File Map toolbar search box (Module 15, A3, §4.5). A controlled input — the panel owns the
// query, resolves matches against the StructuralMap, and drives the camera fly-to; this is pure presentation.

interface DbMapSearchBoxProps {
  value: string;
  onChange: (value: string) => void;
  /** Enter pressed — fly to the next match (or the first when the query just changed). */
  onSubmit: () => void;
  onPrev: () => void;
  onNext: () => void;
  /** Number of resolved matches for the current query. */
  matchCount: number;
  /** Zero-based index of the match the camera is currently on. */
  matchIndex: number;
}

export function DbMapSearchBox(props: DbMapSearchBoxProps) {
  const { value, onChange, onSubmit, onPrev, onNext, matchCount, matchIndex } = props;
  const hasQuery = value.trim().length > 0;

  return (
    <div className="flex items-center gap-1">
      <div className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5">
        <Search className="h-3 w-3 text-muted-foreground" />
        <input
          type="text"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault();
              onSubmit();
            }
          }}
          placeholder="page:N · segment:N · type…"
          className="w-44 bg-transparent text-[11px] text-foreground outline-none placeholder:text-muted-foreground"
          data-testid="dbmap-search"
        />
        {hasQuery && (
          <span className="font-mono text-[10px] tabular-nums text-muted-foreground" data-testid="dbmap-search-count">
            {matchCount === 0 ? 'no match' : `${matchIndex + 1}/${matchCount}`}
          </span>
        )}
      </div>
      <button
        type="button"
        onClick={onPrev}
        disabled={matchCount === 0}
        className="rounded border border-border bg-card p-0.5 text-muted-foreground hover:text-foreground disabled:opacity-40"
        title="Previous match"
      >
        <ChevronLeft className="h-3 w-3" />
      </button>
      <button
        type="button"
        onClick={onNext}
        disabled={matchCount === 0}
        className="rounded border border-border bg-card p-0.5 text-muted-foreground hover:text-foreground disabled:opacity-40"
        title="Next match"
      >
        <ChevronRight className="h-3 w-3" />
      </button>
    </div>
  );
}
