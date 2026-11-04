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
    private readonly ContextMigrationFunc<TOld, TNew> _contextFunc;

    public string ComponentName { get; }
    public int FromRevision { get; }
    public int ToRevision { get; }
    public int OldSize { get; }
    public int NewSize { get; }

    public TypedMigrationEntry(MigrationFunc<TOld, TNew> func) : this(func, null)
    {
    }

    /// <summary>A migration function that sets texts through its <see cref="MigrationContext"/> (#1094).</summary>
    public TypedMigrationEntry(ContextMigrationFunc<TOld, TNew> func) : this(null, func)
    {
    }

    private TypedMigrationEntry(MigrationFunc<TOld, TNew> func, ContextMigrationFunc<TOld, TNew> contextFunc)
    {
        if (func == null && contextFunc == null)
        {
            throw new ArgumentNullException(nameof(func));
        }

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
        _contextFunc = contextFunc;
        ComponentName = oldAttr.Name;
        FromRevision = oldAttr.Revision;
        ToRevision = newAttr.Revision;
        OldSize = Unsafe.SizeOf<TOld>();
        NewSize = Unsafe.SizeOf<TNew>();
    }

    public void Execute(ReadOnlySpan<byte> source, Span<byte> destination, MigrationContext context)
    {
        ref var oldRef = ref Unsafe.As<byte, TOld>(ref MemoryMarshal.GetReference(source));
        ref var newRef = ref Unsafe.As<byte, TNew>(ref MemoryMarshal.GetReference(destination));
        if (_contextFunc != null)
        {
            _contextFunc(ref oldRef, out newRef, context ?? new MigrationContext(null));
            return;
        }

        _func(ref oldRef, out newRef);
    }
}
