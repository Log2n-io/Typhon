using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Marks a struct as "no-copy": the analyzer will flag any by-value parameter passing (TYPHON001)
/// and any value-copying assignment or return (TYPHON003).
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute to large structs that must be passed by <c>ref</c> to avoid expensive stack
/// copies and to preserve mutable internal state (caches, epoch pins, etc.).
/// </para>
/// <para>
/// The TYPHON001 analyzer enforces that parameters of the marked type use the <c>ref</c> modifier —
/// <c>in</c>, <c>out</c>, and by-value passing are rejected. The TYPHON003 analyzer detects value copies
/// through assignments, variable initializations, and return statements.
/// </para>
/// <example>
/// <code>
/// [NoCopy(Reason = "248-byte struct with mutable SIMD cache")]
/// [StructLayout(LayoutKind.Sequential)]
/// public struct MyBigStruct { /* ... */ }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
internal sealed class NoCopyAttribute : Attribute
{
    /// <summary>
    /// Optional explanation included in the diagnostic message when a violation is detected.
    /// </summary>
    public string Reason { get; set; }
}
