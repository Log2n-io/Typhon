import { useState } from 'react';
import { NAV_KINDS } from '../app/nav-finder';
import { StructureKind } from '../data/swg-schema';
import {
  colorOf,
  cssColor,
  GROUP_COLORS,
  KIND_INSTALLATION,
  NAMED_COLORS,
  WAYPOINT_NAME_MAX_BYTES,
} from '../data/waypoints';
import { useMap } from '../state/map-store';
import { useUi } from '../state/ui-store';
import { useWaypoints } from '../state/waypoints-store';
import { useWindows } from '../state/windows-store';
import { formatDistance } from './NavPane';
import { WindowFrame } from './WindowFrame';

/**
 * The Waypoints window (client.md C38), opened from the waypoint pane's title: what the pane shows — each kind of city structure, the missions, the
 * player's pins and its own waypoints, by a switch each — and the player's own waypoints: made where it stands, made active, shown on the map,
 * walked to, deleted. Each in its kind's colour or the one its owner chose.
 */
export function WaypointsWindow() {
  const open = useWaypoints((s) => s.open);
  return open ? <Waypoints /> : null;
}

function Waypoints() {
  const close = useWaypoints((s) => s.close);
  const list = useWaypoints((s) => s.list);
  const max = useWaypoints((s) => s.max);
  const hidden = useWindows((s) => s.layout.navHidden);
  const player = useMap((s) => s.player);
  const [name, setName] = useState('');
  const [color, setColor] = useState(0);
  const waypoints = useWaypoints.getState();

  const toggle = (key: string, label: string, colour: string) => (
    <label className="check" key={key} style={{ color: colour }}>
      <input
        type="checkbox"
        checked={!hidden.includes(key)}
        onChange={(e) => {
          useWindows.getState().setNavShown(key, e.target.checked);
        }}
      />{' '}
      {label}
    </label>
  );

  return (
    <WindowFrame
      id="waypoints"
      title="Waypoints"
      extra={<span className="missions-held">{list === null ? '…' : `${String(list.length)}/${String(max)}`}</span>}
      onClose={close}
      ariaLabel="Waypoints"
      className="waypoints"
      minW={300}
      initial={(vw, vh) => ({ x: vw - 702, y: Math.max(10, vh - 620), w: 380, h: 0 })}
    >
      <section>
        <h3>The pane shows</h3>
        <div className="waypoint-filter">
          {NAV_KINDS.map((kind) => toggle(`city:${String(kind)}`, kindName(kind), GROUP_COLORS.city))}
          {toggle('mission', 'Missions', GROUP_COLORS.mission)}
          {toggle('structure', 'Your structures', GROUP_COLORS.structure)}
          {toggle('custom', 'Your waypoints', GROUP_COLORS.custom)}
        </div>
      </section>
      <section>
        <h3>Yours</h3>
        <form
          className="row"
          onSubmit={(e) => {
            e.preventDefault();
            if (player !== null && name.trim().length > 0 && waypoints.create(name.trim(), player.x, player.z, color)) {
              setName('');
            }
          }}
        >
          <input
            value={name}
            maxLength={WAYPOINT_NAME_MAX_BYTES}
            placeholder="Name — made where you stand"
            onChange={(e) => {
              setName(e.target.value);
            }}
          />
          <select
            value={color}
            onChange={(e) => {
              setColor(Number(e.target.value));
            }}
            style={{ color: color === 0 ? GROUP_COLORS.custom : cssColor(color) }}
          >
            <option value={0}>yellow</option>
            {Object.entries(NAMED_COLORS).map(([n, rgb]) => (
              <option key={n} value={rgb} style={{ color: cssColor(rgb) }}>
                {n}
              </option>
            ))}
          </select>
          <button
            type="submit"
            disabled={player === null || name.trim().length === 0}
            title={player === null ? 'Stand on the planet' : ''}
          >
            Make
          </button>
        </form>
        <div className="hint">Or hold on the map, or type /way &lt;name&gt; in chat.</div>
        {list === null ? (
          <div className="hint">Reading your waypoints…</div>
        ) : list.length === 0 ? (
          <div className="hint">None yet.</div>
        ) : (
          list.map((w) => (
            <div
              className={w.active ? 'waypoint-row nav-active' : 'waypoint-row'}
              key={w.id}
              style={{ color: colorOf(w) }}
            >
              <span
                className="waypoint-name"
                title={w.kind === KIND_INSTALLATION ? 'Your installation' : 'Your waypoint'}
              >
                {w.active ? '▶ ' : ''}
                {w.name}
              </span>
              <span className="waypoint-where">
                {player === null
                  ? `${String(Math.round(w.x))}, ${String(Math.round(w.z))}`
                  : formatDistance(Math.hypot(w.x - player.x, w.z - player.z))}
              </span>
              <button
                type="button"
                title={w.active ? 'Make none active' : 'Make it the active one'}
                onClick={() => {
                  waypoints.activate(w.active ? '0' : w.id);
                }}
              >
                {w.active ? '★' : '☆'}
              </button>
              <button
                type="button"
                title="Show it on the map"
                onClick={() => {
                  useMap.getState().showAt(w.x, w.z);
                }}
              >
                🗺
              </button>
              <button
                type="button"
                title="Walk there"
                onClick={() => {
                  useUi.getState().walkTo(w.x, w.z);
                }}
              >
                ➜
              </button>
              <button
                type="button"
                title="Delete it"
                onClick={() => {
                  waypoints.remove(w.id);
                }}
              >
                ✕
              </button>
            </div>
          ))
        )}
      </section>
    </WindowFrame>
  );
}

/** A kind of city structure as the filter names it: what it is, not which one is nearest. */
function kindName(kind: number): string {
  switch (kind) {
    case StructureKind.Shuttleport:
      return 'Shuttleports';
    case StructureKind.Bazaar:
      return 'Bazaars';
    case StructureKind.MissionTerminal:
      return 'Mission terminals';
    case StructureKind.CraftingStation:
      return 'Crafting stations';
    default:
      return 'Structures';
  }
}
