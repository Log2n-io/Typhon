using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

[TestFixture]
class ViewRegistryTests
{
    private ServiceProvider _sp;
    private IMemoryAllocator _allocator;
    private IResource _parent;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var sc = new ServiceCollection();
        sc.AddResourceRegistry()
          .AddMemoryAllocator();
        _sp = sc.BuildServiceProvider();
        _allocator = _sp.GetRequiredService<IMemoryAllocator>();
        _parent = _sp.GetRequiredService<IResourceRegistry>().Allocation;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _sp?.Dispose();
    }

    private class MockView : IView, IDisposable
    {
        private readonly IMemoryAllocator _allocator;
        private readonly IResource _resourceParent;
        private ViewDeltaRingBuffer _deltaBuffer;

        public MockView(IMemoryAllocator allocator, IResource resourceParent)
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
    public void EmptyRegistry_AllFieldsReturnEmptySpan()
    {
        var registry = new ViewRegistry(4);

        for (var i = 0; i < 4; i++)
        {
            Assert.That(registry.GetViewsForField(i).Length, Is.EqualTo(0));
        }
    }

    [Test]
    public void RegisterSingleView_SingleFieldDependency_ReturnedByGetViewsForField()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [2] };

        registry.RegisterView(view, view.DeltaBuffer);

        var views = registry.GetViewsForField(2);
        Assert.That(views.Length, Is.EqualTo(1));
        Assert.That(views[0].View, Is.SameAs(view));
    }

    [Test]
    public void RegisterSingleView_MultipleFieldDependencies_ReturnedForEachField()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [0, 2, 3] };

        registry.RegisterView(view, view.DeltaBuffer);

        Assert.That(registry.GetViewsForField(0).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(0)[0].View, Is.SameAs(view));

        Assert.That(registry.GetViewsForField(1).Length, Is.EqualTo(0), "Field 1 not in dependencies");

        Assert.That(registry.GetViewsForField(2).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(2)[0].View, Is.SameAs(view));

        Assert.That(registry.GetViewsForField(3).Length, Is.EqualTo(1));
        Assert.That(registry.GetViewsForField(3)[0].View, Is.SameAs(view));
    }

    [Test]
    public void RegisterMultipleViews_SameField_AllReturned()
    {
        var registry = new ViewRegistry(4);
        var viewA = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [1] };
        var viewB = new MockView(_allocator, _parent) { ViewId = 2, FieldDependencies = [1] };
        var viewC = new MockView(_allocator, _parent) { ViewId = 3, FieldDependencies = [1] };

        registry.RegisterView(viewA, viewA.DeltaBuffer);
        registry.RegisterView(viewB, viewB.DeltaBuffer);
        registry.RegisterView(viewC, viewC.DeltaBuffer);

        var views = registry.GetViewsForField(1);
        Assert.That(views.Length, Is.EqualTo(3));
        Assert.That(views[0].View, Is.SameAs(viewA));
        Assert.That(views[1].View, Is.SameAs(viewB));
        Assert.That(views[2].View, Is.SameAs(viewC));
    }

    [Test]
    public void DeregisterView_Removed_OthersRemain()
    {
        var registry = new ViewRegistry(4);
        var viewA = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [0, 1] };
        var viewB = new MockView(_allocator, _parent) { ViewId = 2, FieldDependencies = [0, 1] };

        registry.RegisterView(viewA, viewA.DeltaBuffer);
        registry.RegisterView(viewB, viewB.DeltaBuffer);

        registry.DeregisterView(viewA);

        var field0 = registry.GetViewsForField(0);
        Assert.That(field0.Length, Is.EqualTo(1));
        Assert.That(field0[0].View, Is.SameAs(viewB));

        var field1 = registry.GetViewsForField(1);
        Assert.That(field1.Length, Is.EqualTo(1));
        Assert.That(field1[0].View, Is.SameAs(viewB));
    }

    [Test]
    public void DeregisterNonExistentView_NoCrash()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [0, 1] };

        // Deregister a view that was never registered
        Assert.DoesNotThrow(() => registry.DeregisterView(view));
    }

    [Test]
    public void ViewCount_TracksCorrectly()
    {
        var registry = new ViewRegistry(4);
        Assert.That(registry.ViewCount, Is.EqualTo(0));

        var viewA = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [0] };
        var viewB = new MockView(_allocator, _parent) { ViewId = 2, FieldDependencies = [1] };

        registry.RegisterView(viewA, viewA.DeltaBuffer);
        Assert.That(registry.ViewCount, Is.EqualTo(1));

        registry.RegisterView(viewB, viewB.DeltaBuffer);
        Assert.That(registry.ViewCount, Is.EqualTo(2));

        registry.DeregisterView(viewA);
        Assert.That(registry.ViewCount, Is.EqualTo(1));

        registry.DeregisterView(viewB);
        Assert.That(registry.ViewCount, Is.EqualTo(0));
    }

    [Test]
    public void RegisterView_FieldDependencyOutOfRange_Throws()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [2, 5] };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => registry.RegisterView(view, view.DeltaBuffer));
        Assert.That(ex.Message, Does.Contain("field dependency 5"));
        Assert.That(ex.Message, Does.Contain("4 fields"));
    }

    [Test]
    public void GetViewsForField_OutOfRange_ReturnsEmptySpan()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [0] };
        registry.RegisterView(view, view.DeltaBuffer);

        Assert.That(registry.GetViewsForField(-1).Length, Is.EqualTo(0));
        Assert.That(registry.GetViewsForField(4).Length, Is.EqualTo(0));
        Assert.That(registry.GetViewsForField(int.MaxValue).Length, Is.EqualTo(0));
    }

    [Test]
    public void ExplicitRegistration_WithComponentTag_RoundTrips()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [] };

        registry.RegisterView(view, view.DeltaBuffer, [0, 2], 1);

        var field0 = registry.GetViewsForField(0);
        Assert.That(field0.Length, Is.EqualTo(1));
        Assert.That(field0[0].View, Is.SameAs(view));
        Assert.That(field0[0].ComponentTag, Is.EqualTo(1));

        var field2 = registry.GetViewsForField(2);
        Assert.That(field2.Length, Is.EqualTo(1));
        Assert.That(field2[0].View, Is.SameAs(view));
        Assert.That(field2[0].ComponentTag, Is.EqualTo(1));

        // Unregistered field should be empty
        Assert.That(registry.GetViewsForField(1).Length, Is.EqualTo(0));
    }

    [Test]
    public void ExplicitRegistration_SameViewTwoTags_BothPresent()
    {
        var registry = new ViewRegistry(4);
        var view = new MockView(_allocator, _parent) { ViewId = 1, FieldDependencies = [] };

        registry.RegisterView(view, view.DeltaBuffer, [0], 0);
        registry.RegisterView(view, view.DeltaBuffer, [0], 1);

        var field0 = registry.GetViewsForField(0);
        Assert.That(field0.Length, Is.EqualTo(2));
        Assert.That(field0[0].View, Is.SameAs(view));
        Assert.That(field0[0].ComponentTag, Is.EqualTo(0));
        Assert.That(field0[1].View, Is.SameAs(view));
        Assert.That(field0[1].ComponentTag, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(5000)]
    public void ConcurrentReadDuringWrite_NoTornReads()
    {
        var registry = new ViewRegistry(8);
        var running = 1;
        var errors = 0;

        // Writer thread: continuously registers and deregisters views
        var writer = Task.Run(() =>
        {
            var views = new MockView[20];
            for (var i = 0; i < views.Length; i++)
            {
                views[i] = new MockView(_allocator, _parent) { ViewId = i, FieldDependencies = [i % 8] };
            }

            while (Volatile.Read(ref running) == 1)
            {
                for (var i = 0; i < views.Length; i++)
                {
                    registry.RegisterView(views[i], views[i].DeltaBuffer);
                }
                for (var i = 0; i < views.Length; i++)
                {
                    registry.DeregisterView(views[i]);
                }
            }
        });

        // 4 reader threads: continuously read views for all fields
        var readers = new Task[4];
        for (var r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(() =>
            {
                while (Volatile.Read(ref running) == 1)
                {
                    for (var f = 0; f < 8; f++)
                    {
                        try
                        {
                            var span = registry.GetViewsForField(f);
                            // Access every element to detect torn reads
                            for (var i = 0; i < span.Length; i++)
                            {
                                var v = span[i].View;
                                if (v == null)
                                {
                                    Interlocked.Increment(ref errors);
                                }
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref errors);
                        }
                    }
                }
            });
        }

        // Run for ~100ms
        Thread.Sleep(100);
        Volatile.Write(ref running, 0);

        Task.WaitAll([writer, ..readers]);

        Assert.That(errors, Is.EqualTo(0), "No torn reads or null entries should be observed");
    }
}