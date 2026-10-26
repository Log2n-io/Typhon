import { afterEach, describe, expect, it } from 'vitest';
import { panelKey } from '../src/app/panel-keys';
import {
  formatCredits,
  MAX_TIP,
  parseChatCommand,
  parseWallet,
  playerMenuItems,
  tipRefusal,
  WALLET_RETRY_MS,
  WalletReader,
  type WalletSnapshot,
  type WalletTransfer,
} from '../src/data/wallet';
import { runChatCommand } from '../src/state/chat-commands';
import { useChat } from '../src/state/chat-store';
import { useUi } from '../src/state/ui-store';
import { useWallet } from '../src/state/wallet-store';

/**
 * The wallet on the client (economy-foundation.md E0-7, E0-10): `/tip` typed in chat, the wallet read on its revision, the
 * tips announced in chat, the inventory's and the journal's keys, and the player menu's "Tip…".
 */

// `isTyping` tests `target instanceof HTMLInputElement`, which throws when the global is undefined (camera-input.test.ts).
{
  const target = globalThis as unknown as Record<string, unknown>;
  const absent = function Absent(): void {};
  target['HTMLInputElement'] ??= absent;
  target['HTMLSelectElement'] ??= absent;
  target['HTMLTextAreaElement'] ??= absent;
}

describe('a chat command', () => {
  it('is a line starting with /, and speech otherwise', () => {
    expect(parseChatCommand('hello there')).toBeNull();
    expect(parseChatCommand(' tip 5')).toBeNull();
    expect(parseChatCommand('/tip 1000')).toEqual({ kind: 'tip', amount: 1000 });
    expect(parseChatCommand('  /TIP   250  ')).toEqual({ kind: 'tip', amount: 250 });
  });

  it('takes grouped thousands', () => {
    expect(parseChatCommand('/tip 1,000')).toEqual({ kind: 'tip', amount: 1000 });
    expect(parseChatCommand('/tip 1 000')).toEqual({ kind: 'tip', amount: 1000 });
    expect(parseChatCommand('/tip 10_000')).toEqual({ kind: 'tip', amount: 10000 });
  });

  it('refuses what cannot be tipped before it is sent, saying why', () => {
    for (const line of ['/tip', '/tip abc', '/tip -5', '/tip 1.5', '/tip 0', `/tip ${String(MAX_TIP + 1)}`, '/dance']) {
      const command = parseChatCommand(line);
      expect(command?.kind, line).toBe('error');
    }

    expect(parseChatCommand(`/tip ${String(MAX_TIP)}`)).toEqual({ kind: 'tip', amount: MAX_TIP });
    expect(parseChatCommand('/tip 1')).toEqual({ kind: 'tip', amount: 1 });
    const unknown = parseChatCommand('/dance');
    expect(unknown?.kind === 'error' ? unknown.message : '').toContain('Unknown command /dance');
  });
});

describe('the server refusals', () => {
  it('name each reason the server gives, and still say something for one they do not know', () => {
    expect(tipRefusal(143)).toBe('not enough credits');
    expect(tipRefusal(146)).toContain('20 m');
    expect(tipRefusal(999)).toBe('refused (999)');
  });

  it('credits are grouped, with the unit', () => {
    expect(formatCredits(1234567)).toBe('1,234,567 cr');
  });
});

const transfer = (seq: string, kind: string, amount: number, other: string | null = 'World'): WalletTransfer => ({
  seq,
  tick: 10,
  kind,
  amount,
  other,
});

describe('the wallet answer', () => {
  it('is checked, and a malformed row dropped', () => {
    expect(parseWallet(null)).toBeNull();
    expect(parseWallet({ credits: 'x', transfers: [] })).toBeNull();
    const wallet = parseWallet({
      open: true,
      credits: 1000,
      transfers: [transfer('1', 'grant', 1000), { seq: 2 }, transfer('3', 'tip', 5, null)],
    });
    expect(wallet).toEqual({
      open: true,
      credits: 1000,
      transfers: [transfer('1', 'grant', 1000), transfer('3', 'tip', 5, null)],
    });
  });
});

describe('the wallet reader', () => {
  const answer = (credits: number) => ({ open: true, credits, transfers: [] });
  const harness = (answers: unknown[], token: () => string | null = () => 'tok') => {
    let clock = 0;
    const urls: { url: string; auth: string | undefined }[] = [];
    let resolveNext: (() => void) | null = null;
    const reader = new WalletReader(
      token,
      (url, init) => {
        urls.push({ url, auth: init.headers['Authorization'] });
        const next = answers.shift();
        return new Promise((resolve) => {
          resolveNext = () => {
            resolve(
              next === 'fail'
                ? { ok: false, status: 500, json: () => Promise.resolve(null) }
                : next === 401
                  ? { ok: false, status: 401, json: () => Promise.resolve(null) }
                  : { ok: true, status: 200, json: () => Promise.resolve(next) },
            );
          };
        });
      },
      () => clock,
    );
    const settle = async (): Promise<void> => {
      const r = resolveNext;
      resolveNext = null;
      r?.();
      for (let i = 0; i < 5; i++) {
        await Promise.resolve();
      }
    };
    return { reader, urls, settle, advance: (ms: number) => (clock += ms) };
  };

  it('reads when the revision moves, with the bearer token, and not again until it moves again', async () => {
    const h = harness([answer(1000), answer(900)]);
    h.reader.update(Number.NaN);
    expect(h.urls).toHaveLength(0);
    h.reader.update(1);
    expect(h.urls).toEqual([{ url: 'swg/wallet', auth: 'Bearer tok' }]);
    await h.settle();
    expect(h.reader.wallet?.credits).toBe(1000);
    const version = h.reader.version;
    h.reader.update(1);
    expect(h.urls).toHaveLength(1);
    h.reader.update(2);
    await h.settle();
    expect(h.reader.wallet?.credits).toBe(900);
    expect(h.reader.version).toBe(version + 1);
  });

  it('reads again at once when the revision moved during a read', async () => {
    const h = harness([answer(1000), answer(800)]);
    h.reader.update(1);
    h.reader.update(2);
    expect(h.urls).toHaveLength(1);
    await h.settle();
    expect(h.urls).toHaveLength(2);
    await h.settle();
    expect(h.reader.wallet?.credits).toBe(800);
  });

  it('retries a failed read after the delay, and a refused token not at all', async () => {
    const h = harness(['fail', answer(1000)]);
    h.reader.update(1);
    await h.settle();
    h.reader.update(1);
    expect(h.urls).toHaveLength(1);
    h.advance(WALLET_RETRY_MS);
    h.reader.update(1);
    expect(h.urls).toHaveLength(2);
    await h.settle();
    expect(h.reader.wallet?.credits).toBe(1000);

    const refused = harness([401]);
    refused.reader.update(1);
    await refused.settle();
    refused.advance(WALLET_RETRY_MS * 10);
    refused.reader.update(1);
    expect(refused.urls).toHaveLength(1);
  });

  it('drops an answer that lands after a clear: another session', async () => {
    const h = harness([answer(1000)]);
    h.reader.update(1);
    h.reader.clear();
    await h.settle();
    expect(h.reader.wallet).toBeNull();
  });
});

describe('the wallet store', () => {
  afterEach(() => {
    useWallet.setState({ wallet: null, inventoryOpen: false, actions: null });
    useChat.getState().clear();
    useUi.setState({ selectedNetId: 0, contextMenu: null });
  });

  const snapshot = (credits: number, transfers: WalletTransfer[]): WalletSnapshot => ({
    open: true,
    credits,
    transfers,
  });

  it('says a tip given or received in the chat, but not the first read: history, not news', () => {
    const grant = transfer('1', 'grant', 1000);
    useWallet.getState().setWallet(snapshot(1000, [grant, transfer('0', 'tip', 5, 'Old')]));
    expect(useChat.getState().lines).toHaveLength(0);

    // Newest first, as the server lists them; said oldest first.
    useWallet
      .getState()
      .setWallet(
        snapshot(1250, [
          transfer('4', 'mission', 200),
          transfer('3', 'tip', 100, 'Kira'),
          transfer('2', 'tip', -50, 'Rho'),
          grant,
        ]),
      );
    const lines = useChat.getState().lines;
    expect(lines.map((l) => l.text)).toEqual(['You tipped Rho 50 cr', 'Kira tipped you 100 cr']);
    expect(lines.every((l) => l.system === true)).toBe(true);
  });

  it('forgets the wallet on a new session without announcing anything', () => {
    useWallet.getState().setWallet(snapshot(1000, []));
    useWallet.getState().resetWallet();
    useWallet.getState().setWallet(snapshot(900, [transfer('2', 'tip', -100, 'Rho')]));
    expect(useChat.getState().lines).toHaveLength(0);
  });

  it('toggles the inventory', () => {
    useWallet.getState().toggleInventory();
    expect(useWallet.getState().inventoryOpen).toBe(true);
    useWallet.getState().toggleInventory();
    expect(useWallet.getState().inventoryOpen).toBe(false);
  });

  it('/tip goes to the selection through the app, and says so when it cannot', () => {
    const sent: { target: number; amount: number }[] = [];
    let accept = true;
    useWallet.getState().setActions({
      tip: (target, amount) => {
        sent.push({ target, amount });
        return accept;
      },
    });

    expect(runChatCommand({ kind: 'tip', amount: 1000 })).toBe(true);
    expect(sent).toHaveLength(0);
    expect(useChat.getState().lines.at(-1)?.text).toContain('select a player first');

    useUi.setState({ selectedNetId: 42 });
    runChatCommand({ kind: 'tip', amount: 1000 });
    expect(sent).toEqual([{ target: 42, amount: 1000 }]);
    const said = useChat.getState().lines.length;

    accept = false;
    expect(runChatCommand({ kind: 'tip', amount: 5 })).toBe(false); // kept in the box, to send again
    expect(useChat.getState().lines).toHaveLength(said + 1);
    expect(useChat.getState().lines.at(-1)?.text).toContain('not sent');

    runChatCommand({ kind: 'error', message: 'Usage: /tip <amount>' });
    expect(useChat.getState().lines.at(-1)).toMatchObject({ text: 'Usage: /tip <amount>', system: true });
  });
});

describe("a player's context menu", () => {
  afterEach(() => {
    useUi.setState({ selectedNetId: 0, contextMenu: null });
  });

  it('offers a tip, which opens the chat on /tip with that player selected', () => {
    const items = playerMenuItems(42);
    expect(items.map((i) => i.action)).toEqual([{ kind: 'tip', target: 42 }]);
    const before = useChat.getState().composeRequest?.seq ?? 0;
    useUi.getState().runContextAction(items[0].action);
    expect(useUi.getState().selectedNetId).toBe(42);
    expect(useChat.getState().composeRequest).toEqual({ text: '/tip ', seq: before + 1 });
  });
});

describe('the panel keys', () => {
  const key = (k: string, extra: Partial<KeyboardEvent> = {}): KeyboardEvent =>
    ({
      key: k,
      repeat: false,
      isComposing: false,
      ctrlKey: false,
      altKey: false,
      metaKey: false,
      target: null,
      ...extra,
    }) as KeyboardEvent;

  it('J is the journal and I the inventory, either case', () => {
    expect(panelKey(key('j'))).toBe('journal');
    expect(panelKey(key('J'))).toBe('journal');
    expect(panelKey(key('i'))).toBe('inventory');
    expect(panelKey(key('I'))).toBe('inventory');
    expect(panelKey(key('k'))).toBeNull();
  });

  it('are not panel keys with a modifier, held down, or typed into a field', () => {
    expect(panelKey(key('i', { ctrlKey: true }))).toBeNull();
    expect(panelKey(key('i', { altKey: true }))).toBeNull();
    expect(panelKey(key('i', { metaKey: true }))).toBeNull();
    expect(panelKey(key('j', { repeat: true }))).toBeNull();
    const Field = (globalThis as unknown as { HTMLTextAreaElement: new () => EventTarget }).HTMLTextAreaElement;
    expect(panelKey(key('i', { target: new Field() }))).toBeNull();
  });
});
