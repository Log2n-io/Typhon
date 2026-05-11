using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>Tests for the producer-side string interner used by Query Definition Export (#342, v9).</summary>
[TestFixture]
public class QuerySourceStringInternerTests
{
    [SetUp]
    public void Setup() => QuerySourceStringInterner.Reset();

    [Test]
    public void Intern_ReturnsSameId_ForSameString()
    {
        var id1 = QuerySourceStringInterner.Intern("path/to/file.cs");
        var id2 = QuerySourceStringInterner.Intern("path/to/file.cs");
        Assert.That(id1, Is.EqualTo(id2));
        Assert.That(id1, Is.GreaterThan(0));
    }

    [Test]
    public void Intern_ReturnsDifferentIds_ForDifferentStrings()
    {
        var id1 = QuerySourceStringInterner.Intern("a.cs");
        var id2 = QuerySourceStringInterner.Intern("b.cs");
        Assert.That(id1, Is.Not.EqualTo(id2));
    }

    [Test]
    public void Intern_ReturnsZero_ForNullOrEmpty()
    {
        Assert.That(QuerySourceStringInterner.Intern(null), Is.EqualTo((ushort)0));
        Assert.That(QuerySourceStringInterner.Intern(string.Empty), Is.EqualTo((ushort)0));
    }

    [Test]
    public void SnapshotStrings_ReturnsIdIndexedTable()
    {
        var idA = QuerySourceStringInterner.Intern("a.cs");
        var idB = QuerySourceStringInterner.Intern("b.cs");
        var idC = QuerySourceStringInterner.Intern("c.cs");

        var snapshot = QuerySourceStringInterner.SnapshotStrings();

        Assert.That(snapshot.Length, Is.EqualTo(4));  // slot 0 sentinel + 3 strings
        Assert.That(snapshot[0], Is.Null);
        Assert.That(snapshot[idA], Is.EqualTo("a.cs"));
        Assert.That(snapshot[idB], Is.EqualTo("b.cs"));
        Assert.That(snapshot[idC], Is.EqualTo("c.cs"));
    }

    [Test]
    public void Reset_ClearsInternerState()
    {
        QuerySourceStringInterner.Intern("first.cs");
        var first = QuerySourceStringInterner.SnapshotStrings();
        Assert.That(first.Length, Is.EqualTo(2));

        QuerySourceStringInterner.Reset();
        var afterReset = QuerySourceStringInterner.SnapshotStrings();
        Assert.That(afterReset.Length, Is.EqualTo(1));  // just the sentinel

        var newId = QuerySourceStringInterner.Intern("second.cs");
        Assert.That(newId, Is.EqualTo((ushort)1));  // restarts from 1
    }

    [Test]
    public void Intern_HandlesManyDistinctStrings()
    {
        for (var i = 0; i < 500; i++)
        {
            var id = QuerySourceStringInterner.Intern($"file{i}.cs");
            Assert.That(id, Is.GreaterThan(0));
        }
        var snapshot = QuerySourceStringInterner.SnapshotStrings();
        Assert.That(snapshot.Length, Is.EqualTo(501));  // 500 distinct + sentinel
    }
}

/// <summary>Tests for the once-per-session dedup tracker used by Query Definition Export (#342, v9).</summary>
[TestFixture]
public class QueryDefinitionDescribeTrackerTests
{
    [SetUp]
    public void Setup() => QueryDefinitionDescribeTracker.Reset();

    [Test]
    public void TryMarkAndCheck_ReturnsTrueOnFirstObservation()
    {
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(kind: 0, localId: 42), Is.True);
    }

    [Test]
    public void TryMarkAndCheck_ReturnsFalseOnSubsequentObservations()
    {
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 42), Is.True);
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 42), Is.False);
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 42), Is.False);
    }

    [Test]
    public void TryMarkAndCheck_DiscriminatesByKind()
    {
        // Same localId, different kind → different identities, both get to emit.
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(kind: 0, localId: 7), Is.True);
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(kind: 1, localId: 7), Is.True);
        // Re-checks for both kinds return false.
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 7), Is.False);
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(1, 7), Is.False);
    }

    [Test]
    public void Reset_ClearsTrackerState()
    {
        QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 1);
        QueryDefinitionDescribeTracker.Reset();
        Assert.That(QueryDefinitionDescribeTracker.TryMarkAndCheck(0, 1), Is.True);
    }
}
