using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Marks a property or method as safe to return a <c>[NoCopy]</c> type by value.
/// The TYPHON003 analyzer requires this on any method or property that returns a <c>[NoCopy]</c> type,
/// whether declared on the type itself or on an external type (e.g., a factory method).
/// </summary>
/// <remarks>
/// <para>The analyzer cannot statically distinguish factories (fresh value, no real copy) from
/// getters (copy of existing live state). This attribute is the author's explicit declaration that
/// the method constructs a new value rather than copying existing state.</para>
/// <para>For inlined factories, the JIT constructs the value directly in the caller's stack slot —
/// no actual copy occurs at runtime.</para>
/// </remarks>
/// <example>
/// <code>
/// // Self-factory on the [NoCopy] type itself
/// [NoCopy]
/// public struct UnitOfWorkContext
/// {
///     [AllowCopy]
///     public static UnitOfWorkContext None => new(...);
/// }
///
/// // External factory returning a [NoCopy] type
/// public class ChunkBasedSegment
/// {
///     [AllowCopy]
///     internal ChunkAccessor CreateChunkAccessor() => new(...);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AllowCopyAttribute : Attribute;
