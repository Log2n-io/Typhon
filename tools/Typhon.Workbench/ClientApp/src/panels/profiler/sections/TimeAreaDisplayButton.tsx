import * as React from 'react';
import { SlidersHorizontal } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Display-options icon mounted in the ruler-row gutter, beside the section-filter funnel. Clicking
 * opens a Radix popover with the rendering toggles that aren't section visibility — span colour lens,
 * off-CPU overlay, dynamic track height. All persisted in `useProfilerViewStore`.
 *
 * Why a separate button from {@link TimeAreaFilterButton}: that popover filters *which* sections show;
 * this one controls *how* the visible sections render. Splitting the two concerns behind distinct
 * icons keeps each discoverable — the off-CPU toggle in particular was previously buried under the
 * filter funnel, where nobody expects a rendering option.
 */
export function TimeAreaDisplayButton(): React.JSX.Element {
  const spanColorMode = useProfilerViewStore((s) => s.spanColorMode);
  const setSpanColorMode = useProfilerViewStore((s) => s.setSpanColorMode);
  const showOffCpu = useProfilerViewStore((s) => s.showOffCpu);
  const toggleShowOffCpu = useProfilerViewStore((s) => s.toggleShowOffCpu);
  const dynamicTrackHeight = useProfilerViewStore((s) => s.dynamicTrackHeight);
  const toggleDynamicTrackHeight = useProfilerViewStore((s) => s.toggleDynamicTrackHeight);

  return (
    <Popover>
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label="Display options"
          title="Display options"
          className="flex items-center justify-center h-5 w-5 rounded-sm text-muted-foreground hover:text-foreground hover:bg-accent/60"
        >
          <SlidersHorizontal className="h-3.5 w-3.5" />
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" sideOffset={4} className="w-72 p-2">
        <div className="mb-1">
          <span className="text-sm font-medium">Display</span>
        </div>
        {/*
         * Color spans by — alternative lenses on the same data:
         *   name     → hash span name into the palette (default; pre-toggle behaviour)
         *   thread   → palette[threadSlot]; spots cross-thread patterns
         *   depth    → palette[depth]; visualises call-stack nesting
         *   duration → log-scale heat ramp (blue → green → orange → red); makes outliers pop
         */}
        <label className="flex items-center gap-2 pt-2 text-xs">
          <span className="text-muted-foreground shrink-0">Color spans by</span>
          <select
            value={spanColorMode}
            onChange={(e) => setSpanColorMode(e.target.value as typeof spanColorMode)}
            className="flex-1 h-7 rounded-sm border border-input bg-background px-2 text-sm"
          >
            <option value="name">Name</option>
            <option value="thread">Thread</option>
            <option value="depth">Depth</option>
            <option value="duration">Duration (heat)</option>
          </select>
        </label>
        {/*
         * Off-CPU overlay — semi-transparent hatched bars on each thread lane showing where the OS
         * switched the thread out (kind-254 context-switch events). Can be visually noisy on a
         * contended machine, so it's a dedicated toggle.
         */}
        <label className="flex items-center gap-2 pt-2 mt-2 border-t border-border text-xs">
          <input type="checkbox" checked={showOffCpu} onChange={toggleShowOffCpu} className="h-3.5 w-3.5" />
          <span className="text-muted-foreground">Show off-CPU intervals</span>
        </label>
        {/*
         * Dynamic track height — size each slot lane to the deepest span in the current viewport
         * rather than the session-wide maximum. Tracks shrink/grow as the user pans.
         */}
        <label className="flex items-center gap-2 pt-2 mt-2 border-t border-border text-xs">
          <input type="checkbox" checked={dynamicTrackHeight} onChange={toggleDynamicTrackHeight} className="h-3.5 w-3.5" />
          <span className="text-muted-foreground">Dynamic track height</span>
        </label>
      </PopoverContent>
    </Popover>
  );
}
