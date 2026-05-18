using NUnit.Framework;

namespace AntHill.Harness.Tests;

[TestFixture]
public sealed class AssertionEvaluatorTests
{
    private static RunMetrics Metrics(long ants = 1000, long cap = 1000, double p99 = 5.0, int exc = 0) => new()
    {
        TicksExecuted = 100,
        TickP50Ms = 2.0,
        TickP99Ms = p99,
        FinalAntCount = ants,
        ConfiguredAntCount = cap,
        ExceptionCount = exc,
    };

    [Test]
    public void Evaluate_NullAssertions_NoFailures()
    {
        Assert.That(AssertionEvaluator.Evaluate(null, Metrics()), Is.Empty);
    }

    [Test]
    public void Evaluate_ExceptionsWithinMax_Passes()
    {
        var a = new AssertionSettings { Exceptions = new BoundSpec { Max = 0 } };
        Assert.That(AssertionEvaluator.Evaluate(a, Metrics(exc: 0)), Is.Empty);
    }

    [Test]
    public void Evaluate_ExceptionsExceedMax_Fails()
    {
        var a = new AssertionSettings { Exceptions = new BoundSpec { Max = 0 } };
        var failures = AssertionEvaluator.Evaluate(a, Metrics(exc: 1));
        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("exceptions"));
    }

    [Test]
    public void Evaluate_AntCountInvariantHolds_Passes()
    {
        var a = new AssertionSettings { AntCount = new BoundSpec { Invariant = true } };
        Assert.That(AssertionEvaluator.Evaluate(a, Metrics(ants: 900, cap: 1000)), Is.Empty);
    }

    [Test]
    public void Evaluate_AntCountInvariantViolated_Fails()
    {
        var a = new AssertionSettings { AntCount = new BoundSpec { Invariant = true } };
        var failures = AssertionEvaluator.Evaluate(a, Metrics(ants: 1500, cap: 1000));
        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Does.Contain("invariant"));
    }

    [Test]
    public void Evaluate_TickP99ExceedsMax_Fails()
    {
        var a = new AssertionSettings { TickP99Ms = new BoundSpec { Max = 8.0 } };
        Assert.That(AssertionEvaluator.Evaluate(a, Metrics(p99: 12.0)), Has.Count.EqualTo(1));
    }
}
