namespace Typhon.Workbench.Dtos.Schema;

/// <summary>
/// Full layout of a component type — the data the Schema Inspector's Layout view needs to render a cache-line-aligned
/// byte grid with padding and cross-boundary warnings. Sizes are in bytes.
/// </summary>
/// <param name="StorageSize">User-authored data bytes (ComponentStorageSize) — what the byte grid draws.</param>
/// <param name="TotalSize">StorageSize + EntityPKOverheadSize header — shown as metadata, not drawn.</param>
/// <param name="Fields">Ordered ascending by Offset; padding is implicit (gaps between consecutive fields).</param>
/// <param name="StorageMode">MVCC storage mode — "Versioned" / "SingleVersion" / "Transient" (GAP-25).</param>
public record ComponentSchemaDto(
    string TypeName,
    string FullName,
    int StorageSize,
    int TotalSize,
    bool AllowMultiple,
    int Revision,
    FieldDto[] Fields,
    string StorageMode);
