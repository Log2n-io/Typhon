using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Wraps a strongly-typed <see cref="MigrationFunc{TOld,TNew}"/> behind the uniform <see cref="IMigrationEntry"/> interface.
/// Uses <see cref="Unsafe.As{TFrom,TTo}"/> to reinterpret byte spans as struct references — zero-copy, no allocations.
/// </summary>
internal sealed class TypedMigrationEntry<TOld, TNew> : IMigrationEntry where TOld : unmanaged where TNew : unmanaged
{
    private readonly MigrationFunc<TOld, TNew> _func;

    public string ComponentName { get; }
    public int FromRevision { get; }
    public int ToRevision { get; }
    public int OldSize { get; }
    public int NewSize { get; }

    public TypedMigrationEntry(MigrationFunc<TOld, TNew> func)
    {
        ArgumentNullException.ThrowIfNull(func);

        var oldAttr = typeof(TOld).GetCustomAttribute<ComponentAttribute>();
        var newAttr = typeof(TNew).GetCustomAttribute<ComponentAttribute>();

        if (oldAttr == null)
        {
            throw new ArgumentException($"Type '{typeof(TOld).Name}' is missing [Component] attribute.", nameof(func));
        }

        if (newAttr == null)
        {
            throw new ArgumentException($"Type '{typeof(TNew).Name}' is missing [Component] attribute.", nameof(func));
        }

        if (oldAttr.Name != newAttr.Name)
        {
            throw new ArgumentException(
                $"Component name mismatch: '{typeof(TOld).Name}' has Name='{oldAttr.Name}' but '{typeof(TNew).Name}' has Name='{newAttr.Name}'. " +
                "Migration functions must transform between revisions of the same component.");
        }

        if (oldAttr.Revision >= newAttr.Revision)
        {
            throw new ArgumentException(
                $"Revision must increase: '{typeof(TOld).Name}' has Revision={oldAttr.Revision} but '{typeof(TNew).Name}' has Revision={newAttr.Revision}. " +
                "Only forward migrations are supported.");
        }

        _func = func;
        ComponentName = oldAttr.Name;
        FromRevision = oldAttr.Revision;
        ToRevision = newAttr.Revision;
        OldSize = Unsafe.SizeOf<TOld>();
        NewSize = Unsafe.SizeOf<TNew>();
    }

    public void Execute(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ref var oldRef = ref Unsafe.As<byte, TOld>(ref MemoryMarshal.GetReference(source));
        ref var newRef = ref Unsafe.As<byte, TNew>(ref MemoryMarshal.GetReference(destination));
        _func(ref oldRef, out newRef);
    }
}
