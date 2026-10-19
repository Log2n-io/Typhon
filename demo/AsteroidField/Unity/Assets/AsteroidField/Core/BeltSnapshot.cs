// Plain C# (C# 9, netstandard2.1): compiled by Unity and, linked, by the headless bot and the tests — the same code reads the stream in all three.
using System;
using System.Collections.Generic;

namespace AsteroidField.Client
{
    /// <summary>One entity as the last frame described it: its newest motion segment, and the fields this demo draws.</summary>
    public struct EntityView
    {
        public uint NetId;

        /// <summary>The newest motion segment: the position at <see cref="T0"/> and the displacement per tick after it, in belt metres (X, Y).</summary>
        public double X0;

        public double Y0;
        public double Vx;
        public double Vy;
        public uint T0;

        /// <summary>A ship's newest heading in radians (0 along +Y); 0 for an asteroid. What steering reads; drawing reads <see cref="BeltSnapshot.HeadingAt"/>.</summary>
        public double Heading;

        /// <summary>An asteroid's ore and size (1–4); 0 for a ship.</summary>
        public int Ore;

        public int Size;

        /// <summary>A ship's mining target (an asteroid's netId), 0 when its beam is off; whether a drone flies it.</summary>
        public uint Target;

        public bool Drone;

        /// <summary>This entity's segments in <see cref="BeltSnapshot.Segments"/>, oldest first: those render time can still reach.</summary>
        public int SegmentFirst;

        public int SegmentCount;

        /// <summary>A ship's headings in <see cref="BeltSnapshot.Headings"/>, oldest first.</summary>
        public int HeadingFirst;

        public int HeadingCount;

        /// <summary>The position at <paramref name="tick"/> on the newest segment — right at the newest tick, which is what a bot steers by.</summary>
        public readonly void PositionAt(double tick, out double x, out double y)
        {
            var dt = tick - T0;
            x = X0 + (Vx * dt);
            y = Y0 + (Vy * dt);
        }
    }

    /// <summary>One motion segment: where an entity was at <see cref="T0"/>, and its displacement per tick after it. A new epoch is a teleport.</summary>
    public struct MotionSegment
    {
        public uint T0;
        public byte Epoch;
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
    }

    /// <summary>A ship's heading as of a tick.</summary>
    public struct HeadingSample
    {
        public long Tick;
        public double Heading;
    }

    /// <summary>
    /// Everything the client knows after one frame, copied out of the SDK's store on its receive thread, read on the game's thread. Not changed once
    /// handed over; a game that is done with one gives it back (<see cref="BeltLink.Recycle"/>) and the link refills it for a later frame.
    /// </summary>
    public sealed class BeltSnapshot
    {
        public long Tick;

        /// <summary>The server's tick period, in milliseconds.</summary>
        public double PeriodMs;

        public readonly List<EntityView> Asteroids = new List<EntityView>();
        public readonly List<EntityView> Ships = new List<EntityView>();

        /// <summary>Every entity's recent segments, each entity's run contiguous and oldest first (<see cref="EntityView.SegmentFirst"/>).</summary>
        public readonly List<MotionSegment> Segments = new List<MotionSegment>();

        /// <summary>Every ship's recent headings, each ship's run contiguous and oldest first (<see cref="EntityView.HeadingFirst"/>).</summary>
        public readonly List<HeadingSample> Headings = new List<HeadingSample>();

        /// <summary>The ship this client flies (its netId), 0 before the server has given it one.</summary>
        public uint SelfNetId;

        /// <summary>Owner-only state: the hold and the credits. Nobody else's client receives these.</summary>
        public int Cargo;

        public int Credits;

        /// <summary>Bytes received since connecting, and frames applied.</summary>
        public long BytesReceived;

        public long Frames;

        public bool TryGetShip(uint netId, out EntityView ship)
        {
            foreach (var s in Ships)
            {
                if (s.NetId == netId)
                {
                    ship = s;
                    return true;
                }
            }

            ship = default;
            return false;
        }

        /// <summary>
        /// Where <paramref name="e"/> is at render time: on the segment in force then — the newest whose start render time has reached, the oldest
        /// before all of them — extrapolated, and over the last tick before a newer segment of the same epoch, carried onto that segment's origin.
        /// </summary>
        /// <remarks>
        /// The rule of the SDK's <c>MotionEvaluator</c> for linear motion, on the copied segments: render time trails the newest frame by the render delay,
        /// so the newest segment is not the one in force, and extrapolating it backwards cuts the corner of every turn.
        /// </remarks>
        public void PositionAt(in EntityView e, long renderTick, double frac, out double x, out double y)
        {
            if (e.SegmentCount == 0)
            {
                e.PositionAt(renderTick + frac, out x, out y);
                return;
            }

            var last = e.SegmentFirst + e.SegmentCount - 1;
            var at = e.SegmentFirst;
            for (var i = last; i > e.SegmentFirst; i--)
            {
                if (renderTick >= Segments[i].T0)
                {
                    at = i;
                    break;
                }
            }

            var s = Segments[at];
            var dt = (renderTick - (long)s.T0) + frac;
            if (at < last)
            {
                // The newer segment was emitted because this one's velocity stopped describing the motion at its start: the last tick before it goes
                // to the origin the server reported, not along the old velocity past it.
                var next = Segments[at + 1];
                var span = (long)next.T0 - s.T0;
                if (span > 0 && dt >= 0 && next.Epoch == s.Epoch && dt >= span - 1)
                {
                    var u = Math.Min(dt - (span - 1), 1d);
                    var fromX = s.X + (s.Vx * (span - 1));
                    var fromY = s.Y + (s.Vy * (span - 1));
                    x = fromX + ((next.X - fromX) * u);
                    y = fromY + ((next.Y - fromY) * u);
                    return;
                }
            }

            x = s.X + (s.Vx * dt);
            y = s.Y + (s.Vy * dt);
        }

        /// <summary>A ship's heading at render time: the newest sample render time has reached, the oldest before all of them.</summary>
        public double HeadingAt(in EntityView e, long renderTick)
        {
            if (e.HeadingCount == 0)
            {
                return e.Heading;
            }

            for (var i = e.HeadingFirst + e.HeadingCount - 1; i > e.HeadingFirst; i--)
            {
                if (renderTick >= Headings[i].Tick)
                {
                    return Headings[i].Heading;
                }
            }

            return Headings[e.HeadingFirst].Heading;
        }

        internal void Clear()
        {
            Tick = 0;
            PeriodMs = 0;
            Asteroids.Clear();
            Ships.Clear();
            Segments.Clear();
            Headings.Clear();
            SelfNetId = 0;
            Cargo = 0;
            Credits = 0;
            BytesReceived = 0;
            Frames = 0;
        }
    }

    /// <summary>A frame's arrival, for the render clock: its tick and when it was received (<see cref="BeltLink.NowMs"/>).</summary>
    public readonly struct FrameArrival
    {
        public FrameArrival(long tick, double receivedMs)
        {
            Tick = tick;
            ReceivedMs = receivedMs;
        }

        public long Tick { get; }

        public double ReceivedMs { get; }
    }
}
