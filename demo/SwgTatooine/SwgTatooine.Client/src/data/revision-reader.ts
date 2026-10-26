/**
 * An owner read behind a revision (economy-foundation.md E0-10, inventory-loot.md § 6): the body carries an owner field that
 * the server moves whenever what `GET <path>` answers changed for its character, and {@link RevisionReader} re-reads it
 * when it moves. The wallet (`swg/wallet`, field `wallet`) and the inventory (`swg/inventory`, field `items`) are two of them.
 */

/** How long a failed read waits before it is tried again, milliseconds. */
export const REVISION_RETRY_MS = 3000;

/** How long a read may take before it is abandoned and retried, milliseconds. */
export const REVISION_TIMEOUT_MS = 10_000;

/** How a request is made: `fetch`'s shape, narrowed to what a reader uses, so a test or the mock can answer it. */
export type RevisionFetch = (
  url: string,
  init: { headers: Record<string, string>; signal?: AbortSignal },
) => Promise<{ ok: boolean; status?: number; json(): Promise<unknown> }>;

/**
 * One owner read, re-read whenever its revision moves.
 *
 * The journal's rules exactly (`MissionJournal`): the revision is the trigger, not a clock; a failed read is retried after
 * {@link REVISION_RETRY_MS}, a hung one abandoned after {@link REVISION_TIMEOUT_MS}; a revision that moved again meanwhile is
 * read again at once; a 401 or 403 is not retried until the token changes; an answer landing after {@link clear} is dropped.
 */
export class RevisionReader<T> {
  /** Bumped whenever a new answer arrives, and on {@link clear}. */
  version = 0;

  private answer: T | null = null;
  private generation = 0;
  private refusedToken: string | null = null;
  private wantedRev = -1;
  private readRev = -1;
  private inFlight = false;
  private retryAtMs = 0;

  private readonly path: string;
  private readonly parse: (answer: unknown) => T | null;
  private readonly fetcher: RevisionFetch;
  private readonly token: () => string | null;
  private readonly now: () => number;

  /**
   * @param path The endpoint, relative to the page: `swg/wallet`.
   * @param parse Checks an answer; `null` for anything else, which is retried as a failed read.
   * @param token The bearer token of this session's login, or `null` without one.
   * @param fetcher How a request is made; `fetch`, same-origin.
   * @param now A monotonic clock in milliseconds, for the retry delay.
   */
  constructor(
    path: string,
    parse: (answer: unknown) => T | null,
    token: () => string | null,
    fetcher: RevisionFetch = (url, init) => fetch(url, init),
    now: () => number = () => performance.now(),
  ) {
    this.path = path;
    this.parse = parse;
    this.token = token;
    this.fetcher = fetcher;
    this.now = now;
  }

  /** The answer as last read, or `null` before the first one. */
  get value(): T | null {
    return this.answer;
  }

  /**
   * Called every frame with the body's owner field; reads when it moved since the last read.
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
    this.answer = null;
    this.wantedRev = -1;
    this.readRev = -1;
    this.retryAtMs = 0;
    this.version++;
  }

  private async read(rev: number, token: string): Promise<void> {
    const generation = this.generation;
    this.inFlight = true;
    let raw: unknown;
    let refused = false;
    try {
      const response = await this.fetcher(this.path, {
        headers: { Authorization: `Bearer ${token}` },
        signal: AbortSignal.timeout(REVISION_TIMEOUT_MS),
      });
      refused = response.status === 401 || response.status === 403;
      raw = response.ok ? await response.json() : null;
    } catch {
      raw = null;
    }

    if (generation !== this.generation) {
      return; // cleared meanwhile: another session's answer
    }

    this.inFlight = false;
    const answer = this.parse(raw);
    if (answer === null) {
      this.retryAtMs = this.now() + REVISION_RETRY_MS;
      this.refusedToken = refused ? token : null;
      return;
    }

    this.readRev = rev;
    this.answer = answer;
    this.version++;
    if (this.wantedRev !== rev) {
      this.update(this.wantedRev);
    }
  }
}
