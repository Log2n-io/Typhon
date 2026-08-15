// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { QueryClient } from '@tanstack/react-query';

/**
 * Switching capture must drop the profiler query cache.
 *
 * Every profiler query keys on the SESSION id alone — `['profiler', 'metadata', sessionId]`, `['profiler',
 * 'cpu-frames', sessionId]`, and so on. Profiles are session sub-resources, so that id deliberately does not change
 * when you switch capture. The observable symptom: the top banner updates (it reads `activeProfileId` off the session
 * store) while every profiler view below it keeps rendering the previous recording.
 *
 * This pins the cache semantics `useProfileList.invalidate` relies on, at the level where the bug actually lived.
 * A panel-level test would not have caught it: the panel was correct, the cache underneath it was not.
 */
describe('profile switch — profiler cache', () => {
  let client: QueryClient;

  afterEach(() => {
    client?.clear();
  });

  const seedProfilerCache = (sessionId: string) => {
    client.setQueryData(['profiler', 'metadata', sessionId], { capture: 'first' });
    client.setQueryData(['profiler', 'cpu-frames', sessionId], { frames: 1 });
    client.setQueryData(['profiler', 'source-locations', sessionId], { locations: 1 });
    client.setQueryData(['sessions', sessionId, 'profiles'], { profiles: ['a', 'b'] });
  };

  it('removeQueries on the profiler root drops every profiler entry for the unchanged session id', () => {
    client = new QueryClient();
    seedProfilerCache('session-1');

    client.removeQueries({ queryKey: ['profiler'] });

    expect(client.getQueryData(['profiler', 'metadata', 'session-1'])).toBeUndefined();
    expect(client.getQueryData(['profiler', 'cpu-frames', 'session-1'])).toBeUndefined();
    expect(client.getQueryData(['profiler', 'source-locations', 'session-1'])).toBeUndefined();
  });

  it('leaves the profiles list alone — it is what tells you which capture is now active', () => {
    client = new QueryClient();
    seedProfilerCache('session-1');

    client.removeQueries({ queryKey: ['profiler'] });

    expect(client.getQueryData(['sessions', 'session-1', 'profiles'])).toEqual({ profiles: ['a', 'b'] });
  });

  it('invalidate alone would NOT do — the stale entry survives and is served on the next render', () => {
    // This is why the fix uses removeQueries. An invalidated query is marked stale and refetched, but its cached data
    // is still returned synchronously, so the new capture would open showing one frame of the old one.
    client = new QueryClient();
    seedProfilerCache('session-1');

    void client.invalidateQueries({ queryKey: ['profiler'] });

    expect(client.getQueryData(['profiler', 'metadata', 'session-1'])).toEqual({ capture: 'first' });
  });
});
