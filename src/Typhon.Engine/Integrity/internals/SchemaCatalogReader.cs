using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>One field of a component, as recovered from the persisted <c>FieldR1</c> collection.</summary>
/// <remarks>
/// Field-level decode was the whole justification for <c>G5</c> (<c>typhon check --schema &lt;dll&gt;</c>). It is in the
/// file — see <c>09 §5.5</c> — so the increment is closed and these four checks need no assembly:
/// <c>CLU-03</c>, <c>CLU-04</c>, <c>IDX-03</c>, <c>IDX-04</c>.
/// </remarks>
internal sealed class FieldView
{
    /// <summary>Field name as declared on the component.</summary>
    public string Name { get; init; } = "";

    /// <summary>Stable numeric id within its component.</summary>
    public int FieldId { get; init; }

    /// <summary>Logical field type.</summary>
    public FieldType Type { get; init; }

    /// <summary>Byte offset within the component's per-entity storage.</summary>
    public int Offset { get; init; }

    /// <summary>Byte size within the component's per-entity storage.</summary>
    public int Size { get; init; }

    /// <summary>Whether the field carries an index.</summary>
    public bool HasIndex { get; init; }

    /// <summary>Whether that index permits several entries per key.</summary>
    public bool IndexAllowMultiple { get; init; }

    /// <summary>Root page of the field's own index segment, or <c>0</c>; already range-checked.</summary>
    public int IndexRoot { get; init; }

    /// <summary>Whether the field is static — not stored per entity.</summary>
    public bool IsStatic { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} : {Type} @{Offset}+{Size}{(HasIndex ? " [indexed]" : "")}";
}

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

    /// <summary>Component schema names in slot order, from the row's VSBS-backed collection. Empty when unreadable.</summary>
    public IReadOnlyList<string> ComponentNames { get; init; } = [];

    /// <summary>The buffer id the row records for its component-name collection, as read. <c>0</c> when it has none.</summary>
    public int ComponentNamesBufferId { get; init; }

    /// <summary>
    /// Number of <c>Versioned</c> components — the quantity the EntityMap's value record is sized by.
    /// </summary>
    /// <remarks>
    /// <c>-1</c> when the component names could not be read, which is what stops the <c>MAP</c> family rather than a
    /// guess: <c>RecordSize = 19 + 4 × versionedSlotCount</c>, and a wrong count silently shifts every key in the map.
    /// </remarks>
    public int VersionedSlotCount { get; init; } = -1;

    /// <summary>
    /// The EntityMap value-record size, <c>ClusterEntityRecordAccessor.RecordSize(VersionedSlotCount)</c>, or <c>-1</c>.
    /// </summary>
    /// <remarks>
    /// This is the number that unblocks <c>MAP-01</c>/<c>MAP-02</c>. The map is a
    /// <c>RawValuePagedHashMap&lt;long,…&gt;</c> whose value size is a <i>runtime</i> argument, so bucket capacity —
    /// and therefore where the key array ends — cannot be derived from the stride alone. It is derivable from the
    /// manifest, through the archetype's component list and each component's storage mode.
    /// </remarks>
    public int EntityRecordSize => VersionedSlotCount < 0 ? -1 : ClusterEntityRecordAccessor.RecordSize(VersionedSlotCount);

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

    /// <summary>The component's persisted storage mode.</summary>
    public StorageMode StorageMode { get; init; }

    /// <summary>Field descriptors in declaration order, from the row's VSBS-backed collection. Empty when unreadable.</summary>
    public IReadOnlyList<FieldView> Fields { get; init; } = [];

    /// <summary>The buffer id the row records for its field collection, as read. <c>0</c> when it has none.</summary>
    /// <remarks>Kept alongside the decoded elements because <c>ALO-04</c> accounts for handles, not for contents.</remarks>
    public int FieldsBufferId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Size} B, {FieldCount} fields, {StorageMode})";
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
    private readonly VsbsReader _vsbs;

    /// <summary>Component-collection segments by chunk stride — how a buffer id is resolved to the segment holding it.</summary>
    private readonly Dictionary<int, (SegmentView Segment, ChunkGeometry Geometry)> _collectionsByStride = [];

    /// <summary>Creates a reader over a page source.</summary>
    /// <param name="source">The source to read through.</param>
    /// <param name="knownSegmentRoots">Segment roots the physical sweep found, used to validate every pointer.</param>
    public SchemaCatalogReader(IPageSource source, IEnumerable<int> knownSegmentRoots)
    {
        _source = source;
        _walker = new SegmentWalker(source);
        _knownRoots = [.. knownSegmentRoots];
        _vsbs = new VsbsReader(source);
    }

    /// <summary>Components recovered from the manifest, by schema name.</summary>
    public Dictionary<string, ComponentView> Components { get; } = new(StringComparer.Ordinal);

    /// <summary>Archetypes recovered from the manifest, by name.</summary>
    public Dictionary<string, ArchetypeView> Archetypes { get; } = new(StringComparer.Ordinal);

    /// <summary>What could not be read, in the scanner's own words. Empty on a healthy manifest.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>Whether anything at all was recovered.</summary>
    public bool IsUsable => Components.Count > 0;

    /// <summary>The component-collection segments found, by chunk stride — what <c>ALO-04</c> accounts for handles in.</summary>
    public IReadOnlyDictionary<int, (SegmentView Segment, ChunkGeometry Geometry)> CollectionSegments => _collectionsByStride;

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

        IndexCollectionSegments();

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
                StorageMode = (StorageMode)row.StorageMode,
                Fields = ReadFields(row, name),
                FieldsBufferId = row.Fields._bufferId,
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

            var componentNames = ReadComponentNames(row, name);

            Archetypes[name] = new ArchetypeView
            {
                Name = name,
                RoutingId = row.RoutingId,
                ComponentCount = row.ComponentCount,
                NextEntityKey = row.NextEntityKey,
                ComponentNames = componentNames,
                ComponentNamesBufferId = row.ComponentNames._bufferId,
                VersionedSlotCount = CountVersionedSlots(componentNames, name),
                ClusterSegmentRoot = Resolve(row.ClusterSegmentSPI, name, nameof(ArchetypeR1.ClusterSegmentSPI)),
                EntityMapRoot = Resolve(row.EntityMapSPI, name, nameof(ArchetypeR1.EntityMapSPI)),
                IndexRoot = Resolve(row.ClusterIndexSPI, name, nameof(ArchetypeR1.ClusterIndexSPI)),
                String64IndexRoot = Resolve(row.ClusterString64IndexSPI, name, nameof(ArchetypeR1.ClusterString64IndexSPI))
            };
        }
    }

    /// <summary>
    /// Maps every component-collection segment by its chunk stride.
    /// </summary>
    /// <remarks>
    /// A buffer id is a chunk id, and chunk ids are only unique <i>within</i> a segment — the engine pools collection
    /// segments by stride (<c>GetComponentCollectionSegment</c>), so an id on its own names nothing. The element type
    /// picks the stride, and the stride picks the segment. Any stride claimed by two segments is reported rather than
    /// silently resolved to whichever came first, because that would decode one collection's buffers out of another's
    /// pages.
    /// </remarks>
    private void IndexCollectionSegments()
    {
        var page = new byte[IntegrityConstants.PageSize];

        foreach (var root in _knownRoots)
        {
            if (!_source.TryReadPage(root, page))
            {
                continue;
            }

            var segment = _walker.WalkSegment(root);
            if (segment.Kind != StorageSegmentKind.ComponentCollection)
            {
                continue;
            }

            var geometry = ChunkGeometry.FromPage(page);
            if (!geometry.IsUsable)
            {
                _diagnostics.Add($"The component-collection segment rooted at page {root} records no chunk stride, so its buffers cannot be located.");
                continue;
            }

            if (_collectionsByStride.TryGetValue(geometry.Stride, out var first))
            {
                _diagnostics.Add($"Two component-collection segments (pages {first.Segment.RootPageIndex} and {root}) both use stride "
                    + $"{geometry.Stride}; collection fields of that element size were not decoded.");
                continue;
            }

            _collectionsByStride[geometry.Stride] = (segment, geometry);
        }
    }

    /// <summary>Reads one component row's <c>FieldR1</c> collection.</summary>
    private IReadOnlyList<FieldView> ReadFields(ComponentR1 row, string owner)
    {
        var raw = ReadCollection<FieldR1>(row.Fields._bufferId, owner, nameof(ComponentR1.Fields));
        if (raw.Count == 0)
        {
            return [];
        }

        var fields = new List<FieldView>(raw.Count);
        foreach (var f in raw)
        {
            var name = SafeName(f.Name.AsString);
            if (name.Length == 0)
            {
                continue;
            }

            fields.Add(new FieldView
            {
                Name = name,
                FieldId = f.FieldId,
                Type = f.Type,
                Offset = f.OffsetInComponentStorage,
                Size = f.SizeInComponentStorage,
                HasIndex = f.HasIndex,
                IndexAllowMultiple = f.IndexAllowMultiple,
                IsStatic = f.IsStatic,
                IndexRoot = Resolve((int)f.IndexSPI, $"{owner}.{name}", nameof(FieldR1.IndexSPI))
            });
        }

        return fields;
    }

    /// <summary>Reads one archetype row's component-name collection.</summary>
    private IReadOnlyList<string> ReadComponentNames(ArchetypeR1 row, string owner)
    {
        var raw = ReadCollection<String64>(row.ComponentNames._bufferId, owner, nameof(ArchetypeR1.ComponentNames));
        if (raw.Count == 0)
        {
            return [];
        }

        var names = new List<string>(raw.Count);
        foreach (var s in raw)
        {
            var name = SafeName(s.AsString);
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Resolves a buffer id against the collection segment sized for <typeparamref name="T"/> and reads it.</summary>
    private List<T> ReadCollection<T>(int bufferId, string owner, string field) where T : unmanaged
    {
        var elements = new List<T>();
        if (bufferId == 0)
        {
            return elements;   // never allocated — an empty collection, not damage
        }

        var stride = VsbsReader.StrideForElementSize(Unsafe.SizeOf<T>());
        if (!_collectionsByStride.TryGetValue(stride, out var target))
        {
            _diagnostics.Add($"'{owner}' has a {field} collection in a segment of stride {stride}, but no such component-collection "
                + "segment was found; the collection was not read.");
            return elements;
        }

        var before = _vsbs.Diagnostics.Count;
        if (!_vsbs.TryReadBuffer(target.Segment, target.Geometry, bufferId, elements))
        {
            for (var i = before; i < _vsbs.Diagnostics.Count; i++)
            {
                _diagnostics.Add($"'{owner}'.{field}: {_vsbs.Diagnostics[i]}");
            }

            elements.Clear();
        }

        return elements;
    }

    /// <summary>
    /// Counts an archetype's <c>Versioned</c> components, which is what sizes its EntityMap value record.
    /// </summary>
    /// <remarks>
    /// Returns <c>-1</c> rather than a partial count when any named component is missing from the catalog. An
    /// undercount is not a smaller answer, it is a different record size, and every key read out of the map with it
    /// would land in the wrong place — the failure mode <c>MAP-04</c> exists to prevent, arrived at arithmetically
    /// instead of through a bad pointer.
    /// </remarks>
    private int CountVersionedSlots(IReadOnlyList<string> componentNames, string archetype)
    {
        if (componentNames.Count == 0)
        {
            return -1;
        }

        var versioned = 0;
        foreach (var name in componentNames)
        {
            if (!Components.TryGetValue(name, out var component))
            {
                _diagnostics.Add($"Archetype '{archetype}' names component '{name}', which the component catalog does not describe; "
                    + "its entity-record size could not be derived and EntityMap checks were skipped.");
                return -1;
            }

            if (component.StorageMode == StorageMode.Versioned)
            {
                versioned++;
            }
        }

        return versioned;
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
    /// A persisted chunk can be narrower than the CLR struct that decodes it, and a direct <see cref="MemoryMarshal.Read{T}(ReadOnlySpan{byte})"/> would then
    /// read bytes it does not own: the next row's, or past the page's raw-data area on the last chunk of a page, where it throws instead.
    /// <para>
    /// Until #816 the commonest source of that gap was alignment: the engine persisted a component's field extent while the CLR rounded the type up to the
    /// alignment of its widest field, which made <c>ArchetypeR1</c> 108 bytes on disk and 112 in memory. That divergence is gone — a component's storage size
    /// IS <c>sizeof(T)</c> now (SCHEMA-06), and <c>ArchetypeR1</c> carries <c>StructLayout.Pack = 4</c>, which brings the type itself to 108. What remains is
    /// schema evolution: rows
    /// written by an older, narrower revision of a component outlive it on disk, and this reader is what lets the integrity scanner decode them without a
    /// migration pass.
    /// </para>
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
