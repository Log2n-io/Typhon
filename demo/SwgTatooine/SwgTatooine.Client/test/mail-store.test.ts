import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { MarketCommand } from '../src/data/market';
import { useChat } from '../src/state/chat-store';
import { useMail } from '../src/state/mail-store';
import { useMarket, type MarketActions } from '../src/state/market-store';

/**
 * The mail window's store against answers the test releases one at a time (mail.md § 6, review 2026-11-04): an answer that lands after the player acted
 * must not undo what they did, a read must count once, and a send answered after the window closed is still said.
 */

interface Pending {
  readonly url: string;
  readonly body: string | undefined;
  readonly answer: (json: unknown, ok?: boolean) => void;
}

function harness() {
  const sent: MarketCommand[] = [];
  const pending: Pending[] = [];
  const actions: MarketActions = {
    send: (command) => {
      sent.push(command);
      return true;
    },
    fetch: (url, init) =>
      new Promise((resolve) => {
        pending.push({
          url,
          body: init.body,
          answer: (json, ok = true) => {
            resolve({ ok, status: ok ? 200 : 400, json: () => Promise.resolve(json) });
          },
        });
      }),
    token: () => 't',
  };
  useMarket.getState().setActions(actions);
  return {
    sent,
    /** The oldest request not yet answered, taken off the queue. */
    next: (): Pending => {
      const p = pending.shift();
      if (p === undefined) {
        throw new Error('no request pending');
      }

      return p;
    },
    pendingCount: () => pending.length,
  };
}

const flush = async (): Promise<void> => {
  for (let i = 0; i < 8; i++) {
    await Promise.resolve();
  }
};

const row = (id: string, read: boolean) => ({
  id,
  kind: 'player',
  from: 'han',
  ageS: 5,
  ref: '0',
  read,
  subject: `Mail ${id}`,
});

const inbox = (...mails: ReturnType<typeof row>[]) => ({
  unread: mails.filter((m) => !m.read).length,
  max: 100,
  mails,
});

describe('the mail store (mail.md § 6)', () => {
  beforeEach(() => {
    useMail.getState().reset();
    useChat.getState().clear();
  });

  afterEach(() => {
    useMarket.getState().setActions(null);
  });

  it('reads then deletes a mail at once: neither command is held behind the other', async () => {
    const h = harness();
    useMail.getState().toggle();
    h.next().answer(inbox(row('7', false)));
    await flush();
    void useMail.getState().show('7');
    h.next().answer({ ...row('7', false), text: 'hello' });
    await flush();
    useMail.getState().remove('7');
    expect(h.sent).toEqual([
      { kind: 'mailRead', mail: '7' },
      { kind: 'mailDelete', mail: '7' },
    ]);
    expect(useMail.getState().inbox).toEqual([]);
    expect(useMail.getState().opened).toBeNull();
  });

  it('drops a read of the inbox that was under way when a mail was deleted, so the mail does not come back', async () => {
    const h = harness();
    useMail.getState().toggle();
    h.next().answer(inbox(row('7', false), row('8', true)));
    await flush();
    void useMail.getState().refresh();
    useMail.getState().remove('7');
    h.next().answer(inbox(row('7', false), row('8', true)));
    await flush();
    expect(useMail.getState().inbox?.map((m) => m.id)).toEqual(['8']);
    expect(useMail.getState().unread).toBe(0);
  });

  it('counts a mail read once, opened twice or opened while its row already says read', async () => {
    const h = harness();
    useMail.getState().toggle();
    h.next().answer(inbox(row('7', false), row('8', false)));
    await flush();
    expect(useMail.getState().unread).toBe(2);
    for (let i = 0; i < 2; i++) {
      void useMail.getState().show('7');
      // The server has not marked it yet: the mail's own answer still says unread.
      h.next().answer({ ...row('7', false), text: '' });
      await flush();
    }

    expect(useMail.getState().unread).toBe(1);
    expect(h.sent.filter((c) => c.kind === 'mailRead')).toHaveLength(1);
  });

  it('shows the mail clicked last, not the one whose answer came last', async () => {
    const h = harness();
    useMail.getState().toggle();
    h.next().answer(inbox(row('7', true), row('8', true)));
    await flush();
    void useMail.getState().show('7');
    void useMail.getState().show('8');
    const first = h.next();
    h.next().answer({ ...row('8', true), text: 'eight' });
    await flush();
    first.answer({ ...row('7', true), text: 'seven' });
    await flush();
    expect(useMail.getState().opened?.id).toBe('8');
  });

  it('says in the chat a send refused after the window closed, and keeps the window quiet', async () => {
    const h = harness();
    useMail.getState().write({ to: 'han', subject: 'Hides', text: 'for sale' });
    h.next().answer(inbox());
    await flush();
    void useMail.getState().send();
    const post = h.next();
    expect(JSON.parse(post.body ?? '{}')).toEqual({ to: 'han', subject: 'Hides', text: 'for sale' });
    useMail.getState().close();
    post.answer({ ok: false, reason: 200 }, false);
    await flush();
    expect(useMail.getState().notice).toBeNull();
    expect(useMail.getState().sending).toBe(false);
    expect(useChat.getState().histories.tells.map((l) => l.text)).toEqual([
      expect.stringMatching(/^Mail to han not sent: /),
    ]);
  });

  it('says a send that went through, the window closed or not', async () => {
    const h = harness();
    useMail.getState().write({ to: 'han', subject: 'Hides', text: '' });
    h.next().answer(inbox());
    await flush();
    void useMail.getState().send();
    useMail.getState().close();
    h.next().answer({ ok: true, mail: '9' });
    await flush();
    expect(useMail.getState().notice).toBeNull();
    expect(useChat.getState().histories.tells.map((l) => l.text)).toEqual(['Mail sent to han: Hides']);
    expect(h.pendingCount()).toBe(0);
  });
});
