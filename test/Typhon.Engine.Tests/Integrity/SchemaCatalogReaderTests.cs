using NUnit.Framework;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The production manifest reader: what it recovers from a healthy database, and what it does with a damaged one.
/// </summary>
/// <remarks>
/// The second half is the one that matters. <c>MAP-04</c> is stated in the catalogue as <i>"a hard requirement on the
/// traversal code, not merely a finding"</i> — a scanner that follows a damaged pointer into a crash is worse than
/// useless on precisely the databases it exists to diagnose. That discipline has to hold one level above the entity
/// maps too: the manifest is a set of segment pointers, and a torn catalog row is a plausible way to meet a bad one.
/// </remarks>
[TestFixture]
internal sealed class SchemaCatalogReaderTests : IntegrityFixtureBase
{
    [Test]
    [CancelAfter(30_000)]
    public void ItRecoversEveryComponentAndArchetypeFromAHealthyDatabase()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);

        var reader = ReadManifest(out var roots);

        Assert.That(reader.IsUsable, Is.True, Describe(reader));
        Assert.That(reader.Diagnostics, Is.Empty, "a healthy manifest must produce no diagnostics:\n  " + Describe(reader));

        // The manifest describes itself and the archetype catalog. Both are engine-defined, so their absence means the
        // decode is wrong rather than that the database is unusual.
        Assert.That(reader.Components.Keys, Does.Contain(ComponentR1.SchemaName).And.Contain(ArchetypeR1.SchemaName));

        // Every recovered segment pointer resolves — Resolve() drops any that does not, so a silent zero here would be
        // the reader hiding damage rather than the database being clean. Checked against the sweep, not against itself.
        foreach (var c in reader.Components.Values)
        {
            if (c.ComponentSegmentRoot != 0)
            {
                Assert.That(roots, Does.Contain(c.ComponentSegmentRoot), $"component '{c.Name}'");
            }
        }

        Assert.That(reader.Archetypes, Is.Not.Empty, "no archetype was recovered:\n  " + Describe(reader));
        foreach (var a in reader.Archetypes.Values)
        {
            Assert.That(a.ComponentCount, Is.GreaterThan(0), $"archetype '{a.Name}' reports no components");
            Assert.That(a.NextEntityKey, Is.GreaterThan(0), $"archetype '{a.Name}' has no entity-key watermark");
            Assert.That(a.RoutingId, Is.GreaterThanOrEqualTo(0));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public void ADanglingSegmentPointerIsReportedAndNotFollowed()
    {
        BuildHealthyDatabase();
        var before = DamageKit.Baseline(BundlePath);

        var damage = DamageKit.RedirectCatalogSegmentPointer(BundlePath, out var owner, out var bogus);
        DamageKit.AssertOnlyDeclaredBytesChanged(before, damage);

        var reader = ReadManifest(out _);

        // Reported, in words naming what was wrong.
        Assert.That(string.Join("\n", reader.Diagnostics), Does.Contain(bogus.ToString()),
            "the reader must say which pointer it refused to follow:\n  " + Describe(reader));

        // Not followed. The row survives with the pointer zeroed rather than carrying an address into nowhere, so no
        // later check can dereference it by trusting the view.
        Assert.That(reader.Components.TryGetValue(owner, out var view), Is.True,
            $"the row for '{owner}' should survive with its bad pointer dropped, not vanish:\n  " + Describe(reader));
        Assert.That(view.ComponentSegmentRoot, Is.Zero,
            "a pointer that does not resolve must be reported as absent, never handed on:\n  " + Describe(reader));

        // And the rest of the manifest is still there. A reader that gave up on the first bad row would leave every
        // downstream check with nothing, which is the failure mode this whole feature exists to replace.
        Assert.That(reader.Components.Count, Is.GreaterThan(1), Describe(reader));
        Assert.That(reader.Archetypes, Is.Not.Empty, "one dangling component pointer must not cost the archetype catalog");
    }

    [Test]
    [CancelAfter(30_000)]
    public void AnUnreadableBootstrapDegradesInsteadOfThrowing()
    {
        BuildHealthyDatabase();
        DamageKit.Baseline(BundlePath);
        DamageKit.ClobberBothMetaSlots(BundlePath);

        var reader = ReadManifest(out _);

        Assert.That(reader.IsUsable, Is.False);
        Assert.That(reader.Diagnostics, Is.Not.Empty, "the reader must say why it recovered nothing");
    }

    private SchemaCatalogReader ReadManifest(out List<int> roots)
    {
        using var source = new OfflineBundlePageSource(BundlePath);
        roots = SweepSegmentRoots(source);

        var reader = new SchemaCatalogReader(source, roots);
        reader.Read(BootstrapReader.Read(source));
        return reader;
    }

    /// <summary>The physical sweep, so pointers are validated against a list built by an independent route.</summary>
    private static List<int> SweepSegmentRoots(IPageSource source)
    {
        var roots = new List<int>();
        var page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page) || (PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            if (MemoryMarshal.Read<int>(PageImage.RawData(page)) == p)
            {
                roots.Add(p);
            }
        }

        return roots;
    }

    private static string Describe(SchemaCatalogReader reader)
    {
        var lines = new List<string> { $"components={reader.Components.Count} archetypes={reader.Archetypes.Count}" };
        foreach (var c in reader.Components.Values)
        {
            lines.Add($"  {c}  compSeg={c.ComponentSegmentRoot} revSeg={c.RevisionSegmentRoot}");
        }

        foreach (var a in reader.Archetypes.Values)
        {
            lines.Add($"  {a}  nextKey={a.NextEntityKey} cluster={a.ClusterSegmentRoot} map={a.EntityMapRoot}");
        }

        foreach (var d in reader.Diagnostics)
        {
            lines.Add($"  ! {d}");
        }

        return string.Join("\n", lines);
    }
}
