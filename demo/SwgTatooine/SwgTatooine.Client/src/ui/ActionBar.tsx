import type { CSSProperties } from 'react';
import { ACTION_SLOTS } from '../app/action-bar';
import { levelTitle, xpPercent } from '../data/progress';
import { SpeedClass } from '../data/source';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

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
  if (!playing) {
    return null;
  }

  // The combat level left of the bar, and the way to the next one under it (character-level.md § 8).
  const level = vitals?.level ?? Number.NaN;
  const xpByte = (vitals?.xpFraction ?? Number.NaN) * 255;
  const xp = xpPercent(xpByte);
  return (
    <div className="gamebar ham" title="Health">
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
export function ActionBar() {
  const playing = useUi((s) => s.playing);
  const icons = useUi((s) => s.actionIcons);
  const stance = useUi((s) => s.stance);
  const used = useUi((s) => s.slotUsed);
  const pressSlot = useUi((s) => s.pressSlot);
  const riding = useStats((s) => s.stats?.gait === SpeedClass.swoop);
  if (!playing) {
    return null;
  }

  return (
    <div className="gamebar actionbar">
      <div className="slots">
        {ACTION_SLOTS.map((slot) => {
          const on = slot.kind === 'mount' ? riding : stance === slot.key;
          const icon = icons[slot.asset];
          return (
            <button
              key={slot.key}
              className={on ? 'slot on' : 'slot'}
              style={{ '--slot': slot.color } as CSSProperties}
              title={`${slot.label} (${String(slot.key)})`}
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
              {used?.key === slot.key ? <span key={used.seq} className="slot-flash" /> : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/** A fraction as a bar width, percent: 0 for none yet. */
function fill(fraction: number): number {
  return Number.isFinite(fraction) ? Math.max(0, Math.min(1, fraction)) * 100 : 0;
}
