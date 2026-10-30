using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// Databases open side by side, whose schemas share archetype names (#554, #384). Each session loads its schema into its own load context, and its engine consumes
/// the archetypes of that context, whatever else the process holds: the fixture schema in the default context, or another session's copy of it.
/// </summary>
/// <remarks>Parallel on purpose: this is the fixture that says engines need no serialization around the registry.</remarks>
[TestFixture]
public sealed class EngineLifecycleSideBySideTests
{
    private string _tempDir;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-side-by-side-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string NewFixture(string name) => FixtureDatabase.CreateOrReuse(Path.Combine(_tempDir, name), force: true).TyphonFilePath;

    private static void AssertOpensClean(EngineLifecycle lifecycle)
    {
        Assert.That(lifecycle.State, Is.EqualTo(SchemaCompatibility.State.Ready), () => string.Join("; ", lifecycle.Diagnostics.Select(d => $"{d.Kind}: {d.Detail}")));
        Assert.That(lifecycle.Diagnostics.Where(d => d.Kind.StartsWith("archetype_", StringComparison.Ordinal)), Is.Empty);
        Assert.That(lifecycle.Engine.OwnArchetypes().Any(), Is.True, "a session must find its schema's archetypes");
        Assert.That(lifecycle.Engine.EnumerateStorageSegments().Count(s => s.Kind == StorageSegmentKind.Cluster), Is.GreaterThan(0));
    }

    /// <summary>The issue's order: the session holds the fixture's collectible copy, and a generation uses the default context's. It used to throw, after wiping the output.</summary>
    [Test]
    public async Task AFixtureOpened_ThenAnotherGenerated_BothOpen()
    {
        var first = NewFixture("first");
        using var held = await EngineLifecycle.OpenAsync(first);
        AssertOpensClean(held);

        var second = NewFixture("second");
        using var other = await EngineLifecycle.OpenAsync(second);

        AssertOpensClean(other);
    }

    [Test]
    public async Task TwoSessions_HoldSeparateArchetypes()
    {
        var path = NewFixture("a");
        using var a = await EngineLifecycle.OpenAsync(path);
        using var b = await EngineLifecycle.OpenAsync(NewFixture("b"));

        var ofA = a.Engine.OwnArchetypes().ToArray();
        var ofB = b.Engine.OwnArchetypes().ToArray();

        Assert.That(ofA.Select(m => m.ArchetypeType.FullName), Is.EquivalentTo(ofB.Select(m => m.ArchetypeType.FullName)), "one schema, twice");
        Assert.That(ofA.Select(m => m.ArchetypeId).Intersect(ofB.Select(m => m.ArchetypeId)), Is.Empty, "each copy is its own archetype");
        Assert.That(ofA.Select(m => m.ArchetypeType).Intersect(ofB.Select(m => m.ArchetypeType)), Is.Empty);
    }

    [Test]
    public async Task ClosingOneSession_LeavesTheOtherUsable()
    {
        var a = await EngineLifecycle.OpenAsync(NewFixture("a"));
        using var b = await EngineLifecycle.OpenAsync(NewFixture("b"));
        var archetypes = b.Engine.OwnArchetypes().ToArray();

        a.Dispose();

        Assert.That(b.Engine.OwnArchetypes(), Is.EquivalentTo(archetypes), "closing a session releases its own archetypes and no others");
        foreach (var meta in archetypes)
        {
            Assert.That(ArchetypeRegistry.GetMetadata(meta.ArchetypeId), Is.SameAs(meta));
        }

        // And the engine still works on them: a transaction, and a checkpoint that walks every archetype.
        using (var tx = b.Engine.CreateQuickTransaction())
        {
            Assert.That(tx.Commit(), Is.True);
        }

        Assert.DoesNotThrow(() => b.Engine.ForceCheckpoint());
    }

    [Test]
    public async Task ConcurrentOpens_NeverReportASpuriousState()
    {
        var a = NewFixture("a");
        var b = NewFixture("b");

        for (var round = 0; round < 20; round++)
        {
            using var start = new Barrier(2);
            var first = Task.Run(() => { start.SignalAndWait(); return EngineLifecycle.OpenAsync(a); });
            var second = Task.Run(() => { start.SignalAndWait(); return EngineLifecycle.OpenAsync(b); });

            using var one = await first;
            using var two = await second;

            AssertOpensClean(one);
            AssertOpensClean(two);
        }
    }
}
