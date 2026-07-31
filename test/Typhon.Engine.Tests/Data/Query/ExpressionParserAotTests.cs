using NUnit.Framework;
using System;
using System.Linq.Expressions;

namespace Typhon.Engine.Tests;

/// <summary>
/// Locks in the predicate shapes that <see cref="ExpressionParser.EvaluateConstant"/> must resolve WITHOUT falling back
/// to <c>Expression.Lambda(...).Compile().DynamicInvoke()</c>.
///
/// <para><b>Why this fixture exists (#409).</b> That fallback needs runtime code generation, so under Native AOT it is
/// gated off and throws a <see cref="NotSupportedException"/> instead. These tests are therefore not cosmetic: any
/// predicate shape that reaches the fallback works on CoreCLR and <i>fails on a published native binary</i>. Asserting
/// the fast paths here is what stops a refactor from silently narrowing them and breaking AOT consumers — a regression
/// no amount of CoreCLR testing would surface.</para>
///
/// <para>The fallback itself stays reachable on CoreCLR (unchanged behaviour there); the contract this fixture pins is
/// "the common shapes never need it".</para>
/// </summary>
class ExpressionParserAotTests
{
    [Test]
    public void LiteralConstant_ResolvesWithoutCompiling()
    {
        var predicates = ExpressionParser.Parse<CompD>(p => p.B == 42);

        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].Value, Is.EqualTo(42));
    }

    [Test]
    public void CapturedLocal_ResolvesWithoutCompiling()
    {
        // The single most common real-world shape: a local hoisted into a compiler-generated display class, which the
        // parser reads as MemberAccess(ConstantExpression) rather than compiling.
        var threshold = 7;
        var predicates = ExpressionParser.Parse<CompD>(p => p.B > threshold);

        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThan));
        Assert.That(predicates[0].Value, Is.EqualTo(7));
    }

    [Test]
    public void CapturedLocal_WithNumericPromotion_ResolvesWithoutCompiling()
    {
        // A widening Convert wrapper is inserted by the compiler when the operand types differ; the parser must strip
        // it rather than treat the whole node as a computed expression.
        short narrow = 9;
        var predicates = ExpressionParser.Parse<CompD>(p => p.B >= narrow);

        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].Operator, Is.EqualTo(CompareOp.GreaterThanOrEqual));
        Assert.That(Convert.ToInt64(predicates[0].Value), Is.EqualTo(9L));
    }

    [Test]
    public void HoistedComputedValue_ResolvesWithoutCompiling()
    {
        // The documented workaround the AOT NotSupportedException tells users to apply: compute into a local FIRST, so
        // the predicate sees a captured variable instead of an arithmetic node. Proving it works is what makes that
        // error message actionable rather than a dead end.
        var a = 20;
        var b = 3;
        var limit = a + b;
        var predicates = ExpressionParser.Parse<CompD>(p => p.B < limit);

        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].Value, Is.EqualTo(23));
    }

    [Test]
    public void CapturedField_ResolvesWithoutCompiling()
    {
        var holder = new Holder { Limit = 15 };
        var predicates = ExpressionParser.Parse<CompD>(p => p.B <= holder.Limit);

        Assert.That(predicates, Has.Length.EqualTo(1));
        Assert.That(predicates[0].Value, Is.EqualTo(15));
    }

    [Test]
    public void DnfBranches_WithCapturedLocals_ResolveWithoutCompiling()
    {
        // WhereField normalizes to DNF before the indexed scan; each leaf goes through the same constant evaluation.
        var low = 5;
        var high = 50;
        var branches = ExpressionParser.ParseDnf<CompD>(p => p.B < low || p.B > high);

        Assert.That(branches, Has.Length.EqualTo(2));
        Assert.That(branches[0][0].Value, Is.EqualTo(5));
        Assert.That(branches[1][0].Value, Is.EqualTo(50));
    }

    private sealed class Holder
    {
        public int Limit;
    }
}
