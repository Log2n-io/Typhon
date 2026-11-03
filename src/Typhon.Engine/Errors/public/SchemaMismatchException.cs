using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// An open with <see cref="SchemaMatch.Exact"/> found the registered schema, or an assembly declaring it, different from what the database holds (#1485):
/// the build is not the one that wrote it. Nothing of the difference was written.
/// </summary>
[PublicAPI]
public class SchemaMismatchException : TyphonException
{
    /// <summary>What differs, one line each: the component, archetype or assembly, what the database holds and what the build has.</summary>
    public IReadOnlyList<string> Differences { get; }

    /// <summary>Creates the refusal of <paramref name="subject"/>, listing its <paramref name="differences"/>.</summary>
    /// <param name="subject">The component, archetype or assembly refused.</param>
    /// <param name="differences">What differs.</param>
    public SchemaMismatchException(string subject, IReadOnlyList<string> differences) : base(TyphonErrorCode.SchemaMismatch,
        $"{subject} does not match the database exactly (an exact-schema open: a drill or a restore's settle opens only with the build that wrote it): "
        + string.Join("; ", differences) + ".")
    {
        Differences = differences;
    }
}
