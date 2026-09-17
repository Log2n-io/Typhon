import { describe, expect, it } from 'vitest';
import {
  CommandQueue,
  CommandRefused,
  MessageType,
  readCommands,
  type CatalogPlan,
  type FieldValues,
  type MessagePlan,
} from '../src/index.js';
import { catalogPlan } from './net-support.js';

/*
 * One sequence space per session, one `clientTick` per message, the batch split under `limits.clientMessageBytes`, and a
 * local token bucket per command type so a client drops its own excess instead of having the server count it (§ 3, § 8).
 */

const plan: CatalogPlan = catalogPlan();
const steer: MessagePlan = plan.commandByName('Steer')!;
const region: MessagePlan = plan.clientRegion!;

function steerValues(heading: number): FieldValues {
  return { boost: 1, stance: 1, heading, note: 'go', speed: 0.5 };
}

function regionValues(x: number): FieldValues {
  return { altitudeM: 10, budgetKiBps: 64, vertices: [x, 0, x + 10, 0, x + 10, 10] };
}

interface Decoded {
  readonly seqs: number[];
  readonly clientTicks: number[];
  readonly types: string[];
}

function decode(messages: Uint8Array[]): Decoded {
  const seqs: number[] = [];
  const clientTicks: number[] = [];
  const types: string[] = [];
  for (const message of messages) {
    expect(message[0]).toBe(MessageType.Commands);
    readCommands(message, plan, {
      command: (type, seq, clientTick) => {
        types.push(type.name);
        seqs.push(seq);
        clientTicks.push(clientTick);
      },
      number: () => undefined,
      text: () => undefined,
      bytes: () => undefined,
      list: () => undefined,
    });
  }

  return { seqs, clientTicks, types };
}

describe('CommandQueue', () => {
  it('batches a frame into one message, numbering commands from one sequence space', () => {
    const queue = new CommandQueue({ plan, now: () => 0 });
    expect(queue.enqueue(steer, steerValues(0))).toBe(1);
    expect(queue.enqueue(steer, steerValues(0.5))).toBe(2);
    expect(queue.pendingCount).toBe(2);

    const messages: Uint8Array[] = [];
    expect(queue.flush(77, (m) => messages.push(m.slice()))).toBe(1);
    expect(queue.pendingCount).toBe(0);
    expect(decode(messages)).toEqual({ seqs: [1, 2], clientTicks: [77, 77], types: ['Steer', 'Steer'] });

    // Nothing pending, nothing sent.
    expect(queue.flush(78, () => undefined)).toBe(0);
  });

  it('wraps its sequence at 2^16, as serial arithmetic expects', () => {
    const queue = new CommandQueue({ plan, now: () => 0, firstSeq: 65_534 });
    const seqs = [
      queue.enqueue(steer, steerValues(0)),
      queue.enqueue(steer, steerValues(0)),
      queue.enqueue(steer, steerValues(0)),
    ];
    expect(seqs).toEqual([65_534, 65_535, 0]);
    expect(queue.nextSeq).toBe(1);
  });

  it('refuses a command its type’s bucket cannot pay for, and refills it over time', () => {
    let nowMs = 0;
    const queue = new CommandQueue({ plan, now: () => nowMs });
    // ClientRegion allows 5 a second, burst 5.
    for (let i = 0; i < 5; i++) {
      expect(queue.enqueue(region, regionValues(i))).toBeGreaterThanOrEqual(0);
    }

    expect(queue.enqueue(region, regionValues(9))).toBe(CommandRefused.RateLimited);
    expect(queue.rateLimited).toBe(1);

    nowMs = 200;
    expect(queue.enqueue(region, regionValues(9))).toBeGreaterThanOrEqual(0);
    // A type with no rate in the catalog is unlimited.
    for (let i = 0; i < 50; i++) {
      expect(queue.enqueue(steer, steerValues(0))).toBeGreaterThanOrEqual(0);
    }
  });

  it('keeps only the newest of a `latest` command, as the server’s slot would', () => {
    let nowMs = 0;
    const queue = new CommandQueue({ plan, now: () => nowMs });
    queue.enqueue(steer, steerValues(0));
    const first = queue.enqueue(region, regionValues(0));
    nowMs = 1000;
    const second = queue.enqueue(region, regionValues(500));
    expect(second).not.toBe(first);
    expect([queue.pendingCount, queue.coalescedCount]).toEqual([2, 1]);

    const messages: Uint8Array[] = [];
    queue.flush(5, (m) => messages.push(m.slice()));
    const decoded = decode(messages);
    expect(decoded.types).toEqual(['Steer', 'ClientRegion']);
    expect(decoded.seqs).toEqual([1, second]);
  });

  it('splits a batch that would exceed the client message limit, keeping one clientTick', () => {
    // The kitchen sink allows 1 KiB per client message.
    expect(plan.catalog.limits.clientMessageBytes).toBe(1024);
    const queue = new CommandQueue({ plan, now: () => 0 });
    for (let i = 0; i < 200; i++) {
      queue.enqueue(steer, steerValues(i / 1000));
    }

    const messages: Uint8Array[] = [];
    const count = queue.flush(9, (m) => messages.push(m.slice()));
    expect(count).toBeGreaterThan(1);
    expect(Math.max(...messages.map((m) => m.length))).toBeLessThanOrEqual(1024);
    const decoded = decode(messages);
    expect(decoded.seqs).toHaveLength(200);
    expect(new Set(decoded.clientTicks)).toEqual(new Set([9]));
  });

  it('refuses a command that could not fit a message on its own, and a type from another catalog', () => {
    const queue = new CommandQueue({ plan, now: () => 0, maxMessageBytes: 12 });
    expect(() => queue.enqueue(steer, steerValues(0))).toThrow(/above the 12 B limit/);

    const other = catalogPlan();
    expect(() => queue.enqueue(other.commandByName('Steer')!, steerValues(0))).toThrow(/does not belong/);
  });

  it('drops what a disconnect made pointless', () => {
    const queue = new CommandQueue({ plan, now: () => 0 });
    queue.enqueue(steer, steerValues(0));
    queue.clear();
    expect(queue.flush(1, () => undefined)).toBe(0);
  });
});
