using System;
using System.Globalization;
using System.Text;

namespace SwgTatooine;

public sealed partial class TatooineSim
{
    /// <summary>
    /// The realm directory this process publishes at <c>/typhon/demo.json</c>, as a JSON fragment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A realm directory is deliberately not a wire concept</b> (<c>12-realms.md</c> § 6: "tools list realms from
    /// <c>dbe.Realms</c>"), so this is an application surface rather than a subscriptions block. It goes beside the
    /// figures already served there, in the one document the browser client already fetches.
    /// </para>
    /// <para>
    /// <b>Planets and space are enumerated; interiors are described.</b> The shipping map has around six hundred
    /// enterable buildings per planet, so a two-planet run has more than twelve hundred interior realms — that is a range,
    /// not a list, and serving it as one would be a large document nobody could use in a menu. So the layout is published
    /// instead: the first interior realm and how many each planet has, which is the rule the server itself applies. A
    /// client computing an id from those two numbers is following a rule it was TOLD, which is a different thing from
    /// reconstructing one from command-line flags it cannot see — the case <see cref="RealmTag"/> exists to prevent.
    /// </para>
    /// <para>
    /// Every entry carries its <c>appTag</c> so a selector can show what a realm is before entering it, without a round
    /// trip and without the client duplicating <see cref="RealmTag"/>'s layout to build one.
    /// </para>
    /// </remarks>
    public string RealmDirectoryJson()
    {
        var json = new StringBuilder();
        json.Append("{\"planets\":[");
        for (var planet = 0; planet < _config.Planets; planet++)
        {
            if (planet > 0)
            {
                json.Append(',');
            }

            // Planet 0 carries no RealmReplicationConfig of its own — it is ConfigureSpatialGrid's realm — so the engine
            // sends AppTag 0 for it, and RealmTag.Planet(0) is 0 by construction. Computing it here rather than special-
            // casing keeps the directory and the wire agreeing however that default changes.
            json.Append(CultureInfo.InvariantCulture, $"{{\"id\":{planet},\"appTag\":{RealmTag.Planet(planet)}}}");
        }

        json.Append("],\"interiors\":{\"first\":")
            .Append(_config.Planets.ToString(CultureInfo.InvariantCulture))
            .Append(",\"perPlanet\":")
            .Append(InteriorsPerPlanet.ToString(CultureInfo.InvariantCulture))
            .Append('}');

        if (SpaceRealm >= 0)
        {
            json.Append(CultureInfo.InvariantCulture, $",\"space\":{{\"id\":{SpaceRealm},\"appTag\":{RealmTag.Space()}}}");
        }

        // <b>Dungeons are deliberately absent.</b> Publishing their id range was worse than publishing nothing: a
        // dungeon realm is registered only while a party is inside one, so every entry would be true when the document
        // was fetched and refused when it was clicked — and `ViewableRealms` excludes them for exactly that reason, so
        // the directory would have been advertising realms the command is guaranteed to reject. A dungeon watcher needs
        // its own way of knowing which ones are live, and that is its own feature.
        json.Append('}');
        return json.ToString();
    }

    /// <summary>The most realms one inventory document describes, before it starts counting them instead of listing them.</summary>
    /// <remarks>
    /// A shipping map has around six hundred enterable buildings per planet and every one of them is a realm, so an
    /// inventory that listed them all would be a document nobody could read and one that grows with the world. The cap
    /// is on what a viewer can take in, not on what the server can produce.
    /// </remarks>
    public const int RealmInventoryMaxRows = 64;

    /// <summary>
    /// What every realm is doing right now, and what is standing in it, as the JSON served at <c>/typhon/realms.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the dormancy proof, and the row policy is the proof rather than a concession to document size.</b>
    /// Planets, space and live dungeons are always listed — there are few and a planet never sleeps. Interiors are listed
    /// only while they are NOT dormant, which is exactly the ones somebody is in: a row appears when a bot walks through a
    /// door and falls off the list once the room has been empty for <c>--interior-sleep</c>. Everything else is one
    /// number, because "1 197 interiors asleep" is the claim being demonstrated.
    /// </para>
    /// <para>
    /// <b>The state is live and the population is a second or so old, and the document says so.</b> <c>StateOf</c> reads
    /// the policy's own state off the tick it last evaluated, from any thread; the population comes from the last census
    /// walk, which happens on the tick thread at <see cref="SimConfig.RealmCensusHz"/>. Publishing the two under one
    /// timestamp would be the quieter half of a lie, so each carries its own.
    /// </para>
    /// <para>
    /// Written by hand with a <see cref="StringBuilder"/>, as <see cref="RealmDirectoryJson"/> is and for the same reason:
    /// the slim builder's source-generated serialization would want a context for a document this endpoint builds once a
    /// second and throws away.
    /// </para>
    /// </remarks>
    public string RealmInventoryJson()
    {
        var census = RealmPopulation.Current;

        // Counts walks its own snapshot of the registered realms and then asks each one for its state, so a realm
        // unregistered between the two makes it throw — on a Kestrel thread, once a second, for the life of the run.
        // A poll that arrives as a dungeon closes reports no aggregate rather than a 500.
        var counts = default(RealmStateCounts);
        try
        {
            counts = Dbe.Realms.Counts;
        }
        catch (InvalidOperationException)
        {
            // A realm closed under the walk. The next poll is a second away.
        }

        var json = new StringBuilder(1024);
        json.Append(CultureInfo.InvariantCulture,
            $"{{\"tick\":{Runtime?.CurrentTickNumber ?? 0},\"censusTick\":{census.Tick},\"maxRows\":{RealmInventoryMaxRows}");
        json.Append(CultureInfo.InvariantCulture,
            $",\"counts\":{{\"active\":{counts.Active},\"simulated\":{counts.Simulated},\"dormant\":{counts.Dormant}"
            + $",\"closing\":{counts.Closing},\"divided\":{counts.Divided}}}");

        var rows = 0;
        var omitted = 0;
        var body = new StringBuilder(1024);

        void Row(int realm, uint appTag, RealmRunState state, int divisor, int sleepAfterTicks)
        {
            if (rows >= RealmInventoryMaxRows)
            {
                omitted++;
                return;
            }

            var population = census.Of((ushort)realm);
            if (rows > 0)
            {
                body.Append(',');
            }

            // The generation beside the id, because a realm's identity is the PAIR (12-realms § 1.1): ids are reused once a realm is retired, so a
            // client comparing rows by id alone can take a reused id for the realm it replaced. It is the same number the REALM block puts on the wire,
            // so the panel and the session agree about which incarnation they are naming.
            body.Append(CultureInfo.InvariantCulture,
                $"{{\"id\":{realm},\"generation\":{Dbe.Realms.GenerationOf(new RealmId((ushort)realm))}"
                + $",\"appTag\":{appTag},\"state\":\"{StateName(state)}\",\"divisor\":{divisor}"
                + $",\"sleepAfterTicks\":{sleepAfterTicks},\"players\":{population.Players},\"npcs\":{population.Npcs}"
                + $",\"creatures\":{population.Creatures},\"structures\":{population.Structures}}}");
            rows++;
        }

        for (var planet = 0; planet < _config.Planets; planet++)
        {
            // Planet 0 is the measured workload and runs at full rate always; the others carry --planet-divisor.
            if (TryStateOf(planet, out var state))
            {
                Row(planet, RealmTag.Planet(planet), state, planet == 0 ? 1 : Math.Max(1, _config.PlanetDivisor), 0);
            }
        }

        if (SpaceRealm >= 0 && TryStateOf(SpaceRealm, out var spaceState))
        {
            Row(SpaceRealm, RealmTag.Space(), spaceState, Math.Max(1, _config.SpaceDivisor), 0);
        }

        // <b>Before the interiors, not after them, and that is the row cap talking.</b> The cap is applied in the order
        // rows arrive, so with dungeons last a busy world — more awake interiors than the cap — would silently put every
        // live dungeon in `omitted`. A dungeon exists only while a party is inside one, which makes it the least
        // droppable row in the document.
        for (var slot = 0; slot < _config.Dungeons; slot++)
        {
            if (TryStateOf(FirstDungeonRealm + slot, out var state))
            {
                Row(FirstDungeonRealm + slot, RealmTag.Dungeon(slot), state, 1, Math.Max(1, _config.TickRateHz));
            }
        }

        // Interiors: the awake ones, which is the point. A dormant one is in the `dormant` count above and nowhere else.
        var interiorSleepTicks = _config.InteriorSleepS > 0f ? Math.Max(1, (int)(_config.InteriorSleepS * _config.TickRateHz)) : 0;
        for (var planet = 0; planet < _config.Planets && InteriorsPerPlanet > 0; planet++)
        {
            var first = _config.Planets + (planet * InteriorsPerPlanet);
            for (var realm = first; realm < first + InteriorsPerPlanet; realm++)
            {
                // The state read ONCE and carried into the row. Read again inside Row it could have changed in between,
                // and the document would then carry a row saying "dormant" from a loop whose whole job is to exclude
                // exactly those.
                if (TryStateOf(realm, out var state) && state != RealmRunState.Dormant)
                {
                    Row(realm, RealmTag.Interior(realm - first), state, 1, interiorSleepTicks);
                }
            }
        }

        json.Append(CultureInfo.InvariantCulture, $",\"omitted\":{omitted},\"realms\":[").Append(body).Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// What realm <paramref name="realm"/> is doing, or <see langword="false"/> when it is not there to ask.
    /// </summary>
    /// <remarks>
    /// <b>A catch rather than an <c>IsRegistered</c> check, because the check cannot be made atomic from out here.</b>
    /// A dungeon is unregistered at the first fence that finds it empty, on the tick thread, and a check-then-ask from a
    /// Kestrel thread has a window between the two however short it is written. <c>StateOf</c> throws for a realm that is
    /// gone, so the throw IS the answer: a realm that vanished mid-document is simply absent from it, which is what the
    /// next poll would say anyway.
    /// </remarks>
    private bool TryStateOf(int realm, out RealmRunState state)
    {
        try
        {
            state = Dbe.Realms.StateOf(new RealmId((ushort)realm));
            return true;
        }
        catch (InvalidOperationException)
        {
            state = RealmRunState.Closing;
            return false;
        }
    }

    /// <summary>The run state as the client names it: lower case, and the same four words the engine's enum uses.</summary>
    /// <remarks>
    /// Spelled here rather than with <c>ToString</c> so a rename of the engine's enum member is a compile error in this
    /// file instead of a silent change to a string the browser branches on.
    /// </remarks>
    private static string StateName(RealmRunState state) => state switch
    {
        RealmRunState.Active => "active",
        RealmRunState.Simulated => "simulated",
        RealmRunState.Dormant => "dormant",
        RealmRunState.Closing => "closing",
        _ => "unknown",
    };
}
