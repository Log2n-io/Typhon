using System;
using System.Buffers.Binary;
using System.IO;

namespace Typhon.Engine.Internals;

/// <summary>What a <c>backup-state</c> file holds: the session's backup lineage and its change map, as a clean close left them.</summary>
internal sealed class BackupStateContents
{
    /// <summary>The nonce the close wrote here and into the data file's meta page, in the flip that set the clean flag. Never 0.</summary>
    public ulong Nonce { get; init; }

    /// <summary>The database the file belongs to.</summary>
    public Guid DatabaseId { get; init; }

    /// <summary>The WAL timeline at the close.</summary>
    public Guid TimelineId { get; init; }

    /// <summary>The store the last point went to.</summary>
    public Guid StoreId { get; init; }

    /// <summary>The last point's chain.</summary>
    public Guid ChainId { get; init; }

    /// <summary>The last point's id.</summary>
    public uint LastBackupId { get; init; }

    /// <summary>The data file's length in pages at the close.</summary>
    public int DataFilePages { get; init; }

    /// <summary>The last point's <c>P</c>.</summary>
    public long LastPointLsn { get; init; }

    /// <summary>The change map over <c>[0, DataFilePages)</c>, dense: every page written since the last point's freeze.</summary>
    public ulong[] Map { get; init; } = [];
}

/// <summary>How reading a <c>backup-state</c> file ended.</summary>
internal enum BackupStateReadResult
{
    /// <summary>Not read: the last close was not clean, so no file can vouch for this data file.</summary>
    NotConsulted,

    /// <summary>The file was read, is intact and carries the expected nonce.</summary>
    Loaded,

    /// <summary>No file.</summary>
    Missing,

    /// <summary>The file belongs to another close than the one the data file records: stale, copied in, or the close wrote none (nonce 0).</summary>
    NonceMismatch,

    /// <summary>Damaged or not a backup-state file.</summary>
    Invalid,

    /// <summary>The file could not be read.</summary>
    Unreadable,
}

/// <summary>
/// <c>{bundle}/backup-state</c>: the backup lineage and the change map a TRUSTED session leaves at its clean close, so the next session's first backup
/// can be incremental (02 §1.2, BKP-06). The chunk-summary pattern (#1143): a random nonce written here and into the meta page in the flip that sets the
/// clean flag; every open that follows clears the nonce in the data file, so after a crash, a failed open, an offline repair or a restore the file no
/// longer vouches for anything. Any doubt reads as "no file": the next backup is full, which costs I/O and nothing else.
/// </summary>
/// <remarks>
/// <b>Layout</b> (little-endian). Header, 112 bytes: magic <c>TYBKSTAT</c>, u16 version, u16 flags, u32 reserved, u64 nonce, GUIDs DatabaseId,
/// TimelineId, StoreId, ChainId, u32 LastBackupId, u32 DataFilePages, i64 LastPointLsn, u32 map word count, u32 CRC32C of the first 108 bytes. Body:
/// the map words, u64 each. Footer: u32 CRC32C of the body.
/// </remarks>
internal static class BackupStateFile
{
    /// <summary>The file's name inside the bundle.</summary>
    internal const string FileName = "backup-state";

    private const ulong Magic = 0x5441_5453_4B42_5954;   // "TYBKSTAT"
    private const ushort Version = 1;
    private const int HeaderSize = 112;
    private const int HeaderCrcOffset = 108;

    /// <summary>The file's path in <paramref name="bundleDirectory"/>.</summary>
    internal static string PathIn(string bundleDirectory) => Path.Combine(bundleDirectory, FileName);

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, replacing any previous file through a temporary file and a rename, so a crash leaves
    /// the old file or the new one, never a torn one.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">What to write; its nonce must not be 0.</param>
    /// <param name="flushToDisk">Whether to fsync before the rename. False only in test mode.</param>
    internal static void Write(string path, BackupStateContents contents, bool flushToDisk)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Nonce == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contents), "0 is the nonce of a close that wrote no backup-state.");
        }

        var map = contents.Map ?? [];
        var buffer = new byte[HeaderSize + map.Length * sizeof(ulong) + sizeof(uint)];
        var header = buffer.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], Version);
        BinaryPrimitives.WriteUInt64LittleEndian(header[16..], contents.Nonce);
        contents.DatabaseId.TryWriteBytes(header[24..]);
        contents.TimelineId.TryWriteBytes(header[40..]);
        contents.StoreId.TryWriteBytes(header[56..]);
        contents.ChainId.TryWriteBytes(header[72..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], contents.LastBackupId);
        BinaryPrimitives.WriteInt32LittleEndian(header[92..], contents.DataFilePages);
        BinaryPrimitives.WriteInt64LittleEndian(header[96..], contents.LastPointLsn);
        BinaryPrimitives.WriteInt32LittleEndian(header[104..], map.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HeaderCrcOffset..], Crc32CUtil.Compute(header[..HeaderCrcOffset]));

        var body = buffer.AsSpan(HeaderSize, map.Length * sizeof(ulong));
        for (var i = 0; i < map.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(body[(i * sizeof(ulong))..], map[i]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(HeaderSize + body.Length), Crc32CUtil.Compute(body));

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(buffer);
            stream.Flush(flushToDisk);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Rewrites the file after an offline backup (#1413): the same nonce (the data file did not change), a new lineage, and an empty map (the point just
    /// taken covers every page).
    /// </summary>
    internal static void Rewrite(string path, BackupStateContents previous, Guid storeId, Guid chainId, uint backupId, long pointLsn, bool flushToDisk)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Write(path, new BackupStateContents
        {
            Nonce = previous.Nonce,
            DatabaseId = previous.DatabaseId,
            TimelineId = previous.TimelineId,
            StoreId = storeId,
            ChainId = chainId,
            LastBackupId = backupId,
            DataFilePages = previous.DataFilePages,
            LastPointLsn = pointLsn,
            Map = new ulong[(previous.DataFilePages + 63) >> 6],
        }, flushToDisk);
    }

    /// <summary>
    /// Reads the file and returns its contents when it was written under <paramref name="expectedNonce"/> (never 0) and is intact; otherwise
    /// <c>null</c>, with the reason in <paramref name="result"/>.
    /// </summary>
    internal static BackupStateContents TryRead(string path, ulong expectedNonce, out BackupStateReadResult result)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                result = BackupStateReadResult.Missing;
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result = BackupStateReadResult.Unreadable;
            return null;
        }

        return Parse(bytes, expectedNonce, out result);
    }

    /// <summary>The validation half of <see cref="TryRead"/>, over bytes in memory.</summary>
    internal static BackupStateContents Parse(ReadOnlySpan<byte> bytes, ulong expectedNonce, out BackupStateReadResult result)
    {
        result = BackupStateReadResult.Invalid;
        if (bytes.Length < HeaderSize + sizeof(uint))
        {
            return null;
        }

        var header = bytes[..HeaderSize];
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(header[HeaderCrcOffset..]) != Crc32CUtil.Compute(header[..HeaderCrcOffset]))
        {
            return null;
        }

        var nonce = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if (expectedNonce == 0 || nonce != expectedNonce)
        {
            result = BackupStateReadResult.NonceMismatch;
            return null;
        }

        var words = BinaryPrimitives.ReadInt32LittleEndian(header[104..]);
        var pages = BinaryPrimitives.ReadInt32LittleEndian(header[92..]);
        if (words < 0 || pages < 0 || words != (pages + 63) >> 6 || bytes.Length != HeaderSize + (long)words * sizeof(ulong) + sizeof(uint))
        {
            return null;
        }

        var body = bytes.Slice(HeaderSize, words * sizeof(ulong));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[(HeaderSize + body.Length)..]) != Crc32CUtil.Compute(body))
        {
            return null;
        }

        var map = new ulong[words];
        for (var i = 0; i < words; i++)
        {
            map[i] = BinaryPrimitives.ReadUInt64LittleEndian(body[(i * sizeof(ulong))..]);
        }

        result = BackupStateReadResult.Loaded;
        return new BackupStateContents
        {
            Nonce = nonce,
            DatabaseId = new Guid(header.Slice(24, 16)),
            TimelineId = new Guid(header.Slice(40, 16)),
            StoreId = new Guid(header.Slice(56, 16)),
            ChainId = new Guid(header.Slice(72, 16)),
            LastBackupId = BinaryPrimitives.ReadUInt32LittleEndian(header[88..]),
            DataFilePages = pages,
            LastPointLsn = BinaryPrimitives.ReadInt64LittleEndian(header[96..]),
            Map = map,
        };
    }

    /// <summary>Deletes the file (and a leftover temporary). Never throws: a file that cannot be deleted is still invalidated by its nonce.</summary>
    internal static void Delete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The nonce cleared at the next open makes it untrusted anyway.
        }
    }
}
