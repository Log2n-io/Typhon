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
}
