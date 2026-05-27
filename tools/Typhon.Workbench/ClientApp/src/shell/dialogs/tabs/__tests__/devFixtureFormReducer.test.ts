// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  BULK_LOAD_AUTO_THRESHOLD,
  CUSTOM_CONFIG_STORAGE_KEY,
  DEFAULT_DATABASE_NAME,
  PRESET_CONFIGS,
  devFixtureFormReducer,
  initialDevFixtureFormState,
  loadCustomConfigFromStorage,
  saveCustomConfigToStorage,
  totalEntityCount,
  validateDatabaseName,
  type DevFixtureFormState,
  type FixtureConfig,
} from '../devFixtureFormReducer';

/**
 * Pure-reducer + localStorage tests for the Dev Fixture form. The reducer is the source of truth for preset
 * selection ↔ Custom-flip semantics + per-field clamping; the localStorage helpers persist the user's Custom
 * config so re-opening the dialog lands on their last shape. These tests run without React.
 */

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  localStorage.clear();
});

function initialState(): DevFixtureFormState {
  return initialDevFixtureFormState();
}

describe('devFixtureFormReducer — preset selection', () => {
  it('initial state defaults to Default preset when no Custom is stored', () => {
    const s = initialState();
    expect(s.presetId).toBe('default');
    expect(s.config).toEqual(PRESET_CONFIGS.default);
    expect(s.showAdvanced).toBe(false);
    expect(s.databaseName).toBe(DEFAULT_DATABASE_NAME);
    expect(s.databaseNameError).toBeNull();
  });

  it('initial state restores the saved Custom config when one exists in localStorage', () => {
    const custom: FixtureConfig = { ...PRESET_CONFIGS.default, compAArchCount: 42 };
    saveCustomConfigToStorage(custom);
    const s = initialState();
    expect(s.presetId).toBe('custom');
    expect(s.config.compAArchCount).toBe(42);
  });

  it('selecting a non-custom preset replaces config with that preset\'s canonical values', () => {
    const start = initialState();
    const next = devFixtureFormReducer(start, { type: 'select-preset', id: 'tiny' });
    expect(next.presetId).toBe('tiny');
    expect(next.config).toEqual(PRESET_CONFIGS.tiny);
  });

  it('selecting "custom" loads the stored config (or keeps current when none stored)', () => {
    const custom: FixtureConfig = { ...PRESET_CONFIGS.default, particleArchCount: 99 };
    saveCustomConfigToStorage(custom);
    const start = devFixtureFormReducer(initialState(), { type: 'select-preset', id: 'stress' });
    expect(start.presetId).toBe('stress');
    const next = devFixtureFormReducer(start, { type: 'select-preset', id: 'custom' });
    expect(next.presetId).toBe('custom');
    expect(next.config.particleArchCount).toBe(99);
  });
});

describe('devFixtureFormReducer — edits flip preset to Custom', () => {
  it('set-count flips preset to "custom" + updates the field', () => {
    const start = initialState();
    expect(start.presetId).toBe('default');
    const next = devFixtureFormReducer(start, { type: 'set-count', key: 'guildArchCount', value: 17 });
    expect(next.presetId).toBe('custom');
    expect(next.config.guildArchCount).toBe(17);
  });

  it('set-count clamps to non-negative integers', () => {
    const start = initialState();
    const negative = devFixtureFormReducer(start, { type: 'set-count', key: 'compAArchCount', value: -5 });
    expect(negative.config.compAArchCount).toBe(0);
    const floated = devFixtureFormReducer(start, { type: 'set-count', key: 'compAArchCount', value: 3.9 });
    expect(floated.config.compAArchCount).toBe(3);
  });

  it('set-fragmentation clamps to [0, 1]', () => {
    const start = initialState();
    expect(devFixtureFormReducer(start, { type: 'set-fragmentation', value: 1.5 }).config.particleFragmentation).toBe(1);
    expect(devFixtureFormReducer(start, { type: 'set-fragmentation', value: -0.5 }).config.particleFragmentation).toBe(0);
    expect(devFixtureFormReducer(start, { type: 'set-fragmentation', value: 0.42 }).config.particleFragmentation).toBe(0.42);
  });

  it('set-seed flips to custom and floors fractional input', () => {
    const start = initialState();
    const next = devFixtureFormReducer(start, { type: 'set-seed', value: 42.9 });
    expect(next.presetId).toBe('custom');
    expect(next.config.seed).toBe(42);
  });

  it('randomize-seed produces a positive 31-bit integer + flips to custom', () => {
    const start = initialState();
    const next = devFixtureFormReducer(start, { type: 'randomize-seed' });
    expect(next.presetId).toBe('custom');
    expect(Number.isInteger(next.config.seed)).toBe(true);
    expect(next.config.seed).toBeGreaterThanOrEqual(0);
    expect(next.config.seed).toBeLessThan(0x7fffffff);
  });

  it('toggle-advanced flips visibility independently of preset', () => {
    const start = initialState();
    expect(start.showAdvanced).toBe(false);
    const opened = devFixtureFormReducer(start, { type: 'toggle-advanced' });
    expect(opened.showAdvanced).toBe(true);
    expect(opened.presetId).toBe('default'); // toggling advanced does NOT flip to custom
    const closed = devFixtureFormReducer(opened, { type: 'toggle-advanced' });
    expect(closed.showAdvanced).toBe(false);
  });
});

describe('localStorage helpers', () => {
  it('saveCustomConfigToStorage round-trips through loadCustomConfigFromStorage', () => {
    const cfg: FixtureConfig = { ...PRESET_CONFIGS.default, compAArchCount: 7, seed: 99 };
    saveCustomConfigToStorage(cfg);
    const loaded = loadCustomConfigFromStorage();
    expect(loaded).toEqual(cfg);
  });

  it('returns null when no entry exists', () => {
    expect(loadCustomConfigFromStorage()).toBeNull();
  });

  it('returns null on corrupted JSON', () => {
    localStorage.setItem(CUSTOM_CONFIG_STORAGE_KEY, 'not json at all');
    expect(loadCustomConfigFromStorage()).toBeNull();
  });

  it('returns null when any field is non-finite (NaN / Infinity)', () => {
    const bad = { ...PRESET_CONFIGS.default, seed: Number.NaN };
    localStorage.setItem(CUSTOM_CONFIG_STORAGE_KEY, JSON.stringify(bad));
    expect(loadCustomConfigFromStorage()).toBeNull();
  });

  it('falls back to Default values for missing fields (forward-compat with future schema adds)', () => {
    // Drop the seed field — older saved configs that predate a hypothetical new field.
    const partial = { ...PRESET_CONFIGS.default } as Partial<FixtureConfig>;
    delete partial.seed;
    localStorage.setItem(CUSTOM_CONFIG_STORAGE_KEY, JSON.stringify(partial));
    const loaded = loadCustomConfigFromStorage();
    expect(loaded).not.toBeNull();
    expect(loaded?.seed).toBe(PRESET_CONFIGS.default.seed); // filled from the merge fallback
  });
});

describe('preset values', () => {
  it('every preset has finite numeric values for every required field', () => {
    for (const id of Object.keys(PRESET_CONFIGS) as Array<keyof typeof PRESET_CONFIGS>) {
      const cfg = PRESET_CONFIGS[id];
      for (const [key, value] of Object.entries(cfg)) {
        expect(typeof value).toBe('number');
        expect(Number.isFinite(value)).toBe(true);
        if (key === 'particleFragmentation') {
          expect(value).toBeGreaterThanOrEqual(0);
          expect(value).toBeLessThanOrEqual(1);
        } else if (key !== 'seed') {
          expect(value).toBeGreaterThanOrEqual(0);
        }
      }
    }
  });

  it('database-name validation matches the C# regex (1-64 chars, alnum + dash + underscore)', () => {
    // Valid cases
    expect(validateDatabaseName('base-tests')).toBeNull();
    expect(validateDatabaseName('stress_v2')).toBeNull();
    expect(validateDatabaseName('A')).toBeNull();
    expect(validateDatabaseName('a'.repeat(64))).toBeNull();
    // Invalid: empty / too long / disallowed chars
    expect(validateDatabaseName('')).not.toBeNull();
    expect(validateDatabaseName('   ')).not.toBeNull(); // trim → empty
    expect(validateDatabaseName('a'.repeat(65))).not.toBeNull();
    expect(validateDatabaseName('has spaces')).not.toBeNull();
    expect(validateDatabaseName('with/slash')).not.toBeNull();
    expect(validateDatabaseName('with.dots')).not.toBeNull();
    expect(validateDatabaseName('emoji-🚀')).not.toBeNull();
  });

  it('set-database-name updates the name AND its inline error without flipping preset to custom', () => {
    const start = initialState();
    // Valid name
    const valid = devFixtureFormReducer(start, { type: 'set-database-name', value: 'stress-v2' });
    expect(valid.databaseName).toBe('stress-v2');
    expect(valid.databaseNameError).toBeNull();
    expect(valid.presetId).toBe('default'); // editing the filename does NOT flip the preset

    // Invalid name surfaces error inline (don't block the input — let the user correct it)
    const invalid = devFixtureFormReducer(start, { type: 'set-database-name', value: 'has spaces' });
    expect(invalid.databaseName).toBe('has spaces'); // value stored verbatim
    expect(invalid.databaseNameError).not.toBeNull();
    expect(invalid.presetId).toBe('default');
  });

  it('Default preset matches the C# `FixtureConfig.Default` numeric values', () => {
    // If this fails, the server and client have drifted apart — the on-disk hash will be different
    // for "Default" between the two, which breaks the cache-hit fast path.
    expect(PRESET_CONFIGS.default).toEqual({
      compAArchCount: 1_000,
      compABArchCount: 500,
      compABCArchCount: 500,
      compDArchCount: 200,
      guildArchCount: 50,
      playerArchCount: 300,
      particleArchCount: 2_000,
      particleFragmentation: 0.40,
      seed: 123_456_789,
    });
  });
});

describe('devFixtureFormReducer — useBulkLoad auto-toggle', () => {
  it('initial state has useBulkLoad=false for Default preset (below threshold)', () => {
    const s = initialDevFixtureFormState();
    expect(totalEntityCount(PRESET_CONFIGS.default)).toBeLessThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(false);
  });

  it('selecting Stress preset (~420 k total) leaves useBulkLoad=false (still under threshold)', () => {
    const s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'stress' });
    expect(totalEntityCount(PRESET_CONFIGS.stress)).toBeLessThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(false);
  });

  it('selecting a >5 M custom config auto-toggles useBulkLoad to true', () => {
    const customLarge: FixtureConfig = {
      ...PRESET_CONFIGS.default,
      particleArchCount: 8_000_000,
    };
    saveCustomConfigToStorage(customLarge);
    const s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'custom' });
    expect(totalEntityCount(s.config)).toBeGreaterThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(true);
  });

  it('"set-use-bulk-load" overrides the auto-toggle (user choice wins)', () => {
    // Auto-on via large custom config…
    const customLarge: FixtureConfig = { ...PRESET_CONFIGS.default, particleArchCount: 8_000_000 };
    saveCustomConfigToStorage(customLarge);
    let s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'custom' });
    expect(s.useBulkLoad).toBe(true);

    // …then user toggles off
    s = devFixtureFormReducer(s, { type: 'set-use-bulk-load', value: false });
    expect(s.useBulkLoad).toBe(false);
    // Toggling does NOT flip preset to "custom" (we're already custom; verifies it doesn't error on standard preset either)
    expect(s.presetId).toBe('custom');
  });

  it('"set-use-bulk-load" never auto-flips presetId', () => {
    let s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'default' });
    s = devFixtureFormReducer(s, { type: 'set-use-bulk-load', value: true });
    expect(s.presetId).toBe('default');
    expect(s.useBulkLoad).toBe(true);
  });

  it('set-count edits do NOT auto-re-toggle useBulkLoad (only preset selection does)', () => {
    let s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'default' });
    expect(s.useBulkLoad).toBe(false);
    // User edits count to push total past threshold — but reducer doesn't re-evaluate on set-count.
    // The user can manually toggle if they want to switch modes.
    s = devFixtureFormReducer(s, { type: 'set-count', key: 'particleArchCount', value: 10_000_000 });
    expect(s.useBulkLoad).toBe(false);
    expect(s.presetId).toBe('custom');
  });
});
