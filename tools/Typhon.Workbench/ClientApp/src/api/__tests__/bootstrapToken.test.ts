// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';

// The module keeps captured state (token in sessionStorage, db path in a module variable), so each test
// gets a fresh module + clean DOM. `vi.resetModules()` + dynamic import gives every test its own copy.
describe('bootstrapToken', () => {
  beforeEach(() => {
    vi.resetModules();
    window.sessionStorage.clear();
    window.history.replaceState(null, '', '/');
  });

  it('captures the token from the URL fragment, stores it, and strips the fragment', async () => {
    window.location.hash = '#wbtoken=TOKEN123';
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getBootstrapToken()).toBe('TOKEN123');
    expect(window.sessionStorage.getItem('wb.bootstrapToken')).toBe('TOKEN123');
    // The fragment must be gone so the token never lingers in the address bar / history / referrer.
    expect(window.location.hash).toBe('');
  });

  it('captures the optional db path from the fragment', async () => {
    const dbPath = 'C:\\data\\my.typhon';
    window.location.hash = `#wbtoken=T&db=${encodeURIComponent(dbPath)}`;
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getInitialDbPath()).toBe(dbPath);
    expect(window.location.hash).toBe('');
  });

  it('is a no-op with no fragment (Vite dev-proxy mode)', async () => {
    window.location.hash = '';
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getBootstrapToken()).toBeNull();
    expect(mod.getInitialDbPath()).toBeNull();
  });
});
