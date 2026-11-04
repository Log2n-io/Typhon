import { useState } from 'react';

import { EXTRACTOR_FAMILIES, EXTRACTOR_TEMPLATE, paidText, stateText, type Installation } from '../data/installations';
import type { InventoryItem } from '../data/items';
import { useInstallations } from '../state/installations-store';
import { useItems } from '../state/items-store';
import { useMap } from '../state/map-store';
import { useMarket } from '../state/market-store';
import { useWaypoints } from '../state/waypoints-store';

const NO_ITEMS: readonly InventoryItem[] = [];

/** The upkeep the buttons buy, hours. */
const PAY_HOURS = [1, 8] as const;

/**
 * The character window's Structures tab (client.md C39, crafting-build.md § 10; <kbd>L</kbd>): the lots used of the character's 10 and the upkeep its
 * installations cost an hour; each extractor — what it draws, what its hopper holds, whether it runs and how long its upkeep lasts — with Collect, Pay
 * and Pick up, which the server takes within 30 m of it, Pin (its waypoint, C38) and Show on map; and its extractor kits, placed on the lot it stands
 * in, outside the cities.
 */
export function StructuresTab() {
  const list = useInstallations((s) => s.list);
  const notice = useMarket((s) => s.rowNotices.install);
  const items = useItems((s) => s.snapshot?.items ?? NO_ITEMS);
  const kits = items.filter((i) => i.template === EXTRACTOR_TEMPLATE && i.slot <= 100);
  const running = list?.installations.filter((i) => i.state !== 3).length ?? 0;
  return (
    <div className="installations">
      {list === null ? null : (
        <div className="hint">
          Lots {String(list.lots)} / {String(list.lotsAllowed)} · upkeep {String(running * list.upkeepPerHour)} cr an
          hour while they run
        </div>
      )}
      {notice === undefined ? null : (
        <div className="hint" role="alert">
          {notice}
        </div>
      )}
      {list === null ? (
        <div className="hint">Reading your installations…</div>
      ) : list.installations.length === 0 ? (
        <div className="hint">No installation yet. The vendor sells an extractor kit; place it out of town.</div>
      ) : (
        list.installations.map((i) => <Row key={i.id} item={i} upkeepPerHour={list.upkeepPerHour} />)
      )}
      {kits.length === 0 ? null : <Kits kits={kits} />}
    </div>
  );
}

function Row({ item, upkeepPerHour }: { readonly item: Installation; readonly upkeepPerHour: number }) {
  const pending = useMarket((s) => s.pending.includes('install'));
  const { collect, pay, pickUp } = useInstallations.getState();
  const [confirming, setConfirming] = useState(false);
  // Its pin (C38): the player's waypoint following it, if it has one.
  const pin = useWaypoints((s) => s.list?.find((w) => w.installation === item.id) ?? null);
  return (
    <div className="installation-row" data-testid={`installation-${item.id}`}>
      <div>
        <b>Extractor · {item.familyName}</b> · {String(item.held)} / {String(item.hopper)} · {stateText(item.state)}
      </div>
      <div className="hint">
        {String(item.rate)} an hour · upkeep left {paidText(item.paidSeconds)} · at {Math.round(item.x)},{' '}
        {Math.round(item.z)}
      </div>
      <div className="row">
        <button
          type="button"
          disabled={pending || item.held === 0}
          onClick={() => {
            collect(item.id);
          }}
        >
          Collect {String(item.held)}
        </button>
        {PAY_HOURS.map((h) => (
          <button
            key={h}
            type="button"
            disabled={pending}
            onClick={() => {
              pay(item.id, h * upkeepPerHour);
            }}
          >
            Pay {String(h)} h ({String(h * upkeepPerHour)} cr)
          </button>
        ))}
        <button
          type="button"
          disabled={pending || item.held > 0}
          title={item.held > 0 ? 'Collect its hopper first' : 'Back into its kit; what upkeep is left is not refunded'}
          onClick={() => {
            if (confirming) {
              setConfirming(false);
              pickUp(item.id);
            } else {
              setConfirming(true);
            }
          }}
        >
          {confirming ? 'Pick up — sure?' : 'Pick up'}
        </button>
        <button
          type="button"
          title={pin === null ? 'A waypoint on it, in the waypoint pane and on the map' : 'Delete its waypoint'}
          onClick={() => {
            if (pin === null) {
              useWaypoints.getState().create(`Extractor · ${item.familyName}`, item.x, item.z, 0, item.id);
            } else {
              useWaypoints.getState().remove(pin.id);
            }
          }}
        >
          {pin === null ? '📌 Pin' : '📌 Unpin'}
        </button>
        <button
          type="button"
          title="Show it on the map"
          onClick={() => {
            useMap.getState().showAt(item.x, item.z);
          }}
        >
          🗺
        </button>
      </div>
    </div>
  );
}

function Kits({ kits }: { readonly kits: readonly InventoryItem[] }) {
  const pending = useMarket((s) => s.pending.includes('install'));
  const place = useInstallations((s) => s.place);
  const [family, setFamily] = useState<number>(EXTRACTOR_FAMILIES[0].template);
  return (
    <div className="installation-kits">
      <div className="hud-title">Place an extractor</div>
      <div className="hint">On the 64 m lot you stand in, out of town. It makes nothing until its upkeep is paid.</div>
      {kits.map((k) => (
        <div key={k.id} className="row">
          <span>
            Kit · {String(k.stats?.rate ?? 0)} an hour, hopper {String(k.stats?.hopper ?? 0)}
          </span>
          <select
            aria-label="What it draws"
            value={family}
            onChange={(e) => {
              setFamily(Number(e.target.value));
            }}
          >
            {EXTRACTOR_FAMILIES.map((f) => (
              <option key={f.template} value={f.template}>
                {f.name}
              </option>
            ))}
          </select>
          <button
            type="button"
            disabled={pending}
            onClick={() => {
              place(k.id, family);
            }}
          >
            Place here
          </button>
        </div>
      ))}
    </div>
  );
}
