import { Fragment } from 'react';
import { realmPanelView, type RealmPanelRow } from '../data/realm-inventory';
import { useRealms } from '../state/realm-store';
import { useStats } from '../state/stats-store';
import { useUi } from '../state/ui-store';

/**
 * What every realm is doing, and what it costs when nobody is in it (CLI3D-11).
 *
 * <b>The one place the realms design's central claim is visible.</b> "An unobserved realm costs zero" was asserted in a
 * document and drawn nowhere: this is where a viewer watches a cantina go to sleep after the last bot walks out, and
 * where "1 197 asleep" is a number on screen rather than a sentence in a design.
 *
 * <b>The rows are the awake realms, and that is the feature.</b> A shipping map has around six hundred enterable
 * buildings per planet, so the server lists planets, space, live dungeons and the interiors that are NOT dormant, and
 * counts the rest. A row appearing when somebody walks through a door and falling off the list once the room has been
 * empty for the sleep delay is the demonstration.
 *
 * Every decision is in {@link realmPanelView}; nothing here is testable and nothing here needs to be. Collapsed by
 * default: it is the most detailed thing on the panel and the toolbar is not where a viewer starts.
 */
/**
 * What a row's tooltip says: where it goes, how fast it runs, and when it will fall asleep.
 *
 * The sleep delay is the other half of the dormancy story — a room that is awake now and sleeps in three seconds is a
 * different fact from one that never sleeps — and it costs no column to say so here.
 */
function describe(realm: RealmPanelRow): string {
  const rate = realm.divisor > 1 ? ` · simulated at 1/${realm.divisor} rate` : '';
  const sleep =
    realm.sleepAfterTicks > 0 ? ` · sleeps after ${realm.sleepAfterTicks} unobserved ticks` : ' · never sleeps';
  return `Look at realm ${realm.id}${rate}${sleep}`;
}

export function RealmPanel() {
  const inventory = useRealms((s) => s.inventory);
  const open = useRealms((s) => s.open);
  const setOpen = useRealms((s) => s.setOpen);
  const viewRealm = useUi((s) => s.viewRealm);
  const canViewRealm = useStats((s) => s.stats?.canViewRealm) ?? false;
  // Both halves of the realm's identity, selected narrowly so the panel re-renders on a crossing and not four times a
  // second. A row is "here" only when the pair matches — a recycled id is a different realm (12-realms § 1.1).
  const hereId = useStats((s) => s.stats?.realm?.realmId) ?? null;
  const hereGeneration = useStats((s) => s.stats?.realm?.generation) ?? null;
  const here = hereId === null || hereGeneration === null ? null : { realmId: hereId, generation: hereGeneration };

  const view = realmPanelView(inventory, here);
  if (view === null) {
    return null;
  }

  // `open` is held in the store rather than left to the element: this panel renders nothing when there is no inventory,
  // so one failed poll unmounts it — and the element's own `open` does not survive that. A viewer who had it open would
  // find it closed afterwards, which reads as the panel having done it by itself.
  return (
    <details
      className="realm-panel"
      open={open}
      onToggle={(e) => {
        setOpen(e.currentTarget.open);
      }}
    >
      <summary>
        {/* The separator sits OUTSIDE the span, or the middot before the sleeping count is accented with it. */}
        {view.summary.map((part, i) => (
          <Fragment key={part.text}>
            {i > 0 && ' · '}
            <span className={part.accent ? 'accent' : ''}>{part.text}</span>
          </Fragment>
        ))}
      </summary>
      {/* The body is not rendered while the panel is shut. A `details` element keeps its children in the DOM either
          way, so without this a closed panel would still lay out and diff sixty-odd rows of six cells every second for
          a viewer who is not looking at them — and the summary line, which is the part worth watching, is above it. */}
      {open && (
        <>
          <div className="hint">{view.freshness}</div>
          {/* The table stays a table, and the scrolling is the wrapper's. `display: block` on a `table` wraps its row
              groups in an anonymous box that shrink-wraps, so `width: 100%` stops reaching the columns and the header
              scrolls away with the rows — and the headings are single letters, wanted precisely when scrolled. */}
          <div className="realm-scroll">
            <table className="realm-table">
              <thead>
                <tr>
                  <th>Realm</th>
                  <th>State</th>
                  {/* `abbr` rather than a `title` on the cell: a title attribute on a one-letter heading is not
                      reliably announced, and these four headings are one letter each. */}
                  <th>
                    <abbr title="Players standing in this realm">P</abbr>
                  </th>
                  <th>
                    <abbr title="City NPCs">N</abbr>
                  </th>
                  <th>
                    <abbr title="Creatures">C</abbr>
                  </th>
                  <th>
                    <abbr title="Buildings, terminals, houses, factories, harvesters and lairs">S</abbr>
                  </th>
                </tr>
              </thead>
              <tbody>
                {view.rows.map((realm) => (
                  <tr key={realm.id} className={realm.here ? 'here' : ''}>
                    <td>
                      {/* A realm row is a place to go, when the session is allowed to go there and is not already there.
                    Asking for the realm on screen produces no REALM block, so nothing would end the fade the ask
                    starts and the viewer would sit behind an opaque screen until the safety valve fired. Against a
                    source that cannot honour ViewRealm the name is plain text rather than a button that does
                    nothing — the same rule the realm selector above follows. */}
                      {canViewRealm && !realm.here ? (
                        <button
                          className="link"
                          title={describe(realm)}
                          onClick={() => {
                            viewRealm(realm.id);
                          }}
                        >
                          {realm.label}
                        </button>
                      ) : (
                        realm.label
                      )}
                    </td>
                    <td className={`realm-state ${realm.state}`}>
                      {realm.state}
                      {realm.divisor > 1 && ` 1/${realm.divisor}`}
                    </td>
                    <td className="num">{realm.players}</td>
                    <td className="num">{realm.npcs}</td>
                    <td className="num">{realm.creatures}</td>
                    <td className="num">{realm.structures}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </details>
  );
}
