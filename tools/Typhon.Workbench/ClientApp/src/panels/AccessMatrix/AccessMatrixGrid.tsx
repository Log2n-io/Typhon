import { useMemo } from 'react';
import { ACCESS_COLOR } from '@/panels/DataFlow/barBuilding';
import { colorForPhase } from '@/libs/palettes';
import type { AccessMatrix, Cell, Column } from './matrixBuilding';

/**
 * CSS Grid renderer for the Access Matrix. Sticky phase header row + sticky row header column. Click on a cell
 * mirrors the system to {@link useSelectionStore.system}; the column header doubles as a click target for the
 * same effect.
 *
 * Sizing: rows are {@link ROW_HEIGHT_PX} (matches existing 22px convention), columns are {@link COL_WIDTH_PX}.
 * The first row is the phase tint header (sticky top); the second row is the system name header. Below is the
 * cell grid.
 *
 * Virtualization: deferred to a follow-up. Plain CSS Grid with up to ~24,000 cells (worst-case 200 systems × 120
 * components) renders in <100ms. The realistic AntHill-scale matrix is ~20×10 = 200 cells, well within budget.
 * If a real workload tips the budget, swap in `@tanstack/react-virtual` (already a dep) for both axes.
 */
export interface AccessMatrixGridProps {
  matrix: AccessMatrix;
  /** Phases in declared order — drives header tint coloring. */
  phases: readonly string[];
  /** Currently selected system (cross-panel) — column gets a stronger outline + brightened cells. */
  selectedSystem: string | null;
  /** Phase D (#327): currently-selected dataTrack id — matching row gets an amber outline. */
  selectedTrackId: string | null;
  /** Phase D (#327): currently-selected phase — matching phase header brightens. */
  selectedPhase: string | null;
  /** Phase D (#327): cross-panel hover key — column belonging to the hovered system brightens. */
  hoveredSystem: string | null;
  /** Click handler for cells / column headers — fires with the system name. */
  onSelectSystem?: (systemName: string) => void;
  /** Phase D (#327): click handler for row headers — fires with the track id. */
  onSelectTrack?: (trackId: string) => void;
  /** Phase D (#327): click handler for phase headers — fires with the phase name. */
  onSelectPhase?: (phaseName: string) => void;
}

const ROW_HEIGHT_PX = 22;
const COL_WIDTH_PX = 96;
const ROW_HEADER_WIDTH_PX = 200;
const PHASE_HEADER_HEIGHT_PX = 14;
const SYSTEM_HEADER_HEIGHT_PX = 22;

export default function AccessMatrixGrid({
  matrix,
  phases,
  selectedSystem,
  selectedTrackId,
  selectedPhase,
  hoveredSystem,
  onSelectSystem,
  onSelectTrack,
  onSelectPhase,
}: AccessMatrixGridProps) {
  const { rows, columns, cells } = matrix;

  // Phase index map for quick color lookup.
  const phaseIndex = useMemo(() => {
    const m = new Map<string, number>();
    phases.forEach((p, i) => m.set(p, i));
    return m;
  }, [phases]);

  // Compute max touch count once for intensity normalization. Cells with touchCount > 0 brighten relative to this.
  const maxTouch = useMemo(() => {
    let max = 0;
    for (const c of cells.values()) {
      if (c.touchCount > max) max = c.touchCount;
    }
    return max;
  }, [cells]);

  if (rows.length === 0 || columns.length === 0) {
    return (
      <div className="flex h-full items-center justify-center p-4 text-sm text-muted-foreground">
        No data to display. Open a session and ensure topology is loaded.
      </div>
    );
  }

  const totalGridWidth = ROW_HEADER_WIDTH_PX + columns.length * COL_WIDTH_PX;
  const totalGridHeight = PHASE_HEADER_HEIGHT_PX + SYSTEM_HEADER_HEIGHT_PX + rows.length * ROW_HEIGHT_PX;

  return (
    <div className="h-full w-full overflow-auto bg-background">
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: `${ROW_HEADER_WIDTH_PX}px repeat(${columns.length}, ${COL_WIDTH_PX}px)`,
          gridTemplateRows: `${PHASE_HEADER_HEIGHT_PX}px ${SYSTEM_HEADER_HEIGHT_PX}px repeat(${rows.length}, ${ROW_HEIGHT_PX}px)`,
          width: totalGridWidth,
          height: totalGridHeight,
        }}
      >
        {/* Top-left corner: empty placeholder under both sticky headers. */}
        <div
          className="sticky left-0 top-0 z-30 border-b border-r border-border bg-card"
          style={{ gridRow: '1 / span 2', gridColumn: '1 / 2' }}
        />

        {/* Phase header row: sticky top, spans columns of each phase group with phase-color tint. */}
        {renderPhaseHeaders(columns, phaseIndex, selectedPhase, onSelectPhase)}

        {/* System header row: sticky top, one cell per column. Phase D (#327): cross-panel hover brightens column. */}
        {columns.map((col, colIdx) => {
          const isSelected = selectedSystem === col.systemName;
          const isHovered = hoveredSystem === col.systemName;
          return (
            <button
              type="button"
              key={`syshdr:${col.systemName}`}
              className={
                'sticky z-20 flex items-center justify-start truncate border-b border-r border-border px-1 text-left text-[11px] ' +
                (isSelected
                  ? 'bg-amber-500/20 font-semibold text-foreground'
                  : isHovered
                    ? 'bg-foreground/10 text-foreground'
                    : 'bg-card text-foreground hover:bg-muted')
              }
              style={{
                gridRow: 2,
                gridColumn: colIdx + 2,
                top: PHASE_HEADER_HEIGHT_PX,
              }}
              title={col.systemName}
              data-testid={`access-matrix-system-${col.systemName}`}
              aria-pressed={isSelected}
              data-hovered={isHovered ? 'true' : 'false'}
              onClick={() => onSelectSystem?.(col.systemName)}
            >
              {col.systemName}
            </button>
          );
        })}

        {/* Row header column: sticky left, one cell per row. Phase D (#327): clickable → dataTrack. */}
        {rows.map((row, rowIdx) => {
          const isSelected = selectedTrackId === row.id;
          return (
            <button
              type="button"
              key={`rowhdr:${row.id}`}
              className={
                'sticky left-0 z-10 flex items-center truncate border-b border-r border-border px-2 text-left text-[11px] ' +
                (isSelected
                  ? 'bg-amber-500/20 font-semibold text-foreground ring-1 ring-amber-400/60'
                  : 'bg-card text-foreground hover:bg-muted')
              }
              style={{
                gridRow: rowIdx + 3,
                gridColumn: 1,
                height: ROW_HEIGHT_PX,
              }}
              title={row.label}
              data-testid={`access-matrix-row-${row.id}`}
              aria-pressed={isSelected}
              onClick={() => onSelectTrack?.(row.id)}
            >
              {row.label}
            </button>
          );
        })}

        {/* Cell grid: one element per (row, col); fall through transparent if no cell exists. */}
        {rows.flatMap((row, rowIdx) =>
          columns.map((col, colIdx) => {
            const key = `${row.id}|${col.systemName}`;
            const cell = cells.get(key);
            return (
              <CellTile
                key={key}
                cell={cell}
                col={col}
                rowIdx={rowIdx}
                colIdx={colIdx}
                maxTouch={maxTouch}
                isSelected={selectedSystem === col.systemName}
                onClick={() => onSelectSystem?.(col.systemName)}
              />
            );
          }),
        )}
      </div>
    </div>
  );
}

/**
 * Render the phase header row as one bar per phase group. Each bar spans the columns of that phase, sticky to
 * the top so it stays visible while scrolling. Phase-color tint matches `colorForPhase` from the Critical Path
 * panel for visual continuity with the System DAG's swim-lanes.
 */
function renderPhaseHeaders(
  columns: readonly Column[],
  phaseIndex: Map<string, number>,
  selectedPhase: string | null,
  onSelectPhase: ((phase: string) => void) | undefined,
): React.ReactElement[] {
  const out: React.ReactElement[] = [];
  let i = 0;
  while (i < columns.length) {
    const phase = columns[i].phaseName;
    let j = i;
    while (j < columns.length && columns[j].phaseName === phase) j++;
    const span = j - i;
    const idx = phase ? phaseIndex.get(phase) ?? -1 : -1;
    const color = colorForPhase(idx);
    const isSelected = phase !== '' && selectedPhase === phase;
    // Phase D (#327): clickable phase header → cross-panel phase selection. Empty-phase columns aren't clickable.
    out.push(
      <button
        type="button"
        key={`phdr:${i}:${phase}`}
        disabled={phase === ''}
        className={
          'sticky top-0 z-20 flex items-center justify-center truncate border-r border-border text-[10px] uppercase tracking-wide text-foreground ' +
          (isSelected ? 'ring-1 ring-amber-400 brightness-125' : 'hover:brightness-110')
        }
        style={{
          gridRow: 1,
          gridColumn: `${i + 2} / span ${span}`,
          background: color.fill,
        }}
        title={phase || '(no phase)'}
        data-testid={phase ? `access-matrix-phase-${phase}` : undefined}
        aria-pressed={phase !== '' ? isSelected : undefined}
        onClick={phase !== '' ? () => onSelectPhase?.(phase) : undefined}
      >
        {phase || '—'}
      </button>,
    );
    i = j;
  }
  return out;
}

interface CellTileProps {
  cell: Cell | undefined;
  col: Column;
  rowIdx: number;
  colIdx: number;
  maxTouch: number;
  isSelected: boolean;
  onClick: () => void;
}

/**
 * Single cell renderer. Visual rules:
 * - No cell → transparent grid space (matrix is sparse; absence is meaningful).
 * - Cell with `accessKind: 'none'` AND `touchCount > 0` → faded slate (touched at runtime without declared access).
 * - Cell with declared access → access-kind color, intensity scaled by `touchCount / maxTouch`.
 * - Selected column → stronger outline + 2px right/left edge tint.
 */
function CellTile({ cell, rowIdx, colIdx, maxTouch, isSelected, onClick }: CellTileProps) {
  const baseStyle: React.CSSProperties = {
    gridRow: rowIdx + 3,
    gridColumn: colIdx + 2,
    height: ROW_HEIGHT_PX,
    cursor: cell ? 'pointer' : 'default',
  };

  if (!cell) {
    // Even empty cells are clickable to select the system — clicking anywhere in a column is a friendly affordance.
    return (
      <div
        className={
          'border-b border-r border-border ' +
          (isSelected ? 'bg-amber-500/5' : 'bg-transparent hover:bg-muted/30')
        }
        style={baseStyle}
        onClick={onClick}
      />
    );
  }

  const baseColor = ACCESS_COLOR[cell.accessKind];
  // Intensity from touch count. 0 ticks → 50% alpha (the "declared but didn't fire in this range" case);
  // max ticks → 100% alpha. Continuous gradient between.
  const intensity = maxTouch === 0 ? 0.55 : 0.55 + 0.45 * Math.min(1, cell.touchCount / maxTouch);
  const fill = colorWithAlpha(baseColor, intensity);

  return (
    <button
      type="button"
      className={
        'flex items-center justify-center border-b border-r text-[10px] font-mono leading-none text-white ' +
        (isSelected ? 'border-amber-400 ring-1 ring-amber-400/50' : 'border-border hover:brightness-110')
      }
      style={{ ...baseStyle, background: fill }}
      title={`${cell.columnSystemName}: ${cell.accessKind}${cell.touchCount > 0 ? ` (${cell.touchCount} touches)` : ''}`}
      data-testid={`access-matrix-cell-${cell.rowId}|${cell.columnSystemName}`}
      onClick={onClick}
    >
      {cell.touchCount > 0 ? cell.touchCount : ''}
    </button>
  );
}

/**
 * Convert a hex color (`#rrggbb`) to an `rgba(r, g, b, a)` string for alpha blending.
 */
function colorWithAlpha(hex: string, alpha: number): string {
  if (!hex.startsWith('#') || hex.length !== 7) return hex;
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha.toFixed(2)})`;
}
