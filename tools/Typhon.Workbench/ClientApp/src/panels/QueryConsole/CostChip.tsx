import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';

/**
 * Live cost chip — renders the debounced `/query/plan` result as `≈ N arch / N ent / N MB`. When the DSL is invalid
 * (cost state = 'error') we show `—` per design §14.2. The 250 ms debounce is owned by `useQueryConsolePlan`.
 */
export function CostChip() {
  const plan = useQueryConsoleStore((s) => s.costPreview);
  const state = useQueryConsoleStore((s) => s.costState);

  if (state === 'idle') return <Pill text="≈ —" hint="Write a query to estimate cost" />;
  if (state === 'loading') return <Pill text="≈ …" hint="Estimating…" />;
  if (state === 'error') return <Pill text="≈ —" hint="Fix syntax to see cost" />;
  if (!plan) return <Pill text="≈ —" />;

  // Approximate MB: pagesRead × 4 KB (engine page size). Round to 1 decimal for kilobytes; whole MB above 1 MB.
  // Orval generates `long` as `string | number` (JSON-safe for 64-bit); coerce explicitly.
  const pages = Number(plan.estimatedPagesRead ?? 0);
  const bytes = pages * 4096;
  const sizeText = bytes < 1024 * 1024
    ? `${Math.max(1, Math.round(bytes / 1024))} KB`
    : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;

  return (
    <Pill
      text={`≈ ${plan.archetypesScanned} arch / ${plan.estimatedTotalEntities} ent / ${sizeText}`}
      hint="Estimated cost (planner; not actuals)"
    />
  );
}

function Pill({ text, hint }: { text: string; hint?: string }) {
  return (
    <span
      className="rounded-full border border-border bg-muted px-2 py-0.5 font-mono text-xs text-muted-foreground"
      title={hint}
    >
      {text}
    </span>
  );
}
