using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thrown when an insert or update would create a duplicate key in a unique index.
/// </summary>
[PublicAPI]
public class UniqueConstraintViolationException : TyphonException
{
    private const string DefaultMessage = "A duplicate key was detected in a unique index.";

    /// <summary>
    /// Creates a new <see cref="UniqueConstraintViolationException"/> with the default message.
    /// </summary>
    public UniqueConstraintViolationException() : base(TyphonErrorCode.UniqueConstraintViolation, DefaultMessage)
    {
    }
}
