using System;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>Accounts that exist, for cases that log a player in without going through a signup.</summary>
/// <remarks>
/// <b>An account a token names must exist.</b> A login creates the account's character, whose <see cref="Character.Account"/> is a cascade foreign key, and
/// the engine refuses a commit pointing one at an entity nothing holds (CASC-02): the account id a token carries is not enough on its own any more. These
/// are spawned directly — an identity and no secret — rather than through <c>AccountStore.TrySignup</c>, whose password derivation is the slow part of a
/// signup and has nothing to do with a case that only needs a player.
/// </remarks>
internal static class TestAccounts
{
    private static long _created;

    /// <summary>Creates an account whose name no other call in this process has used.</summary>
    /// <param name="dbe">The engine the session will be served by.</param>
    /// <param name="hint">A readable part of the name.</param>
    /// <returns>The account.</returns>
    public static EntityId Create(DatabaseEngine dbe, string hint = "player")
    {
        using var tx = dbe.CreateQuickTransaction();
        var account = tx.Spawn<AccountArch>(AccountArch.Identity.Set(new Account
        {
            Name = (String64)$"{hint}_{System.Threading.Interlocked.Increment(ref _created)}",
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
        }));
        Assert.That(tx.Commit(), Is.True, "the test account was not created");
        return account;
    }

    /// <summary>The account a case knows by <paramref name="key"/>: created on first use in this database, the same one after, a restart included.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="key">The case's name for it.</param>
    /// <returns>The account.</returns>
    public static EntityId For(DatabaseEngine dbe, ulong key)
    {
        var name = $"acct_{key:x}";
        if (AccountStore.TryFind(dbe, name, out var found, out _))
        {
            return found;
        }

        using var tx = dbe.CreateQuickTransaction();
        var account = tx.Spawn<AccountArch>(AccountArch.Identity.Set(new Account { Name = (String64)name, CreatedUtcTicks = DateTime.UtcNow.Ticks }));
        Assert.That(tx.Commit(), Is.True, "the test account was not created");
        return account;
    }
}
