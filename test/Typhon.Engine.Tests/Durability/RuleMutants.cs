using System;
using NUnit.Framework;
using NUnit.Framework.Internal;

namespace Typhon.Engine.Tests;

/// <summary>
/// The helper behind <see cref="RuleMutantAttribute"/>: runs a deliberately rule-violating scenario through a
/// verifier's own assertion path and requires that assertion to reject it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "positive evidence" and not just "it threw".</b> <c>run-tlc.sh</c> learned this the expensive way, and
/// its comment is worth restating because the trap is identical here: its <c>--expect-violation</c> mode originally
/// accepted <i>anything that was not green</i>, so a parse error, an undeclared constant, an OOM or a failed jar
/// download all satisfied the genuineness check — letting it pass without the invariant ever being evaluated. The
/// same hole in C# would be <c>Assert.Throws&lt;AssertionException&gt;</c>: a null-reference in the mutant's setup,
/// or an unrelated pre-condition assert firing first, would "prove" a verifier genuine that never ran.
/// </para>
/// <para>
/// So a mutant counts only when the failure carries the verifier's OWN message marker. That is the C# equivalent of
/// TLC printing the violated invariant's name.
/// </para>
/// </remarks>
internal static class RuleMutants
{
    /// <summary>
    /// Asserts that <paramref name="violatingScenario"/> — a scenario that breaks <paramref name="ruleId"/> — is
    /// rejected by the verifier's own assertion, identified by <paramref name="expectedFailureMarker"/>.
    /// </summary>
    /// <param name="ruleId">The rule from <c>claude/rules/</c> whose verifier is being proven falsifiable.</param>
    /// <param name="expectedFailureMarker">
    /// A distinctive substring of the message the VERIFIER emits when it rejects. This is the positive evidence:
    /// it must come from the assertion under test, not from the mutant's own scaffolding, or the check degenerates
    /// into "something went wrong somewhere".
    /// </param>
    /// <param name="violatingScenario">Drives the verifier with an input that violates the rule.</param>
    public static void AssertDetects(string ruleId, string expectedFailureMarker, Action violatingScenario)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        ArgumentNullException.ThrowIfNull(expectedFailureMarker);
        ArgumentNullException.ThrowIfNull(violatingScenario);

        // NUnit RECORDS every Assert.Fail into the running test's result, independently of the exception it throws.
        // Catching the AssertionException is therefore not enough: the mutant's DELIBERATE failure would still be
        // replayed at teardown as "Multiple failures or warnings in test", failing the very test that proves the
        // mutant was rejected — precisely backwards. And the record list is a ReadOnlyCollection, so it cannot be
        // trimmed after the fact. Instead, give the scenario a scratch TestResult to record into and restore the
        // real one afterwards: the mutant's assertions belong to the MUTANT, not to this test.
        var context = TestExecutionContext.CurrentContext;
        var realResult = context?.CurrentResult;
        if (realResult != null && context.CurrentTest != null)
        {
            context.CurrentResult = context.CurrentTest.MakeTestResult();
        }

        string failure = null;
        try
        {
            violatingScenario();

            failure =
                $"Rule {ruleId}: the verifier ACCEPTED a scenario that violates the rule — it cannot fail, so its "
                + "green result is not evidence of anything. Either the verifier asserts something weaker than the "
                + "rule states (LOG-06's hand-constructed wire bytes are the canonical example), or this mutant "
                + "does not actually violate the rule. Fix whichever is true; do not delete the mutant.";
        }
        catch (SuccessException)
        {
            // Assert.Pass() — the scenario declared success, which IS the verifier accepting the violation.
            failure =
                $"Rule {ruleId}: the verifier ACCEPTED a scenario that violates the rule (it called Assert.Pass) — "
                + "it cannot fail, so its green result is not evidence of anything.";
        }
        catch (AssertionException ex)
        {
            if (!ex.Message.Contains(expectedFailureMarker, StringComparison.Ordinal))
            {
                failure =
                    $"Rule {ruleId}: the mutant failed, but NOT on the verifier's own assertion. Expected the "
                    + $"failure message to contain '{expectedFailureMarker}'; it was:{Environment.NewLine}"
                    + $"{ex.Message}{Environment.NewLine}A mutant that fails for an unrelated reason proves nothing "
                    + "about the verifier — it is the C# form of TLC 'not green' being mistaken for 'invariant "
                    + "violated'.";
            }

            // else: the verifier rejected the violation, and did so for the stated reason. This is the pass.
        }
        catch (Exception ex)
        {
            failure =
                $"Rule {ruleId}: the mutant threw {ex.GetType().Name} instead of failing the verifier's assertion. "
                + $"That is a broken mutant (it crashed before the verifier ran), not evidence the verifier can "
                + $"fail.{Environment.NewLine}{ex}";
        }
        finally
        {
            if (realResult != null)
            {
                context.CurrentResult = realResult;
            }
        }

        if (failure != null)
        {
            Assert.Fail(failure);
        }
    }
}
