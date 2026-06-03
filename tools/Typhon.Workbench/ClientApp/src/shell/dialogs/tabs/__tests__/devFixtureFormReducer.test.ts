// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  BULK_LOAD_AUTO_THRESHOLD,
  CUSTOM_CONFIG_STORAGE_KEY,
  DATABASE_NAME_STORAGE_KEY,
  DEFAULT_DATABASE_NAME,
  PlayersPerGuildShape,
  PRESET_CONFIGS,
  USE_BULK_LOAD_STORAGE_KEY,
  devFixtureFormReducer,
  initialDevFixtureFormState,
  loadCustomConfigFromStorage,
  loadDatabaseNameFromStorage,
  loadUseBulkLoadFromStorage,
  saveCustomConfigToStorage,
  saveDatabaseNameToStorage,
  saveUseBulkLoadToStorage,
  totalEntityCount,
  validateDatabaseName,
  type DevFixtureFormState,
  type FixtureBoolKey,
  type FixtureConfig,
  type FixtureFractionKey,
} from '../devFixtureFormReducer';

/**
 * Pure-reducer + localStorage tests for the Dev Fixture form (SWG schema). The reducer is the source of truth for
 * preset selection ↔ Custom-flip semantics + per-field clamping; the localStorage helpers persist the user's Custom
 * config so re-opening the dialog lands on their last shape. These tests run without React.
 */

const BOOL_KEYS: FixtureBoolKey[] = ['includeMultiAffixItems', 'includePolymorphicStructure'];
const FRACTION_KEYS: FixtureFractionKey[] = [
  'onlinePlayerFraction',
  'brokenHarvesterFraction',
  'depletedDepositFraction',
  'idleFactoryFraction',
  'deletedPlayerFraction',
];

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
    const custom: FixtureConfig = { ...PRESET_CONFIGS.default, resourceTypeCount: 42 };
    saveCustomConfigToStorage(custom);
    const s = initialState();
    expect(s.presetId).toBe('custom');
    expect(s.config.resourceTypeCount).toBe(42);
  });

  it('selecting a non-custom preset replaces config with that preset\'s canonical values', () => {
    const start = initialState();
    const next = devFixtureFormReducer(start, { type: 'select-preset', id: 'tiny' });
    expect(next.presetId).toBe('tiny');
    expect(next.config).toEqual(PRESET_CONFIGS.tiny);
  });

  it('selecting "custom" loads the stored config (or keeps current when none stored)', () => {
    const custom: FixtureConfig = { ...PRESET_CONFIGS.default, itemCount: 99 };
    saveCustomConfigToStorage(custom);
    const start = devFixtureFormReducer(initialState(), { type: 'select-preset', id: 'stress' });
    expect(start.presetId).toBe('stress');
    const next = devFixtureFormReducer(start, { type: 'select-preset', id: 'custom' });
    expect(next.presetId).toBe('custom');
    expect(next.config.itemCount).toBe(99);
  });
});

describe('devFixtureFormReducer — edits flip preset to Custom', () => {
  it('set-count flips preset to "custom" + updates the field', () => {
    const start = initialState();
    expect(start.presetId).toBe('default');
    const next = devFixtureFormReducer(start, { type: 'set-count', key: 'guildCount', value: 17 });
    expect(next.presetId).toBe('custom');
    expect(next.config.guildCount).toBe(17);
  });

  it('set-count clamps to non-negative integers', () => {
    const start = initialState();
    const negative = devFixtureFormReducer(start, { type: 'set-count', key: 'resourceTypeCount', value: -5 });
    expect(negative.config.resourceTypeCount).toBe(0);
    const floated = devFixtureFormReducer(start, { type: 'set-count', key: 'resourceTypeCount', value: 3.9 });
    expect(floated.config.resourceTypeCount).toBe(3);
  });

  it('set-fraction clamps to [0, 1] and flips to custom', () => {
    const start = initialState();
    expect(devFixtureFormReducer(start, { type: 'set-fraction', key: 'onlinePlayerFraction', value: 1.5 }).config.onlinePlayerFraction).toBe(1);
    expect(devFixtureFormReducer(start, { type: 'set-fraction', key: 'onlinePlayerFraction', value: -0.5 }).config.onlinePlayerFraction).toBe(0);
    const ok = devFixtureFormReducer(start, { type: 'set-fraction', key: 'deletedPlayerFraction', value: 0.42 });
    expect(ok.config.deletedPlayerFraction).toBe(0.42);
    expect(ok.presetId).toBe('custom');
  });

  it('set-bool toggles a complexity flag and flips to custom', () => {
    const start = initialState();
    expect(start.config.includePolymorphicStructure).toBe(true);
    const next = devFixtureFormReducer(start, { type: 'set-bool', key: 'includePolymorphicStructure', value: false });
    expect(next.presetId).toBe('custom');
    expect(next.config.includePolymorphicStructure).toBe(false);
  });

  it('set-shape clamps to a valid PlayersPerGuildShape member', () => {
    const start = initialState();
    const clumped = devFixtureFormReducer(start, { type: 'set-shape', value: PlayersPerGuildShape.Clumped });
    expect(clumped.presetId).toBe('custom');
    expect(clumped.config.playersPerGuildShape).toBe(PlayersPerGuildShape.Clumped);
    // Out-of-range pins to Uniform.
    expect(devFixtureFormReducer(start, { type: 'set-shape', value: 99 }).config.playersPerGuildShape).toBe(PlayersPerGuildShape.Uniform);
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
  it('saveCustomConfigToStorage round-trips through loadCustomConfigFromStorage (incl. booleans)', () => {
    const cfg: FixtureConfig = { ...PRESET_CONFIGS.default, resourceTypeCount: 7, includeMultiAffixItems: false, seed: 99 };
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

  it('returns null when a numeric field is non-finite (NaN / Infinity)', () => {
    const bad = { ...PRESET_CONFIGS.default, seed: Number.NaN };
    localStorage.setItem(CUSTOM_CONFIG_STORAGE_KEY, JSON.stringify(bad));
    expect(loadCustomConfigFromStorage()).toBeNull();
  });

  it('returns null when a boolean field is the wrong primitive kind', () => {
    const bad = { ...PRESET_CONFIGS.default, includeMultiAffixItems: 1 };
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
  it('every preset has the right primitive kind + range for every field', () => {
    for (const id of Object.keys(PRESET_CONFIGS) as Array<keyof typeof PRESET_CONFIGS>) {
      const cfg = PRESET_CONFIGS[id];
      for (const [key, value] of Object.entries(cfg)) {
        if ((BOOL_KEYS as string[]).includes(key)) {
          expect(typeof value).toBe('boolean');
        } else {
          expect(typeof value).toBe('number');
          expect(Number.isFinite(value as number)).toBe(true);
          if ((FRACTION_KEYS as string[]).includes(key)) {
            expect(value as number).toBeGreaterThanOrEqual(0);
            expect(value as number).toBeLessThanOrEqual(1);
          } else if (key !== 'seed') {
            expect(value as number).toBeGreaterThanOrEqual(0);
          }
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

  it('Default preset matches the C# `FixtureConfig.Default` values', () => {
    // If this fails, the server and client have drifted apart — the on-disk hash will be different
    // for "Default" between the two, which breaks the cache-hit fast path.
    expect(PRESET_CONFIGS.default).toEqual({
      resourceTypeCount: 60,
      guildCount: 50,
      recipeCount: 200,
      playerCount: 2_000,
      depositCount: 1_000,
      harvesterCount: 800,
      factoryCount: 200,
      itemCount: 5_000,
      resourceTaxonomyDepth: 3,
      maxAffixesPerItem: 4,
      professionCount: 16,
      includeMultiAffixItems: true,
      includePolymorphicStructure: true,
      onlinePlayerFraction: 0.15,
      brokenHarvesterFraction: 0.1,
      depletedDepositFraction: 0.05,
      idleFactoryFraction: 0.2,
      playersPerGuildShape: PlayersPerGuildShape.Zipf,
      deletedPlayerFraction: 0.02,
      seed: 123_456_789,
    });
  });

  it('Default total entity count is 9310 (mirrors C# TotalSpawnEstimate)', () => {
    // 60 RT + 50 Guild + 200 Recipe + 2000 Player + 1000 Deposit + 800 Harvester + 200 Factory + 5000 Item.
    expect(totalEntityCount(PRESET_CONFIGS.default)).toBe(9_310);
  });

  it('totalEntityCount excludes Factories when polymorphism is disabled', () => {
    const cfg: FixtureConfig = { ...PRESET_CONFIGS.default, includePolymorphicStructure: false };
    expect(totalEntityCount(cfg)).toBe(9_310 - PRESET_CONFIGS.default.factoryCount);
  });
});

describe('database-name + BulkLoad persistence', () => {
  it('saveDatabaseNameToStorage round-trips through loadDatabaseNameFromStorage', () => {
    saveDatabaseNameToStorage('my-fixture');
    expect(loadDatabaseNameFromStorage()).toBe('my-fixture');
  });

  it('clearing the database name (empty string) removes the key', () => {
    saveDatabaseNameToStorage('x');
    saveDatabaseNameToStorage('');
    expect(loadDatabaseNameFromStorage()).toBeNull();
    expect(localStorage.getItem(DATABASE_NAME_STORAGE_KEY)).toBeNull();
  });

  it('saveUseBulkLoadToStorage round-trips both boolean states', () => {
    saveUseBulkLoadToStorage(true);
    expect(loadUseBulkLoadFromStorage()).toBe(true);
    saveUseBulkLoadToStorage(false);
    expect(loadUseBulkLoadFromStorage()).toBe(false);
  });

  it('loadUseBulkLoadFromStorage returns null when nothing is stored', () => {
    expect(loadUseBulkLoadFromStorage()).toBeNull();
    expect(localStorage.getItem(USE_BULK_LOAD_STORAGE_KEY)).toBeNull();
  });

  it('initial state restores the saved database name (validated) and BulkLoad toggle', () => {
    saveDatabaseNameToStorage('restored-db');
    saveUseBulkLoadToStorage(true);
    const s = initialDevFixtureFormState();
    expect(s.databaseName).toBe('restored-db');
    expect(s.databaseNameError).toBeNull();
    expect(s.useBulkLoad).toBe(true);
  });

  it('initial database name falls back to the default when none is stored', () => {
    const s = initialDevFixtureFormState();
    expect(s.databaseName).toBe(DEFAULT_DATABASE_NAME);
  });

  it('initial state surfaces a validation error for a restored invalid name', () => {
    saveDatabaseNameToStorage('has spaces');
    const s = initialDevFixtureFormState();
    expect(s.databaseName).toBe('has spaces');
    expect(s.databaseNameError).not.toBeNull();
  });

  it('a stored BulkLoad choice wins over the size-based auto-recommendation on restore', () => {
    // A >5 M custom config would auto-recommend BulkLoad=true, but the user's explicit "false" must win.
    const customLarge: FixtureConfig = { ...PRESET_CONFIGS.default, playerCount: 8_000_000 };
    saveCustomConfigToStorage(customLarge);
    saveUseBulkLoadToStorage(false);
    const s = initialDevFixtureFormState();
    expect(totalEntityCount(s.config)).toBeGreaterThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(false);
  });

  it('falls back to the size-based auto-recommendation when no BulkLoad choice is stored', () => {
    const customLarge: FixtureConfig = { ...PRESET_CONFIGS.default, playerCount: 8_000_000 };
    saveCustomConfigToStorage(customLarge);
    // No saveUseBulkLoadToStorage call → null → auto-recommend from size.
    const s = initialDevFixtureFormState();
    expect(s.useBulkLoad).toBe(true);
  });
});

describe('devFixtureFormReducer — useBulkLoad auto-toggle', () => {
  it('initial state has useBulkLoad=false for Default preset (below threshold)', () => {
    const s = initialDevFixtureFormState();
    expect(totalEntityCount(PRESET_CONFIGS.default)).toBeLessThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(false);
  });

  it('selecting Stress preset (~105 k total) leaves useBulkLoad=false (still under threshold)', () => {
    const s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'stress' });
    expect(totalEntityCount(PRESET_CONFIGS.stress)).toBeLessThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(false);
  });

  it('selecting a >5 M custom config auto-toggles useBulkLoad to true', () => {
    const customLarge: FixtureConfig = {
      ...PRESET_CONFIGS.default,
      playerCount: 8_000_000,
    };
    saveCustomConfigToStorage(customLarge);
    const s = devFixtureFormReducer(initialDevFixtureFormState(), { type: 'select-preset', id: 'custom' });
    expect(totalEntityCount(s.config)).toBeGreaterThan(BULK_LOAD_AUTO_THRESHOLD);
    expect(s.useBulkLoad).toBe(true);
  });

  it('"set-use-bulk-load" overrides the auto-toggle (user choice wins)', () => {
    // Auto-on via large custom config…
    const customLarge: FixtureConfig = { ...PRESET_CONFIGS.default, playerCount: 8_000_000 };
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
    s = devFixtureFormReducer(s, { type: 'set-count', key: 'playerCount', value: 10_000_000 });
    expect(s.useBulkLoad).toBe(false);
    expect(s.presetId).toBe('custom');
  });
});
