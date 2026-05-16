import { formatDuration } from './canvasUtils';
import type { TimeAreaHover } from './timeAreaHitTest';
import { OffCpuCategoryNames, TraceEventKind, WaitReasonDescriptions } from '@/libs/profiler/model/types';

/**
 * Build the text lines shown in the TimeArea's hover tooltip for span / chunk / phase / mini-row-op
 * / tick hovers. Returns `null` for hovers that don't warrant a tooltip (help glyph, gutter
 * chevron, gauge — each has its own dedicated overlay).
 *
 * Kept as a pure function so the React wrapper only needs to supply the hit-test result; no store
 * reads or canvas state. Consumers portal the output through {@link HelpOverlay} or any equivalent
 * multi-line text overlay.
 */
export function buildHoverTooltipLines(hover: TimeAreaHover): string[] | null {
  if (!hover) return null;
  switch (hover.kind) {
    case 'span': {
      const s = hover.span;
      const lines = [
        s.name,
        `Duration: ${formatDuration(s.durationUs)}`,
        `Thread slot: ${s.threadSlot}`,
      ];
      const d = s.depth ?? 0;
      if (d > 0) lines.push(`Depth: ${d}`);
      if (s.kickoffDurationUs !== undefined && s.kickoffDurationUs !== s.durationUs) {
        lines.push(`Kickoff: ${formatDuration(s.kickoffDurationUs)}`);
      }
      // Kind-specific metadata from the rawEvent. ClusterMigration: archetype + entity count + total
      // component instances moved (entities × per-entity slot count). The `componentCount` value comes
      // from the engine's wire-additive payload; on traces produced by older engines it's undefined.
      if (s.kind === TraceEventKind.ClusterMigration && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.migrationCount !== undefined) lines.push(`Entities: ${s.rawEvent.migrationCount.toLocaleString()}`);
        if (s.rawEvent.componentCount !== undefined) lines.push(`Components: ${s.rawEvent.componentCount.toLocaleString()}`);
      }
      // SpatialClusterMigrationDetectScan (249): begin = (archetype, scanSlotCount), optional outcomes
      // = migrationsQueued / hysteresisAbsorbed / clustersTouched. Older traces lack the optional set.
      if ((s.kind as number) === 249 && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.scanSlotCount !== undefined) lines.push(`Scan slots: ${s.rawEvent.scanSlotCount.toLocaleString()}`);
        if (s.rawEvent.clustersTouched !== undefined) lines.push(`Clusters touched: ${s.rawEvent.clustersTouched.toLocaleString()}`);
        if (s.rawEvent.migrationsQueued !== undefined) lines.push(`Migrations queued: ${s.rawEvent.migrationsQueued.toLocaleString()}`);
        if (s.rawEvent.hysteresisAbsorbed !== undefined) lines.push(`Hysteresis absorbed: ${s.rawEvent.hysteresisAbsorbed.toLocaleString()}`);
      }
      // SpatialClusterAabbRefresh (250): begin = (archetype, clusterScanned), optional outcomes =
      // aabbsChanged / slotsScanned / outlierGuardFires.
      if ((s.kind as number) === 250 && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.clusterScanned !== undefined) lines.push(`Clusters scanned: ${s.rawEvent.clusterScanned.toLocaleString()}`);
        if (s.rawEvent.slotsScanned !== undefined) lines.push(`Slots scanned: ${s.rawEvent.slotsScanned.toLocaleString()}`);
        if (s.rawEvent.aabbsChanged !== undefined) lines.push(`AABBs changed: ${s.rawEvent.aabbsChanged.toLocaleString()}`);
        if (s.rawEvent.outlierGuardFires !== undefined && s.rawEvent.outlierGuardFires > 0) {
          lines.push(`Outlier guard fires: ${s.rawEvent.outlierGuardFires.toLocaleString()}`);
        }
      }
      // WriteTickFenceTable (251): begin = (componentTypeId, dirtyEntryCount), optional = walPublished/hasShadow/hasSpatial.
      if ((s.kind as number) === 251 && s.rawEvent) {
        if (s.rawEvent.componentTypeId !== undefined) lines.push(`Component: #${s.rawEvent.componentTypeId}`);
        if (s.rawEvent.dirtyEntryCount !== undefined) lines.push(`Dirty entries: ${s.rawEvent.dirtyEntryCount.toLocaleString()}`);
        const flags: string[] = [];
        if (s.rawEvent.walPublished) flags.push('WAL');
        if (s.rawEvent.hasShadow) flags.push('Shadow');
        if (s.rawEvent.hasSpatial) flags.push('Spatial');
        if (flags.length > 0) lines.push(`Paths: ${flags.join(' + ')}`);
      }
      // WriteTickFenceShadow (252): begin = (componentTypeId, indexedFieldCount), optional = totalShadowEntries.
      if ((s.kind as number) === 252 && s.rawEvent) {
        if (s.rawEvent.componentTypeId !== undefined) lines.push(`Component: #${s.rawEvent.componentTypeId}`);
        if (s.rawEvent.indexedFieldCount !== undefined) lines.push(`Indexed fields: ${s.rawEvent.indexedFieldCount.toLocaleString()}`);
        if (s.rawEvent.totalShadowEntries !== undefined) lines.push(`Shadow entries: ${s.rawEvent.totalShadowEntries.toLocaleString()}`);
      }
      // WriteTickFenceSpatial (253): begin = (componentTypeId, dirtyEntryCount), optional = escapedCount.
      if ((s.kind as number) === 253 && s.rawEvent) {
        if (s.rawEvent.componentTypeId !== undefined) lines.push(`Component: #${s.rawEvent.componentTypeId}`);
        if (s.rawEvent.dirtyEntryCount !== undefined) lines.push(`Dirty entries: ${s.rawEvent.dirtyEntryCount.toLocaleString()}`);
        if (s.rawEvent.escapedCount !== undefined) lines.push(`Escaped: ${s.rawEvent.escapedCount.toLocaleString()}`);
      }
      // WriteTickFenceCluster (61): begin = (archetypeId), all else optional.
      if ((s.kind as number) === 61 && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.dirtyClusterCount !== undefined) lines.push(`Dirty clusters: ${s.rawEvent.dirtyClusterCount.toLocaleString()}`);
        if (s.rawEvent.entryCount !== undefined) lines.push(`Dirty entries: ${s.rawEvent.entryCount.toLocaleString()}`);
        const flags: string[] = [];
        if (s.rawEvent.walPublished) flags.push('WAL');
        if (s.rawEvent.hasShadow) flags.push('Shadow');
        if (s.rawEvent.hasSpatial) flags.push('Spatial');
        if (flags.length > 0) lines.push(`Paths: ${flags.join(' + ')}`);
      }
      // WriteTickFenceClusterShadow (62): begin = (archetypeId, dirtyClusterCount), optional = totalShadowEntries.
      if ((s.kind as number) === 62 && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.dirtyClusterCount !== undefined) lines.push(`Dirty clusters: ${s.rawEvent.dirtyClusterCount.toLocaleString()}`);
        if (s.rawEvent.totalShadowEntries !== undefined) lines.push(`Shadow entries: ${s.rawEvent.totalShadowEntries.toLocaleString()}`);
      }
      // WriteTickFenceClusterSpatial (63): begin = (archetypeId, dirtyClusterCount), optional = migrationsExecuted.
      if ((s.kind as number) === 63 && s.rawEvent) {
        if (s.rawEvent.archetypeId !== undefined) lines.push(`Archetype: #${s.rawEvent.archetypeId}`);
        if (s.rawEvent.dirtyClusterCount !== undefined) lines.push(`Dirty clusters: ${s.rawEvent.dirtyClusterCount.toLocaleString()}`);
        if (s.rawEvent.migrationsExecuted !== undefined && s.rawEvent.migrationsExecuted > 0) {
          lines.push(`Migrations: ${s.rawEvent.migrationsExecuted.toLocaleString()}`);
        }
      }
      return lines;
    }
    case 'chunk': {
      const c = hover.chunk;
      const label = c.isParallel ? `${c.systemName}[${c.chunkIndex}]` : c.systemName;
      const lines = [
        label,
        `Duration: ${formatDuration(c.durationUs)}`,
        `Thread slot: ${c.threadSlot}`,
      ];
      if (c.entitiesProcessed > 0) lines.push(`Entities: ${c.entitiesProcessed.toLocaleString()}`);
      if (c.isParallel) lines.push(`Parallel: ${c.totalChunks} chunks`);
      return lines;
    }
    case 'phase': {
      const p = hover.phase;
      return [
        `Phase: ${p.phaseName}`,
        `Tick: ${hover.tickNumber}`,
        `Duration: ${formatDuration(p.durationUs)}`,
      ];
    }
    case 'phase-marker': {
      const m = hover.marker;
      const lines = [
        `Marker: ${m.label}`,
        `Tick: ${hover.tickNumber}`,
      ];
      if (m.detail !== undefined) {
        lines.push(m.detail);
      }
      return lines;
    }
    case 'mini-row-op': {
      const op = hover.op;
      return [
        `${hover.rowLabel}: ${op.name}`,
        `Duration: ${formatDuration(op.durationUs)}`,
        `Thread slot: ${op.threadSlot}`,
      ];
    }
    case 'off-cpu': {
      const iv = hover.interval;
      const lines = [
        `Off-CPU — ${OffCpuCategoryNames[iv.category] ?? 'Other'}`,
        `Duration: ${formatDuration(iv.durationUs)}`,
        `Reason: ${WaitReasonDescriptions[iv.waitReason] ?? `Reason ${iv.waitReason}`}`,
        `Thread slot: ${iv.threadSlot}`,
        `Last CPU: ${iv.processorNumber}`,
      ];
      // Ready-queue latency is the scheduler-pressure signal — only meaningful when non-zero (0 = unknown).
      if (iv.readyTimeUs > 0) lines.push(`Ready-queue wait: ${formatDuration(iv.readyTimeUs)}`);
      return lines;
    }
    case 'tick':
    case 'help':
    case 'gutter-chevron':
    case 'gauge':
      return null;
  }
}
