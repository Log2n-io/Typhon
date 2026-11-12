using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>What a temp block holds.</summary>
internal enum RebuildBlockKind : ushort
{
    /// <summary>A sorted run of <see cref="StagedEntry{TKey}"/>; the owner is the run id.</summary>
    Run = 1,

    /// <summary>Element-id pairs of one multi-value field and one location bucket; the owner encodes both.</summary>
    Pairs = 2,
}

/// <summary>Where a run lives: consecutive blocks of the temp file, or a native buffer that never spilled.</summary>
internal readonly unsafe struct RunDescriptor
{
    public readonly long Offset;
    public readonly int Blocks;
    public readonly int Owner;
    public readonly long Entries;
    public readonly byte* Memory;
    public readonly RebuildBlockKind Kind;

    public RunDescriptor(long offset, int blocks, int owner, long entries, RebuildBlockKind kind = RebuildBlockKind.Run)
    {
        Offset = offset;
        Blocks = blocks;
        Owner = owner;
        Entries = entries;
        Memory = null;
        Kind = kind;
    }

    public RunDescriptor(byte* memory, long entries, int owner)
    {
        Offset = 0;
        Blocks = 0;
        Owner = owner;
        Entries = entries;
        Memory = memory;
        Kind = RebuildBlockKind.Run;
    }

    /// <summary>True for a run held in memory.</summary>
    public bool InMemory => Memory != null;
}

/// <summary>
/// The scratch file of one archetype's index rebuild (#1588, design §D4, §8): fixed 64 KiB blocks, each with a 32-byte header carrying two CRC32Cs, read
/// back and verified block by block (RB-10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never outlives the process.</b> On Windows the handle is opened <see cref="FileOptions.DeleteOnClose"/>, so the kernel deletes the file when the
/// process dies; elsewhere the file is unlinked right after it is opened, so it has no name and dies with its descriptor. <see cref="Sweep"/>, at every
/// open, covers anything left anyway.
/// </para>
/// <para>
/// <b>Synchronous I/O, never <see cref="FileOptions.Asynchronous"/>:</b> a synchronous read through an overlapped handle costs about twice as much on
/// Windows (huge-scan assessment). Buffered, no write-through, no flush: the data is recomputable, and a crash makes the next open rebuild from the
/// clusters. Offsets are reserved with <see cref="Interlocked.Add(ref long, long)"/>, so the rebuild's workers write side by side (design Phase 5),
/// each through a staging buffer of its own.
/// </para>
/// <para>
/// A write packs whole blocks — header, payload, zero pad — into the caller's staging buffer and writes it in one call, rather than a gather write of
/// header and payload slices: on Windows a synchronous handle's gather is one <c>WriteFile</c> per segment ("WriteFileGather does not support sync
/// handles", <c>RandomAccess.Windows.cs</c>), two to three system calls a block where the copy costs one 64 KiB <c>memcpy</c>.
/// </para>
/// </remarks>
internal sealed unsafe class RebuildTempFile : IDisposable
{
    public const int BlockSize = 64 * 1024;
    public const int HeaderSize = 32;
    public const string Extension = ".tmp";

    /// <summary>Every rebuild temp file's name starts with this; the sweep deletes nothing else.</summary>
    public const string FilePrefix = "ix-";

    private const uint Magic = 0x4258_4954;   // "TIXB", little-endian
    private const ushort FormatVersion = 1;

    private SafeFileHandle _handle;
    private long _end;
    private long _bytesWritten;
    private long _bytesRead;

    private RebuildTempFile(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The file's path (gone from the directory already, off Windows).</summary>
    public string Path { get; }

    /// <summary>The bytes reserved so far: the file's logical end.</summary>
    public long Length => Volatile.Read(ref _end);

    /// <summary>The bytes written, for the stats.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>The bytes read back, for the stats.</summary>
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>The records of <paramref name="stride"/> bytes one block holds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Capacity(int stride) => (BlockSize - HeaderSize) / stride;

    /// <summary>The blocks <paramref name="count"/> records of <paramref name="stride"/> bytes take.</summary>
    public static int BlocksFor(long count, int stride) => (int)((count + Capacity(stride) - 1) / Capacity(stride));

    /// <summary>Creates the temp file <paramref name="name"/> in <paramref name="directory"/>, which is created if missing.</summary>
    /// <param name="directory">The rebuild's temp directory.</param>
    /// <param name="name">The file name, ending in <see cref="Extension"/>.</param>
    /// <param name="preallocationSize">A best-effort size hint; 0 for none.</param>
    public static RebuildTempFile Create(string directory, string name, long preallocationSize = 0)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, name);
        var windows = OperatingSystem.IsWindows();
        var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, windows ? FileOptions.DeleteOnClose : FileOptions.None,
            Math.Max(0, preallocationSize));
        if (!windows)
        {
            // Unlinked now, it has no name to outlive the process with; the descriptor keeps the data until it closes.
            try
            {
                File.Delete(path);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        return new RebuildTempFile(handle, path);
    }

    /// <summary>
    /// Deletes every rebuild temp file (<see cref="FilePrefix"/>*<see cref="Extension"/>) left in <paramref name="directory"/>, a directory of this
    /// database's own, before an open's first rebuild. A file another live process holds open cannot be deleted on Windows and is skipped: the bundle
    /// lock means it is not this database's.
    /// </summary>
    /// <returns>The files deleted.</returns>
    public static int Sweep(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, FilePrefix + "*" + Extension))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }

    /// <summary>
    /// Writes <paramref name="count"/> records of <paramref name="stride"/> bytes from <paramref name="records"/> as one run of consecutive blocks.
    /// </summary>
    /// <param name="records">The records, contiguous, at least <c>count × stride</c> bytes.</param>
    /// <param name="stride">The bytes a record takes.</param>
    /// <param name="count">The records to write; 0 writes nothing.</param>
    /// <param name="kind">What the blocks hold.</param>
    /// <param name="owner">The run id, or the pairs' field and bucket.</param>
    /// <param name="staging">The caller's write buffer: a non-zero multiple of <see cref="BlockSize"/>.</param>
    public RunDescriptor Write(ReadOnlySpan<byte> records, int stride, long count, RebuildBlockKind kind, int owner, Span<byte> staging)
    {
        var blocks = BlocksFor(count, stride);
        if (blocks == 0)
        {
            return new RunDescriptor(Length, 0, owner, 0, kind);
        }

        var offset = Reserve(blocks);
        WriteBlocks(offset, 0, records, stride, count, kind, owner, staging);
        return new RunDescriptor(offset, blocks, owner, count, kind);
    }

    /// <summary>Reserves <paramref name="blocks"/> consecutive blocks at the file's end, for a run written in several pieces; returns their offset.</summary>
    public long Reserve(int blocks) => Interlocked.Add(ref _end, (long)blocks * BlockSize) - (long)blocks * BlockSize;

    /// <summary>
    /// Writes <paramref name="count"/> records as the blocks <paramref name="firstSequence"/>.. of a run reserved at <paramref name="runOffset"/>. Every
    /// piece but a run's last must fill its blocks: a multiple of <see cref="Capacity"/> records.
    /// </summary>
    public void WriteBlocks(long runOffset, int firstSequence, ReadOnlySpan<byte> records, int stride, long count, RebuildBlockKind kind, int owner,
        Span<byte> staging)
    {
        if (staging.Length < BlockSize || staging.Length % BlockSize != 0)
        {
            ThrowHelper.ThrowArgument($"RebuildTempFile: the staging buffer holds {staging.Length} bytes, not a non-zero multiple of {BlockSize}.");
        }

        var capacity = Capacity(stride);
        var blocks = BlocksFor(count, stride);
        var offset = runOffset + (long)firstSequence * BlockSize;
        var staged = 0;
        var written = 0L;
        for (var b = 0; b < blocks; b++)
        {
            var inBlock = (int)Math.Min(capacity, count - (long)b * capacity);
            var block = staging.Slice(staged, BlockSize);
            var payload = records.Slice(b * capacity * stride, inBlock * stride);
            payload.CopyTo(block.Slice(HeaderSize));
            block.Slice(HeaderSize + payload.Length).Clear();
            WriteHeader(block, kind, owner, firstSequence + b, inBlock, stride, Crc32CUtil.Compute(payload));
            staged += BlockSize;

            if (staged == staging.Length || b == blocks - 1)
            {
                RandomAccess.Write(_handle, staging.Slice(0, staged), offset + written);
                written += staged;
                staged = 0;
            }
        }

        Interlocked.Add(ref _bytesWritten, written);
    }

    /// <summary>Reads <c>destination.Length / BlockSize</c> whole blocks at <paramref name="offset"/>; a short read fails the open as a corrupt block
    /// does.</summary>
    public void Read(long offset, Span<byte> destination)
    {
        var done = 0;
        while (done < destination.Length)
        {
            var n = RandomAccess.Read(_handle, destination.Slice(done), offset + done);
            if (n <= 0)
            {
                throw new IndexRebuildTempCorruptException(Path, offset + done, $"short read: {done} of {destination.Length} bytes");
            }

            done += n;
        }

        Interlocked.Add(ref _bytesRead, done);
    }

    /// <summary>
    /// Checks one block read back at <paramref name="offset"/> against what its reader expects, and returns its payload: <c>Count × stride</c> bytes.
    /// </summary>
    /// <exception cref="IndexRebuildTempCorruptException">Any check failed (RB-10): the block is never used.</exception>
    public ReadOnlySpan<byte> Verify(ReadOnlySpan<byte> block, long offset, RebuildBlockKind kind, int owner, int sequence, int stride)
    {
        ref readonly var h = ref MemoryMarshal.AsRef<BlockHeader>(block);
        string failed = null;
        if (h.Magic != Magic)
        {
            failed = $"magic 0x{h.Magic:X8}";
        }
        else if (h.HeaderCrc != Crc32CUtil.Compute(block.Slice(0, HeaderSize - sizeof(uint))))
        {
            failed = "header CRC";
        }
        else if (h.Version != FormatVersion)
        {
            failed = $"version {h.Version}";
        }
        else if (h.Kind != (ushort)kind)
        {
            failed = $"kind {h.Kind}, expected {(ushort)kind}";
        }
        else if (h.Owner != owner)
        {
            failed = $"owner {h.Owner}, expected {owner}";
        }
        else if (h.Sequence != sequence)
        {
            failed = $"sequence {h.Sequence}, expected {sequence}";
        }
        else if (h.Stride != stride)
        {
            failed = $"stride {h.Stride}, expected {stride}";
        }
        else if (h.Count <= 0 || h.Count > Capacity(stride))
        {
            failed = $"count {h.Count}, capacity {Capacity(stride)}";
        }
        else if (h.PayloadCrc != Crc32CUtil.Compute(block.Slice(HeaderSize, h.Count * stride)))
        {
            failed = "payload CRC";
        }

        if (failed != null)
        {
            throw new IndexRebuildTempCorruptException(Path, offset, failed);
        }

        return block.Slice(HeaderSize, h.Count * stride);
    }

    /// <summary>Test hook: overwrites bytes of the file in place, to stage a corruption. The handle is exclusive, so nothing else can.</summary>
    internal void PatchForTest(long offset, ReadOnlySpan<byte> bytes) => RandomAccess.Write(_handle, bytes, offset);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        handle?.Dispose();
    }

    private static void WriteHeader(Span<byte> block, RebuildBlockKind kind, int owner, int sequence, int count, int stride, uint payloadCrc)
    {
        ref var h = ref MemoryMarshal.AsRef<BlockHeader>(block);
        h.Magic = Magic;
        h.Version = FormatVersion;
        h.Kind = (ushort)kind;
        h.Owner = owner;
        h.Sequence = sequence;
        h.Count = count;
        h.Stride = stride;
        h.PayloadCrc = payloadCrc;
        h.HeaderCrc = Crc32CUtil.Compute(block.Slice(0, HeaderSize - sizeof(uint)));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = HeaderSize)]
    private struct BlockHeader
    {
        public uint Magic;
        public ushort Version;
        public ushort Kind;
        public int Owner;
        public int Sequence;
        public int Count;
        public int Stride;
        public uint PayloadCrc;
        public uint HeaderCrc;
    }
}
