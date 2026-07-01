using System;
using System.Diagnostics;

namespace Typhon.CompetitiveBenchmark.Harness;

/// <summary>
/// Stopwatch-based ns/op micro-measurement (mirrors the existing <c>StorageModeCompareBenchmarks.Measure</c> pattern in
/// Typhon.Benchmark). Each invocation performs a *batch* of operations so the per-iteration <see cref="Stopwatch"/> read
/// (which itself costs ~8–20 ns via QPC) is amortized — critical when the op under test is ~14 ns. BDN replaces this for
/// the precision micro-latency scenarios (A1/C4/C8); for the C0 ladder a calibrated loop is enough and matches the
/// existing bench's conventions.
/// </summary>
public static class Measure
{
    /// <summary>
    /// Times <paramref name="batch"/> (which performs <paramref name="opsPerBatch"/> operations) until at least
    /// <paramref name="minMs"/> have elapsed, after <paramref name="warmupBatches"/> warmup runs. Returns ns per single op.
    /// </summary>
    public static double NsPerOp(Action batch, int opsPerBatch, int warmupBatches = 600, long minMs = 500)
    {
        for (int i = 0; i < warmupBatches; i++)
        {
            batch();
        }

        long batches = 0;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < minMs)
        {
            batch();
            batches++;
        }
        sw.Stop();

        double nsPerBatch = (double)sw.ElapsedTicks / Stopwatch.Frequency * 1_000_000_000.0 / batches;
        return nsPerBatch / opsPerBatch;
    }

    public static string FormatNs(double ns) =>
        ns >= 1_000_000 ? $"{ns / 1_000_000:F2} ms"
        : ns >= 1_000 ? $"{ns / 1_000:F2} µs"
        : $"{ns:F1} ns";
}
