export {
  allocateField,
  MOTION_GROUP,
  validateSchema,
  type ArchetypeSchema,
  type FieldArray,
  type FieldKind,
  type FieldSchema,
  type PositionKind,
  type WorldSchema,
} from './store/schema.js';
export {
  ArchetypeStore,
  MOTION_RECORD_BYTES,
  MOTION_SEGMENTS_OFFSET,
  SEGMENT_HISTORY,
} from './store/archetype-store.js';
export { archetypeOf, NOT_FOUND, slotOf, WorldStore, type WorldStoreOptions } from './store/world-store.js';
export { epochAt, evaluateLive, evaluateSlot, headingOf, MOTION_STRIDE, segmentEntryAt } from './motion/motion.js';
export { Clock, type ClockOptions } from './clock/clock.js';
export { AggregateGrid, type GridSchema } from './aggregates/aggregate-grid.js';
