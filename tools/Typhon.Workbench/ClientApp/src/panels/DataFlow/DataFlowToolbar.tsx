import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { type GranularityLevel, type XAxisMode, useDataFlowViewStore } from './useDataFlowViewStore';

/**
 * Top toolbar for the Data Flow Timeline. Three controls per design §11:
 * - Granularity slider (L0–L4)
 * - X-axis mode (uniform / equal / log)
 * - Hover-isolate escape hatch (toggleable; default ON)
 *
 * No phase-collapse menu yet — phase fences render but per-phase collapse is deferred (design §16
 * notes interactive phase collapse as a v1 feature, but in practice it's only useful once the user
 * has bars to collapse, which the v1 panel may render empty against existing traces).
 */
export default function DataFlowToolbar() {
  const granularityLevel = useDataFlowViewStore((s) => s.granularityLevel);
  const xMode = useDataFlowViewStore((s) => s.xMode);
  const hoverIsolateEnabled = useDataFlowViewStore((s) => s.hoverIsolateEnabled);
  const setGranularity = useDataFlowViewStore((s) => s.setGranularityLevel);
  const setXMode = useDataFlowViewStore((s) => s.setXMode);
  const setHoverIsolate = useDataFlowViewStore((s) => s.setHoverIsolateEnabled);

  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-border bg-card px-2 py-1">
      <span className="text-xs text-muted-foreground">Granularity</span>
      <GranularitySegmented value={granularityLevel} onChange={setGranularity} />

      <Separator orientation="vertical" className="h-6" />

      <span className="text-xs text-muted-foreground">X-axis</span>
      <XModeSegmented value={xMode} onChange={setXMode} />

      <Separator orientation="vertical" className="h-6" />

      <Button
        size="sm"
        variant={hoverIsolateEnabled ? 'default' : 'outline'}
        className="h-7"
        onClick={() => setHoverIsolate(!hoverIsolateEnabled)}
        title="Toggle hover-to-isolate (H)"
      >
        Hover isolate
      </Button>
    </div>
  );
}

const GRANULARITY_LABELS: Record<GranularityLevel, string> = {
  L0: 'L0',
  L1: 'L1',
  L2: 'L2',
  L3: 'L3',
  L4: 'L4',
};

const GRANULARITY_DESCRIPTIONS: Record<GranularityLevel, string> = {
  L0: 'Domain — Components / Queues / Resources',
  L1: 'Phase × Domain',
  L2: 'Component-family (default)',
  L3: 'Component type',
  L4: 'Archetype × component (finest)',
};

function GranularitySegmented({
  value,
  onChange,
}: {
  value: GranularityLevel;
  onChange: (level: GranularityLevel) => void;
}) {
  const levels: GranularityLevel[] = ['L0', 'L1', 'L2', 'L3', 'L4'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {levels.map((level) => (
        <button
          key={level}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === level
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          title={GRANULARITY_DESCRIPTIONS[level]}
          onClick={() => onChange(level)}
        >
          {GRANULARITY_LABELS[level]}
        </button>
      ))}
    </div>
  );
}

const X_MODE_LABELS: Record<XAxisMode, string> = {
  uniform: 'Uniform',
  equal: 'Equal',
  log: 'Log',
};

function XModeSegmented({
  value,
  onChange,
}: {
  value: XAxisMode;
  onChange: (mode: XAxisMode) => void;
}) {
  const modes: XAxisMode[] = ['uniform', 'equal', 'log'];
  return (
    <div className="flex overflow-hidden rounded-md border border-border">
      {modes.map((mode) => (
        <button
          key={mode}
          type="button"
          className={
            'h-7 px-2 text-xs leading-none ' +
            (value === mode
              ? 'bg-primary text-primary-foreground'
              : 'bg-background text-foreground hover:bg-muted')
          }
          onClick={() => onChange(mode)}
        >
          {X_MODE_LABELS[mode]}
        </button>
      ))}
    </div>
  );
}
