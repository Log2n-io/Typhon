import { useState } from 'react';
import { EQUIP_SLOTS, equipped, mitigation, type InventoryItem } from '../data/items';
import { formatCredits } from '../data/wallet';
import { useCharacter, type CharacterTab } from '../state/character-store';
import { useInstallations } from '../state/installations-store';
import { useItems } from '../state/items-store';
import { useMap } from '../state/map-store';
import { useMarket } from '../state/market-store';
import { useSessionStore } from '../state/session-store';
import { useWallet } from '../state/wallet-store';
import { useWaypoints } from '../state/waypoints-store';
import { StructuresTab } from './InstallationsPanel';
import { ITEM_DRAG_TYPE, ItemSlot } from './InventoryWindow';
import { WindowFrame } from './WindowFrame';

const NO_ITEMS: readonly InventoryItem[] = [];

const TABS: readonly { readonly tab: CharacterTab; readonly label: string }[] = [
  { tab: 'overview', label: 'Overview' },
  { tab: 'equipment', label: 'Equipment' },
  { tab: 'structures', label: 'Structures' },
];

/**
 * The character window (client.md C39), toggled by <kbd>C</kbd> and the game toolbar; <kbd>L</kbd> opens it on Structures. Three tabs: who the
 * character is and how far it has come; what it wears, with the totals the server fights with; and the structures it has put down.
 */
export function CharacterWindow() {
  const open = useCharacter((s) => s.open);
  return open ? <Character /> : null;
}

function Character() {
  const tab = useCharacter((s) => s.tab);
  const setTab = useCharacter((s) => s.setTab);
  const close = useCharacter((s) => s.close);
  const name = useSessionStore((s) => s.grant?.name ?? '');
  return (
    <WindowFrame
      id="character"
      title={name.length > 0 ? name : 'Character'}
      onClose={close}
      ariaLabel="Character"
      className="character"
      minW={340}
      initial={(vw) => ({ x: Math.max(10, Math.round(vw / 2) - 420), y: 80, w: 520, h: 0 })}
    >
      <div className="bazaar-tabs" role="tablist">
        {TABS.map((t) => (
          <button
            key={t.tab}
            type="button"
            role="tab"
            aria-selected={tab === t.tab}
            className={tab === t.tab ? 'active' : ''}
            onClick={() => {
              setTab(t.tab);
            }}
          >
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'overview' ? <OverviewTab /> : tab === 'equipment' ? <EquipmentTab /> : <StructuresTab />}
    </WindowFrame>
  );
}

function OverviewTab() {
  const progress = useCharacter((s) => s.progress);
  const wallet = useWallet((s) => s.wallet);
  const player = useMap((s) => s.player);
  const installations = useInstallations((s) => s.list);
  const waypoints = useWaypoints((s) => s.list);
  const span = progress === null ? 0 : progress.xpNextLevel - progress.xpLevelStart;
  const into = progress === null ? 0 : progress.xp - progress.xpLevelStart;
  return (
    <div className="character-overview">
      {progress === null ? (
        <div className="hint">Reading your character…</div>
      ) : (
        <>
          <div className="character-level">
            <b>CL {String(progress.level)}</b>
            <div
              className="ham-xp character-xp"
              title={span > 0 ? `${String(into)} of ${String(span)} XP into the level` : 'The last level'}
            >
              <div
                className="ham-xp-fill"
                style={{ width: `${String(span > 0 ? Math.min(100, (100 * into) / span) : 100)}%` }}
              />
            </div>
            <span className="hint">
              {span > 0 ? `${String(span - into)} XP to CL ${String(progress.level + 1)}` : 'the last level'}
            </span>
          </div>
          <table className="character-table">
            <tbody>
              <tr>
                <td>XP</td>
                <td className="num">{progress.xp.toLocaleString('en-US')}</td>
                <td className="hint">
                  {progress.killXp.toLocaleString('en-US')} from kills · {progress.missionXp.toLocaleString('en-US')}{' '}
                  from missions
                </td>
              </tr>
              <tr>
                <td>Health</td>
                <td className="num">{progress.maxHealth.toLocaleString('en-US')}</td>
                <td className="hint">at full</td>
              </tr>
              <tr>
                <td>Credits</td>
                <td className="num">{wallet === null ? '…' : formatCredits(wallet.credits)}</td>
                <td />
              </tr>
              <tr>
                <td>Where</td>
                <td className="num">
                  {player === null ? '—' : `${String(Math.round(player.x))}, ${String(Math.round(player.z))}`}
                </td>
                <td className="hint">{player === null ? 'not on the planet' : ''}</td>
              </tr>
              <tr>
                <td>Lots</td>
                <td className="num">
                  {installations === null
                    ? '—'
                    : `${String(installations.lots)} / ${String(installations.lotsAllowed)}`}
                </td>
                <td className="hint">Structures tab</td>
              </tr>
              <tr>
                <td>Waypoints</td>
                <td className="num">{waypoints === null ? '—' : String(waypoints.length)}</td>
                <td className="hint">the waypoint pane&apos;s title opens them</td>
              </tr>
            </tbody>
          </table>
          <Combat />
        </>
      )}
    </div>
  );
}

/**
 * What the character fights with, as the server computes it (`SimBridge.ComputeGear`): its weapon's hit, pace and reach, what its armour takes off a
 * hit by damage type, and its wear. Armour has no drawback in this game: encumbrance is a D3 line (crafting-catalog.md), not built.
 */
function Combat() {
  const items = useItems((s) => s.snapshot?.items ?? NO_ITEMS);
  const worn = equipped(items);
  const weapon = worn.get(101)?.stats;
  const { kinetic, energy } = mitigation(worn);
  const pieces = EQUIP_SLOTS.filter(({ slot }) => slot !== 101 && worn.has(slot)).length;
  const tired = [...worn.values()].filter(
    (i) =>
      i.stats !== undefined && (i.stats.broken === true || i.stats.condition < i.stats.maxCondition * REPAIR_BELOW),
  ).length;
  const perSecond =
    weapon === undefined || (weapon.delayMs ?? 0) <= 0 ? 0 : (weapon.damage ?? 0) / ((weapon.delayMs ?? 1) / 1000);
  return (
    <>
      <h3>Combat</h3>
      <table className="character-table">
        <tbody>
          <tr>
            <td>Weapon</td>
            <td className="num">{weapon === undefined ? 'unarmed' : `${String(weapon.damage ?? 0)} a hit`}</td>
            <td className="hint">
              {weapon === undefined
                ? 'the fist'
                : `every ${((weapon.delayMs ?? 0) / 1000).toFixed(2)} s (${perSecond.toFixed(0)} a second), reach ${((weapon.rangeDm ?? 0) / 10).toFixed(0)} m`}
            </td>
          </tr>
          <tr>
            <td>Kinetic</td>
            <td className="num">−{(kinetic / 10).toFixed(1)} %</td>
            <td className="hint">off every kinetic hit taken: beasts, blades, knucklers</td>
          </tr>
          <tr>
            <td>Energy</td>
            <td className="num">−{(energy / 10).toFixed(1)} %</td>
            <td className="hint">off every energy hit taken: blasters (camp soldiers, players); at most 75 % each</td>
          </tr>
          <tr>
            <td>Armour</td>
            <td className="num">{`${String(pieces)} / 6`}</td>
            <td className="hint">
              {tired > 0 ? `${String(tired)} worn down: repair at a crafting station · ` : ''}no drawback (no
              encumbrance in this game yet)
            </td>
          </tr>
        </tbody>
      </table>
    </>
  );
}

/** A condition below this share of its maximum asks for a repair. */
const REPAIR_BELOW = 0.25;

function EquipmentTab() {
  const items = useItems((s) => s.snapshot?.items ?? NO_ITEMS);
  const icons = useItems((s) => s.icons);
  const notice = useMarket((s) => s.rowNotices.gear);
  const [over, setOver] = useState<number | null>(null);
  const worn = equipped(items);
  const weapon = worn.get(101);
  const { kinetic, energy } = mitigation(worn);

  return (
    <div className="character-equipment">
      <div className="character-slots">
        {EQUIP_SLOTS.map(({ slot, label }) => {
          const item = worn.get(slot);
          const tired =
            item?.stats !== undefined &&
            (item.stats.broken === true || item.stats.condition < item.stats.maxCondition * REPAIR_BELOW);
          return (
            <div
              key={slot}
              className={over === slot ? 'character-slot drop' : 'character-slot'}
              data-testid={`equip-slot-${String(slot)}`}
              onDragOver={(e) => {
                if (e.dataTransfer.types.includes(ITEM_DRAG_TYPE)) {
                  e.preventDefault();
                  setOver(slot);
                }
              }}
              onDragLeave={() => {
                setOver(null);
              }}
              onDrop={(e) => {
                e.preventDefault();
                setOver(null);
                const id = e.dataTransfer.getData(ITEM_DRAG_TYPE);
                const dropped = items.find((i) => i.id === id);
                // The server puts it where it is worn, whatever slot it was dropped on; dropped on another, it says so first.
                if (dropped?.stats?.wears !== slot) {
                  useMarket.getState().settle('gear');
                  return;
                }

                useMarket.getState().equip(id);
              }}
            >
              <span className="character-slot-label">{label}</span>
              {item === undefined ? (
                <div
                  className="inventory-slot equip-empty"
                  title={`${label}: drag an item here, or click one in the inventory`}
                >
                  <span className="inventory-initials">{label[0]}</span>
                </div>
              ) : (
                <ItemSlot
                  item={item}
                  icon={icons[item.item]}
                  picked={false}
                  onPick={() => {
                    useMarket.getState().unequip(slot);
                  }}
                />
              )}
              {item?.stats === undefined ? null : (
                <span className={tired ? 'character-condition tired' : 'character-condition'}>
                  {item.stats.broken === true
                    ? 'broken'
                    : `${String(Math.round((100 * item.stats.condition) / Math.max(1, item.stats.maxCondition)))} %`}
                </span>
              )}
            </div>
          );
        })}
      </div>
      {notice === undefined ? null : (
        <div className="hint" role="alert">
          {notice}
        </div>
      )}
      <table className="character-table">
        <tbody>
          <tr>
            <td>Weapon</td>
            <td colSpan={2}>
              {weapon?.stats === undefined
                ? 'unarmed'
                : `${weapon.name} · ${String(weapon.stats.damage ?? 0)} a hit · ${((weapon.stats.delayMs ?? 0) / 1000).toFixed(2)} s · ${((weapon.stats.rangeDm ?? 0) / 10).toFixed(0)} m`}
            </td>
          </tr>
          <tr>
            <td>Armour</td>
            <td colSpan={2}>
              kinetic {(kinetic / 10).toFixed(1)} % · energy {(energy / 10).toFixed(1)} % of a hit taken off (at most 75
              %)
            </td>
          </tr>
        </tbody>
      </table>
      <div className="hint">
        A click on a worn piece takes it off. A piece under a quarter of its condition shows red: repair it at a
        crafting station.
      </div>
    </div>
  );
}
