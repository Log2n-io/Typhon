import { useState } from 'react';
import { useQueryConsoleStore } from '@/stores/useQueryConsoleStore';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import type { PredicateNodeDto } from '@/api/generated/model/predicateNodeDto';
import type { QuerySpecDto } from '@/api/generated/model/querySpecDto';
import { FromArchetypeCombobox } from './FromArchetypeCombobox';
import { hasExplicitTake, specToDsl } from './specToDsl';
import { AddComponentPopover } from './chips/AddComponentPopover';
import { AddPredicatePopover } from './chips/AddPredicatePopover';
import { AddStageMenu, type StageId } from './chips/AddStageMenu';
import { AndOrChip } from './chips/AndOrChip';
import { ComponentChip } from './chips/ComponentChip';
import { OrderByChip } from './chips/OrderByChip';
import { PredicateChip } from './chips/PredicateChip';
import { SpatialChip } from './chips/SpatialChip';

/**
 * Chip-mode editor (#386 Phase 1 + Phase 1.5 polish). Composes the schema-aware chip primitives so the user
 * builds a query through guided choices — pick an archetype, pick components from its actual component list,
 * compose WHERE predicates via the 4-step popover (Component → indexed-only Field → type-filtered Op → typed
 * Value), pick ORDER BY from the same component, set SKIP/TAKE numerically. Every chip is keyed off the live
 * schema so the user never types a name the engine doesn't know.
 *
 * Architecture notes:
 *   - The spec is the single source of truth (Zustand store); chips render from it + write back via `mutate`.
 *   - Every mutation also re-serializes the spec to DSL via {@link specToDsl} so the DSL editor stays in sync
 *     without a server round-trip (avoid /parse for known-good chip-emitted shapes).
 *   - The WHERE AST is kept flat-left-associative ((cmp1 op cmp2) op cmp3 …) — nested grouping is a DSL-only
 *     escape hatch. Per-position AND/OR chips drive the combinator at each junction.
 */
export function SpecChips() {
  const spec = useQueryConsoleStore((s) => s.spec);
  const setSpec = useQueryConsoleStore((s) => s.setSpec);
  const setDslDraft = useQueryConsoleStore((s) => s.setDslDraft);

  const mutate = (mut: (s: QuerySpecDto) => QuerySpecDto) => {
    const next = mut(spec);
    setSpec(next);
    setDslDraft(specToDsl(next));
  };

  // Optional stages — track which the user has opted into. The mandatory FROM is always shown; everything else
  // appears once added via the "Add stage" menu. We keep this UI-only state so a momentarily-empty WHERE doesn't
  // disappear before the user finishes adding the first predicate.
  const [extraStages, setExtraStages] = useState<Set<StageId>>(new Set());
  const addStage = (s: StageId) => setExtraStages(new Set([...extraStages, s]));

  const hasWith = (spec.with ?? []).length > 0 || extraStages.has('WITH');
  const hasWithout = (spec.without ?? []).length > 0 || extraStages.has('WITHOUT');
  const hasExclude = (spec.exclude ?? []).length > 0 || extraStages.has('EXCLUDE');
  const hasEnabled = (spec.enabled ?? []).length > 0 || extraStages.has('ENABLED');
  const hasDisabled = (spec.disabled ?? []).length > 0 || extraStages.has('DISABLED');
  const hasWhere = spec.where != null || extraStages.has('WHERE');
  const hasSpatial = (spec.spatial ?? []).length > 0 || extraStages.has('SPATIAL');
  const hasSelect = (spec.select ?? []).length > 0 || extraStages.has('SELECT');
  const hasOrderBy = spec.orderBy != null || extraStages.has('ORDER BY');
  const hasSkip = Number(spec.skip ?? 0) > 0 || extraStages.has('SKIP');
  // Detect TAKE by value via the shared rule (so chip visibility can't drift from specToDsl's emission), not only
  // by the user-added flag. `extraStages` is component-local UI state that resets on a chips⇄DSL mode switch, so a
  // TAKE parsed in from DSL — or any non-default cap — would otherwise stay invisible while still being applied.
  const hasTake = hasExplicitTake(spec.take) || extraStages.has('TAKE');

  const available: StageId[] = (
    ['WITH', 'WITHOUT', 'EXCLUDE', 'ENABLED', 'DISABLED', 'WHERE', 'SPATIAL', 'SELECT', 'ORDER BY', 'SKIP', 'TAKE'] as StageId[]
  ).filter((s) => {
    if (s === 'WITH') return !hasWith;
    if (s === 'WITHOUT') return !hasWithout;
    if (s === 'EXCLUDE') return !hasExclude;
    if (s === 'ENABLED') return !hasEnabled;
    if (s === 'DISABLED') return !hasDisabled;
    if (s === 'WHERE') return !hasWhere;
    if (s === 'SPATIAL') return !hasSpatial;
    if (s === 'SELECT') return !hasSelect;
    if (s === 'ORDER BY') return !hasOrderBy;
    if (s === 'SKIP') return !hasSkip;
    if (s === 'TAKE') return !hasTake;
    return false;
  });

  // Single-component WHERE constraint (Phase 1 compiler): if there's at least one predicate, lock new ones to
  // its component. The compiler also surfaces this as a `multi_component_where_not_supported` error, but locking
  // at the UI is more user-friendly than letting them author and then bouncing.
  const wherePredicates = flattenWhere(spec.where);
  const lockedComponent = wherePredicates[0]?.component ?? undefined;
  const whereCombinator = whereTopLevelKind(spec.where);

  return (
    // Full-height flex column: the stage stack scrolls in the upper region, the DSL mirror is anchored to the bottom
    // and grows to fill the gap (so the panel never shows dead space below the preview box).
    <div className="flex h-full flex-col text-sm">
      <div className="flex-1 space-y-2 overflow-auto p-2">
      <ChipRow label="FROM">
        <FromArchetypeCombobox
          value={spec.archetype ?? ''}
          onChange={(next) => mutate((s) => ({ ...s, archetype: next }))}
        />
        <label className="ml-2 flex items-center gap-1 text-xs">
          <input
            type="checkbox"
            checked={spec.polymorphic ?? true}
            onChange={(e) => mutate((s) => ({ ...s, polymorphic: e.target.checked }))}
          />
          polymorphic
        </label>
      </ChipRow>

      {hasWith && (
        <ComponentChipRow
          label="WITH"
          values={spec.with ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, with: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'WITH'))}
        />
      )}
      {hasWithout && (
        <ComponentChipRow
          label="WITHOUT"
          values={spec.without ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, without: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'WITHOUT'))}
        />
      )}
      {hasExclude && (
        <ComponentChipRow
          label="EXCLUDE"
          values={spec.exclude ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, exclude: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'EXCLUDE'))}
        />
      )}
      {hasEnabled && (
        <ComponentChipRow
          label="ENABLED"
          values={spec.enabled ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, enabled: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'ENABLED'))}
        />
      )}
      {hasDisabled && (
        <ComponentChipRow
          label="DISABLED"
          values={spec.disabled ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, disabled: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'DISABLED'))}
        />
      )}

      {hasWhere && (
        <ChipRow label="WHERE">
          <div className="flex flex-1 flex-wrap items-center gap-1">
            {wherePredicates.map((node, i) => (
              <PredicateGroup
                key={i}
                node={node}
                showConnector={i > 0}
                connectorKind={whereCombinator}
                onToggleConnector={() => mutate((s) => ({ ...s, where: flipWhereCombinator(s.where) }))}
                onRemove={() => mutate((s) => ({ ...s, where: removeWherePredicate(s.where, i) }))}
              />
            ))}
            <AddPredicatePopover
              archetypeRef={spec.archetype}
              lockedComponent={lockedComponent}
              onAdd={(node) => mutate((s) => ({ ...s, where: appendWherePredicate(s.where, node, whereCombinator) }))}
            />
            {wherePredicates.length === 0 && (
              <button
                type="button"
                onClick={() => setExtraStages(removeFrom(extraStages, 'WHERE'))}
                className="ml-1 text-xs text-muted-foreground hover:text-foreground"
                title="Remove the WHERE stage"
              >
                × stage
              </button>
            )}
          </div>
        </ChipRow>
      )}

      {hasSpatial && (
        <ChipRow label="SPATIAL">
          <SpatialChip
            value={(spec.spatial ?? [])[0] ?? null}
            archetype={spec.archetype}
            onChange={(next) => {
              mutate((s) => ({ ...s, spatial: next ? [next] : [] }));
              if (!next) setExtraStages(removeFrom(extraStages, 'SPATIAL'));
            }}
          />
        </ChipRow>
      )}

      {hasSelect && (
        <ComponentChipRow
          label="SELECT"
          values={spec.select ?? []}
          archetype={spec.archetype}
          onChange={(next) => mutate((s) => ({ ...s, select: next }))}
          onClose={() => setExtraStages(removeFrom(extraStages, 'SELECT'))}
        />
      )}

      {hasOrderBy && (
        <ChipRow label="ORDER BY">
          <OrderByChip
            value={spec.orderBy ?? null}
            whereComponent={lockedComponent ?? null}
            onChange={(next) => {
              mutate((s) => ({ ...s, orderBy: next as QuerySpecDto['orderBy'] }));
              if (!next) setExtraStages(removeFrom(extraStages, 'ORDER BY'));
            }}
          />
        </ChipRow>
      )}

      {hasSkip && (
        <ChipRow label="SKIP">
          <input
            type="number"
            className="w-24 rounded border border-border bg-background px-2 py-1 font-mono"
            value={spec.skip ?? 0}
            onChange={(e) => mutate((s) => ({ ...s, skip: Number(e.target.value) || 0 }))}
            aria-label="Skip"
          />
          <RemoveStageButton
            onClick={() => {
              mutate((s) => ({ ...s, skip: 0 }));
              setExtraStages(removeFrom(extraStages, 'SKIP'));
            }}
          />
        </ChipRow>
      )}

      {hasTake && (
        <ChipRow label="TAKE">
          <input
            type="number"
            className="w-24 rounded border border-border bg-background px-2 py-1 font-mono"
            value={spec.take ?? 1000}
            onChange={(e) => mutate((s) => ({ ...s, take: Number(e.target.value) || 1000 }))}
            aria-label="Take"
          />
          <RemoveStageButton
            onClick={() => {
              mutate((s) => ({ ...s, take: 1000 }));
              setExtraStages(removeFrom(extraStages, 'TAKE'));
            }}
          />
        </ChipRow>
      )}

      <div className="pt-1">
        <AddStageMenu available={available} onPick={addStage} />
      </div>
      </div>

      {/* Read-only DSL mirror — always shows the serialised form of what the chips have composed. Selectable +
          has a Copy button so the user can hand the exact query state to support / chat / GitHub without
          switching to DSL mode. Updates live as chips change (the mutate path already calls setDslDraft, so
          the dslDraft store value is always in sync). Anchored to the bottom and grows to fill the remaining
          height so there's no dead gap below it. */}
      <DslMirror />
    </div>
  );
}

function DslMirror() {
  const dsl = useQueryConsoleStore((s) => s.dslDraft);
  const text = dsl || '(empty)';
  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      // Clipboard denied (non-secure context, etc.) — the textarea is selectable so the user falls back to Ctrl+C.
    }
  };
  return (
    // Bottom-anchored: `shrink-0` header row + a `flex-1` textarea that fills the gap below the stage stack instead of
    // being capped to a fixed line count (which left a dead space at the bottom of the chips pane).
    <div className="flex min-h-32 flex-1 flex-col gap-1 border-t border-border p-2 pt-2">
      <div className="flex shrink-0 items-center justify-between">
        <span className="font-mono text-xs text-muted-foreground">DSL preview</span>
        <button
          type="button"
          onClick={onCopy}
          className="rounded border border-border bg-muted/40 px-2 py-0.5 text-xs hover:bg-muted"
          title="Copy DSL to clipboard"
        >
          Copy
        </button>
      </div>
      {/* readOnly + select-all-on-focus = friendliest copy UX. select-text is redundant on form controls (user
          agents override the body { user-select: none } rule for them) but kept for clarity. */}
      <textarea
        readOnly
        value={text}
        onFocus={(e) => e.currentTarget.select()}
        className="select-text w-full flex-1 resize-none rounded border border-border bg-muted/20 p-2 font-mono text-xs"
        spellCheck={false}
        aria-label="DSL preview (read-only)"
      />
    </div>
  );
}

// ── Stage row primitives ───────────────────────────────────────────────────────────────────────────────────

function ChipRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center gap-2">
      <span className="w-24 shrink-0 font-mono text-xs text-muted-foreground">{label}</span>
      <div className="flex flex-1 flex-wrap items-center gap-1">{children}</div>
    </div>
  );
}

function ComponentChipRow({
  label,
  values,
  archetype,
  onChange,
  onClose,
}: {
  label: string;
  values: string[];
  archetype: string | null | undefined;
  onChange: (next: string[]) => void;
  onClose: () => void;
}) {
  const { label: nameLabel } = useComponentNames();
  return (
    <ChipRow label={label}>
      {values.map((tn) => (
        <ComponentChip
          key={tn}
          typeName={tn}
          label={nameLabel(tn)}
          onRemove={() => {
            const next = values.filter((v) => v !== tn);
            onChange(next);
            if (next.length === 0) onClose();
          }}
        />
      ))}
      <AddComponentPopover
        archetypeRef={archetype}
        excluded={values}
        onAdd={(tn) => onChange([...values, tn])}
        label={label}
      />
      {values.length === 0 && <RemoveStageButton onClick={onClose} hint="Remove this empty stage" />}
    </ChipRow>
  );
}

function RemoveStageButton({ onClick, hint }: { onClick: () => void; hint?: string }) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={hint ?? 'Remove stage'}
      className="ml-1 rounded text-xs text-muted-foreground hover:text-foreground"
    >
      × stage
    </button>
  );
}

function PredicateGroup({
  node,
  showConnector,
  connectorKind,
  onToggleConnector,
  onRemove,
}: {
  node: PredicateNodeDto;
  showConnector: boolean;
  connectorKind: 'and' | 'or';
  onToggleConnector: () => void;
  onRemove: () => void;
}) {
  return (
    <>
      {showConnector && <AndOrChip kind={connectorKind} onToggle={onToggleConnector} />}
      <PredicateChip node={node} onRemove={onRemove} />
    </>
  );
}

// ── WHERE AST helpers — flat-left-associative shape ───────────────────────────────────────────────────────

/** Flatten the WHERE AST into a sequence of cmp nodes. Phase 1 keeps WHERE shape: cmp | (cmp1 op cmp2 op …). */
function flattenWhere(node: PredicateNodeDto | null | undefined): PredicateNodeDto[] {
  if (!node) return [];
  if (node.kind === 'cmp') return [node];
  const children = (node.children ?? []) as PredicateNodeDto[];
  return children.flatMap(flattenWhere);
}

/** The combinator at the WHERE root (AND for a single cmp or no node — meaningless but stable). */
function whereTopLevelKind(node: PredicateNodeDto | null | undefined): 'and' | 'or' {
  if (!node || node.kind === 'cmp') return 'and';
  return node.kind === 'or' ? 'or' : 'and';
}

/** Toggle the WHERE root's combinator (and ⇄ or). No-op for a single-cmp / empty tree. */
function flipWhereCombinator(node: QuerySpecDto['where']): QuerySpecDto['where'] {
  if (!node || node.kind === 'cmp') return node;
  const flipped = node.kind === 'and' ? 'or' : 'and';
  return { ...node, kind: flipped };
}

/** Append a new cmp predicate to the WHERE tree, building/keeping a flat-left shape. */
function appendWherePredicate(
  node: QuerySpecDto['where'],
  add: PredicateNodeDto,
  preferredCombinator: 'and' | 'or',
): QuerySpecDto['where'] {
  if (!node) return add;
  if (node.kind === 'cmp') {
    return { kind: preferredCombinator, children: [node, add], component: null, field: null, op: null, value: null };
  }
  const children = [...((node.children ?? []) as PredicateNodeDto[]), add];
  return { ...node, children };
}

/** Remove the predicate at `index` from the flattened sequence. Re-collapses a single-child group back to a cmp.
 *
 * Returns `null` (cast to `QuerySpecDto['where']`) when the last predicate is removed — orval marks the field as
 * non-nullable but the server treats null/absent as "no WHERE", so the cast is the safe representation. */
function removeWherePredicate(node: QuerySpecDto['where'], index: number): QuerySpecDto['where'] {
  if (!node) return null as unknown as QuerySpecDto['where'];
  const flat = flattenWhere(node);
  flat.splice(index, 1);
  if (flat.length === 0) return null as unknown as QuerySpecDto['where'];
  if (flat.length === 1) return flat[0];
  return { kind: whereTopLevelKind(node), children: flat, component: null, field: null, op: null, value: null };
}

function removeFrom<T>(set: Set<T>, item: T): Set<T> {
  const next = new Set(set);
  next.delete(item);
  return next;
}
