using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Tests for View delta behavior in the context of subscriptions.
/// Focuses on pull-mode Views (no WHERE clause) and their interaction with
/// the subscription Output phase ordering (Refresh → BeginSync → BuildDelta).
/// </summary>
[TestFixture]
class ViewDeltaTests : TestBase<ViewDeltaTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SvEcsPosition>();
        dbe.RegisterComponentFromAccessor<SvEcsVelocity>();
        dbe.RegisterComponentFromAccessor<SvEcsHealth>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. Pull-mode View basic delta behavior
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PullView_FirstRefresh_EmptyView_NoDelta()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Refresh with no entities
        using var tx = dbe.CreateQuickTransaction();
        view.Refresh(tx);
        var delta = view.GetDelta();

        Assert.That(delta.IsEmpty, Is.True);
        Assert.That(view.Count, Is.EqualTo(0));
    }

    [Test]
    public void PullView_RefreshAfterSpawn_ShowsAdded()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Spawn entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Refresh — should see 2 Added
        using var readTx = dbe.CreateQuickTransaction();
        view.Refresh(readTx);
        var delta = view.GetDelta();

        Assert.That(delta.Added.Count, Is.EqualTo(2));
        Assert.That(delta.Removed.Count, Is.EqualTo(0));
        Assert.That(view.Count, Is.EqualTo(2));
    }

    [Test]
    public void PullView_SecondRefresh_SameEntities_NoDelta()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Spawn entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // First refresh — sees Added
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            Assert.That(view.GetDelta().Added.Count, Is.EqualTo(1));
            view.ClearDelta();
        }

        // Second refresh — no new entities → empty delta
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            var delta = view.GetDelta();

            Assert.That(delta.IsEmpty, Is.True, "Second refresh with same entities should produce no delta");
            Assert.That(view.Count, Is.EqualTo(1), "Entity should still be in View");
        }
    }

    [Test]
    public void PullView_SpawnBetweenRefreshes_ShowsOnlyNewAdded()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Spawn entity A
        EntityId idA;
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 0, 0);
            var vel = new SvEcsVelocity(0, 0, 0);
            idA = tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Refresh 1: sees A as Added
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            Assert.That(view.GetDelta().Added.Count, Is.EqualTo(1));
            view.ClearDelta();
        }

        // Spawn entity B
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(2, 0, 0);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Refresh 2: should see ONLY B as Added (A already in _entityIds)
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            var delta = view.GetDelta();

            Assert.That(delta.Added.Count, Is.EqualTo(1), "Only the new entity should be Added");
            Assert.That(view.Count, Is.EqualTo(2), "Both entities should be in View");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. Subscription Output phase simulation (no TCP)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SubscriptionFlow_RefreshAllViews_ThenSubscribe_ThenSpawn_ClientSeesAdded()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Simulate tick 1: no clients → RefreshAllViews (empties View, no entities)
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            view.ClearDelta();
            tx.Commit();
        }

        // Client subscribes — BeginSync captures current entity set
        var syncState = new ViewSubscriptionState();
        IncrementalSyncTracker.BeginSync(syncState, view);
        Assert.That(syncState.SyncSnapshot, Is.Null, "Empty View → null snapshot (immediate sync complete)");

        // Simulate sync complete
        syncState.Phase = SubscriptionPhase.Active;

        // Spawn entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Simulate next tick's Output phase: Refresh View → build delta
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            var delta = view.GetDelta();

            Assert.That(delta.Added.Count, Is.EqualTo(3), "Newly spawned entities should appear as Added");
            Assert.That(view.Count, Is.EqualTo(3));
        }
    }

    [Test]
    public void SubscriptionFlow_EntitiesExistBeforeSubscribe_SyncCapturesThem()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Spawn entities BEFORE client subscribes
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Simulate ticks with no clients → RefreshAllViews populates _entityIds
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            Assert.That(view.Count, Is.EqualTo(2), "View should contain entities after refresh");
            view.ClearDelta();
            tx.Commit();
        }

        // Client subscribes — BeginSync should capture the 2 existing entities
        var syncState = new ViewSubscriptionState();
        IncrementalSyncTracker.BeginSync(syncState, view);

        Assert.That(syncState.SyncSnapshot, Is.Not.Null, "Non-empty View → snapshot for incremental sync");
        Assert.That(syncState.SyncSnapshot.Length, Is.EqualTo(2), "Snapshot should contain 2 entities");
    }

    [Test]
    public void SubscriptionFlow_RefreshPopulatesView_ThenBeginSync_ThenBuildDelta_NoDoubleAdd()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Spawn entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        // Simulate exact Output phase ordering: Phase 1 → Phase 2 → Phase 3
        using var readTx = dbe.CreateQuickTransaction();

        // Phase 1: Refresh (adds entities to _entityIds, builds delta with Added)
        view.Refresh(readTx);
        Assert.That(view.GetDelta().Added.Count, Is.EqualTo(2), "Phase 1: Refresh should find 2 Added");
        Assert.That(view.Count, Is.EqualTo(2));

        // Phase 2: BeginSync captures entity set
        var syncState = new ViewSubscriptionState();
        IncrementalSyncTracker.BeginSync(syncState, view);
        Assert.That(syncState.SyncSnapshot.Length, Is.EqualTo(2), "Phase 2: Sync snapshot should have 2 entities");

        // Phase 3: BuildFromRefreshedView reads delta (should still have Added from Phase 1)
        var delta = view.GetDelta();
        Assert.That(delta.Added.Count, Is.EqualTo(2), "Phase 3: Delta should still have 2 Added (not cleared yet)");

        view.ClearDelta();

        // Next tick: no new entities → delta should be empty
        using var readTx2 = dbe.CreateQuickTransaction();
        view.Refresh(readTx2);
        var delta2 = view.GetDelta();
        Assert.That(delta2.IsEmpty, Is.True, "Next tick with no new entities should produce empty delta");
    }

    [Test]
    public void SubscriptionFlow_SpawnAfterSync_NewEntitiesAppearAsAdded()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        // Tick 1: no entities, subscribe, sync completes immediately
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            view.ClearDelta();
        }

        var syncState = new ViewSubscriptionState();
        IncrementalSyncTracker.BeginSync(syncState, view);
        Assert.That(syncState.SyncSnapshot, Is.Null); // Empty sync
        syncState.Phase = SubscriptionPhase.Active;

        // Tick 2: spawn 5 entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            for (var i = 0; i < 5; i++)
            {
                tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            }

            tx.Commit();
        }

        // Output phase tick 2: Refresh → delta
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            var delta = view.GetDelta();
            Assert.That(delta.Added.Count, Is.EqualTo(5), "5 new entities should appear as Added");

            view.ClearDelta();
        }

        // Tick 3: spawn 3 more
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(4, 5, 6);
            var vel = new SvEcsVelocity(0, 0, 0);
            for (var i = 0; i < 3; i++)
            {
                tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            }

            tx.Commit();
        }

        // Output phase tick 3: Refresh → only 3 new
        using (var tx = dbe.CreateQuickTransaction())
        {
            view.Refresh(tx);
            var delta = view.GetDelta();
            Assert.That(delta.Added.Count, Is.EqualTo(3), "Only 3 NEW entities should appear as Added");
            Assert.That(view.Count, Is.EqualTo(8), "Total should be 8");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. Full DeltaBuilder simulation (no TCP)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void DeltaBuilder_SharedView_ProducesCorrectDelta()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        var published = PublishedView.CreateShared("test", view, SubscriptionPriority.Normal);

        // Spawn entities
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        // Build delta via DeltaBuilder
        var builder = new DeltaBuilder();
        using var readTx = dbe.CreateQuickTransaction();
        var msg = builder.BuildSharedViewDelta(published, readTx, dbe);
        readTx.Commit();

        Assert.That(msg, Is.Not.Null, "Should produce a delta message");
        Assert.That(msg.Value.Added, Is.Not.Null);
        Assert.That(msg.Value.Added.Length, Is.EqualTo(2), "Should have 2 Added entities");
        Assert.That(msg.Value.Added[0].Components, Is.Not.Null.And.Not.Empty, "Added entities should have component data");
    }

    [Test]
    public void DeltaBuilder_SecondCall_OnlyNewEntities()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<SvEcsUnit>().ToView();

        var published = PublishedView.CreateShared("test", view, SubscriptionPriority.Normal);
        var builder = new DeltaBuilder();

        // Tick 1: spawn 2 entities, build delta
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(1, 2, 3);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var msg = builder.BuildSharedViewDelta(published, tx, dbe);
            Assert.That(msg.Value.Added.Length, Is.EqualTo(2));
            tx.Commit();
        }

        // Tick 2: spawn 1 more entity
        using (var tx = dbe.CreateQuickTransaction())
        {
            var pos = new SvEcsPosition(4, 5, 6);
            var vel = new SvEcsVelocity(0, 0, 0);
            tx.Spawn<SvEcsUnit>(SvEcsUnit.Position.Set(in pos), SvEcsUnit.Velocity.Set(in vel));
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        using (var tx = dbe.CreateQuickTransaction())
        {
            var msg = builder.BuildSharedViewDelta(published, tx, dbe);
            Assert.That(msg, Is.Not.Null);
            Assert.That(msg.Value.Added.Length, Is.EqualTo(1), "Only 1 new entity should be Added");
            tx.Commit();
        }
    }
}
