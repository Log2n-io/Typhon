using System;

namespace Typhon.Engine.Tests;

/// <summary>
/// Marks a test as the falsifiable proof of a correctness rule from <c>claude/rules/</c> (e.g. "LOG-02").
/// The rule-coverage audit (08 T-3 / P0.4 — not yet built) will fail CI if a <c>[fatal]</c>/<c>[silent]</c> rule
/// has no <see cref="VerifiesRuleAttribute"/> test. Harmless until then; applied now so the matrix is seeded.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
internal sealed class VerifiesRuleAttribute : Attribute
{
    public VerifiesRuleAttribute(string ruleId) => RuleId = ruleId;

    public string RuleId { get; }
}
