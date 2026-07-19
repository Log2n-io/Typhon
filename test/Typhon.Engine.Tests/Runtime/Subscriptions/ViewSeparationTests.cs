using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests that published Views cannot be used as system inputs, and vice versa.
/// </summary>
[NonParallelizable]
[TestFixture]
class ViewSeparationTests : TestBase<ViewSeparationTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.RegisterComponentFromAccessor<EcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    [Test]
    public void UseAsSystemInput_ThenPublish_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<EcsUnit>().ToView();

        // Create runtime with the view as system input → marks IsSystemInput
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("TestSystem", _ => { }, input: () => view);
        }, new RuntimeOptions { WorkerCount = 1 });

        // Publishing the same view should throw
        var ex = Assert.Throws<InvalidOperationException>(() => runtime.PublishView("test_view", view));
        Assert.That(ex.Message, Does.Contain("system input"));
    }

    [Test]
    public void PublishView_SameNameTwice_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view1 = tx.Query<EcsUnit>().ToView();

        using var tx2 = dbe.CreateQuickTransaction();
        var view2 = tx2.Query<EcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1 });

        runtime.PublishView("test_view", view1);
        Assert.Throws<ArgumentException>(() => runtime.PublishView("test_view", view2));
    }

    [Test]
    public void PublishView_SameInstanceTwice_Throws()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<EcsUnit>().ToView();

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1 });

        runtime.PublishView("view_a", view);
        var ex = Assert.Throws<InvalidOperationException>(() => runtime.PublishView("view_b", view));
        Assert.That(ex.Message, Does.Contain("already published"));
    }

    [Test]
    public void SeparateViewInstances_SameQuery_NoConflict()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var viewForSystem = tx.Query<EcsUnit>().ToView();

        using var tx2 = dbe.CreateQuickTransaction();
        var viewForSubs = tx2.Query<EcsUnit>().ToView();

        // Separate instances — no conflict
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").QuerySystem("TestSystem", _ => { }, input: () => viewForSystem);
        }, new RuntimeOptions { WorkerCount = 1 });

        Assert.DoesNotThrow(() => runtime.PublishView("test_view", viewForSubs));
        Assert.That(runtime.PublishedViews.Count, Is.EqualTo(1));
    }

    [Test]
    public void PublishView_SetsIsPublishedFlag()
    {
        using var dbe = SetupEngine();
        using var tx = dbe.CreateQuickTransaction();
        var view = tx.Query<EcsUnit>().ToView();

        Assert.That(view.IsPublished, Is.False);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Noop", _ => { });
        }, new RuntimeOptions { WorkerCount = 1 });

        runtime.PublishView("test_view", view);

        Assert.That(view.IsPublished, Is.True);
    }
}
