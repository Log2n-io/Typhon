using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Convenience extensions for <see cref="DatabaseEngine"/>.
/// </summary>
[PublicAPI]
public static class DatabaseEngineExtensions
{
    /// <summary>
    /// Creates a single-transaction UoW with auto-dispose semantics. When the returned transaction is disposed, the backing UoW is also disposed automatically.
    /// </summary>
    /// <param name="dbe">The database engine.</param>
    /// <param name="durabilityMode">Controls when WAL records become crash-safe. Default is <see cref="DurabilityMode.Deferred"/>.</param>
    /// <returns>A transaction whose <see cref="System.IDisposable.Dispose"/> also disposes the owning UoW.</returns>
    /// <remarks>
    /// This is a convenience wrapper for the common single-transaction pattern:
    /// <code>
    /// using var tx = dbe.CreateQuickTransaction();
    /// tx.CreateEntity(ref comp);
    /// tx.Commit();
    /// // tx.Dispose() also disposes the backing UoW
    /// </code>
    /// </remarks>
    [return: TransfersOwnership]
    public static Transaction CreateQuickTransaction(this DatabaseEngine dbe, DurabilityMode durabilityMode = DurabilityMode.Deferred)
    {
        var uow = dbe.CreateUnitOfWork(durabilityMode);
        dbe.LogUowLifecycle("CreateQuickTransaction: UoW created, calling CreateTransaction");
        var tx = uow.CreateTransaction();
        dbe.LogQuickTxCreated(tx.TSN);
        tx.OwnsUnitOfWork = true;
        return tx;
    }

    /// <summary>
    /// Creates a read-only transaction with no UnitOfWork and no ChangeSet allocation.
    /// Write operations (Create/Update/Delete) throw <see cref="System.InvalidOperationException"/>.
    /// Commit is a no-op. Dispose exits the epoch scope and removes from the chain.
    /// </summary>
    [return: TransfersOwnership]
    public static Transaction CreateReadOnlyTransaction(this DatabaseEngine dbe) => dbe.TransactionChain.CreateTransaction(dbe, readOnly: true);
}
