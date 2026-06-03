import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import type { PredicateNodeDto } from '@/api/generated/model/predicateNodeDto';

/**
 * Single comparison-predicate pill: `Component.Field op value` with × remove. The component is rendered with its
 * smart short label; the tooltip carries the canonical DSL form (full component name included) so a hover discloses
 * what gets serialised. Rendered inline with AND/OR connector chips between predicates.
 */
export function PredicateChip({ node, onRemove }: { node: PredicateNodeDto; onRemove: () => void }) {
  const { label: nameLabel } = useComponentNames();
  if (node.kind !== 'cmp') return null;
  const label = `${node.component}.${node.field} ${node.op} ${formatValue(node.value)}`;
  return (
    <span
      title={label}
      className="inline-flex items-center gap-1 rounded border border-border bg-muted/50 px-1.5 py-0.5 font-mono text-xs"
    >
      <span className="text-muted-foreground">{nameLabel(node.component)}.</span>
      <span>{node.field}</span>
      <span className="text-muted-foreground">{node.op}</span>
      <span>{formatValue(node.value)}</span>
      <button
        type="button"
        onClick={onRemove}
        className="ml-1 text-muted-foreground hover:text-foreground"
        aria-label="Remove predicate"
        title="Remove"
      >
        ×
      </button>
    </span>
  );
}

function formatValue(v: unknown): string {
  if (typeof v === 'string') {
    return /^[A-Za-z_][A-Za-z0-9_]*$/.test(v) ? v : JSON.stringify(v);
  }
  if (v === null || v === undefined) return '';
  return String(v);
}
