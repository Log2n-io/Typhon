import { beforeEach, describe, expect, it } from 'vitest';
import { Connection, MessageType, parsePing, PingScheduler, type PingMessage } from '../src/index.js';
import { FakeSocket, FakeTimers, pongMessage, welcomeMessage } from './net-support.js';

/*
 * The mandatory PING loop (§ 3): `pingHz` from the catalog, `lastAppliedTick` from the store — the server's lag skip
 * reads it (02 § 6) — and the round trip the PONG answers with.
 */

describe('PingScheduler', () => {
  it('pings at once, then at the catalog rate, carrying the newest applied tick', () => {
    const timers = new FakeTimers();
    const pings: PingMessage[] = [];
    let applied = 0;
    const scheduler = new PingScheduler({
      pingHz: 4,
      send: (ping) => pings.push(ping),
      lastAppliedTick: () => applied,
      timers,
      now: () => timers.now,
    });

    scheduler.start();
    expect(pings).toHaveLength(1);
    expect(pings[0]!.lastAppliedTick).toBe(0);

    applied = 41;
    timers.advance(250);
    expect(pings).toHaveLength(2);
    expect(pings[1]!.lastAppliedTick).toBe(41);

    applied = 42;
    timers.advance(1000);
    expect(pings).toHaveLength(6);
    expect(pings[5]!.lastAppliedTick).toBe(42);
    expect(pings[5]!.clientMs).toBe(1250);

    scheduler.stop();
    timers.advance(1000);
    expect(pings).toHaveLength(6);
    expect(timers.pending).toBe(0);
  });

  it('measures the round trip from the PONG that echoes its clientMs, and smooths it', () => {
    const timers = new FakeTimers();
    const pings: PingMessage[] = [];
    const scheduler = new PingScheduler({
      pingHz: 4,
      send: (ping) => pings.push(ping),
      lastAppliedTick: () => 0,
      timers,
      now: () => timers.now,
      rttWeight: 0.5,
    });

    scheduler.start();
    expect(scheduler.outstanding).toBe(1);
    timers.advance(40);
    scheduler.onPong({ clientMs: pings[0]!.clientMs, tick: 9, usIntoTick: 1 });
    expect([scheduler.lastRttMs, scheduler.rttMs, scheduler.serverTick]).toEqual([40, 40, 9]);
    expect([scheduler.pingsSent, scheduler.pongsReceived, scheduler.outstanding]).toEqual([1, 1, 0]);

    // The next round trip is twice as long; the smoothed estimate moves half way.
    timers.advance(210);
    scheduler.onPong({ clientMs: pings[1]!.clientMs, tick: 10, usIntoTick: 0 }, timers.now + 80);
    expect(scheduler.lastRttMs).toBe(80);
    expect(scheduler.rttMs).toBe(60);
  });

  it('measures across a clientMs wrap, since both sides are 32-bit', () => {
    const timers = new FakeTimers();
    const scheduler = new PingScheduler({
      pingHz: 1,
      send: () => undefined,
      lastAppliedTick: () => 0,
      timers,
      now: () => 0,
    });
    // The ping went out just before 2^32 ms; the pong arrived just after.
    scheduler.onPong({ clientMs: 0xffffff00, tick: 1, usIntoTick: 0 }, 0x0000_0030);
    expect(scheduler.lastRttMs).toBe(0x130);
  });

  it('refuses a rate that is not positive', () => {
    expect(
      () =>
        new PingScheduler({
          pingHz: 0,
          send: () => undefined,
          lastAppliedTick: () => 0,
        }),
    ).toThrow(RangeError);
  });
});

describe('PingScheduler over a connection', () => {
  beforeEach(() => {
    FakeSocket.reset();
  });

  it('sends PING messages on the wire and takes its rate from the catalog', () => {
    const timers = new FakeTimers();
    let scheduler: PingScheduler | null = null;
    const connection = new Connection({
      url: 'wss://example.test/typhon',
      socket: (url, protocols) => new FakeSocket(url, protocols),
      timers,
      now: () => timers.now,
      handlers: {
        onWelcome: (session, c) => {
          scheduler = new PingScheduler({
            pingHz: session.plan.catalog.tick.pingHz,
            send: (ping) => {
              c.sendPing(ping);
            },
            lastAppliedTick: () => 12,
            timers,
            now: () => timers.now,
          });
          scheduler.start();
        },
        onPong: (pong, recvMs) => scheduler?.onPong(pong, recvMs),
      },
    });

    connection.connect();
    FakeSocket.latest.open();
    FakeSocket.latest.deliver(welcomeMessage());
    const sent = FakeSocket.latest.sent;
    // HELLO, then the first PING.
    expect(sent[1]![0]).toBe(MessageType.Ping);
    expect(parsePing(sent[1]!).lastAppliedTick).toBe(12);

    // The kitchen-sink catalog pings 4 times a second.
    timers.advance(1000);
    expect(sent.filter((m) => m[0] === MessageType.Ping)).toHaveLength(5);

    timers.advance(30);
    FakeSocket.latest.deliver(pongMessage(parsePing(sent[sent.length - 1]!).clientMs, 5));
    expect(scheduler!.lastRttMs).toBe(30);
  });
});
