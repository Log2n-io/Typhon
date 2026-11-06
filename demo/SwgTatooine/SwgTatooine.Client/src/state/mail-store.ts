import { create } from 'zustand';
import {
  composeProblem,
  kindOfNumber,
  mailCategory,
  parseInbox,
  parseMail,
  type MailFull,
  type MailItem,
} from '../data/mail';
import { marketRefusal, type MarketCommand } from '../data/market';
import type { MailArrival } from '../data/source';
import { useChat } from './chat-store';
import { useMarket } from './market-store';

/** What is being written: to whom, about what, and the text. */
export interface MailDraft {
  readonly to: string;
  readonly subject: string;
  readonly text: string;
}

/**
 * The mail window's state (mail.md § 6): the inbox as last read, the mail open, the one being written, the unread count the toolbar shows. Read at login
 * and whenever a `MailNotice` arrives; a mail opened is marked read, and the server's answer to a send is said in the window.
 */
export interface MailState {
  readonly open: boolean;
  readonly inbox: readonly MailItem[] | null;
  readonly unread: number;
  readonly opened: MailFull | null;
  readonly draft: MailDraft | null;
  /** The last send's answer, or what is wrong with the draft; `null` when there is nothing to say. */
  readonly notice: string | null;
  readonly sending: boolean;
  /** Who the last /tell heard came from: what /r answers. */
  readonly lastTellFrom: string | null;
  readonly toggle: () => void;
  readonly close: () => void;
  readonly refresh: () => Promise<void>;
  /** Opens one mail, reading its text, and marks it read. */
  readonly show: (id: string) => Promise<void>;
  readonly remove: (id: string) => void;
  readonly removeRead: () => void;
  /** Opens the composer, filled in where given; the window opens with it. */
  readonly write: (draft?: Partial<MailDraft>) => void;
  readonly setDraft: (change: Partial<MailDraft>) => void;
  readonly send: () => Promise<void>;
  /** A `MailNotice`: the inbox read again, the toolbar's count updated, a line in the chat. */
  readonly arrived: (mail: MailArrival) => void;
  readonly heardFrom: (name: string) => void;
  /** Reads the inbox once a session can, for the toolbar's unread count: called by the frame loop. Again only after {@link ENSURE_RETRY_MS}. */
  readonly ensure: () => void;
  readonly reset: () => void;
}

/** How long a first read that found no answer waits before it is asked again, ms. */
export const ENSURE_RETRY_MS = 5000;

let ensuredAt = Number.NEGATIVE_INFINITY;

const auth = (): Record<string, string> => {
  const token = useMarket.getState().actions?.token() ?? null;
  return token === null ? {} : { Authorization: `Bearer ${token}` };
};

const fetchJson = async (
  url: string,
  init?: { method?: string; body?: string },
): Promise<{ ok: boolean; json: unknown } | null> => {
  const actions = useMarket.getState().actions;
  if (actions === null) {
    return null;
  }

  try {
    const headers = init?.body === undefined ? auth() : { ...auth(), 'Content-Type': 'application/json' };
    const response = await actions.fetch(url, { headers, ...init });
    return { ok: response.ok, json: await response.json().catch(() => null) };
  } catch {
    return null;
  }
};

const sendCommand = (command: MarketCommand): boolean => useMarket.getState().sendCommand(command);

/** `POST /swg/mail`'s answer: whether it was sent, and the refusal's reason when not. */
function sendAnswer(answer: unknown): { readonly ok: boolean; readonly reason?: number } {
  const a = answer as Record<string, unknown> | null | undefined;
  if (a === null || a === undefined || typeof a !== 'object') {
    return { ok: false };
  }

  return typeof a.reason === 'number' ? { ok: a.ok === true, reason: a.reason } : { ok: a.ok === true };
}

// The inbox read under way: an answer from before a read, a delete, a reset or a newer read is dropped, so it cannot bring back what they changed.
let generation = 0;
// The mail being opened, likewise: a later click, a Write or a reset wins over an earlier answer.
let opening = 0;

export const useMail = create<MailState>((set, get) => ({
  open: false,
  inbox: null,
  unread: 0,
  opened: null,
  draft: null,
  notice: null,
  sending: false,
  lastTellFrom: null,
  toggle: () => {
    if (get().open) {
      get().close();
    } else {
      set({ open: true });
      void get().refresh();
    }
  },
  close: () => {
    set({ open: false, draft: null, notice: null });
  },
  refresh: async () => {
    const mine = ++generation;
    const answer = await fetchJson('swg/mail');
    const inbox = answer === null || !answer.ok ? null : parseInbox(answer.json);
    if (inbox !== null && mine === generation) {
      const opened = get().opened;
      set({
        inbox: inbox.mails,
        unread: inbox.unread,
        opened: opened !== null && inbox.mails.some((m) => m.id === opened.id) ? opened : null,
      });
    }
  },
  show: async (id) => {
    const mine = ++opening;
    const answer = await fetchJson(`swg/mail/${id}`);
    const mail = answer === null || !answer.ok ? null : parseMail(answer.json);
    if (mail === null || mine !== opening) {
      return;
    }

    set({ opened: mail, draft: null, notice: null });
    const row = get().inbox?.find((m) => m.id === id);
    if (!mail.read && row?.read !== true && sendCommand({ kind: 'mailRead', mail: id })) {
      // Shown read at once: the server's answer is the next read of the inbox, and one already under way would say it was not.
      generation++;
      set((s) => ({
        inbox: s.inbox?.map((m) => (m.id === id ? { ...m, read: true } : m)) ?? null,
        unread: Math.max(0, s.unread - 1),
      }));
    }
  },
  remove: (id) => {
    if (sendCommand({ kind: 'mailDelete', mail: id })) {
      generation++;
      set((s) => ({
        inbox: s.inbox?.filter((m) => m.id !== id) ?? null,
        unread: Math.max(0, s.unread - (s.inbox?.some((m) => m.id === id && !m.read) === true ? 1 : 0)),
        opened: s.opened?.id === id ? null : s.opened,
      }));
    }
  },
  removeRead: () => {
    if (sendCommand({ kind: 'mailDelete', mail: '0' })) {
      generation++;
      set((s) => ({
        inbox: s.inbox?.filter((m) => !m.read) ?? null,
        opened: s.opened?.read === true ? null : s.opened,
      }));
    }
  },
  write: (draft) => {
    opening++;
    set({
      open: true,
      opened: null,
      notice: null,
      draft: { to: draft?.to ?? '', subject: draft?.subject ?? '', text: draft?.text ?? '' },
    });
    if (get().inbox === null) {
      void get().refresh();
    }
  },
  setDraft: (change) => {
    const draft = get().draft;
    if (draft !== null) {
      set({ draft: { ...draft, ...change }, notice: null });
    }
  },
  send: async () => {
    const draft = get().draft;
    if (draft === null || get().sending) {
      return;
    }

    const problem = composeProblem(draft.to, draft.subject, draft.text);
    if (problem !== null) {
      set({ notice: problem });
      return;
    }

    set({ sending: true, notice: null });
    const answer = await fetchJson('swg/mail', {
      method: 'POST',
      body: JSON.stringify({ to: draft.to.trim(), subject: draft.subject.trim(), text: draft.text }),
    });
    const json = sendAnswer(answer?.json);
    if (answer !== null && answer.ok && json.ok) {
      set({
        sending: false,
        draft: get().draft === draft ? null : get().draft,
        notice: get().open ? `Sent to ${draft.to.trim()}` : null,
      });
      useChat.getState().notice(`Mail sent to ${draft.to.trim()}: ${draft.subject.trim()}`, 'tells');
      return;
    }

    const why =
      json.reason !== undefined
        ? marketRefusal(json.reason)
        : answer === null
          ? 'Not sent — no answer from the server'
          : 'Not sent';
    if (!get().open || get().draft !== draft) {
      // The window was closed, or the draft dropped, while it was on its way: the chat says it, with what it was, since the window cannot.
      set({ sending: false });
      useChat.getState().notice(`Mail to ${draft.to.trim()} not sent: ${why}`, 'tells');
      return;
    }

    set({ sending: false, notice: why });
  },
  arrived: (mail) => {
    const kind = kindOfNumber(mail.kind);
    set({ unread: mail.unread });
    const from = mail.from === '' ? '' : ` from ${mail.from}`;
    useChat.getState().notice(`New mail${from}: ${mail.subject} — N opens your mail`, mailCategory(kind));
    void get().refresh();
  },
  heardFrom: (name) => {
    set({ lastTellFrom: name });
  },
  ensure: () => {
    const now = performance.now();
    if (get().inbox !== null || useMarket.getState().actions === null || now - ensuredAt < ENSURE_RETRY_MS) {
      return;
    }

    ensuredAt = now;
    void get().refresh();
  },
  reset: () => {
    generation++;
    opening++;
    ensuredAt = Number.NEGATIVE_INFINITY;
    set({
      open: false,
      inbox: null,
      unread: 0,
      opened: null,
      draft: null,
      notice: null,
      sending: false,
      lastTellFrom: null,
    });
  },
}));
