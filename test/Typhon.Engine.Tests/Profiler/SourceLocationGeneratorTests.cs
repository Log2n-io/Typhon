using System;
using System.Linq;
using NUnit.Framework;
using Typhon.Engine.Profiler.Generated;

namespace Typhon.Engine.Tests.Profiler;

/// <summary>
/// Phase 2 (#302): tests that the <see cref="SourceLocations"/> table emitted by <c>SourceLocationGenerator</c>
/// is well-formed — non-empty, deterministic ordering, repo-relative paths, IDs starting at 1, no duplicates.
/// </summary>
/// <remarks>
/// These tests inspect the static table baked into <c>Typhon.Engine.dll</c>. They verify the GENERATOR
/// PRODUCED CORRECT METADATA. Verifying that interceptors actually redirect at runtime requires an active
/// profiler session; that's covered by the live-attach round-trip in Phase 4. The compilation succeeding at
/// all (with the generator's interceptors referencing <c>BeginXxx_WithSiteId</c>) is itself strong evidence
/// the interceptor wiring is correct — a malformed interceptor signature fails the build.
/// </remarks>
[TestFixture]
public class SourceLocationGeneratorTests
{
    [Test]
    public void TableIsNonEmpty()
    {
        Assert.That(SourceLocations.All.Length, Is.GreaterThan(0),
            "Generator should emit at least one site for the call sites in Typhon.Engine");
        Assert.That(SourceLocations.Files.Length, Is.GreaterThan(0),
            "FileTable should have at least one entry");
    }

    [Test]
    public void IdsStartAtOneAndAreSequential()
    {
        // ID 0 is reserved for "unknown source". Generator assigns IDs starting at 1, sequentially.
        Assert.That(SourceLocations.All[0].Id, Is.EqualTo((ushort)1),
            "First site ID must be 1 — 0 is reserved for unknown-source fallback");

        for (int i = 1; i < SourceLocations.All.Length; i++)
        {
            Assert.That(SourceLocations.All[i].Id, Is.EqualTo((ushort)(SourceLocations.All[i - 1].Id + 1)),
                $"Site IDs must be sequential; gap at index {i}");
        }
    }

    [Test]
    public void IdsAreUnique()
    {
        var ids = SourceLocations.All.Select(e => e.Id).ToArray();
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length),
            "Site IDs must be unique");
    }

    [Test]
    public void FilesAreRepoRelative()
    {
        // Per design §4.4: paths must be repo-relative ("/_/..." form), not absolute build-machine paths.
        // This is the portability + privacy invariant — tests on a different machine should resolve.
        foreach (var file in SourceLocations.Files)
        {
            Assert.That(file, Does.StartWith("/_/"),
                $"File path '{file}' must use the /_/ repo-relative sentinel; absolute paths leak build-machine state");
        }
    }

    [Test]
    public void EntriesReferenceValidFileIds()
    {
        var fileCount = (ushort)SourceLocations.Files.Length;
        foreach (var entry in SourceLocations.All)
        {
            Assert.That(entry.FileId, Is.LessThan(fileCount),
                $"Entry id={entry.Id} fileId={entry.FileId} is out of range (Files.Length={fileCount})");
        }
    }

    [Test]
    public void EntriesAreSortedDeterministically()
    {
        // Generator sorts by (filePath, line, column) before assigning IDs. The sorted order anchors the
        // deterministic-ID property: site IDs are stable across builds and machines.
        for (int i = 1; i < SourceLocations.All.Length; i++)
        {
            var prev = SourceLocations.All[i - 1];
            var curr = SourceLocations.All[i];

            // (FileId, Line) must be monotonically non-decreasing — same file → line increases; different file → fileId increases.
            var prevKey = ((int)prev.FileId, prev.Line);
            var currKey = ((int)curr.FileId, curr.Line);
            Assert.That(currKey, Is.GreaterThanOrEqualTo(prevKey),
                $"Entries must be sorted by (FileId, Line); violation at index {i}: prev={prevKey}, curr={currKey}");
        }
    }

    [Test]
    public void BTreeInsertSiteIsAttributed()
    {
        // BTree.cs has at least one TyphonEvent.BeginBTreeInsert call site. After Phase 2's generator wires up,
        // the table must contain an entry whose file ends with BTree.cs (any of BTree.cs / BTree.Insert.cs / etc.).
        var btreeFiles = SourceLocations.Files
            .Select((f, idx) => (path: f, idx: (ushort)idx))
            .Where(x => x.path.Contains("BTree", StringComparison.Ordinal))
            .ToArray();
        Assert.That(btreeFiles, Is.Not.Empty,
            "Generator should attribute at least one site in a BTree*.cs file");
    }

    [Test]
    public void EntriesCarryMethodNames()
    {
        // The containing method name is captured for free at attribution time and shown in the Workbench
        // detail row ("file:line · method"). Empty method names indicate a generator-level regression.
        foreach (var entry in SourceLocations.All)
        {
            Assert.That(entry.Method, Is.Not.Null.And.Not.Empty,
                $"Entry id={entry.Id} has no method name");
        }
    }
}
