using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// A7: the <c>human</c> profile is <c>player-lite</c> plus the buildings, and a player session is given it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted against the declared profiles, because that is the only place a profile exists.</b> Checked while writing this:
/// profiles are NOT in the catalog a client negotiates — the catalog carries archetypes, commands, events and metrics, and nothing
/// about who is served what. A profile is server-side policy, so <c>SubscriptionsRegistry.Profiles</c> is the whole observable
/// surface, and <c>ObserverDeclaration.Archetypes</c> is the set this item is about.
/// </para>
/// <para>
/// <b>That absence is itself worth knowing</b>, because it settles how a client could ever ask for a cheaper view: not by naming a
/// profile, which it cannot see, but by saying something about itself in <c>HELLO</c> that the admission hook reads. That is A8's
/// wire and not this.
/// </para>
/// <para>
/// <b>What this cannot show</b> is a building arriving in a frame. That needs a frame decoder this suite does not have, and it is
/// what A-1 — walk around in a browser — verifies in one second.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class HumanProfileChecks
{
    private string _dir;
    private TatooineSim _sim;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _sim.StartReplication();
    }

    [TearDown]
    public void TearDown()
    {
        _sim?.Dispose();
        _sim = null;
        TatooineReplication.ResetSessionAccounting();
        Worlds.Delete(_dir);
    }

    /// <summary>Every archetype any observer of a profile carries, across its base shape and its realm variants.</summary>
    /// <param name="profile">The profile's name.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    /// Unioned over the observers rather than read off the first, so the assertion does not depend on whether a realm variant is a
    /// second observer in one declaration or something else. What A7 claims is "this profile serves buildings", and the union is
    /// exactly that claim.
    /// </remarks>
    private HashSet<Type> ArchetypesOf(string profile)
    {
        var declaration = _sim.Runtime.Subscriptions.Profiles.FirstOrDefault(p => p.Name == profile);
        Assert.That(declaration, Is.Not.Null, $"profile '{profile}' is not declared");

        var set = new HashSet<Type>();
        foreach (var observer in declaration.Observers)
        {
            foreach (var archetype in observer.Archetypes)
            {
                set.Add(archetype);
            }
        }

        return set;
    }

    /// <summary>All three profiles are declared, whichever one a run serves.</summary>
    /// <remarks>
    /// <c>human</c> is declared unconditionally rather than only when <c>PlayerProfileName</c> selects it. A profile that appeared
    /// only in some runs would make two otherwise-identical servers differ in their declarations, and a measurement run would then
    /// be comparing more than the one thing it meant to change.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AllThreePlayerFacingProfilesAreDeclared()
    {
        var names = _sim.Runtime.Subscriptions.Profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain(TatooineReplication.HumanProfile));
            Assert.That(names, Does.Contain(TatooineReplication.PlayerProfile), "the measurement profile must stay declared");
            Assert.That(names, Does.Contain(TatooineReplication.SpectateProfile));
        });
    }

    /// <summary>
    /// The human profile carries <c>WorldObject</c> and the measurement profile does not — which is the whole of A7.
    /// </summary>
    /// <remarks>
    /// <b>The DIFFERENCE is the assertion, not the presence.</b> A case that only checked <c>human</c> carries <c>WorldObject</c>
    /// would pass if somebody "fixed" <c>player-lite</c> by adding it there too — and that is the change the measurement comment
    /// exists to prevent, because it puts a moving sphere's crescent sweep over one more archetype on every simulated player instead
    /// of on the handful of humans.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void TheHumanProfileCarriesBuildingsAndTheMeasurementProfileDoesNot()
    {
        var human = ArchetypesOf(TatooineReplication.HumanProfile);
        var lite = ArchetypesOf(TatooineReplication.PlayerProfile);

        Assert.Multiple(() =>
        {
            Assert.That(human, Does.Contain(typeof(WorldObject)), "a human who cannot see buildings is standing in an empty desert");
            Assert.That(lite, Does.Not.Contain(typeof(WorldObject)),
                "the measurement profile gained WorldObject, which puts a crescent sweep over it on every simulated player and makes every CPU figure "
                + "taken before this change incomparable with every one taken after it");

            // "player-lite PLUS buildings": the rest of the set has to be the same, or this is a different profile that happens to have buildings.
            Assert.That(human, Is.SupersetOf(lite), "the human profile must be the player's shape plus something, not instead of it");
            Assert.That(human.Count, Is.EqualTo(lite.Count + 1), "it must be plus exactly ONE archetype — the buildings");
        });
    }

    /// <summary>
    /// The spectate profile and the human profile carry the same set, and are still two declarations.
    /// </summary>
    /// <remarks>
    /// <b>Identical today and deliberately not merged.</b> They are the same shape for different reasons — a spectator rides somebody
    /// else's entity and a player drives its own — and <c>Spectate</c>'s own commentary turns on what <c>Control</c> leaks to a
    /// watcher. The day a <c>Player</c> declares an <c>[Owner]</c> field they diverge: the watcher must stop seeing it and the player
    /// must not. One shared name would make that a rename under pressure, so this case pins the equality as a FACT about today rather
    /// than as a reason to collapse them.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void TheSpectateAndHumanProfilesAgreeToday_AndAreStillSeparate()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ArchetypesOf(TatooineReplication.HumanProfile), Is.EquivalentTo(ArchetypesOf(TatooineReplication.SpectateProfile)));
            Assert.That(TatooineReplication.HumanProfile, Is.Not.EqualTo(TatooineReplication.SpectateProfile),
                "two profiles with one name cannot diverge when Player declares an [Owner] field");
        });
    }

    /// <summary>A player session is served the human profile by default, and the switch is what a measurement run flips.</summary>
    /// <remarks>
    /// The default is the decision. Phase B's bots connect as <c>player</c> and the server must not be able to tell them from a
    /// person, so it cannot serve them something cheaper — the moment it could, the profile would be the thing that distinguished
    /// them. Either everybody pays for buildings or nobody does, and the browser is what this phase is for.
    /// </remarks>
    [Test]
    public void APlayerSessionIsServedTheHumanProfileByDefault()
    {
        Assert.That(TatooineReplication.PlayerProfileName, Is.EqualTo(TatooineReplication.HumanProfile));

        TatooineReplication.PlayerProfileName = TatooineReplication.PlayerProfile;
        Assert.That(TatooineReplication.PlayerProfileName, Is.EqualTo(TatooineReplication.PlayerProfile),
            "a measurement run must be able to ask for the old shape");

        TatooineReplication.ResetSessionAccounting();
        Assert.That(TatooineReplication.PlayerProfileName, Is.EqualTo(TatooineReplication.HumanProfile),
            "the reset must put the default back, or one fixture's measurement setting becomes the next fixture's");
    }
}
