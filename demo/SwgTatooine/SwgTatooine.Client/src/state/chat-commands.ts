import type { ChatCommand } from '../data/wallet';
import { useChat } from './chat-store';
import { useUi } from './ui-store';
import { useWallet } from './wallet-store';

/**
 * Carries out a chat command (`parseChatCommand`): its refusals and its answers are the client's own lines.
 *
 * `/tip` goes to the selection — the server refuses anything that is not another player with a wallet within reach, and
 * says why through `onTipRefused`.
 *
 * @returns Whether the line is done with: `false` when nothing left the client, so the box keeps it to send again, as
 *   it keeps a line of speech the rate refused.
 */
export function runChatCommand(command: ChatCommand): boolean {
  const chat = useChat.getState();
  if (command.kind === 'error') {
    chat.notice(command.message);
    return true;
  }

  const target = useUi.getState().selectedNetId;
  if (target === 0) {
    chat.notice('/tip: select a player first — click one, then /tip <amount>');
    return true;
  }

  if (useWallet.getState().actions?.tip(target, command.amount) !== true) {
    chat.notice('/tip: not sent — wait a moment and press Enter again');
    return false;
  }

  return true;
}
