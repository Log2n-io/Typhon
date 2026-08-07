using System;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// The mutant harness's own mutants. <see cref="RuleMutants.AssertDetects"/> exists to stop a verifier being
/// trusted without evidence, so it would be self-defeating to trust IT without evidence: these tests plant each
/// way a mutant can be wrong and require the helper to reject it.
/// </summary>
[TestFixture]
internal sealed class RuleMutantsTests
{
    private const string Marker = "differential oracle rejected";

    [Test]
    public void GenuineRejection_ByTheVerifiersOwnAssertion_Passes()
    {
        RuleMutants.AssertDetects("XX-01", Marker, () => Assert.Fail($"{Marker}: value bytes differ"));
    }

    [Test]
    public void VerifierThatAcceptsTheViolation_IsReported()
    {
        // The LOG-06 shape: the "verifier" runs, asserts something weaker than the rule, and stays green.
        var ex = Assert.Throws<AssertionException>(
            () => RuleMutants.AssertDetects("XX-01", Marker, () => Assert.Pass()));

        Assert.That(ex.Message, Does.Contain("ACCEPTED a scenario that violates the rule"));
        Assert.That(ex.Message, Does.Contain("Assert.Pass"), "the accept mode must be named, not merged into one message");
    }

    [Test]
    public void MutantThatDoesNothingAtAll_IsReported()
    {
        var ex = Assert.Throws<AssertionException>(
            () => RuleMutants.AssertDetects("XX-01", Marker, () => { }));

        Assert.That(ex.Message, Does.Contain("cannot fail"));
    }

    [Test]
    public void AssertionFailure_WithoutTheVerifiersMarker_DoesNotCount()
    {
        // The run-tlc.sh trap in C# form: an UNRELATED assert fires first, so the verifier never ran, yet a naive
        // Assert.Throws<AssertionException> would have called this a genuine rejection.
        var ex = Assert.Throws<AssertionException>(
            () => RuleMutants.AssertDetects("XX-01", Marker, () => Assert.Fail("precondition: fixture not seeded")));

        Assert.That(ex.Message, Does.Contain("NOT on the verifier's own assertion"));
        Assert.That(ex.Message, Does.Contain("precondition: fixture not seeded"), "the real failure must be shown");
    }

    [Test]
    public void SetupCrash_IsNotEvidence()
    {
        var ex = Assert.Throws<AssertionException>(
            () => RuleMutants.AssertDetects("XX-01", Marker, () => throw new InvalidOperationException("boom")));

        Assert.That(ex.Message, Does.Contain("broken mutant"));
        Assert.That(ex.Message, Does.Contain("InvalidOperationException"));
    }

    [Test]
    public void MarkerMatchIsOrdinal_NotCaseInsensitive()
    {
        // A marker is copied from the verifier's message; matching loosely would let a near-miss pass and slowly
        // decouple the mutant from the assertion it is supposed to pin.
        Assert.Throws<AssertionException>(
            () => RuleMutants.AssertDetects("XX-01", Marker, () => Assert.Fail("DIFFERENTIAL ORACLE REJECTED")));
    }

    [Test]
    public void NullArguments_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => RuleMutants.AssertDetects(null, Marker, () => { }));
        Assert.Throws<ArgumentNullException>(() => RuleMutants.AssertDetects("XX-01", null, () => { }));
        Assert.Throws<ArgumentNullException>(() => RuleMutants.AssertDetects("XX-01", Marker, null));
    }
}
