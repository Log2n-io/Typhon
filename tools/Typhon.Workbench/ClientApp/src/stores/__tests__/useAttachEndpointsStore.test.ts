import { beforeEach, describe, expect, it } from 'vitest';
import { hostOf, isLoopbackEndpoint, useAttachEndpointsStore } from '@/stores/useAttachEndpointsStore';

describe('isLoopbackEndpoint', () => {
  it.each([
    'localhost',
    'localhost:9100',
    'LOCALHOST:9100',
    'tatooine.localhost:9100',
    '127.0.0.1',
    '127.0.0.1:9100',
    // The whole /8 is loopback, and a check that knew only 127.0.0.1 would badge this as remote.
    '127.0.0.2:9100',
    '127.1.2.3',
    '::1',
    '[::1]:9100',
    '0:0:0:0:0:0:0:1',
  ])('%s stays on this machine', (endpoint) => {
    expect(isLoopbackEndpoint(endpoint)).toBe(true);
  });

  it.each([
    'tatooine.demo.typhondb.io:9100',
    '10.0.0.5:9100',
    // One digit from loopback, and the difference is a socket to another host.
    '128.0.0.1:9100',
    '12.7.0.1',
    '[2001:db8::1]:9100',
    'origin.tatooine.demo.typhondb.io',
  ])('%s crosses the network', (endpoint) => {
    expect(isLoopbackEndpoint(endpoint)).toBe(false);
  });

  it('is false for an empty endpoint rather than defaulting to safe', () => {
    // A blank field is not an endpoint, and badging it "loopback" would put a reassuring label on nothing. The tab
    // asks this only once the shape check has passed.
    expect(isLoopbackEndpoint('')).toBe(false);
    expect(isLoopbackEndpoint('   ')).toBe(false);
  });
});

describe('hostOf', () => {
  it('keeps a bracketed IPv6 address whole', () => {
    // Splitting on the first colon — the obvious implementation — returns '[' here, which is how every IPv6 endpoint
    // would have been classified as remote.
    expect(hostOf('[::1]:9100')).toBe('[::1]');
    expect(hostOf('[2001:db8::1]:9100')).toBe('[2001:db8::1]');
  });

  it('treats a bare IPv6 address as a host, not a host and a port', () => {
    expect(hostOf('::1')).toBe('::1');
    expect(hostOf('2001:db8::1')).toBe('2001:db8::1');
  });

  it('splits an ordinary host:port and passes a bare host through', () => {
    expect(hostOf('tatooine.demo.typhondb.io:9100')).toBe('tatooine.demo.typhondb.io');
    expect(hostOf('localhost')).toBe('localhost');
  });
});

describe('useAttachEndpointsStore', () => {
  beforeEach(() => {
    useAttachEndpointsStore.setState({ entries: [] });
  });

  it('records an attach at the front', () => {
    const { record } = useAttachEndpointsStore.getState();
    record('localhost:9100');
    record('10.0.0.5:9100');

    expect(useAttachEndpointsStore.getState().entries.map((e) => e.endpoint)).toEqual([
      '10.0.0.5:9100',
      'localhost:9100',
    ]);
  });

  it('moves a re-attached endpoint to the front instead of duplicating it', () => {
    const { record } = useAttachEndpointsStore.getState();
    record('a:9100');
    record('b:9100');
    record('a:9100');

    expect(useAttachEndpointsStore.getState().entries.map((e) => e.endpoint)).toEqual(['a:9100', 'b:9100']);
  });

  it('matches an endpoint case-insensitively, since a host has no case', () => {
    const { record } = useAttachEndpointsStore.getState();
    record('Tatooine.Demo:9100');
    record('tatooine.demo:9100');

    const entries = useAttachEndpointsStore.getState().entries;
    expect(entries).toHaveLength(1);
    // The spelling of the most recent attach wins: it is what the user just typed.
    expect(entries[0].endpoint).toBe('tatooine.demo:9100');
  });

  it('keeps a label through a later attach that carries none', () => {
    const { record, rename } = useAttachEndpointsStore.getState();
    record('10.0.0.5:9100');
    rename('10.0.0.5:9100', 'tatooine demo');
    record('10.0.0.5:9100');

    expect(useAttachEndpointsStore.getState().entries[0].label).toBe('tatooine demo');
  });

  it('clears a label renamed to blank rather than storing an empty one', () => {
    const { record, rename } = useAttachEndpointsStore.getState();
    record('10.0.0.5:9100', 'named');
    rename('10.0.0.5:9100', '   ');

    expect(useAttachEndpointsStore.getState().entries[0].label).toBeUndefined();
  });

  it('caps the list at twelve, dropping the oldest', () => {
    const { record } = useAttachEndpointsStore.getState();
    for (let i = 0; i < 15; i++) {
      record(`host-${i}:9100`);
    }

    const entries = useAttachEndpointsStore.getState().entries;
    expect(entries).toHaveLength(12);
    expect(entries[0].endpoint).toBe('host-14:9100');
    expect(entries.map((e) => e.endpoint)).not.toContain('host-0:9100');
  });

  it('ignores a blank endpoint', () => {
    const { record } = useAttachEndpointsStore.getState();
    record('   ');

    expect(useAttachEndpointsStore.getState().entries).toHaveLength(0);
  });

  it('removes and clears', () => {
    const { record, remove, clear } = useAttachEndpointsStore.getState();
    record('a:9100');
    record('b:9100');

    // Spelled with a different case on purpose: forgetting an endpoint must not depend on how it is typed.
    remove('A:9100');
    expect(useAttachEndpointsStore.getState().entries.map((e) => e.endpoint)).toEqual(['b:9100']);

    clear();
    expect(useAttachEndpointsStore.getState().entries).toHaveLength(0);
  });
});
