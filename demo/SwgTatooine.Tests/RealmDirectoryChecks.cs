using System.IO;
using NUnit.Framework;

namespace SwgTatooine.Tests;

/// <summary>
/// What <c>/typhon/demo.json</c> tells a client about this world's realms.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other half of a contract that was only held from one side.</b> The <c>AppTag</c> layout is pinned by
/// hand-written literals in both languages (<see cref="RealmTagChecks"/> and the client's <c>realm-view.test.ts</c>),
/// which is sound. The DIRECTORY was not: the client's parser was tested against a fixture written by hand, so nothing
/// asserted that this server emits the document that fixture was copied from. A field renamed here would have passed
/// every test on both sides and broken the realm selector in the browser.
/// </para>
/// <para>
/// It is asserted as an exact string on purpose. The document is built by hand with a <c>StringBuilder</c> and no
/// serializer, so its shape — the commas, the nesting, which keys are present — is the thing that can break, and a
/// parse-then-check-fields test would accept a document the client's own parser rejects.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RealmDirectoryChecks
{
    [Test]
    public void ThreePlanetsWithInteriorsAndSpace_PublishTheLayoutTheClientParses()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Planets = 3;
            config.Interiors = true;
            config.Space = true;
            config.Dungeons = 2;

            using var sim = new TatooineSim(config);
            sim.Initialize();

            // Every number here is derived from the world that was actually built, not copied from a run: the point is
            // the SHAPE of the document and that space sits immediately after the last interior, which is the whole
            // realm layout expressed in one arithmetic identity the client also applies.
            var n = sim.InteriorsPerPlanet;
            var space = 3 + (3 * n);
            var expected =
                "{\"planets\":[{\"id\":0,\"appTag\":0},{\"id\":1,\"appTag\":1048577},{\"id\":2,\"appTag\":2097154}]," +
                $"\"interiors\":{{\"first\":3,\"perPlanet\":{n}}}," +
                $"\"space\":{{\"id\":{space},\"appTag\":536870912}}}}";

            Assert.That(sim.RealmDirectoryJson(), Is.EqualTo(expected));
            Assert.That(sim.SpaceRealm, Is.EqualTo(space), "the identity the client computes must hold on this side too");
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    [Test]
    public void ASingleFlatWorld_StillPublishesAWellFormedDocument()
    {
        // The default run: one planet, no interiors, no space. The client treats a directory missing `space` as valid
        // and one missing `interiors` as unusable, so the degenerate shape has to stay well formed rather than
        // collapsing to something its parser rejects and reports as "no realms at all".
        var dir = Worlds.NewDirectory();
        try
        {
            using var sim = new TatooineSim(Worlds.Small(dir));
            sim.Initialize();

            Assert.That(
                sim.RealmDirectoryJson(),
                Is.EqualTo("{\"planets\":[{\"id\":0,\"appTag\":0}],\"interiors\":{\"first\":1,\"perPlanet\":0}}"));
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    [Test]
    public void NoDungeonIsAdvertised_BecauseNoneOfThemCanBeEntered()
    {
        // `ViewableRealms` stops below the dungeon slots, so a dungeon in the directory would be an entry the command
        // is guaranteed to refuse. The two facts live in different files; this is what keeps them agreeing.
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Dungeons = 4;

            using var sim = new TatooineSim(config);
            sim.Initialize();

            Assert.That(sim.RealmDirectoryJson(), Does.Not.Contain("dungeon"));
            Assert.That(TatooineReplication.ViewableRealms, Is.LessThanOrEqualTo(sim.FirstDungeonRealm));
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
