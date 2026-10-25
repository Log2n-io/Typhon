/**
 * The names behind players' replicated `nameId`s (A12), fetched from `/typhon/names` once per id and kept for the session.
 *
 * <b>An id on the wire and the text over HTTP</b>, because a 64-byte name on every player — the scripted crowd included —
 * took the server's player clusters from 25 entities to 10 (`PlayerNames.cs`). A client meets a handful of real players;
 * asking for each once is nothing next to every tick walking two and a half times the clusters.
 *
 * <b>Batched and never repeated.</b> Ids asked for in one frame go out as one request, on the next turn of the event loop. An
 * id the server does not know is remembered as nameless, so it is not asked for again every frame; a failed request is
 * retried, but not before {@link RETRY_MS}.
 */

/** How long a failed request keeps its ids from being asked for again, milliseconds. */
export const RETRY_MS = 5000;

/** The most ids one request carries: the server's `PlayerNames.MaxIdsPerRequest`. */
export const MAX_IDS_PER_REQUEST = 256;

type Fetch = (url: string) => Promise<{ ok: boolean; json(): Promise<unknown> }>;

export class PlayerNameDirectory {
  /** Bumped whenever a name arrives, so a label built from this directory knows to be rebuilt. */
  version = 0;

  private readonly names = new Map<number, string>();
  private readonly asked = new Set<number>();
  private readonly pending = new Set<number>();
  private readonly retryAfter = new Map<number, number>();
  private scheduled = false;
  /** Bumped by {@link clear}, so an answer to a request from before it is dropped. */
  private generation = 0;

  private readonly fetcher: Fetch;
  private readonly now: () => number;

  /**
   * @param fetcher How a request is made; `fetch`, same-origin.
   * @param now A monotonic clock in milliseconds, for the retry delay.
   */
  constructor(fetcher: Fetch = (url) => fetch(url), now: () => number = () => performance.now()) {
    this.fetcher = fetcher;
    this.now = now;
  }

  /**
   * The name an id stands for, or `null` while it is not known yet — asking the server for it if nobody has. 0 is "no
   * name", and is never asked about.
   */
  nameOf(id: number): string | null {
    if (!(id > 0)) {
      return null;
    }

    const known = this.names.get(id);
    if (known !== undefined) {
      return known === '' ? null : known;
    }

    if (!this.asked.has(id) && this.now() >= (this.retryAfter.get(id) ?? 0)) {
      this.asked.add(id);
      this.pending.add(id);
      this.schedule();
    }

    return null;
  }

  /** Forgets every name: ids are the server process's, and a new session may be talking to another one. */
  clear(): void {
    this.names.clear();
    this.asked.clear();
    this.pending.clear();
    this.retryAfter.clear();
    this.generation++;
    this.version++;
  }

  private schedule(): void {
    if (this.scheduled) {
      return;
    }

    this.scheduled = true;
    setTimeout(() => {
      this.scheduled = false;
      void this.flush();
    }, 0);
  }

  private async flush(): Promise<void> {
    const generation = this.generation;
    const ids = [...this.pending].slice(0, MAX_IDS_PER_REQUEST);
    for (const id of ids) {
      this.pending.delete(id);
    }

    if (this.pending.size > 0) {
      this.schedule();
    }

    if (ids.length === 0) {
      return;
    }

    let answer: unknown;
    try {
      const response = await this.fetcher(`typhon/names?ids=${ids.join(',')}`);
      answer = response.ok ? await response.json() : null;
    } catch {
      answer = null;
    }

    if (generation !== this.generation) {
      return;
    }

    if (typeof answer !== 'object' || answer === null) {
      // Unreachable or refused: ask again later, not on every frame until it answers.
      const later = this.now() + RETRY_MS;
      for (const id of ids) {
        this.asked.delete(id);
        this.retryAfter.set(id, later);
      }

      return;
    }

    const byId = answer as Record<string, unknown>;
    for (const id of ids) {
      const name = byId[String(id)];
      // An id the server left out has no name it will give: remembered as nameless, never asked about again.
      this.names.set(id, typeof name === 'string' ? name : '');
    }

    this.version++;
  }
}
