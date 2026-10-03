using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// <see cref="ChunkTable{T}"/> and the two front-ends that hand a chunk its record: <see cref="ChunkedCallbackSystem{TContext, TChunk}"/> and
/// <see cref="Dag.ChunkedSystem{TChunk}"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
class ChunkTableTests : TestBase<ChunkTableTests>
{
    private const int Dispatches = 60;
    private const int MaxChunks = 13;

    private struct Rec
    {
        public int Dispatch;
        public int Chunk;
        public long Check;
    }

    private static long CheckOf(int dispatch, int chunk) => dispatch * 1_000_003L + chunk * 31L + 7;

    // The chunk count of dispatch d: up and down, so a table is reused both larger and smaller than it was.
    private static int CountOf(int dispatch) => dispatch * 5 % MaxChunks + 1;

    // ═══════════════════════════════════════════════════════════════════════
    // ChunkTable on its own
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void Reset_SizesZeroesAndBoundsEveryRead()
    {
        var table = new ChunkTable<Rec>();
        var records = table.Reset(4);
        Assert.That(records.Length, Is.EqualTo(4));
        for (var i = 0; i < 4; i++)
        {
            table[i].Chunk = i + 100;
        }

        Assert.That(table[3].Chunk, Is.EqualTo(103));

        // Smaller: the old records beyond the new count are out of reach, and the ones within it are zeroed.
        table.Reset(2);
        Assert.Multiple(() =>
        {
            Assert.That(table.Count, Is.EqualTo(2));
            Assert.That(table[1].Chunk, Is.Zero, "Reset zeroes the records it hands back");
            Assert.That(() => _ = table[2], Throws.InvalidOperationException, "a record left over from a larger dispatch is never readable");
            Assert.That(() => _ = table[-1], Throws.InvalidOperationException);
        });

        // Larger than ever: grows.
        table.Reset(50);
        table[49].Chunk = 49;
        Assert.That(table[49].Chunk, Is.EqualTo(49));
        Assert.That(() => table.Reset(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // The record reaches its chunk
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>What prepare planned and what the chunks saw, per dispatch. Prepare and chunks of one system never overlap (CD-03).</summary>
    private sealed class Ledger
    {
        public int Dispatch = -1;
        public readonly int[] Planned = new int[Dispatches + 64];
        public readonly int[] Ran = new int[Dispatches + 64];
        public readonly List<string> Errors = [];

        public int Prepare(ChunkTable<Rec> plan)
        {
            var d = Dispatch + 1;
            if (d >= Planned.Length)
            {
                return 0;
            }

            var count = CountOf(d);
            var records = plan.Reset(count);
            for (var c = 0; c < count; c++)
            {
                records[c] = new Rec { Dispatch = d, Chunk = c, Check = CheckOf(d, c) };
            }

            Planned[d] = count;
            Dispatch = d;
            return count;
        }

        public void Execute(TickContext tick, ref Rec rec)
        {
            if (rec.Chunk != tick.ChunkIndex || rec.Dispatch != Dispatch || rec.Check != CheckOf(rec.Dispatch, rec.Chunk))
            {
                lock (Errors)
                {
                    Errors.Add($"chunk {tick.ChunkIndex} of dispatch {Dispatch} read {{dispatch {rec.Dispatch}, chunk {rec.Chunk}, check {rec.Check}}}");
                }
            }

            Interlocked.Increment(ref Ran[rec.Dispatch]);
        }

        public void AssertEveryChunkRanOnceWithItsRecord()
        {
            Assert.That(Errors, Is.Empty);
            Assert.That(Dispatch, Is.GreaterThanOrEqualTo(Dispatches), "precondition: enough dispatches ran");
            for (var d = 0; d <= Dispatch; d++)
            {
                Assert.That(Ran[d], Is.EqualTo(Planned[d]), $"dispatch {d}: every chunk it planned ran exactly once");
            }
        }
    }

    private sealed class LedgerContext
    {
        public readonly Ledger Ledger = new();
    }

    private sealed class LedgerSystem : ChunkedCallbackSystem<LedgerContext, Rec>
    {
        protected override void Configure(SystemBuilder<LedgerContext> b) => b
            .Name("Ledger")
            .ChunkedParallel(1);

        protected override int Prepare(LedgerContext ctx, ChunkTable<Rec> plan) => ctx.Ledger.Prepare(plan);

        protected override void Execute(TickContext tick, ref Rec chunk) => Context.Ledger.Execute(tick, ref chunk);
    }

    [Test]
    [VerifiesRule("CD-03")]
    public void TypedSystem_EachChunkRunsWithTheRecordItsDispatchWrote()
    {
        var context = new LedgerContext();
        Run(schedule => schedule.PublicTrack.DeclareDag("Test").Add(new LedgerSystem()),
            runtime => runtime.RegisterContext(context),
            () => Volatile.Read(ref context.Ledger.Dispatch) >= Dispatches);

        context.Ledger.AssertEveryChunkRanOnceWithItsRecord();
    }

    [Test]
    [VerifiesRule("CD-03")]
    public void LambdaSystem_EachChunkRunsWithTheRecordItsDispatchWrote()
    {
        var ledger = new Ledger();
        Run(schedule => schedule.PublicTrack.DeclareDag("Test").ChunkedSystem<Rec>("Ledger", ledger.Prepare, ledger.Execute),
            _ => { },
            () => Volatile.Read(ref ledger.Dispatch) >= Dispatches);

        ledger.AssertEveryChunkRanOnceWithItsRecord();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Prepare's return value
    // ═══════════════════════════════════════════════════════════════════════

    private sealed class FixedContext
    {
        public Func<ChunkTable<Rec>, int> Prepare;
        public int Ran;
        public int SuccessorRan;
        public readonly List<int> Chunks = [];
    }

    private sealed class FixedSystem : ChunkedCallbackSystem<FixedContext, Rec>
    {
        protected override void Configure(SystemBuilder<FixedContext> b) => b
            .Name("Fixed")
            .ChunkedParallel(3);

        protected override int Prepare(FixedContext ctx, ChunkTable<Rec> plan) => ctx.Prepare(plan);

        protected override void Execute(TickContext tick, ref Rec chunk)
        {
            Interlocked.Increment(ref Context.Ran);
            lock (Context.Chunks)
            {
                Context.Chunks.Add(chunk.Chunk);
            }
        }
    }

    private void RunFixed(FixedContext context, int ticks = 3)
    {
        var ticksSeen = 0;
        Run(schedule => schedule.PublicTrack.DeclareDag("Test")
                .Add(new FixedSystem())
                .CallbackSystem("Successor", _ => Interlocked.Increment(ref context.SuccessorRan), after: "Fixed")
                .CallbackSystem("Ticks", _ => Interlocked.Increment(ref ticksSeen)),
            runtime => runtime.RegisterContext(context),
            () => Volatile.Read(ref ticksSeen) >= ticks);
    }

    [Test]
    public void MinusOne_KeepsTheStaticCount_WithTheRecordsPrepareWrote()
    {
        var context = new FixedContext
        {
            Prepare = plan =>
            {
                var records = plan.Reset(3);
                for (var c = 0; c < 3; c++)
                {
                    records[c].Chunk = c;
                }

                return -1;
            }
        };
        RunFixed(context);

        Assert.That(context.Ran, Is.GreaterThanOrEqualTo(3).And.Matches<int>(n => n % 3 == 0), "three chunks per tick, the static ChunkedParallel(3)");
        Assert.That(new HashSet<int>(context.Chunks), Is.EquivalentTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void MinusOne_WithoutRecords_FailsTheSystem_RatherThanReadingNothing()
    {
        var context = new FixedContext { Prepare = _ => -1 };
        RunFixed(context);

        Assert.That(context.Ran, Is.Zero, "no chunk ran without a record");
        Assert.That(context.SuccessorRan, Is.Zero, "the system failed, so its successor was skipped");
    }

    [Test]
    public void MoreChunksThanRecords_FailsAtPrepare()
    {
        var context = new FixedContext
        {
            Prepare = plan =>
            {
                plan.Reset(2);
                return 5;
            }
        };
        RunFixed(context);

        Assert.That(context.Ran, Is.Zero, "the dispatch never opened");
        Assert.That(context.SuccessorRan, Is.Zero, "the system failed, so its successor was skipped");
    }

    [Test]
    public void Zero_SkipsTheSystem_AndItsSuccessorsStillRun()
    {
        var context = new FixedContext { Prepare = plan => plan.Reset(0).Length };
        RunFixed(context);

        Assert.That(context.Ran, Is.Zero);
        Assert.That(context.SuccessorRan, Is.GreaterThanOrEqualTo(3), "a skip is not a failure");
    }

    // ═══════════════════════════════════════════════════════════════════════

    private void Run(Action<RuntimeSchedule> declare, Action<TyphonRuntime> bind, Func<bool> done)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        using var runtime = TyphonRuntime.Create(dbe, declare, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 1000 });
        bind(runtime);
        runtime.Start();
        var reached = SpinWait.SpinUntil(done, TimeSpan.FromSeconds(10));
        runtime.Shutdown();
        Assert.That(reached, Is.True, "precondition: the runtime got far enough");
    }
}
