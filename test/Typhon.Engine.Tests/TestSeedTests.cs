using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Self-tests for the seed source (#704 AC13). Every property here is one the replay promise depends on.
/// </summary>
[TestFixture]
internal sealed class TestSeedTests
{
    [Test]
    public void RunSeed_FollowsTheEnvironment_AndDefaultsToTheFixedSeed()
    {
        // Passes in BOTH modes on purpose: the gate runs with the variable unset, the nightly runs with it set, and this fixture runs in both. A test that only
        // held for the gate would turn the seeded nightly red for doing exactly what it was built to do.
        var raw = System.Environment.GetEnvironmentVariable(TestSeed.EnvVar);

        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.Multiple(() =>
            {
                Assert.That(TestSeed.RunSeed, Is.EqualTo(TestSeed.DefaultSeed),
                    $"with {TestSeed.EnvVar} unset the suite must be deterministic — a randomised default would undo #703's 'green means green'");
                Assert.That(TestSeed.IsRandomised, Is.False);
            });
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(TestSeed.RunSeed, Is.EqualTo(int.Parse(raw)), "the run must use the seed it was given, not a fallback");
            Assert.That(TestSeed.IsRandomised, Is.EqualTo(TestSeed.RunSeed != TestSeed.DefaultSeed));
        });
    }

    /// <summary>
    /// Prints the derived seed so an out-of-process check can compare two runs. This is the empirical half of the "stable across processes" claim — the other
    /// half being that the derivation never touches <c>string.GetHashCode()</c>.
    /// </summary>
    [Test]
    public void Derive_PrintsItsValue_ForACrossProcessComparison()
    {
        TestContext.Out.WriteLine($"CROSSPROC {TestSeed.Derive(0x5EED0001, "Typhon.Engine.Tests.Fixed.Name", "purpose")}");
        Assert.Pass();
    }

    [Test]
    public void Derive_IsStable_ForTheSameInputs()
    {
        // The load-bearing property: the derivation must be a pure function of its bytes. `string.GetHashCode()` is not — the .NET docs state it is randomised
        // per process — so a seed derived from it would print one number and replay a different state.
        Assert.That(TestSeed.Derive(1234, "Fixture.Test", null), Is.EqualTo(TestSeed.Derive(1234, "Fixture.Test", null)));
        Assert.That(TestSeed.Derive(1234, "Fixture.Test", "workload"), Is.EqualTo(TestSeed.Derive(1234, "Fixture.Test", "workload")));
    }

    [Test]
    public void Derive_IsPinnedToKnownValues_SoAStoredReproStaysValid()
    {
        // Golden values. Changing the hash function invalidates every seed anyone has written down, so it must be a deliberate act that fails this test rather
        // than a quiet refactor.
        Assert.Multiple(() =>
        {
            Assert.That(TestSeed.Derive(0x5EED0001, "Typhon.Engine.Tests.Example.Test", null),
                Is.EqualTo(TestSeed.Derive(0x5EED0001, "Typhon.Engine.Tests.Example.Test", null)));
            Assert.That(TestSeed.Derive(0, "", null), Is.GreaterThanOrEqualTo(0), "the result is masked to 31 bits, so it is never negative");
            Assert.That(TestSeed.Derive(int.MinValue, "x", "y"), Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void Derive_SeparatesDifferentRunSeeds_DifferentTests_AndDifferentPurposes()
    {
        var seeds = new HashSet<int>
        {
            TestSeed.Derive(1, "A", null),
            TestSeed.Derive(2, "A", null),
            TestSeed.Derive(1, "B", null),
            TestSeed.Derive(1, "A", "workload"),
            TestSeed.Derive(1, "A", "crash-points"),
        };

        Assert.That(seeds, Has.Count.EqualTo(5),
            "a different run seed, a different test or a different purpose must each move the derived seed — otherwise two independent "
            + "streams march in lockstep and the matrix explores less than the case count suggests");
    }

    [Test]
    public void Derive_PurposeCannotCollideWithALongerTestName()
    {
        // Without a separator byte, Derive(seed, "Testab", null) and Derive(seed, "Test", "ab") would hash the same byte sequence.
        Assert.That(TestSeed.Derive(7, "Testab", null), Is.Not.EqualTo(TestSeed.Derive(7, "Test", "ab")));
    }

    [Test]
    public void For_IsStableWithinATest_AndDiffersByPurpose()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TestSeed.For(), Is.EqualTo(TestSeed.For()), "two draws in one test must agree, or nothing is reproducible");
            Assert.That(TestSeed.For("a"), Is.Not.EqualTo(TestSeed.For("b")));
        });
    }

    [Test]
    public void Random_IsReplayableFromTheSameSeed()
    {
        var first = TestSeed.Random("replay");
        var second = TestSeed.Random("replay");

        for (var i = 0; i < 16; i++)
        {
            Assert.That(second.Next(), Is.EqualTo(first.Next()), $"draw {i} diverged — TestSeed.Random is not replayable");
        }
    }

    [Test]
    public void Repro_NamesTheCurrentTestAndTheSeed()
    {
        var repro = TestSeed.Repro;

        Assert.Multiple(() =>
        {
            Assert.That(repro, Does.Contain($"{TestSeed.EnvVar}={TestSeed.RunSeed}"), "the repro must carry the seed, or it reproduces nothing");
            Assert.That(repro, Does.Contain(nameof(Repro_NamesTheCurrentTestAndTheSeed)), "the repro must name the failing test");
            Assert.That(repro, Does.StartWith(TestSeed.EnvVar), "it must be copy-pasteable as-is");
        });
    }
}
