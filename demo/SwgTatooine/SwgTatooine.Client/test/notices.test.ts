import { afterEach, describe, expect, it } from 'vitest';
import { noticeLines, type PlayerNotice } from '../src/data/notices';
import { useChat } from '../src/state/chat-store';
import { announceNotices } from '../src/state/wallet-store';

/**
 * What the chat says of the server's notices (`PlayerNotice`): the client never reads the ledger (Loïc, 2026-10-29), so a
 * drop, a tip, a sale and its tax reach it only as notices, one frame's batch at a time.
 */

const drop = (
  kind: string,
  name: string,
  mk: number,
  count: number,
  ref = '500',
  tick = 10,
  incoming = true,
): PlayerNotice => ({
  tick,
  items: true,
  incoming,
  template: name.length,
  mk,
  count,
  amount: 0,
  ref,
  kind,
  other: null,
  name,
});

const credits = (
  kind: string,
  amount: number,
  incoming: boolean,
  other: string | null,
  ref = '0',
  tick = 10,
): PlayerNotice => ({
  tick,
  items: false,
  incoming,
  template: 0,
  mk: 0,
  count: 0,
  amount,
  ref,
  kind,
  other,
  name: '',
});

describe('the notice lines', () => {
  it('say the notices of one drop as one line, the same item at the same Mk summed, in the order they came', () => {
    const batch = [drop('kill', 'Armor Core', 2, 1), drop('kill', 'Hide', 2, 2), drop('kill', 'Hide', 2, 1)];
    expect(noticeLines(batch)).toEqual(['Loot: Armor Core Mk II, Hide Mk II ×3']);
  });

  it('say two drops as two lines, oldest first, a cache by its kind', () => {
    const batch = [drop('mission', 'Weapon Enhancement', 3, 1, '77', 12), drop('lair', 'Bone', 2, 6, '66', 11)];
    expect(noticeLines(batch)).toEqual(['Lair cache: Bone Mk II ×6', 'Mission cache: Weapon Enhancement Mk III']);
  });

  it('say nothing of items that left, nor of a kind that is not a drop', () => {
    const batch = [
      drop('list', 'Hide', 1, 4, '9', 10, false),
      drop('vendor', 'Bone', 1, 2, '0', 10, false),
      drop('give', 'Hide', 1, 1),
    ];
    expect(noticeLines(batch)).toEqual([]);
  });

  it('say a tip from either side', () => {
    expect(noticeLines([credits('tip', 150, true, 'Ana')])).toEqual(['Ana tipped you 150 cr']);
    expect(noticeLines([credits('tip', 1500, false, 'Bo')])).toEqual(['You tipped Bo 1,500 cr']);
  });

  it('say a sale with the tax its listing paid, a purchase, a vendor payment, and not a grant or a fee alone', () => {
    const sale = [credits('sale', 1000, true, 'Bo', '42'), credits('tax', 50, false, null, '42')];
    expect(noticeLines(sale, () => 'Hide Mk II ×4')).toEqual(['Sold Hide Mk II ×4 to Bo for 1,000 cr (tax 50)']);
    expect(noticeLines([credits('sale', 300, false, 'Cy', '43')])).toEqual(['Bought from Cy for 300 cr']);
    expect(noticeLines([credits('vendor', 12, true, null)])).toEqual(['Vendor paid 12 cr']);
    expect(noticeLines([credits('grant', 1000, true, null), credits('listfee', 5, false, null)])).toEqual([]);
  });
});

describe('the announced notices', () => {
  afterEach(() => {
    useChat.getState().clear();
  });

  it('go to the chat as system lines', () => {
    announceNotices([drop('kill', 'Hide', 1, 4), credits('tip', 7, true, 'Ana')]);
    const lines = useChat.getState().lines;
    expect(lines.map((l) => l.text)).toEqual(['Loot: Hide Mk I ×4', 'Ana tipped you 7 cr']);
    expect(lines.every((l) => l.system === true)).toBe(true);
  });
});
