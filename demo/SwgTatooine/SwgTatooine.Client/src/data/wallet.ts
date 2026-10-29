import type { ContextMenuItem } from '../state/ui-store';
import { REVISION_RETRY_MS, REVISION_TIMEOUT_MS, RevisionReader, type RevisionFetch } from './revision-reader';

/**
 * The character's wallet (economy-foundation.md): its balance, as `GET /swg/wallet` answers it, and the `/tip` command
 * that moves credits to another player. <b>Never a ledger row</b>: a tip, a sale or a reward reaches the client as the
 * server's notice (`notices.ts`).
 *
 * <b>Read on a revision, the journal's pattern</b> (`data/missions.ts`): the body carries an owner field `wallet` that the
 * server moves with every transfer touching its character, and {@link WalletReader} re-reads the wallet when it moves.
 */

/** The wallet as last read. */
export interface WalletSnapshot {
  /** Whether the character has a wallet yet: opened on the tick of its first login. */
  readonly open: boolean;
  readonly credits: number;
}

/** The context menu of another player (a long press on them): tipping them, which opens the chat on `/tip `. */
export function playerMenuItems(target: number): ContextMenuItem[] {
  return [
    { label: 'Tip…', hint: 'Give this player some of your credits: /tip <amount>', action: { kind: 'tip', target } },
  ];
}

/** The tip refusals, as the server's ACKS reasons name them (`TatooineReplication.TipNotEnough` …). */
export const TIP_REASONS: Readonly<Record<number, string>> = {
  142: 'you are not in the world',
  143: 'not enough credits',
  144: 'that is not a player with a wallet',
  145: 'you cannot tip yourself',
  146: 'too far away — stand within 20 m',
  147: 'that amount cannot be tipped',
  148: 'the server could not apply it — nothing moved, try again',
};

/** What a tip refusal reads as; a reason this table does not know still says something. */
export function tipRefusal(reason: number): string {
  return TIP_REASONS[reason] ?? `refused (${String(reason)})`;
}

/** The most one `/tip` may move, as the server holds it (`EconomyLedger.MaxTip`): refused here before it is sent. */
export const MAX_TIP = 1_000_000;

/** How long a tip waits for a refusal, milliseconds: an accepted one is never answered — the wallet moves instead. */
export const TIP_ANSWER_MS = 5000;

/** A chat line that is a command: what it asks for, or why it cannot be done. */
export type ChatCommand =
  { readonly kind: 'tip'; readonly amount: number } | { readonly kind: 'error'; readonly message: string };

/**
 * Reads a chat line as a command: `null` for a line that is speech. A line is a command when it starts with `/`.
 *
 * `/tip <amount>` gives the selected player that many credits — a whole number from 1 to {@link MAX_TIP}; thousands may be
 * grouped (`/tip 1,000`, `/tip 1 000`).
 */
export function parseChatCommand(line: string): ChatCommand | null {
  const text = line.trim();
  if (!text.startsWith('/')) {
    return null;
  }

  const [name = '', ...rest] = text.slice(1).split(/\s+/);
  switch (name.toLowerCase()) {
    case 'tip': {
      const raw = rest.join('').replace(/[,_]/g, '');
      if (raw.length === 0) {
        return { kind: 'error', message: 'Usage: /tip <amount>, with a player selected' };
      }

      if (!/^\d+$/.test(raw)) {
        return { kind: 'error', message: `/tip: "${rest.join(' ')}" is not a whole number of credits` };
      }

      const amount = Number(raw);
      if (amount < 1 || amount > MAX_TIP) {
        return { kind: 'error', message: `/tip: from 1 to ${MAX_TIP.toLocaleString('en-US')} credits` };
      }

      return { kind: 'tip', amount };
    }

    default:
      return { kind: 'error', message: `Unknown command /${name} — try /tip <amount>` };
  }
}

/** Credits as the panels write them: grouped, with the unit. */
/** How often the inspector reads a selected player's balance again, milliseconds. */
export const BALANCE_OF_REFRESH_MS = 5000;

/** The answer of `GET /swg/wallet/of`, checked: the credits of an open wallet; `null` for a closed one or anything else. */
export function parseBalance(json: unknown): number | null {
  if (typeof json !== 'object' || json === null) {
    return null;
  }

  const r = json as { open?: unknown; credits?: unknown };
  return r.open === true && typeof r.credits === 'number' && Number.isFinite(r.credits) ? r.credits : null;
}

/** Another player's balance by its account name (`GET /swg/wallet/of`); `null` for an unknown name or a failed read. */
export async function readBalanceOf(
  name: string,
  fetcher: (url: string) => Promise<Response> = (url) => fetch(url),
): Promise<number | null> {
  try {
    const response = await fetcher(`swg/wallet/of?name=${encodeURIComponent(name)}`);
    return response.ok ? parseBalance(await response.json()) : null;
  } catch {
    return null;
  }
}

export function formatCredits(credits: number): string {
  return `${credits.toLocaleString('en-US')} cr`;
}

/** How long a failed wallet read waits before it is tried again, milliseconds. */
export const WALLET_RETRY_MS = REVISION_RETRY_MS;

/** How long a wallet read may take before it is abandoned and retried, milliseconds. */
export const WALLET_TIMEOUT_MS = REVISION_TIMEOUT_MS;

/** The character's wallet, re-read whenever the body's `wallet` revision moves: a {@link RevisionReader} on `swg/wallet`. */
export class WalletReader extends RevisionReader<WalletSnapshot> {
  /**
   * @param token The bearer token of this session's login, or `null` without one.
   * @param fetcher How a request is made; `fetch`, same-origin.
   * @param now A monotonic clock in milliseconds, for the retry delay.
   */
  constructor(token: () => string | null, fetcher?: RevisionFetch, now?: () => number) {
    super('swg/wallet', parseWallet, token, fetcher, now);
  }

  /** The wallet as last read, or `null` before the first answer. */
  get wallet(): WalletSnapshot | null {
    return this.value;
  }
}

/** The answer of `GET /swg/wallet`, checked; `null` for anything else. */
export function parseWallet(answer: unknown): WalletSnapshot | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const w = answer as Record<string, unknown>;
  if (typeof w.credits !== 'number') {
    return null;
  }

  return { open: w.open === true, credits: w.credits };
}
