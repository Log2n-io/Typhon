using NUnit.Framework;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Services.Querying;

namespace Typhon.Workbench.Tests.Services.Querying;

/// <summary>
/// Unit tests for <see cref="DslParser"/> (#386 Phase 1 AC-4). Covers every grammar production from the Query Console
/// design doc §5.1, the §5.2 round-trip examples, and 15+ deliberate-error recovery cases. The parser must never throw
/// on user input — every malformed construct surfaces as a <see cref="ParseErrorDto"/> with line + column.
/// </summary>
[TestFixture]
public sealed class DslParserTests
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Happy paths — every §5.1 production
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void Parse_FromOnly_PolymorphicByDefault()
    {
        var r = DslParser.Parse("FROM PlayerArch");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("PlayerArch"));
        Assert.That(r.Spec.Polymorphic, Is.True);
        Assert.That(r.Spec.Take, Is.EqualTo(DslParser.DefaultTake));   // default 1000 when TAKE omitted
    }

    [Test]
    public void Parse_FromExact_SetsPolymorphicFalse()
    {
        var r = DslParser.Parse("FROM PlayerArch exact");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Polymorphic, Is.False);
    }

    [Test]
    public void Parse_FromArchetypeIdWithHash_CanonicalisesToHashForm()
    {
        // '#2001' is what the Workbench schema browser displays — accept and canonicalise.
        var r = DslParser.Parse("FROM #2001");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("#2001"));
    }

    [Test]
    public void Parse_FromArchetypeIdBareNumeric_CanonicalisesToHashForm()
    {
        // Bare '2001' is accepted as a forgiving alternative; canonicalised to '#2001' so the compiler
        // has a single shape to dispatch on.
        var r = DslParser.Parse("FROM 2001");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("#2001"));
    }

    [Test]
    public void Parse_FromHashWithoutDigits_ReportsError()
    {
        var r = DslParser.Parse("FROM #foo");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("'#'"));
    }

    [Test]
    public void Parse_FromArchetypeIdWithExactKeyword_PreservesPolymorphism()
    {
        var r = DslParser.Parse("FROM #2001 exact");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("#2001"));
        Assert.That(r.Spec.Polymorphic, Is.False);
    }

    [Test]
    public void Parse_FromExplicitPolymorphic_SetsPolymorphicTrue()
    {
        var r = DslParser.Parse("FROM PlayerArch polymorphic");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Polymorphic, Is.True);
    }

    [Test]
    public void Parse_WithSingle_AddsOneComponent()
    {
        var r = DslParser.Parse("FROM PlayerArch WITH Inventory");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Inventory" }));
    }

    [Test]
    public void Parse_WithDottedComponentName_KeepsFullName()
    {
        // Engine components are registered by [Component("...")] attribute which is often namespaced — e.g.
        // "Typhon.Workbench.Fixture.CompA". The DSL must accept the same form the schema endpoint returns.
        var r = DslParser.Parse("FROM PlayerArch WITH Typhon.Workbench.Fixture.CompA");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Typhon.Workbench.Fixture.CompA" }));
    }

    [Test]
    public void Parse_WithMultipleDottedComponents_AddsAll()
    {
        var r = DslParser.Parse("FROM PlayerArch WITH My.Ns.CompA, My.Ns.CompB, Plain");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "My.Ns.CompA", "My.Ns.CompB", "Plain" }));
    }

    [Test]
    public void Parse_Select_MultipleComponents_CommaSeparated()
    {
        var r = DslParser.Parse("FROM PlayerArch SELECT My.Ns.CompA, My.Ns.CompB, Plain");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Select, Is.EqualTo(new[] { "My.Ns.CompA", "My.Ns.CompB", "Plain" }));
    }

    [Test]
    public void Parse_NoSelect_SelectIsEmpty()
    {
        // The no-SELECT default must surface as an empty (not null) list so the compiler's projection fallback holds.
        var r = DslParser.Parse("FROM PlayerArch");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Select, Is.Empty);
    }

    [Test]
    public void Parse_WhereDottedComponentName_SplitsAtLastDot()
    {
        // Last segment = field; everything before joined by '.' = component name.
        var r = DslParser.Parse("FROM PlayerArch WHERE Typhon.Workbench.Fixture.QCompA.Level >= 50");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where, Is.Not.Null);
        Assert.That(r.Spec.Where.Component, Is.EqualTo("Typhon.Workbench.Fixture.QCompA"));
        Assert.That(r.Spec.Where.Field, Is.EqualTo("Level"));
    }

    [Test]
    public void Parse_OrderByDottedComponentName_SplitsAtLastDot()
    {
        var r = DslParser.Parse("FROM PlayerArch WHERE My.Ns.C.F == 1 ORDER BY My.Ns.C.F DESC");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.OrderBy, Is.Not.Null);
        Assert.That(r.Spec.OrderBy.Component, Is.EqualTo("My.Ns.C"));
        Assert.That(r.Spec.OrderBy.Field, Is.EqualTo("F"));
        Assert.That(r.Spec.OrderBy.Descending, Is.True);
    }

    [Test]
    public void Parse_WithMultipleComma_AddsAll()
    {
        var r = DslParser.Parse("FROM PlayerArch WITH Inventory, Combat, Stats");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Inventory", "Combat", "Stats" }));
    }

    [Test]
    public void Parse_Without_AddsToWithoutList()
    {
        var r = DslParser.Parse("FROM PlayerArch WITHOUT Frozen");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Without, Is.EqualTo(new[] { "Frozen" }));
    }

    [Test]
    public void Parse_Exclude_AddsToExcludeList()
    {
        var r = DslParser.Parse("FROM PlayerArch EXCLUDE BotArch");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Exclude, Is.EqualTo(new[] { "BotArch" }));
    }

    [Test]
    public void Parse_EnabledAndDisabled_AddToRespectiveLists()
    {
        var r = DslParser.Parse("FROM PlayerArch ENABLED Combat DISABLED Stealth");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Enabled, Is.EqualTo(new[] { "Combat" }));
        Assert.That(r.Spec.Disabled, Is.EqualTo(new[] { "Stealth" }));
    }

    [Test]
    public void Parse_WhereSimpleEq_BuildsCmpNode()
    {
        var r = DslParser.Parse("FROM PlayerArch WHERE Player.Level == 50");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where, Is.Not.Null);
        Assert.That(r.Spec.Where.Kind, Is.EqualTo("cmp"));
        Assert.That(r.Spec.Where.Component, Is.EqualTo("Player"));
        Assert.That(r.Spec.Where.Field, Is.EqualTo("Level"));
        Assert.That(r.Spec.Where.Op, Is.EqualTo("=="));
        Assert.That(r.Spec.Where.Value, Is.EqualTo(50L));
    }

    [Test]
    public void Parse_WhereAllComparisonOps_RoundTripsCorrectly()
    {
        foreach (var (dsl, expectedOp) in new[]
        {
            ("FROM A WHERE C.F == 1", "=="),
            ("FROM A WHERE C.F != 1", "!="),
            ("FROM A WHERE C.F > 1", ">"),
            ("FROM A WHERE C.F < 1", "<"),
            ("FROM A WHERE C.F >= 1", ">="),
            ("FROM A WHERE C.F <= 1", "<="),
        })
        {
            var r = DslParser.Parse(dsl);
            Assert.That(r.Errors, Is.Empty, dsl);
            Assert.That(r.Spec.Where.Op, Is.EqualTo(expectedOp), dsl);
        }
    }

    [Test]
    public void Parse_WhereAnd_BuildsAndNode()
    {
        var r = DslParser.Parse("FROM PlayerArch WHERE Player.Level >= 50 AND Player.Faction == 1");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Kind, Is.EqualTo("and"));
        Assert.That(r.Spec.Where.Children, Has.Length.EqualTo(2));
        Assert.That(r.Spec.Where.Children[0].Field, Is.EqualTo("Level"));
        Assert.That(r.Spec.Where.Children[1].Field, Is.EqualTo("Faction"));
    }

    [Test]
    public void Parse_WhereOr_BuildsOrNode()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == 1 OR C.G == 2");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Kind, Is.EqualTo("or"));
    }

    [Test]
    public void Parse_WhereParenthesized_RespectsExplicitGrouping()
    {
        var r = DslParser.Parse("FROM A WHERE (C.F == 1 OR C.F == 2) AND C.G == 3");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Kind, Is.EqualTo("and"));
        Assert.That(r.Spec.Where.Children[0].Kind, Is.EqualTo("or"));        // parens preserved
        Assert.That(r.Spec.Where.Children[1].Field, Is.EqualTo("G"));
    }

    [Test]
    public void Parse_WhereNegativeIntLiteral_PreservesSign()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == -42");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Value, Is.EqualTo(-42L));
    }

    [Test]
    public void Parse_WhereFloatLiteral_KeepsDouble()
    {
        var r = DslParser.Parse("FROM A WHERE C.F > 3.14");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Value, Is.EqualTo(3.14d));
    }

    [Test]
    public void Parse_WhereStringLiteral_StoresUnquoted()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == \"hello\"");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Value, Is.EqualTo("hello"));
    }

    [Test]
    public void Parse_WhereBoolLiteral_StoresBool()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == true");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Value, Is.EqualTo(true));
    }

    [Test]
    public void Parse_WhereIdentifierValue_StoresAsString()
    {
        // Enum-like identifier — compiler will resolve against the field type.
        var r = DslParser.Parse("FROM A WHERE C.Faction == Combat");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Where.Value, Is.EqualTo("Combat"));
    }

    [Test]
    public void Parse_SpatialNearby_StoresClause()
    {
        var r = DslParser.Parse("FROM A SPATIAL Position NEARBY 1.0, 2.0, 3.0 RADIUS 50");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Spatial, Has.Length.EqualTo(1));
        Assert.That(r.Spec.Spatial[0].Kind, Is.EqualTo("nearby"));
        Assert.That(r.Spec.Spatial[0].Parameters, Is.EqualTo(new[] { 1.0, 2.0, 3.0, 50.0 }));
    }

    [Test]
    public void Parse_SpatialAabb_StoresMinMaxPair()
    {
        var r = DslParser.Parse("FROM A SPATIAL Position AABB 0, 0, 0, 10, 10, 10");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Spatial[0].Kind, Is.EqualTo("aabb"));
        Assert.That(r.Spec.Spatial[0].Parameters, Has.Length.EqualTo(6));
    }

    [Test]
    public void Parse_SpatialRay_StoresOriginDirectionMaxDist()
    {
        var r = DslParser.Parse("FROM A SPATIAL Position RAY 0, 0, 0, 1, 0, 0, 100");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Spatial[0].Kind, Is.EqualTo("ray"));
        Assert.That(r.Spec.Spatial[0].Parameters, Has.Length.EqualTo(7));
    }

    [Test]
    public void Parse_SpatialWithQualifiedComponent_KeepsFullDottedName()
    {
        // Regression: ParseSpatial used to Consume() a single identifier, so a namespaced component name — the form
        // the engine registers and WHERE requires — failed at the first dot ("Expected NEARBY, AABB, or RAY"). It
        // must read the full dotted component-ident like every other stage (§5.1 grammar).
        var r = DslParser.Parse("FROM A SPATIAL Typhon.Workbench.Fixture.PlayerPosition NEARBY 1, 2, 3 RADIUS 10");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Spatial, Has.Length.EqualTo(1));
        Assert.That(r.Spec.Spatial[0].Component, Is.EqualTo("Typhon.Workbench.Fixture.PlayerPosition"));
        Assert.That(r.Spec.Spatial[0].Kind, Is.EqualTo("nearby"));
    }

    [Test]
    public void Parse_Navigate_StoresClause()
    {
        var r = DslParser.Parse("FROM A NAVIGATE OwnerId -> Player WHERE Player.Faction == 1");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Navigate, Has.Length.EqualTo(1));
        Assert.That(r.Spec.Navigate[0].Field, Is.EqualTo("OwnerId"));
        Assert.That(r.Spec.Navigate[0].TargetComponent, Is.EqualTo("Player"));
        Assert.That(r.Spec.Navigate[0].Where, Is.Not.Null);
    }

    [Test]
    public void Parse_OrderByAsc_DefaultsAscending()
    {
        var r = DslParser.Parse("FROM A ORDER BY C.F");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.OrderBy.Descending, Is.False);
    }

    [Test]
    public void Parse_OrderByDesc_SetsDescending()
    {
        var r = DslParser.Parse("FROM A ORDER BY C.F DESC");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.OrderBy.Descending, Is.True);
    }

    [Test]
    public void Parse_SkipAndTake_SetsBoth()
    {
        var r = DslParser.Parse("FROM A SKIP 25 TAKE 50");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Skip, Is.EqualTo(25));
        Assert.That(r.Spec.Take, Is.EqualTo(50));
    }

    [Test]
    public void Parse_AtRevisionHead_DefaultsToHead()
    {
        var r = DslParser.Parse("FROM A AT REVISION HEAD");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Revision.Kind, Is.EqualTo("head"));
    }

    [Test]
    public void Parse_AtRevisionInteger_StoresRevision()
    {
        var r = DslParser.Parse("FROM A AT REVISION 12345");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Revision.Kind, Is.EqualTo("revision"));
        Assert.That(r.Spec.Revision.Value, Is.EqualTo(12345L));
    }

    [Test]
    public void Parse_AtTick_StoresTick()
    {
        var r = DslParser.Parse("FROM A AT TICK 999");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Revision.Kind, Is.EqualTo("tick"));
        Assert.That(r.Spec.Revision.Value, Is.EqualTo(999L));
    }

    [Test]
    public void Parse_AtTime_StoresIsoString()
    {
        var r = DslParser.Parse("FROM A AT TIME \"2026-05-28T12:00:00Z\"");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Revision.Kind, Is.EqualTo("time"));
        Assert.That(r.Spec.Revision.TimeIso, Is.EqualTo("2026-05-28T12:00:00Z"));
    }

    [Test]
    public void Parse_LowercaseKeywords_IsCaseInsensitive()
    {
        var r = DslParser.Parse("from PlayerArch with Inventory where Player.Level >= 50 order by Player.Level desc take 100");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("PlayerArch"));
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Inventory" }));
        Assert.That(r.Spec.Where, Is.Not.Null);
        Assert.That(r.Spec.OrderBy.Descending, Is.True);
        Assert.That(r.Spec.Take, Is.EqualTo(100));
    }

    [Test]
    public void Parse_DesignDocFullExample_RoundTripsCleanly()
    {
        // §5.2 of the design doc — full example query.
        var dsl =
            "FROM PlayerArch polymorphic\n" +
            "WITH Inventory\n" +
            "WITHOUT Frozen\n" +
            "ENABLED Combat\n" +
            "WHERE Player.Level >= 50\n" +
            "  AND Player.Faction == 1\n" +
            "ORDER BY Player.Level DESC\n" +
            "TAKE 100\n" +
            "AT REVISION HEAD";
        var r = DslParser.Parse(dsl);
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo("PlayerArch"));
        Assert.That(r.Spec.Polymorphic, Is.True);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Inventory" }));
        Assert.That(r.Spec.Without, Is.EqualTo(new[] { "Frozen" }));
        Assert.That(r.Spec.Enabled, Is.EqualTo(new[] { "Combat" }));
        Assert.That(r.Spec.Where.Kind, Is.EqualTo("and"));
        Assert.That(r.Spec.OrderBy.Descending, Is.True);
        Assert.That(r.Spec.Take, Is.EqualTo(100));
    }

    [Test]
    public void Parse_LineComments_AreSkipped()
    {
        var r = DslParser.Parse("FROM PlayerArch -- this is a comment\nWITH Inventory");
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.With, Is.EqualTo(new[] { "Inventory" }));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Error cases — parser must never throw, always emit diagnostics with location
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void Parse_Empty_ReturnsNoErrorAndEmptySpec()
    {
        // Empty input is a valid (incomplete) draft — chip-mode user mid-authoring.
        var r = DslParser.Parse(string.Empty);
        Assert.That(r.Errors, Is.Empty);
        Assert.That(r.Spec.Archetype, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Parse_MissingFrom_ReportsError()
    {
        var r = DslParser.Parse("WITH Inventory");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("FROM"));
    }

    [Test]
    public void Parse_FromWithoutIdentifier_ReportsError()
    {
        // 'FROM 3.14' is invalid — a float is neither an identifier nor an archetype id. (Previously this test
        // used 'FROM 123' but numeric IDs are now a valid form — see Parse_FromArchetypeIdBareNumeric_…)
        var r = DslParser.Parse("FROM 3.14");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("archetype"));
    }

    [Test]
    public void Parse_WhereWithoutDot_ReportsError()
    {
        var r = DslParser.Parse("FROM A WHERE Player == 50");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("'.'"));
    }

    [Test]
    public void Parse_WhereWithoutOperator_ReportsError()
    {
        var r = DslParser.Parse("FROM A WHERE Player.Level 50");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("comparison operator"));
    }

    [Test]
    public void Parse_WhereWithoutValue_ReportsError()
    {
        var r = DslParser.Parse("FROM A WHERE Player.Level ==");
        Assert.That(r.Errors, Is.Not.Empty);
    }

    [Test]
    public void Parse_UnterminatedString_ReportsError()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == \"unterminated");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("Unterminated"));
    }

    [Test]
    public void Parse_UnexpectedCharacter_ReportsErrorAndContinues()
    {
        var r = DslParser.Parse("FROM A WHERE C.F == 5 @ AND C.G == 6");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("Unexpected"));
    }

    [Test]
    public void Parse_AtRevisionWithoutValue_ReportsError()
    {
        var r = DslParser.Parse("FROM A AT REVISION");
        Assert.That(r.Errors, Is.Not.Empty);
    }

    [Test]
    public void Parse_SkipWithoutInteger_ReportsError()
    {
        var r = DslParser.Parse("FROM A SKIP \"foo\"");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("integer"));
    }

    [Test]
    public void Parse_OrderWithoutBy_ReportsError()
    {
        var r = DslParser.Parse("FROM A ORDER Player.Level");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("BY"));
    }

    [Test]
    public void Parse_NavigateWithoutArrow_ReportsError()
    {
        var r = DslParser.Parse("FROM A NAVIGATE OwnerId Player");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("->"));
    }

    [Test]
    public void Parse_SpatialNearbyWithoutRadius_ReportsError()
    {
        var r = DslParser.Parse("FROM A SPATIAL Position NEARBY 0, 0, 0");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Errors[0].Message, Does.Contain("RADIUS"));
    }

    [Test]
    public void Parse_SpatialAabbWithoutComma_ReportsError()
    {
        var r = DslParser.Parse("FROM A SPATIAL Position AABB 0, 0, 0 10, 10, 10");
        Assert.That(r.Errors, Is.Not.Empty);
    }

    [Test]
    public void Parse_UnknownStageKeyword_ReportsErrorAndRecovers()
    {
        // 'FOOBAR' isn't a stage keyword, parser should error then recover to TAKE.
        var r = DslParser.Parse("FROM A FOOBAR Whatever TAKE 25");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Spec.Take, Is.EqualTo(25), "Recovery should let TAKE land after the error");
    }

    [Test]
    public void Parse_BadWhereRecoversToNextStage()
    {
        // Bad WHERE then valid TAKE — recovery should still apply TAKE.
        var r = DslParser.Parse("FROM A WHERE 123 TAKE 7");
        Assert.That(r.Errors, Is.Not.Empty);
        Assert.That(r.Spec.Take, Is.EqualTo(7));
    }

    [Test]
    public void Parse_ErrorPositions_AreOneIndexedLineAndColumn()
    {
        var r = DslParser.Parse("FROM\nWHERE bad");
        Assert.That(r.Errors, Is.Not.Empty);
        // First diagnostic — "FROM requires an archetype identifier" — is at the WHERE token on line 2.
        Assert.That(r.Errors[0].Line, Is.GreaterThanOrEqualTo(1));
        Assert.That(r.Errors[0].Column, Is.GreaterThanOrEqualTo(1));
    }
}
