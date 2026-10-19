using System;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// What a backup point's image says about itself, read from its own meta pair (pages 0 and 1) with <see cref="BootstrapReader"/>: the same pair
/// selection the engine's open makes, so the pack and the restore can never disagree with it.
/// </summary>
internal readonly record struct BackupImageMeta(bool Usable, string DatabaseName, int FormatRevision, long CheckpointLsn, bool CleanShutdown,
    Guid DatabaseId, string Problem)
{
    /// <summary>Reads the meta pair through any page source.</summary>
    public static BackupImageMeta Read(IPageSource source)
    {
        var view = BootstrapReader.Read(source);
        if (!view.IsUsable)
        {
            return new BackupImageMeta(false, null, 0, 0, false, Guid.Empty,
                view.SelectedSlot < 0 ? "neither meta slot (pages 0 and 1) is valid" : $"page 0 signature '{view.Signature}' is not Typhon's");
        }

        var (lsn, clean) = view.ReadWatermarks();
        var databaseId = view.TryGet(DatabaseEngine.BK_DatabaseId, out var v) ? UnpackGuid(v) : Guid.Empty;
        return new BackupImageMeta(true, view.DatabaseName, view.FormatRevision, lsn, clean, databaseId, null);
    }

    /// <summary>Reads the meta pair from the two page images themselves.</summary>
    public static BackupImageMeta Read(ReadOnlySpan<byte> page0, ReadOnlySpan<byte> page1)
    {
        using var source = new MetaPairPageSource(page0, page1);
        return Read(source);
    }

    /// <summary>A bootstrap Guid: four <c>int</c>s over its 16 bytes, the packing of <c>DatabaseEngine.PackDatabaseId</c>.</summary>
    public static Guid UnpackGuid(BootstrapDictionary.Value value)
    {
        if (value.IntCount < 4)
        {
            return Guid.Empty;
        }

        Span<byte> bytes = stackalloc byte[16];
        var ints = MemoryMarshal.Cast<byte, int>(bytes);
        for (var i = 0; i < 4; i++)
        {
            ints[i] = value.GetInt(i);
        }

        return new Guid(bytes);
    }

    /// <summary>An <see cref="IPageSource"/> holding only the meta pair.</summary>
    private sealed class MetaPairPageSource : IPageSource
    {
        private readonly byte[] _pages = new byte[2 * IntegrityConstants.PageSize];

        public MetaPairPageSource(ReadOnlySpan<byte> page0, ReadOnlySpan<byte> page1)
        {
            page0[..IntegrityConstants.PageSize].CopyTo(_pages);
            page1[..IntegrityConstants.PageSize].CopyTo(_pages.AsSpan(IntegrityConstants.PageSize));
        }

        public int PageCount => 2;

        public bool TryReadPage(int index, Span<byte> destination)
        {
            if ((uint)index > 1)
            {
                return false;
            }

            _pages.AsSpan(index * IntegrityConstants.PageSize, IntegrityConstants.PageSize).CopyTo(destination);
            return true;
        }

        public string Describe() => "meta pair";

        public void Dispose()
        {
        }
    }
}
