using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Marks a test as the falsifiable proof of a correctness rule from <c>rules/</c> (e.g. "LOG-02").
/// </summary>
/// <remarks>
/// The rule-coverage audit is <c>scripts/audit-rule-coverage.py</c> (#703), and it is now built and gating: it
/// cross-checks these attributes against the rule database in both directions and fails on an id that names no
/// rule, a rule whose <c>verified:</c> field names a test that does not exist, and — the point of the exercise —
/// a rule verified by a test that ships no <see cref="RuleMutantAttribute"/> companion.
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
internal sealed class VerifiesRuleAttribute : Attribute
{
    public VerifiesRuleAttribute(string ruleId) => RuleId = ruleId;

    public string RuleId { get; }
}

/// <summary>
/// Marks a test as the <b>genuineness proof</b> of a <see cref="VerifiesRuleAttribute"/>: it drives the verifier's
/// own assertion path with a deliberately rule-violating input and requires that assertion to FAIL.
/// </summary>
/// <remarks>
/// <para>
/// A rule whose verifier cannot fail is worse than a rule with no verifier, because it reports confidence. Four of
/// this suite's fifteen cited verifiers were in that state: LOG-06 asserts what the emitter puts on the wire, yet
/// both cited tests hand-construct their own wire bytes and round-trip them, so they stay green in the same build
/// as a red production probe (#389).
/// </para>
/// <para>
/// This generalises the discipline the merge gate already applies to the TLA+ specs — model-check the spec green,
/// then model-check a MUTANT and require a violation (<c>merge-gate.yml</c>, <c>run-tlc.sh --expect-violation</c>).
/// Use <see cref="RuleMutants.AssertDetects"/> to write the body; it enforces the same "positive evidence" rule
/// that script learned the hard way.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
internal sealed class RuleMutantAttribute : Attribute
{
    public RuleMutantAttribute(string ruleId) => RuleId = ruleId;

    public string RuleId { get; }
}
