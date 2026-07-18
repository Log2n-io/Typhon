using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Typhon.Engine.Tests;

class BtreeTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;
    private ILogger<BtreeTests> _logger;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("MemPageCount");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("MemPageCount")! : PagedMMF.MinimumMemPageCount;
        dcs *= PagedMMF.PageSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = true;
            });

        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();

        _logger = _serviceProvider.GetRequiredService<ILogger<BtreeTests>>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    [Test]
    unsafe public void ForwardInsertionTest()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            tree.Add(10, 10, ref accessor);
            Assert.That(tree[10], Is.EqualTo(10));
            tree.Add(15, 15, ref accessor);
            tree.Add(20, 20, ref accessor);
            Assert.That(tree[20], Is.EqualTo(20));
            tree.Add(50, 50, ref accessor);
            tree.Add(80, 80, ref accessor);
            Assert.That(tree[80], Is.EqualTo(80));
            tree.Add(90, 90, ref accessor);
            Assert.That(tree[90], Is.EqualTo(90));

            tree.Add(100, 100, ref accessor);
            Assert.That(tree[100], Is.EqualTo(100));
            tree.Add(120, 120, ref accessor);
            Assert.That(tree[120], Is.EqualTo(120));
            tree.Add(140, 140, ref accessor);
            Assert.That(tree[140], Is.EqualTo(140));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void ForwardFloatInsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new FloatSingleBTree<PersistentStore>(segment);

            tree.Add(-0.10f, 10, ref accessor);
            Assert.That(tree[-0.10f], Is.EqualTo(10));
            tree.Add(0.15f, 15, ref accessor);
            tree.Add(0.20f, 20, ref accessor);
            Assert.That(tree[0.20f], Is.EqualTo(20));
            tree.Add(0.50f, 50, ref accessor);
            tree.Add(0.80f, 80, ref accessor);
            Assert.That(tree[0.80f], Is.EqualTo(80));
            tree.Add(-0.90f, 90, ref accessor);
            Assert.That(tree[-0.90f], Is.EqualTo(90));

            tree.Add(0.101f, 100, ref accessor);
            Assert.That(tree[0.101f], Is.EqualTo(100));
            tree.Add(0.121f, 120, ref accessor);
            Assert.That(tree[0.121f], Is.EqualTo(120));
            tree.Add(0.141f, 140, ref accessor);
            Assert.That(tree[0.141f], Is.EqualTo(140));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void ReverseInsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            tree.Add(140, 140, ref accessor);
            Assert.That(tree[140], Is.EqualTo(140));
            tree.Add(120, 120, ref accessor);
            Assert.That(tree[120], Is.EqualTo(120));
            tree.Add(100, 100, ref accessor);
            Assert.That(tree[100], Is.EqualTo(100));
            tree.Add(90, 90, ref accessor);
            Assert.That(tree[90], Is.EqualTo(90));
            tree.Add(80, 80, ref accessor);
            Assert.That(tree[80], Is.EqualTo(80));
            tree.Add(50, 50, ref accessor);

            tree.Add(20, 20, ref accessor);
            Assert.That(tree[20], Is.EqualTo(20));
            tree.Add(15, 15, ref accessor);

            tree.Add(10, 10, ref accessor);
            Assert.That(tree[10], Is.EqualTo(10));

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void ReverseString64InsertionTest()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(IndexString64Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new String64SingleBTree<PersistentStore>(segment);

            tree.Add("140", 140, ref accessor);
            Assert.That(tree["140"], Is.EqualTo(140));
            tree.Add("120", 120, ref accessor);
            Assert.That(tree["120"], Is.EqualTo(120));
            tree.Add("100", 100, ref accessor);
            Assert.That(tree["100"], Is.EqualTo(100));
            tree.Add("90", 90, ref accessor);
            Assert.That(tree["90"], Is.EqualTo(90));
            tree.Add("80", 80, ref accessor);
            Assert.That(tree["80"], Is.EqualTo(80));
            tree.Add("50", 50, ref accessor);

            tree.Add("20", 20, ref accessor);
            Assert.That(tree["20"], Is.EqualTo(20));
            tree.Add("15", 15, ref accessor);

            tree.Add("10", 10, ref accessor);
            Assert.That(tree["10"], Is.EqualTo(10));

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckTree()
    {
        var values = new int[] {
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70,
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200,
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            foreach (var v in values)
            {
                tree.Add(v, v, ref accessor);
                tree.CheckConsistency(ref accessor);
            }

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }


    [Test]
    unsafe public void CheckRemove()
    {
        var values = new int[] {
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70,
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200,
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        var valuesToRemove = new int[] {
            200, 10, 100, 5, 50, 70,
            35, 9, 99, 3,
            241, 148, 77, 91, 142, 22, 88,
            404, 6, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int loopC = 0; loopC < 2; loopC++)
            {
                foreach (var v in values)
                {
                    tree.Add(v, v + 1, ref accessor);
                }

                Assert.That(tree.Remove(8080, out var _, ref accessor), Is.False);
                tree.CheckConsistency(ref accessor);

                foreach (var v in valuesToRemove)
                {
                    Assert.That(tree.Remove(v, out var val, ref accessor), Is.True, () => $"Failed removed key {v}");
                    Assert.That(val, Is.EqualTo(v + 1));
                    tree.CheckConsistency(ref accessor);
                }

                for (int i = 0; i < values.Length; i++)
                {
                    int value = values[i];
                    if (valuesToRemove.Contains(value)) continue;

                    Assert.That(tree.Remove(value, out var val, ref accessor), Is.True, () => $"Failed removed key {value}");
                    Assert.That(val, Is.EqualTo(value + 1));
                    tree.CheckConsistency(ref accessor);
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckRemoveL16()
    {
        var values = new short[] {
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70,
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200,
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        var valuesToRemove = new short[] {
            200, 10, 100, 5, 50, 70,
            35, 9, 99, 3,
            241, 148, 77, 91, 142, 22, 88,
            404, 6, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index16Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new ShortSingleBTree<PersistentStore>(segment);

            for (int loopC = 0; loopC < 2; loopC++)
            {
                foreach (var v in values)
                {
                    tree.Add(v, v + 1, ref accessor);
                }

                Assert.That(tree.Remove((short)8080, out var _, ref accessor), Is.False);
                tree.CheckConsistency(ref accessor);

                foreach (var v in valuesToRemove)
                {
                    Assert.That(tree.Remove(v, out var val, ref accessor), Is.True, () => $"Failed removed key {v}");
                    Assert.That(val, Is.EqualTo(v + 1));
                    tree.CheckConsistency(ref accessor);
                }

                for (int i = 0; i < values.Length; i++)
                {
                    short value = values[i];
                    if (valuesToRemove.Contains(value)) continue;

                    Assert.That(tree.Remove(value, out var val, ref accessor), Is.True, () => $"Failed removed key {value}");
                    Assert.That(val, Is.EqualTo(value + 1));
                    tree.CheckConsistency(ref accessor);
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckRemoveL64()
    {
        var values = new long[] {
            1, 2, 3, 10, 100, 20, 33, 5, 50, 70,
            35, 9, 99, 101, 109, 103, 102, 40, 51, 200,
            241, 148, 400, 123, 89, 77, 91, 142, 22, 88,
            404, 6, 221, 301, 298, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        var valuesToRemove = new long[] {
            200, 10, 100, 5, 50, 70,
            35, 9, 99, 3,
            241, 148, 77, 91, 142, 22, 88,
            404, 6, 87, 550, 403, 503, 531,
            72, 81, 499, 98, 912
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index64Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new LongSingleBTree<PersistentStore>(segment);

            for (int loopC = 0; loopC < 2; loopC++)
            {
                foreach (var v in values)
                {
                    tree.Add(v, (int)(v + 1), ref accessor);
                }

                Assert.That(tree.Remove(8080L, out var _, ref accessor), Is.False);
                tree.CheckConsistency(ref accessor);

                foreach (var v in valuesToRemove)
                {
                    Assert.That(tree.Remove(v, out var val, ref accessor), Is.True, () => $"Failed removed key {v}");
                    Assert.That(val, Is.EqualTo((int)(v + 1)));
                    tree.CheckConsistency(ref accessor);
                }

                for (int i = 0; i < values.Length; i++)
                {
                    long value = values[i];
                    if (valuesToRemove.Contains(value)) continue;

                    Assert.That(tree.Remove(value, out var val, ref accessor), Is.True, () => $"Failed removed key {value}");
                    Assert.That(val, Is.EqualTo((int)(value + 1)));
                    tree.CheckConsistency(ref accessor);
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckRemoveString64()
    {
        var values = new[] {
            "001", "002", "003", "010", "100", "020", "033", "005", "050", "070",
            "035", "009", "099", "101", "109", "103", "102", "040", "051", "200",
            "241", "148", "400", "123", "089", "077", "091", "142", "022", "088",
            "404", "006", "221", "301", "298", "087", "550", "403", "503", "531",
            "072", "081", "499", "098", "912"
        };

        var valuesToRemove = new[] {
            "200", "010", "100", "005", "050", "070",
            "035", "009", "099", "003",
            "241", "148", "077", "091", "142", "022", "088",
            "404", "006", "087", "550", "403", "503", "531",
            "072", "081", "499", "098", "912"
        };

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(IndexString64Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new String64SingleBTree<PersistentStore>(segment);

            for (int loopC = 0; loopC < 2; loopC++)
            {
                foreach (var v in values)
                {
                    tree.Add(v, int.Parse(v) + 1, ref accessor);
                }

                Assert.That(tree.Remove("8080", out var _, ref accessor), Is.False);
                tree.CheckConsistency(ref accessor);

                foreach (var v in valuesToRemove)
                {
                    Assert.That(tree.Remove(v, out var val, ref accessor), Is.True, () => $"Failed removed key {v}");
                    Assert.That(val, Is.EqualTo(int.Parse(v) + 1));
                    tree.CheckConsistency(ref accessor);
                }

                for (int i = 0; i < values.Length; i++)
                {
                    string value = values[i];
                    if (valuesToRemove.Contains(value)) continue;

                    Assert.That(tree.Remove(value, out var val, ref accessor), Is.True, () => $"Failed removed key {value}");
                    Assert.That(val, Is.EqualTo(int.Parse(value) + 1));
                    tree.CheckConsistency(ref accessor);
                }
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);

            var eid0 = tree.Add(1, 10, ref accessor);
            var eid1 = tree.Add(3, 30, ref accessor);
            var eid2 = tree.Add(2, 20, ref accessor);
            var eid3 = tree.Add(2, 21, ref accessor);
            var eid4 = tree.Add(1, 11, ref accessor);
            var eid5 = tree.Add(3, 31, ref accessor);
            var eid6 = tree.Add(2, 22, ref accessor);
            var eid7 = tree.Add(1, 12, ref accessor);

            {
                using var a = tree.TryGetMultiple(1, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(2, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(3, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
            }

            tree.RemoveValue(1, eid0, 10, ref accessor);
            tree.RemoveValue(1, eid7, 12, ref accessor);
            tree.RemoveValue(1, eid4, 11, ref accessor);

            {
                using var a = tree.TryGetMultiple(1, ref accessor);
                Assert.That(a.IsValid, Is.False);
            }

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckByteMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index16Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new ByteMultipleBTree<PersistentStore>(segment);

            var eid0 = tree.Add(1, 10, ref accessor);
            var eid1 = tree.Add(3, 30, ref accessor);
            var eid2 = tree.Add(2, 20, ref accessor);
            var eid3 = tree.Add(2, 21, ref accessor);
            var eid4 = tree.Add(1, 11, ref accessor);
            var eid5 = tree.Add(3, 31, ref accessor);
            var eid6 = tree.Add(2, 22, ref accessor);
            var eid7 = tree.Add(1, 12, ref accessor);

            {
                using var a = tree.TryGetMultiple(1, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(2, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(3, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
            }

            tree.RemoveValue(1, eid0, 10, ref accessor);
            tree.RemoveValue(1, eid7, 12, ref accessor);
            tree.RemoveValue(1, eid4, 11, ref accessor);

            {
                using var a = tree.TryGetMultiple(1, ref accessor);
                Assert.That(a.IsValid, Is.False);
            }

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void CheckFloatMultipleTree()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();

        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new FloatMultipleBTree<PersistentStore>(segment);

            var eid0 = tree.Add(1.1f, 10, ref accessor);
            var eid1 = tree.Add(3.1f, 30, ref accessor);
            var eid2 = tree.Add(2.1f, 20, ref accessor);
            var eid3 = tree.Add(2.1f, 21, ref accessor);
            var eid4 = tree.Add(1.1f, 11, ref accessor);
            var eid5 = tree.Add(3.1f, 31, ref accessor);
            var eid6 = tree.Add(2.1f, 22, ref accessor);
            var eid7 = tree.Add(1.1f, 12, ref accessor);

            {
                using var a = tree.TryGetMultiple(1.1f, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(2.1f, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(3));
            }

            {
                using var a = tree.TryGetMultiple(3.1f, ref accessor);
                Assert.That(a.IsValid, Is.True);
                Assert.That(a.ReadOnlyElements.Length, Is.EqualTo(2));
            }

            tree.RemoveValue(1.1f, eid0, 10, ref accessor);
            tree.RemoveValue(1.1f, eid7, 12, ref accessor);
            tree.RemoveValue(1.1f, eid4, 11, ref accessor);

            {
                using var a = tree.TryGetMultiple(1, ref accessor);
                Assert.That(a.IsValid, Is.False);
            }

            tree.CheckConsistency(ref accessor);

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void CheckSingleWithPersistence()
    {
        const int itemCount = 10000;

        Dictionary<float, int> items = new Dictionary<float, int>(itemCount);
        var segmentIndex = 0;

        {
            using var scope = _serviceProvider.CreateScope();
            using var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
            var changeSet = mmf.CreateChangeSet();

            var segment = mmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk), changeSet);
            segmentIndex = segment.RootPageIndex;
            var depth = epochManager.EnterScope();
            try
            {
                var accessor = segment.CreateChunkAccessor(changeSet);
                var tree = new FloatSingleBTree<PersistentStore>(segment);

                var rand = new Random(1234);
                var curValue = 12;

                var sw = new Stopwatch();

                for (int i = 0; i < itemCount; i++)
                {
                    float key;
                    while (true)
                    {
                        key = rand.NextSingle();
                        if (items.TryAdd(key, curValue))
                        {
                            break;
                        }
                    }
                    tree.Add(key, curValue, ref accessor);
                    curValue++;
                }

                changeSet.SaveChanges();

                accessor.Dispose();
            }
            finally
            {
                epochManager.ExitScope(depth);
            }
        }

        {
            using var scope = _serviceProvider.CreateScope();
            using var mmf = scope.ServiceProvider.GetRequiredService<ManagedPagedMMF>();
            using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
            var segment = mmf.LoadChunkBasedSegment(segmentIndex, sizeof(Index32Chunk));
            var depth = epochManager.EnterScope();
            try
            {
                var accessor = segment.CreateChunkAccessor();
                var tree = new FloatSingleBTree<PersistentStore>(segment, true);

                foreach (var kvp in items)
                {
                    var result = tree.TryGet(kvp.Key, ref accessor);
                    Assert.That(result.IsSuccess, Is.True, $"Key {kvp.Key} not found after reload");
                    Assert.That(result.Value, Is.EqualTo(kvp.Value), $"Wrong value for key {kvp.Key}");
                }

                accessor.Dispose();
            }
            finally
            {
                epochManager.ExitScope(depth);
            }
        }

        /*
        tree.CheckConsistency(ref accessor);

        _logger.LogInformation("Total insertion count {itemCount}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {time}",
            itemCount, segment.AllocatedChunkCount, (sw.Elapsed / itemCount).TotalSeconds.FriendlyTime());
    */
    }

    [Test]
    [Property("MemPageCount", 1024)]   // 300-page segment within epoch scope: all accessed pages are epoch-protected and non-evictable
    unsafe public void CheckSingleTreeBigAmount()
    {
        const int itemCount = 10000;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new FloatSingleBTree<PersistentStore>(segment);

            var rand = new Random(1234);
            var hashset = new HashSet<float>();

            var sw = new Stopwatch();

            for (int i = 0; i < itemCount; i++)
            {
                float val;
                while (true)
                {
                    val = rand.NextSingle();
                    if (hashset.Add(val))
                    {
                        break;
                    }
                }
                sw.Start();
                tree.Add(val, 1, ref accessor);
                sw.Stop();
            }

            tree.CheckConsistency(ref accessor);

            _logger.LogInformation("Total insertion count {itemCount}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {time}",
                itemCount, segment.AllocatedChunkCount, (sw.Elapsed / itemCount).TotalSeconds.FriendlyTime());

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void CheckMultipleTreeBigAmount()
    {
        const int itemCount = 100;

        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 300, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntMultipleBTree<PersistentStore>(segment);

            var chunkCapacity = segment.ChunkCapacity;
            var freeChunkCount = segment.FreeChunkCount;

            var elemIdDic = new Dictionary<int, List<int>>(itemCount);

            var sw = new Stopwatch();

            int gc = 0;
            for (int i = 0; i < itemCount; i++)
            {
                var idList = new List<int>(i);
                elemIdDic.Add(i, idList);

                for (int j = 0; j < i; j++, gc++)
                {
                    sw.Start();
                    var item = tree.Add(i, 10 + j, ref accessor);
                    sw.Stop();
                    idList.Add(item);
                }
            }

            _logger.LogError("Total insertion count {Gc}, chunk allocated {SegmentAllocatedChunkCount}, time per insert {FriendlyTime}", gc, segment.AllocatedChunkCount, (sw.Elapsed / gc).TotalSeconds.FriendlyTime());

            // Parse every element buffers
            for (int i = 1; i < itemCount; i++)
            {
                var c = 0;
                using var a = tree.TryGetMultiple(i, ref accessor);
                Assert.That(a.IsValid, Is.True);
                do
                {
                    c += a.ReadOnlyElements.Length;
                } while (a.NextChunk());

                Assert.That(c, Is.EqualTo(i));
            }

            // Now this is the nasty part, we delete half of the chunk of the buffer to create fragmentation that
            //  will be solved during the next parsing...
            for (int i = 0; i < itemCount; i++)
            {
                var idList = elemIdDic[i];

                for (int j = 0; j < i; j++, gc++)
                {
                    var elemId = idList[j];
                    if (((elemId + i) & 1) != 0)                // Use 'i'  to alternate deleting either odd or even chunks
                    {
                        tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                    }
                }
            }

            _logger.LogError("Remove half key/values, chunk allocated {cc}", segment.AllocatedChunkCount);

            // Parse every element buffers
            for (int i = 1; i < itemCount; i++)
            {
                var c = 0;
                using var a = tree.TryGetMultiple(i, ref accessor);
                if (a.IsValid == false) continue;

                //Assert.That(a.IsValid, Is.True);
                do
                {
                    c += a.ReadOnlyElements.Length;
                } while (a.NextChunk());

                //Assert.That(c, Is.EqualTo(i));
            }
            _logger.LogError("Remove half key/values, chunk allocated after collect {cc}", segment.AllocatedChunkCount);

            // Delete the rest
            for (int i = 0; i < itemCount; i++)
            {
                var idList = elemIdDic[i];

                for (int j = 0; j < i; j++, gc++)
                {
                    var elemId = idList[j];
                    if (((elemId + i) & 1) == 0)                // Use 'i'  to alternate deleting either odd or even chunks
                    {
                        tree.RemoveValue(i, elemId, 10 + j, ref accessor);
                    }
                }
            }

            //tree.First

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }

        _logger.LogError("Remove all key/values, chunk allocated {cc}", segment.AllocatedChunkCount);
    }

    #region EnumerateLeaves tests

    [Test]
    unsafe public void EnumerateLeaves_EmptyTree_YieldsNothing()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntSingleBTree<PersistentStore>(segment);

            int count = 0;
            foreach (var kv in tree.EnumerateLeaves())
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(0));
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void EnumerateLeaves_SingleLeaf_YieldsAllItems()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            // Insert few items (stays within a single leaf)
            tree.Add(30, 300, ref accessor);
            tree.Add(10, 100, ref accessor);
            tree.Add(20, 200, ref accessor);

            var keys = new List<int>();
            var values = new List<int>();
            foreach (var kv in tree.EnumerateLeaves())
            {
                keys.Add(kv.Key);
                values.Add(kv.Value);
            }

            Assert.That(keys, Is.EqualTo(new[] { 10, 20, 30 }));
            Assert.That(values, Is.EqualTo(new[] { 100, 200, 300 }));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void EnumerateLeaves_AfterDeletions_YieldsRemaining()
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            var allKeys = new[] { 1, 2, 3, 10, 20, 33, 5, 50, 70, 35, 9, 99, 101 };
            foreach (var k in allKeys)
            {
                tree.Add(k, k + 1, ref accessor);
            }

            // Delete some keys
            var toDelete = new[] { 2, 10, 50, 99 };
            foreach (var k in toDelete)
            {
                tree.Remove(k, out _, ref accessor);
            }

            var expected = allKeys.Except(toDelete).Order().ToArray();

            var enumerated = new List<int>();
            foreach (var kv in tree.EnumerateLeaves())
            {
                enumerated.Add(kv.Key);
            }

            Assert.That(enumerated.Count, Is.EqualTo(expected.Length));
            Assert.That(enumerated, Is.EqualTo(expected));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    #endregion

    #region RangeScan tests

    [Test]
    unsafe public void GetMinKey_ReturnsFirstKey()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            tree.Add(30, 30, ref accessor);
            tree.Add(10, 10, ref accessor);
            tree.Add(20, 20, ref accessor);

            Assert.That(tree.GetMinKey(), Is.EqualTo(10));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void RangeScan_EmptyTree_YieldsNothing()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var tree = new IntSingleBTree<PersistentStore>(segment);

            var count = 0;
            foreach (var kv in tree.EnumerateRange(1, 100))
            {
                count++;
            }

            Assert.That(count, Is.EqualTo(0));
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void RangeScan_FullRange_MatchesEnumerateLeaves()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            var keys = new[] { 5, 50, 10, 30, 20, 40, 1, 100 };
            foreach (var k in keys)
            {
                tree.Add(k, k, ref accessor);
            }

            var fromLeaves = new List<int>();
            foreach (var kv in tree.EnumerateLeaves())
            {
                fromLeaves.Add(kv.Key);
            }

            var fromRange = new List<int>();
            foreach (var kv in tree.EnumerateRange(int.MinValue, int.MaxValue))
            {
                fromRange.Add(kv.Key);
            }

            Assert.That(fromRange, Is.EqualTo(fromLeaves));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void RangeScan_TightBounds_OnlyQualifyingEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            foreach (var k in new[] { 1, 5, 10, 15, 20, 25, 30, 35, 40 })
            {
                tree.Add(k, k, ref accessor);
            }

            var result = new List<int>();
            foreach (var kv in tree.EnumerateRange(10, 30))
            {
                result.Add(kv.Key);
            }

            Assert.That(result, Is.EqualTo(new[] { 10, 15, 20, 25, 30 }));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    [Property("MemPageCount", 1024)]
    unsafe public void RangeScan_MultiLeaf_CorrectOrdering()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            for (int i = 1; i <= 500; i++)
            {
                tree.Add(i, i, ref accessor);
            }

            var result = new List<int>();
            foreach (var kv in tree.EnumerateRange(100, 400))
            {
                result.Add(kv.Key);
            }

            Assert.That(result.Count, Is.EqualTo(301));
            Assert.That(result[0], Is.EqualTo(100));
            Assert.That(result[^1], Is.EqualTo(400));

            // Verify ascending order
            for (int i = 1; i < result.Count; i++)
            {
                Assert.That(result[i], Is.GreaterThan(result[i - 1]));
            }

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void RangeScanDescending_ReturnsReverseOrder()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            foreach (var k in new[] { 1, 5, 10, 15, 20, 25, 30 })
            {
                tree.Add(k, k, ref accessor);
            }

            var result = new List<int>();
            foreach (var kv in tree.EnumerateRangeDescending(10, 25))
            {
                result.Add(kv.Key);
            }

            Assert.That(result, Is.EqualTo(new[] { 25, 20, 15, 10 }));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    [Test]
    unsafe public void RangeScan_AfterDeletions_CorrectEntries()
    {
        using var mpmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var epochManager = _serviceProvider.GetRequiredService<EpochManager>();
        var segment = mpmmf.AllocateChunkBasedSegment(PageBlockType.None, 10, sizeof(Index32Chunk));
        var depth = epochManager.EnterScope();
        try
        {
            var accessor = segment.CreateChunkAccessor();
            var tree = new IntSingleBTree<PersistentStore>(segment);

            var allKeys = new[] { 1, 2, 3, 10, 20, 33, 5, 50, 70, 35, 9, 99, 101 };
            foreach (var k in allKeys)
            {
                tree.Add(k, k + 1, ref accessor);
            }

            var toDelete = new[] { 2, 10, 50, 99 };
            foreach (var k in toDelete)
            {
                tree.Remove(k, out _, ref accessor);
            }

            var expected = allKeys.Except(toDelete).Where(k => k >= 5 && k <= 70).Order().ToArray();

            var result = new List<int>();
            foreach (var kv in tree.EnumerateRange(5, 70))
            {
                result.Add(kv.Key);
            }

            Assert.That(result.Count, Is.EqualTo(expected.Length));
            Assert.That(result, Is.EqualTo(expected));

            accessor.Dispose();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    #endregion
}
