/**
 * Commands sent and not refused yet, by their sequence number: what each one was about, until its refusal arrives or its
 * answer window closes.
 *
 * <b>Accepted silently, refused out loud.</b> A take, a cancel or a tip that the server accepts is never acked, so an entry
 * cannot wait for an answer: it is forgotten {@link answerMs} after it was sent, the time within which a refusal would
 * have come. Kept longer, a seq the queue reused — a reconnect restarts it at 1, a wrap after half an hour of driving —
 * would read a later command's refusal as this one's.
 */
export class PendingSeqs<T> {
  private readonly asks = new Map<number, { readonly value: T; readonly atMs: number }>();
  private readonly answerMs: number;

  /** @param answerMs How long a refusal may take to arrive, milliseconds. */
  constructor(answerMs: number) {
    this.answerMs = answerMs;
  }

  /** Remembers a command just sent, forgetting those whose window closed. */
  add(seq: number, value: T, nowMs: number): void {
    for (const [old, ask] of this.asks) {
      if (nowMs - ask.atMs > this.answerMs) {
        this.asks.delete(old);
      }
    }

    this.asks.set(seq, { value, atMs: nowMs });
  }

  /**
   * A refusal arrived for `seq`: what that command was about, and it is forgotten — or `undefined` when no command of
   * this kind is waiting on that seq, or its window has closed.
   */
  take(seq: number, nowMs: number): T | undefined {
    const ask = this.asks.get(seq);
    if (ask === undefined) {
      return undefined;
    }

    this.asks.delete(seq);
    return nowMs - ask.atMs > this.answerMs ? undefined : ask.value;
  }

  /** Forgets everything: the session that sent them is gone. */
  clear(): void {
    this.asks.clear();
  }

  get size(): number {
    return this.asks.size;
  }
}
