using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Typhon.Benchmark;

/// <summary>
/// The store side of backup, CPU only (#1413 plan §7): what one page costs the pack writer (the copy-before-write drain rate capture sizes its queue
/// against, target p99 ≤ 20 µs), what decoding one costs, and the throughput of writing, materialising and verifying a whole point — all over an
/// in-memory file system, so the numbers are the codec and CRC work and never the disk. Prepared, not yet run: timings belong on a quiet machine.
/// </summary>
/// <remarks>
/// Page classes: <see cref="PageClass.Zero"/> (no frame at all), <see cref="PageClass.Sparse"/> (a quarter written, the shape of a B+Tree or cluster
/// page), <see cref="PageClass.Random"/> (incompressible, stored raw). Gates (plan §7, CPU-bound): pack write ≥ 500 MB/s, materialise ≥ 1 GB/s,
/// verify full ≥ 1.2 GB/s of raw image bytes.
/// </remarks>
[BenchmarkCategory("Storage", "Backup")]
[MemoryDiagnoser]
public class PackCodecBenchmarks
{
    public enum PageClass
    {
        Zero,
        Sparse,
        Random,
    }

    private const int Pages = 1024;
    private byte[][] _images;
    private byte[][] _frames;
    private PackPageEntry[] _entries;
    private byte[] _page;

    [Params(PageClass.Zero, PageClass.Sparse, PageClass.Random)]
    public PageClass Class { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _images = BackupBenchmarkCorpus.Pages(Class, Pages);
        _page = new byte[PagedMMF.PageSize];
        _frames = new byte[Pages][];
        _entries = new PackPageEntry[Pages];
        var scratch = new byte[4 + PackFormat.MaxCompressedPage];
        for (var i = 0; i < Pages; i++)
        {
            var image = _images[i];
            var stored = K4os.Compression.LZ4.LZ4Codec.Encode(image, scratch.AsSpan(4));
            if (stored <= 0 || stored >= PagedMMF.PageSize)
            {
                image.CopyTo(scratch.AsSpan(4));
                stored = PagedMMF.PageSize;
            }

            BitConverter.TryWriteBytes(scratch, stored);
            _frames[i] = scratch.AsSpan(0, 4 + stored).ToArray();
            _entries[i] = new PackPageEntry((uint)i, (uint)stored, 1024, Crc32CUtil.Compute(image));
        }
    }

    /// <summary>One page into a pack writer (CRC, zero test, LZ4, frame copy); the write buffers go to a file that keeps nothing.</summary>
    [Benchmark(OperationsPerInvoke = Pages)]
    public void AddPage()
    {
        var fs = new BackupBenchmarkFileSystem(keepBytes: false);
        var identity = new PackIdentity(Guid.Empty, Guid.Empty, Guid.Empty, 1, 0, 1, BackupKind.Full, Guid.Empty, PagedMMF.DatabaseFormatRevision, "bench",
            false);
        var writer = new PackWriter(fs, fs.OpenCreateNew("/p/x.tpack.tmp"), identity, null);
        writer.SetImage(Pages, 0, 0);
        for (var i = 0; i < Pages; i++)
        {
            writer.AddPage(i, _images[i], false);
        }

        writer.Abort();
    }

    /// <summary>One frame decoded and checked against its PageCrc.</summary>
    [Benchmark(OperationsPerInvoke = Pages)]
    public bool DecodePage()
    {
        var ok = true;
        for (var i = 0; i < Pages; i++)
        {
            ok &= PackReader.DecodePage(_entries[i], _frames[i], _page, out _);
        }

        return ok;
    }

    /// <summary>The incremental CRC over one page, the per-buffer cost of FileCRC.</summary>
    [Benchmark(OperationsPerInvoke = Pages)]
    public uint CrcAppend()
    {
        var state = Crc32CUtil.Start;
        for (var i = 0; i < Pages; i++)
        {
            state = Crc32CUtil.Append(state, _images[i]);
        }

        return Crc32CUtil.Finish(state);
    }
}

/// <summary>
/// A whole point through the store, in memory: write the pack (store publish included), materialise it, verify it quick and full. Bytes per second
/// are of the raw image (<see cref="ImageMiB"/>), a third of its pages each of zero, sparse and random.
/// </summary>
[BenchmarkCategory("Storage", "Backup")]
public class BackupStoreThroughputBenchmarks
{
    private BackupBenchmarkFileSystem _fs;
    private byte[][] _image;
    private byte[] _page0;
    private byte[] _page1;

    [Params(64)]
    public int ImageMiB { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var pages = ImageMiB * 1024 * 1024 / PagedMMF.PageSize;
        _image = BackupBenchmarkCorpus.Mixed(pages);
        (_page0, _page1) = BackupBenchmarkCorpus.MetaPair(out _);
        _image[0] = _page0;
        _image[1] = _page1;
        _fs = new BackupBenchmarkFileSystem(keepBytes: true);
        WriteFullPoint();
    }

    private BackupPointInfo WriteFullPoint()
    {
        var databaseId = BackupBenchmarkCorpus.DatabaseId;
        using var store = BackupStore.OpenForWrite("/s", databaseId, true, _fs);
        var sink = store.BeginPoint(new BackupPointPlan(databaseId, Guid.Empty, BackupKind.Full, Guid.Empty, 0, PagedMMF.DatabaseFormatRevision, "bench",
            false));
        sink.SetImage(_image.Length, BackupBenchmarkCorpus.CheckpointLsn, 0);
        for (var p = 0; p < _image.Length; p++)
        {
            sink.AddPage(p, _image[p], false);
        }

        return sink.Complete(BackupBenchmarkCorpus.CheckpointLsn, 0);
    }

    [Benchmark]
    public long WritePack() => WriteFullPoint().PackBytes;

    [Benchmark]
    public int Materialize()
    {
        var view = StoreView.Open("/s", _fs, trustCatalog: false);
        var chain = ChainResolver.Resolve(view, 1);
        var packs = PackChainPageSource.OpenPacks(_fs, view, chain);
        try
        {
            var target = new BackupBenchmarkFileSystem(keepBytes: false);
            using var file = target.OpenCreateNew("/t/data");
            return ChainMaterializer.Materialize(packs, file).Pages;
        }
        finally
        {
            foreach (var p in packs)
            {
                p.Dispose();
            }
        }
    }

    [Benchmark]
    public int VerifyQuick() => (int)BackupVerifier.VerifyPoint("/s", PointSelector.ById(1), VerifyLevel.Quick, null, _fs).Verdict;

    [Benchmark]
    public int VerifyFull() => (int)BackupVerifier.VerifyPoint("/s", PointSelector.ById(1), VerifyLevel.Full, null, _fs).Verdict;
}

/// <summary>
/// The open-time A/B of the restore marker probe (plan §7: one <c>File.Exists</c> per open), same binary: <see cref="NoProbe"/> routes the probe through
/// a file system that answers without a system call, <see cref="WithProbe"/> through the disk. Gate: |Δ median| ≤ max(1 %, 100 µs), over interleaved
/// runs.
/// </summary>
[BenchmarkCategory("Storage", "Backup")]
public class RestoreMarkerOpenBenchmarks
{
    private ServiceProvider _provider;
    private readonly BackupBenchmarkFileSystem _noFiles = new(keepBytes: false);

    [GlobalSetup]
    public void Setup()
    {
        _provider = BenchmarkEngine.BuildEcsEngine(4096, "RestoreMarkerOpen");
        OpenClose(null);
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    private void OpenClose(IStoreFileSystem markerFs)
    {
        DatabaseEngine.RestoreMarkerFileSystemForTest = markerFs;
        try
        {
            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        }
        finally
        {
            DatabaseEngine.RestoreMarkerFileSystemForTest = null;
        }
    }

    [Benchmark(Baseline = true)]
    public void NoProbe() => OpenClose(_noFiles);

    [Benchmark]
    public void WithProbe() => OpenClose(null);
}

/// <summary>Deterministic page images for the backup benchmarks.</summary>
internal static class BackupBenchmarkCorpus
{
    /// <summary>The database the meta pair belongs to, set by <see cref="MetaPair"/>.</summary>
    public static Guid DatabaseId { get; private set; }

    /// <summary>The meta pair's CheckpointLSN, set by <see cref="MetaPair"/>.</summary>
    public static long CheckpointLsn { get; private set; }

    public static byte[][] Pages(PackCodecBenchmarks.PageClass kind, int count)
    {
        var random = new Random(1413);
        var pages = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var page = new byte[PagedMMF.PageSize];
            switch (kind)
            {
                case PackCodecBenchmarks.PageClass.Sparse:
                    random.NextBytes(page.AsSpan(0, 192));
                    for (var o = 192; o < PagedMMF.PageSize / 4; o += 8)
                    {
                        BitConverter.TryWriteBytes(page.AsSpan(o), (long)random.Next(0, 1000));
                    }

                    break;
                case PackCodecBenchmarks.PageClass.Random:
                    random.NextBytes(page);
                    break;
            }

            pages[i] = page;
        }

        return pages;
    }

    public static byte[][] Mixed(int count)
    {
        var zero = Pages(PackCodecBenchmarks.PageClass.Zero, count / 3);
        var sparse = Pages(PackCodecBenchmarks.PageClass.Sparse, count / 3);
        var random = Pages(PackCodecBenchmarks.PageClass.Random, count - (2 * (count / 3)));
        var all = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            all.Add((i % 3) switch
            {
                0 => zero[i / 3 % zero.Length],
                1 => sparse[i / 3 % sparse.Length],
                _ => random[i / 3 % random.Length],
            });
        }

        return [.. all];
    }

    /// <summary>A real meta pair: a small database created once, its pages 0 and 1 read back (the writer checks them against the header).</summary>
    public static (byte[], byte[]) MetaPair(out string bundle)
    {
        var provider = BenchmarkEngine.BuildEcsEngine(2048, "BackupBenchMeta");
        using (var scope = provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            DatabaseId = dbe.DatabaseId;
            bundle = dbe.MMF.BundleDirectory;
        }

        provider.Dispose();
        using var source = new OfflineBundlePageSource(bundle);
        var p0 = new byte[PagedMMF.PageSize];
        var p1 = new byte[PagedMMF.PageSize];
        source.TryReadPage(0, p0);
        source.TryReadPage(1, p1);
        var meta = BackupImageMeta.Read(p0, p1);
        CheckpointLsn = meta.CheckpointLsn;
        return (p0, p1);
    }
}

/// <summary>A minimal in-memory <see cref="IStoreFileSystem"/>: whole files in growable arrays, or none at all when only the write cost matters.</summary>
internal sealed class BackupBenchmarkFileSystem(bool keepBytes) : IStoreFileSystem
{
    private readonly Dictionary<string, MemoryFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    private static string N(string path) => path.Replace('\\', '/').TrimEnd('/');

    public IStoreFile OpenCreateNew(string path, long preallocationSize = 0)
    {
        var file = new MemoryFile(N(path), keepBytes);
        _files[file.Path] = file;
        return file;
    }

    public IStoreFile OpenRead(string path) => _files.TryGetValue(N(path), out var f) ? f : throw new FileNotFoundException(path);

    public IDisposable TryLockExclusive(string path) => new MemoryFile(N(path), false);

    public bool ProbeExclusive(string path) => true;

    public void Rename(string source, string destination, bool replace)
    {
        if (_files.Remove(N(source), out var f))
        {
            f.Path = N(destination);
            _files[f.Path] = f;
        }
    }

    public void FlushDirectory(string directory)
    {
    }

    public bool FileExists(string path) => _files.ContainsKey(N(path));

    public bool DirectoryExists(string path) => _directories.Contains(N(path));

    public void CreateDirectory(string path) => _directories.Add(N(path));

    public void DeleteFile(string path) => _files.Remove(N(path));

    public void DeleteDirectory(string path) => _directories.Remove(N(path));

    public IReadOnlyList<StoreFileEntry> EnumerateFiles(string directory)
    {
        var d = N(directory) + "/";
        var list = new List<StoreFileEntry>();
        foreach (var (path, f) in _files)
        {
            if (path.StartsWith(d, StringComparison.Ordinal) && path.IndexOf('/', d.Length) < 0)
            {
                list.Add(new StoreFileEntry(path[d.Length..], f.Length));
            }
        }

        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public bool IsBundleHeldByLiveProcess(string bundleDirectory) => false;

    private sealed class MemoryFile(string path, bool keep) : IStoreFile
    {
        private byte[] _bytes = keep ? new byte[64 * 1024] : [];
        private long _length;

        public string Path { get; set; } = path;

        public long Length => _length;

        public int Read(Span<byte> destination, long offset)
        {
            if (offset >= _length)
            {
                return 0;
            }

            var n = (int)Math.Min(destination.Length, _length - offset);
            _bytes.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }

        public void Write(ReadOnlySpan<byte> source, long offset)
        {
            var end = offset + source.Length;
            if (keep)
            {
                if (end > _bytes.Length)
                {
                    Array.Resize(ref _bytes, (int)Math.Max(end, _bytes.Length * 2L));
                }

                source.CopyTo(_bytes.AsSpan((int)offset));
            }

            _length = Math.Max(_length, end);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> source, long offset)
        {
            Write(source.Span, offset);
            return Task.CompletedTask;
        }

        public void SetLength(long length) => _length = length;

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
