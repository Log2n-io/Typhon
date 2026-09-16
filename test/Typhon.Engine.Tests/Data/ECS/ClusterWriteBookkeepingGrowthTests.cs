using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Cwb.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CwbPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class CwbUnit : Archetype<CwbUnit>
{
    public static readonly Comp<CwbPos> Pos = Register<CwbPos>();
}

/// <summary>
/// CA-04: a flag written from a thread holding no latch lands in the LIVE write-bookkeeping arrays, even when another transaction's commit is growing them
/// at that moment (#903). Both scenarios are driven through the three test seams, so neither depends on timing.
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterWriteBookkeepingGrowthTests : TestBase<ClusterWriteBookkeepingGrowthTests>
{
    private const string Ca04Marker = "CA-04: the flag did not land in the live arrays";
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private const int FlaggedChunkId = 3;
    private const ulong FlaggedSlots = 1UL << 5;
    private const int FlaggedDestCell = 42;
    private const int GrownLength = 4096;

    /// <summary>The fix's protocol, as <c>ClusterRef</c> reaches it.</summary>
    private static void FlagUnderTheStamp(ArchetypeClusterState state, Action parkOnStamp)
    {
        state.WriteBookkeepingStampedProbe = parkOnStamp;
        state.FlagMigration(FlaggedChunkId, FlaggedSlots, FlaggedDestCell);
    }

    /// <summary>
    /// The pre-#903 form: the arrays are resolved once and written through that reference, with nothing confirming it is still the live one.
    /// </summary>
    private static void FlagWithoutTheStamp(ArchetypeClusterState state, Action parkOnStamp)
    {
        var slots = state.ClusterMigrationPendingSlots;
        var keys = state.ClusterMigrationDestCellKeys;
        parkOnStamp();
        Interlocked.Or(ref slots[FlaggedChunkId], FlaggedSlots);
        keys[FlaggedChunkId] = FlaggedDestCell;
    }

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-04")]
    public void AFlagWrittenDuringAGrowsCopy_LandsInTheGrownArrays() => FlagDuringAGrowsCopy(FlagUnderTheStamp);

    /// <summary>The same window, written the pre-#903 way: the OR lands in the array the grow has already copied, and the publish drops it.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("CA-04")]
    public void AFlagWrittenWithoutTheStamp_IsLostInTheGrowsCopy() =>
        RuleMutants.AssertDetects("CA-04", Ca04Marker, () => FlagDuringAGrowsCopy(FlagWithoutTheStamp));

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-04")]
    public void AFlagStampedBeforeAGrow_IsRedoneInTheGrownArrays() => FlagStampedBeforeAGrowsCopy(FlagUnderTheStamp);

    /// <summary>
    /// The process bit takes the same route, and a lost one is a cluster whose bound is never refreshed (CA-02) rather than a crossing never detected.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CA-04")]
    public void AProcessBitStampedBeforeAGrow_IsRedoneInTheGrownBitmap() =>
        FlagStampedBeforeAGrowsCopy(
            (state, parkOnStamp) =>
            {
                state.WriteBookkeepingStampedProbe = parkOnStamp;
                state.SetClusterProcessBit(FlaggedChunkId);
            },
            AssertTheProcessBitLanded);

    /// <summary>
    /// The pair split the protocol's write order exists to prevent: the slot bits go under the stamp and are redone, the destination hint is written
    /// through a reference resolved before the grow and is not. Half a crossing is what the rule's second invariant forbids.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("CA-04")]
    public void ACrossingWhoseHintMissesTheGrow_IsDetected() =>
        RuleMutants.AssertDetects("CA-04", Ca04Marker, () => FlagStampedBeforeAGrowsCopy(FlagWithASplitPair));

    /// <summary>The bits under the stamp, the hint outside it.</summary>
    private static void FlagWithASplitPair(ArchetypeClusterState state, Action parkOnStamp)
    {
        var staleKeys = state.ClusterMigrationDestCellKeys;   // resolved before the grow, so the stomp lands in the copy it abandons
        parkOnStamp();
        staleKeys[FlaggedChunkId] = FlaggedDestCell;
        int stamp;
        do
        {
            stamp = state.BeginWriteBookkeepingWrite();
            Interlocked.Or(ref Volatile.Read(ref state.ClusterMigrationPendingSlots)[FlaggedChunkId], FlaggedSlots);
        }
        while (!state.WriteBookkeepingWriteLanded(stamp));
    }

    /// <summary>The redo's case without the protocol: the flag is taken before the grow and written after its copy, so nothing brings it forward.</summary>
    [Test]
    [CancelAfter(15_000)]
    [RuleMutant("CA-04")]
    public void AFlagTakenBeforeAGrow_IsLostWithoutTheStamp() =>
        RuleMutants.AssertDetects("CA-04", Ca04Marker, () => FlagStampedBeforeAGrowsCopy(FlagWithoutTheStamp));

    /// <summary>
    /// The flagger starts INSIDE the grow's copy window: with the protocol it waits the grow out; without it, it writes into the doomed arrays.
    /// </summary>
    private void FlagDuringAGrowsCopy(Action<ArchetypeClusterState, Action> flag)
    {
        var state = StateWithBookkeeping();
        using var flagReleased = new ManualResetEventSlim();
        using var flagSettled = new ManualResetEventSlim();   // the flag returned, or it is waiting the grow out
        state.WriteBookkeepingWaitProbe = flagSettled.Set;
        state.WriteBookkeepingGrowCopiedProbe = () =>
        {
            flagReleased.Set();
            Await(flagSettled, "the flag neither returned nor waited for the grow");
        };

        var flagger = new Flagger(() =>
        {
            Await(flagReleased, "the grow never reached its copy");
            flag(state, static () => { });
        }, flagSettled);

        state.EnsureClusterWriteBookkeepingCapacity(GrownLength);
        flagger.AssertReturned();
        AssertTheFlagLanded(state);
    }

    /// <summary>The flagger takes its stamp BEFORE the grow and writes after the copy — the case the re-check exists for.</summary>
    private void FlagStampedBeforeAGrowsCopy(Action<ArchetypeClusterState, Action> flag, Action<ArchetypeClusterState> assert = null)
    {
        var state = StateWithBookkeeping();
        using var flagStamped = new ManualResetEventSlim();
        using var growCopied = new ManualResetEventSlim();
        using var flagSettled = new ManualResetEventSlim();
        var parked = 0;
        Action parkOnStamp = () =>
        {
            // The first attempt only: a redo runs straight through.
            if (Interlocked.Exchange(ref parked, 1) == 0)
            {
                flagStamped.Set();
                Await(growCopied, "the grow never reached its copy");
            }
        };
        state.WriteBookkeepingWaitProbe = flagSettled.Set;
        state.WriteBookkeepingGrowCopiedProbe = () =>
        {
            growCopied.Set();
            Await(flagSettled, "the flag neither returned nor waited for the grow");
        };

        var flagger = new Flagger(() => flag(state, parkOnStamp), flagSettled);
        Await(flagStamped, "the flag never took its stamp");
        state.EnsureClusterWriteBookkeepingCapacity(GrownLength);
        flagger.AssertReturned();
        if (assert != null)
        {
            assert(state);
        }
        else
        {
            AssertTheFlagLanded(state);
        }
    }

    /// <summary>An engine with one spatial archetype, its write-bookkeeping quartet sized small enough that the test's grow is a real one.</summary>
    private ArchetypeClusterState StateWithBookkeeping()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CwbPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(1000f, 1000f),
            cellSize: 100f));
        dbe.InitializeArchetypes();

        var state = dbe._archetypeStates[Archetype<CwbUnit>.Metadata.ArchetypeId].ClusterState;
        state.EnsureClusterWriteBookkeepingCapacity(64);
        Assert.That(state.ClusterMigrationPendingSlots.Length, Is.LessThan(GrownLength), "the quartet must start smaller than the grow this test drives");
        return state;
    }

    private static void AssertTheFlagLanded(ArchetypeClusterState state)
    {
        Assert.That(state.ClusterMigrationPendingSlots.Length, Is.GreaterThanOrEqualTo(GrownLength), "the grow must have happened, or the flag raced nothing");
        var landed = (state.ClusterMigrationPendingSlots[FlaggedChunkId], state.ClusterMigrationDestCellKeys[FlaggedChunkId]);
        Assert.That(landed, Is.EqualTo((FlaggedSlots, FlaggedDestCell)), Ca04Marker);
    }

    private static void AssertTheProcessBitLanded(ArchetypeClusterState state)
    {
        var requiredWords = (GrownLength + 63) >> 6;
        Assert.That(state.ClusterProcessBitmap.Length, Is.GreaterThanOrEqualTo(requiredWords), "the grow must have happened, or the flag raced nothing");
        var word = state.ClusterProcessBitmap[FlaggedChunkId >> 6];
        Assert.That(word & (1L << (FlaggedChunkId & 63)), Is.Not.Zero, Ca04Marker);
    }

    private static void Await(ManualResetEventSlim signal, string what)
    {
        if (!signal.Wait(HandshakeTimeout))
        {
            throw new TimeoutException(what);
        }
    }

    /// <summary>Runs one flag write on its own thread and sets <c>settled</c> when it returns, whether it threw or not.</summary>
    private sealed class Flagger
    {
        private readonly Thread _thread;
        private Exception _failure;

        public Flagger(Action flag, ManualResetEventSlim settled)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    flag();
                }
                catch (Exception e)
                {
                    _failure = e;
                }
                finally
                {
                    settled.Set();
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void AssertReturned()
        {
            Assert.That(_thread.Join(HandshakeTimeout), Is.True, "the flag write never returned");
            Assert.That(_failure, Is.Null);
        }
    }
}
