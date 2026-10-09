using NUnit.Framework;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Services.Querying;

namespace Typhon.Workbench.Tests;

/// <summary>
/// WB-06 / #1083 rung 4 — the Query Console stops silently answering realm 0.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a bug in a shipped feature, not a new capability.</b> <c>QuerySpecCompiler.ApplySpatial</c> emitted
/// <c>WhereNearby</c> / <c>WhereInAABB</c> / <c>WhereRay</c> and never called <c>InRealm</c>, whose own documentation
/// reads "Realm 0 when never called". So every SPATIAL query the Console has run since #386 searched realm 0 whatever
/// the author meant — right by accident on a single-realm database, and an unexplained empty result on any other.
/// </para>
/// <para>
/// The parser half is tested here; the compiler's refusal needs a live multi-realm engine and is covered where the
/// other compiler cases are.
/// </para>
/// </remarks>
[TestFixture]
public sealed class QueryConsoleRealmScopeTests
{
    private static SpatialClauseDto ParseOneSpatial(string dsl)
    {
        var result = DslParser.Parse(dsl);
        Assert.That(result.Errors, Is.Empty, $"unexpected parse errors: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        Assert.That(result.Spec.Spatial, Has.Length.EqualTo(1), "the DSL should have produced exactly one spatial stage");
        return result.Spec.Spatial[0];
    }

    [Test]
    public void ASpatialStageWithNoRealmLeavesTheRealmUnnamed()
    {
        var clause = ParseOneSpatial("FROM Player\nSPATIAL Position NEARBY 0, 0, 0 RADIUS 10");

        Assert.That(clause.Realm, Is.Null, "an unnamed realm must stay unnamed, so the compiler can refuse rather than guess");
    }

    [TestCase("FROM Player\nSPATIAL Position NEARBY 0, 0, 0 RADIUS 10 IN REALM 7", TestName = "Nearby")]
    [TestCase("FROM Player\nSPATIAL Position AABB 0, 0, 0, 64, 64, 0 IN REALM 7", TestName = "Aabb")]
    [TestCase("FROM Player\nSPATIAL Position RAY 0, 0, 0, 1, 0, 0, 100 IN REALM 7", TestName = "Ray")]
    public void EverySpatialShapeAcceptsInRealm(string dsl)
    {
        Assert.That(ParseOneSpatial(dsl).Realm, Is.EqualTo(7));
    }

    [Test]
    public void RealmZeroIsAChoiceAndNotTheAbsenceOfOne()
    {
        // The distinction the whole change rests on: "IN REALM 0" means the author picked realm 0, while an absent
        // clause means they said nothing. Parsing the first as null would put us straight back in the silent default.
        Assert.That(ParseOneSpatial("FROM Player\nSPATIAL Position NEARBY 0, 0, 0 RADIUS 10 IN REALM 0").Realm, Is.EqualTo(0));
    }

    [Test]
    public void InWithoutRealmIsRejectedWithAUsableMessage()
    {
        var result = DslParser.Parse("FROM Player\nSPATIAL Position NEARBY 0, 0, 0 RADIUS 10 IN 7");

        Assert.That(result.Errors, Is.Not.Empty);
        Assert.That(string.Join(" ", result.Errors.Select(e => e.Message)), Does.Contain("IN REALM"),
            "the error should name the syntax it wanted, not merely reject the token");
    }

    [TestCase("-1")]
    [TestCase("1.5")]
    [TestCase("70000")]
    public void ARealmIdThatIsNotARealmIdIsRejected(string id)
    {
        var result = DslParser.Parse($"FROM Player\nSPATIAL Position NEARBY 0, 0, 0 RADIUS 10 IN REALM {id}");

        Assert.That(result.Errors, Is.Not.Empty, $"'{id}' is not a realm id and must not parse as one");
    }
}
