import { Crosshair, RefreshCw } from 'lucide-react';
import { useDbMapStore } from '@/stores/useDbMapStore';
import type { DbMapEncoding, DbMapLens, DbMapPageOrder } from '@/libs/dbmap/types';
import { DbMapSearchBox } from './DbMapSearchBox';
import { DbMapFilterMenu } from './DbMapFilterMenu';
import { DbMapExportMenu } from './DbMapExportMenu';

// The Database File Map toolbar (Module 15, A3, §6.1) — the encoding picker, the lens selector, the segment
// overlay toggle, fit / refresh, and the search box. Encoding / lens / overlay are read straight from the
// singleton store; the panel passes only the callbacks that need its internals (fit, refresh, search).

interface DbMapToolbarProps {
  onFit: () => void;
  onRefresh: () => void;
  onExportViewPng: () => void;
  onExportMapPng: () => void;
  onExportCsv: () => void;
  search: string;
  onSearchChange: (value: string) => void;
  onSearchSubmit: () => void;
  onSearchPrev: () => void;
  onSearchNext: () => void;
  searchMatchCount: number;
  searchMatchIndex: number;
}

const SELECT_CLASS = 'rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-foreground';

export function DbMapToolbar(props: DbMapToolbarProps) {
  const encoding = useDbMapStore((s) => s.encoding);
  const setEncoding = useDbMapStore((s) => s.setEncoding);
  const pageOrder = useDbMapStore((s) => s.pageOrder);
  const setPageOrder = useDbMapStore((s) => s.setPageOrder);
  const lens = useDbMapStore((s) => s.lens);
  const setLens = useDbMapStore((s) => s.setLens);
  const segmentOverlay = useDbMapStore((s) => s.segmentOverlay);
  const toggleSegmentOverlay = useDbMapStore((s) => s.toggleSegmentOverlay);

  return (
    <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
      <label className="text-[11px] text-muted-foreground">Order</label>
      <select
        className={SELECT_CLASS}
        value={pageOrder}
        onChange={(e) => setPageOrder(e.target.value as DbMapPageOrder)}
        data-testid="dbmap-page-order"
        title="Page layout — Hilbert curve (2D locality) or row-major sequential"
      >
        <option value="hilbert">Hilbert</option>
        <option value="sequential">Sequential</option>
      </select>

      <label className="text-[11px] text-muted-foreground">Encoding</label>
      <select
        className={SELECT_CLASS}
        value={encoding}
        onChange={(e) => setEncoding(e.target.value as DbMapEncoding)}
        data-testid="dbmap-encoding"
      >
        <optgroup label="Coarse">
          <option value="pageType">Page type</option>
          <option value="segment">Owning segment</option>
          <option value="freeUsed">Free / used</option>
        </optgroup>
        <optgroup label="Detail">
          <option value="fillDensity">Fill density</option>
          <option value="writeAge">Write age</option>
          <option value="crc">CRC status</option>
          <option value="residency">Cache residency</option>
          <option value="entropy">Entropy</option>
          <option value="byteClass">Byte class</option>
        </optgroup>
      </select>

      <label className="text-[11px] text-muted-foreground">Lens</label>
      <select
        className={SELECT_CLASS}
        value={lens}
        onChange={(e) => setLens(e.target.value as DbMapLens)}
        data-testid="dbmap-lens"
      >
        <option value="none">None</option>
        <option value="fragmentation">Fragmentation</option>
        <option value="freeSpace">Free space</option>
        <option value="pathology">Pathology</option>
      </select>

      <button
        type="button"
        onClick={toggleSegmentOverlay}
        className={`rounded border px-1.5 py-0.5 text-[11px] ${
          segmentOverlay
            ? 'border-primary bg-primary/15 text-foreground'
            : 'border-border bg-card text-muted-foreground'
        }`}
        title="Toggle segment-boundary overlay (s)"
      >
        Segments
      </button>
      <button
        type="button"
        onClick={props.onFit}
        className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
        title="Fit whole file (f or middle-click)"
      >
        <Crosshair className="h-3 w-3" /> Fit
      </button>
      <button
        type="button"
        onClick={props.onRefresh}
        className="flex items-center gap-1 rounded border border-border bg-card px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
        title="Refresh the map"
      >
        <RefreshCw className="h-3 w-3" /> Refresh
      </button>

      <DbMapFilterMenu />
      <DbMapExportMenu onExportViewPng={props.onExportViewPng} onExportMapPng={props.onExportMapPng} onExportCsv={props.onExportCsv} />

      <div className="ml-auto">
        <DbMapSearchBox
          value={props.search}
          onChange={props.onSearchChange}
          onSubmit={props.onSearchSubmit}
          onPrev={props.onSearchPrev}
          onNext={props.onSearchNext}
          matchCount={props.searchMatchCount}
          matchIndex={props.searchMatchIndex}
        />
      </div>
    </div>
  );
}
