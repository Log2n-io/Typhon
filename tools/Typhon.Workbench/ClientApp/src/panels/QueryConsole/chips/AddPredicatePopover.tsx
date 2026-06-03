import { useMemo, useState, useEffect } from 'react';
import { Command } from 'cmdk';
import { Plus } from 'lucide-react';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import type { PredicateNodeDto } from '@/api/generated/model/predicateNodeDto';
import { useArchetypeComponents } from '@/hooks/queryConsole/useArchetypeComponents';
import { useComponentNames } from '@/hooks/queryConsole/useComponentNames';
import {
  useComponentSchema,
  operatorsForType,
  isNumericType,
  type ComparisonOp,
} from '@/hooks/queryConsole/useComponentSchema';
import { humpFilter } from '@/shell/camelHumpFilter';

/**
 * The 4-step WHERE-predicate composer (design §4.3). Stepper UI: pick Component → pick Field (non-indexed greyed
 * out with tooltip) → pick Operator (filtered to the field's type) → enter Value. On confirm, emits a
 * fully-formed `cmp` PredicateNodeDto.
 *
 * Phase-1 single-component constraint: when `lockedComponent` is set (a previous predicate has already picked a
 * component), the picker shows only that component and surfaces a tooltip explaining why. Predicates from a
 * second component would currently trip the compiler's `multi_component_where_not_supported` check.
 */
export function AddPredicatePopover({
  archetypeRef,
  lockedComponent,
  onAdd,
  size = 'normal',
}: {
  archetypeRef: string | null | undefined;
  /** When set, the picker forces this component (other components are hidden — design §4.3 + Phase 1 compiler limit). */
  lockedComponent?: string;
  onAdd: (node: PredicateNodeDto) => void;
  size?: 'normal' | 'small';
}) {
  const [open, setOpen] = useState(false);
  const [component, setComponent] = useState<string | null>(lockedComponent ?? null);
  const [field, setField] = useState<string | null>(null);
  const [op, setOp] = useState<ComparisonOp | null>(null);
  const [valueText, setValueText] = useState('');

  // Reset the cascade whenever the popover opens (and re-apply lockedComponent if set).
  useEffect(() => {
    if (open) {
      setComponent(lockedComponent ?? null);
      setField(null);
      setOp(null);
      setValueText('');
    }
  }, [open, lockedComponent]);

  const { components } = useArchetypeComponents(archetypeRef);
  const { label: nameLabel } = useComponentNames();
  const visibleComponents = lockedComponent ? components.filter((c) => c.typeName === lockedComponent) : components;
  const { schema } = useComponentSchema(component);
  const fields = schema?.fields ?? [];

  const selectedField = field ? fields.find((f) => f.name === field) : null;
  const ops = useMemo(() => operatorsForType(selectedField?.typeName), [selectedField?.typeName]);
  const valueIsNumeric = isNumericType(selectedField?.typeName);

  const canConfirm = component && field && op && valueText.trim().length > 0;

  const confirm = () => {
    if (!canConfirm) return;
    const value = parseValueForField(valueText.trim(), selectedField?.typeName);
    const node: PredicateNodeDto = {
      kind: 'cmp',
      children: null,
      component: component!,
      field: field!,
      op: op!,
      value,
    };
    onAdd(node);
    setOpen(false);
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={!archetypeRef}
          className={`inline-flex items-center gap-0.5 rounded border border-dashed border-border px-1.5 py-0.5 ${
            size === 'small' ? 'text-xs' : 'text-xs'
          } text-muted-foreground hover:bg-muted disabled:opacity-50`}
          title={archetypeRef ? 'Add a predicate' : 'Pick an archetype first'}
        >
          <Plus className="h-3 w-3" />
          predicate
        </button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-96 p-0">
        <div className="flex flex-col gap-2 p-3">
          {/* Step 1 — Component */}
          <Step label="1. Component" done={!!component}>
            {component ? (
              <ChosenPill
                value={nameLabel(component)}
                title={component}
                onChange={() => setComponent(null)}
                disabled={!!lockedComponent}
              />
            ) : (
              <Command filter={humpFilter} className="rounded border border-border">
                <Command.Input
                  placeholder="Pick a component…"
                  className="w-full border-b border-border bg-transparent px-2 py-1 text-sm outline-none placeholder:text-muted-foreground"
                  autoFocus
                />
                <Command.List className="max-h-40 overflow-auto p-1">
                  {visibleComponents.length === 0 && (
                    <Command.Empty className="px-2 py-1 text-xs text-muted-foreground">
                      No components on this archetype.
                    </Command.Empty>
                  )}
                  {visibleComponents.map((c) => (
                    <Command.Item
                      key={c.typeName}
                      value={c.typeName}
                      keywords={[nameLabel(c.typeName), `${c.fullName} ${c.typeName}`]}
                      onSelect={() => setComponent(c.typeName)}
                      className="cursor-pointer rounded px-2 py-1 font-mono text-sm aria-selected:bg-accent aria-selected:text-accent-foreground"
                    >
                      <span title={c.typeName}>{nameLabel(c.typeName)}</span>
                      <span className="ml-2 text-xs text-muted-foreground">
                        {c.indexCount > 0 ? `${c.indexCount} indexed` : 'no indexes — picks limited'}
                      </span>
                    </Command.Item>
                  ))}
                </Command.List>
              </Command>
            )}
            {lockedComponent && (
              <div className="mt-1 text-xs text-muted-foreground">
                Phase 1 limits WHERE to one component — locked to{' '}
                <span className="font-mono" title={lockedComponent}>
                  {nameLabel(lockedComponent)}
                </span>
                .
              </div>
            )}
          </Step>

          {/* Step 2 — Field (indexed live; non-indexed greyed per design §4.3) */}
          {component && (
            <Step label="2. Field" done={!!field}>
              {field ? (
                <ChosenPill
                  value={field}
                  onChange={() => {
                    setField(null);
                    setOp(null);
                  }}
                />
              ) : (
                <Command filter={humpFilter} className="rounded border border-border">
                  <Command.Input
                    placeholder="Pick a field…"
                    className="w-full border-b border-border bg-transparent px-2 py-1 text-sm outline-none placeholder:text-muted-foreground"
                  />
                  <Command.List className="max-h-40 overflow-auto p-1">
                    {fields.length === 0 && (
                      <Command.Empty className="px-2 py-1 text-xs text-muted-foreground">Loading…</Command.Empty>
                    )}
                    {fields.map((f) => (
                      <Command.Item
                        key={f.name ?? ''}
                        value={f.name ?? ''}
                        disabled={!f.isIndexed}
                        onSelect={() => {
                          if (!f.isIndexed) return;
                          setField(f.name ?? '');
                          // Re-default operator to '==' on field change so the user doesn't keep a stale op.
                          setOp(operatorsForType(f.typeName)[0]);
                        }}
                        className={`flex cursor-pointer items-center justify-between gap-2 rounded px-2 py-1 font-mono text-sm aria-selected:bg-accent aria-selected:text-accent-foreground ${
                          f.isIndexed ? '' : 'cursor-not-allowed opacity-40'
                        }`}
                        title={f.isIndexed ? undefined : 'Not indexed — cannot be used in WHERE'}
                      >
                        <span>{f.name}</span>
                        <span className="shrink-0 text-xs text-muted-foreground">
                          {f.typeName} {f.isIndexed ? '· [Index]' : '· (no index)'}
                        </span>
                      </Command.Item>
                    ))}
                  </Command.List>
                </Command>
              )}
            </Step>
          )}

          {/* Step 3 — Operator (segmented buttons) */}
          {component && field && (
            <Step label="3. Operator" done={!!op}>
              <div className="flex gap-1">
                {ops.map((candidate) => (
                  <button
                    key={candidate}
                    type="button"
                    onClick={() => setOp(candidate)}
                    className={`rounded border px-2 py-0.5 font-mono text-sm ${
                      op === candidate
                        ? 'border-primary bg-primary text-primary-foreground'
                        : 'border-border bg-background hover:bg-muted'
                    }`}
                  >
                    {candidate}
                  </button>
                ))}
              </div>
            </Step>
          )}

          {/* Step 4 — Value (type-aware input) */}
          {component && field && op && (
            <Step label="4. Value" done={valueText.trim().length > 0}>
              <input
                type={valueIsNumeric ? 'number' : 'text'}
                value={valueText}
                onChange={(e) => setValueText(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && canConfirm) {
                    e.preventDefault();
                    confirm();
                  }
                }}
                autoFocus
                className="w-full rounded border border-border bg-background px-2 py-1 font-mono text-sm"
                placeholder={selectedField?.typeName ?? 'value'}
              />
            </Step>
          )}

          <div className="flex justify-end gap-2 border-t border-border pt-2">
            <button
              type="button"
              onClick={() => setOpen(false)}
              className="rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-muted"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={confirm}
              disabled={!canConfirm}
              className="rounded bg-primary px-3 py-1 text-xs font-semibold text-primary-foreground disabled:opacity-50"
            >
              Add
            </button>
          </div>
        </div>
      </PopoverContent>
    </Popover>
  );
}

function Step({ label, done, children }: { label: string; done: boolean; children: React.ReactNode }) {
  return (
    <div>
      <div className="mb-1 flex items-center gap-1 text-xs text-muted-foreground">
        <span className={done ? 'text-green-500' : ''}>{done ? '✓' : '○'}</span>
        {label}
      </div>
      {children}
    </div>
  );
}

function ChosenPill({
  value,
  onChange,
  disabled,
  title,
}: {
  value: string;
  onChange: () => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <div className="flex items-center gap-2 rounded border border-border bg-muted/30 px-2 py-1 font-mono text-sm">
      <span className="flex-1" title={title}>
        {value}
      </span>
      <button
        type="button"
        onClick={onChange}
        disabled={disabled}
        className="text-xs text-muted-foreground hover:text-foreground disabled:opacity-50"
        title={disabled ? 'Locked' : 'Change'}
      >
        change
      </button>
    </div>
  );
}

/**
 * Coerce the user's free-text value into a JSON-native scalar matching the field's type. The server's
 * `ExpressionTreeBuilder.CoerceValue` does its own type-coercion, so a string is always safe as a fallback — but
 * for numeric fields we narrow client-side so the cost chip sees the right type immediately.
 */
function parseValueForField(text: string, typeName: string | null | undefined): unknown {
  if (typeName && isNumericType(typeName)) {
    const n = Number(text);
    return Number.isFinite(n) ? n : text;
  }
  if (typeName === 'Boolean') {
    if (text.toLowerCase() === 'true') return true;
    if (text.toLowerCase() === 'false') return false;
    return text;
  }
  return text;
}
