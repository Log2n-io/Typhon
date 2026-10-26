import { describe, expect, it } from 'vitest';
import { crowdsTerminal, TERMINAL_CROWD_M, type TicketTerminal } from '../src/app/ticket-terminal';

// A terminal whose box spans x 10-11, z 20-21, used from a stand point 2 m south of it with a 1.5 m reach.
const use: TicketTerminal = { x: 10.5, z: 18, radiusM: 1.5, corners: null };
const min = [10, 0, 20];
const max = [11, 2, 21];

describe('crowdsTerminal', () => {
  it('counts a body inside the terminal box as at the terminal', () => {
    expect(crowdsTerminal(10.5, 20.5, use, min, max)).toBe(true);
  });

  it('counts a body queueing at the stand point as at the terminal', () => {
    expect(crowdsTerminal(10.5, 18, use, min, max)).toBe(true);
    expect(crowdsTerminal(10.5, 18 - use.radiusM - TERMINAL_CROWD_M + 0.1, use, min, max)).toBe(true);
  });

  it('counts a body just beside the box, within the crowd margin, as at the terminal', () => {
    expect(crowdsTerminal(11 + TERMINAL_CROWD_M - 0.1, 20.5, use, min, max)).toBe(true);
  });

  it('leaves a body away from the terminal to win the pick', () => {
    expect(crowdsTerminal(10.5, 18 - use.radiusM - TERMINAL_CROWD_M - 0.1, use, min, max)).toBe(false);
    expect(crowdsTerminal(11 + TERMINAL_CROWD_M + 0.1, 20.5, use, min, max)).toBe(false);
    expect(crowdsTerminal(30, 40, use, min, max)).toBe(false);
  });
});
