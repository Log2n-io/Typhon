using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Engine.Internals;

/// <summary><c>store.json</c>: the store's identity, written once.</summary>
internal sealed class StoreIdentityFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid StoreId { get; set; }

    public Guid DatabaseId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; }
}

/// <summary><c>store.lock.json</c>: who holds the store lock, best effort, to name a holder in a refusal.</summary>
internal sealed class StoreLockHolder
{
    public int Pid { get; set; }

    public string Machine { get; set; }

    public DateTime StartedUtc { get; set; }

    public string Purpose { get; set; }
}

/// <summary>One pack in the catalog: its header and footer facts, its file name and size.</summary>
internal sealed class CatalogPack
{
    public string File { get; set; }

    public long Size { get; set; }

    public uint BackupId { get; set; }

    public BackupKind Kind { get; set; }

    public bool Suspect { get; set; }

    public bool Offline { get; set; }

    public Guid DatabaseId { get; set; }

    public Guid StoreId { get; set; }

    public Guid ChainId { get; set; }

    public uint ParentId { get; set; }

    public uint BaseId { get; set; }

    public int ImagePages { get; set; }

    public long CheckpointLsn { get; set; }

    public long PointLsn { get; set; }

    public DateTime FreezeUtc { get; set; }

    public DateTime TailEndUtc { get; set; }

    public int PageCount { get; set; }

    public int TailSegments { get; set; }

    public Guid TimelineId { get; set; }

    public int FormatRevision { get; set; }

    public string EngineVersion { get; set; }

    [JsonIgnore]
    public bool IsBase => Kind != BackupKind.Incremental;

    public static CatalogPack From(string file, long size, in PackHeader h, in PackFooter f) => new()
    {
        File = file,
        Size = size,
        BackupId = h.BackupId,
        Kind = h.Kind,
        Suspect = f.Suspect,
        Offline = h.Offline,
        DatabaseId = h.DatabaseId,
        StoreId = h.StoreId,
        ChainId = h.ChainId,
        ParentId = h.ParentId,
        BaseId = h.BaseId,
        ImagePages = h.ImagePages,
        CheckpointLsn = h.CheckpointLsn,
        PointLsn = f.PointLsn,
        FreezeUtc = ToUtc(h.FreezeUtcTicks),
        TailEndUtc = ToUtc(f.TailEndUtcTicks),
        PageCount = f.PageCount,
        TailSegments = f.TailSegmentCount,
        TimelineId = h.TimelineId,
        FormatRevision = h.DatabaseFormatRevision,
        EngineVersion = h.EngineVersion,
    };

    private static DateTime ToUtc(long ticks) =>
        ticks is >= 0 and <= 3155378975999999999L ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
}

/// <summary>A pack file the catalog could not read, kept so a chain through it is reported as damaged, not missing.</summary>
internal sealed class CatalogUnreadable
{
    public string File { get; set; }

    public long Size { get; set; }

    public string Reason { get; set; }
}

/// <summary>A chain's head, per <c>ChainId</c>.</summary>
internal sealed class CatalogChain
{
    public Guid ChainId { get; set; }

    public uint BaseId { get; set; }

    public uint HeadBackupId { get; set; }

    /// <summary>The chain's distinct points (an id with both an <c>-I</c> and a <c>-C</c> pack counts once).</summary>
    public int Depth { get; set; }

    /// <summary>The packs a restore of the head reads (its resolution, <c>-C</c> preferred); 0 when the head does not resolve (DL-9).</summary>
    public int HeadDepth { get; set; }

    public Guid TimelineId { get; set; }
}

/// <summary>
/// <c>catalog.json</c>: a CACHE of every pack's header and footer (D7: packs are the authority). A pure function of the packs and the id
/// high-water, so a rebuild after its loss is byte-identical.
/// </summary>
internal sealed class StoreCatalogFile
{
    /// <summary>2: <see cref="CatalogChain.Depth"/> counts points, not packs, and <see cref="CatalogChain.HeadDepth"/> exists. A v1 file is rebuilt.</summary>
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; }

    public Guid StoreId { get; set; }

    public Guid DatabaseId { get; set; }

    /// <summary>CRC32C (hex) of the sorted <c>(name, size)</c> of <c>packs/*.tpack</c>: the staleness test (DC-10), never file times.</summary>
    public string Fingerprint { get; set; }

    public uint HighWaterBackupId { get; set; }

    public List<CatalogPack> Packs { get; set; } = [];

    public List<CatalogUnreadable> Unreadable { get; set; } = [];

    public List<CatalogChain> Chains { get; set; } = [];
}

/// <summary>Builds, fingerprints and (de)serialises the catalog.</summary>
internal static class StoreCatalog
{
    public const string FileName = "catalog.json";

    /// <summary>The fingerprint of a <c>packs/</c> listing: published packs only, in name order.</summary>
    public static string Fingerprint(IReadOnlyList<StoreFileEntry> packFiles)
    {
        var crc = Crc32CUtil.Start;
        Span<byte> size = stackalloc byte[9];
        foreach (var f in packFiles)
        {
            if (!IsPublishedPack(f.Name))
            {
                continue;
            }

            crc = Crc32CUtil.Append(crc, Encoding.UTF8.GetBytes(f.Name));
            size[0] = 0;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(size[1..], f.Length);
            crc = Crc32CUtil.Append(crc, size);
        }

        return Crc32CUtil.Finish(crc).ToString("x8", CultureInfo.InvariantCulture);
    }

    public static bool IsPublishedPack(string name) => PackFormat.TryParseFileName(name, out _, out _, out var temp) && !temp;

    /// <summary>The catalog of a set of packs, in a canonical order.</summary>
    public static StoreCatalogFile Build(StoreIdentityFile identity, string fingerprint, uint highWater, List<CatalogPack> packs,
        List<CatalogUnreadable> unreadable)
    {
        packs.Sort(static (a, b) => string.CompareOrdinal(a.File, b.File));
        unreadable.Sort(static (a, b) => string.CompareOrdinal(a.File, b.File));
        var heads = new Dictionary<Guid, CatalogChain>();
        var points = new HashSet<(Guid, uint)>();
        var standing = new Dictionary<uint, CatalogPack>(packs.Count);
        foreach (var p in packs)
        {
            if (!heads.TryGetValue(p.ChainId, out var chain))
            {
                chain = new CatalogChain { ChainId = p.ChainId, BaseId = uint.MaxValue };
                heads[p.ChainId] = chain;
            }

            if (points.Add((p.ChainId, p.BackupId)))
            {
                chain.Depth++;
            }

            if (p.Kind == BackupKind.Compacted || !standing.ContainsKey(p.BackupId))
            {
                standing[p.BackupId] = p;
            }

            if (p.IsBase && p.BackupId < chain.BaseId)
            {
                chain.BaseId = p.BackupId;
            }

            if (p.BackupId >= chain.HeadBackupId)
            {
                chain.HeadBackupId = p.BackupId;
                chain.TimelineId = p.TimelineId;
            }
        }

        var chains = new List<CatalogChain>(heads.Values);
        foreach (var c in chains)
        {
            // A chain whose base pack is gone has no base to name.
            if (c.BaseId == uint.MaxValue)
            {
                c.BaseId = 0;
            }

            c.HeadDepth = ResolutionDepth(standing, c.ChainId, c.HeadBackupId);
        }

        chains.Sort(static (a, b) => a.BaseId != b.BaseId ? a.BaseId.CompareTo(b.BaseId) : a.ChainId.CompareTo(b.ChainId));
        return new StoreCatalogFile
        {
            FormatVersion = StoreCatalogFile.CurrentFormatVersion,
            StoreId = identity.StoreId,
            DatabaseId = identity.DatabaseId,
            Fingerprint = fingerprint,
            HighWaterBackupId = highWater,
            Packs = packs,
            Unreadable = unreadable,
            Chains = chains,
        };
    }

    /// <summary>
    /// The packs a restore of <paramref name="pointId"/> reads, following <c>ParentId</c> with the compacted pack of an id preferred, as
    /// <see cref="ChainResolver"/> walks it; 0 when the walk leaves the chain or misses a pack (the head is then not restorable). Headers only: the
    /// link checks are the resolver's.
    /// </summary>
    private static int ResolutionDepth(Dictionary<uint, CatalogPack> standing, Guid chainId, uint pointId)
    {
        var depth = 0;
        var id = pointId;
        while (standing.TryGetValue(id, out var pack) && pack.ChainId == chainId && depth < ChainResolver.MaxDepth)
        {
            depth++;
            if (pack.IsBase)
            {
                return depth;
            }

            if (pack.ParentId >= id)
            {
                break;
            }

            id = pack.ParentId;
        }

        return 0;
    }

    public static byte[] Serialize(StoreCatalogFile catalog) => JsonSerializer.SerializeToUtf8Bytes(catalog, BackupJsonContext.Default.StoreCatalogFile);

    /// <summary>Parses a catalog; <c>null</c> for anything unreadable or of an unknown version (it is a cache: the caller rebuilds).</summary>
    public static StoreCatalogFile TryDeserialize(ReadOnlySpan<byte> json)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize(json, BackupJsonContext.Default.StoreCatalogFile);
            return catalog is { FormatVersion: StoreCatalogFile.CurrentFormatVersion, Packs: not null, Unreadable: not null, Chains: not null }
                ? catalog
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated serialisation of the store's JSON files, so the engine stays trim/AOT-clean (#409).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(StoreIdentityFile))]
[JsonSerializable(typeof(StoreLockHolder))]
[JsonSerializable(typeof(StoreCatalogFile))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
