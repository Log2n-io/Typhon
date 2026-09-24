export {
  allocateField,
  isNumericKind,
  MAX_GROUPS,
  MOTION_CHANGE_BIT,
  validateSchema,
  type ArchetypeSchema,
  type FieldArray,
  type FieldKind,
  type FieldSchema,
  type NumericFieldKind,
  type PositionSchema,
  type WorldSchema,
} from './store/schema.js';
export {
  ArchetypeStore,
  DEFAULT_MAX_RENDER_DELAY_MS,
  motionRecordBytes,
  motionSegmentsOffset,
  segmentHistoryFor,
} from './store/archetype-store.js';
export { archetypeOf, NOT_FOUND, slotOf, WorldStore, type WorldStoreOptions } from './store/world-store.js';
export { epochAt, evaluateLive, evaluateSlot, headingOf, MAX_MOTION_STRIDE, segmentEntryAt } from './motion/motion.js';
export { Clock, type ClockOptions } from './clock/clock.js';
export { AggregateGrid, type GridSchema } from './aggregates/aggregate-grid.js';
export { FrameApplier, type FrameApplierOptions } from './apply/frame-applier.js';
export { AckList, EventRecord, retainBytes, SelfState, SourceList, StatsState } from './apply/frame-state.js';
export { fieldKindOf, gridSchemaFromCatalog, worldSchemaFromCatalog } from './apply/schema-from-catalog.js';

export {
  AckReason,
  BlockType,
  BuiltInCommand,
  Capabilities,
  CloseCode,
  isValidClientCloseCode,
  MessageType,
  ProtocolConstants,
  SourceStatus,
  TCP_PREAMBLE,
  TickFlags,
} from './protocol/constants.js';
export { malformed, protocolError, WireFormatError } from './protocol/errors.js';
export { WireReader } from './protocol/reader.js';
export { varuSize, WireWriter } from './protocol/writer.js';
export { decodeUtf8, encodeUtf8 } from './protocol/utf8.js';
export {
  decodeAngle,
  decodeF16,
  decodeQuant,
  decodeQuat3,
  decodeQuat3Halves,
  decodeSnorm,
  decodeTickLo,
  decodeUnorm,
  decodeVec,
  decodeVel,
  encodeAngle,
  encodeF16,
  encodeQuant,
  encodeQuat3,
  encodeSnorm,
  encodeUnorm,
  encodeVec,
  encodeVel,
  pow2,
  quantStep,
  roundHalfAway,
  SQRT1_2,
  symmetricLimit,
  TAU,
  unsignedTop,
} from './protocol/math.js';
export {
  ArchetypePlan,
  CatalogError,
  CatalogPlan,
  FieldPlan,
  GridPlan,
  MessagePlan,
  MetricPlan,
  parseCatalog,
  PositionPlan,
  SectionPlan,
  ValueKind,
  type Catalog,
  type CatalogApp,
  type CatalogArchetype,
  type CatalogCodec,
  type CatalogCommand,
  type CatalogCommandRate,
  type CatalogEvent,
  type CatalogField,
  type CatalogGrid,
  type CatalogLimits,
  type CatalogMetric,
  type CatalogOwner,
  type CatalogPosition,
  type CatalogProtocolVersion,
  type CatalogTick,
} from './protocol/catalog.js';
export { CODEC_TOKENS, CodecKind, codecKindOf, isListElement, isPacked } from './protocol/codec-kinds.js';
export { checkCanonical, validateCatalog } from './protocol/catalog-validator.js';
export {
  MAX_LIST_COMPONENTS,
  readNumber,
  readPackedBits,
  readSection,
  writeNumber,
  writePackedBits,
  writeSection,
  type FieldSink,
  type FieldValue,
  type FieldValues,
} from './protocol/field-codec.js';
export {
  BlockMask,
  invalidStateMask,
  nextNetId,
  runLength,
  segmentsOfAStill,
  TickReader,
  type EntitiesDecoder,
  type EntitiesTarget,
  type GeneratedDecoders,
  type TickSink,
} from './protocol/tick-reader.js';
export {
  catalogHashOf,
  CODEGEN_USAGE,
  generateDecoders,
  parseCodegenArgs,
  type CodegenArguments,
  type GenerateOptions,
} from './codegen/generate.js';
export {
  beginBlock,
  endBlock,
  writeAcksBlock,
  writeAggregateBlock,
  writeDebugBlock,
  writeEntitiesBlock,
  writeEventsBlock,
  writeExtBlock,
  writeSelfBlock,
  writeSourcesBlock,
  writeStatsBlock,
  writeTickHeader,
  type AggregateCellInput,
  type EnterRecord,
  type EventInput,
  type SegmentRecord,
  type SourceInput,
  type StateRecord,
} from './protocol/tick-writer.js';
export { readCommands, writeCommands, type CommandInput, type CommandSink } from './protocol/commands.js';
export {
  catalogHashFromHex,
  catalogHashToHex,
  checkCapsGranted,
  parseBye,
  parseHello,
  parseKick,
  parseMessage,
  parsePing,
  parsePong,
  parseWelcome,
  readBye,
  readHello,
  readKick,
  readPing,
  readPong,
  readWelcome,
  truncateUtf8,
  writeBye,
  writeHello,
  writeKick,
  writePing,
  writePong,
  writeWelcome,
  type HelloMessage,
  type KickMessage,
  type PingMessage,
  type PongMessage,
  type WelcomeMessage,
} from './protocol/messages.js';

export {
  Connection,
  ConnectionState,
  type CatalogCache,
  type ConnectionClose,
  type ConnectionHandlers,
  type ConnectionOptions,
  type SessionInfo,
} from './net/connection.js';
export {
  SocketState,
  systemTimers,
  systemWebSocket,
  type SocketCloseEvent,
  type SocketMessageEvent,
  type TimerApi,
  type TimerHandle,
  type WebSocketFactory,
  type WebSocketLike,
} from './net/socket.js';
export { PingScheduler, type PingOptions } from './net/ping.js';
export {
  Backoff,
  ReconnectingClient,
  reconnectRule,
  ReconnectRule,
  type BackoffOptions,
  type ReconnectingClientOptions,
} from './net/reconnect.js';
export { forEachMessage, replayStream, StreamRecorder, type FrameConsumer } from './net/recorder.js';
export { CommandQueue, CommandRefused, type CommandQueueOptions } from './commands/queue.js';
export {
  createClientRegion,
  REGION_MAX_VERTICES,
  REGION_MIN_VERTICES,
  REGION_MIN_VERTICES_3D,
  REGION_RATE,
  RegionSender,
  type RegionOptions,
} from './interest/region.js';
