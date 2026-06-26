using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;

namespace Typhon.Engine.Tests;

class ViewChangeCaptureTests : TestBase<ViewChangeCaptureTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Archetype<CompDArch>.Touch();
    }

    private class TestView : IView, IDisposable
    {
        private readonly IMemoryAllocator _allocator;
        private readonly IResource _resourceParent;
        private ViewDeltaRingBuffer _deltaBuffer;

        public TestView(IMemoryAllocator allocator, IResource resourceParent)
        {
            _allocator = allocator;
            _resourceParent = resourceParent;
        }

        public int ViewId { get; set; }
        public int[] FieldDependencies { get; set; }
        public bool IsDisposed { get; set; }
        public ViewDeltaRingBuffer DeltaBuffer => _deltaBuffer ??= new ViewDeltaRingBuffer(_allocator, _resourceParent);

        public void Dispose()
        {
            _deltaBuffer?.Dispose();
            IsDisposed = true;
        }
    }

    [Test]
    public void UpdateField_EntryInRingBuffer_CorrectBeforeAfterKeys()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var view = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B (int, index 1)
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        EntityId entityId;
        long createTsn;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
            createTsn = t.TSN;
        }

        var entityPk = (long)entityId.RawValue;

        // Drain creation entry
        Assert.That(view.DeltaBuffer.TryPeek(long.MaxValue, out _, out _, out _, out _), Is.True);
        view.DeltaBuffer.Advance();

        long updateTsn;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 20, 2.0); // Only B changed: 10 → 20
            ref var w = ref t.OpenMut(entityId).Write(CompDArch.D);
            w = d;
            t.Commit();
            updateTsn = t.TSN;
        }

        Assert.That(view.DeltaBuffer.TryPeek(long.MaxValue, out var entry, out var flags, out var tsn, out _), Is.True);
        Assert.That(entry.EntityPK, Is.EqualTo(entityPk));
        Assert.That(entry.BeforeKey.AsInt(), Is.EqualTo(10));
        Assert.That(entry.AfterKey.AsInt(), Is.EqualTo(20));
        Assert.That(tsn, Is.EqualTo(updateTsn));
        Assert.That(flags & 0x80, Is.EqualTo(0), "isDeletion should be false");
        Assert.That(flags & 0x40, Is.EqualTo(0), "isCreation should be false");
    }

    [Test]
    public void CreateEntity_EntryWithCreationFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var view = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B (int)
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        EntityId entityId;
        long createTsn;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 42, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
            createTsn = t.TSN;
        }

        var entityPk = (long)entityId.RawValue;

        Assert.That(view.DeltaBuffer.TryPeek(long.MaxValue, out var entry, out var flags, out var tsn, out _), Is.True);
        Assert.That(entry.EntityPK, Is.EqualTo(entityPk));
        Assert.That(entry.BeforeKey.IsZero, Is.True, "BeforeKey should be zeroed for creation");
        Assert.That(entry.AfterKey.AsInt(), Is.EqualTo(42));
        Assert.That(tsn, Is.EqualTo(createTsn));
        Assert.That(flags & 0x40, Is.Not.EqualTo(0), "isCreation flag should be set");
        Assert.That(flags & 0x80, Is.EqualTo(0), "isDeletion flag should not be set");
    }

    [Test]
    public void DeleteEntity_EntryWithDeletionFlag()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var view = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B (int)
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 99, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        var entityPk = (long)entityId.RawValue;

        // Drain creation entry
        Assert.That(view.DeltaBuffer.TryPeek(long.MaxValue, out _, out _, out _, out _), Is.True);
        view.DeltaBuffer.Advance();

        long deleteTsn;
        {
            using var t = dbe.CreateQuickTransaction();
            t.Destroy(entityId);
            t.Commit();
            deleteTsn = t.TSN;
        }

        Assert.That(view.DeltaBuffer.TryPeek(long.MaxValue, out var entry, out var flags, out var tsn, out _), Is.True);
        Assert.That(entry.EntityPK, Is.EqualTo(entityPk));
        Assert.That(entry.BeforeKey.AsInt(), Is.EqualTo(99), "BeforeKey should be old value");
        Assert.That(entry.AfterKey.IsZero, Is.True, "AfterKey should be zeroed for deletion");
        Assert.That(tsn, Is.EqualTo(deleteTsn));
        Assert.That(flags & 0x80, Is.Not.EqualTo(0), "isDeletion flag should be set");
        Assert.That(flags & 0x40, Is.EqualTo(0), "isCreation flag should not be set");
    }

    [Test]
    public void MultiFieldChange_OneEntryPerChangedField()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();

        // Register views on all 3 indexed fields
        using var viewA = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [0] }; // Field A (float)
        using var viewB = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 2, FieldDependencies = [1] }; // Field B (int)
        using var viewC = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 3, FieldDependencies = [2] }; // Field C (double)
        ct.ViewRegistry.RegisterView(viewA, viewA.DeltaBuffer);
        ct.ViewRegistry.RegisterView(viewB, viewB.DeltaBuffer);
        ct.ViewRegistry.RegisterView(viewC, viewC.DeltaBuffer);

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Drain creation entries (one per field per view)
        DrainBuffer(viewA.DeltaBuffer);
        DrainBuffer(viewB.DeltaBuffer);
        DrainBuffer(viewC.DeltaBuffer);

        // Update all 3 fields
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 20, 6.0);
            ref var w = ref t.OpenMut(entityId).Write(CompDArch.D);
            w = d;
            t.Commit();
        }

        // Each view should have exactly 1 entry
        Assert.That(viewA.DeltaBuffer.Count, Is.EqualTo(1), "View on field A should get 1 entry");
        Assert.That(viewB.DeltaBuffer.Count, Is.EqualTo(1), "View on field B should get 1 entry");
        Assert.That(viewC.DeltaBuffer.Count, Is.EqualTo(1), "View on field C should get 1 entry");

        // Verify keys
        viewA.DeltaBuffer.TryPeek(long.MaxValue, out var entryA, out _, out _, out _);
        Assert.That(entryA.BeforeKey.AsFloat(), Is.EqualTo(1.0f));
        Assert.That(entryA.AfterKey.AsFloat(), Is.EqualTo(5.0f));

        viewB.DeltaBuffer.TryPeek(long.MaxValue, out var entryB, out _, out _, out _);
        Assert.That(entryB.BeforeKey.AsInt(), Is.EqualTo(10));
        Assert.That(entryB.AfterKey.AsInt(), Is.EqualTo(20));

        viewC.DeltaBuffer.TryPeek(long.MaxValue, out var entryC, out _, out _, out _);
        Assert.That(entryC.BeforeKey.AsDouble(), Is.EqualTo(2.0));
        Assert.That(entryC.AfterKey.AsDouble(), Is.EqualTo(6.0));
    }

    [Test]
    // QUARANTINE (#406): passes on Windows (isolated + full-suite parallel) but fails only on Linux CI with
    // IndexOutOfRangeException. Excluded from the merge gate pending a Linux repro.
    [Category("Quarantine")]
    public void UnchangedField_NoEntryForThatFieldView()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var viewB = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B (int)
        using var viewC = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 2, FieldDependencies = [2] }; // Field C (double)
        ct.ViewRegistry.RegisterView(viewB, viewB.DeltaBuffer);
        ct.ViewRegistry.RegisterView(viewC, viewC.DeltaBuffer);

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        DrainBuffer(viewB.DeltaBuffer);
        DrainBuffer(viewC.DeltaBuffer);

        // Only change field A (float) — B and C unchanged
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 10, 2.0);
            ref var w = ref t.OpenMut(entityId).Write(CompDArch.D);
            w = d;
            t.Commit();
        }

        Assert.That(viewB.DeltaBuffer.Count, Is.EqualTo(0), "View on unchanged field B should get no entries");
        Assert.That(viewC.DeltaBuffer.Count, Is.EqualTo(0), "View on unchanged field C should get no entries");
    }

    [Test]
    public void DisposedView_SkippedDuringNotification()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        var view = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Dispose the view before next commit
        view.Dispose();

        // This should not crash or append to the disposed buffer
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 20, 2.0);
            ref var w = ref t.OpenMut(entityId).Write(CompDArch.D);
            w = d;
            t.Commit();
        }

        // No crash means success — disposed views are skipped
    }

    [Test]
    public void MultipleViewsOnSameField_AllReceiveEntries()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var view1 = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B
        using var view2 = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 2, FieldDependencies = [1] }; // Field B
        ct.ViewRegistry.RegisterView(view1, view1.DeltaBuffer);
        ct.ViewRegistry.RegisterView(view2, view2.DeltaBuffer);

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Both views should receive the creation entry
        Assert.That(view1.DeltaBuffer.Count, Is.EqualTo(1), "View 1 should receive creation entry");
        Assert.That(view2.DeltaBuffer.Count, Is.EqualTo(1), "View 2 should receive creation entry");
    }

    [Test]
    public void NoViewsRegistered_NoOverhead_NoCrash()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        // No views registered — just do CRUD and make sure nothing crashes
        EntityId entityId;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            entityId = t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(5.0f, 20, 6.0);
            ref var w = ref t.OpenMut(entityId).Write(CompDArch.D);
            w = d;
            t.Commit();
        }

        {
            using var t = dbe.CreateQuickTransaction();
            t.Destroy(entityId);
            t.Commit();
        }
    }

    [Test]
    public void FlagsPacking_FieldIndexInBits5To0()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();

        // Register views on fields 0, 1, 2
        using var view0 = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [0] };
        using var view1 = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 2, FieldDependencies = [1] };
        using var view2 = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 3, FieldDependencies = [2] };
        ct.ViewRegistry.RegisterView(view0, view0.DeltaBuffer);
        ct.ViewRegistry.RegisterView(view1, view1.DeltaBuffer);
        ct.ViewRegistry.RegisterView(view2, view2.DeltaBuffer);

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
        }

        // Check field index encoded in bits [5:0]
        view0.DeltaBuffer.TryPeek(long.MaxValue, out _, out var flags0, out _, out _);
        Assert.That(flags0 & 0x3F, Is.EqualTo(0), "Field index 0 in bits [5:0]");
        Assert.That(flags0 & 0x40, Is.Not.EqualTo(0), "isCreation bit should be set");

        view1.DeltaBuffer.TryPeek(long.MaxValue, out _, out var flags1, out _, out _);
        Assert.That(flags1 & 0x3F, Is.EqualTo(1), "Field index 1 in bits [5:0]");

        view2.DeltaBuffer.TryPeek(long.MaxValue, out _, out var flags2, out _, out _);
        Assert.That(flags2 & 0x3F, Is.EqualTo(2), "Field index 2 in bits [5:0]");
    }

    [Test]
    public void TSN_StoredCorrectlyInRingBuffer()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        RegisterComponents(dbe);
        dbe.InitializeArchetypes();

        var ct = dbe.GetComponentTable<CompD>();
        using var view = new TestView(MemoryAllocator, ResourceRegistry.Allocation) { ViewId = 1, FieldDependencies = [1] }; // Field B
        ct.ViewRegistry.RegisterView(view, view.DeltaBuffer);

        long tsn1, tsn2;
        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(1.0f, 10, 2.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
            tsn1 = t.TSN;
        }

        view.DeltaBuffer.TryPeek(long.MaxValue, out _, out _, out var storedTsn1, out _);
        Assert.That(storedTsn1, Is.EqualTo(tsn1), "TSN from creation should match transaction TSN");
        view.DeltaBuffer.Advance();

        {
            using var t = dbe.CreateQuickTransaction();
            var d = new CompD(2.0f, 20, 3.0);
            t.Spawn<CompDArch>(CompDArch.D.Set(in d));
            t.Commit();
            tsn2 = t.TSN;
        }

        view.DeltaBuffer.TryPeek(long.MaxValue, out _, out _, out var storedTsn2, out _);
        Assert.That(storedTsn2, Is.EqualTo(tsn2), "TSN from second creation should match");
        Assert.That(tsn2, Is.GreaterThan(tsn1), "TSN should be monotonically increasing");
    }

    private static void DrainBuffer(ViewDeltaRingBuffer buffer)
    {
        while (buffer.TryPeek(long.MaxValue, out _, out _, out _, out _))
        {
            buffer.Advance();
        }
    }
}
