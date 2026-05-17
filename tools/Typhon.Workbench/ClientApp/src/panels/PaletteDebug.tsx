import {
  CACHE_EXCLUSIVE_COLOR,
  GAUGE_PALETTE,
  OVERVIEW_PALETTE,
  PHASE_PALETTE,
  SELECTED_COLOR,
  SPAN_PALETTE,
  SPAN_PALETTE_LIGHT,
  SYSTEM_PALETTE,
  TIMELINE_PALETTE,
  TIMELINE_PALETTE_LIGHT,
  UNPHASED_COLOR,
  type PaletteColor,
} from '@/libs/palettes';
import { OFF_CPU_PALETTE } from '@/libs/profiler/canvas/theme';
import { ACCESS_COLOR } from '@/panels/DataFlow/barBuilding';

/**
 * Debug panel — renders every colour palette the Workbench draws with as labelled swatches. A
 * developer aid: opened only from the command palette ("Debug: Color Palettes"), never in the
 * View menu and not part of any default layout.
 */
export default function PaletteDebug() {
  return (
    <div className="h-full w-full overflow-auto bg-background p-3 text-[11px]">
      <h2 className="text-[13px] font-semibold text-foreground">Color Palettes</h2>
      <p className="mb-3 text-muted-foreground">
        Every palette the Workbench renders with. Debug view — opened via the command palette.
      </p>

      <PairSection
        name="PHASE_PALETTE"
        note="Phase band — colorForPhase(index). @/libs/palettes"
        colors={[...PHASE_PALETTE, UNPHASED_COLOR]}
        lastLabel="unphased"
      />
      <PairSection
        name="SYSTEM_PALETTE"
        note="System-identity bars — colorForSystem(name). The phase palette minus the alarm-red hue. @/libs/palettes"
        colors={SYSTEM_PALETTE}
      />
      <HexSection
        name="TIMELINE_PALETTE"
        note="13-colour Turbo ramp — phase / operation bands (dark theme). @/libs/profiler/canvas/canvasUtils"
        colors={TIMELINE_PALETTE}
      />
      <HexSection name="TIMELINE_PALETTE_LIGHT" note="Turbo ramp darkened for the light theme." colors={TIMELINE_PALETTE_LIGHT} />
      <HexSection name="SPAN_PALETTE" note="Span / flame bars (dark theme)." colors={SPAN_PALETTE} />
      <HexSection name="SPAN_PALETTE_LIGHT" note="Span / flame bars (light theme)." colors={SPAN_PALETTE_LIGHT} />
      <HexSection name="GAUGE_PALETTE" note="8-colour Viridis ramp — the gauge region." colors={GAUGE_PALETTE} />
      <HexSection
        name="OFF_CPU_PALETTE"
        note="Off-CPU wait-reason overlays — index matches OffCpuCategory. @/libs/profiler/canvas/theme"
        colors={OFF_CPU_PALETTE}
      />

      <Section name="ACCESS_COLOR" note="Data-flow access kinds. @/panels/DataFlow/barBuilding">
        {Object.entries(ACCESS_COLOR).map(([k, v]) => (
          <Swatch key={k} label={k} value={v} fill={v} />
        ))}
      </Section>
      <Section name="OVERVIEW_PALETTE" note="Tick-overview bars. @/libs/profiler/canvas/canvasUtils">
        {Object.entries(OVERVIEW_PALETTE).map(([k, v]) => (
          <Swatch key={k} label={k} value={v} fill={v} />
        ))}
      </Section>
      <Section name="Identity constants" note="Theme-independent single colours.">
        <Swatch label="SELECTED_COLOR" value={SELECTED_COLOR} fill={SELECTED_COLOR} />
        <Swatch label="CACHE_EXCLUSIVE_COLOR" value={CACHE_EXCLUSIVE_COLOR} fill={CACHE_EXCLUSIVE_COLOR} />
      </Section>
    </div>
  );
}

/** One palette block — a name, an optional source/usage note, and a wrapped row of swatches. */
function Section({ name, note, children }: { name: string; note?: string; children: React.ReactNode }) {
  return (
    <div className="mb-4">
      <div className="font-mono font-semibold text-foreground">{name}</div>
      {note && <div className="mb-1.5 text-muted-foreground">{note}</div>}
      <div className="flex flex-wrap gap-2">{children}</div>
    </div>
  );
}

/** A string[] palette → indexed swatches. */
function HexSection({ name, note, colors }: { name: string; note?: string; colors: readonly string[] }) {
  return (
    <Section name={name} note={note}>
      {colors.map((c, i) => (
        <Swatch key={i} label={`[${i}]`} value={c} fill={c} />
      ))}
    </Section>
  );
}

/** A {@link PaletteColor}[] palette → swatches whose box shows the fill bordered by the stroke. */
function PairSection({
  name,
  note,
  colors,
  lastLabel,
}: {
  name: string;
  note?: string;
  colors: readonly PaletteColor[];
  lastLabel?: string;
}) {
  return (
    <Section name={name} note={note}>
      {colors.map((c, i) => (
        <Swatch
          key={i}
          label={lastLabel && i === colors.length - 1 ? lastLabel : `[${i}]`}
          value={`fill ${c.fill}`}
          subValue={`stroke ${c.stroke}`}
          fill={c.fill}
          stroke={c.stroke}
        />
      ))}
    </Section>
  );
}

/** One colour swatch — a fixed-size colour box plus its label and value text. */
function Swatch({
  label,
  value,
  subValue,
  fill,
  stroke,
}: {
  label: string;
  value?: string;
  subValue?: string;
  fill: string;
  stroke?: string;
}) {
  return (
    <div className="flex w-[140px] flex-col gap-0.5">
      <div
        className="h-11 w-full rounded"
        style={{ backgroundColor: fill, border: stroke ? `2px solid ${stroke}` : '1px solid hsl(var(--border))' }}
      />
      <span className="truncate font-mono text-foreground" title={label}>
        {label}
      </span>
      {value && (
        <span className="truncate font-mono text-muted-foreground" title={value}>
          {value}
        </span>
      )}
      {subValue && (
        <span className="truncate font-mono text-muted-foreground" title={subValue}>
          {subValue}
        </span>
      )}
    </div>
  );
}
