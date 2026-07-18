using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Isolated test class for bulk R-Tree inserts (multiple splits).
/// Separate from SpatialRTreeTests to avoid test interaction issues.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SpatialRTreeBulkTests
{
    private IServiceProvider _serviceProvider;
    private string _testDir;

    [SetUp]
    public void Setup()
    {
        var testName = TestContext.CurrentContext.Test.Name;
        foreach (var c in new[] { '(', ')', ',', ' ' })
        {
            testName = testName.Replace(c, '_');
        }
        if (testName.Length > 25)
        {
            testName = testName[^25..];
        }

        _testDir = Path.Combine(Path.GetTempPath(), "Typhon.Tests", "SpatialBulk");
        Directory.CreateDirectory(_testDir);

        var sc = new ServiceCollection();
        sc.AddLogging(b =>
            {
                b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "mm:ss.fff "; });
                b.SetMinimumLevel(LogLevel.Warning);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddScopedManagedPagedMemoryMappedFile(o =>
            {
                o.DatabaseName = $"BLK{testName}";
                o.DatabaseDirectory = _testDir;
                o.DatabaseCacheSize = (ulong)(PagedMMF.MinimumMemPageCount * PagedMMF.PageSize);
                o.TestMode = true;
            });
        _serviceProvider = sc.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        if (_testDir != null)
        {
            try
            {
                foreach (var file in Directory.GetFiles(_testDir))
                {
                    try { File.Delete(file); }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static double[] MakeCoords(SpatialVariant variant, double minX, double minY, double maxX, double maxY)
    {
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var coords = new double[desc.CoordCount];
        int h = desc.CoordCount / 2;
        coords[0] = minX;
        coords[1] = minY;
        if (h == 3) { coords[2] = 0; }
        coords[h] = maxX;
        coords[h + 1] = maxY;
        if (h == 3) { coords[h + 2] = 1; }
        return coords;
    }

    [Test]
    [TestCase(50)]
    [TestCase(100)]
    [TestCase(200)]
    [CancelAfter(10000)]
    public void BulkInsert_R2Df32(int count)
    {
        using var pmmf = _serviceProvider.GetRequiredService<ManagedPagedMMF>();
        using var em = _serviceProvider.GetRequiredService<EpochManager>();
        var guard = EpochGuard.Enter(em);

        var variant = SpatialVariant.R2Df32;
        var desc = SpatialNodeDescriptor.ForVariant(variant);
        var segment = pmmf.AllocateChunkBasedSegment(PageBlockType.None, 20, desc.Stride);
        var tree = new SpatialRTree<PersistentStore>(segment, variant);
        var accessor = segment.CreateChunkAccessor();

        for (int i = 0; i < count; i++)
        {
            var coords = MakeCoords(variant, i * 10, i * 10, i * 10 + 5, i * 10 + 5);
            tree.Insert(i + 1, coords, ref accessor);
        }

        Assert.That(tree.EntityCount, Is.EqualTo(count));

        TreeValidator.Validate(tree);

        accessor.Dispose();
        guard.Dispose();
    }
}
