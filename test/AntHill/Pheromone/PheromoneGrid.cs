using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

namespace AntHill;

/// <summary>
/// Two-channel pheromone grid: food trail + home trail.
/// Flat float arrays, O(1) lookup by world coordinate. No ECS dependency.
/// </summary>
public sealed class PheromoneGrid
{
    public const int GridSize = 1000;           // 1000×1000 cells
    public const float CellSize = 20f;          // 20 world units per cell
    public const float InvCellSize = 1f / CellSize;
    public const float MaxPheromone = 255f;

    /// <summary>Food trail: deposited by returning ants, followed by foraging ants.</summary>
    public readonly float[] Food = new float[GridSize * GridSize];

    /// <summary>Home trail: deposited by foraging ants, followed by returning ants.</summary>
    public readonly float[] Home = new float[GridSize * GridSize];

    /// <summary>Fight alarm: deposited by damaged ants (combat or spider). Workers flee; soldiers approach.</summary>
    public readonly float[] Fight = new float[GridSize * GridSize];

    /// <summary>Fire warning: deposited by ants near Burning cells. Both castes flee.</summary>
    public readonly float[] Fire = new float[GridSize * GridSize];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int WorldToIndex(float wx, float wy)
    {
        var gx = Math.Clamp((int)(wx * InvCellSize), 0, GridSize - 1);
        var gy = Math.Clamp((int)(wy * InvCellSize), 0, GridSize - 1);
        return gy * GridSize + gx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadFood(float wx, float wy) => Food[WorldToIndex(wx, wy)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadHome(float wx, float wy) => Home[WorldToIndex(wx, wy)];

    /// <summary>
    /// Deposit pheromone. No synchronization — rare lost updates on same 20×20 cell
    /// are acceptable for pheromone simulation. Avoids Interlocked overhead per ant.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Deposit(float[] channel, int index, float amount)
    {
        var val = channel[index] + amount;
        if (val > MaxPheromone) val = MaxPheromone;
        channel[index] = val;
    }

    /// <summary>
    /// Deposit pheromone in a 3×3 cell stamp centred at (wx, wy) with falloff:
    /// centre 1.0, edge 0.5, corner 0.25. Used by event-driven channels (Fight) where a single
    /// deposit needs to reach ants whose 3-sensor footprint may not overlap the centre cell.
    /// Total area-integrated mass per call ≈ 4× the centre amount; coverage radius ≈ 1.5 cells
    /// (≈ 30 sim units / 0.15 m) on top of the centre.
    /// </summary>
    public static void DepositArea(float[] channel, float wx, float wy, float amount)
    {
        var gx = (int)(wx * InvCellSize);
        var gy = (int)(wy * InvCellSize);
        if (gx < 0) gx = 0; else if (gx >= GridSize) gx = GridSize - 1;
        if (gy < 0) gy = 0; else if (gy >= GridSize) gy = GridSize - 1;

        var x0 = gx > 0 ? gx - 1 : gx;
        var x1 = gx < GridSize - 1 ? gx + 1 : gx;
        var y0 = gy > 0 ? gy - 1 : gy;
        var y1 = gy < GridSize - 1 ? gy + 1 : gy;

        for (var y = y0; y <= y1; y++)
        {
            var dyAbs = y == gy ? 0 : 1;
            var row = y * GridSize;
            for (var x = x0; x <= x1; x++)
            {
                var dxAbs = x == gx ? 0 : 1;
                var weight = (dxAbs == 0 && dyAbs == 0) ? 1f
                              : (dxAbs == 0 || dyAbs == 0) ? 0.5f
                              : 0.25f;
                Deposit(channel, row + x, amount * weight);
            }
        }
    }

    private const int EvaporateChunks = 16;

    /// <summary>
    /// Evaporate both channels in parallel. Splits the grid into 16 stripes,
    /// each processed with AVX intrinsics. Memory-bandwidth bound, parallelized
    /// across cores for aggregate L3 bandwidth.
    /// </summary>
    public void Evaporate(float decayFactor)
    {
        var len = Food.Length;
        // Chunk size must be a multiple of Vector256<float>.Count (8) so each worker's
        // SIMD loop is clean and we don't need per-chunk tail handling inside the parallel body.
        var vecSize = Vector256<float>.Count;
        var chunkSize = ((len / EvaporateChunks) / vecSize) * vecSize;
        var parallelEnd = chunkSize * EvaporateChunks;

        var food = Food;
        var home = Home;

        Parallel.For(0, EvaporateChunks, chunk =>
        {
            var start = chunk * chunkSize;
            var end = start + chunkSize;
            EvaporateRange(food, home, start, end, decayFactor);
        });

        // Scalar tail for the last few elements not covered by parallel chunks
        for (var i = parallelEnd; i < len; i++)
        {
            food[i] *= decayFactor;
            home[i] *= decayFactor;
        }
    }

    /// <summary>
    /// Single-channel evaporator for the danger channels (Fight, Fire). Same parallel-chunked
    /// + AVX shape as <see cref="Evaporate"/> but operates on one array so each channel can decay
    /// at its own rate (Fight is fast, Fire is slow).
    /// </summary>
    public void EvaporateChannel(float[] channel, float decayFactor)
    {
        var len = channel.Length;
        var vecSize = Vector256<float>.Count;
        var chunkSize = ((len / EvaporateChunks) / vecSize) * vecSize;
        var parallelEnd = chunkSize * EvaporateChunks;

        Parallel.For(0, EvaporateChunks, chunk =>
        {
            var start = chunk * chunkSize;
            var end = start + chunkSize;
            EvaporateSingleRange(channel, start, end, decayFactor);
        });

        for (var i = parallelEnd; i < len; i++)
        {
            channel[i] *= decayFactor;
        }
    }

    private static void EvaporateSingleRange(float[] arr, int start, int end, float decayFactor)
    {
        ref var p = ref MemoryMarshal.GetArrayDataReference(arr);
        if (Avx.IsSupported)
        {
            var decay256 = Vector256.Create(decayFactor);
            var vecSize = Vector256<float>.Count;
            var i = (nuint)start;
            var simdEnd = (nuint)end;
            for (; i + (nuint)vecSize <= simdEnd; i += (nuint)vecSize)
            {
                Avx.Multiply(Vector256.LoadUnsafe(ref p, i), decay256).StoreUnsafe(ref p, i);
            }
        }
        else
        {
            for (var i = start; i < end; i++)
            {
                Unsafe.Add(ref p, i) *= decayFactor;
            }
        }
    }

    private static void EvaporateRange(float[] food, float[] home, int start, int end, float decayFactor)
    {
        ref var fp = ref MemoryMarshal.GetArrayDataReference(food);
        ref var hp = ref MemoryMarshal.GetArrayDataReference(home);

        if (Avx.IsSupported)
        {
            var decay256 = Vector256.Create(decayFactor);
            var vecSize = Vector256<float>.Count;
            var i = (nuint)start;
            var simdEnd = (nuint)end;
            for (; i + (nuint)vecSize <= simdEnd; i += (nuint)vecSize)
            {
                var f = Vector256.LoadUnsafe(ref fp, i);
                var h = Vector256.LoadUnsafe(ref hp, i);
                Avx.Multiply(f, decay256).StoreUnsafe(ref fp, i);
                Avx.Multiply(h, decay256).StoreUnsafe(ref hp, i);
            }
        }
        else
        {
            for (var i = start; i < end; i++)
            {
                Unsafe.Add(ref fp, i) *= decayFactor;
                Unsafe.Add(ref hp, i) *= decayFactor;
            }
        }
    }
}
