using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Thrown at commit when a transaction points a cascade foreign key (<c>[Index(OnParentDelete = CascadeAction.Delete)]</c>) at a parent that does not
/// exist, is destroyed, or is being destroyed by a committing transaction. The commit has made no change: its transaction is still open and may be rolled
/// back, or committed again once the reference is fixed.
/// </summary>
[PublicAPI]
public class ForeignKeyViolationException : TyphonException
{
    /// <summary>Creates the exception for <paramref name="child"/> referencing <paramref name="parent"/>.</summary>
    public ForeignKeyViolationException(EntityId child, EntityId parent, bool parentDying)
        : base(TyphonErrorCode.ForeignKeyViolation, parentDying
            ? $"Entity {child} references {parent}, which a committing transaction is destroying."
            : $"Entity {child} references {parent}, which does not exist or is destroyed.")
    {
        Child = child;
        Parent = parent;
        ParentDying = parentDying;
    }

    /// <summary>The entity whose foreign key names <see cref="Parent"/>.</summary>
    public EntityId Child { get; }

    /// <summary>The parent the foreign key names.</summary>
    public EntityId Parent { get; }

    /// <summary>The parent was being destroyed, not yet destroyed: that destroy may still abort, and a retry may then succeed.</summary>
    public bool ParentDying { get; }

    /// <summary>True when the parent was only being destroyed: its destroy may abort.</summary>
    public override bool IsTransient => ParentDying;
}
