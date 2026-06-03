/**
 * Connector chip between two adjacent WHERE predicates. Click to toggle AND ↔ OR. Reflects the kind of the
 * containing combinator node — for Phase 1 we keep the WHERE AST flat-left-associative (no nested parens), so
 * the per-position toggle drives the whole sequence's grouping shape.
 */
export function AndOrChip({ kind, onToggle }: { kind: 'and' | 'or'; onToggle: () => void }) {
  return (
    <button
      type="button"
      onClick={onToggle}
      title={`Click to flip to ${kind === 'and' ? 'OR' : 'AND'}`}
      className="rounded border border-border bg-background px-1.5 py-0.5 font-mono text-xs uppercase text-muted-foreground hover:bg-muted hover:text-foreground"
    >
      {kind}
    </button>
  );
}
