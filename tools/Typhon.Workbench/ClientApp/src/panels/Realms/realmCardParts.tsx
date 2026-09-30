/**
 * The two presentational pieces both realm Inspector bodies use, so the catalog card and the live card read as one
 * surface rather than as two panels that happen to describe the same object.
 */

export function CardSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-1">
      <h4 className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{title}</h4>
      <dl className="flex flex-col gap-0.5">{children}</dl>
    </section>
  );
}

export function CardRow({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3" title={hint}>
      <dt className="shrink-0 text-xs text-muted-foreground">{label}</dt>
      <dd className="truncate text-right tabular-nums">{value}</dd>
    </div>
  );
}
