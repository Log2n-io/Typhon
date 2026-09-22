using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Precomputed layout descriptor for a spatial R-Tree node. All fields are readonly so the JIT treats them as constants after inlining, enabling a single
/// generic implementation to serve all four variants (2D/3D x f32/f64) without runtime polymorphism overhead.
/// </summary>
internal readonly struct SpatialNodeDescriptor
{
    // Core layout parameters
    public readonly int Stride;
    public readonly int CoordCount;       // 4 (2D) or 6 (3D)
    public readonly int CoordSize;        // 4 (f32) or 8 (f64)
    public readonly int HeaderSize;
    public readonly int EntryAreaSize;    // Stride - HeaderSize

    // Category filtering — union mask in header, per-entry mask in leaf SOA
    public readonly int UnionCategoryMaskOffset; // offset of 4-byte union mask in node header (both leaf and internal)

    // Leaf SOA layout
    public readonly int LeafCapacity;
    public readonly int LeafCoordOffsets;       // = HeaderSize (start of first coord array)
    public readonly int LeafCoordStride;        // LeafCapacity * CoordSize (distance between coord arrays)
    public readonly int LeafIdOffset;           // start of payload id array
    public readonly int LeafIdSize;             // 8 (payload id always 64-bit)
    public readonly int LeafCategoryMaskOffset; // start of CategoryMask array (uint per entry)
    public readonly int LeafCategoryMaskSize;   // 4 (CategoryMask always uint)

    // Internal SOA layout
    public readonly int InternalCapacity;
    public readonly int InternalCoordStride; // InternalCapacity * CoordSize
    public readonly int InternalIdOffset;    // start of ChildChunkId array
    public readonly int InternalIdSize;      // 4 (ChunkId always int)

    // Rebalancing threshold
    public readonly int MinFill;          // ceil(LeafCapacity * 0.4)

    // Pre-built instances for the four standard configurations
    public static readonly SpatialNodeDescriptor R2Df32 = FromVariant(SpatialVariant.R2Df32, 512);
    public static readonly SpatialNodeDescriptor R3Df32 = FromVariant(SpatialVariant.R3Df32, 512);
    public static readonly SpatialNodeDescriptor R2Df64 = FromVariant(SpatialVariant.R2Df64, 512);
    public static readonly SpatialNodeDescriptor R3Df64 = FromVariant(SpatialVariant.R3Df64, 768);

    private SpatialNodeDescriptor(SpatialVariant variant, int stride)
    {
        bool is3D = variant is SpatialVariant.R3Df32 or SpatialVariant.R3Df64;
        bool isF64 = variant is SpatialVariant.R2Df64 or SpatialVariant.R3Df64;

        Stride = stride;
        CoordCount = is3D ? 6 : 4;
        CoordSize = isF64 ? 8 : 4;

        // Header: OlcVersion(4) + Control(4) + ParentChunkId(4) + NodeMBR(CoordCount * CoordSize) + UnionCategoryMask(4)
        // The UnionCategoryMask sits immediately after NodeMBR (replaces old 2D alignment padding)
        UnionCategoryMaskOffset = 12 + CoordCount * CoordSize;
        HeaderSize = UnionCategoryMaskOffset + 4;

        EntryAreaSize = Stride - HeaderSize;

        // +8 payload id (64-bit) + 4 CategoryMask (uint).
        //
        // A 4-byte ComponentChunkId sat between them until #872 step 13. It was the owning component's chunk id, carried so a two-pass compound query
        // could reach component storage without an EntityMap lookup — a service only the ENTITY-level tree could offer, since a cluster tree's payload IS a
        // cluster chunk id and it passed zero here. With that tree retired the field was 4 bytes of zero per entry on the only trees left, and dropping it
        // raises LeafCapacity: R2Df32 15 -> 17, R3Df32 11 -> 13, R2Df64 9 -> 10. R3Df64 stays at 11 — its 704-byte entry area divides by 60 the same way it
        // divided by 64 — which is why AC-13.5 asks only about the f32 variants.
        int leafEntrySize = CoordCount * CoordSize + 8 + 4;
        int internalEntrySize = CoordCount * CoordSize + 4;  // +4 for ChildChunkId (int)

        LeafCapacity = EntryAreaSize / leafEntrySize;
        InternalCapacity = EntryAreaSize / internalEntrySize;

        // Leaf SOA offsets: [Coords...] [PayloadIds] [CategoryMasks]
        LeafCoordOffsets = HeaderSize;
        LeafCoordStride = LeafCapacity * CoordSize;
        LeafIdOffset = LeafCoordOffsets + CoordCount * LeafCoordStride;
        LeafIdSize = 8;
        LeafCategoryMaskOffset = LeafIdOffset + LeafCapacity * LeafIdSize;
        LeafCategoryMaskSize = 4;

        // Internal SOA offsets
        InternalCoordStride = InternalCapacity * CoordSize;
        InternalIdOffset = HeaderSize + CoordCount * InternalCoordStride;
        InternalIdSize = 4;

        MinFill = (int)Math.Ceiling(LeafCapacity * 0.4);
    }

    /// <summary>
    /// Create a descriptor for the given variant and stride.
    /// </summary>
    public static SpatialNodeDescriptor FromVariant(SpatialVariant variant, int stride) => new(variant, stride);

    /// <summary>
    /// Get the pre-built descriptor for a variant using standard strides (512 for most, 768 for 3D-f64).
    /// </summary>
    public static SpatialNodeDescriptor ForVariant(SpatialVariant variant) => variant switch
    {
        SpatialVariant.R2Df32 => R2Df32,
        SpatialVariant.R3Df32 => R3Df32,
        SpatialVariant.R2Df64 => R2Df64,
        SpatialVariant.R3Df64 => R3Df64,
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown spatial variant")
    };
}
