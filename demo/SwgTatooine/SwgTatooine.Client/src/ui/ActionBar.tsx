import { useState, type CSSProperties } from 'react';
import { ACTION_SLOTS } from '../app/action-bar';
import { equipped } from '../data/items';
import { STIM_COOLDOWN_MS, stimDoses } from '../data/potions';
import { levelTitle, xpPercent } from '../data/progress';
import { SpeedClass } from '../data/source';
import { useItems } from '../state/items-store';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';
import { useBar } from './WindowFrame';

/**
 * The HAM pools, top left beside the toolbar.
 *
 * <b>Only for a session with an avatar</b>, like the action bar: the god camera has no pools to show.
 *
 * <b>One pool, Health.</b> HAM is SWG's health, action and mind; this game has health only, so the panel shows the one it
 * has rather than two bars nothing fills.
 */
export function HamBar() {
  const playing = useUi((s) => s.playing);
  const vitals = useStats((s) => s.stats?.vitals ?? null);
  const bar = useBar('ham');
  if (!playing) {
    return null;
  }

  // The combat level left of the bar, and the way to the next one under it (character-level.md § 8).
  const level = vitals?.level ?? Number.NaN;
  const xpByte = (vitals?.xpFraction ?? Number.NaN) * 255;
  const xp = xpPercent(xpByte);
  return (
    <div className="gamebar ham" title="Health" style={bar.style}>
      {bar.grip}
      <span className="ham-level" title={levelTitle(level, xpByte)}>
        {Number.isFinite(level) && level > 0 ? `CL ${level}` : 'CL —'}
      </span>
      <span className="ham-label">H</span>
      <div className="ham-pools">
        <div className="ham-bar health">
          <div className="ham-fill" style={{ width: `${fill(vitals?.fraction ?? Number.NaN)}%` }} />
          <span className="ham-text">
            {vitals === null || !Number.isFinite(vitals.health) ? '—' : vitals.health.toLocaleString()}
          </span>
        </div>
        <div className="ham-xp" title={levelTitle(level, xpByte)}>
          <div className="ham-xp-fill" style={{ width: `${Number.isFinite(xp) ? xp : 0}%` }} />
        </div>
      </div>
    </div>
  );
}

/**
 * The action bar, bottom left beside the chat: one slot per top-row digit.
 *
 * <b>Only for a session with an avatar.</b> The god camera has no body to mount or arm.
 */
/** The fist's slot: always usable, whatever is worn. */
const UNARMED_SLOT = 2;

export function ActionBar() {
  const playing = useUi((s) => s.playing);
  const bar = useBar('action');
  const icons = useUi((s) => s.actionIcons);
  const stance = useUi((s) => s.stance);
  const used = useUi((s) => s.slotUsed);
  const pressSlot = useUi((s) => s.pressSlot);
  const riding = useStats((s) => s.stats?.gait === SpeedClass.swoop);
  const inventory = useItems((s) => s.snapshot);
  const stimReadyAt = useUi((s) => s.stimReadyAt);
  if (!playing) {
    return null;
  }

  // The stims held, in doses (crafting-catalog.md § 5.4): the slot's count; none, and it is off.
  const doses = inventory === null ? null : stimDoses(inventory.items);

  // A stance fights with what is worn (crafting-build.md C1): the fist always, the others only with their weapon on. The server refuses the rest; the
  // bar says so first. Before the first inventory read every slot is offered.
  const weapon = inventory === null ? null : (equipped(inventory.items).get(101)?.item ?? '');
  const armed = (asset: string, key: number) => weapon === null || key === UNARMED_SLOT || asset === weapon;

  return (
    <div className="gamebar actionbar" style={bar.style}>
      {bar.grip}
      <div className="slots">
        {ACTION_SLOTS.map((slot) => {
          const drink = slot.kind === 'consume';
          const on = slot.kind === 'mount' ? riding : !drink && stance === slot.key;
          const usable = slot.kind === 'mount' || (drink ? doses === null || doses > 0 : armed(slot.asset, slot.key));
          const icon = icons[slot.asset];
          const why = drink ? 'no stim held — craft or buy one' : 'equip one first';
          return (
            <button
              key={slot.key}
              className={on ? 'slot on' : usable ? 'slot' : 'slot off'}
              style={{ '--slot': slot.color } as CSSProperties}
              disabled={!usable && !on}
              title={usable ? `${slot.label} (${String(slot.key)})` : `${slot.label} — ${why}`}
              // Not focusable by the mouse: a focused button would take Space and Enter from the game.
              onMouseDown={(e) => {
                e.preventDefault();
              }}
              onClick={() => {
                pressSlot(slot.key, false);
              }}
            >
              {icon === undefined ? null : <img className="slot-icon" src={icon} alt="" draggable={false} />}
              <span className="slot-key">{slot.key}</span>
              {drink && doses !== null ? <span className="slot-count">{doses}</span> : null}
              {drink ? <StimCooldown key={stimReadyAt} readyAt={stimReadyAt} /> : null}
              {used?.key === slot.key ? <span key={used.seq} className="slot-flash" /> : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/**
 * The stim slot's wait: a shade that drains from the top over the cooldown, started by the heal itself. One CSS animation, remounted by the heal's time and
 * started as far into it as the wait already is — a component that ticks would re-render the bar sixty times a second.
 */
function StimCooldown({ readyAt }: { readonly readyAt: number }) {
  // When it mounted: remounted by each heal (its key), so this is how far into the wait the bar was drawn.
  const [mountedAt] = useState(() => performance.now());
  const left = readyAt - mountedAt;
  if (readyAt === 0 || left <= 0) {
    return null;
  }

  return (
    <span
      className="slot-cooldown"
      style={{
        animationDuration: `${String(STIM_COOLDOWN_MS)}ms`,
        animationDelay: `${String(-(STIM_COOLDOWN_MS - left))}ms`,
      }}
    />
  );
}

/** A fraction as a bar width, percent: 0 for none yet. */
function fill(fraction: number): number {
  return Number.isFinite(fraction) ? Math.max(0, Math.min(1, fraction)) * 100 : 0;
}
