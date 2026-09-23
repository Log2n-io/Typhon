using System;
using System.Runtime.CompilerServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// The per-archetype constants the motion rule is evaluated against, derived once per block from the compiled position and the tick period in force.
/// </summary>
/// <remarks>
/// <para>
/// Every member here is a number the rule would otherwise recompute for each of a cluster's 64 slots: a square root avoided by comparing squared distances,
/// a division turned into a threshold, the four byte offsets a segment is read and written at. None of it depends on the entity, so none of it belongs in the
/// per-entity path.
/// </para>
/// <para>
/// <b>The tick period is the current one, not the nominal one</b> ([02 § 4](../../../claude/design/Subscriptions/02-execution.md)). The teleport threshold is
/// a speed times a period, so a dilated tick covers more ground and the threshold rises with it — time dilation can only make the rule more lenient, never
/// turn ordinary movement into a teleport.
/// </para>
/// </remarks>
internal readonly struct MotionPolicy
{
    /// <summary>The compiled position, or <see langword="null"/> when the archetype does not move.</summary>
    public CompiledPosition Position { get; }

    /// <summary>Whether this policy describes a moving archetype at all; a default-valued policy does not.</summary>
    public bool Enabled => Position != null;

    /// <summary>Axes on the wire: 2 or 3.</summary>
    public int Dims { get; }

    /// <summary>Bytes of one quantized position axis — the <c>pos</c> codec's width.</summary>
    public int PosBytes { get; }

    /// <summary>Bytes of one velocity axis, or <c>0</c> for the <c>none</c> model, which carries no velocity.</summary>
    public int VelBytes { get; }

    /// <summary>The velocity codec's width in bits, or <c>0</c> for the <c>none</c> model.</summary>
    public int VelBits { get; }

    /// <summary>The velocity codec's divisor: one velocity code is a position step over this (W5).</summary>
    public int QuantaDiv { get; }

    /// <summary>Whether a segment carries a velocity to extrapolate from.</summary>
    public bool Linear { get; }

    /// <summary>Whether <c>VelocityFrom</c> named a column the velocity is read from instead of measured.</summary>
    public bool VelocityDeclared { get; }

    /// <summary>The per-axis position quantum, in metres.</summary>
    public double[] Step { get; }

    /// <summary>The extrapolation error budget, squared — compared against a squared distance so no square root is taken per entity.</summary>
    public double ToleranceSquared { get; }

    /// <summary>The teleport threshold as a distance per tick, squared: <c>(Teleport x period)²</c>.</summary>
    public double TeleportSquaredPerTick { get; }

    /// <summary>How many ticks a <b>moving</b> segment may live before it is refreshed. A stationary one never is.</summary>
    public uint MaxAgeTicks { get; }

    /// <summary>Byte offset of the pre-encoded segment inside one hot entry.</summary>
    public int SegmentOffset { get; }

    /// <summary>Byte offset of the velocity <b>within the segment</b>: straight after <c>p0</c>.</summary>
    public int SegmentVelocityOffset { get; }

    /// <summary>Byte offset of <c>t0</c> <b>within the segment</b>: after <c>p0</c> and the velocity.</summary>
    public int SegmentTickOffset { get; }

    /// <summary>Byte offset of the epoch byte <b>within the segment</b>, which is its last.</summary>
    public int SegmentEpochOffset { get; }

    /// <summary>Byte offset of the previous quantized position inside one cold entry.</summary>
    public int PrevPositionOffset { get; }

    /// <summary>Byte offset of the run start <c>(p_s, t_s)</c> inside one cold entry.</summary>
    public int RunStartOffset { get; }

    /// <summary>Byte offset of <c>t_s</c> inside the run start: after <c>p_s</c>.</summary>
    public int RunStartTickOffset { get; }

    /// <summary>The tick period the teleport threshold and a declared velocity are scaled by, in seconds.</summary>
    public double TickPeriodSeconds { get; }

    /// <summary>The declared velocity column's stride, when <c>VelocityFrom</c> named one.</summary>
    public int VelocityComponentSize { get; }

    /// <summary>Byte offset of the declared velocity value inside its component.</summary>
    public int VelocityFieldOffset { get; }

    private MotionPolicy(CompiledPosition position, in ReplicationBlockLayout layout, double tickPeriodSeconds)
    {
        Position = position;
        Dims = position.Dims;
        PosBytes = position.Pos.Bits / 8;
        Linear = position.Linear && position.Vel != null;
        VelBits = Linear ? position.Vel.Bits : 0;
        VelBytes = Linear ? position.Vel.Bits / 8 : 0;
        QuantaDiv = Linear ? position.Vel.QuantaDiv : 1;
        VelocityDeclared = position.VelocityIsDeclared;
        VelocityComponentSize = position.VelocityComponentSize;
        VelocityFieldOffset = position.VelocityFieldOffsetInComponent;
        Step = position.PositionStep;
        TickPeriodSeconds = tickPeriodSeconds;

        var tolerance = position.ToleranceMetres;
        ToleranceSquared = tolerance * tolerance;

        var teleport = position.TeleportMaxSpeedMps * tickPeriodSeconds;
        TeleportSquaredPerTick = teleport * teleport;

        // Rounded rather than truncated, and floored at one tick: a MaxAge shorter than a tick would otherwise round to zero and turn the heartbeat into a
        // segment per tick for every mover — the failure mode the rejected trigger was rejected for.
        var ageTicks = Math.Round(position.MaxAgeSeconds / tickPeriodSeconds, MidpointRounding.AwayFromZero);
        MaxAgeTicks = ageTicks < 1 ? 1 : ageTicks > uint.MaxValue ? uint.MaxValue : (uint)ageTicks;

        // The three below are offsets WITHIN the segment, not within the hot entry: every use of them is against a pointer that already carries
        // SegmentOffset, and folding it in twice put the velocity, t0 and epoch of every entity 32 bytes past their own entry.
        SegmentOffset = layout.SegmentOffsetInHotEntry;
        SegmentVelocityOffset = Dims * PosBytes;
        SegmentTickOffset = SegmentVelocityOffset + (Dims * VelBytes);
        SegmentEpochOffset = SegmentTickOffset + 2;

        PrevPositionOffset = layout.PrevPositionOffsetInColdEntry;
        RunStartOffset = layout.RunStartOffsetInColdEntry;
        RunStartTickOffset = RunStartOffset + (Dims * PosBytes);
    }

    /// <summary>
    /// Derives the policy for one archetype, or a disabled one when it does not move.
    /// </summary>
    /// <param name="position">The compiled position, which may be <see langword="null"/>.</param>
    /// <param name="layout">The archetype's block layout, which the segment and run-start offsets come from.</param>
    /// <param name="tickPeriodSeconds">
    /// The tick period in force, in seconds; a non-positive value takes <see cref="MotionTracker.DefaultTickPeriodSeconds"/>.
    /// </param>
    /// <returns>The policy.</returns>
    public static MotionPolicy For(CompiledPosition position, in ReplicationBlockLayout layout, double tickPeriodSeconds)
    {
        if (position == null || !position.Moving || layout.SegmentBytes <= 0)
        {
            return default;
        }

        var period = double.IsFinite(tickPeriodSeconds) && tickPeriodSeconds > 0 ? tickPeriodSeconds : MotionTracker.DefaultTickPeriodSeconds;
        return new MotionPolicy(position, in layout, period);
    }
}

/// <summary>
/// P1-10 — the motion rule of [02 § 4](../../../claude/design/Subscriptions/02-execution.md): when an entity's position stops travelling as a position and
/// starts travelling as a <b>segment</b> a client extrapolates, and what that segment's velocity is fitted from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three triggers, and a fourth thing that is not one.</b> A new segment is emitted when the client's own extrapolation would be wrong by more than the
/// declared tolerance (<c>|p0 + v·(T − t0) − p_T| &gt; Tolerance</c>), when the step is a teleport (<c>|Δp| &gt; Teleport × period</c>, and then the epoch
/// advances so no client interpolates across it), and when a <b>moving</b> segment is older than <c>MaxAge</c>. A stationary entity cannot drift, so it gets
/// no heartbeat at all — its segment stands until it moves. The fourth thing is the <b>run restart</b>, which changes what the next refit measures and emits
/// nothing by itself.
/// </para>
/// <para>
/// <b>Why not "the quantized velocity changed".</b> float32 positions round at about a millimetre near ±8 km, so a single-tick velocity jitters by ±10 mm/s
/// on a dead-straight path; that trigger would emit a segment per tick for most movers, up to eight times the bandwidth plan. It is measured here rather than
/// merely argued: <see cref="ArchetypeReplicationState.ShadowSegmentsEmitted"/> counts what it <i>would</i> have emitted, beside what this rule actually did,
/// on the same run and from the same binary (AC-21).
/// </para>
/// <para>
/// <b>The run-start window.</b> A new segment's velocity is refit as <c>v = (p_T − p_s) / (T − t_s)</c> from where the current straight run began, so the
/// measurement noise falls with the run's length instead of being a single tick's rounding. The run restarts on a teleport, on an error-triggered refit, and
/// when the current step departs from the run's mean velocity by more than <c>max(2 mm/tick, 10 %)</c> — the test that catches a gradual arc a tick or two
/// before the tolerance does, while float32 jitter stays under the floor. It deliberately does <b>not</b> restart on a heartbeat, which is the case that wants
/// the longest window it can get.
/// </para>
/// <para>
/// <b>No simulation knowledge.</b> A dead entity gives <c>v = 0</c>, a world-edge clamp zeroes that axis, an overshoot stays linear and a respawn is a
/// teleport. <c>VelocityFrom(field)</c> is the exact alternative for a simulation that already keeps a velocity and does not move its entities every tick.
/// </para>
/// <para>
/// <b>State, and its bytes.</b> Everything is in the block: the segment <c>p0 | v | t0 | epoch</c> in the hot entry's pre-encoded region, the segment's
/// absolute start tick in <c>GroupTicks[0]</c> (the wire's <c>t0</c> is its low 16 bits, so the rule's arithmetic never meets the 2¹⁶ wrap), the previous
/// quantized position and the run start <c>(p_s, t_s)</c> in the cold entry. Nothing is allocated, here or by anything this calls.
/// </para>
/// </remarks>
internal static unsafe class MotionTracker
{
    /// <summary>
    /// The tick period used when none has been supplied: <c>1 / 60</c>, matching <c>RuntimeOptions.BaseTickRate</c>'s own default.
    /// </summary>
    /// <remarks>
    /// A fallback, not a policy. <see cref="ArchetypeReplicationState.TickPeriodSeconds"/> is what production is meant to carry, assigned from
    /// <c>SubscriptionsRuntime.NominalTickPeriodSeconds</c>; this value is what the rule runs on until it is.
    /// </remarks>
    public const double DefaultTickPeriodSeconds = 1.0 / 60.0;

    /// <summary>
    /// The floor of the run-departure test, in metres per tick: below 2 mm a step is float32 rounding rather than a change of direction.
    /// </summary>
    public const double RunDepartureFloorMetresPerTick = 0.002;

    /// <summary>The proportional part of the run-departure test: a step 10 % off the run's mean velocity starts a new run.</summary>
    public const double RunDepartureFraction = 0.10;

    /// <summary>Doubles of scratch one call needs: four vectors of at most three axes.</summary>
    public const int ScratchDoubles = 12;

    /// <summary>
    /// Decides this tick's motion for one watched entity, and writes the segment when there is one.
    /// </summary>
    /// <param name="policy">The archetype's derived constants.</param>
    /// <param name="hot">The slot's hot entry: the segment's bytes and, in <c>GroupTicks[0]</c>, its absolute start tick.</param>
    /// <param name="coldBytes">The slot's cold entry.</param>
    /// <param name="current">This tick's quantized position, in the same byte form the segment and the previous position hold.</param>
    /// <param name="velocityColumn">
    /// The declared velocity column's base in the cluster this block describes, or <see langword="null"/> when the velocity is measured.
    /// </param>
    /// <param name="slot">The slot within the cluster, which indexes the declared velocity column.</param>
    /// <param name="initialize">Whether the entry is being (re-)initialized this tick, in which case the segment is the enter position.</param>
    /// <param name="tick">The tick being projected.</param>
    /// <param name="scratch">At least <see cref="ScratchDoubles"/> doubles, carved once per block by the caller.</param>
    /// <param name="segments">Accumulates the segments this rule emitted.</param>
    /// <param name="shadow">Accumulates what the rejected "the quantized velocity changed" trigger would have emitted (AC-21).</param>
    public static void Update(in MotionPolicy policy, ReplicationHotEntry* hot, byte* coldBytes, byte* current, byte* velocityColumn, int slot,
        bool initialize, uint tick, Span<double> scratch, ref int segments, ref int shadow)
    {
        if (!policy.Enabled || hot == null || coldBytes == null || current == null)
        {
            return;
        }

        var hotBytes = (byte*)hot;
        var segment = hotBytes + policy.SegmentOffset;
        var previous = coldBytes + policy.PrevPositionOffset;
        var runStart = coldBytes + policy.RunStartOffset;
        var runTick = coldBytes + policy.RunStartTickOffset;
        var dims = policy.Dims;
        var posBytes = policy.PosBytes;

        if (initialize)
        {
            // No history any session could hold, and none this rule could fit a velocity from: the entry enters with a stationary segment at where it is,
            // and the first measurement happens on its second projected tick. The epoch is whatever the entry already carried — zero on a fresh or reused
            // slot, because the entry was cleared, and unchanged for an entity that was merely not pushed for a tick.
            StartRun(policy, runStart, runTick, current, tick);
            EmitSegment(policy, segment, current, default, tick, segment[policy.SegmentEpochOffset]);
            hot->GroupTicks[0] = tick;
            segments++;
            shadow++;
            return;
        }

        var p = scratch;
        var q = scratch[3..];
        var step = scratch[6..];
        var fit = scratch[9..];

        for (var a = 0; a < dims; a++)
        {
            var axisStep = policy.Step[a];

            // The codec's `min` is dropped on purpose: every quantity below is a DIFFERENCE of positions, so the offset cancels, and dropping it keeps the
            // magnitudes at world scale rather than at the grid's absolute coordinates.
            p[a] = ReadCode(current + (a * posBytes), posBytes) * axisStep;
            q[a] = ReadCode(previous + (a * posBytes), posBytes) * axisStep;
            step[a] = p[a] - q[a];
        }

        // ── 1. Teleport ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // The threshold is the declared speed over the CURRENT tick period, so a dilated tick raises it rather than manufacturing teleports exactly when
        // the server is least able to afford them.
        if (LengthSquared(step, dims) > policy.TeleportSquaredPerTick)
        {
            var epoch = unchecked((byte)(segment[policy.SegmentEpochOffset] + 1));
            StartRun(policy, runStart, runTick, current, tick);
            EmitSegment(policy, segment, current, default, tick, epoch);
            hot->GroupTicks[0] = tick;
            segments++;
            shadow++;
            return;
        }

        // ── 2. The run-departure test, which restarts the run and emits nothing ─────────────────────────────────────────────────────────────────────
        //
        // Run before the error test, so a refit in the same tick fits from the fresh run rather than across the corner. It needs a mean the current step
        // is not itself the whole of, hence a run at least two ticks long.
        var started = ReadTick(runTick);
        if (tick - started >= 2)
        {
            var span = (double)(tick - started);
            var departure = 0d;
            var mean = 0d;
            for (var a = 0; a < dims; a++)
            {
                var runMean = (p[a] - (ReadCode(runStart + (a * posBytes), posBytes) * policy.Step[a])) / span;
                var off = step[a] - runMean;
                departure += off * off;
                mean += runMean * runMean;
            }

            var limit = RunDepartureFraction * Math.Sqrt(mean);
            if (limit < RunDepartureFloorMetresPerTick)
            {
                limit = RunDepartureFloorMetresPerTick;
            }

            if (departure > limit * limit)
            {
                // The new run begins at the step that departed, not at this tick: that step is the first of the new direction, and throwing it away would
                // leave the next refit with a zero-length window.
                StartRun(policy, runStart, runTick, previous, tick - 1);
            }
        }

        // ── 3. The error a client would be making, and the segment's age ────────────────────────────────────────────────────────────────────────────
        var t0 = hot->GroupTicks[0];
        var age = tick - t0;
        var elapsed = (double)age;
        var error = 0d;
        var segmentMoving = false;
        var shadowFires = false;
        for (var a = 0; a < dims; a++)
        {
            var axisStep = policy.Step[a];
            var velocity = 0d;
            if (policy.Linear)
            {
                var code = ReadVelocity(segment + policy.SegmentVelocityOffset + (a * policy.VelBytes), policy.VelBytes);
                velocity = WireMath.DecodeVel(code, axisStep, policy.QuantaDiv, policy.VelBits);
                segmentMoving |= code != 0;

                // The rejected trigger, evaluated beside the adopted one: it fires when the velocity the client holds is not the one this tick's step
                // quantizes to — which is what a "send a segment when the quantized velocity changes" engine would have had to emit for it to hold it.
                shadowFires |= WireMath.EncodeVel(step[a], axisStep, policy.QuantaDiv, policy.VelBits) != code;
            }

            var origin = ReadCode(segment + (a * posBytes), posBytes) * axisStep;
            var off = origin + (velocity * elapsed) - p[a];
            error += off * off;
        }

        if (shadowFires)
        {
            shadow++;
        }

        var refit = error > policy.ToleranceSquared;
        var heartbeat = segmentMoving && age >= policy.MaxAgeTicks;
        if (!refit && !heartbeat)
        {
            return;
        }

        // ── 4. The refit, from the run start ────────────────────────────────────────────────────────────────────────────────────────────────────────
        started = ReadTick(runTick);
        var window = (double)(tick - started);
        if (policy.VelocityDeclared && velocityColumn != null)
        {
            // The declaration's exact alternative to the measurement: the simulation's own velocity, in metres per second, scaled to the wire's
            // displacement per tick. Nothing measured is mixed into it.
            var value = velocityColumn + (slot * policy.VelocityComponentSize) + policy.VelocityFieldOffset;
            for (var a = 0; a < dims; a++)
            {
                fit[a] = Unsafe.ReadUnaligned<float>(ref value[a * 4]) * policy.TickPeriodSeconds;
            }
        }
        else if (window > 0)
        {
            for (var a = 0; a < dims; a++)
            {
                fit[a] = (p[a] - (ReadCode(runStart + (a * posBytes), posBytes) * policy.Step[a])) / window;
            }
        }
        else
        {
            fit[..dims].Clear();
        }

        EmitSegment(policy, segment, current, fit, tick, segment[policy.SegmentEpochOffset]);
        hot->GroupTicks[0] = tick;
        segments++;

        if (refit)
        {
            // An error-triggered refit says the run it was fitted from no longer describes the motion, so the next one starts here. A heartbeat says
            // nothing of the kind and keeps the long window — which is the whole reason it is not a run restart.
            StartRun(policy, runStart, runTick, current, tick);
        }
    }

    /// <summary>
    /// Whether the segment this entry last published carries a non-zero velocity — i.e. the client is still dead-reckoning it forward.
    /// </summary>
    /// <param name="policy">The archetype's motion policy.</param>
    /// <param name="hotBytes">The slot's hot entry.</param>
    /// <returns><see langword="true"/> when any axis of the stored segment has a non-zero velocity code.</returns>
    /// <remarks>
    /// <b>Why anything outside this class needs to ask.</b> Motion is the one projected thing that is STATEFUL on the client: a client holding a moving
    /// segment keeps advancing the entity every frame whether or not the server sends anything. So "this entity's bytes are identical to last tick's"
    /// does not mean "this client needs nothing" — it usually means the opposite, that the entity has STOPPED and the client does not know yet. A
    /// change-gated projection pass that skipped such a slot would let the client coast past tolerance, which is exactly what the differential oracle
    /// caught: a creature 0.14 units ahead of the server after two hundred churned ticks, against a 0.057 tolerance.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsExtrapolating(in MotionPolicy policy, byte* hotBytes)
    {
        if (policy.SegmentOffset < 0)
        {
            return false;
        }

        var velocity = hotBytes + policy.SegmentOffset + policy.SegmentVelocityOffset;
        for (var a = 0; a < policy.Dims; a++)
        {
            if (ReadVelocity(velocity + (a * policy.VelBytes), policy.VelBytes) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes one segment into the hot entry's pre-encoded region: <c>p0</c>, the velocity when the model carries one, <c>t0</c> as the tick's low 16 bits
    /// (W9) and the epoch.
    /// </summary>
    /// <param name="policy">The archetype's derived constants.</param>
    /// <param name="segment">The segment region inside the hot entry.</param>
    /// <param name="position">The quantized position the segment starts at.</param>
    /// <param name="velocity">Metres per tick per axis, or an empty span for a stationary segment.</param>
    /// <param name="tick">The segment's absolute start tick.</param>
    /// <param name="epoch">The motion epoch this segment belongs to.</param>
    private static void EmitSegment(in MotionPolicy policy, byte* segment, byte* position, Span<double> velocity, uint tick, byte epoch)
    {
        var bytes = policy.Dims * policy.PosBytes;
        for (var i = 0; i < bytes; i++)
        {
            segment[i] = position[i];
        }

        if (policy.Linear)
        {
            var at = segment + policy.SegmentVelocityOffset;
            for (var a = 0; a < policy.Dims; a++)
            {
                // Clamped by the codec, and the clamp is never reached in practice: the width was derived from the same teleport threshold that trigger 1
                // refuses to let a step exceed (W5), so a displacement that would saturate has already been sent as a teleport.
                var code = velocity.IsEmpty ? 0 : WireMath.EncodeVel(velocity[a], policy.Step[a], policy.QuantaDiv, policy.VelBits);
                WriteCode(at + (a * policy.VelBytes), policy.VelBytes, unchecked((uint)code));
            }
        }

        var t0 = segment + policy.SegmentTickOffset;
        t0[0] = (byte)tick;
        t0[1] = (byte)(tick >> 8);
        segment[policy.SegmentEpochOffset] = epoch;
    }

    /// <summary>Makes <paramref name="position"/> at <paramref name="tick"/> the start of the current run.</summary>
    private static void StartRun(in MotionPolicy policy, byte* runStart, byte* runTick, byte* position, uint tick)
    {
        var bytes = policy.Dims * policy.PosBytes;
        for (var i = 0; i < bytes; i++)
        {
            runStart[i] = position[i];
        }

        WriteCode(runTick, 4, tick);
    }

    /// <summary>The squared length of a vector, so a comparison against a threshold costs no square root.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LengthSquared(Span<double> v, int dims)
    {
        var total = 0d;
        for (var a = 0; a < dims; a++)
        {
            total += v[a] * v[a];
        }

        return total;
    }

    /// <summary>Reads a little-endian unsigned code of <paramref name="bytes"/> bytes — the form a quantized position axis is stored and sent in.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadCode(byte* at, int bytes)
    {
        uint value = 0;
        for (var i = 0; i < bytes; i++)
        {
            value |= (uint)at[i] << (8 * i);
        }

        return value;
    }

    /// <summary>Writes a little-endian code of <paramref name="bytes"/> bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteCode(byte* at, int bytes, uint code)
    {
        for (var i = 0; i < bytes; i++)
        {
            at[i] = (byte)(code >> (8 * i));
        }
    }

    /// <summary>Reads a little-endian two's-complement velocity code, sign-extended from its codec's width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadVelocity(byte* at, int bytes)
    {
        var raw = ReadCode(at, bytes);
        var shift = 32 - (bytes * 8);
        return shift == 0 ? (int)raw : (int)(raw << shift) >> shift;
    }

    /// <summary>Reads the run start's absolute tick.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadTick(byte* at) => ReadCode(at, 4);
}
