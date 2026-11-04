using System.Collections.Generic;
using System.Threading;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Server;

/// <summary>
/// A character's own waypoints (client.md C38), in the Workshop's side transaction: created at a point of the body's realm or on one of its
/// installations, deleted, made active — the owner's commands alone — and an installation's pins deleted with it when it is picked up.
/// </summary>
/// <remarks>
/// <b>Held in memory, keyed by owner</b>, read on the first use after a start or a failed commit: a create counts the owner's in O(1), an activation
/// clears the owner's other active one without a query.
/// </remarks>
public sealed partial class SimBridge
{
    private readonly Dictionary<long, (EntityId Id, WaypointCore C)> _waypoints = [];
    private readonly Dictionary<long, List<long>> _waypointsOf = [];
    private readonly List<(SessionId Session, ushort Seq)> _waypointsApplied = [];
    private bool _waypointsLoaded;
    private bool _waypointsDirty;
    private long _pendingWaypointsCreated;
    private long _pendingWaypointsDeleted;
    private long _waypointsCreated;
    private long _waypointsDeleted;

    /// <summary>Waypoints created since start, and deleted — by their owner or with the installation they pinned.</summary>
    public long WaypointsCreated => Interlocked.Read(ref _waypointsCreated);

    public long WaypointsDeleted => Interlocked.Read(ref _waypointsDeleted);

    /// <summary>The waypoints, read on the first use after a start or a failed commit.</summary>
    private void EnsureWaypoints(Transaction tx)
    {
        if (_waypointsLoaded)
        {
            return;
        }

        _waypoints.Clear();
        _waypointsOf.Clear();
        foreach (var found in tx.Query<WaypointArch>())
        {
            Keep(unchecked((long)found.Id.RawValue), found.Id, found.Read(WaypointArch.Core));
        }

        _waypointsLoaded = true;
    }

    private void Keep(long key, EntityId id, in WaypointCore c)
    {
        _waypoints[key] = (id, c);
        if (!_waypointsOf.TryGetValue(c.Owner, out var keys))
        {
            keys = [];
            _waypointsOf[c.Owner] = keys;
        }

        if (!keys.Contains(key))
        {
            keys.Add(key);
        }
    }

    private void Forget(Transaction tx, long key)
    {
        var (id, c) = _waypoints[key];
        tx.Destroy(id);
        _waypoints.Remove(key);
        _waypointsOf[c.Owner].Remove(key);
        _waypointsDirty = true;
        _pendingWaypointsDeleted++;
    }

    private int WaypointCountOf(long owner) => _waypointsOf.TryGetValue(owner, out var keys) ? keys.Count : 0;

    private void CreateWaypointFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<CreateWaypoint> command)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out _, out var realm, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var v = command.Value;
        var name = Waypoints.CleanName(v.Name.Utf8);
        if (name == null || !Waypoints.ColorOk(v.Color))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.WaypointBad);
            return;
        }

        var c = new WaypointCore { Owner = owner, Name = (String64)name, Color = v.Color, Kind = WaypointKind.Custom, X = v.X, Z = v.Z, Realm = realm };
        if (v.Installation != 0)
        {
            // A pin: the character's own installation, once — where it stands, in its realm.
            var installation = unchecked((long)v.Installation);
            if (!_installations.TryGetValue(installation, out var found) || found.C.Owner != owner || PinOf(owner, installation) != 0)
            {
                RefuseWorkshop(subs, in command, TatooineReplication.WaypointBad);
                return;
            }

            c.Kind = WaypointKind.Installation;
            c.Installation = installation;
            c.X = found.C.X;
            c.Z = found.C.Z;
            c.Realm = found.C.Realm;
        }
        else if (!Waypoints.OnPlanet(v.X, v.Z))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.WaypointBad);
            return;
        }

        if (WaypointCountOf(owner) >= Waypoints.MaxPerCharacter)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.WaypointsFull);
            return;
        }

        var id = tx.Spawn<WaypointArch>(WaypointArch.Core.Set(in c));
        Keep(unchecked((long)id.RawValue), id, in c);
        _waypointsDirty = true;
        _pendingWaypointsCreated++;
        _waypointsApplied.Add((command.Session, command.Seq));
    }

    private void DeleteWaypointFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<DeleteWaypoint> command)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out _, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var key = unchecked((long)command.Value.Waypoint);
        if (!_waypoints.TryGetValue(key, out var found) || found.C.Owner != owner)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.WaypointUnknown);
            return;
        }

        Forget(tx, key);
        _waypointsApplied.Add((command.Session, command.Seq));
    }

    private void SetActiveWaypointFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<SetActiveWaypoint> command)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out _, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var target = unchecked((long)command.Value.Waypoint);
        if (target != 0 && (!_waypoints.TryGetValue(target, out var found) || found.C.Owner != owner))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.WaypointUnknown);
            return;
        }

        // The one asked for active, every other of the owner's not: only those whose flag changes are written.
        if (_waypointsOf.TryGetValue(owner, out var keys))
        {
            foreach (var key in keys)
            {
                var (id, c) = _waypoints[key];
                var active = (byte)(key == target ? 1 : 0);
                if (c.Active != active)
                {
                    c.Active = active;
                    tx.OpenMut(id).Set(WaypointArch.Core, c);
                    _waypoints[key] = (id, c);
                    _waypointsDirty = true;
                }
            }
        }

        _waypointsApplied.Add((command.Session, command.Seq));
    }

    /// <summary>The waypoint pinning an installation of the owner's, or 0.</summary>
    private long PinOf(long owner, long installation)
    {
        if (_waypointsOf.TryGetValue(owner, out var keys))
        {
            foreach (var key in keys)
            {
                if (_waypoints[key].C.Installation == installation)
                {
                    return key;
                }
            }
        }

        return 0;
    }

    /// <summary>An installation picked up: its pins go with it, in the same transaction.</summary>
    private void DeletePinsOf(Transaction tx, long installation)
    {
        if (!_waypointsLoaded || !_installations.TryGetValue(installation, out var found))
        {
            return;
        }

        var pin = PinOf(found.C.Owner, installation);
        if (pin != 0)
        {
            Forget(tx, pin);
        }
    }

    /// <summary>The pass's end: a failed commit refuses what was applied and reads the waypoints again; a commit publishes the counts.</summary>
    private void EndWaypoints(SubscriptionsCommands subs, bool committed)
    {
        if (!committed)
        {
            foreach (var (session, seq) in _waypointsApplied)
            {
                subs?.Reject(session, seq, TatooineReplication.CraftFailed);
            }

            if (_waypointsDirty)
            {
                _waypointsLoaded = false;
            }
        }
        else
        {
            Interlocked.Add(ref _waypointsCreated, _pendingWaypointsCreated);
            Interlocked.Add(ref _waypointsDeleted, _pendingWaypointsDeleted);
        }

        _waypointsApplied.Clear();
        _waypointsDirty = false;
        _pendingWaypointsCreated = 0;
        _pendingWaypointsDeleted = 0;
    }
}
