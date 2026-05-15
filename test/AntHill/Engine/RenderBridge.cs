using System;

namespace AntHill;

/// <summary>
/// Double-buffered growable render buffer owned by a single worker thread.
/// The engine writes to <see cref="Data"/>/<see cref="Count"/>, then <see cref="Reset"/> swaps
/// the backing array so Godot can read the completed buffer while the engine writes the next frame.
/// </summary>
public sealed class RenderWorkerBuffer
{
    private const int Stride = 12;

    private float[][] _buffers = new float[2][];
    private int _writeSlot;

    /// <summary>Current write buffer. Written by the engine during FillRender.</summary>
    public float[] Data;

    /// <summary>Number of instances written to <see cref="Data"/> this frame.</summary>
    public int Count;

    public RenderWorkerBuffer(int initialCapacity)
    {
        int len = initialCapacity * Stride;
        _buffers[0] = new float[len];
        _buffers[1] = new float[len];
        _writeSlot = 0;
        Data = _buffers[0];
    }

    /// <summary>Swap to the other buffer for the next frame. Call AFTER snapshotting Data/Count.</summary>
    public void Reset()
    {
        _writeSlot ^= 1;
        Data = _buffers[_writeSlot];
        Count = 0;
    }

    /// <summary>Ensure backing array can hold at least <paramref name="additionalInstances"/> more. Call once per cluster.</summary>
    public void EnsureCapacity(int additionalInstances)
    {
        int needed = (Count + additionalInstances) * Stride;
        if (needed > Data.Length)
        {
            int newLen = Data.Length;
            while (newLen < needed) newLen *= 2;
            Array.Resize(ref Data, newLen);
            _buffers[_writeSlot] = Data;
        }
    }
}

/// <summary>
/// Immutable snapshot of one buffer: array reference + count at publish time.
/// Godot reads these — engine never modifies a published snapshot.
/// </summary>
public struct BufferSnapshot
{
    public float[] Data;
    public int Count;
}

/// <summary>
/// Published render data: immutable snapshots of per-worker buffers + overlay.
/// Each snapshot is consumed by its own MultiMeshInstance2D — zero copies.
/// </summary>
public sealed class RenderFrame
{
    public BufferSnapshot[] Buffers;
    public BufferSnapshot Overlay;
    public int VisibleAnts;

    /// <summary>Downsampled pheromone heatmap: RGBA byte array (200×200×4). Green=food, Blue=home. Ready for Image.SetData.</summary>
    public byte[] HeatmapRGBA;
    public const int HeatmapSize = 200;

    /// <summary>Phase 6B — fire CA state, 200×200 byte array (R8). One byte per cell: 0=Empty, 1=Fuel, 2=Burning. Ready for Image.SetData with Format.R8.</summary>
    public byte[] FireR8;

    /// <summary>Phase 6C — plant indices whose state changed since the last render frame (Alive→Burnt, Burnt→Despawned). VegetationRenderer drains the prefix [0, PlantDirtyCount) and pushes per-instance colour updates. Soft cap of 4096 / frame; missed transitions get re-enqueued by the next CA tick on the same cell.</summary>
    public int[] PlantDirty;
    public int PlantDirtyCount;
}

public sealed class RenderBridge
{
    private volatile RenderFrame _latest;

    public void Publish(RenderFrame frame) => _latest = frame;
    public RenderFrame GetLatest() => _latest;
}
