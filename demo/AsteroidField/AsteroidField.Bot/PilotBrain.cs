using System;
using System.Collections.Generic;
using AsteroidField.Client;
using Typhon.Client;

namespace AsteroidField.Bot;

/// <summary>
/// A pilot that needs no human: flies to the nearest asteroid it can see, mines it, and sells when the server says the hold is full. It reads the stream
/// through the same <see cref="BeltLink"/> the Unity client does, so what it can do, a Unity pilot can.
/// </summary>
public sealed class PilotBrain
{
    private const double MineRangeM = 60;
    private readonly BeltLink _link;
    private readonly List<ReceivedEvent> _events = [];
    private uint _target;

    public PilotBrain(BeltLink link) => _link = link;

    /// <summary>The last snapshot taken.</summary>
    public BeltSnapshot World { get; private set; }

    /// <summary>Hold-full events received, and sales made.</summary>
    public int HoldsFull { get; private set; }

    public int Sales { get; private set; }

    /// <summary>Takes the newest snapshot and decides nothing: a pilot that only watches.</summary>
    public void Observe()
    {
        if (_link.TryTakeLatest(out var snapshot))
        {
            World = snapshot;
        }
    }

    /// <summary>One decision: call it at about the server's tick rate.</summary>
    public void Step()
    {
        if (_link.TryTakeLatest(out var snapshot))
        {
            World = snapshot;
        }

        if (World == null || !World.TryGetShip(World.SelfNetId, out var self))
        {
            return;
        }

        _events.Clear();
        _link.DrainEvents(_events);
        foreach (var e in _events)
        {
            if (e.Name == "CargoFull")
            {
                HoldsFull++;
                _link.Sell();
                Sales++;
                _target = 0;
            }
        }

        // The server clears a target that was mined out (or that this hold filled up on): follow it.
        if (_target != 0 && self.Target == 0 && !Contains(World.Asteroids, _target))
        {
            _target = 0;
        }

        if (_target == 0 && Nearest(World, self, out var nearest))
        {
            _target = nearest;
            _link.Mine(nearest);
        }

        Steer(World, self);
    }

    private void Steer(BeltSnapshot world, EntityView self)
    {
        if (_target == 0 || !TryFind(world.Asteroids, _target, out var rock))
        {
            _link.Steer(0, 0);
            return;
        }

        self.PositionAt(world.Tick, out var sx, out var sy);
        rock.PositionAt(world.Tick, out var rx, out var ry);
        var dx = rx - sx;
        var dy = ry - sy;
        var error = Wrap(Math.Atan2(dx, dy) - self.Heading);
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var thrust = distance > MineRangeM * 0.5 && Math.Abs(error) < 0.6 ? Math.Clamp(distance / 150, 0.2, 1) : -0.3;
        _link.Steer((float)thrust, (float)Math.Clamp(error * 2, -1, 1));
    }

    private static bool Nearest(BeltSnapshot world, EntityView self, out uint netId)
    {
        self.PositionAt(world.Tick, out var sx, out var sy);
        netId = 0;
        var best = double.MaxValue;
        foreach (var a in world.Asteroids)
        {
            a.PositionAt(world.Tick, out var x, out var y);
            var d = ((x - sx) * (x - sx)) + ((y - sy) * (y - sy));
            if (a.Ore > 0 && d < best)
            {
                best = d;
                netId = a.NetId;
            }
        }

        return netId != 0;
    }

    private static bool Contains(List<EntityView> views, uint netId) => TryFind(views, netId, out _);

    private static bool TryFind(List<EntityView> views, uint netId, out EntityView view)
    {
        foreach (var v in views)
        {
            if (v.NetId == netId)
            {
                view = v;
                return true;
            }
        }

        view = default;
        return false;
    }

    private static double Wrap(double a)
    {
        while (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }

        while (a < -Math.PI)
        {
            a += 2 * Math.PI;
        }

        return a;
    }
}
