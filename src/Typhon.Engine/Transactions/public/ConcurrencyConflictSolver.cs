// unset

using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Provides conflict resolution context when a write-write conflict is detected during
/// <see cref="Transaction.Commit(ConcurrencyConflictHandler)"/>.
/// </summary>
/// <remarks>
/// <para>
/// During commit, each entity is checked for conflicts (another transaction committed new data since our read).
/// When a conflict is detected and a <see cref="ConcurrencyConflictHandler"/> was provided, the solver is populated
/// with the conflicting entity's data, then the handler is invoked once per conflicting entity.
/// </para>
/// <para>The four data views are:</para>
/// <list type="bullet">
/// <item><description><see cref="ReadData{T}"/>: the component state at the time of <c>ReadEntity</c> (our transaction's snapshot baseline).</description></item>
/// <item><description><see cref="CommittedData{T}"/>: the latest committed state by another transaction (the value that caused the conflict).</description></item>
/// <item><description><see cref="CommittingData{T}"/>: the dirty-write state from our <c>UpdateEntity</c> call (what we intended to commit).</description></item>
/// <item><description><see cref="ToCommitData{T}"/>: the output buffer — initialized with <c>CommittingData</c> (last writer wins). The handler writes the resolved value here.</description></item>
/// </list>
/// <para>
/// The default resolution (before the handler runs) is "last writer wins": the committing data is copied into <c>ToCommitData</c>.
/// The handler can override this by writing a different value (e.g., rebasing a delta onto the latest committed state).
/// </para>
/// </remarks>
[PublicAPI]
public unsafe class ConcurrencyConflictSolver
{
    private byte* _readData;
    private byte* _committedData;
    private byte* _committingData;
    private byte* _toCommitData;
    private ComponentInfo _info;

    [ThreadStatic]
    private static ConcurrencyConflictSolver ThreadLocalConflictSolver;

    internal ConcurrencyConflictSolver() { }

    internal static ConcurrencyConflictSolver GetConflictSolver()
    {
        ThreadLocalConflictSolver ??= new ConcurrencyConflictSolver();
        ThreadLocalConflictSolver.Reset();
        return ThreadLocalConflictSolver;
    }

    /// <summary>Primary key of the conflicting entity.</summary>
    public long PrimaryKey { get; private set; }

    /// <summary>The .NET type of the component (e.g. <c>typeof(CompA)</c>).</summary>
    public Type ComponentType => _info.ComponentTable.Definition.POCOType;

    /// <summary>The component definition with field metadata.</summary>
    public DBComponentDefinition ComponentDefinition => _info.ComponentTable.Definition;

    /// <summary>Whether a conflict was detected for this solver instance.</summary>
    public bool HasConflict { get; private set; }

    /// <summary>Copies <see cref="ReadData{T}"/> into <see cref="ToCommitData{T}"/> (discard all changes, revert to read snapshot).</summary>
    public void TakeRead<T>() where T : unmanaged => ToCommitData<T>() = ReadData<T>();

    /// <summary>Copies <see cref="CommittedData{T}"/> into <see cref="ToCommitData{T}"/> (accept the other transaction's value).</summary>
    public void TakeCommitted<T>() where T : unmanaged => ToCommitData<T>() = CommittedData<T>();

    /// <summary>Copies <see cref="CommittingData{T}"/> into <see cref="ToCommitData{T}"/> (last writer wins — this is the default).</summary>
    public void TakeCommitting<T>() where T : unmanaged => ToCommitData<T>() = CommittingData<T>();

    /// <summary>Returns a ref to the component state as read by our transaction (snapshot baseline).</summary>
    public ref T ReadData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_readData);

    /// <summary>Returns a ref to the latest committed state by another transaction (the conflicting value).</summary>
    public ref T CommittedData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committedData);

    /// <summary>Returns a ref to our dirty-write state from <c>UpdateEntity</c> (what we intended to commit).</summary>
    public ref T CommittingData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_committingData);

    /// <summary>Returns a ref to the output buffer. Write the resolved value here. Initialized with <see cref="CommittingData{T}"/>.</summary>
    public ref T ToCommitData<T>() where T : unmanaged => ref Unsafe.AsRef<T>(_toCommitData);

    internal void Setup(long pk, ComponentInfo info, byte* readData, byte* committedData, byte* committingData, byte* toCommitData)
    {
        PrimaryKey = pk;
        _readData = readData;
        _committedData = committedData;
        _committingData = committingData;
        _toCommitData = toCommitData;
        _info = info;
        HasConflict = true;

        // Default is last revision wins, so we copy the committing data to the toCommit data
        var componentSize = info.ComponentTable.ComponentStorageSize;
        new Span<byte>(_committingData, componentSize).CopyTo(new Span<byte>(_toCommitData, componentSize));
    }

    internal void Reset()
    {
        HasConflict = false;
    }
}
