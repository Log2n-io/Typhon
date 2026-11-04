import { beforeEach, describe, expect, it } from 'vitest';
import { COMMANDS, helpLines, parseChatCommand } from '../src/data/chat-commands';
import { runChatCommand } from '../src/state/chat-commands';
import { useChat } from '../src/state/chat-store';
import { useWindows } from '../src/state/windows-store';

/** The slash commands (client.md C37, U1-3 and U1-4): one registry, `/help` naming every command, `/debug` showing and hiding the debug layer. */

describe('the command registry', () => {
  it('reads speech as speech and a command by its name, in any case', () => {
    expect(parseChatCommand('hello')).toBeNull();
    expect(parseChatCommand('/HELP')).toEqual({ kind: 'help' });
    expect(parseChatCommand('  /debug  ')).toEqual({ kind: 'debug', on: null });
    expect(parseChatCommand('/debug ON')).toEqual({ kind: 'debug', on: true });
    expect(parseChatCommand('/debug off')).toEqual({ kind: 'debug', on: false });
    expect(parseChatCommand('/debug maybe')).toEqual({ kind: 'error', message: 'Usage: /debug [on|off]' });
  });

  it('answers an unknown command with every command there is', () => {
    const answer = parseChatCommand('/dance');
    expect(answer?.kind).toBe('error');
    for (const c of COMMANDS) {
      expect(answer?.kind === 'error' ? answer.message : '').toContain(`/${c.name}`);
    }
  });

  it('has /help name every command with its usage', () => {
    const lines = helpLines();
    expect(lines).toHaveLength(COMMANDS.length);
    for (const c of COMMANDS) {
      expect(lines.some((l) => l.startsWith(c.usage))).toBe(true);
    }
  });
});

describe('the commands carried out', () => {
  beforeEach(() => {
    useWindows.getState().load('commands-test', true);
  });

  it('/debug toggles the debug layer, and on and off set it, each said in chat', () => {
    const before = useChat.getState().lines.length;
    expect(useWindows.getState().layout.debug).toBe(false);
    expect(runChatCommand({ kind: 'debug', on: null })).toBe(true);
    expect(useWindows.getState().layout.debug).toBe(true);
    runChatCommand({ kind: 'debug', on: true });
    expect(useWindows.getState().layout.debug).toBe(true);
    runChatCommand({ kind: 'debug', on: false });
    expect(useWindows.getState().layout.debug).toBe(false);
    expect(useChat.getState().lines.length - before).toBe(3);
  });

  it('/help says a line per command', () => {
    const before = useChat.getState().lines.length;
    expect(runChatCommand({ kind: 'help' })).toBe(true);
    expect(useChat.getState().lines.length - before).toBe(COMMANDS.length);
  });
});
