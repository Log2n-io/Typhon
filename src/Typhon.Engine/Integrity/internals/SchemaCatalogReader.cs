using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>One archetype as recovered from the persisted manifest, with every pointer already range-checked.</summary>
internal sealed class ArchetypeView
{
    /// <summary>Archetype name, decoded from the row's inline <c>String64</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Durable per-database routing id — the value embedded in every <c>EntityId</c> of this archetype.</summary>
    public int RoutingId { get; init; }

    /// <summary>Total component count, own plus inherited. The value the entity-record size is derived from.</summary>
    public int ComponentCount { get; init; }

    /// <summary>Entity-key allocator watermark at the last clean close. <c>ALO-02</c> compares live keys against it.</summary>
    public long NextEntityKey { get; init; }

    /// <summary>Root page of the cluster segment, or <c>0</c> when the archetype has no cluster storage.</summary>
    public int ClusterSegmentRoot { get; init; }

    /// <summary>Root page of the EntityMap segment, or <c>0</c> when it was not persisted.</summary>
    public int EntityMapRoot { get; init; }

    /// <summary>Root page of the per-archetype secondary-index segment, or <c>0</c>.</summary>
    public int IndexRoot { get; init; }

    /// <summary>Root page of the per-archetype <c>String64</c> index segment, or <c>0</c>.</summary>
    public int String64IndexRoot { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} (routing {RoutingId}, {ComponentCount} components)";
}

/// <summary>One component definition as recovered from the persisted manifest.</summary>
internal sealed class ComponentView
{
    /// <summary>Component schema name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Per-entity data size in bytes, excluding storage overhead.</summary>
    public int Size { get; init; }

    /// <summary>Per-entity storage overhead in bytes; <c>0</c> when the layout carries none.</summary>
    public int Overhead { get; init; }

    /// <summary>Number of non-static fields.</summary>
    public int FieldCount { get; init; }

    /// <summary>Root page of the component's data segment.</summary>
    public int ComponentSegmentRoot { get; init; }

    /// <summary>Root page of the component's revision-table segment; <c>0</c> for a non-Versioned component.</summary>
    public int RevisionSegmentRoot { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Size} B, {FieldCount} fields)";
}

/// <summary>
/// Reads the database's own schema manifest from raw bytes — the catalogs that make a Typhon file self-describing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the design said it could not.</b>
/// <c>claude/design/Durability/Integrity/09-closing-the-gap.md</c> §1 concluded "the schema is not in the file", from
/// <c>DBD = new DatabaseDefinitions()</c> — a true statement about how the engine <i>boots</i>, not about what the file
/// <i>records</i>. Alongside the rebuilt runtime definitions the engine persists <c>ComponentR1</c> and
/// <c>ArchetypeR1</c>, and between them they carry component sizes, field counts, segment roots, per-archetype component
/// counts and the entity-key watermark. §1.1 records the correction; this is what acts on it.
/// </para>
/// <para>
/// <b>Why the stride had to come first.</b> The catalogs are themselves ordinary chunk-based segments, so none of this
/// was readable until format revision 7 put the stride on the page (#753). Stride was the only fact genuinely missing,
/// and it turned out to be the keystone rather than one item on a list.
/// </para>
/// <para>
/// <b>Every pointer is validated before it is followed.</b> That is <c>MAP-04</c>'s discipline applied one level up: a
/// scanner that dereferences a damaged catalog into a crash is worse than useless on the databases it exists to
/// diagnose. A row that does not resolve is reported through <see cref="Diagnostics"/> and skipped, never followed.
/// </para>
/// </remarks>
internal sealed class SchemaCatalogReader
{
    /// <summary>Bootstrap key naming the component catalog's segment.</summary>
    private const string ComponentCatalogKey = "sys.ComponentR1";

    private readonly IPageSource _source;
    private readonly SegmentWalker _walker;
    private readonly HashSet<int> _knownRoots;
    private readonly List<string> _diagnostics = [];

    /// <summary>Creates a reader over a page source.</summary>
    /// <param name="source">The source to read through.</param>
    /// <param name="knownSegmentRoots">Segment roots the physical sweep found, used to validate every pointer.</param>
    public SchemaCatalogReader(IPageSource source, IEnumerable<int> knownSegmentRoots)
    {
        _source = source;
        _walker = new SegmentWalker(source);
        _knownRoots = [.. knownSegmentRoots];
    }

    /// <summary>Components recovered from the manifest, by schema name.</summary>
    public Dictionary<string, ComponentView> Components { get; } = new(StringComparer.Ordinal);

    /// <summary>Archetypes recovered from the manifest, by name.</summary>
    public Dictionary<string, ArchetypeView> Archetypes { get; } = new(StringComparer.Ordinal);

    /// <summary>What could not be read, in the scanner's own words. Empty on a healthy manifest.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Whether anything at all was recovered.</summary>
    public bool IsUsable => Components.Count > 0;

    /// <summary>Reads both catalogs. Never throws on damage; unreadable rows land in <see cref="Diagnostics"/>.</summary>
    /// <param name="bootstrap">The parsed bootstrap, which names the component catalog's segment.</param>
    public void Read(BootstrapView bootstrap)
    {
        if (bootstrap == null || !bootstrap.IsUsable)
        {
            _diagnostics.Add("The bootstrap is unusable, so the schema manifest could not be located.");
            return;
        }

        if (!bootstrap.TryGet(ComponentCatalogKey, out var spi) || spi.IntCount < 1)
        {
            _diagnostics.Add($"The bootstrap does not name '{ComponentCatalogKey}', so no component definitions could be read.");
            return;
        }

        var catalogRoot = spi.GetInt(0);
        foreach (var row in ReadRows<ComponentR1>(catalogRoot, "component catalog"))
        {
            var name = SafeName(row.Name.AsString);
            if (name.Length == 0 || row.CompSize <= 0)
            {
                continue;   // the segment's reserved sentinel chunk decodes as all-zero
            }

            Components[name] = new ComponentView
            {
                Name = name,
                Size = row.CompSize,
                Overhead = row.CompOverhead,
                FieldCount = row.FieldCount,
                ComponentSegmentRoot = Resolve(row.ComponentSPI, name, nameof(ComponentR1.ComponentSPI)),
                RevisionSegmentRoot = Resolve(row.VersionSPI, name, nameof(ComponentR1.VersionSPI))
            };
        }

        // The archetype catalog is an ordinary component like any other, so it is found BY NAME in the catalog that was
        // just read — not by a hard-coded bootstrap key, because it does not have one.
        if (!Components.TryGetValue(ArchetypeR1.SchemaName, out var archetypeDef) || archetypeDef.ComponentSegmentRoot == 0)
        {
            _diagnostics.Add(
                $"The component catalog does not describe '{ArchetypeR1.SchemaName}', so no archetype could be read. "
                + "Per-archetype checks (component counts, entity-key watermarks, cluster and EntityMap roots) are unavailable.");
            return;
        }

        foreach (var row in ReadRows<ArchetypeR1>(archetypeDef.ComponentSegmentRoot, "archetype catalog"))
        {
            var name = SafeName(row.Name.AsString);
            if (name.Length == 0)
            {
                continue;
            }

            Archetypes[name] = new ArchetypeView
            {
                Name = name,
                RoutingId = row.RoutingId,
                ComponentCount = row.ComponentCount,
                NextEntityKey = row.NextEntityKey,
                ClusterSegmentRoot = Resolve(row.ClusterSegmentSPI, name, nameof(ArchetypeR1.ClusterSegmentSPI)),
                EntityMapRoot = Resolve(row.EntityMapSPI, name, nameof(ArchetypeR1.EntityMapSPI)),
                IndexRoot = Resolve(row.ClusterIndexSPI, name, nameof(ArchetypeR1.ClusterIndexSPI)),
                String64IndexRoot = Resolve(row.ClusterString64IndexSPI, name, nameof(ArchetypeR1.ClusterString64IndexSPI))
            };
        }
    }

    /// <summary>Enumerates one catalog segment's allocated chunks, decoded as <typeparamref name="T"/> rows.</summary>
    private IEnumerable<T> ReadRows<T>(int segmentRoot, string what) where T : unmanaged
    {
        if (segmentRoot <= 0 || segmentRoot >= _source.PageCount)
        {
            _diagnostics.Add($"The {what} points at page {segmentRoot}, which is outside the file.");
            yield break;
        }

        var page = new byte[IntegrityConstants.PageSize];
        if (!_source.TryReadPage(segmentRoot, page))
        {
            _diagnostics.Add($"The {what}'s root page {segmentRoot} could not be read.");
            yield break;
        }

        var geometry = ChunkGeometry.FromPage(page);
        if (!geometry.IsUsable)
        {
            _diagnostics.Add($"The {what}'s root page {segmentRoot} records no chunk stride, so its rows cannot be located.");
            yield break;
        }

        var segment = _walker.WalkSegment(segmentRoot);
        var pages = segment.Pages;
        var capacity = geometry.Capacity(pages.Count);

        for (var id = 0; id < capacity; id++)
        {
            if (!geometry.TryLocate(id, out var ordinal, out var chunkInPage) || ordinal >= pages.Count)
            {
                continue;
            }

            if (!_source.TryReadPage(pages[ordinal], page) || !geometry.IsChunkAllocated(page, ordinal == 0, chunkInPage))
            {
                continue;
            }

            var at = geometry.OffsetInPage(ordinal, chunkInPage);
            if (at + geometry.Stride > IntegrityConstants.PageSize)
            {
                _diagnostics.Add($"The {what}'s chunk {id} would run past the end of page {pages[ordinal]}; it was skipped.");
                continue;
            }

            yield return ReadRow<T>(new ReadOnlySpan<byte>(page, at, geometry.Stride));
        }
    }

    /// <summary>
    /// Decodes one persisted row, tolerating a chunk <b>smaller</b> than the CLR struct.
    /// </summary>
    /// <remarks>
    /// The engine persists a component's own size; the CLR rounds its type up to the alignment of its widest field.
    /// <c>ArchetypeR1</c> is 108 bytes on disk and 112 in memory, because <c>long NextEntityKey</c> forces the type to a
    /// multiple of 8 — so a direct <see cref="MemoryMarshal.Read{T}(ReadOnlySpan{byte})"/> reads four bytes it does not
    /// own: the next row's, or past the page's raw-data area on the last chunk of a page, where it throws instead.
    /// <c>ComponentR1</c> hides this entirely, its 160 bytes already landing on an 8-boundary, so a reader written and
    /// tested against the component catalog alone works right up until it meets the archetype one.
    /// </remarks>
    private static T ReadRow<T>(ReadOnlySpan<byte> chunk) where T : unmanaged
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
        buffer.Clear();
        chunk[..Math.Min(chunk.Length, buffer.Length)].CopyTo(buffer);
        return MemoryMarshal.Read<T>(buffer);
    }

    /// <summary>Accepts a segment pointer only when the physical sweep independently found that segment.</summary>
    private int Resolve(int candidate, string owner, string field)
    {
        if (candidate == 0)
        {
            return 0;   // "not persisted" is a legitimate, documented value for several of these
        }

        if (_knownRoots.Contains(candidate))
        {
            return candidate;
        }

        _diagnostics.Add($"'{owner}' names page {candidate} as its {field}, but no segment is rooted there; the pointer was not followed.");
        return 0;
    }

    /// <summary>Trims a decoded name and rejects one that is empty or implausible, rather than propagating garbage.</summary>
    private static string SafeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var trimmed = raw.Trim();
        for (var i = 0; i < trimmed.Length; i++)
        {
            // A damaged row can decode to control characters that would corrupt every report they appear in.
            if (char.IsControl(trimmed[i]))
            {
                return "";
            }
        }

        return trimmed;
    }
}
