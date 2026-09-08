using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Shared spatial decoding for the cluster path, plus the migration-storm warning.
/// </summary>
/// <remarks>
/// <b>Named for what it used to do.</b> This class held the insert / update / remove maintenance for the entity-level R-Tree — the fat-AABB containment
/// check, the back-pointer fixups, the Layer-1 occupancy counters. #872 step 13 removed that tree, and with it every writer here. Two things survived: the
/// migration-storm warning, and <see cref="ReadAndValidateBoundsFromPtr"/> — the single decoder for all eight <see cref="SpatialFieldType"/> shapes, which
/// the CLUSTER path had always borrowed and which has <b>8</b> live call sites in <c>src/</c>. (It said 18 until #916 counted them.)
/// </remarks>
internal static unsafe partial class SpatialMaintainer
{
    // ── LoggerMessage partials ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cluster migration storm: {MigrationCount} migrations in a single tick for archetype id {ArchetypeId} ({DurationMs:F3} ms) — possible viewport warp, teleport event, or unphysical speed")]
    internal static partial void LogHighMigrationRate(ILogger logger, int migrationCount, ushort archetypeId, double durationMs);

    /// <summary>
    /// Read spatial bounds from a raw field pointer, convert BSphere to AABB if needed.
    /// Used by cluster path where fieldPtr points directly into cluster SoA data.
    /// Returns false if bounds are degenerate (NaN/Inf/Min>Max).
    /// </summary>
    /// <remarks>
    /// <b>The <c>SpatialNodeDescriptor desc</c> parameter was removed in #916 O3 because nothing ever read it.</b> Every call site passed
    /// <c>ss.Descriptor</c> — 72 bytes, by value — into a parameter this method's body never mentions, once per entity on the narrowphase, which is roughly
    /// half of a typical query. #916 set out to stop <c>AabbClusterEnumerator</c> carrying a second copy of the descriptor and take it <c>in</c> at the point
    /// of use; the point of use turned out not to have one. The field and the parameter both went instead, which is strictly better than passing a dead value
    /// cheaply. Whether the JIT was already eliding the copy is not the question — an argument nothing reads should not be in the signature.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ReadAndValidateBoundsFromPtr(byte* fieldPtr, SpatialFieldInfo fi, Span<double> coords)
    {
        switch (fi.FieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                var aabb = *(AABB2F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3F:
            {
                var aabb = *(AABB3F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.AABB2D:
            {
                var aabb = *(AABB2D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3D:
            {
                var aabb = *(AABB3D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            default:
                return false;
        }

        return true;
    }
}
