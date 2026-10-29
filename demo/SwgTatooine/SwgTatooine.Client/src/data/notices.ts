import { itemList } from './items';
import { marketWalletLine } from './market';
import { formatCredits } from './wallet';

/**
 * The moves of credits and items the server tells this session as they happen (`PlayerNotice`): a drop, a tip, a sale and
 * its tax, a purchase, a reward. What the chat says of them.
 *
 * <b>The client never reads the ledger</b> (Loïc, 2026-10-29): the ledgers are the operators' audit trail. The wallet and
 * the inventory answers carry a balance and stacks, and the news of a move arrives here, once, on the tick it is written.
 */

/** One move that concerns this player. */
export interface PlayerNotice {
  /** The world tick the server told it on. */
  readonly tick: number;
  /** Items moved; credits otherwise. */
  readonly items: boolean;
  /** This player received; it gave otherwise. */
  readonly incoming: boolean;
  /** The item's template; 0 for credits. */
  readonly template: number;
  /** The item's Mk; 0 for credits. */
  readonly mk: number;
  /** How many items; 0 for credits. */
  readonly count: number;
  /** How many credits, never negative: {@link incoming} says which way. */
  readonly amount: number;
  /** What the move is about — the victim, the lair, the listing — as text: a 64-bit value. The rows of one drop share it. */
  readonly ref: string;
  /** The ledger row's kind: `kill`, `lair`, `mission`, `buy`, `return`, `list`, `vendor`; `grant`, `tip`, `sale`, `tax`… */
  readonly kind: string;
  /** The other side's character name; `null` for the world or a character that is gone. */
  readonly other: string | null;
  /** The item's name, the server's: the client has no item table. Empty for credits. */
  readonly name: string;
}

/** How a drop's line starts, by its kind; a kind not here is not a drop and is not said. */
const DROP_PREFIX: Readonly<Partial<Record<string, string>>> = {
  kill: 'Loot',
  lair: 'Lair cache',
  mission: 'Mission cache',
  // Out of a bazaar listing into the inventory (bazaar.md § 6): bought, or one's own listing back.
  buy: 'Collected',
  return: 'Collected',
};

/**
 * The chat lines of one batch of notices — every notice of one frame — oldest first.
 *
 * The items received in one drop (one kind, one `ref`, one tick) are one line, the same item at the same Mk summed. A tip is
 * said from either side; a bazaar sale with the tax the same listing paid; a purchase; the vendor's payment.
 *
 * @param saleItem What a sale or a purchase sold, by its amount and listing (`market-store`); `null` when unknown.
 */
export function noticeLines(
  batch: readonly PlayerNotice[],
  saleItem: (amount: number, ref: string) => string | null = () => null,
): string[] {
  const lines: { tick: number; order: number; text: string }[] = [];
  const drops = new Map<string, { tick: number; order: number; prefix: string; items: PlayerNotice[] }>();
  batch.forEach((n, order) => {
    if (n.items) {
      const prefix = DROP_PREFIX[n.kind];
      if (!n.incoming || prefix === undefined) {
        return;
      }

      const key = `${n.kind}:${n.ref}:${String(n.tick)}`;
      const drop = drops.get(key);
      if (drop === undefined) {
        drops.set(key, { tick: n.tick, order, prefix, items: [n] });
      } else {
        drop.items.push(n);
      }

      return;
    }

    const text = creditLine(n, batch, saleItem);
    if (text !== null) {
      lines.push({ tick: n.tick, order, text });
    }
  });

  for (const drop of drops.values()) {
    const entries = drop.items.map((i) => ({ item: String(i.template), name: i.name, mk: i.mk, count: i.count }));
    lines.push({ tick: drop.tick, order: drop.order, text: `${drop.prefix}: ${itemList(entries)}` });
  }

  lines.sort((a, b) => a.tick - b.tick || a.order - b.order);
  return lines.map((l) => l.text);
}

// A move of credits as the chat says it, or null for one it does not say (a grant, a list fee or a tax said alone).
function creditLine(
  n: PlayerNotice,
  batch: readonly PlayerNotice[],
  saleItem: (amount: number, ref: string) => string | null,
): string | null {
  const signed = n.incoming ? n.amount : -n.amount;
  const who = n.other ?? 'somebody';
  if (n.kind === 'tip') {
    return n.incoming ? `${who} tipped you ${formatCredits(n.amount)}` : `You tipped ${who} ${formatCredits(n.amount)}`;
  }

  // The tax a sale paid is the notice naming the same listing.
  const tax =
    n.kind === 'sale' && n.incoming
      ? (batch.find((x) => !x.items && x.kind === 'tax' && x.ref === n.ref && !x.incoming)?.amount ?? 0)
      : 0;
  const what = n.kind === 'sale' ? saleItem(signed, n.ref) : null;
  return marketWalletLine(n.kind, signed, n.other, tax, what);
}
