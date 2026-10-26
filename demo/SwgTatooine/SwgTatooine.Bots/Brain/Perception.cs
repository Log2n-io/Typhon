using System;
using System.Collections.Generic;
using Typhon.Client;
using Typhon.Protocol;
using SwgTatooine.Navigation;

namespace SwgTatooine.Bots.Brain;

/// <summary>Reduces a bot's replicated store to the handful of facts a decision needs.</summary>
/// <remarks>
/// <para>
/// <b>One pass per tick, and the only place in the brain that touches the SDK.</b> Everything below this is a pure
/// function of a <see cref="Perceived"/>, which is what makes the decision testable without a server. The scan is
/// linear over the live slots of four archetypes — hundreds inside a city — and it happens once rather than once per
/// question.
/// </para>
/// <para>
/// <b>By archetype NAME, never by index.</b> The catalog numbers archetypes in its own order and that order is the
/// server's to change; a bot that hardcoded 0 for <c>WorldObject</c> would silently read creatures as buildings. Field
/// indices are resolved the same way, once per catalog, and a field the server does not replicate simply stays absent —
/// the bot then decides without it rather than reading a neighbouring field's bytes.
/// </para>
/// </remarks>
public sealed class Perception
{
    private const string WorldObject = "WorldObject";
    private const string Creature = "Creature";
    private const string Player = "Player";
    private const string Lair = "CreatureLair";
    private const string Offer = "MissionOffer";

    private WorldStore _bound;
    private int _structures = -1;
    private int _creatures = -1;
    private int _players = -1;
    private int _nameIdField = -1;
    private int _lairs = -1;
    private int _guardsField = -1;
    private int _offers = -1;
    private int _serialField = -1;
    private int _distanceField = -1;
    private int _offerLevelField = -1;
    private int _creatureLevelField = -1;
    private int _kindField = -1;
    private int _regionField = -1;
    private int _assetField = -1;
    private int _yawField = -1;
    private int _mountedArchetype = -1;
    private int _mountedField = -1;
    private int _stanceField = -1;
    private readonly StoreStructures _view = new();

    /// <summary>How far above its own combat level a bot fights (content-levels.md § 7): a creature or an offer past it is left alone.</summary>
    public const int LevelReach = 3;

    /// <summary>
    /// The bot's own combat level, which bounds what it hunts and which offer it prefers: 1 — where a new character belongs, the city fringes — until the
    /// character's CL is replicated and read here (character-level.md).
    /// </summary>
    public int CombatLevel { get; set; } = 1;

    /// <summary>The highest level this bot takes on: its <see cref="CombatLevel"/> plus <see cref="LevelReach"/>.</summary>
    public int MaxLevel => CombatLevel + LevelReach;

    /// <summary>Reads what this bot can see, and teaches its map any facility in view.</summary>
    /// <param name="store">The bot's store, or null before a catalog has arrived.</param>
    /// <param name="facilities">The map to fill as structures are seen.</param>
    /// <returns>What the brain decides from.</returns>
    public Perceived Read(WorldStore store, FacilityMap facilities)
    {
        // NetId and NOT Received: `Received` means "this frame carried a SELF block", and it is cleared at the start of every
        // frame. SELF arrives when the server anchors the session and then only when something about it changes, so requiring it
        // per frame blinds the bot on nearly every tick — which it did, and the fleet sent two intents per session in 45 seconds
        // while every counter said the run was healthy. NetId persists and is 0 for "controls nothing", which is the question.
        if (store?.Self == null || store.Self.NetId == 0)
        {
            return default;   // no body: a camera, or a session the server has not anchored yet
        }

        Resolve(store);
        if (!store.TryLocate(store.Self.NetId, out var ownArchetype, out var ownSlot))
        {
            return default;   // the server named a body this session does not hold yet
        }

        var own = store.Archetypes[ownArchetype];
        // Where the body IS, at the latest tick the store has heard of — not where its motion segment started. A moving body is sent as a segment
        // (a start and a velocity) and a new one only when its course changes, so the head position stood still for seconds while the body ran: a
        // bot steering by it believed itself five seconds behind, which a walk to a door cannot survive.
        Span<double> motion = stackalloc double[own.MotionStride];
        MotionEvaluator.EvaluateSlot(own, ownSlot, Math.Max(0L, store.Tick), 0d, motion);
        var x = (float)motion[0];
        var z = own.Dims > 1 ? (float)motion[1] : 0f;

        if (_mountedArchetype != ownArchetype)
        {
            _mountedArchetype = ownArchetype;
            _mountedField = FieldOf(store, ownArchetype, "mounted");
            _stanceField = FieldOf(store, ownArchetype, "stance");
        }

        var mounted = _mountedField >= 0 && store.Archetypes[ownArchetype].Numbers[_mountedField] is { } m && m[ownSlot] == 1;
        _view.Bind(store, _structures, _kindField, _regionField, _assetField, _yawField);
        var world = new Perceived
        {
            HasBody = true,
            X = x,
            Z = z,
            Realm = store.Realm?.RealmId ?? 0,
            BoardRefused = BoardRefusedIn(store),
            Mounted = mounted,
            Stance = _stanceField >= 0 && store.Archetypes[ownArchetype].Numbers[_stanceField] is { } st ? (int)st[ownSlot] : 0,
            Structures = _view,
        };

        world = NearestCreature(store, _creatures, x, z, world);
        return Facilities(store, x, z, world, facilities);
    }

    /// <summary>
    /// The nearest other player with a name within <paramref name="reachM"/> — a session's character, a person's or a bot's, which is what has a
    /// wallet (economy-foundation.md E0-11) — or 0. The scripted crowd is a <c>Player</c> too, and carries no name.
    /// </summary>
    /// <param name="store">The bot's store.</param>
    /// <param name="x">The bot, east.</param>
    /// <param name="z">The bot, north.</param>
    /// <param name="reachM">How far, metres.</param>
    /// <returns>Its netId, or 0.</returns>
    /// <remarks>Not part of <see cref="Read"/>: it is asked only when a tip is due, a few times a minute, rather than scanned every tick.</remarks>
    public uint NearestNamedPlayer(WorldStore store, float x, float z, float reachM)
    {
        if (store?.Self == null || store.Self.NetId == 0)
        {
            return 0;
        }

        Resolve(store);
        if (_players < 0 || _nameIdField < 0)
        {
            return 0;
        }

        var set = store.Archetypes[_players];
        var names = set.Numbers[_nameIdField];
        if (names == null)
        {
            return 0;
        }

        var self = store.Self.NetId;
        var best = (double)reachM;
        var bestNetId = 0u;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var netId = set.NetIds[slot];
            if (netId == self || !(names[slot] > 0))
            {
                continue;
            }

            var at = set.HeadPosition(slot);
            var distance = Routing.Distance(x, z, (float)at[0], set.Dims > 1 ? (float)at[1] : 0f);
            if (distance <= best)
            {
                best = distance;
                bestNetId = netId;
            }
        }

        return bestNetId;
    }

    /// <summary>
    /// The lair in view standing within <paramref name="reachM"/> of a point — the held mission's, at the position its journal gave — and its occupants
    /// still standing (lair-objectives.md B-3).
    /// </summary>
    /// <returns>Its netId, or 0 when no lair in view stands there.</returns>
    public uint LairAt(WorldStore store, float x, float z, float reachM, out int guards)
    {
        guards = 0;
        if (store?.Self == null || store.Self.NetId == 0)
        {
            return 0;
        }

        Resolve(store);
        if (_lairs < 0)
        {
            return 0;
        }

        var set = store.Archetypes[_lairs];
        var guardsOf = _guardsField >= 0 ? set.Numbers[_guardsField] : null;
        var best = (double)reachM;
        var found = 0u;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var at = set.HeadPosition(slot);
            var distance = Routing.Distance(x, z, (float)at[0], set.Dims > 1 ? (float)at[1] : 0f);
            if (distance <= best)
            {
                best = distance;
                found = set.NetIds[slot];
                guards = guardsOf != null ? (int)guardsOf[slot] : 0;
            }
        }

        return found;
    }

    /// <summary>
    /// The offer to take at the terminal the bot stands at (lair-objectives.md B-1): of those listed within <paramref name="reachM"/>, the one whose lair
    /// is nearest the terminal — the shortest ride out — among those within <see cref="MaxLevel"/> (content-levels.md § 7), and the nearest of all when
    /// none is.
    /// </summary>
    /// <returns>Its netId, or 0 when none is listed within reach.</returns>
    public uint BestOffer(WorldStore store, float x, float z, float reachM, out uint serial)
    {
        serial = 0;
        if (store?.Self == null || store.Self.NetId == 0)
        {
            return 0;
        }

        Resolve(store);
        if (_offers < 0 || _serialField < 0)
        {
            return 0;
        }

        var set = store.Archetypes[_offers];
        var serials = set.Numbers[_serialField];
        var distances = _distanceField >= 0 ? set.Numbers[_distanceField] : null;
        var levels = _offerLevelField >= 0 ? set.Numbers[_offerLevelField] : null;
        if (serials == null)
        {
            return 0;
        }

        var nearest = double.MaxValue;
        var nearestInReach = double.MaxValue;
        var found = 0u;
        var foundInReach = 0u;
        var serialInReach = 0u;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var at = set.HeadPosition(slot);
            if (Routing.Distance(x, z, (float)at[0], set.Dims > 1 ? (float)at[1] : 0f) > reachM)
            {
                continue;
            }

            var ride = distances != null ? distances[slot] : 0d;
            if (ride < nearest)
            {
                nearest = ride;
                found = set.NetIds[slot];
                serial = (uint)serials[slot];
            }

            if (ride < nearestInReach && (levels == null || levels[slot] <= MaxLevel))
            {
                nearestInReach = ride;
                foundInReach = set.NetIds[slot];
                serialInReach = (uint)serials[slot];
            }
        }

        if (foundInReach != 0)
        {
            serial = serialInReach;
            return foundInReach;
        }

        return found;
    }

    /// <summary>The nearest creature, with its position and distance.</summary>
    /// <param name="store">The store.</param>
    /// <param name="archetype">Which archetype to scan, or -1.</param>
    /// <param name="x">The bot, east.</param>
    /// <param name="z">The bot, north.</param>
    /// <param name="world">What has been read so far.</param>
    /// <returns>The updated perception.</returns>
    /// <remarks>
    /// <para>
    /// <b>Lairs are deliberately not scanned</b>: only the god profiles observe them, so a player session's store never
    /// holds one and a scan would walk an always-empty archetype. See <see cref="Perceived.NearestCreature"/>.
    /// </para>
    /// <para>
    /// <b>Nor a creature above <see cref="MaxLevel"/></b> (content-levels.md § 7): a level-45 viper in view no longer pulls a CL-1 bot into a fight it
    /// cannot win.
    /// </para>
    /// </remarks>
    private Perceived NearestCreature(WorldStore store, int archetype, float x, float z, Perceived world)
    {
        if (archetype < 0)
        {
            return world;
        }

        var set = store.Archetypes[archetype];
        var levels = _creatureLevelField >= 0 ? set.Numbers[_creatureLevelField] : null;
        var best = double.MaxValue;
        var bestNetId = 0u;
        var bestX = 0f;
        var bestZ = 0f;
        var bestLevel = 0;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var level = levels != null ? (int)levels[slot] : 0;
            if (level > MaxLevel)
            {
                continue;
            }

            var at = set.HeadPosition(slot);
            var px = (float)at[0];
            var pz = set.Dims > 1 ? (float)at[1] : 0f;
            var distance = Routing.Distance(x, z, px, pz);
            if (distance < best)
            {
                best = distance;
                bestNetId = set.NetIds[slot];
                bestX = px;
                bestZ = pz;
                bestLevel = level;
            }
        }

        if (bestNetId == 0)
        {
            return world;
        }

        return world with
        {
            NearestCreature = bestNetId,
            NearestCreatureX = bestX,
            NearestCreatureZ = bestZ,
            NearestCreatureM = (float)best,
            NearestCreatureLevel = bestLevel,
        };
    }

    /// <summary>Teaches the map every shuttleport, bazaar and mission terminal in view, and notices a port close enough to board at.</summary>
    /// <param name="store">The store.</param>
    /// <param name="x">The bot, east.</param>
    /// <param name="z">The bot, north.</param>
    /// <param name="world">What has been read so far.</param>
    /// <param name="facilities">The map to fill.</param>
    /// <returns>The updated perception.</returns>
    /// <remarks>
    /// <b>Learning happens here rather than in the brain, because it is a fact about what arrived and not a decision.</b>
    /// A structure carries its kind and its region on enter, so a bot walking through a city collects that city's
    /// facilities as a side effect of being there — and remembers them after it has left and they have gone from the
    /// store.
    /// </remarks>
    private Perceived Facilities(WorldStore store, float x, float z, Perceived world, FacilityMap facilities)
    {
        if (_structures < 0 || _kindField < 0 || _regionField < 0 || facilities == null)
        {
            return world;
        }

        var set = store.Archetypes[_structures];
        var kinds = set.Numbers[_kindField];
        var regions = set.Numbers[_regionField];
        if (kinds == null || regions == null)
        {
            return world;
        }

        var assets = _assetField >= 0 ? set.Numbers[_assetField] : null;
        var yaws = _yawField >= 0 ? set.Numbers[_yawField] : null;

        var port = 0;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var region = (int)regions[slot];
            if (region == 0)
            {
                continue;   // in the wild: it belongs to no city, so there is nothing to remember it under
            }

            // The three kinds a goal reads, and only those, and it is a cost decision. A city holds about 190 structures and
            // every one of them was being filed every tick by every bot standing there — some 380 k hashed writes a second at
            // 500 bots — for a table whose readers ask for ports, bazaars and mission terminals: a dozen per city. A POI's
            // props carry a region too, and theirs are numbered above the cities' (WorldBuilder starts poiIndex at
            // Cities.Count), so they are never confused with a city's facilities.
            var kind = (int)kinds[slot];
            if (kind != Routing.ShuttleportKind && kind != Routing.BazaarKind && kind != Routing.MissionTerminalKind)
            {
                continue;
            }

            var at = set.HeadPosition(slot);
            var px = (float)at[0];
            var pz = set.Dims > 1 ? (float)at[1] : 0f;

            // The region is kept as the wire carries it — a city plus one — so that 0 means "none" the whole way through.
            facilities.Observe(region, kind, px, pz, assets != null ? (int)assets[slot] + 1 : 0, yaws != null ? (int)yaws[slot] : 0);
            if (kind == Routing.ShuttleportKind && Routing.Distance(x, z, px, pz) <= BoardingM)
            {
                port = region;
            }
        }

        return world with { PortRegion = port };
    }

    /// <summary><c>ACKS</c> reason: the body was not at a shuttleport — <c>TatooineReplication.ShuttleNotAtPort</c>.</summary>
    /// <remarks>
    /// The bot's copy of two server constants, for the reason <see cref="BoardingM"/> is: this project references
    /// <c>Typhon.Client</c> and nothing of the server, deliberately, so a shared constant would mean a shared
    /// assembly. <c>AckReasons.FirstApplicationReason</c> is the protocol's, so only the offsets are duplicated.
    /// </remarks>
    public const byte ShuttleNotAtPort = AckReasons.FirstApplicationReason + 3;

    /// <summary><c>ACKS</c> reason: the destination city does not exist, or is the one it stands in.</summary>
    public const byte ShuttleBadDestination = AckReasons.FirstApplicationReason + 4;

    /// <summary>Whether the frame just applied refused a boarding.</summary>
    /// <param name="store">The store, whose ACK list holds this frame's answers only.</param>
    /// <returns>Whether one of the two shuttle refusals arrived.</returns>
    /// <remarks>
    /// <b>By reason and not by sequence number.</b> Matching the ACK to the command that earned it would need the
    /// swarm's sequence numbers in the brain; a bot has exactly one boarding in flight at a time, so "a boarding was
    /// refused" is the whole question. The list is cleared every frame by the applier, so this is never stale.
    /// </remarks>
    private static bool BoardRefusedIn(WorldStore store)
    {
        var acks = store.Acks;
        for (var i = 0; i < acks.Count; i++)
        {
            var reason = acks[i].Reason;
            if (reason == ShuttleNotAtPort || reason == ShuttleBadDestination)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How close the server lets a body board, in metres — <c>TatooineReplication.ShuttleBoardM</c>.</summary>
    /// <remarks>
    /// The bot's copy of a server constant, so it asks when the ask will be accepted rather than learning by refusal. A
    /// refusal is still the authority: if these two ever disagree the bot walks closer and asks again, which is what a
    /// person does when a prompt does not appear.
    /// </remarks>
    public const double BoardingM = 30d;

    /// <summary>Resolves the archetype and field indices once per catalog.</summary>
    /// <param name="store">The store to resolve against.</param>
    private void Resolve(WorldStore store)
    {
        if (ReferenceEquals(_bound, store))
        {
            return;
        }

        _bound = store;
        _structures = IndexOf(store, WorldObject);
        _creatures = IndexOf(store, Creature);
        _players = IndexOf(store, Player);
        _nameIdField = FieldOf(store, _players, "nameId");
        _lairs = IndexOf(store, Lair);
        _guardsField = FieldOf(store, _lairs, "guards");
        _offers = IndexOf(store, Offer);
        _serialField = FieldOf(store, _offers, "serial");
        _distanceField = FieldOf(store, _offers, "distanceM");
        _offerLevelField = FieldOf(store, _offers, "level");
        _creatureLevelField = FieldOf(store, _creatures, "level");

        // Both fields are resolved against the archetype they are read from, which is the bug this line replaced: the
        // first version resolved a field index against `Player` and then indexed it into whatever archetype held the
        // body, bounds-checked only. A field index is meaningful in one archetype and nowhere else.
        _kindField = FieldOf(store, _structures, "kind");
        _regionField = FieldOf(store, _structures, "region");
        _assetField = FieldOf(store, _structures, "asset");
        _yawField = FieldOf(store, _structures, "yaw");
        _mountedArchetype = -1;
    }

    private static int IndexOf(WorldStore store, string name)
    {
        for (var i = 0; i < store.Archetypes.Length; i++)
        {
            if (store.Archetypes[i].Plan.Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FieldOf(WorldStore store, int archetype, string name)
    {
        if (archetype < 0)
        {
            return -1;
        }

        var fields = store.Archetypes[archetype].Plan.Fields;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>The structures a bot holds, read only when a goal asks for them.</summary>
/// <remarks>
/// <b>On demand, because a path is planned once a leg and the store is read every tick.</b> Copying a city's 190 structures into a list each tick for
/// a reader that wants them every few seconds is the cost the facility filter above already declined. The brain calls <see cref="CopyTo"/> when it
/// plans; a test hands it a list.
/// </remarks>
public interface IStructureView
{
    /// <summary>Appends every structure held to a list (the list is not cleared).</summary>
    void CopyTo(List<SeenStructure> into);
}

/// <summary>A fixed list of structures, for a test or a snapshot.</summary>
public sealed class ListStructures(IReadOnlyList<SeenStructure> list) : IStructureView
{
    /// <inheritdoc/>
    public void CopyTo(List<SeenStructure> into) => into.AddRange(list);
}

/// <summary>The view over a bot's store, rebound each tick.</summary>
internal sealed class StoreStructures : IStructureView
{
    private WorldStore _store;
    private int _archetype = -1;
    private int _kind = -1;
    private int _region = -1;
    private int _asset = -1;
    private int _yaw = -1;

    public void Bind(WorldStore store, int archetype, int kind, int region, int asset, int yaw)
    {
        _store = store;
        _archetype = archetype;
        _kind = kind;
        _region = region;
        _asset = asset;
        _yaw = yaw;
    }

    public void CopyTo(List<SeenStructure> into)
    {
        if (_store == null || _archetype < 0 || _asset < 0)
        {
            return;
        }

        var set = _store.Archetypes[_archetype];
        var kinds = _kind >= 0 ? set.Numbers[_kind] : null;
        var regions = _region >= 0 ? set.Numbers[_region] : null;
        var assets = set.Numbers[_asset];
        var yaws = _yaw >= 0 ? set.Numbers[_yaw] : null;
        if (assets == null)
        {
            return;
        }

        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var at = set.HeadPosition(slot);
            into.Add(new SeenStructure(
                (float)at[0],
                set.Dims > 1 ? (float)at[1] : 0f,
                kinds != null ? (int)kinds[slot] : 0,
                (int)assets[slot],
                yaws != null ? (int)yaws[slot] : 0,
                regions != null ? (int)regions[slot] : 0));
        }
    }
}
