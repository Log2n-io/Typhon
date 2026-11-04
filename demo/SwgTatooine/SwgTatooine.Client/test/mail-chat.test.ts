import { beforeEach, describe, expect, it } from 'vitest';
import { parseChatCommand } from '../src/data/chat-commands';
import {
  CATEGORY_HISTORY,
  DEFAULT_TABS,
  newTabId,
  parseTabs,
  tabLines,
  unreadIn,
  type ChatTab,
} from '../src/data/chat-tabs';
import {
  ageText,
  composeProblem,
  kindOfNumber,
  mailCategory,
  MAIL_TEXT_MAX,
  parseInbox,
  parseMail,
  senderOf,
} from '../src/data/mail';
import { marketWire } from '../src/data/market';
import { noticeEntries, type PlayerNotice } from '../src/data/notices';
import { useChat } from '../src/state/chat-store';

const notice = (over: Partial<PlayerNotice>): PlayerNotice => ({
  tick: 1,
  items: true,
  incoming: true,
  template: 8,
  mk: 2,
  count: 3,
  amount: 0,
  ref: '7',
  kind: 'kill',
  other: null,
  name: 'Hide',
  ...over,
});

describe('the chat’s categories (client.md C44)', () => {
  beforeEach(() => {
    useChat.getState().clear();
  });

  it('keeps a history per category, so the speech of a busy town cannot push a drop out', () => {
    const chat = useChat.getState();
    chat.notice('Loot: Hide Mk II ×3', 'loot');
    for (let i = 0; i < CATEGORY_HISTORY * 3; i++) {
      useChat.getState().say({ tick: i, speaker: 9, text: `line ${String(i)}`, atMs: 0 });
    }

    const { histories } = useChat.getState();
    expect(histories.local).toHaveLength(CATEGORY_HISTORY);
    expect(histories.loot.map((l) => l.text)).toEqual(['Loot: Hide Mk II ×3']);
    const events = DEFAULT_TABS[1];
    expect(tabLines(histories, events).map((l) => l.text)).toEqual(['Loot: Hide Mk II ×3']);
    expect(tabLines(histories, DEFAULT_TABS[0])).toHaveLength(CATEGORY_HISTORY);
  });

  it('merges a tab’s categories in arrival order, and counts what arrived since it was shown', () => {
    const chat = useChat.getState();
    chat.notice('one', 'loot');
    chat.say({ tick: 1, speaker: 3, text: 'two', atMs: 0 });
    chat.notice('three', 'economy');
    const { histories } = useChat.getState();
    const tab: ChatTab = { id: 't', name: 'Mine', categories: ['economy', 'loot'] };
    expect(tabLines(histories, tab).map((l) => l.text)).toEqual(['one', 'three']);
    expect(unreadIn(histories, tab, 0)).toBe(2);
    useChat.getState().markSeen('t');
    expect(unreadIn(useChat.getState().histories, tab, useChat.getState().seen.t)).toBe(0);
    useChat.getState().notice('four', 'loot');
    expect(unreadIn(useChat.getState().histories, tab, useChat.getState().seen.t)).toBe(1);
  });

  it('says a /tell under Tells, and does not count it as heard around you', () => {
    useChat.getState().say({ tick: 0, speaker: 0, text: 'meet me', atMs: 0, tell: { other: 'han', outgoing: false } });
    const s = useChat.getState();
    expect(s.histories.tells.map((l) => l.tell?.other)).toEqual(['han']);
    expect(s.heard).toBe(0);
  });

  it('files each notice under its category: loot, the bazaar, a structure', () => {
    expect(noticeEntries([notice({})]).map((e) => e.category)).toEqual(['loot']);
    expect(noticeEntries([notice({ kind: 'extract', ref: '9' })]).map((e) => e.category)).toEqual(['structures']);
    expect(noticeEntries([notice({ kind: 'buy', ref: '5' })]).map((e) => e.category)).toEqual(['economy']);
    expect(
      noticeEntries([notice({ items: false, kind: 'upkeep', incoming: false, amount: 100 })]).map((e) => e.category),
    ).toEqual(['structures']);
  });

  it('reads saved tabs, keeping what is valid, and falls back to the defaults', () => {
    expect(parseTabs(undefined)).toBe(DEFAULT_TABS);
    expect(
      parseTabs([
        { id: 'a', name: '  Trade  ', categories: ['economy', 'nonsense'] },
        { id: 'a', name: 'dup', categories: [] },
        7,
      ]),
    ).toEqual([{ id: 'a', name: 'Trade', categories: ['economy'] }]);
    expect(parseTabs([{ id: 'x', name: '', categories: [] }])).toBe(DEFAULT_TABS);
    expect(newTabId(DEFAULT_TABS)).toBe('tab3');
  });
});

describe('the mail (mail.md § 6)', () => {
  it('reads the inbox and one mail, dropping a malformed row', () => {
    const inbox = parseInbox({
      unread: 1,
      max: 100,
      mails: [
        {
          id: '5',
          kind: 'hopperfull',
          from: '',
          sentTick: 1,
          ageS: 90,
          ref: '77',
          read: false,
          subject: 'Extractor full: Metal',
        },
        { id: '4', kind: 'player', from: 'han', ageS: 7200, read: true, subject: 'Hides' },
        { kind: 'player' },
      ],
    });
    expect(inbox?.mails.map((m) => [m.id, senderOf(m), ageText(m.ageS)])).toEqual([
      ['5', 'Your structures', '1 min ago'],
      ['4', 'han', '2 h ago'],
    ]);
    expect(inbox?.unread).toBe(1);
    expect(parseInbox({ nope: true })).toBeNull();
    expect(parseMail({ id: '4', kind: 'sold', subject: 'Sold: 5 Hide', text: 'han bought…' })?.text).toBe(
      'han bought…',
    );
  });

  it('names a notice’s kind, the category its arrival is said under, and how long ago', () => {
    expect([1, 2, 5, 6, 9].map(kindOfNumber)).toEqual(['player', 'hopperfull', 'sold', 'expired', 'unknown']);
    expect([mailCategory('player'), mailCategory('sold'), mailCategory('stopped')]).toEqual([
      'tells',
      'economy',
      'structures',
    ]);
    expect([30, 600, 3 * 86_400, 86_400].map(ageText)).toEqual(['just now', '10 min ago', '3 days ago', '1 day ago']);
  });

  it('says what is wrong with a draft before it is sent', () => {
    expect(composeProblem('', 'Hi', '')).toBe('Who is it for?');
    expect(composeProblem('han', ' ', '')).toBe('It needs a subject');
    expect(composeProblem('han', 'x'.repeat(64), '')).toContain('too long');
    expect(composeProblem('han', 'Hi', 'é'.repeat(MAIL_TEXT_MAX / 2 + 1))).toContain('too long');
    expect(composeProblem('han', 'Hi', 'é'.repeat(MAIL_TEXT_MAX / 2))).toBeNull();
  });

  it('parses /tell, /r and /mail, and sends a tell and a delete by name', () => {
    expect(parseChatCommand('/tell han meet me at the cantina')).toEqual({
      kind: 'tell',
      to: 'han',
      text: 'meet me at the cantina',
    });
    expect(parseChatCommand('/tell han')).toEqual({ kind: 'error', message: 'Usage: /tell <player> <text>' });
    expect(parseChatCommand('/r on my way')).toEqual({ kind: 'reply', text: 'on my way' });
    expect(parseChatCommand('/mail han Hides for sale')).toEqual({
      kind: 'mail',
      to: 'han',
      subject: 'Hides for sale',
    });
    expect(parseChatCommand('/mail')).toEqual({ kind: 'mail', to: '', subject: '' });
    expect(marketWire({ kind: 'tell', to: 'han', text: 'hi' })).toEqual({
      name: 'TellPlayer',
      values: { to: 'han', text: 'hi' },
    });
    expect(marketWire({ kind: 'mailDelete', mail: '0' })).toEqual({ name: 'DeleteMail', values: { mail: 0n } });
  });
});
