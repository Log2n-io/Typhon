using System;
using System.Collections.Generic;
using Typhon.Shell.Extensibility;
using Typhon.Shell.Session;

namespace Typhon.Shell.Commands;

/// <summary>
/// Adapter that wraps <see cref="ShellSession"/> to implement <see cref="IShellCommandContext"/>, allowing extension commands to interact with the shell
/// without depending on shell internals.
/// </summary>
internal sealed class ShellCommandContext : IShellCommandContext
{
    private readonly ShellSession _session;

    public ShellCommandContext(ShellSession session)
    {
        _session = session;
    }

    public DatabaseEngine Engine => _session.Engine;
    public Transaction CurrentTransaction => _session.Transaction;

    public Transaction GetOrCreateTransaction(out bool isAutoCommit) =>
        _session.GetOrCreateTransaction(out isAutoCommit);

    public Transaction BeginTransaction() => _session.BeginTransaction();
    public bool CommitTransaction() => _session.CommitTransaction();
    public void RollbackTransaction() => _session.RollbackTransaction();
    public void MarkDirty() => _session.MarkDirty();

    public bool IsOpen => _session.IsOpen;
    public string Format => _session.Format;
    public bool Verbose => _session.Verbose;
    public IReadOnlyDictionary<string, Type> ComponentTypes => _session.ComponentTypes;
}
