using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Tests.Backup;

/// <summary>One WAL segment of a test image: its header facts and the bytes a capture would copy (up to the last written 4 KiB block).</summary>
internal sealed record TestWalSegment(long SegmentId, long FirstLsn, uint SegmentSize, byte[] Bytes);

/// <summary>
/// A real database image, built once by the engine and held in memory: its pages, its WAL segments and what its meta pair says. Packs written
/// from it pass the writer's own cross-checks (meta pair, CheckpointLSN, DatabaseId) because nothing in it is synthetic.
/// </summary>
internal sealed class BackupTestImage
{
    public string DatabaseName { get; init; }

    public Guid DatabaseId { get; init; }

    public long CheckpointLsn { get; init; }

    public bool Clean { get; init; }

    public int FormatRevision { get; init; }

    public byte[][] Pages { get; init; }

    public IReadOnlyList<TestWalSegment> WalSegments { get; init; }

    /// <summary>The bundle on disk the image was read from (kept for tests that open or compare it).</summary>
    public string BundlePath { get; init; }

    public int PageCount => Pages.Length;
}

/// <summary>The shared images, each built on first use and reused by every fixture of the run.</summary>
internal static class BackupTestImages
{
    private static readonly Lazy<BackupTestImage> CleanImage = new(() => Build("BkImgClean", unclean: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<BackupTestImage> UncleanImage = new(() => Build("BkImgUnclean", unclean: true), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A cleanly closed database: clean flag set, WAL present but not needed.</summary>
    public static BackupTestImage Clean => CleanImage.Value;

    /// <summary>A database closed without the clean marker: its open must take the crash path, so its point needs a tail (DC-3).</summary>
    public static BackupTestImage Unclean => UncleanImage.Value;

    /// <summary>Builds a fresh image under <paramref name="name"/>; the bundle stays on disk under this run's storage root.</summary>
    public static BackupTestImage Build(string name, bool unclean, int entities = 48, string root = null)
    {
        root ??= Path.Combine(TestStorage.ForFixture("BackupTestImages"), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(root, "db");
        var bundle = Path.Combine(dbDir, name + ".typhon");
        var walDir = Path.Combine(bundle, "wal");
        Directory.CreateDirectory(walDir);
        CreateDatabase(name, dbDir, walDir, unclean, entities);
        return Read(bundle);
    }

    /// <summary>Reads an existing, closed bundle as a test image.</summary>
    public static BackupTestImage Read(string bundle)
    {
        byte[][] pages;
        using (var source = new OfflineBundlePageSource(bundle))
        {
            pages = new byte[source.PageCount][];
            for (var i = 0; i < pages.Length; i++)
            {
                pages[i] = new byte[IntegrityConstants.PageSize];
                if (!source.TryReadPage(i, pages[i]))
                {
                    throw new InvalidOperationException($"page {i} unreadable");
                }
            }
        }

        var meta = BackupImageMeta.Read(pages[0], pages[1]);
        if (!meta.Usable)
        {
            throw new InvalidOperationException($"test image meta unusable: {meta.Problem}");
        }

        var segments = new List<TestWalSegment>();
        var walDir = Path.Combine(bundle, "wal");
        if (Directory.Exists(walDir))
        {
            var files = Directory.GetFiles(walDir, "*.wal");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                if (bytes.Length < WalSegmentHeader.SizeInBytes)
                {
                    continue;
                }

                var header = MemoryMarshal.Read<WalSegmentHeader>(bytes);
                if (!header.Validate())
                {
                    continue;
                }

                // What a capture copies: the written prefix, 4 KiB aligned (never less than the header).
                var written = bytes.Length;
                while (written > WalSegmentHeader.SizeInBytes && bytes.AsSpan(written - 4096, 4096).IndexOfAnyExcept((byte)0) < 0)
                {
                    written -= 4096;
                }

                segments.Add(new TestWalSegment(header.SegmentId, header.FirstLSN, header.SegmentSize, bytes.AsSpan(0, written).ToArray()));
            }
        }

        segments.Sort(static (a, b) => a.SegmentId.CompareTo(b.SegmentId));
        return new BackupTestImage
        {
            DatabaseName = meta.DatabaseName,
            DatabaseId = meta.DatabaseId,
            CheckpointLsn = meta.CheckpointLsn,
            Clean = meta.CleanShutdown,
            FormatRevision = meta.FormatRevision,
            Pages = pages,
            WalSegments = segments,
            BundlePath = bundle,
        };
    }

    /// <summary>
    /// A copy of <paramref name="image"/> whose meta pair records another on-disk format revision, restamped so it still verifies (the
    /// <c>DamageKit.ForgeFormatRevision</c> technique, in memory).
    /// </summary>
    public static BackupTestImage WithFormatRevision(BackupTestImage image, int revision)
    {
        var pages = (byte[][])image.Pages.Clone();
        for (var slot = 0; slot <= 1; slot++)
        {
            var page = (byte[])image.Pages[slot].Clone();
            // RootFileHeader at the page-base header's end; its revision field is at +32 (BootstrapReader's layout).
            BitConverter.GetBytes(revision).CopyTo(page, PagedMMF.PageBaseHeaderSize + 32);
            PagedMMF.StampPageForWrite(page, PageSectorFooter.ReadFilePageIndex(page), allowSectorFooter: false);
            pages[slot] = page;
        }

        return new BackupTestImage
        {
            DatabaseName = image.DatabaseName,
            DatabaseId = image.DatabaseId,
            CheckpointLsn = image.CheckpointLsn,
            Clean = image.Clean,
            FormatRevision = revision,
            Pages = pages,
            WalSegments = image.WalSegments,
            BundlePath = image.BundlePath,
        };
    }

    /// <summary>
    /// A copy of <paramref name="image"/> whose WAL segments are cut to their first <paramref name="bytes"/>: the real header and first records,
    /// the declared size kept. For sweeps that write and decode the tail thousands of times and test its handling, not its content.
    /// </summary>
    public static BackupTestImage WithShortTail(BackupTestImage image, int bytes = 8192) => new()
    {
        DatabaseName = image.DatabaseName,
        DatabaseId = image.DatabaseId,
        CheckpointLsn = image.CheckpointLsn,
        Clean = image.Clean,
        FormatRevision = image.FormatRevision,
        Pages = image.Pages,
        WalSegments = image.WalSegments.Select(w => w with { Bytes = w.Bytes[..Math.Min(w.Bytes.Length, bytes)] }).ToList(),
        BundlePath = image.BundlePath,
    };

    /// <summary>The DI graph the integrity fixtures use, with the WAL inside the bundle and a small cache.</summary>
    public static ServiceProvider BuildProvider(string name, string dbDir, string walDir, bool forceCrashRecovery = false)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = name;
                opts.DatabaseDirectory = dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.ForceCrashRecoveryAtOpen = forceCrashRecovery;
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 1024 * 1024,
                    PreAllocateSegments = 1
                };
            });
        return services.BuildServiceProvider();
    }

    private static void CreateDatabase(string name, string dbDir, string walDir, bool unclean, int entities)
    {
        using var provider = BuildProvider(name, dbDir, walDir);
        using var scope = provider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.InitializeArchetypes();
        using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
        {
            for (var i = 0; i < entities; i++)
            {
                using var tx = uow.CreateTransaction();
                var comp = new CompA(i + 1, i, i);
                tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                tx.Commit();
            }

            uow.Flush();
        }

        dbe.ForceCheckpoint();
        dbe.SimulateUncleanShutdownForTest = unclean;
    }
}
