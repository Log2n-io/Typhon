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

  it('captures the optional schema path (typhon ui --open-db) as an initial schema DLL', async () => {
    const dbPath = 'C:\\app\\swg-guide.typhon';
    const schema = 'C:\\app\\bin\\Debug\\net10.0\\MyApp.dll';
    window.location.hash = `#wbtoken=T&db=${encodeURIComponent(dbPath)}&schema=${encodeURIComponent(schema)}`;
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getInitialDbPath()).toBe(dbPath);
    expect(mod.getInitialSchemaPaths()).toEqual([schema]);
  });

  it('defaults schema paths to an empty array when none is passed', async () => {
    window.location.hash = '#wbtoken=T&db=C%3A%5Cx.typhon';
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getInitialSchemaPaths()).toEqual([]);
  });

  it('is a no-op with no fragment (Vite dev-proxy mode)', async () => {
    window.location.hash = '';
    const mod = await import('../bootstrapToken');

    mod.captureLaunchParamsFromUrl();

    expect(mod.getBootstrapToken()).toBeNull();
    expect(mod.getInitialDbPath()).toBeNull();
  });
});

// Regression guard for the `typhon ui` 401 bug: the profiler polling hooks (and every other hand-rolled fetch)
// build headers via applyWorkbenchAuthHeaders. If it stops attaching the bootstrap token, those requests 401 under
// `typhon ui` (no Vite dev-proxy to inject it) — while still passing in dev, so this test is the only tripwire.
describe('applyWorkbenchAuthHeaders', () => {
  beforeEach(() => {
    vi.resetModules();
    window.sessionStorage.clear();
    window.history.replaceState(null, '', '/');
  });

  it('attaches the bootstrap token (X-Workbench-Token) once one has been captured', async () => {
    window.location.hash = '#wbtoken=BOOT123';
    const mod = await import('../bootstrapToken');
    mod.captureLaunchParamsFromUrl();

    const headers = mod.applyWorkbenchAuthHeaders(new Headers(), 'SESS456');

    expect(headers.get('X-Workbench-Token')).toBe('BOOT123');
    expect(headers.get('X-Session-Token')).toBe('SESS456');
    expect(headers.get('X-Workbench-Api')).toBe('1');
  });

  it('does NOT attach X-Workbench-Token in dev-proxy mode (no captured token) — the proxy injects it', async () => {
    // No fragment → getBootstrapToken() is null. The helper must be strictly additive: it can never invent a
    // token, so the dev-proxy path (which supplies the header server-side) is byte-for-byte unchanged.
    const mod = await import('../bootstrapToken');

    const headers = mod.applyWorkbenchAuthHeaders(new Headers(), null);

    expect(headers.has('X-Workbench-Token')).toBe(false);
    expect(headers.has('X-Session-Token')).toBe(false);
    expect(headers.get('X-Workbench-Api')).toBe('1');
  });

  it('never overwrites a header the caller already set', async () => {
    window.location.hash = '#wbtoken=BOOT123';
    const mod = await import('../bootstrapToken');
    mod.captureLaunchParamsFromUrl();

    const preset = new Headers({ 'X-Workbench-Token': 'CALLER', 'Content-Type': 'application/json' });
    const headers = mod.applyWorkbenchAuthHeaders(preset, 'SESS456');

    expect(headers.get('X-Workbench-Token')).toBe('CALLER');
    expect(headers.get('Content-Type')).toBe('application/json');
  });
});
