import { create } from 'zustand';

/** One resolved CPU-sample frame symbol (#351 Phase 4). `line === 0` means the frame has no source. */
export interface CpuFrameSymbol {
  frameId: number;
  method: string;
  file: string;
  line: number;
  categoryId: number;
}

/** One engine/host subsystem category (§8.6). */
export interface CpuCategory {
  id: number;
  name: string;
}

interface CpuFrameState {
  /** frameId → resolved symbol. Empty until the manifest loads. */
  byId: Map<number, CpuFrameSymbol>;
  /** categoryId → display name. */
  categoryName: Map<number, string>;
  /** True once a non-empty manifest has been loaded for the session. */
  hasData: boolean;
  setManifest: (frames: CpuFrameSymbol[], categories: CpuCategory[]) => void;
  clear: () => void;
}

/**
 * Holds the per-session CPU-sample frame-symbol manifest so the Call Tree panel can resolve a folded
 * tree node's `frameId` to a method name + `file:line`. Hydrated once per session by
 * {@link useCpuFrameManifest}; mirrors `useSourceLocationStore`'s role for #302 span site ids.
 */
export const useCpuFrameStore = create<CpuFrameState>()((set) => ({
  byId: new Map(),
  categoryName: new Map(),
  hasData: false,
  setManifest: (frames, categories) => {
    const byId = new Map<number, CpuFrameSymbol>();
    for (const f of frames) {
      byId.set(f.frameId, f);
    }
    const categoryName = new Map<number, string>();
    for (const c of categories) {
      categoryName.set(c.id, c.name);
    }
    set({ byId, categoryName, hasData: frames.length > 0 });
  },
  clear: () => set({ byId: new Map(), categoryName: new Map(), hasData: false }),
}));
