using NUnit.Framework;
using NUnit.Framework.Interfaces;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Typhon.Engine.Tests;

/// <summary>
/// Per-test memory ledger, off unless <c>TYPHON_MEMPROBE</c> names an output directory. For each test it appends one CSV row: private bytes before and
/// after, the peak private bytes seen while the test ran (sampled every 25 ms), the GC heap and its committed bytes after, the engines alive after the
/// test, and the duration. Private bytes minus GC committed is native memory — the page caches and the engine's own allocator.
/// </summary>
/// <remarks>
/// Built to find what drives a shard's test host to several GiB: a test whose <c>after</c> stays above its <c>before</c> retained memory (a static that
/// grows, an engine left alive); one whose <c>peak</c> is far above both allocated transiently. Run the suite single-worker for clean attribution.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MemoryProbeAttribute : Attribute, ITestAction
{
    private static readonly string s_dir = Environment.GetEnvironmentVariable("TYPHON_MEMPROBE");
    private static readonly object s_lock = new();
    private static StreamWriter s_out;
    private static Timer s_sampler;
    private static long s_peak;
    private static long s_before;
    private static long s_startTicks;

    /// <inheritdoc/>
    public ActionTargets Targets => ActionTargets.Test;

    private static long Private()
    {
        using var p = Process.GetCurrentProcess();
        return p.PrivateMemorySize64;
    }

    /// <inheritdoc/>
    public void BeforeTest(ITest test)
    {
        if (string.IsNullOrEmpty(s_dir))
        {
            return;
        }

        lock (s_lock)
        {
            if (s_out == null)
            {
                Directory.CreateDirectory(s_dir);
                s_out = new StreamWriter(Path.Combine(s_dir, $"memprobe-{Environment.ProcessId}.csv")) { AutoFlush = true };
                s_out.WriteLine("fixture,test,beforeMB,afterMB,peakMB,gcHeapMB,gcCommittedMB,liveEngines,maxLiveEngines,ms");
                s_sampler = new Timer(_ =>
                {
                    var now = Private();
                    long seen;
                    while (now > (seen = Interlocked.Read(ref s_peak)) && Interlocked.CompareExchange(ref s_peak, now, seen) != seen)
                    {
                    }
                }, null, 25, 25);
            }

            s_before = Private();
            Interlocked.Exchange(ref s_peak, s_before);
            s_startTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <inheritdoc/>
    public void AfterTest(ITest test)
    {
        if (string.IsNullOrEmpty(s_dir))
        {
            return;
        }

        lock (s_lock)
        {
            var after = Private();
            var peak = Math.Max(Interlocked.Read(ref s_peak), after);
            var gc = GC.GetGCMemoryInfo();
            var ms = Stopwatch.GetElapsedTime(s_startTicks).TotalMilliseconds;
            const double mb = 1024 * 1024;
            var fixture = test.ClassName?.Replace("Typhon.Engine.Tests.", "") ?? "?";
            var name = test.Name.Replace(',', ';').Replace('"', '\'');
            s_out.WriteLine(
                $"{fixture},\"{name}\",{s_before / mb:F0},{after / mb:F0},{peak / mb:F0},{gc.HeapSizeBytes / mb:F0},{gc.TotalCommittedBytes / mb:F0},"
                + $"{ArchetypeRegistry.CurrentLiveEngineCount},{ArchetypeRegistry.MaxLiveEngineCount},{ms:F0}");
        }
    }
}
