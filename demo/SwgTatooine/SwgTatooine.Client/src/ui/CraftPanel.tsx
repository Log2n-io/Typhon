import { useEffect } from 'react';

import { ceiling, lineValue, SCALE, type CraftInput, type Schematic, type SchematicBook } from '../data/craft';
import { mkNumeral, type InventoryItem } from '../data/items';
import { useCraft } from '../state/craft-store';
import { useItems } from '../state/items-store';
import { useMarket } from '../state/market-store';

/** The segments an armour piece's slot takes: padded or composite (crafting-catalog.md § 13.6). */
const PADDED_SEGMENT = 29;
const COMPOSITE_SEGMENT = 30;

/** An add-on by the schematic's kind (§ 13.3). */
const ADDONS: Readonly<Record<string, number>> = { weapon: 16, armor: 17, potion: 19 };

/**
 * The crafting panel (crafting-build.md C2, crafting-catalog.md § 3.4), toggled by <kbd>C</kbd>. Before a craft: a schematic, its inputs picked slot by
 * slot from the inventory, and each line's <b>ceiling</b> computed as the server will (`data/craft.ts`) — so poor inputs show before the first click.
 * During one: each line's value and ceiling, a point at a time, an optional add-on, and the finish. The server rolls; the panel shows.
 */
export function CraftPanel() {
  const open = useCraft((s) => s.open);
  const book = useCraft((s) => s.book);
  const craft = useCraft((s) => s.craft);
  const close = useCraft((s) => s.close);
  const notice = useMarket((s) => s.rowNotices.craft);
  if (!open) {
    return null;
  }

  return (
    <div className="panel bazaar craft" role="dialog" aria-label="Crafting">
      <div className="missions-head">
        <span className="hud-title">Crafting</span>
        <button type="button" className="missions-close" onClick={close} aria-label="Close">
          ✕
        </button>
      </div>
      {notice === undefined ? null : (
        <div className="hint" role="alert">
          {notice}
        </div>
      )}
      {book === null ? (
        <div className="hint">Reading the schematics…</div>
      ) : craft === null ? (
        <Pick book={book} />
      ) : (
        <UnderWay book={book} />
      )}
    </div>
  );
}

function unitsOf(items: readonly InventoryItem[], template: number, mk: number): number {
  return items
    .filter((i) => i.template === template && i.mk === mk && i.stats === undefined)
    .reduce((n, i) => n + i.count, 0);
}

/** The inputs as the ceilings read them, from the Mk and the parts picked (§ 5.3: a slot's parts average their grades and biases). */
function inputsOf(
  s: Schematic,
  items: readonly InventoryItem[],
  mks: readonly number[],
  parts: readonly string[],
): CraftInput[] {
  let nextMk = 0;
  let nextPart = 0;
  return s.slots.map((slot) => {
    if (slot.kind !== 'part') {
      return { template: slot.template, mk: mks[nextMk++] ?? 0, grade: 0, biasKinetic: 0, biasEnergy: 0 };
    }

    let grade = 0;
    let kinetic = 0;
    let energy = 0;
    for (let n = 0; n < slot.count; n++) {
      const item = items.find((i) => i.id === parts[nextPart]);
      nextPart++;
      grade += (item?.stats?.grade ?? 0) / 1000;
      const composite = item?.template === COMPOSITE_SEGMENT;
      kinetic += composite ? 0.6 : 1.0;
      energy += composite ? 1.0 : 0.6;
    }

    return {
      template: slot.template,
      mk: 0,
      grade: grade / slot.count,
      biasKinetic: kinetic / slot.count,
      biasEnergy: energy / slot.count,
    };
  });
}

/** Each slot's first index in the start command's lists: the Mk list for a material or a core, the parts list for parts. */
function slotIndices(s: Schematic): number[] {
  let mk = 0;
  let part = 0;
  return s.slots.map((slot) => {
    if (slot.kind === 'part') {
      const first = part;
      part += slot.count;
      return first;
    }

    return mk++;
  });
}

function Pick({ book }: { readonly book: SchematicBook }) {
  const selected = useCraft((s) => s.selected);
  const select = useCraft((s) => s.select);
  const mks = useCraft((s) => s.mks);
  const parts = useCraft((s) => s.parts);
  const setMk = useCraft((s) => s.setMk);
  const setPart = useCraft((s) => s.setPart);
  const start = useCraft((s) => s.start);
  const items = useItems((s) => s.snapshot?.items ?? []);
  const s = book.schematics.find((x) => x.id === selected);
  const setChoice = useCraft((x) => x.setChoice);

  // A new schematic picks the best of what is held: the highest Mk that covers each material slot, the best-graded parts.
  useEffect(() => {
    if (s === undefined) {
      return;
    }

    const pickedMks: number[] = [];
    const pickedParts: string[] = [];
    for (const slot of s.slots) {
      if (slot.kind === 'part') {
        const fits = items
          .filter((i) =>
            slot.template === 0
              ? i.template === PADDED_SEGMENT || i.template === COMPOSITE_SEGMENT
              : i.template === slot.template,
          )
          .sort((a, b) => (b.stats?.grade ?? 0) - (a.stats?.grade ?? 0));
        for (let n = 0; n < slot.count; n++) {
          pickedParts.push(fits[n]?.id ?? '0');
        }
      } else {
        let best = 0;
        for (let mk = 5; mk >= 1 && best === 0; mk--) {
          best = unitsOf(items, slot.template, mk) >= slot.count ? mk : 0;
        }

        pickedMks.push(slot.kind === 'bulk' ? 0 : best);
      }
    }

    setChoice(pickedMks, pickedParts);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- a pick per schematic, not per inventory read
  }, [selected]);

  const inputs = s === undefined ? [] : inputsOf(s, items, mks, parts);
  // Where each slot's choice sits in the command's two lists: material and core slots number the Mk, part slots the ids.
  const at = s === undefined ? [] : slotIndices(s);
  return (
    <div className="craft-pick">
      <select
        value={selected ?? ''}
        onChange={(e) => {
          select(e.target.value === '' ? null : Number(e.target.value));
        }}
        aria-label="Schematic"
      >
        <option value="">Choose a schematic…</option>
        {book.schematics.map((x) => (
          <option key={x.id} value={x.id}>
            {x.name} ({x.kind})
          </option>
        ))}
      </select>
      {s === undefined ? null : (
        <>
          <div className="craft-slots">
            {s.slots.map((slot, k) => {
              if (slot.kind === 'part') {
                const first = at[k];
                const fits = items.filter((i) =>
                  slot.template === 0
                    ? i.template === PADDED_SEGMENT || i.template === COMPOSITE_SEGMENT
                    : i.template === slot.template,
                );
                return (
                  <div key={k} className="craft-slot">
                    <span>{slot.template === 0 ? 'Segment' : slot.item}</span>
                    {Array.from({ length: slot.count }, (_, n) => (
                      <select
                        key={n}
                        value={parts[first + n] ?? '0'}
                        onChange={(e) => {
                          setPart(first + n, e.target.value);
                        }}
                      >
                        <option value="0">—</option>
                        {fits.map((i) => (
                          <option key={i.id} value={i.id}>
                            {i.name} · grade {String(Math.round((i.stats?.grade ?? 0) / 10))} %
                          </option>
                        ))}
                      </select>
                    ))}
                  </div>
                );
              }

              const index = at[k];
              return (
                <div key={k} className="craft-slot">
                  <span>
                    {slot.item} ×{slot.count}{' '}
                    {slot.kind === 'bulk' ? '(bulk)' : slot.kind === 'core' ? '(core)' : '(quality)'}
                  </span>
                  <select
                    value={mks[index] ?? 0}
                    onChange={(e) => {
                      setMk(index, Number(e.target.value));
                    }}
                  >
                    {slot.kind === 'bulk' ? <option value={0}>any</option> : null}
                    {[1, 2, 3, 4, 5].map((mk) => (
                      <option key={mk} value={mk} disabled={unitsOf(items, slot.template, mk) < slot.count}>
                        Mk {mkNumeral(mk)} ({unitsOf(items, slot.template, mk)})
                      </option>
                    ))}
                  </select>
                </div>
              );
            })}
          </div>
          <Lines book={book} s={s} q={s.lines.map((line) => ceiling(book, s, line, inputs))} f={null} addonMk={0} />
          <button type="button" onClick={start}>
            Start
          </button>
        </>
      )}
    </div>
  );
}

function UnderWay({ book }: { readonly book: SchematicBook }) {
  const craft = useCraft((s) => s.craft);
  const point = useCraft((s) => s.point);
  const enhance = useCraft((s) => s.enhance);
  const finish = useCraft((s) => s.finish);
  const items = useItems((s) => s.snapshot?.items ?? []);
  const s = craft === null ? undefined : book.schematics.find((x) => x.id === craft.schematic);
  if (craft === null || s === undefined) {
    return null;
  }

  const addon = ADDONS[s.kind] as number | undefined;
  return (
    <div className="craft-underway">
      <div className="hud-title">
        {s.name}
        {craft.mk > 0 ? ` · Mk ${mkNumeral(craft.mk)}` : ''} · {String(craft.pointsLeft)} point(s) left
      </div>
      <Lines
        book={book}
        s={s}
        q={craft.lines.map((l) => l.q)}
        f={craft.lines.map((l) => l.f)}
        addonMk={craft.addonMk}
        onPoint={craft.pointsLeft > 0 ? point : undefined}
      />
      {addon === undefined || craft.addonMk > 0 ? null : (
        <div className="craft-slot">
          <span>Add-on</span>
          {[1, 2, 3, 4, 5]
            .filter((mk) => unitsOf(items, addon, mk) > 0)
            .map((mk) => (
              <button
                key={mk}
                type="button"
                onClick={() => {
                  enhance(mk);
                }}
              >
                Mk {mkNumeral(mk)}
              </button>
            ))}
        </div>
      )}
      <button type="button" onClick={finish}>
        Finish
      </button>
    </div>
  );
}

/** Each line: its value now (or its start, before any roll) against its ceiling's best. */
function Lines({
  s,
  q,
  f,
  addonMk,
  onPoint,
}: {
  readonly book: SchematicBook;
  readonly s: Schematic;
  readonly q: readonly number[];
  readonly f: readonly number[] | null;
  readonly addonMk: number;
  readonly onPoint?: ((line: number) => void) | undefined;
}) {
  return (
    <div className="craft-lines">
      {s.lines.map((line, i) => {
        const best = lineValue(line, q[i], SCALE, addonMk);
        const now = f === null ? null : lineValue(line, q[i], f[i], addonMk);
        return (
          <div key={line.name} className="craft-line" data-testid={`craft-line-${line.name}`}>
            <span>{line.name}</span>
            <span>
              {now === null ? '' : `${now.toFixed(1)} · `}ceiling {best.toFixed(1)} (
              {String(Math.round((100 * q[i]) / SCALE))} %)
            </span>
            {onPoint === undefined ? null : (
              <button
                type="button"
                onClick={() => {
                  onPoint(i);
                }}
              >
                +1
              </button>
            )}
          </div>
        );
      })}
    </div>
  );
}
