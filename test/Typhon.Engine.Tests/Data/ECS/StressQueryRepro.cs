using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Stress test for EcsQuery at 1K+ entity scale.
/// Also validates UowRegistry graceful shutdown when transactions are still active.
/// </summary>
[NonParallelizable]
class StressQueryRepro : TestBase<StressQueryRepro>
{
    [Test]
    [CancelAfter(5000)]
    public void WhereField_1K_Execute_And_GracefulShutdown()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();

        for (int batch = 0; batch < 10; batch++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 100; i++)
            {
                var d = new CompD(1.0f, batch * 100 + i, 2.0);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            tx.Commit();
        }

        // Query with active transaction — tests targeted scan at scale
        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 500).Execute();
        Assert.That(result.Count, Is.EqualTo(500));

        // Explicit dispose while tx2 is still active — tests UowRegistry shutdown safety
        dbe.Dispose();
    }

    [Test]
    [CancelAfter(15000)]
    [Property("CacheSize", 4 * 1024 * 1024)] // 4MB cache — matches benchmark scale
    public void WhereField_10K_Execute()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompD>();
        dbe.InitializeArchetypes();

        for (int batch = 0; batch < 20; batch++)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < 500; i++)
            {
                var d = new CompD(1.0f, batch * 500 + i, 2.0);
                tx.Spawn<CompDArch>(CompDArch.D.Set(in d));
            }
            tx.Commit();
        }

        using var tx2 = dbe.CreateQuickTransaction();
        var result = tx2.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5000).Execute();
        Assert.That(result.Count, Is.EqualTo(5000));

        // Also test view creation at 10K scale
        using var tx3 = dbe.CreateQuickTransaction();
        using var view = tx3.Query<CompDArch>().WhereField<CompD>(d => d.B >= 5000).ToView();
        Assert.That(view.Count, Is.EqualTo(5000));
    }
}
