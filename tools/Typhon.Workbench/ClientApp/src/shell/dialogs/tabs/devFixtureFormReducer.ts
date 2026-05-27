/**
 * Pure state + reducer for the Dev Fixture dialog's preset / Advanced form. Lives next to
 * `DevFixtureTab.tsx` so the dialog can `useReducer(devFixtureFormReducer, ...)` without
 * importing UI concerns into this file — keeps the state machine fully testable in vitest.
 *
 * State model:
 *  - `presetId` is one of the named presets OR `'custom'`. Selecting a preset replaces `config` with the
 *    preset's known values; any subsequent edit to `config` flips `presetId` back to `'custom'` and the
 *    edited config is persisted to `localStorage` so the user's last "Custom" tweak survives reloads.
 *  - `showAdvanced` toggles the advanced form's visibility — independent of preset selection.
 *
 * Server contract mirror: this file's `FixtureConfig` shape matches the C# `FixtureConfig` record one-to-one
 * (same field names, same units). The C# side hashes the canonical JSON of this object to key the on-disk
 * cache, so the client and server MUST agree byte-for-byte on the canonical representation.
 */

/** Mirror of the C# `FixtureConfig` record. Field names + units must stay identical. */
export interface FixtureConfig {
  compAArchCount: number;
  compABArchCount: number;
  compABCArchCount: number;
  compDArchCount: number;
  guildArchCount: number;
  playerArchCount: number;
  particleArchCount: number;
  /** Fraction of spawned Particles destroyed post-spawn, in [0, 1]. */
  particleFragmentation: number;
  /** RNG seed driving all randomised fields. */
  seed: number;
}

export type PresetId = 'tiny' | 'default' | 'stress' | 'fragmented' | 'emptyCores' | 'custom';

/** Display label for each preset id — used in the preset-button row. */
export const PRESET_LABELS: Record<PresetId, string> = {
  tiny: 'Tiny',
  default: 'Default',
  stress: 'Stress',
  fragmented: 'Fragmented',
  emptyCores: 'Empty cores',
  custom: 'Custom',
};

/**
 * Preset configurations. Each preset describes a complete `FixtureConfig`; selecting a preset
 * replaces the form's `config` wholesale. The "custom" preset's config comes from localStorage
 * (last-saved edit), falling back to the Default preset if nothing's stored yet.
 */
export const PRESET_CONFIGS: Record<Exclude<PresetId, 'custom'>, FixtureConfig> = {
  tiny: {
    compAArchCount: 10,
    compABArchCount: 10,
    compABCArchCount: 10,
    compDArchCount: 5,
    guildArchCount: 2,
    playerArchCount: 10,
    particleArchCount: 20,
    particleFragmentation: 0.0,
    seed: 1,
  },
  default: {
    compAArchCount: 1_000,
    compABArchCount: 500,
    compABCArchCount: 500,
    compDArchCount: 200,
    guildArchCount: 50,
    playerArchCount: 300,
    particleArchCount: 2_000,
    particleFragmentation: 0.40,
    seed: 123_456_789,
  },
  stress: {
    compAArchCount: 100_000,
    compABArchCount: 50_000,
    compABCArchCount: 50_000,
    compDArchCount: 10_000,
    guildArchCount: 500,
    playerArchCount: 10_000,
    particleArchCount: 200_000,
    particleFragmentation: 0.40,
    seed: 123_456_789,
  },
  fragmented: {
    compAArchCount: 1_000,
    compABArchCount: 500,
    compABCArchCount: 500,
    compDArchCount: 200,
    guildArchCount: 50,
    playerArchCount: 300,
    particleArchCount: 2_000,
    particleFragmentation: 0.90,
    seed: 7,
  },
  emptyCores: {
    compAArchCount: 10_000,
    compABArchCount: 0,
    compABCArchCount: 0,
    compDArchCount: 0,
    guildArchCount: 0,
    playerArchCount: 0,
    particleArchCount: 0,
    particleFragmentation: 0.0,
    seed: 1,
  },
};

export interface DevFixtureFormState {
  presetId: PresetId;
  config: FixtureConfig;
  showAdvanced: boolean;
  /**
   * On-disk database name — drives the generated `{name}.typhon` / `{name}.bin` filenames AND the per-DB output
   * subdirectory. Independent of preset selection (editing it does NOT flip presetId to "custom"; it's a filesystem
   * concern, not a content shape).
   */
  databaseName: string;
  /** Inline validation error for `databaseName` — null when valid. Mirrors the server's regex (see C# `FixtureDatabase.DatabaseNameRegex`). */
  databaseNameError: string | null;
  /**
   * Whether to generate the fixture via the engine's <c>BulkLoadSession</c> opt-in throughput path. Auto-toggled
   * on preset selection (true when the preset's total entity count exceeds <see cref="BULK_LOAD_AUTO_THRESHOLD"/>);
   * user-overridable thereafter. Not part of <see cref="FixtureConfig"/> — the bulk-vs-standard write path doesn't
   * affect content identity (the same config generates byte-identical fixtures regardless of path), so it must
   * stay outside the hashed config.
   */
  useBulkLoad: boolean;
}

/** Auto-toggle threshold for <c>useBulkLoad</c> on preset selection. Above ~5 M entities the standard path is impractical. */
export const BULK_LOAD_AUTO_THRESHOLD = 5_000_000;

export type DevFixtureFormAction =
  | { type: 'select-preset'; id: PresetId }
  | { type: 'set-count'; key: keyof FixtureConfig; value: number }
  | { type: 'set-fragmentation'; value: number }
  | { type: 'set-seed'; value: number }
  | { type: 'toggle-advanced' }
  | { type: 'randomize-seed' }
  | { type: 'set-database-name'; value: string }
  | { type: 'set-use-bulk-load'; value: boolean };

/** Sum of every spawn-able archetype count — used to drive the bulk-load auto-toggle + scale advisory tier. */
export function totalEntityCount(config: FixtureConfig): number {
  return config.compAArchCount + config.compABArchCount + config.compABCArchCount
    + config.compDArchCount + config.guildArchCount + config.playerArchCount
    + config.particleArchCount;
}

/**
 * Client-side validation mirror of the server's `DatabaseNameRegex` (1-64 chars, [a-zA-Z0-9_-]). The server
 * re-validates on the request so a malicious / out-of-date client can't bypass; the client check is purely UX —
 * show inline feedback BEFORE the user hits Generate and gets a 400.
 */
const DATABASE_NAME_REGEX = /^[a-zA-Z0-9_-]{1,64}$/;
const DATABASE_NAME_ERROR = "1–64 chars, letters/digits/'-'/'_' only";

export function validateDatabaseName(candidate: string): string | null {
  const trimmed = candidate.trim();
  if (trimmed.length === 0) return 'Required';
  if (!DATABASE_NAME_REGEX.test(trimmed)) return DATABASE_NAME_ERROR;
  return null;
}

/** Default database name — mirrors the C# `FixtureDatabase.DefaultDatabaseName`. Overridable via the capability response. */
export const DEFAULT_DATABASE_NAME = 'base-tests';

/** Storage key for the user's last "Custom" config. Versioned (`v1`) so a future schema bump can ignore stale values. */
export const CUSTOM_CONFIG_STORAGE_KEY = 'typhon-wb-devfixture-custom-v1';

/**
 * Storage key for the user's destination-folder override. When set, the client sends it as `OutputDirectory` on the
 * create request and the server uses it verbatim (skipping the default `{root}/{databaseName}/` composition).
 * Versioned so a future change to "absolute vs relative" semantics can ignore stale entries.
 */
export const OUTPUT_DIR_STORAGE_KEY = 'typhon-wb-devfixture-outputdir-v1';

/** Load the user's destination-folder override, or `null` when none is stored / storage is unreadable. */
export function loadOutputDirFromStorage(): string | null {
  try {
    const raw = localStorage.getItem(OUTPUT_DIR_STORAGE_KEY);
    return raw && raw.length > 0 ? raw : null;
  } catch {
    return null;
  }
}

/** Persist the user's destination-folder override. Pass empty string to clear. Failures are silent. */
export function saveOutputDirToStorage(value: string): void {
  try {
    if (value.length === 0) {
      localStorage.removeItem(OUTPUT_DIR_STORAGE_KEY);
    } else {
      localStorage.setItem(OUTPUT_DIR_STORAGE_KEY, value);
    }
  } catch {
    /* ignore */
  }
}

/**
 * Load the last-saved "Custom" config from localStorage. Returns null on parse failure / missing key —
 * the reducer treats null as "fall back to Default". Defensive try/catch so a corrupted entry can't crash the dialog.
 */
export function loadCustomConfigFromStorage(): FixtureConfig | null {
  try {
    const raw = localStorage.getItem(CUSTOM_CONFIG_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<FixtureConfig>;
    // Validate shape — every field must be a finite number. A missing field falls back to the Default value
    // so a partial entry (from a future schema add) still loads usable values.
    const merged: FixtureConfig = { ...PRESET_CONFIGS.default, ...parsed };
    if (!Object.values(merged).every((v) => typeof v === 'number' && Number.isFinite(v))) return null;
    return merged;
  } catch {
    return null;
  }
}

/** Persist a config under the "Custom" slot. Failures are silent — localStorage may be disabled (private browsing). */
export function saveCustomConfigToStorage(config: FixtureConfig): void {
  try {
    localStorage.setItem(CUSTOM_CONFIG_STORAGE_KEY, JSON.stringify(config));
  } catch {
    /* ignore */
  }
}

/** Initial state for the dialog. Loads the last-saved Custom config from storage; if absent, picks Default. */
export function initialDevFixtureFormState(): DevFixtureFormState {
  const customStored = loadCustomConfigFromStorage();
  // databaseName starts at the C# default — the dialog overrides this from the capability probe once it lands.
  const baseState = {
    showAdvanced: false,
    databaseName: DEFAULT_DATABASE_NAME,
    databaseNameError: null as string | null,
  };
  if (customStored) {
    return {
      presetId: 'custom',
      config: customStored,
      useBulkLoad: totalEntityCount(customStored) > BULK_LOAD_AUTO_THRESHOLD,
      ...baseState,
    };
  }
  return {
    presetId: 'default',
    config: PRESET_CONFIGS.default,
    useBulkLoad: totalEntityCount(PRESET_CONFIGS.default) > BULK_LOAD_AUTO_THRESHOLD,
    ...baseState,
  };
}

/**
 * Reducer for the Dev Fixture form. Pure, no side effects (persistence to localStorage is the caller's
 * responsibility — the reducer just transitions state). Tests drive this directly without React.
 */
export function devFixtureFormReducer(state: DevFixtureFormState, action: DevFixtureFormAction): DevFixtureFormState {
  switch (action.type) {
    case 'select-preset': {
      // Selecting "custom" loads the last-saved storage entry (or Default if none); other presets load their canonical config.
      // Auto-toggle useBulkLoad based on the new config's total entity count — preset selection is when we know the
      // most about the user's intent, so it's the natural place to recommend bulk-vs-standard. Any subsequent user
      // toggle (set-use-bulk-load) overrides this default.
      const config = action.id === 'custom'
        ? (loadCustomConfigFromStorage() ?? state.config)
        : PRESET_CONFIGS[action.id];
      return {
        ...state,
        presetId: action.id,
        config,
        useBulkLoad: totalEntityCount(config) > BULK_LOAD_AUTO_THRESHOLD,
      };
    }
    case 'set-count': {
      // Any edit to a count flips the preset to "custom" — the user's edit is now the source of truth.
      // `set-count` is keyed on `keyof FixtureConfig` so future fields land here without a new action type.
      const nextConfig = { ...state.config, [action.key]: Math.max(0, Math.floor(action.value)) };
      return { ...state, presetId: 'custom', config: nextConfig };
    }
    case 'set-fragmentation': {
      const clamped = Math.max(0, Math.min(1, action.value));
      return { ...state, presetId: 'custom', config: { ...state.config, particleFragmentation: clamped } };
    }
    case 'set-seed': {
      return { ...state, presetId: 'custom', config: { ...state.config, seed: Math.floor(action.value) } };
    }
    case 'randomize-seed': {
      // 31-bit positive int — matches the C# `Random(int)` constructor's effective range and stays JSON-safe.
      const next = Math.floor(Math.random() * 0x7fffffff);
      return { ...state, presetId: 'custom', config: { ...state.config, seed: next } };
    }
    case 'toggle-advanced': {
      return { ...state, showAdvanced: !state.showAdvanced };
    }
    case 'set-database-name': {
      // Database name does NOT flip the preset to "custom" — it's an on-disk path concern, not a content shape one.
      // Validation runs inline so the user sees the error before submitting (server re-validates as a safety net).
      return { ...state, databaseName: action.value, databaseNameError: validateDatabaseName(action.value) };
    }
    case 'set-use-bulk-load': {
      // User explicitly toggling the bulk-load mode. Does NOT flip the preset to "custom" — bulk-vs-standard is
      // an execution-path concern, not a content-shape concern; the same fixture identity (same hash) can be
      // generated via either path.
      return { ...state, useBulkLoad: action.value };
    }
  }
}
