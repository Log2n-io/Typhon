using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// Wraps a <see cref="ByteMigrationFunc"/> behind the uniform <see cref="IMigrationEntry"/> interface.
/// Used when the old struct type is no longer available in code and the user must manipulate raw bytes.
/// </summary>
internal sealed class ByteMigrationEntry : IMigrationEntry
{
    private readonly ByteMigrationFunc _func;

    public string ComponentName { get; }
    public int FromRevision { get; }
    public int ToRevision { get; }
    public int OldSize { get; }
    public int NewSize { get; }

    public ByteMigrationEntry(string componentName, int fromRevision, int toRevision, int oldSize, int newSize, ByteMigrationFunc func)
    {
        ArgumentNullException.ThrowIfNull(componentName);
        ArgumentNullException.ThrowIfNull(func);

        if (fromRevision >= toRevision)
        {
            throw new ArgumentException($"Revision must increase: fromRevision={fromRevision} >= toRevision={toRevision}. Only forward migrations are supported.");
        }

        if (oldSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(oldSize), oldSize, "Old component size must be positive.");
        }

        if (newSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newSize), newSize, "New component size must be positive.");
        }

        _func = func;
        ComponentName = componentName;
        FromRevision = fromRevision;
        ToRevision = toRevision;
        OldSize = oldSize;
        NewSize = newSize;
    }

    public void Execute(ReadOnlySpan<byte> source, Span<byte> destination) => _func(source, destination);
}
