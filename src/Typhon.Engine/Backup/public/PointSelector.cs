using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>Which backup point an operation targets: the newest one, or one by its id. (Restore-to-LSN and restore-to-time come with #1418.)</summary>
[PublicAPI]
public readonly struct PointSelector : IEquatable<PointSelector>
{
    private PointSelector(bool latest, uint backupId)
    {
        IsLatest = latest;
        BackupId = backupId;
    }

    /// <summary>The point with the highest id in the store.</summary>
    public static PointSelector Latest => new(true, 0);

    /// <summary>The point whose id is <paramref name="backupId"/>.</summary>
    /// <param name="backupId">A point id (1 or more).</param>
    public static PointSelector ById(uint backupId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(backupId);
        return new PointSelector(false, backupId);
    }

    /// <summary>Whether this selects the newest point.</summary>
    public bool IsLatest { get; }

    /// <summary>The selected id; <c>0</c> for <see cref="Latest"/>.</summary>
    public uint BackupId { get; }

    /// <inheritdoc />
    public bool Equals(PointSelector other) => IsLatest == other.IsLatest && BackupId == other.BackupId;

    /// <inheritdoc />
    public override bool Equals(object obj) => obj is PointSelector other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsLatest, BackupId);

    /// <inheritdoc />
    public override string ToString() => IsLatest ? "latest" : $"#{BackupId}";
}
