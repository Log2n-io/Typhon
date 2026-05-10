using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Indicates that ownership of an IDisposable resource is transferred to the callee.
/// When applied to a parameter, it means the method takes responsibility for disposing the resource.
/// When applied to a return value, it means the caller receives ownership and must dispose the resource.
/// </summary>
/// <remarks>
/// <para>
/// This attribute is used by the TYPHON004 analyzer to accurately track disposal responsibility.
/// Without this attribute, the analyzer uses heuristics that may produce false positives or negatives.
/// </para>
/// <para>
/// <b>On Parameters:</b> Indicates the method will dispose the resource or store it for later disposal.
/// The caller should NOT dispose the resource after passing it to this method.
/// </para>
/// <para>
/// <b>On Return Values:</b> Indicates the caller receives a newly-created or exclusively-owned resource.
/// The caller MUST dispose the resource when done.
/// </para>
/// <example>
/// <code>
/// // Parameter: Method takes ownership and will dispose
/// void AddTransaction([TransfersOwnership] Transaction t)
/// {
///     _transactions.Add(t);  // Container manages lifecycle
/// }
/// 
/// // Return: Caller receives ownership and must dispose
/// [return: TransfersOwnership]
/// Transaction CreateTransaction()
/// {
///     return new Transaction();  // Caller must dispose
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = false)]
internal sealed class TransfersOwnershipAttribute : Attribute
{
}
