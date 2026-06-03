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
 * (same field names, same units) for the SWG-inspired schema. The C# side hashes the canonical JSON of the
 * deserialized config to key the on-disk cache, so the client and server MUST agree on field names + values.
 * `toApiFixtureConfig` is the compile-time guard that this mirror stays assignable to the orval-generated DTO.
 */

import type { FixtureConfig as ApiFixtureConfig } from '@/api/generated/model/fixtureConfig';

/**
 * Mirror of the C# `FixtureConfig` record (SWG schema). Field names + units must stay identical to the server.
 * Three axes: volumetry (per-archetype counts), complexity (data-level shape toggles — schema-shape can't vary a
 * compile-time Typhon schema), distribution (entity-mix realism driving the Enable/Disable + cascade demos).
 */
export interface FixtureConfig {
  // ── Volumetry (per-archetype entity counts) ──────────────────────────────────────
  /** ResourceType taxonomy nodes (generated as a tree of depth `resourceTaxonomyDepth`). */
  resourceTypeCount: number;
  /** Guilds. */
  guildCount: number;
  /** Recipes — each gets 1..8 RecipeSlot ComponentCollection elements. */
  recipeCount: number;
  /** Players (the dominant scale axis) — mixed V+SV+Transient storage. */
  playerCount: number;
  /** Resource deposits (static-spatial). */
  depositCount: number;
  /** Harvester structures (polymorphic leaf of Structure). */
  harvesterCount: number;
  /** Factory structures (polymorphic leaf; spawned only when `includePolymorphicStructure`). */
  factoryCount: number;
  /** Items — highest-cardinality table; each gets 0..`maxAffixesPerItem` affix CC elements. */
  itemCount: number;
  // ── Complexity (data-level shape toggles) ────────────────────────────────────────
  /** Depth of the ResourceType.Parent taxonomy tree (1 = flat). */
  resourceTaxonomyDepth: number;
  /** Cap on ItemAffix ComponentCollection elements per item. */
  maxAffixesPerItem: number;
  /** Distinct values for Player.ProfessionId / Recipe.ProfessionReq. */
  professionCount: number;
  /** If false, items spawn with empty affix collections (single-component items). */
  includeMultiAffixItems: boolean;
  /** If false, no Factories spawn (Harvester-only — drops the polymorphic-subtree leaf #2). */
  includePolymorphicStructure: boolean;
  // ── Distribution (entity-mix realism) ────────────────────────────────────────────
  /** Fraction of players whose Session is left ENABLED (online); the rest are Disabled (offline). [0,1]. */
  onlinePlayerFraction: number;
  /** Fraction of harvesters whose MaintenanceState is Disabled (broken). [0,1]. */
  brokenHarvesterFraction: number;
  /** Fraction of deposits whose Deposit component is Disabled (depleted). [0,1]. */
  depletedDepositFraction: number;
  /** Fraction of factories whose PowerSupply is Disabled (idle). [0,1]. */
  idleFactoryFraction: number;
  /** How players spread across guilds — see `PlayersPerGuildShape`. */
  playersPerGuildShape: number;
  /** Fraction of players deleted post-spawn — exercises cascade delete of their Items + Structures. [0,1]. */
  deletedPlayerFraction: number;
  /** RNG seed driving all randomised fields. Same seed + same config ⇒ deterministic generated DB. */
  seed: number;
}

/**
 * Mirror of the C# `PlayersPerGuildShape` enum. System.Text.Json serialises it as its integer value, so the
 * client sends the numeric member; the server deserialises by value (1 ⇒ Zipf, etc.). Keep in declaration order.
 */
export const PlayersPerGuildShape = {
  Uniform: 0,
  Zipf: 1,
  Clumped: 2,
} as const;

/** Display labels for the guild-distribution shape selector. */
export const PLAYERS_PER_GUILD_SHAPE_LABELS: Record<number, string> = {
  [PlayersPerGuildShape.Uniform]: 'Uniform',
  [PlayersPerGuildShape.Zipf]: 'Zipf',
  [PlayersPerGuildShape.Clumped]: 'Clumped',
};

/**
 * Widen the strict form config to the orval-generated request DTO shape (numeric fields are `number | string`
 * server-side to accept string-encoded numbers; the form always uses `number`). This assignment is the
 * compile-time guard that the mirror above stays in lockstep with the C# record: if a field is renamed or added
 * on the server, orval regenerates `ApiFixtureConfig` and this function stops compiling.
 */
export function toApiFixtureConfig(config: FixtureConfig): ApiFixtureConfig {
  return config;
}

export type PresetId = 'tiny' | 'default' | 'stress' | 'industryHeavy' | 'worldHeavy' | 'sparse' | 'custom';

/** Display label for each preset id — used in the preset-button row. */
export const PRESET_LABELS: Record<PresetId, string> = {
  tiny: 'Tiny',
  default: 'Default',
  stress: 'Stress',
  industryHeavy: 'Industry-Heavy',
  worldHeavy: 'World-Heavy',
  sparse: 'Sparse',
  custom: 'Custom',
};

/**
 * Preset configurations. Each preset describes a complete `FixtureConfig`; selecting a preset replaces the
 * form's `config` wholesale. The "custom" preset's config comes from localStorage (last-saved edit), falling
 * back to the Default preset if nothing's stored yet.
 *
 * `default` and `stress` mirror the C# `FixtureConfig.Default` and the backend `Stress_Config` test exactly so
 * the on-disk hash matches between client-driven and test-driven generation (cache-hit fast path).
 */
export const PRESET_CONFIGS: Record<Exclude<PresetId, 'custom'>, FixtureConfig> = {
  tiny: {
    resourceTypeCount: 12,
    guildCount: 4,
    recipeCount: 10,
    playerCount: 40,
    depositCount: 20,
    harvesterCount: 16,
    factoryCount: 4,
    itemCount: 30,
    resourceTaxonomyDepth: 2,
    maxAffixesPerItem: 3,
    professionCount: 4,
    includeMultiAffixItems: true,
    includePolymorphicStructure: true,
    onlinePlayerFraction: 0.5,
    brokenHarvesterFraction: 0.1,
    depletedDepositFraction: 0.1,
    idleFactoryFraction: 0.1,
    playersPerGuildShape: PlayersPerGuildShape.Uniform,
    deletedPlayerFraction: 0.0,
    seed: 1,
  },
  default: {
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
  },
  stress: {
    resourceTypeCount: 1_000,
    guildCount: 500,
    recipeCount: 2_000,
    playerCount: 40_000,
    depositCount: 10_000,
    harvesterCount: 10_000,
    factoryCount: 2_000,
    itemCount: 40_000,
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
  },
  industryHeavy: {
    resourceTypeCount: 120,
    guildCount: 30,
    recipeCount: 1_000,
    playerCount: 1_000,
    depositCount: 3_000,
    harvesterCount: 4_000,
    factoryCount: 2_000,
    itemCount: 30_000,
    resourceTaxonomyDepth: 4,
    maxAffixesPerItem: 8,
    professionCount: 24,
    includeMultiAffixItems: true,
    includePolymorphicStructure: true,
    onlinePlayerFraction: 0.1,
    brokenHarvesterFraction: 0.2,
    depletedDepositFraction: 0.15,
    idleFactoryFraction: 0.1,
    playersPerGuildShape: PlayersPerGuildShape.Clumped,
    deletedPlayerFraction: 0.01,
    seed: 4_242,
  },
  worldHeavy: {
    resourceTypeCount: 60,
    guildCount: 80,
    recipeCount: 200,
    playerCount: 30_000,
    depositCount: 15_000,
    harvesterCount: 8_000,
    factoryCount: 2_000,
    itemCount: 10_000,
    resourceTaxonomyDepth: 3,
    maxAffixesPerItem: 4,
    professionCount: 16,
    includeMultiAffixItems: true,
    includePolymorphicStructure: true,
    onlinePlayerFraction: 0.3,
    brokenHarvesterFraction: 0.1,
    depletedDepositFraction: 0.1,
    idleFactoryFraction: 0.15,
    playersPerGuildShape: PlayersPerGuildShape.Zipf,
    deletedPlayerFraction: 0.05,
    seed: 99,
  },
  sparse: {
    resourceTypeCount: 20,
    guildCount: 10,
    recipeCount: 30,
    playerCount: 500,
    depositCount: 200,
    harvesterCount: 200,
    factoryCount: 0,
    itemCount: 500,
    resourceTaxonomyDepth: 1,
    maxAffixesPerItem: 0,
    professionCount: 2,
    includeMultiAffixItems: false,
    includePolymorphicStructure: false,
    onlinePlayerFraction: 1.0,
    brokenHarvesterFraction: 0.0,
    depletedDepositFraction: 0.0,
    idleFactoryFraction: 0.0,
    playersPerGuildShape: PlayersPerGuildShape.Uniform,
    deletedPlayerFraction: 0.0,
    seed: 1,
  },
};

/** Integer volumetry + complexity fields — drive the `set-count` action (clamped ≥ 0, floored). */
export type FixtureIntKey =
  | 'resourceTypeCount'
  | 'guildCount'
  | 'recipeCount'
  | 'playerCount'
  | 'depositCount'
  | 'harvesterCount'
  | 'factoryCount'
  | 'itemCount'
  | 'resourceTaxonomyDepth'
  | 'maxAffixesPerItem'
  | 'professionCount';

/** Distribution fraction fields — drive the `set-fraction` action (clamped to [0, 1]). */
export type FixtureFractionKey =
  | 'onlinePlayerFraction'
  | 'brokenHarvesterFraction'
  | 'depletedDepositFraction'
  | 'idleFactoryFraction'
  | 'deletedPlayerFraction';

/** Boolean complexity toggles — drive the `set-bool` action. */
export type FixtureBoolKey = 'includeMultiAffixItems' | 'includePolymorphicStructure';

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
   * Whether to generate the fixture via the engine's `BulkLoadSession` opt-in throughput path. Auto-toggled on
   * preset selection (true when the preset's total entity count exceeds `BULK_LOAD_AUTO_THRESHOLD`); user-overridable
   * thereafter. Not part of `FixtureConfig` — the bulk-vs-standard write path doesn't affect content identity (the
   * same config generates byte-identical fixtures regardless of path), so it must stay outside the hashed config.
   */
  useBulkLoad: boolean;
}

/** Auto-toggle threshold for `useBulkLoad` on preset selection. Above ~5 M entities the standard path is impractical. */
export const BULK_LOAD_AUTO_THRESHOLD = 5_000_000;

export type DevFixtureFormAction =
  | { type: 'select-preset'; id: PresetId }
  | { type: 'set-count'; key: FixtureIntKey; value: number }
  | { type: 'set-fraction'; key: FixtureFractionKey; value: number }
  | { type: 'set-bool'; key: FixtureBoolKey; value: boolean }
  | { type: 'set-shape'; value: number }
  | { type: 'set-seed'; value: number }
  | { type: 'toggle-advanced' }
  | { type: 'randomize-seed' }
  | { type: 'set-database-name'; value: string }
  | { type: 'set-use-bulk-load'; value: boolean };

/**
 * Sum of every spawn-able archetype count — used to drive the bulk-load auto-toggle + scale advisory tier.
 * Mirrors C# `FixtureConfig.TotalSpawnEstimate`: the Structure base (825) is never spawned directly, and Factories
 * count only when polymorphism is enabled.
 */
export function totalEntityCount(config: FixtureConfig): number {
  return (
    config.resourceTypeCount +
    config.guildCount +
    config.recipeCount +
    config.playerCount +
    config.depositCount +
    config.harvesterCount +
    (config.includePolymorphicStructure ? config.factoryCount : 0) +
    config.itemCount
  );
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

/** Storage key for the user's last "Custom" config. Versioned (`v2`) — the SWG schema fully replaced the v1 shape. */
export const CUSTOM_CONFIG_STORAGE_KEY = 'typhon-wb-devfixture-custom-v2';

/**
 * Storage key for the user's destination-folder override. When set, the client sends it as `OutputDirectory` on the
 * create request and the server uses it verbatim (skipping the default `{root}/{databaseName}/` composition).
 * Versioned so a future change to "absolute vs relative" semantics can ignore stale entries.
 */
export const OUTPUT_DIR_STORAGE_KEY = 'typhon-wb-devfixture-outputdir-v1';

/** Storage key for the last-used database name — restored on dialog re-open so the user's last target sticks. */
export const DATABASE_NAME_STORAGE_KEY = 'typhon-wb-devfixture-dbname-v1';

/** Storage key for the last-used BulkLoad toggle — restored on dialog re-open (the user's explicit choice survives reloads). */
export const USE_BULK_LOAD_STORAGE_KEY = 'typhon-wb-devfixture-bulkload-v1';

/** The boolean-typed fields of `FixtureConfig` — used by the storage validator to type-check loaded entries. */
const BOOL_KEYS: ReadonlySet<string> = new Set<FixtureBoolKey>(['includeMultiAffixItems', 'includePolymorphicStructure']);

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

/** Load the last-used database name, or `null` when none is stored / storage is unreadable. */
export function loadDatabaseNameFromStorage(): string | null {
  try {
    const raw = localStorage.getItem(DATABASE_NAME_STORAGE_KEY);
    return raw && raw.length > 0 ? raw : null;
  } catch {
    return null;
  }
}

/** Persist the database name. Pass empty string to clear (an empty name isn't worth restoring). Failures are silent. */
export function saveDatabaseNameToStorage(value: string): void {
  try {
    if (value.length === 0) {
      localStorage.removeItem(DATABASE_NAME_STORAGE_KEY);
    } else {
      localStorage.setItem(DATABASE_NAME_STORAGE_KEY, value);
    }
  } catch {
    /* ignore */
  }
}

/** Load the last-used BulkLoad toggle, or `null` when none is stored / storage is unreadable. */
export function loadUseBulkLoadFromStorage(): boolean | null {
  try {
    const raw = localStorage.getItem(USE_BULK_LOAD_STORAGE_KEY);
    if (raw === 'true') return true;
    if (raw === 'false') return false;
    return null;
  } catch {
    return null;
  }
}

/** Persist the BulkLoad toggle. Failures are silent — localStorage may be disabled (private browsing). */
export function saveUseBulkLoadToStorage(value: boolean): void {
  try {
    localStorage.setItem(USE_BULK_LOAD_STORAGE_KEY, value ? 'true' : 'false');
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
    // Validate shape — every field must be the right primitive kind. A missing field falls back to the Default
    // value so a partial entry (from a future schema add) still loads usable values.
    const merged: FixtureConfig = { ...PRESET_CONFIGS.default, ...parsed };
    const valid = (Object.keys(merged) as Array<keyof FixtureConfig>).every((key) => {
      const value = merged[key];
      return BOOL_KEYS.has(key) ? typeof value === 'boolean' : typeof value === 'number' && Number.isFinite(value);
    });
    if (!valid) return null;
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

/**
 * Initial state for the dialog. Restores the user's last-saved Custom config, database name, and BulkLoad toggle from
 * storage; falls back to the Default preset / default name / size-based BulkLoad auto-recommendation when absent.
 */
export function initialDevFixtureFormState(): DevFixtureFormState {
  const customStored = loadCustomConfigFromStorage();
  // databaseName starts at the last-used name (or the C# default). The capability probe only seeds it when empty, so a
  // restored name always wins.
  const storedName = loadDatabaseNameFromStorage() ?? DEFAULT_DATABASE_NAME;
  // BulkLoad: the user's last explicit choice wins over the size-based auto-recommendation on restore. Preset selection
  // still re-applies the auto-toggle afterwards (see the reducer's select-preset case).
  const storedBulk = loadUseBulkLoadFromStorage();
  const baseState = {
    showAdvanced: false,
    databaseName: storedName,
    databaseNameError: validateDatabaseName(storedName),
  };
  if (customStored) {
    return {
      presetId: 'custom',
      config: customStored,
      useBulkLoad: storedBulk ?? (totalEntityCount(customStored) > BULK_LOAD_AUTO_THRESHOLD),
      ...baseState,
    };
  }
  return {
    presetId: 'default',
    config: PRESET_CONFIGS.default,
    useBulkLoad: storedBulk ?? (totalEntityCount(PRESET_CONFIGS.default) > BULK_LOAD_AUTO_THRESHOLD),
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
      const config = action.id === 'custom' ? (loadCustomConfigFromStorage() ?? state.config) : PRESET_CONFIGS[action.id];
      return {
        ...state,
        presetId: action.id,
        config,
        useBulkLoad: totalEntityCount(config) > BULK_LOAD_AUTO_THRESHOLD,
      };
    }
    case 'set-count': {
      // Any edit to a count flips the preset to "custom" — the user's edit is now the source of truth.
      const nextConfig = { ...state.config, [action.key]: Math.max(0, Math.floor(action.value)) };
      return { ...state, presetId: 'custom', config: nextConfig };
    }
    case 'set-fraction': {
      const clamped = Math.max(0, Math.min(1, action.value));
      return { ...state, presetId: 'custom', config: { ...state.config, [action.key]: clamped } };
    }
    case 'set-bool': {
      return { ...state, presetId: 'custom', config: { ...state.config, [action.key]: action.value } };
    }
    case 'set-shape': {
      // Clamp to a valid enum member (0..2). Anything out of range pins to Uniform.
      const shape = Number.isInteger(action.value) && action.value >= 0 && action.value <= 2 ? action.value : PlayersPerGuildShape.Uniform;
      return { ...state, presetId: 'custom', config: { ...state.config, playersPerGuildShape: shape } };
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
