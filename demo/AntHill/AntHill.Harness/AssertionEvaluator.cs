using System.Collections.Generic;

namespace AntHill.Harness;

/// <summary>
/// Evaluates a scenario's assertions against the metrics of a completed run. Pure logic, independently testable.
/// </summary>
public static class AssertionEvaluator
{
    /// <summary>Returns the list of assertion failures — empty when every assertion holds.</summary>
    public static IReadOnlyList<string> Evaluate(AssertionSettings assertions, RunMetrics metrics)
    {
        var failures = new List<string>();
        if (assertions == null)
        {
            return failures;
        }

        CheckBound(failures, "exceptions", assertions.Exceptions, metrics.ExceptionCount);
        CheckBound(failures, "tickP99Ms", assertions.TickP99Ms, metrics.TickP99Ms);
        CheckBound(failures, "antCount", assertions.AntCount, metrics.FinalAntCount);

        // antCount invariant: the live ant count can never exceed the configured spawn cap —// a true invariant that holds regardless of run-to-run nondeterminism.
        if (assertions.AntCount is { Invariant: true } && metrics.FinalAntCount > metrics.ConfiguredAntCount)
        {
            failures.Add(
                $"antCount invariant violated: live count {metrics.FinalAntCount} "
                + $"exceeds spawn cap {metrics.ConfiguredAntCount}");
        }

        return failures;
    }

    private static void CheckBound(List<string> failures, string name, BoundSpec spec, double value)
    {
        if (spec == null)
        {
            return;
        }
        if (spec.Max.HasValue && value > spec.Max.Value)
        {
            failures.Add($"{name} = {value:G6} exceeds max {spec.Max.Value:G6}");
        }
        if (spec.Min.HasValue && value < spec.Min.Value)
        {
            failures.Add($"{name} = {value:G6} below min {spec.Min.Value:G6}");
        }
    }
}
