using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Marks a method or property of a <c>[NoCopy]</c> type that deliberately returns a copy of its own instance — <c>GetEnumerator() =&gt; this</c>,
/// <c>Current =&gt; this</c> — so TYPHON003 does not report that return.
/// </summary>
/// <remarks>
/// <para>Such a copy duplicates live state and is safe only because its caller consumes it: <c>foreach</c> walks and disposes the copy
/// <c>GetEnumerator</c> returns and never touches the original.</para>
/// <para>A factory needs no marker (#1538). The analyzer used to require it on every method returning a no-copy type by value, because a call site
/// cannot tell a factory from a getter. It does not need to: a getter's copy is its own <c>return _field;</c>, which TYPHON003 reports where it is
/// written. A call site is still reported when the member returns by <c>ref</c> (an existing instance) or is declared to return a type parameter
/// (<c>Nullable&lt;T&gt;.Value</c>, whose body no rule can see as a copy).</para>
/// </remarks>
/// <example>
/// <code>
/// [NoCopy]
/// public ref struct RangeEnumerator
/// {
///     [AllowCopy]   // foreach takes this enumerator: the copy is the one it walks and disposes
///     public RangeEnumerator GetEnumerator() => this;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false)]
internal sealed class AllowCopyAttribute : Attribute;
