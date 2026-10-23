using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

/// <summary>索引段大于缓存时，崩溃重开仍能清空并从已提交数据重建索引（#1463）。</summary>
[TestFixture]
internal sealed class SharedIndexClearRecoveryTests : TestBase<SharedIndexClearRecoveryTests>
{
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    protected override void ConfigureEngineOptions(DatabaseEngineOptions options)
    {
        base.ConfigureEngineOptions(options);
        options.Wal.WalDirectory = null;
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("EP-02")]
    public void AnIndexSegmentLargerThanTheCache_RecoversWithTheSameCache()
    {
        const int count = 20_000;
        const int batchSize = 1_000;
        const int moved = 10_000_000;
        var ids = new EntityId[count];

        using (var scope = ServiceProvider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompD>();
            dbe.InitializeArchetypes();

            for (var start = 0; start < count; start += batchSize)
            {
                using var seed = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (var i = start; i < start + batchSize; i++)
                {
                    ids[i] = seed.Spawn<CompDArch>(CompDArch.D.Set(new CompD(i, i, i)));
                }

                Assert.That(seed.Commit(), Is.True);
            }

            // 分批提交，使本测试只触及恢复的整段清理，不要求一次 publish 超过缓存（#831）。
            for (var start = 0; start < count; start += batchSize)
            {
                using var update = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
                for (var i = start; i < start + batchSize; i++)
                {
                    update.OpenMut(ids[i]).Set(CompDArch.D, new CompD(i + 100_000.5f, i + moved, i + 200_000.25));
                }

                Assert.That(update.Commit(), Is.True);
            }

            var indexSegment = dbe._archetypeStates[ArchetypeRegistry.GetMetadata<CompDArch>().ArchetypeId].ClusterState.IndexSegment;
            var cachePages = dbe.MMF.CountDirtyPages().Total;
            Assert.That(cachePages, Is.EqualTo(1_024), "8 MiB 缓存");
            Assert.That(indexSegment.Length, Is.GreaterThan(cachePages), "索引段确实超过缓存");
            dbe.SimulateHardCrash();
        }

        using var reopenedScope = ServiceProvider.CreateScope();
        var reopened = reopenedScope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        reopened.RegisterComponentFromAccessor<CompD>();
        reopened.InitializeArchetypes();

        Assert.That(reopened.CrashRecoveryAtOpen, Is.True);
        Assert.That(reopened.MMF.CountDirtyPages().Total, Is.EqualTo(1_024), "以相同缓存重开");
        using var read = reopened.CreateQuickTransaction();
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.A >= 100_000).Count(), Is.EqualTo(count));
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.A < 100_000).Count(), Is.Zero);
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B >= moved).Count(), Is.EqualTo(count));
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.B < moved).Count(), Is.Zero);
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.C >= 200_000).Count(), Is.EqualTo(count));
        Assert.That(read.Query<CompDArch>().WhereField<CompD>(d => d.C < 200_000).Count(), Is.Zero);
        foreach (var i in new[] { 0, 1, count / 3, count / 2, count - 2, count - 1 })
        {
            var value = read.Open(ids[i]).Read(CompDArch.D);
            Assert.That((value.A, value.B, value.C), Is.EqualTo((i + 100_000.5f, i + moved, i + 200_000.25)), $"entity {i}");
        }
    }
}
