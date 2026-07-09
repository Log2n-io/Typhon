using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thrown when a component's runtime struct definition is incompatible with the persisted schema.
/// Contains the full <see cref="SchemaDiff"/> for programmatic inspection of all detected changes.
/// </summary>
[PublicAPI]
public class SchemaValidationException : TyphonException
{
    /// <summary>The full diff between persisted and runtime schema.</summary>
    public SchemaDiff Diff { get; }

    /// <summary>
    /// Creates a new <see cref="SchemaValidationException"/> from the diff describing the incompatible changes.
    /// </summary>
    /// <param name="diff">The full diff between the persisted and runtime schema.</param>
    public SchemaValidationException(SchemaDiff diff) : base(TyphonErrorCode.SchemaValidation, diff.FormatDetailedMessage())
    {
        Diff = diff;
    }
}
