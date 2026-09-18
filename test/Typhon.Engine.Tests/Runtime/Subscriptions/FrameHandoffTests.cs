using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// P1-14a — the sequence hand-off: <see cref="SessionSendState"/>'s four counters, the two release/acquire pairs they form, and the committed-tick gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verifier is a model checker, not a race.</b> A two-thread test can only ever say "no violation was observed on this machine, this time", and on x64
/// it says that even for code that is plainly wrong, because total store order hides a missing release. So SUB-04's verifier here drives the real
/// <see cref="SessionSendState"/> through <b>every reachable interleaving</b> of the producer's steps, the send loop's steps and the tick driver's, and checks
/// the invariants in every state it can reach. What it proves is the protocol: that no schedule of those steps lets a byte be read before its release or a
/// slot be reused before its acquire. The two-thread million-hand-off case below is the complement — it proves the same code survives real concurrency and
/// real delays — and neither replaces the other.
/// </para>
/// <para>
/// <b>Memory ordering itself is not what a single-threaded explorer checks</b>, and saying so is the honest scope of this fixture: the <c>Volatile</c> pairs
/// are checked by the arm64 nightly and by the threaded case. What is checked here is the part that a race cannot check exhaustively — the counter discipline
/// around them.
/// </para>
/// </remarks>
[TestFixture]
unsafe class FrameHandoffTests
{
    // Markers. Each is the distinctive text the corresponding RuleMutant expects to see; they are the verifier's own voice, which is what makes a mutant's
    // failure evidence rather than noise (see RuleMutants.AssertDetects).
    private const string ReadBeforeRelease = "SUB-04 violated: read before the ReadySeq release";
    private const string ReuseBeforeAcquire = "SUB-04 violated: slot reused before the SentSeq acquire";
    private const string UncommittedSend = "SUB-04 violated: an uncommitted tick was sent";
    private const string TornFrame = "SUB-04 violated: the frame read is not the frame published";

    /// <summary>The harness's frame is two 8-byte halves, so an interleaving can land between them and be seen to.</summary>
    private const int FrameBytes = 16;

    /// <summary>
    /// Recursion and snapshot bound. Asserted never to bind, so a state space that grows is a visible failure rather than a silent truncation.
    /// </summary>
    private const int MaxDepth = 8192;

    #region The interleaving harness

    /// <summary>What the harness makes the producer or the consumer do wrong. <see cref="HandoffMutation.None"/> is the protocol as designed.</summary>
    private enum HandoffMutation
    {
        /// <summary>The protocol as SUB-04 states it.</summary>
        None,

        /// <summary>The release of <c>ReadySeq</c> is moved in front of the frame's bytes.</summary>
        PublishBeforeWriting,

        /// <summary>The release of <c>SentSeq</c> is moved in front of the link's reads, so the producer's acquire lets it reuse a live slot.</summary>
        CompleteBeforeReading,

        /// <summary>The send loop ignores the committed tick and sends whatever is ready.</summary>
        IgnoreTheCommittedTick
    }

    private enum ProducerAction
    {
        Begin,
        WriteLowHalf,
        WriteHighHalf,
        Publish
    }

    private enum ConsumerAction
    {
        Claim,
        ReadLowHalf,
        ReadHighHalf,
        Complete
    }

    /// <summary>
    /// Everything the explorer has to snapshot: the real <see cref="SessionSendState"/>, the two frame buffers, the committed tick, both participants'
    /// program counters and the shadow bookkeeping the invariants are stated over.
    /// </summary>
    /// <remarks>
    /// All native and all blittable, so a branch is one struct copy and a backtrack is another. The shadow fields (<c>Writing</c>, <c>Reading</c>,
    /// <c>Stamp</c>) are what the rule is checked against — the state under test cannot be asked whether it is mid-write, so the harness records it.
    /// </remarks>
    private struct Harness
    {
        public fixed byte Send[SessionSendState.Bytes];
        public fixed byte Frame0[FrameBytes];
        public fixed byte Frame1[FrameBytes];

        public long CommittedTick;

        public int ProducerPc;
        public long ProducerSeq;
        public int ConsumerPc;
        public long ConsumerSeq;
        public long ReadLow;

        public int Writing0;
        public int Writing1;
        public int Reading0;
        public int Reading1;
        public long Stamp0;
        public long Stamp1;
    }

    /// <summary>
    /// What the exploration actually covered. Asserted on, so a harness that silently stops exercising a path fails rather than passing faster.
    /// </summary>
    private struct Coverage
    {
        public int States;
        public int Transitions;
        public int FramesPublished;
        public int FramesCompleted;
        public int InFlightSkips;
        public int TickGateRefusals;
        public int NothingReadyRefusals;
        public int DepthCapHits;
    }

    private static byte* FrameFor(Harness* h, long sequence) => (sequence & 1) == 0 ? h->Frame0 : h->Frame1;

    private static int WritingOf(Harness* h, long sequence) => (sequence & 1) == 0 ? h->Writing0 : h->Writing1;

    private static void SetWriting(Harness* h, long sequence, int value)
    {
        if ((sequence & 1) == 0)
        {
            h->Writing0 = value;
        }
        else
        {
            h->Writing1 = value;
        }
    }

    private static int ReadingOf(Harness* h, long sequence) => (sequence & 1) == 0 ? h->Reading0 : h->Reading1;

    private static void SetReading(Harness* h, long sequence, int value)
    {
        if ((sequence & 1) == 0)
        {
            h->Reading0 = value;
        }
        else
        {
            h->Reading1 = value;
        }
    }

    private static long StampOf(Harness* h, long sequence) => (sequence & 1) == 0 ? h->Stamp0 : h->Stamp1;

    private static void SetStamp(Harness* h, long sequence, long value)
    {
        if ((sequence & 1) == 0)
        {
            h->Stamp0 = value;
        }
        else
        {
            h->Stamp1 = value;
        }
    }

    private static ProducerAction ProducerActionAt(HandoffMutation mutation, int pc) => mutation == HandoffMutation.PublishBeforeWriting
        ? pc switch
        {
            0 => ProducerAction.Begin,
            1 => ProducerAction.Publish,
            2 => ProducerAction.WriteLowHalf,
            _ => ProducerAction.WriteHighHalf
        }
        : pc switch
        {
            0 => ProducerAction.Begin,
            1 => ProducerAction.WriteLowHalf,
            2 => ProducerAction.WriteHighHalf,
            _ => ProducerAction.Publish
        };

    private static ConsumerAction ConsumerActionAt(HandoffMutation mutation, int pc) => mutation == HandoffMutation.CompleteBeforeReading
        ? pc switch
        {
            0 => ConsumerAction.Claim,
            1 => ConsumerAction.Complete,
            2 => ConsumerAction.ReadLowHalf,
            _ => ConsumerAction.ReadHighHalf
        }
        : pc switch
        {
            0 => ConsumerAction.Claim,
            1 => ConsumerAction.ReadLowHalf,
            2 => ConsumerAction.ReadHighHalf,
            _ => ConsumerAction.Complete
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            Assert.Fail(message);
        }
    }

    private static void ProducerStep(Harness* h, HandoffMutation mutation, ref Coverage coverage)
    {
        var state = (SessionSendState*)h->Send;

        switch (ProducerActionAt(mutation, h->ProducerPc))
        {
            case ProducerAction.Begin:
            {
                if (!state->TryBeginFrame(out var sequence, out var recycled))
                {
                    coverage.InFlightSkips++;
                    return;
                }

                Require(ReadingOf(h, sequence) == 0,
                    $"{ReuseBeforeAcquire} — the producer claimed slot {sequence & 1} for sequence {sequence} while the send side still read the frame "
                    + "it held. The acquire of SentSeq is what must make that unrepresentable.");

                Require(!recycled.IsValid || recycled.Bytes == FrameFor(h, sequence),
                    $"{ReuseBeforeAcquire} — the block handed back for slot {sequence & 1} is not the block that slot held.");

                h->ProducerSeq = sequence;
                SetWriting(h, sequence, 1);
                h->ProducerPc = 1;
                return;
            }

            case ProducerAction.WriteLowHalf:
            {
                var sequence = h->ProducerSeq;
                Require(ReadingOf(h, sequence) == 0,
                    $"{ReuseBeforeAcquire} — the producer overwrote slot {sequence & 1} while the send side was still reading it.");

                *(long*)FrameFor(h, sequence) = sequence;
                h->ProducerPc++;
                return;
            }

            case ProducerAction.WriteHighHalf:
            {
                var sequence = h->ProducerSeq;
                Require(ReadingOf(h, sequence) == 0,
                    $"{ReuseBeforeAcquire} — the producer overwrote slot {sequence & 1} while the send side was still reading it.");

                *(long*)(FrameFor(h, sequence) + 8) = sequence;
                SetWriting(h, sequence, 0);
                SetStamp(h, sequence, sequence);
                h->ProducerPc = mutation == HandoffMutation.PublishBeforeWriting ? 0 : 3;

                if (mutation == HandoffMutation.PublishBeforeWriting)
                {
                    h->ProducerSeq = -1;
                }

                return;
            }

            default:
            {
                var sequence = h->ProducerSeq;
                state->PublishFrame(sequence, new FrameBlock(FrameFor(h, sequence), FrameBytes), FrameBytes, sequence + 1);
                coverage.FramesPublished++;
                h->ProducerPc = mutation == HandoffMutation.PublishBeforeWriting ? 2 : 0;

                if (mutation != HandoffMutation.PublishBeforeWriting)
                {
                    h->ProducerSeq = -1;
                }

                return;
            }
        }
    }

    private static void ConsumerStep(Harness* h, HandoffMutation mutation, ref Coverage coverage)
    {
        var state = (SessionSendState*)h->Send;

        switch (ConsumerActionAt(mutation, h->ConsumerPc))
        {
            case ConsumerAction.Claim:
            {
                var gate = mutation == HandoffMutation.IgnoreTheCommittedTick ? long.MaxValue : h->CommittedTick;
                var readyBefore = state->ReadySequence;
                var sentBefore = state->SentSequence;

                if (!state->TryClaimFrame(gate, out var frame))
                {
                    if (sentBefore < readyBefore)
                    {
                        coverage.TickGateRefusals++;
                    }
                    else
                    {
                        coverage.NothingReadyRefusals++;
                    }

                    return;
                }

                Require(frame.Tick <= h->CommittedTick,
                    $"{UncommittedSend} — the send loop took a frame stamped tick {frame.Tick} while the driver has published only {h->CommittedTick}. "
                    + "Publication is gated on the flush, not merely ordered after it.");

                Require(WritingOf(h, frame.Sequence) == 0,
                    $"{ReadBeforeRelease} — the send loop claimed sequence {frame.Sequence} while the producer was still writing its bytes. The release of "
                    + "ReadySeq is the only thing that makes a frame readable.");

                Require(frame.Bytes == FrameFor(h, frame.Sequence) && frame.Length == FrameBytes,
                    $"{TornFrame} — the claimed view does not describe the block published for sequence {frame.Sequence}.");

                h->ConsumerSeq = frame.Sequence;
                SetReading(h, frame.Sequence, 1);
                h->ConsumerPc = 1;
                return;
            }

            case ConsumerAction.ReadLowHalf:
            {
                var sequence = h->ConsumerSeq;
                Require(WritingOf(h, sequence) == 0,
                    $"{ReadBeforeRelease} — bytes 0-7 of sequence {sequence} were read while the producer was still writing them.");

                h->ReadLow = *(long*)FrameFor(h, sequence);
                h->ConsumerPc++;
                return;
            }

            case ConsumerAction.ReadHighHalf:
            {
                var sequence = h->ConsumerSeq;
                Require(WritingOf(h, sequence) == 0,
                    $"{ReadBeforeRelease} — bytes 8-15 of sequence {sequence} were read while the producer was still writing them.");

                var high = *(long*)(FrameFor(h, sequence) + 8);
                Require(high == h->ReadLow, $"{TornFrame} — sequence {sequence} read as {h->ReadLow} then {high}: two halves of two different frames.");
                Require(high == sequence, $"{TornFrame} — sequence {sequence} carries the bytes of frame {high}.");
                Require(StampOf(h, sequence) == sequence, $"{TornFrame} — the slot for sequence {sequence} last held frame {StampOf(h, sequence)}.");

                SetReading(h, sequence, 0);
                h->ConsumerPc = mutation == HandoffMutation.CompleteBeforeReading ? 0 : 3;

                if (mutation == HandoffMutation.CompleteBeforeReading)
                {
                    h->ConsumerSeq = -1;
                }

                return;
            }

            default:
            {
                var sequence = h->ConsumerSeq;
                state->CompleteSend(sequence);
                coverage.FramesCompleted++;
                h->ConsumerPc = mutation == HandoffMutation.CompleteBeforeReading ? 2 : 0;

                if (mutation != HandoffMutation.CompleteBeforeReading)
                {
                    h->ConsumerSeq = -1;
                }

                return;
            }
        }
    }

    /// <summary>The tick driver: it publishes one more tick as durable, and it may always choose not to.</summary>
    private static bool DriverEnabled(Harness* h) => h->CommittedTick < ((SessionSendState*)h->Send)->ReadySequence;

    private static void DriverStep(Harness* h) => h->CommittedTick++;

    private static void CheckCounters(Harness* h)
    {
        var state = (SessionSendState*)h->Send;
        var next = state->NextSequence;
        var ready = state->ReadySequence;
        var sent = state->SentSequence;

        Require(sent <= ready && ready <= next && next - sent <= SessionSendState.K,
            $"SUB-04 violated: the counters left their order — sent {sent}, ready {ready}, next {next}, K {SessionSendState.K}.");
        Require(next - ready is 0 or 1, $"SUB-04 violated: {next - ready} frames are under construction at once; at most one can be.");
    }

    private static long Bucket(long value) => value <= -2 ? 0 : value >= 6 ? 7 : value + 1;

    /// <summary>
    /// The canonical form of a state, so the explorer terminates. Everything is expressed relative to <c>SentSequence</c>, which is what makes the protocol's
    /// unbounded counters into a finite state space — the slots cycle with period <see cref="SessionSendState.K"/> and nothing else grows.
    /// </summary>
    private static long StateKey(Harness* h)
    {
        var state = (SessionSendState*)h->Send;
        var sent = state->SentSequence;
        var current = sent;
        var other = sent + 1;

        long key = 0;
        key = (key << 3) | (uint)h->ProducerPc;
        key = (key << 3) | (uint)h->ConsumerPc;
        key = (key << 3) | Bucket(state->NextSequence - sent);
        key = (key << 3) | Bucket(state->ReadySequence - sent);
        key = (key << 3) | Bucket(h->CommittedTick - sent);
        key = (key << 3) | (uint)(sent & 1);
        key = (key << 3) | (uint)WritingOf(h, current);
        key = (key << 3) | (uint)WritingOf(h, other);
        key = (key << 3) | (uint)ReadingOf(h, current);
        key = (key << 3) | (uint)ReadingOf(h, other);
        key = (key << 3) | Bucket(StampOf(h, current) - sent);
        key = (key << 3) | Bucket(StampOf(h, other) - sent);
        key = (key << 3) | Bucket(h->ProducerSeq - sent);
        key = (key << 3) | Bucket(h->ConsumerSeq - sent);
        key = (key << 3) | Bucket(h->ReadLow - sent);
        return key;
    }

    private static void Explore(Harness* live, Harness* stack, int depth, HashSet<long> visited, HandoffMutation mutation, ref Coverage coverage)
    {
        if (depth >= MaxDepth)
        {
            coverage.DepthCapHits++;
            return;
        }

        if (!visited.Add(StateKey(live)))
        {
            return;
        }

        coverage.States++;
        CheckCounters(live);

        for (var actor = 0; actor < 3; actor++)
        {
            if (actor == 2 && !DriverEnabled(live))
            {
                continue;
            }

            stack[depth] = *live;
            coverage.Transitions++;

            if (actor == 0)
            {
                ProducerStep(live, mutation, ref coverage);
            }
            else if (actor == 1)
            {
                ConsumerStep(live, mutation, ref coverage);
            }
            else
            {
                DriverStep(live);
            }

            Explore(live, stack, depth + 1, visited, mutation, ref coverage);
            *live = stack[depth];
        }
    }

    /// <summary>
    /// Runs the whole reachable state space for one mutation. Returns what it covered.
    /// </summary>
    /// <remarks>
    /// On its own thread with a 32 MB stack: the exploration recurses once per transition on the current path, and the default 1 MB would bound the search by
    /// the stack rather than by the protocol — a truncation that would look exactly like a clean pass.
    /// </remarks>
    private static Coverage RunHarness(HandoffMutation mutation)
    {
        var coverage = default(Coverage);
        Exception failure = null;

        var worker = new Thread(() =>
        {
            var live = (Harness*)NativeMemory.AlignedAlloc((nuint)sizeof(Harness), 64);
            var stack = (Harness*)NativeMemory.AlignedAlloc((nuint)(sizeof(Harness) * MaxDepth), 64);
            try
            {
                NativeMemory.Clear(live, (nuint)sizeof(Harness));
                SessionSendState.Initialize((SessionSendState*)live->Send);
                live->ProducerSeq = -1;
                live->ConsumerSeq = -1;
                live->ReadLow = -1;
                live->Stamp0 = -1;
                live->Stamp1 = -1;

                HashSet<long> visited = [];
                Explore(live, stack, 0, visited, mutation, ref coverage);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                NativeMemory.AlignedFree(live);
                NativeMemory.AlignedFree(stack);
            }
        }, 32 * 1024 * 1024);

        worker.Start();
        worker.Join();

        if (failure != null)
        {
            // Rethrown on the test's own thread so NUnit sees the assertion it raised, not a thread that died quietly.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return coverage;
    }

    #endregion

    /// <summary>
    /// SUB-04, outbound half. Every reachable interleaving of the producer, the send loop and the tick driver is explored, and in each one: no byte is read
    /// before the release that publishes it, no slot is reused before the acquire that frees it, and no frame is sent for a tick the driver has not committed.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-04")]
    public void FrameBytesAreVisibleBeforeTheReadySeqRelease()
    {
        var coverage = RunHarness(HandoffMutation.None);

        Assert.Multiple(() =>
        {
            Assert.That(coverage.DepthCapHits, Is.Zero, "the exploration was truncated by its depth bound, so it proves less than it reports");
            Assert.That(coverage.States, Is.GreaterThan(50), "a state space this small would mean the harness is not moving");

            // Positive evidence that the interesting paths were actually walked, rather than a fast green from a harness that never got going.
            Assert.That(coverage.FramesPublished, Is.GreaterThan(0), "no frame was ever published");
            Assert.That(coverage.FramesCompleted, Is.GreaterThan(0), "no frame was ever completed");
            Assert.That(coverage.InFlightSkips, Is.GreaterThan(0), "K = 2 was never reached, so skip-not-queue was never exercised");
            Assert.That(coverage.TickGateRefusals, Is.GreaterThan(0), "the committed-tick gate never refused anything, so it was never exercised");
            Assert.That(coverage.NothingReadyRefusals, Is.GreaterThan(0), "the send loop never found an empty state");
        });

        TestContext.Out.WriteLine(
            $"SUB-04 outbound: {coverage.States} states, {coverage.Transitions} transitions, {coverage.FramesPublished} publishes, "
            + $"{coverage.FramesCompleted} completions, {coverage.InFlightSkips} in-flight skips, {coverage.TickGateRefusals} tick-gate refusals.");
    }

    /// <summary>
    /// The genuineness proof for the verifier above: each of SUB-04's three clauses is broken in turn, and the verifier's own assertion must reject it.
    /// </summary>
    /// <remarks>
    /// The mutations are of the <b>protocol</b>, not of <see cref="SessionSendState"/>: the harness reorders the steps a producer or a send loop takes around
    /// the same unchanged calls. That is the right place for them — SUB-04 constrains the order in which callers use those calls, and a mutation inside the
    /// type would prove only that its own <c>Debug.Assert</c>s fire.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-04")]
    public void EachClauseOfTheHandoffIsProvenFalsifiable()
    {
        RuleMutants.AssertDetects("SUB-04", ReadBeforeRelease, () => RunHarness(HandoffMutation.PublishBeforeWriting));
        RuleMutants.AssertDetects("SUB-04", ReuseBeforeAcquire, () => RunHarness(HandoffMutation.CompleteBeforeReading));
        RuleMutants.AssertDetects("SUB-04", UncommittedSend, () => RunHarness(HandoffMutation.IgnoreTheCommittedTick));
    }

    #region Unit behaviour

    private static SessionSendState* NewState() => (SessionSendState*)NativeMemory.AlignedAlloc((nuint)sizeof(SessionSendState), 64);

    [Test]
    public void TheStateIsThreeCacheLinesAndTheTwoWritersAreOnDifferentOnes()
    {
        Assert.That(sizeof(SessionSendState), Is.EqualTo(192), "three cache lines: the producer's, the send side's, and the slots'");

        var state = NewState();
        var bytes = (byte*)state;
        try
        {
            SessionSendState.Initialize(state);

            // A producer-only operation must dirty only the first line.
            Assert.That(state->TryBeginFrame(out var sequence, out _), Is.True);
            var block = stackalloc byte[64];
            state->PublishFrame(sequence, new FrameBlock(block, 64), 8, 1);

            Assert.Multiple(() =>
            {
                Assert.That(NonZero(bytes, 0, 64), Is.True, "the producer writes its own line");
                Assert.That(NonZero(bytes, SessionSendState.SendLineOffset, 64), Is.False,
                    "the producer must not touch the send side's line — that is the false sharing this layout exists to prevent");
                Assert.That(NonZero(bytes, SessionSendState.SlotsOffset, 64), Is.True, "the slot it published is on the third line");
            });

            // A send-side-only operation must dirty only the second line.
            var lineBefore = stackalloc byte[64];
            Buffer.MemoryCopy(bytes, lineBefore, 64, 64);

            state->ReportAppliedTick(7);
            Assert.That(state->TryClaimFrame(1, out var frame), Is.True);
            state->CompleteSend(frame.Sequence);

            Assert.Multiple(() =>
            {
                Assert.That(Same(bytes, lineBefore, 64), Is.True, "the send side must not touch the producer's line");
                Assert.That(state->SentSequence, Is.EqualTo(1));
                Assert.That(state->AckedTick, Is.EqualTo(7));
            });
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    private static bool NonZero(byte* p, int offset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (p[offset + i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(byte* a, byte* b, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// K = 2 means skip, not queue: a session with two frames outstanding is not encoded at all this tick, and nothing moves until the send side completes one.
    /// </summary>
    [Test]
    public void ASessionWithKFramesOutstandingIsSkippedRatherThanQueued()
    {
        var state = NewState();
        var block = stackalloc byte[64];
        try
        {
            SessionSendState.Initialize(state);

            for (var i = 0; i < SessionSendState.K; i++)
            {
                Assert.That(state->TryBeginFrame(out var sequence, out _), Is.True);
                state->PublishFrame(sequence, new FrameBlock(block, 64), 8, sequence + 1);
            }

            Assert.That(state->FramesInFlight, Is.EqualTo(SessionSendState.K));

            for (var i = 0; i < 20; i++)
            {
                Assert.That(state->TryBeginFrame(out var skipped, out var recycled), Is.False, "a third frame would queue, and this protocol does not queue");
                Assert.Multiple(() =>
                {
                    Assert.That(skipped, Is.EqualTo(-1));
                    Assert.That(recycled.IsValid, Is.False, "a skipped session must not be handed a block it never asked for");
                });
            }

            Assert.That(state->SkipRun, Is.EqualTo(20), "the run is what the degrade and close thresholds are counted against");

            // One completion makes exactly one slot available again — not two.
            Assert.That(state->TryClaimFrame(long.MaxValue, out var frame), Is.True);
            state->CompleteSend(frame.Sequence);

            Assert.That(state->TryBeginFrame(out var next, out _), Is.True);
            state->PublishFrame(next, new FrameBlock(block, 64), 8, next + 1);
            Assert.That(state->TryBeginFrame(out _, out _), Is.False, "the second slot is still in flight");
            Assert.That(state->SkipRun, Is.EqualTo(1), "a published frame resets the run");
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    [Test]
    public void NothingIsSentForATickTheDriverHasNotCommitted()
    {
        var state = NewState();
        var gate = new FramePublicationGate();
        var block = stackalloc byte[64];
        try
        {
            SessionSendState.Initialize(state);

            Assert.That(state->TryBeginFrame(out var sequence, out _), Is.True);
            state->PublishFrame(sequence, new FrameBlock(block, 64), 8, tick: 12);

            Assert.Multiple(() =>
            {
                Assert.That(gate.CommittedTick, Is.Zero);
                Assert.That(state->TryClaimFrame(gate.CommittedTick, out _), Is.False, "a frame for tick 12 is not sendable before tick 12 has flushed");
            });

            gate.Publish(11);
            Assert.That(state->TryClaimFrame(gate.CommittedTick, out _), Is.False, "one tick short is still short");

            gate.Publish(12);
            Assert.That(state->TryClaimFrame(gate.CommittedTick, out var frame), Is.True);
            Assert.That(frame.Tick, Is.EqualTo(12));
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    /// <summary>
    /// The reason the slots are indexed by sequence and not by tick parity: with a skipped tick, <c>buf[tick &amp; 1]</c> aliases a frame that has not been
    /// sent. The sequence only advances when a frame is actually produced, so it cannot.
    /// </summary>
    [Test]
    public void SlotsFollowTheSequenceNotTheTickParity()
    {
        var state = NewState();
        var block = stackalloc byte[128];
        try
        {
            SessionSendState.Initialize(state);

            // Ticks 10 and 12 produce; tick 11 is silent. Under tick parity both would land in slot 0, one on top of the other.
            Assert.That(state->TryBeginFrame(out var first, out _), Is.True);
            state->PublishFrame(first, new FrameBlock(block, 64), 8, tick: 10);

            Assert.That(state->TryBeginFrame(out var second, out _), Is.True);
            state->PublishFrame(second, new FrameBlock(block + 64, 64), 8, tick: 12);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(0));
                Assert.That(second, Is.EqualTo(1), "two produced frames are two sequences, whatever the ticks they describe");
                Assert.That((nint)state->BlockInSlot(0).Bytes, Is.EqualTo((nint)block));
                Assert.That((nint)state->BlockInSlot(1).Bytes, Is.EqualTo((nint)(block + 64)), "the second frame did not land on the first");
            });
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    [Test]
    public void AnAbandonedFrameGivesItsSequenceBack()
    {
        var state = NewState();
        var block = stackalloc byte[64];
        try
        {
            SessionSendState.Initialize(state);

            Assert.That(state->TryBeginFrame(out var sequence, out _), Is.True);
            state->AbandonFrame(sequence);

            Assert.Multiple(() =>
            {
                Assert.That(state->NextSequence, Is.Zero, "an encode that produced nothing consumes no sequence");
                Assert.That(state->ReadySequence, Is.Zero);
                Assert.That(state->FramesInFlight, Is.Zero);
                Assert.That(state->SkipRun, Is.EqualTo(1), "it is a skip like any other, and the policy counts it as one");
            });

            Assert.That(state->TryBeginFrame(out var again, out _), Is.True);
            Assert.That(again, Is.EqualTo(sequence), "the same sequence is claimed next time");
            state->PublishFrame(again, new FrameBlock(block, 64), 8, 1);
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    /// <summary>
    /// A tick the session had nothing to say on gives its sequence back and does <b>not</b> advance the skip run.
    /// </summary>
    /// <remarks>
    /// The two look identical from the producer's side — a claimed sequence, no frame — and treating them the same closed every healthy session in a world
    /// that went quiet. <c>SkipRun</c> is read only by <c>SkipPolicy.Evaluate</c>, which degrades at twenty and closes with 1013 at fifty; 1013 means "you
    /// cannot keep up", and a client that was served everything there was to serve is the opposite of that. Fifty quiet ticks is half a second at 100 Hz.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-15")]
    public void AnIdleTickCostsASequenceButNotASkip()
    {
        var state = NewState();
        var block = stackalloc byte[64];
        try
        {
            SessionSendState.Initialize(state);

            for (var i = 0; i < 60; i++)
            {
                Assert.That(state->TryBeginFrame(out var sequence, out _), Is.True, "an idle tick is not back-pressure, so the slot is always claimable");
                state->AbandonIdleFrame(sequence);
            }

            Assert.Multiple(() =>
            {
                Assert.That(state->SkipRun, Is.Zero, "sixty quiet ticks past the close threshold, and the session owes the policy nothing");
                Assert.That(state->NextSequence, Is.Zero, "an idle tick consumes no sequence either");
            });

            // And a real skip still counts, from wherever the run stood: the two paths are separate, not one path silenced.
            Assert.That(state->TryBeginFrame(out var claimed, out _), Is.True);
            state->AbandonFrame(claimed);
            Assert.That(state->SkipRun, Is.EqualTo(1), "back-pressure still advances the run");

            Assert.That(state->TryBeginFrame(out var last, out _), Is.True);
            state->PublishFrame(last, new FrameBlock(block, 64), 8, 1);
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    /// <summary>
    /// A session heard from on tick zero carries a mark, because the stamp is the tick plus one.
    /// </summary>
    /// <remarks>
    /// Zero has to mean "this slot was never bound", and tick zero is a real tick — every session on a server in its first tick, and every session in a
    /// fixture that connects straight after <c>Start</c>. Storing the tick itself made those two states identical, and the silence sweep's <c>&gt; 0</c>
    /// guard then exempted such a session from the 4001 policy for the rest of its life: it stopped pinging and was served forever.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-15")]
    public void ASessionHeardFromOnTickZeroIsDistinguishableFromOneNeverBound()
    {
        var state = NewState();
        try
        {
            SessionSendState.Initialize(state);
            Assert.That(state->PingStamp, Is.Zero, "nothing has been heard from this slot yet");

            state->NotePing(0);
            Assert.That(state->PingStamp, Is.EqualTo(1), "tick zero is a mark, and the stamp is the tick plus one");

            state->NotePing(41);
            Assert.That(state->PingStamp, Is.EqualTo(42));

            state->NotePing(7);
            Assert.That(state->PingStamp, Is.EqualTo(42), "the mark never moves backwards");
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    [Test]
    public void TheAcknowledgedTickNeverMovesBackwards()
    {
        var state = NewState();
        try
        {
            SessionSendState.Initialize(state);

            state->ReportAppliedTick(40);
            state->ReportAppliedTick(12);
            Assert.That(state->AckedTick, Is.EqualTo(40), "a PING that overtakes an older one must not make the lag skip fire on a healthy client");

            state->ReportAppliedTick(41);
            Assert.That(state->AckedTick, Is.EqualTo(41));
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    [Test]
    public void ThePublicationGateIsMonotonicAndStartsClosed()
    {
        var gate = new FramePublicationGate();
        Assert.That(gate.CommittedTick, Is.Zero, "nothing is sendable before the first flush");

        gate.Publish(5);
        gate.Publish(9);
        Assert.That(gate.CommittedTick, Is.EqualTo(9));
    }

    /// <summary>
    /// SUB-07's shape for this slice: a steady-state hand-off is pointer arithmetic and four counters, so it must allocate nothing at all. The block is kept in
    /// place by <see cref="FramePool.TryRentOrKeep"/>, which is what keeps the pool's lock off this path too.
    /// </summary>
    [Test]
    public void ASteadyStateHandoffAllocatesNoManagedMemory()
    {
        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "FrameHandoffAllocTests" });
        using var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "HandoffAllocator" });
        using var pool = new FramePool("Frames", registry.Runtime, allocator, new SubscriptionsOptions { FramePoolBudgetBytes = 4L * 1024 * 1024 });

        var state = NewState();
        var gate = new FramePublicationGate();
        try
        {
            SessionSendState.Initialize(state);

            // Warm up: JIT every path, and let BOTH slots take their block — the second rent is an allocator call, and it must not land inside the window.
            RunHandoffs(state, pool, gate, 8, 8);

            var before = GC.GetAllocatedBytesForCurrentThread();
            RunHandoffs(state, pool, gate, 1000, 8);
            var delta = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Multiple(() =>
            {
                Assert.That(delta, Is.Zero, "the hand-off path must not allocate; it runs once per session per tick");
                Assert.That(pool.RentCount, Is.EqualTo(SessionSendState.K), "two rents for the whole run — one block per slot, kept in place thereafter");
            });
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    private static void RunHandoffs(SessionSendState* state, FramePool pool, FramePublicationGate gate, int count, int frameBytes)
    {
        for (var i = 0; i < count; i++)
        {
            if (!state->TryBeginFrame(out var sequence, out var block))
            {
                continue;
            }

            if (!pool.TryRentOrKeep(frameBytes, ref block, out var previous))
            {
                // The slot's block is the caller's from the moment TryBeginFrame handed it over, so an abandoned encode owes it back.
                if (block.IsValid)
                {
                    pool.Return(block);
                }

                state->AbandonFrame(sequence);
                continue;
            }

            if (previous.IsValid)
            {
                pool.Return(previous);
            }

            *(long*)block.Bytes = sequence;
            state->PublishFrame(sequence, block, frameBytes, sequence + 1);
            gate.Publish(sequence + 1);

            if (state->TryClaimFrame(gate.CommittedTick, out var frame))
            {
                state->CompleteSend(frame.Sequence);
            }
        }
    }

    #endregion

    /// <summary>
    /// The complement to the model check: the same code under two real threads, a million hand-offs and randomized delays, asserting no torn frame and no slot
    /// reused while the send side still held it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the in-flight flag proves, and why it cannot false-positive.</b> The send side raises a flag for the slot it is reading and lowers it before
    /// <c>CompleteSend</c>'s release; the producer checks it immediately after <c>TryBeginFrame</c>'s acquire. If the protocol holds, the producer only ever
    /// reaches a slot whose completion has been released, and the lowering of the flag precedes that release — so a raised flag seen by the producer is proof
    /// of a violation, not of a race in the probe.
    /// </para>
    /// <para>
    /// <c>Sensitive</c> because it spins two threads for seconds: run beside eight parallel shards it starves and the run gets slower, not wrong.
    /// </para>
    /// </remarks>
    [Test]
    [Category("Sensitive")]
    [NonParallelizable]
    public void AMillionHandoffsUnderRandomizedDelayNeverTearAFrameOrReuseALiveSlot()
    {
        const int Handoffs = 1_000_000;
        const int FrameLength = 256;
        const int Words = FrameLength / sizeof(long);

        using var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "FrameHandoffStressTests" });
        using var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "StressAllocator" });
        using var pool = new FramePool("Frames", registry.Runtime, allocator, new SubscriptionsOptions { FramePoolBudgetBytes = 4L * 1024 * 1024 });

        var state = NewState();

        // Padded, one line each: the flag is written by the send side and read by the producer every frame, which is exactly the traffic false sharing taxes.
        var reading = (int*)NativeMemory.AlignedAlloc((nuint)(64 * SessionSendState.K), 64);

        var gate = new FramePublicationGate();
        var watchdog = Stopwatch.StartNew();
        string producerFailure = null;
        string consumerFailure = null;
        var abort = 0;

        try
        {
            SessionSendState.Initialize(state);
            NativeMemory.Clear(reading, (nuint)(64 * SessionSendState.K));

            var statePtr = (nint)state;
            var readingPtr = (nint)reading;

            var producer = new Thread(() =>
            {
                var st = (SessionSendState*)statePtr;
                var live = (int*)readingPtr;
                var rng = new Random(1731);
                try
                {
                    for (long i = 0; i < Handoffs; i++)
                    {
                        FrameBlock block;
                        long sequence;
                        while (!st->TryBeginFrame(out sequence, out block))
                        {
                            if (Stalled(watchdog, ref abort))
                            {
                                producerFailure ??= $"producer stalled at frame {i}";
                                return;
                            }

                            Thread.SpinWait(4);
                        }

                        if (Volatile.Read(ref live[(sequence & 1) * 16]) != 0)
                        {
                            producerFailure ??= $"{ReuseBeforeAcquire} — slot {sequence & 1} was handed back for sequence {sequence} while the send side "
                                + "was still reading the frame it held.";
                            Volatile.Write(ref abort, 1);
                            return;
                        }

                        if (!pool.TryRentOrKeep(FrameLength, ref block, out var previous))
                        {
                            producerFailure ??= $"the pool refused a {FrameLength} B block at frame {i}";
                            Volatile.Write(ref abort, 1);
                            return;
                        }

                        if (previous.IsValid)
                        {
                            pool.Return(previous);
                        }

                        var words = (long*)block.Bytes;
                        for (var w = 0; w < Words; w++)
                        {
                            words[w] = sequence;
                        }

                        st->PublishFrame(sequence, block, FrameLength, sequence + 1);

                        // The tick driver's release, after a delay the send side has to tolerate: it is what makes the gate bind on a real schedule rather
                        // than only in the model.
                        if ((i & 7) == 0)
                        {
                            Thread.SpinWait(rng.Next(0, 48));
                        }

                        gate.Publish(sequence + 1);
                    }
                }
                catch (Exception ex)
                {
                    producerFailure ??= ex.ToString();
                    Volatile.Write(ref abort, 1);
                }
            });

            var consumer = new Thread(() =>
            {
                var st = (SessionSendState*)statePtr;
                var live = (int*)readingPtr;
                var rng = new Random(9277);
                try
                {
                    for (long i = 0; i < Handoffs; i++)
                    {
                        FrameView frame;
                        while (!st->TryClaimFrame(gate.CommittedTick, out frame))
                        {
                            if (Stalled(watchdog, ref abort))
                            {
                                consumerFailure ??= $"send side stalled at frame {i}";
                                return;
                            }

                            Thread.SpinWait(4);
                        }

                        if (frame.Tick > gate.CommittedTick)
                        {
                            consumerFailure ??= $"{UncommittedSend} — frame {frame.Sequence} carries tick {frame.Tick}, committed is {gate.CommittedTick}.";
                            Volatile.Write(ref abort, 1);
                            return;
                        }

                        var slot = (int)(frame.Sequence & 1) * 16;
                        Volatile.Write(ref live[slot], 1);

                        if ((i & 3) == 0)
                        {
                            Thread.SpinWait(rng.Next(0, 64));
                        }

                        var words = (long*)frame.Bytes;
                        for (var w = 0; w < Words; w++)
                        {
                            if (words[w] != frame.Sequence)
                            {
                                consumerFailure ??= $"{TornFrame} — word {w} of sequence {frame.Sequence} reads {words[w]}.";
                                Volatile.Write(ref abort, 1);
                                Volatile.Write(ref live[slot], 0);
                                return;
                            }
                        }

                        Volatile.Write(ref live[slot], 0);
                        st->CompleteSend(frame.Sequence);
                    }
                }
                catch (Exception ex)
                {
                    consumerFailure ??= ex.ToString();
                    Volatile.Write(ref abort, 1);
                }
            });

            producer.Start();
            consumer.Start();
            producer.Join();
            consumer.Join();

            Assert.Multiple(() =>
            {
                Assert.That(producerFailure, Is.Null);
                Assert.That(consumerFailure, Is.Null);
                Assert.That(state->SentSequence, Is.EqualTo(Handoffs), "every frame produced was sent exactly once, in order");
                Assert.That(state->NextSequence, Is.EqualTo(Handoffs));
                Assert.That(state->IsDrained, Is.True);
                Assert.That(pool.RentCount, Is.EqualTo(SessionSendState.K), "two blocks served a million frames");
            });

            TestContext.Out.WriteLine($"{Handoffs:N0} hand-offs in {watchdog.ElapsedMilliseconds} ms.");
        }
        finally
        {
            NativeMemory.AlignedFree(state);
            NativeMemory.AlignedFree(reading);
        }
    }

    private static bool Stalled(Stopwatch watchdog, ref int abort)
    {
        if (Volatile.Read(ref abort) != 0)
        {
            return true;
        }

        if (watchdog.ElapsedMilliseconds <= 60_000)
        {
            return false;
        }

        Volatile.Write(ref abort, 1);
        return true;
    }
}
