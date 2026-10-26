import type { ContextMenuItem } from '../state/ui-store';

/**
 * The character's wallet (economy-foundation.md): its balance and recent transfers, as `GET /swg/wallet` answers them, and
 * the `/tip` command that moves credits to another player.
 *
 * <b>Read on a revision, the journal's pattern</b> (`data/missions.ts`): the body carries an owner field `wallet` that the
 * server moves with every transfer touching its character, and {@link WalletReader} re-reads the wallet when it moves.
 */

/** One row of the wallet's history, signed from this wallet's side. */
export interface WalletTransfer {
  /** The ledger's sequence number, as text: a 64-bit value. */
  readonly seq: string;
  /** The world tick it was applied on. */
  readonly tick: number;
  /** `grant`, `mission`, `tip`. */
  readonly kind: string;
  /** Credits received (positive) or paid (negative). */
  readonly amount: number;
  /** The other side: a character's name, `World`, or `null` for one that is gone. */
  readonly other: string | null;
}

/** The wallet as last read. */
export interface WalletSnapshot {
  /** Whether the character has a wallet yet: opened on the tick of its first login. */
  readonly open: boolean;
  readonly credits: number;
  /** The most recent first. */
  readonly transfers: readonly WalletTransfer[];
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
export function formatCredits(credits: number): string {
  return `${credits.toLocaleString('en-US')} cr`;
}

/** How long a failed wallet read waits before it is tried again, milliseconds. */
export const WALLET_RETRY_MS = 3000;

/** How long a wallet read may take before it is abandoned and retried, milliseconds. */
export const WALLET_TIMEOUT_MS = 10_000;

type Fetch = (
  url: string,
  init: { headers: Record<string, string>; signal?: AbortSignal },
) => Promise<{ ok: boolean; status?: number; json(): Promise<unknown> }>;

/**
 * The character's wallet, re-read whenever the body's `wallet` revision moves.
 *
 * The journal's rules exactly (`MissionJournal`): the revision is the trigger, not a clock; a failed read is retried after
 * {@link WALLET_RETRY_MS}, a hung one abandoned after {@link WALLET_TIMEOUT_MS}; a revision that moved again meanwhile is
 * read again at once; a 401 or 403 is not retried until the token changes; an answer landing after {@link clear} is dropped.
 */
export class WalletReader {
  /** Bumped whenever a new answer arrives. */
  version = 0;

  private snapshot: WalletSnapshot | null = null;
  private generation = 0;
  private refusedToken: string | null = null;
  private wantedRev = -1;
  private readRev = -1;
  private inFlight = false;
  private retryAtMs = 0;

  private readonly fetcher: Fetch;
  private readonly token: () => string | null;
  private readonly now: () => number;

  /**
   * @param token The bearer token of this session's login, or `null` without one.
   * @param fetcher How a request is made; `fetch`, same-origin.
   * @param now A monotonic clock in milliseconds, for the retry delay.
   */
  constructor(
    token: () => string | null,
    fetcher: Fetch = (url, init) => fetch(url, init),
    now: () => number = () => performance.now(),
  ) {
    this.token = token;
    this.fetcher = fetcher;
    this.now = now;
  }

  /** The wallet as last read, or `null` before the first answer. */
  get wallet(): WalletSnapshot | null {
    return this.snapshot;
  }

  /**
   * Called every frame with the body's `wallet` owner field; reads the wallet when it moved since the last read.
   *
   * @param rev The revision, or `NaN` while the body has not reported one.
   */
  update(rev: number): void {
    if (!Number.isFinite(rev)) {
      return;
    }

    this.wantedRev = rev;
    if (this.inFlight || rev === this.readRev || this.now() < this.retryAtMs) {
      return;
    }

    const token = this.token();
    if (token === null || token === this.refusedToken) {
      return;
    }

    void this.read(rev, token);
  }

  /** Forgets everything: a new session may be another character on another server. */
  clear(): void {
    this.generation++;
    this.inFlight = false;
    this.refusedToken = null;
    this.snapshot = null;
    this.wantedRev = -1;
    this.readRev = -1;
    this.retryAtMs = 0;
    this.version++;
  }

  private async read(rev: number, token: string): Promise<void> {
    const generation = this.generation;
    this.inFlight = true;
    let answer: unknown;
    let refused = false;
    try {
      const response = await this.fetcher('swg/wallet', {
        headers: { Authorization: `Bearer ${token}` },
        signal: AbortSignal.timeout(WALLET_TIMEOUT_MS),
      });
      refused = response.status === 401 || response.status === 403;
      answer = response.ok ? await response.json() : null;
    } catch {
      answer = null;
    }

    if (generation !== this.generation) {
      return; // cleared meanwhile: another session's answer
    }

    this.inFlight = false;
    const wallet = parseWallet(answer);
    if (wallet === null) {
      this.retryAtMs = this.now() + WALLET_RETRY_MS;
      this.refusedToken = refused ? token : null;
      return;
    }

    this.readRev = rev;
    this.snapshot = wallet;
    this.version++;
    if (this.wantedRev !== rev) {
      this.update(this.wantedRev);
    }
  }
}

/** The answer of `GET /swg/wallet`, checked; `null` for anything else. */
export function parseWallet(answer: unknown): WalletSnapshot | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const w = answer as Record<string, unknown>;
  if (typeof w.credits !== 'number' || !Array.isArray(w.transfers)) {
    return null;
  }

  return {
    open: w.open === true,
    credits: w.credits,
    transfers: w.transfers.filter(isTransfer),
  };
}

function isTransfer(value: unknown): value is WalletTransfer {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const t = value as Record<string, unknown>;
  return (
    typeof t.seq === 'string' &&
    typeof t.tick === 'number' &&
    typeof t.kind === 'string' &&
    typeof t.amount === 'number' &&
    (typeof t.other === 'string' || t.other === null)
  );
}
