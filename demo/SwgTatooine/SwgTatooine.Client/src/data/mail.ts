import type { ChatCategory } from './chat-tabs';

/**
 * Mail as the client reads it (mail.md § 4, § 6): the inbox `GET /swg/mail` answers, one mail with its text, and what a mail is called by its kind.
 */

/** A mail's kind, as the server names it. */
export type MailKind = 'player' | 'hopperfull' | 'upkeeplow' | 'stopped' | 'sold' | 'expired' | 'unknown';

/** One row of the inbox. */
export interface MailItem {
  readonly id: string;
  readonly kind: MailKind;
  /** The sender's character name; empty for the game. */
  readonly from: string;
  /** How long ago it was sent, seconds of world time. */
  readonly ageS: number;
  /** What a system mail is about — the installation, the listing — as a raw id; '0' for a player's. */
  readonly ref: string;
  readonly read: boolean;
  readonly subject: string;
}

/** One mail opened: its row and its text. */
export interface MailFull extends MailItem {
  readonly text: string;
}

/** The inbox: newest first, the unread count, the cap. */
export interface Inbox {
  readonly unread: number;
  readonly max: number;
  readonly mails: readonly MailItem[];
}

/** A mail's subject longest, and its text's, UTF-8 bytes: the server's `Mail.SubjectMaxBytes` and `TextMaxBytes`. */
export const MAIL_SUBJECT_MAX = 63;
export const MAIL_TEXT_MAX = 1000;

const KINDS: readonly MailKind[] = ['player', 'hopperfull', 'upkeeplow', 'stopped', 'sold', 'expired'];

/** The numbers a `MailNotice` carries a kind as, the server's `MailKind`: 1 player … 6 expired. */
export function kindOfNumber(kind: number): MailKind {
  return KINDS[kind - 1] ?? 'unknown';
}

function item(raw: unknown): MailItem | null {
  const m = raw as Record<string, unknown> | null;
  if (m === null || typeof m !== 'object' || typeof m.id !== 'string' || typeof m.subject !== 'string') {
    return null;
  }

  const kind =
    typeof m.kind === 'string' && (KINDS as readonly string[]).includes(m.kind) ? (m.kind as MailKind) : 'unknown';
  return {
    id: m.id,
    kind,
    from: typeof m.from === 'string' ? m.from : '',
    ageS: typeof m.ageS === 'number' ? m.ageS : 0,
    ref: typeof m.ref === 'string' ? m.ref : '0',
    read: m.read === true,
    subject: m.subject,
  };
}

/** `GET /swg/mail`'s answer, checked; `null` for anything else. A malformed row is dropped, not the answer. */
export function parseInbox(answer: unknown): Inbox | null {
  const a = answer as Record<string, unknown> | null;
  if (a === null || typeof a !== 'object' || !Array.isArray(a.mails)) {
    return null;
  }

  const mails = a.mails.map(item).filter((m): m is MailItem => m !== null);
  return {
    unread: typeof a.unread === 'number' ? a.unread : mails.filter((m) => !m.read).length,
    max: typeof a.max === 'number' ? a.max : 100,
    mails,
  };
}

/** `GET /swg/mail/{id}`'s answer, checked; `null` for anything else. */
export function parseMail(answer: unknown): MailFull | null {
  const row = item(answer);
  const text = (answer as Record<string, unknown> | null)?.text;
  return row === null ? null : { ...row, text: typeof text === 'string' ? text : '' };
}

/** Who a mail is from as the list says it: the player, or the part of the game that wrote it. */
export function senderOf(m: Pick<MailItem, 'kind' | 'from'>): string {
  switch (m.kind) {
    case 'player':
      return m.from === '' ? 'Someone' : m.from;
    case 'hopperfull':
    case 'upkeeplow':
    case 'stopped':
      return 'Your structures';
    case 'sold':
    case 'expired':
      return 'The bazaar';
    default:
      return 'Tatooine';
  }
}

/** The chat category a mail's arrival is said under (client.md C44). */
export function mailCategory(kind: MailKind): ChatCategory {
  return kind === 'player' ? 'tells' : kind === 'sold' || kind === 'expired' ? 'economy' : 'structures';
}

/** How long ago, as the list says it: `just now`, `12 min ago`, `3 h ago`, `2 days ago`. */
export function ageText(seconds: number): string {
  if (seconds < 60) {
    return 'just now';
  }

  if (seconds < 3600) {
    return `${String(Math.floor(seconds / 60))} min ago`;
  }

  if (seconds < 86_400) {
    return `${String(Math.floor(seconds / 3600))} h ago`;
  }

  const days = Math.floor(seconds / 86_400);
  return `${String(days)} day${days === 1 ? '' : 's'} ago`;
}

/** The byte length of a text as the server counts it: UTF-8. */
export function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** What is wrong with a mail before it is sent, or `null`: a name, a subject of 1 to {@link MAIL_SUBJECT_MAX} bytes, a text of {@link MAIL_TEXT_MAX}. */
export function composeProblem(to: string, subject: string, text: string): string | null {
  if (to.trim().length === 0) {
    return 'Who is it for?';
  }

  if (subject.trim().length === 0) {
    return 'It needs a subject';
  }

  if (utf8Length(subject.trim()) > MAIL_SUBJECT_MAX) {
    return `The subject is too long: ${String(MAIL_SUBJECT_MAX)} bytes at most`;
  }

  return utf8Length(text) > MAIL_TEXT_MAX ? `The text is too long: ${String(MAIL_TEXT_MAX)} bytes at most` : null;
}
