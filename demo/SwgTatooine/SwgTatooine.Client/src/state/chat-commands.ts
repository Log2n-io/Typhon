import { helpLines, type ChatCommand } from '../data/chat-commands';
import { useChat } from './chat-store';
import { useUi } from './ui-store';
import { useWallet } from './wallet-store';
import { useWindows } from './windows-store';
import { useWaypoints } from './waypoints-store';
import { useStats } from './stats-store';
import { useMap } from './map-store';
import { namedWaypoints } from '../data/waypoints';

/**
 * Carries out a chat command (`parseChatCommand`): its refusals and its answers are the client's own lines.
 *
 * `/help` and `/debug` are answered here. `/tip` goes to the selection — the server refuses anything that is not another player with a wallet within reach, and
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

  if (command.kind === 'help') {
    for (const line of helpLines()) {
      chat.notice(line);
    }

    return true;
  }

  if (command.kind === 'way') {
    return makeWaypoint(command.name, command.x, command.z, command.color);
  }

  if (command.kind === 'waydel') {
    const waypoints = useWaypoints.getState();
    const named = namedWaypoints(waypoints.list ?? [], command.name);
    if (named.length === 0) {
      chat.notice(`/waydel: you have no waypoint named "${command.name}" — /waylist shows them`);
      return true;
    }

    // One a command: the Workshop takes a few a second, so a name held by several is deleted one per Enter.
    if (!waypoints.remove(named[0].id)) {
      chat.notice('/waydel: not sent — wait a moment and press Enter again');
      return false;
    }

    chat.notice(
      named.length === 1
        ? `Waypoint "${named[0].name}" deleted`
        : `One of your ${String(named.length)} "${named[0].name}" deleted: again for the next`,
    );
    return true;
  }

  if (command.kind === 'waylist') {
    const list = useWaypoints.getState().list;
    if (list === null || list.length === 0) {
      chat.notice(
        list === null
          ? 'Your waypoints are not read yet'
          : 'You have no waypoints — /way <name> makes one where you stand',
      );
      return true;
    }

    for (const w of list) {
      chat.notice(`${w.active ? '▶ ' : ''}${w.name} — ${String(Math.round(w.x))}, ${String(Math.round(w.z))}`);
    }

    return true;
  }

  if (command.kind === 'debug') {
    const windows = useWindows.getState();
    windows.setDebug(command.on ?? !windows.layout.debug);
    chat.notice(
      useWindows.getState().layout.debug ? 'Debug windows shown — /debug again hides them' : 'Debug windows hidden',
    );
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

/**
 * `/way` carried out (C38): at the coordinates given, else at the selection when it has a place, else where the character stands. The server makes it in
 * the character's realm and refuses a place off the planet.
 */
function makeWaypoint(name: string, x: number | null, z: number | null, color: number): boolean {
  const chat = useChat.getState();
  let at: { readonly x: number; readonly z: number } | null = x !== null && z !== null ? { x, z } : null;
  let where = 'there';
  if (at === null) {
    const selected = useUi.getState().selectedNetId;
    const inspection = useStats.getState().stats?.inspection ?? null;
    if (selected !== 0 && inspection?.netId === selected && inspection.hasPosition) {
      at = { x: inspection.x, z: inspection.z };
      where = 'at the selection';
    } else {
      const player = useMap.getState().player;
      at = player === null ? null : { x: player.x, z: player.z };
      where = 'where you stand';
    }
  }

  if (at === null) {
    chat.notice('/way: no place to put it — give x z, select something, or stand on the planet');
    return true;
  }

  if (!useWaypoints.getState().create(name, at.x, at.z, color)) {
    chat.notice('/way: not sent — wait a moment and press Enter again');
    return false;
  }

  chat.notice(`Waypoint "${name}" made ${where} (${String(Math.round(at.x))}, ${String(Math.round(at.z))})`);
  return true;
}
