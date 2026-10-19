using System;
using Typhon.Client;

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
    private const string CreatureLair = "CreatureLair";
    private const string Creature = "Creature";
    private const string Player = "Player";

    private WorldStore _bound;
    private int _structures = -1;
    private int _creatures = -1;
    private int _players = -1;
    private int _kindField = -1;
    private int _regionField = -1;
    private int _hpField = -1;

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
        var at = own.HeadPosition(ownSlot);
        var x = (float)at[0];
        var z = own.Dims > 1 ? (float)at[1] : 0f;

        var world = new Perceived
        {
            HasBody = true,
            X = x,
            Z = z,
            Realm = store.Realm?.RealmId ?? 0,
            HealthFraction = Health(own, ownSlot),
            PortCity = -1,
        };

        world = NearestCreature(store, _creatures, x, z, world);
        world = Facilities(store, x, z, world, facilities);
        return world with { PlayersInView = Count(store, _players) };
    }

    /// <summary>The nearest creature, with its position and distance.</summary>
    /// <param name="store">The store.</param>
    /// <param name="archetype">Which archetype to scan, or -1.</param>
    /// <param name="x">The bot, east.</param>
    /// <param name="z">The bot, north.</param>
    /// <param name="world">What has been read so far.</param>
    /// <returns>The updated perception.</returns>
    /// <remarks>
    /// <b>Lairs are deliberately not scanned</b>: only the god profiles observe them, so a player session's store never
    /// holds one and a scan would walk an always-empty archetype. See <see cref="Perceived.NearestCreature"/>.
    /// </remarks>
    private static Perceived NearestCreature(WorldStore store, int archetype, float x, float z, Perceived world)
    {
        if (archetype < 0)
        {
            return world;
        }

        var set = store.Archetypes[archetype];
        var best = double.MaxValue;
        var bestNetId = 0u;
        var bestX = 0f;
        var bestZ = 0f;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
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
            }
        }

        if (bestNetId == 0)
        {
            return world;
        }

        return world with { NearestCreature = bestNetId, NearestCreatureX = bestX, NearestCreatureZ = bestZ, NearestCreatureM = (float)best };
    }

    /// <summary>Teaches the map every structure in view, and notices a port close enough to board at.</summary>
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

        var port = -1;
        for (var i = 0; i < set.LiveCount; i++)
        {
            var slot = set.Live[i];
            var region = (int)regions[slot];
            if (region == 0)
            {
                continue;   // in the wild: it belongs to no city, so there is nothing to remember it under
            }

            var kind = (int)kinds[slot];
            var at = set.HeadPosition(slot);
            var px = (float)at[0];
            var pz = set.Dims > 1 ? (float)at[1] : 0f;

            // region is city + 1, because the server's key field is a sparse index and a sparse index never files key 0.
            facilities.Observe(region - 1, kind, px, pz);
            if (kind == Routing.ShuttleportKind && Routing.Distance(x, z, px, pz) <= BoardingM)
            {
                port = region - 1;
            }
        }

        return world with { PortCity = port };
    }

    /// <summary>How close the server lets a body board, in metres — <c>TatooineReplication.ShuttleBoardM</c>.</summary>
    /// <remarks>
    /// The bot's copy of a server constant, so it asks when the ask will be accepted rather than learning by refusal. A
    /// refusal is still the authority: if these two ever disagree the bot walks closer and asks again, which is what a
    /// person does when a prompt does not appear.
    /// </remarks>
    public const double BoardingM = 20d;

    /// <summary>How many of an archetype are in view.</summary>
    /// <param name="store">The store.</param>
    /// <param name="archetype">Which, or -1.</param>
    /// <returns>The count.</returns>
    private static int Count(WorldStore store, int archetype) => archetype < 0 ? 0 : store.Archetypes[archetype].LiveCount;

    /// <summary>A body's health as a fraction, or 1 when the server does not replicate it.</summary>
    /// <param name="own">The archetype holding it.</param>
    /// <param name="slot">Its slot.</param>
    /// <returns>0..1.</returns>
    private float Health(ArchetypeStore own, int slot)
    {
        if (_hpField < 0 || _hpField >= own.Numbers.Length)
        {
            return 1f;
        }

        var hp = own.Numbers[_hpField];
        return hp == null ? 1f : (float)Math.Clamp(hp[slot], 0d, 1d);
    }

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
        _kindField = FieldOf(store, _structures, "kind");
        _regionField = FieldOf(store, _structures, "region");
        _hpField = FieldOf(store, IndexOf(store, Player), "hp");
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
