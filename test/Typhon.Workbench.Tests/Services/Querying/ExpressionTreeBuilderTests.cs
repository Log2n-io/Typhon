using NUnit.Framework;
using Typhon.Workbench.Dtos.Query;
using Typhon.Workbench.Services.Querying;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Services.Querying;

/// <summary>
/// Unit tests for <see cref="ExpressionTreeBuilder"/> — the bridge that synthesizes
/// <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> from <see cref="PredicateNodeDto"/> trees so the engine's
/// <c>EcsQuery.WhereField&lt;T&gt;</c> entry point can consume them. Tests use a local <see cref="TestStruct"/> type so
/// the suite stays free of engine setup.
/// </summary>
[TestFixture]
public sealed class ExpressionTreeBuilderTests
{
    public struct TestStruct
    {
        public int IntField;
        public float FloatField;
        public string StringField;
        public TestEnum EnumField;
    }

    public enum TestEnum { Zero, One, Two }

    private static PredicateNodeDto Cmp(string field, string op, object value) =>
        new("cmp", null, nameof(TestStruct), field, op, value);

    private static PredicateNodeDto And(params PredicateNodeDto[] children) =>
        new("and", children, null, null, null, null);

    private static PredicateNodeDto Or(params PredicateNodeDto[] children) =>
        new("or", children, null, null, null, null);

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // BuildPredicate — comparison operators
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void BuildPredicate_RegisteredNameDifferentFromClrName_StillResolves()
    {
        // Real-world case: the engine registers components by their [Component("...")] attribute (often namespaced
        // like "Typhon.Workbench.Fixture.Player"), but the CLR class name is the short form ("CompPlayer"). The
        // builder must NOT compare node.Component to T's CLR name — the compiler has already resolved the type
        // via the registered name, and the builder just builds the lambda for T. (Regression — this previously
        // threw 'Predicate component does not match the expected' for any dotted-name component.)
        var node = new PredicateNodeDto(
            "cmp", null,
            "Typhon.Workbench.Fixture.SomeRegisteredName",   // not equal to TestStruct's CLR name
            "IntField", "==", 7L);
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(node).Compile();
        Assert.That(fn(new TestStruct { IntField = 7 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 8 }), Is.False);
    }

    [Test]
    public void BuildPredicate_IntEqual_MatchesEqualValue()
    {
        var lambda = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("IntField", "==", 42L));
        var fn = lambda.Compile();
        Assert.That(fn(new TestStruct { IntField = 42 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 43 }), Is.False);
    }

    [Test]
    public void BuildPredicate_IntNotEqual_InvertsEquality()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("IntField", "!=", 42L)).Compile();
        Assert.That(fn(new TestStruct { IntField = 42 }), Is.False);
        Assert.That(fn(new TestStruct { IntField = 43 }), Is.True);
    }

    [Test]
    public void BuildPredicate_IntGreaterThan_MatchesGreater()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("IntField", ">", 10L)).Compile();
        Assert.That(fn(new TestStruct { IntField = 11 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 10 }), Is.False);
    }

    [Test]
    public void BuildPredicate_IntGreaterThanOrEqual_IncludesBoundary()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("IntField", ">=", 10L)).Compile();
        Assert.That(fn(new TestStruct { IntField = 10 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 9 }), Is.False);
    }

    [Test]
    public void BuildPredicate_FloatLessThan_NarrowsLongToFloat()
    {
        // Parser emits long for "10" — builder must coerce to float to match TestStruct.FloatField's type.
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("FloatField", "<", 10L)).Compile();
        Assert.That(fn(new TestStruct { FloatField = 5.0f }), Is.True);
        Assert.That(fn(new TestStruct { FloatField = 10.0f }), Is.False);
    }

    [Test]
    public void BuildPredicate_StringEquality_MatchesByValue()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("StringField", "==", "hello")).Compile();
        Assert.That(fn(new TestStruct { StringField = "hello" }), Is.True);
        Assert.That(fn(new TestStruct { StringField = "world" }), Is.False);
    }

    [Test]
    public void BuildPredicate_EnumByName_ResolvesToEnumValue()
    {
        // Parser emits the identifier as a string; builder must Enum.Parse against the field type.
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("EnumField", "==", "One")).Compile();
        Assert.That(fn(new TestStruct { EnumField = TestEnum.One }), Is.True);
        Assert.That(fn(new TestStruct { EnumField = TestEnum.Zero }), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // BuildPredicate — composition
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void BuildPredicate_And_RequiresAllBranches()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(
            And(Cmp("IntField", ">=", 10L), Cmp("FloatField", "<", 5L))).Compile();
        Assert.That(fn(new TestStruct { IntField = 20, FloatField = 3 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 20, FloatField = 7 }), Is.False);   // FloatField fails
        Assert.That(fn(new TestStruct { IntField = 5, FloatField = 3 }), Is.False);    // IntField fails
    }

    [Test]
    public void BuildPredicate_Or_RequiresAnyBranch()
    {
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(
            Or(Cmp("IntField", "==", 1L), Cmp("IntField", "==", 2L))).Compile();
        Assert.That(fn(new TestStruct { IntField = 1 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 2 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 3 }), Is.False);
    }

    [Test]
    public void BuildPredicate_NestedAndOr_RespectsTreeShape()
    {
        // (IntField == 1 OR IntField == 2) AND FloatField > 0
        var fn = ExpressionTreeBuilder.BuildPredicate<TestStruct>(
            And(
                Or(Cmp("IntField", "==", 1L), Cmp("IntField", "==", 2L)),
                Cmp("FloatField", ">", 0L)))
            .Compile();
        Assert.That(fn(new TestStruct { IntField = 1, FloatField = 5 }), Is.True);
        Assert.That(fn(new TestStruct { IntField = 3, FloatField = 5 }), Is.False);    // OR fails
        Assert.That(fn(new TestStruct { IntField = 1, FloatField = -1 }), Is.False);   // AND fails
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Error paths — must throw WorkbenchException with stable codes
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void BuildPredicate_UnknownField_ThrowsInvalidField()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("DoesNotExist", "==", 1L)));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_field"));
    }

    [Test]
    public void BuildPredicate_UnknownOperator_ThrowsInvalidSyntax()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("IntField", "<>", 1L)));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_query_syntax"));
    }

    [Test]
    public void BuildPredicate_UnknownNodeKind_ThrowsInvalidSyntax()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.BuildPredicate<TestStruct>(new PredicateNodeDto("wat", null, null, null, null, null)));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_query_syntax"));
    }

    [Test]
    public void BuildPredicate_EnumInvalidName_ThrowsInvalidSyntax()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.BuildPredicate<TestStruct>(Cmp("EnumField", "==", "NotARealEnumValue")));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_query_syntax"));
    }

    [Test]
    public void BuildPredicate_AndWithNoChildren_ThrowsInvalidSyntax()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.BuildPredicate<TestStruct>(new PredicateNodeDto("and", [], null, null, null, null)));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_query_syntax"));
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // BuildFieldSelector — ORDER BY support
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    public void BuildFieldSelector_ReadsField()
    {
        var lambda = ExpressionTreeBuilder.BuildFieldSelector<TestStruct, int>("IntField");
        var fn = lambda.Compile();
        Assert.That(fn(new TestStruct { IntField = 99 }), Is.EqualTo(99));
    }

    [Test]
    public void BuildFieldSelector_TypeMismatch_CastsThrough()
    {
        // Caller may not know the field's exact type; builder should emit a Convert when TKey != field type.
        var lambda = ExpressionTreeBuilder.BuildFieldSelector<TestStruct, long>("IntField");
        Assert.That(lambda.Compile()(new TestStruct { IntField = 42 }), Is.EqualTo(42L));
    }

    [Test]
    public void ResolveFieldType_KnownField_ReturnsClrType()
    {
        Assert.That(ExpressionTreeBuilder.ResolveFieldType(typeof(TestStruct), "IntField"), Is.EqualTo(typeof(int)));
        Assert.That(ExpressionTreeBuilder.ResolveFieldType(typeof(TestStruct), "FloatField"), Is.EqualTo(typeof(float)));
        Assert.That(ExpressionTreeBuilder.ResolveFieldType(typeof(TestStruct), "EnumField"), Is.EqualTo(typeof(TestEnum)));
    }

    [Test]
    public void ResolveFieldType_UnknownField_ThrowsInvalidField()
    {
        var ex = Assert.Throws<WorkbenchException>(() =>
            ExpressionTreeBuilder.ResolveFieldType(typeof(TestStruct), "Nope"));
        Assert.That(ex.ErrorCode, Is.EqualTo("invalid_field"));
    }
}
