/**
 * One component-name pill — used by WITH / WITHOUT / EXCLUDE / ENABLED / DISABLED stages. Renders the smart short
 * label (set by the parent via {@link useComponentNames}) in monospace + an × remove button. Hover-tooltip carries
 * the full identity (fullName, or the raw typeName when no fullName is supplied) so nothing is lost to shortening.
 */
export function ComponentChip({
  typeName,
  label,
  fullName,
  onRemove,
}: {
  typeName: string;
  /** Smart short label to display; falls back to the raw typeName when omitted. */
  label?: string;
  fullName?: string;
  onRemove: () => void;
}) {
  return (
    <span
      title={fullName ?? typeName}
      className="inline-flex items-center gap-1 rounded border border-border bg-muted/50 px-1.5 py-0.5 font-mono text-xs"
    >
      <span>{label ?? typeName}</span>
      <button
        type="button"
        onClick={onRemove}
        className="text-muted-foreground hover:text-foreground"
        aria-label={`Remove ${typeName}`}
        title="Remove"
      >
        ×
      </button>
    </span>
  );
}
