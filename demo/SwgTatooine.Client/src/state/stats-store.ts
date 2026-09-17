import { create } from 'zustand';
import type { SourceStats } from '../data/source';

/** A snapshot the renderer publishes four times a second for the HUD and the inspector. */

export interface LayerStats {
  readonly held: number;
  readonly near: number;
  readonly far: number;
}

export interface InspectedField {
  readonly name: string;
  readonly value: string;
}

export interface Inspection {
  readonly archetype: number;
  readonly netId: number;
  readonly x: number;
  readonly z: number;
  readonly speedMps: number;
  readonly headingDeg: number;
  readonly epoch: number;
  /** False for an archetype without a position: x, z, speed, heading and epoch mean nothing. */
  readonly hasPosition: boolean;
  readonly fields: readonly InspectedField[];
}

export interface FrameStats {
  readonly fps: number;
  /** Main-thread time of the client's own per-frame work (evaluate, cull, pack, upload), mean over the last 128 frames. */
  readonly frameJsMs: number;
  /** 95th percentile of the same 128 frames. */
  readonly frameJsP95Ms: number;
  /** Babylon's own frame time (`SceneInstrumentation.frameTimeCounter`: scene evaluation and draw submission), last-second average. */
  readonly frameTotalMs: number;
  readonly drawCalls: number;
  readonly layers: readonly LayerStats[];
  readonly attackLines: number;
  readonly renderTime: number;
  readonly latestTick: number;
  readonly renderDelayMs: number;
  readonly altitude: number;
  readonly nearRadius: number;
  readonly source: SourceStats;
  readonly inspection: Inspection | null;
  /** The held entity count the HUD must agree with (M1-3): the store's, not the renderer's. */
  readonly held: number;
  readonly anomalies: number;
}

interface StatsState {
  readonly stats: FrameStats | null;
  readonly publish: (stats: FrameStats) => void;
}

export const useStats = create<StatsState>()((set) => ({
  stats: null,
  publish: (stats) => {
    set({ stats });
  },
}));
