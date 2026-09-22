/** The part of Babylon's `Buffer` an upload needs (a `VertexBuffer`'s `getWrapperBuffer()`). */
export interface UploadTarget {
  updateDirectly(data: Float32Array, offset: number, vertexCount?: number): void;
}

/**
 * Uploads the used prefix of an instance buffer without allocating: the view handed to the GPU is one of a cached set of
 * power-of-two prefixes, so a frame uploads at most twice what it draws and creates no typed-array view.
 *
 * The vertex count is always passed, and equals the view's length. That keeps Babylon on its no-copy path, and stops it
 * keeping the view as the buffer's data: after a lost WebGL context it would rebuild the GPU buffer from that prefix,
 * too small for the next larger upload. Without kept data it rebuilds at the buffer's full capacity instead.
 */
export class PrefixUploader {
  private readonly data: Float32Array;
  private readonly stride: number;
  private readonly views: (Float32Array | undefined)[] = [];

  constructor(data: Float32Array, stride: number) {
    this.data = data;
    this.stride = stride;
  }

  /** Uploads at least `count` instances; returns how many the uploaded prefix holds (0 when nothing was sent). */
  upload(target: UploadTarget | null, count: number): number {
    if (target === null || count <= 0) {
      return 0;
    }

    const capacity = this.data.length / this.stride;
    // ceil(log2(n)) for n ≥ 1, in integers.
    const bucket = 32 - Math.clz32(Math.min(count, capacity) - 1);
    let view = this.views[bucket];
    if (view === undefined) {
      view = this.data.subarray(0, Math.min(2 ** bucket, capacity) * this.stride);
      this.views[bucket] = view;
    }

    const instances = view.length / this.stride;
    target.updateDirectly(view, 0, instances);
    return instances;
  }
}
