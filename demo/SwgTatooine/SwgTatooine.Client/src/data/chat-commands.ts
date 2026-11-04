import { parseTipAmount } from './wallet';
import { parseWayArgs, WAY_USAGE } from './waypoints';

/**
 * The slash commands (client.md C37): one registry, so `/help` lists every one and an unknown one is answered with the list. A command is its
 * name, a usage line, a help line and a pure parser; what it does is `state/chat-commands.ts`'s.
 */

/** A chat line that is a command: what it asks for, or why it cannot be done. */
export type ChatCommand =
  | { readonly kind: 'error'; readonly message: string }
  | { readonly kind: 'help' }
  /** Gives the selected player credits (C34). */
  | { readonly kind: 'tip'; readonly amount: number }
  /** Shows or hides the debug layer (C36); `null` toggles it. */
  | { readonly kind: 'debug'; readonly on: boolean | null }
  /** Makes a waypoint of the player's own (C38): at x z, or — `null` — at the selection, or where the character stands. */
  | {
      readonly kind: 'way';
      readonly x: number | null;
      readonly z: number | null;
      readonly name: string;
      readonly color: number;
    }
  /** Deletes the player's own waypoints of a name. */
  | { readonly kind: 'waydel'; readonly name: string }
  /** Lists the player's own waypoints in chat. */
  | { readonly kind: 'waylist' }
  /** A line to one player online, by name (mail.md § 3); `/r` to the last who told this player something. */
  | { readonly kind: 'tell'; readonly to: string; readonly text: string }
  | { readonly kind: 'reply'; readonly text: string }
  /** The mail window, writing to a player, about a subject, where given. */
  | { readonly kind: 'mail'; readonly to: string; readonly subject: string };

export interface CommandSpec {
  readonly name: string;
  readonly usage: string;
  readonly help: string;
  /** The command from its arguments, the line split on white space after the name. */
  readonly parse: (args: readonly string[]) => ChatCommand;
}

export const COMMANDS: readonly CommandSpec[] = [
  {
    name: 'help',
    usage: '/help',
    help: 'lists the commands',
    parse: () => ({ kind: 'help' }),
  },
  {
    name: 'tip',
    usage: '/tip <amount>',
    help: 'gives the selected player that many credits',
    parse: (args) => {
      const tip = parseTipAmount(args);
      return 'error' in tip ? { kind: 'error', message: tip.error } : { kind: 'tip', amount: tip.amount };
    },
  },
  {
    name: 'debug',
    usage: '/debug [on|off]',
    help: 'shows or hides the debug windows',
    parse: (args) => {
      const arg = (args[0] ?? '').toLowerCase();
      if (args.length === 0) {
        return { kind: 'debug', on: null };
      }

      return arg === 'on' || arg === 'off'
        ? { kind: 'debug', on: arg === 'on' }
        : { kind: 'error', message: 'Usage: /debug [on|off]' };
    },
  },
  {
    name: 'way',
    usage: '/way [x z] <name> [#colour]',
    help: 'makes a waypoint: at x z, else at the selection, else where you stand',
    parse: (args) => {
      const way = parseWayArgs(args);
      return way.ok
        ? { kind: 'way', x: way.x, z: way.z, name: way.name, color: way.color }
        : { kind: 'error', message: way.error };
    },
  },
  {
    name: 'waydel',
    usage: '/waydel <name>',
    help: 'deletes your waypoints of that name',
    parse: (args) =>
      args.length === 0
        ? { kind: 'error', message: `Usage: /waydel <name> — the name /waylist shows (${WAY_USAGE})` }
        : { kind: 'waydel', name: args.join(' ') },
  },
  {
    name: 'waylist',
    usage: '/waylist',
    help: 'lists your waypoints',
    parse: () => ({ kind: 'waylist' }),
  },
  {
    name: 'tell',
    usage: '/tell <player> <text>',
    help: 'says something to one player, wherever they are, if they are online',
    parse: (args) =>
      args.length < 2
        ? { kind: 'error', message: 'Usage: /tell <player> <text>' }
        : { kind: 'tell', to: args[0], text: args.slice(1).join(' ') },
  },
  {
    name: 'r',
    usage: '/r <text>',
    help: 'answers the last player who told you something',
    parse: (args) =>
      args.length === 0 ? { kind: 'error', message: 'Usage: /r <text>' } : { kind: 'reply', text: args.join(' ') },
  },
  {
    name: 'mail',
    usage: '/mail [player] [subject]',
    help: 'writes a mail, which waits for the player if they are offline (N opens your mail)',
    parse: (args) => ({ kind: 'mail', to: args[0] ?? '', subject: args.slice(1).join(' ') }),
  },
];

/**
 * Reads a chat line as a command: `null` for a line that is speech, which is any line not starting with `/`. The name is not case-sensitive.
 */
export function parseChatCommand(line: string): ChatCommand | null {
  const text = line.trim();
  if (!text.startsWith('/')) {
    return null;
  }

  const [name = '', ...args] = text.slice(1).split(/\s+/);
  const spec = COMMANDS.find((c) => c.name === name.toLowerCase());
  return spec === undefined
    ? {
        kind: 'error',
        message: `Unknown command /${name} — the commands are ${COMMANDS.map((c) => `/${c.name}`).join(', ')}`,
      }
    : spec.parse(args.filter((a) => a.length > 0));
}

/** What `/help` says: a line per command, its usage and what it does. */
export function helpLines(): string[] {
  return COMMANDS.map((c) => `${c.usage} — ${c.help}`);
}
