using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Linq;
using System.Reflection;

namespace Typhon.Engine.Tests;

class StorageIntegrityChunkCountTests : TestBase<StorageIntegrityChunkCountTests>
{
    /// <summary>#1381: the retry that settles a count under concurrent allocation must not also explain away a real drift, which repeats on every count.</summary>
    [Test]
    public void ACountThatDrifted_IsReported()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();
        using (var t = dbe.CreateQuickTransaction())
        {
            t.Spawn<CompAArch>(CompAArch.A.Set(new CompA(1)));
            t.Commit();
        }

        var segment = dbe.GetComponentTable<CompA>().ComponentSegment;
        var count = segment.GetType().GetField("_allocatedCount", BindingFlags.Instance | BindingFlags.NonPublic);
        var actual = segment.AllocatedChunkCount;
        Assert.That(dbe.RunStorageIntegrityCheck().Issues, Is.Empty, "precondition");

        count.SetValue(segment, actual + 1);
        var report = dbe.RunStorageIntegrityCheck();
        count.SetValue(segment, actual);

        Assert.That(report.Issues.Count(i => i.Kind == StorageIntegrityIssueKind.ChunkSegmentCapacity), Is.EqualTo(1));
        Assert.That(report.ChunkSegmentsUnsettled, Is.Zero);
    }
}
