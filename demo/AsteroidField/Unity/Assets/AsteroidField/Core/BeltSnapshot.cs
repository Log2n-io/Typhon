// Plain C# (C# 9, netstandard2.1): compiled by Unity and, linked, by the headless bot and the tests — the same code reads the stream in all three.
using System.Collections.Generic;

namespace AsteroidField.Client
{
    /// <summary>One entity as the last frame described it: its motion segment, and the fields this demo draws.</summary>
    public struct EntityView
    {
        public uint NetId;

        /// <summary>The motion segment: the position at <see cref="T0"/> and the displacement per tick after it, in belt metres (X, Y).</summary>
        public double X0;

        public double Y0;
        public double Vx;
        public double Vy;
        public uint T0;

        /// <summary>A ship's heading in radians (0 along +Y); 0 for an asteroid.</summary>
        public double Heading;

        /// <summary>An asteroid's ore and size (1–4); 0 for a ship.</summary>
        public int Ore;

        public int Size;

        /// <summary>A ship's mining target (an asteroid's netId), 0 when its beam is off; whether a drone flies it.</summary>
        public uint Target;

        public bool Drone;

        /// <summary>The position at <paramref name="renderTime"/> (ticks, fractional): the segment extrapolated.</summary>
        public readonly void PositionAt(double renderTime, out double x, out double y)
        {
            var dt = renderTime - T0;
            x = X0 + (Vx * dt);
            y = Y0 + (Vy * dt);
        }
    }

    /// <summary>
    /// Everything the client knows after one frame, copied out of the SDK's store on its receive thread, read on the game's thread. Immutable once
    /// published: the link builds a new one per frame and hands over the newest.
    /// </summary>
    public sealed class BeltSnapshot
    {
        public long Tick;

        /// <summary>The server's tick period, in milliseconds.</summary>
        public double PeriodMs;

        public readonly List<EntityView> Asteroids = new List<EntityView>();
        public readonly List<EntityView> Ships = new List<EntityView>();

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
