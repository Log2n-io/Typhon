using System;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

unsafe class BootstrapDictionaryTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Round-trip tests — write then read, verify all types
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RoundTrip_Bool()
    {
        var dict = new BootstrapDictionary();
        dict.SetBool("Enabled", true);
        dict.SetBool("Disabled", false);

        var restored = RoundTrip(dict);
        Assert.That(restored.GetBool("Enabled"), Is.True);
        Assert.That(restored.GetBool("Disabled"), Is.False);
    }

    [Test]
    public void RoundTrip_Int1()
    {
        var dict = new BootstrapDictionary();
        dict.SetInt("SPI", 42);

        var restored = RoundTrip(dict);
        Assert.That(restored.GetInt("SPI"), Is.EqualTo(42));
    }

    [Test]
    public void RoundTrip_Int4_ComponentTableSPIs()
    {
        var dict = new BootstrapDictionary();
        dict.Set("sys.ComponentR1", BootstrapDictionary.Value.FromInt4(10, 20, 30, 40));

        var restored = RoundTrip(dict);
        var value = restored.Get("sys.ComponentR1");
        Assert.That(value.Type, Is.EqualTo(BootstrapDictionary.ValueType.Int4));
        Assert.That(value.GetInt(0), Is.EqualTo(10));
        Assert.That(value.GetInt(1), Is.EqualTo(20));
        Assert.That(value.GetInt(2), Is.EqualTo(30));
        Assert.That(value.GetInt(3), Is.EqualTo(40));
    }

    [Test]
    public void RoundTrip_Int6()
    {
        var dict = new BootstrapDictionary();
        dict.Set("BigTuple", BootstrapDictionary.Value.FromInt6(1, 2, 3, 4, 5, 6));

        var restored = RoundTrip(dict);
        var value = restored.Get("BigTuple");
        Assert.That(value.IntCount, Is.EqualTo(6));
        for (int i = 0; i < 6; i++)
        {
            Assert.That(value.GetInt(i), Is.EqualTo(i + 1));
        }
    }

    [Test]
    public void RoundTrip_Long()
    {
        var dict = new BootstrapDictionary();
        dict.SetLong("NextFreeTSN", 123456789L);

        var restored = RoundTrip(dict);
        Assert.That(restored.GetLong("NextFreeTSN"), Is.EqualTo(123456789L));
    }

    [Test]
    public void RoundTrip_DateTime()
    {
        var dict = new BootstrapDictionary();
        var now = DateTime.UtcNow;
        dict.SetDateTime("LastCheckpoint", now);

        var restored = RoundTrip(dict);
        Assert.That(restored.Get("LastCheckpoint").AsDateTime.Ticks, Is.EqualTo(now.Ticks));
    }

    [Test]
    public void RoundTrip_String()
    {
        var dict = new BootstrapDictionary();
        dict.SetString("DatabaseName", "MyTestDB");

        var restored = RoundTrip(dict);
        Assert.That(restored.Get("DatabaseName").AsString, Is.EqualTo("MyTestDB"));
    }

    [Test]
    public void RoundTrip_MixedTypes()
    {
        var dict = new BootstrapDictionary();
        dict.SetBool("WALEnabled", true);
        dict.SetInt("OccupancyMapSPI", 1);
        dict.Set("sys.ComponentR1", BootstrapDictionary.Value.FromInt4(10, 20, 30, 40));
        dict.SetLong("CheckpointLSN", 0L);
        dict.SetLong("NextFreeTSN", 42L);
        dict.SetString("DatabaseName", "TestDB");
        dict.SetInt("UserSchemaVersion", 5);

        var restored = RoundTrip(dict);
        Assert.That(restored.Count, Is.EqualTo(7));
        Assert.That(restored.GetBool("WALEnabled"), Is.True);
        Assert.That(restored.GetInt("OccupancyMapSPI"), Is.EqualTo(1));
        Assert.That(restored.Get("sys.ComponentR1").GetInt(2), Is.EqualTo(30));
        Assert.That(restored.GetLong("CheckpointLSN"), Is.EqualTo(0L));
        Assert.That(restored.GetLong("NextFreeTSN"), Is.EqualTo(42L));
        Assert.That(restored.Get("DatabaseName").AsString, Is.EqualTo("TestDB"));
        Assert.That(restored.GetInt("UserSchemaVersion"), Is.EqualTo(5));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RoundTrip_Empty()
    {
        var dict = new BootstrapDictionary();
        var restored = RoundTrip(dict);
        Assert.That(restored.Count, Is.EqualTo(0));
    }

    [Test]
    public void RoundTrip_LargeNumberOfEntries()
    {
        var dict = new BootstrapDictionary();
        for (int i = 0; i < 50; i++)
        {
            dict.SetInt($"Entry{i}", i * 100);
        }

        var restored = RoundTrip(dict);
        Assert.That(restored.Count, Is.EqualTo(50));
        Assert.That(restored.GetInt("Entry0"), Is.EqualTo(0));
        Assert.That(restored.GetInt("Entry49"), Is.EqualTo(4900));
    }

    [Test]
    public void RoundTrip_UnicodeKey()
    {
        var dict = new BootstrapDictionary();
        dict.SetInt("café", 42);

        var restored = RoundTrip(dict);
        Assert.That(restored.GetInt("café"), Is.EqualTo(42));
    }

    [Test]
    public void RoundTrip_StringValue_WithUnicode()
    {
        var dict = new BootstrapDictionary();
        dict.SetString("Name", "données_françaises");

        var restored = RoundTrip(dict);
        Assert.That(restored.Get("Name").AsString, Is.EqualTo("données_françaises"));
    }

    [Test]
    public void GetInt_Missing_ReturnsDefault()
    {
        var dict = new BootstrapDictionary();
        Assert.That(dict.GetInt("Missing", 99), Is.EqualTo(99));
    }

    [Test]
    public void GetBool_Missing_ReturnsDefault()
    {
        var dict = new BootstrapDictionary();
        Assert.That(dict.GetBool("Missing", true), Is.True);
    }

    [Test]
    public void Set_OverwritesExisting()
    {
        var dict = new BootstrapDictionary();
        dict.SetInt("Key", 1);
        dict.SetInt("Key", 2);
        Assert.That(dict.GetInt("Key"), Is.EqualTo(2));
        Assert.That(dict.Count, Is.EqualTo(1));
    }

    [Test]
    public void CalculateSize_MatchesWrittenBytes()
    {
        var dict = new BootstrapDictionary();
        dict.SetInt("OccupancyMapSPI", 1);
        dict.Set("sys.ComponentR1", BootstrapDictionary.Value.FromInt4(10, 20, 30, 40));
        dict.SetLong("NextFreeTSN", 42L);

        int calculated = dict.CalculateSize();

        byte* buf = stackalloc byte[8192];
        int written = dict.WriteTo(buf, 8192);

        Assert.That(written, Is.EqualTo(calculated));
    }

    [Test]
    public void WriteTo_TooSmallBuffer_Throws()
    {
        var dict = new BootstrapDictionary();
        dict.SetString("LongKey", "This is a value that takes space");

        byte* buf = stackalloc byte[10]; // way too small
        Assert.Throws<InvalidOperationException>(() => dict.WriteTo(buf, 10));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Realistic scenario — current RootFileHeader replacement
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void RoundTrip_RealisticBootstrap()
    {
        var dict = new BootstrapDictionary();

        // Core infrastructure
        dict.SetInt("OccupancyMapSPI", 1);
        dict.Set("OccupancyReserved", BootstrapDictionary.Value.FromInt2(5, 6)); // NextReservedPage, NextReservedMapPage
        dict.SetInt("UowRegistrySPI", 3);
        dict.SetInt("SystemSchemaRevision", 1);
        dict.SetInt("UserSchemaVersion", 5);

        // MVCC & durability
        dict.SetLong("NextFreeTSN", 42L);
        dict.SetLong("CheckpointLSN", 0L);

        // System tables — all SPIs packed per table
        dict.Set("sys.ComponentR1", BootstrapDictionary.Value.FromInt4(10, 20, 30, 40));      // comp, rev, default, str64
        dict.Set("sys.SchemaHistory", BootstrapDictionary.Value.FromInt4(50, 60, 70, 80));
        dict.Set("sys.ArchetypeR1", BootstrapDictionary.Value.FromInt4(90, 100, 110, 120));

        // Collection segments
        dict.SetInt("collection.FieldR1", 105);

        int size = dict.CalculateSize();
        Assert.That(size, Is.LessThan(8192), $"Bootstrap stream should fit in one page, got {size} bytes");

        var restored = RoundTrip(dict);
        Assert.That(restored.Count, Is.EqualTo(11));

        // Verify system table tuple unpacking
        var compR1 = restored.Get("sys.ComponentR1");
        Assert.That(compR1.GetInt(0), Is.EqualTo(10));  // ComponentSPI
        Assert.That(compR1.GetInt(1), Is.EqualTo(20));  // CompRevSPI
        Assert.That(compR1.GetInt(2), Is.EqualTo(30));  // DefaultIndexSPI
        Assert.That(compR1.GetInt(3), Is.EqualTo(40));  // String64IndexSPI
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Concurrency — the bootstrap has writers on several threads (#770)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Inserting new keys on one thread, while another overwrites an existing key and a third serializes, must lose nothing and throw nothing.
    /// </summary>
    /// <remarks>
    /// The engine's shape: the checkpoint thread adds the collection-pool directory (new keys, so the table resizes), the page allocator overwrites
    /// <c>OccupancyReserved</c> from whichever thread grows a segment, and a meta flip serializes the whole dictionary. An overwrite that lands during a resize
    /// can be lost — a stale reserved page is allocator corruption — and a serializer that enumerates through an insert throws.
    /// </remarks>
    [Test]
    [CancelAfter(10_000)]
    public void ConcurrentInsertOverwriteAndSerialize_LoseNothing()
    {
        const int rounds = 20;
        const int inserts = 256;

        for (var round = 0; round < rounds; round++)
        {
            var dict = new BootstrapDictionary();
            dict.SetInt("OccupancyReserved", 0);
            Exception failure = null;
            using var start = new System.Threading.Barrier(3);
            var lastOverwrite = 0;

            var inserter = new System.Threading.Thread(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < inserts; i++)
                {
                    dict.SetInt($"collection.{i}", i);
                }
            });
            var overwriter = new System.Threading.Thread(() =>
            {
                start.SignalAndWait();
                for (var v = 1; v <= inserts; v++)
                {
                    dict.SetInt("OccupancyReserved", v);
                    lastOverwrite = v;
                }
            });
            var serializer = new System.Threading.Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    byte* page = stackalloc byte[8192];
                    for (var i = 0; i < inserts / 8; i++)
                    {
                        dict.WriteTo(page, 8192);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            inserter.Start();
            overwriter.Start();
            serializer.Start();
            inserter.Join();
            overwriter.Join();
            serializer.Join();

            Assert.That(failure, Is.Null, $"round {round}: serializing the dictionary while another thread wrote it threw");
            Assert.That(dict.GetInt("OccupancyReserved", -1), Is.EqualTo(lastOverwrite), $"round {round}: the last overwrite was lost to a concurrent insert");
            for (var i = 0; i < inserts; i++)
            {
                Assert.That(dict.GetInt($"collection.{i}", -1), Is.EqualTo(i), $"round {round}: inserted key collection.{i} was lost");
            }

            Assert.That(dict.Count, Is.EqualTo(inserts + 1), $"round {round}: entry count");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helper
    // ═══════════════════════════════════════════════════════════════════════

    private static BootstrapDictionary RoundTrip(BootstrapDictionary source)
    {
        byte* buf = stackalloc byte[8192];
        int written = source.WriteTo(buf, 8192);

        var restored = new BootstrapDictionary();
        restored.ReadFrom(buf, written);
        return restored;
    }
}
