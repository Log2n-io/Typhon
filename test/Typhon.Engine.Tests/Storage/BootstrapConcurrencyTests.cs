using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// The bootstrap dictionary is written by run-time registrations (realms, grid config, schema version, seed revision, occupancy growth) while the
/// checkpoint serializes it into the meta page (#1292).
/// </summary>
[TestFixture]
internal sealed class BootstrapConcurrencyTests : TestBase<BootstrapConcurrencyTests>
{
    // No periodic checkpoint: an oversized probe key must not reach the checkpoint's own persist.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    /// <summary>
    /// A key added while the meta page is persisted: the serializer's enumeration threw, inside the meta page's latch, which nothing released — every
    /// later meta writer then failed to latch it.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public void AKeyAddedWhileTheMetaPageIsPersisted_BreaksNeither()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var mmf = dbe.MMF;

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Exception persistFault = null, writeFault = null;
        var persister = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    mmf.PersistMetaNow();
                }
            }
            catch (Exception e)
            {
                persistFault = e;
            }
        });
        var writer = Task.Run(() =>
        {
            try
            {
                for (var i = 0; !stop.IsCancellationRequested; i++)
                {
                    mmf.Bootstrap.SetInt($"probe.{i & 63}", i);
                    mmf.Bootstrap.Remove($"probe.{(i + 32) & 63}");
                }
            }
            catch (Exception e)
            {
                writeFault = e;
            }
        });
        Task.WaitAll(persister, writer);

        Assert.That(persistFault, Is.Null, "the meta page's persist, beside a bootstrap writer");
        Assert.That(writeFault, Is.Null, "the bootstrap writer, beside the persist");
        Assert.That(() => Task.Run(mmf.PersistMetaNow).GetAwaiter().GetResult(), Throws.Nothing, "a later meta writer, on another thread");
    }

    /// <summary>A persist of the meta page that throws — a bootstrap too large for it — leaves the page unlatched for the next meta writer.</summary>
    [Test]
    [CancelAfter(10_000)]
    public void AMetaPersistThatThrows_LeavesTheMetaPageToTheNextWriter()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.InitializeArchetypes();
        var mmf = dbe.MMF;

        mmf.Bootstrap.SetString("probe.oversized", new string('x', PagedMMF.PageSize));
        Assert.That(mmf.PersistMetaNow, Throws.Exception, "premise: the bootstrap no longer fits the meta page");
        mmf.Bootstrap.Remove("probe.oversized");
        Assert.That(() => Task.Run(mmf.PersistMetaNow).GetAwaiter().GetResult(), Throws.Nothing, "the next meta writer, on another thread");
    }
}
