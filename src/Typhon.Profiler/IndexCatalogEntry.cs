namespace Typhon.Profiler;

/// <summary>
/// One entry in the v7+ <c>IndexCatalog</c> table — a flat list of every B+Tree (or spatial) index defined on the schema, keyed by (ComponentTypeId, FieldId).
/// Redundant with the per-field <c>HasIndex</c> flag in <see cref="ComponentDefinitionRecord.Fields"/> but flat-listed here so the
/// Workbench <c>SchemaIndexes</c> panel can iterate indexes without traversing every component.
/// </summary>
public sealed class IndexCatalogEntry
{
    /// <summary>Component type id (matches <see cref="ComponentDefinitionRecord.ComponentTypeId"/>).</summary>
    public int ComponentTypeId { get; init; }

    /// <summary>Field id within the component (matches <see cref="FieldDefinitionRecord.FieldId"/>).</summary>
    public int FieldId { get; init; }

    /// <summary>
    /// Index variant byte. Encodes the value-type half-byte (Byte=0 / Short=1 / Int=2 / Long=3 / Float=4 / Double=5 / Char=6 / String64=7) in the low nibble;
    /// the high nibble carries the multiplicity flag (0=Single, 1=Multiple). Matches the dispatch the engine uses to pick between <c>SingleBTree&lt;T&gt;</c>
    /// and <c>MultipleBTree&lt;T&gt;</c> at registration.
    /// </summary>
    public byte Variant { get; init; }

    /// <summary>Convenience flag — true iff the index allows multiple values per key.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>True iff this is a spatial (R-Tree) index rather than B+Tree. Spatial entries leave <see cref="Variant"/> = 0xFF.</summary>
    public bool IsSpatial { get; init; }

    /// <summary>True iff the index was auto-created by the engine (vs. explicitly declared via <c>[Index]</c>).</summary>
    public bool IsAuto { get; init; }
}
