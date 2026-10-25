using System;

namespace SwgTatooine.Server.Accounts;

/// <summary>
/// Signup and login against the engine, from a Kestrel thread (A2).
/// </summary>
/// <remarks>
/// <para>
/// <b>This runs OFF the tick, and that is legal for exactly one reason.</b> <c>Account</c> is <c>StorageMode.Versioned</c> on
/// a NON-SPATIAL archetype, which is the one combination <c>EW-02</c> (<c>rules/runtime-scheduling.md</c>) permits a thread
/// outside the DAG to write with no coordination: MVCC makes the write concurrent, the <c>EntityMap</c> and the name's
/// B+Tree are both touched through their full latching protocols, and having no placement field keeps the archetype out of
/// the spatial structures the tick fence rewrites unlatched. Add a placement component to <see cref="AccountArch"/> and this
/// file becomes a race.
/// </para>
/// <para>
/// <b>The PBKDF2 cost is paid here on purpose.</b> 600 000 iterations is a few hundred microseconds of one core; on a
/// Kestrel thread that is a slow HTTP request, and on the tick thread it would be a visible frame. Nothing in this class may
/// move into a system.
/// </para>
/// <para>
/// <b>A duplicate name is the index's refusal, surfaced — never a pre-check.</b> <see cref="TrySignup"/> does not look the
/// name up first. It spawns and lets the unique index decide, because a check followed by an insert is a race that two
/// simultaneous signups win together. That is acceptance criterion A-2 and the reason <c>Account.Name</c> is indexed at all.
/// </para>
/// </remarks>
public static class AccountStore
{
    /// <summary>What a signup attempt did.</summary>
    public enum SignupOutcome
    {
        /// <summary>The account exists and is ready to log in.</summary>
        Created,

        /// <summary>The name is already an account's. The unique index said so.</summary>
        NameTaken,

        /// <summary>The name does not pass <see cref="AccountName"/>'s rules.</summary>
        BadName,

        /// <summary>The password does not pass <see cref="PasswordHash"/>'s length rules.</summary>
        BadPassword,

        /// <summary>The outfit names none of <see cref="TatooineData.PlayerOutfits"/>.</summary>
        BadOutfit,
    }

    /// <summary>What a login attempt did.</summary>
    public enum LoginOutcome
    {
        /// <summary>Name found, password verified.</summary>
        Ok,

        /// <summary>No account carries that name.</summary>
        NoSuchAccount,

        /// <summary>The account exists and the password does not match.</summary>
        WrongPassword,

        /// <summary>The name does not pass <see cref="AccountName"/>'s rules, so no account could carry it.</summary>
        BadName,
    }

    /// <summary>
    /// Creates an account, or says why not.
    /// </summary>
    /// <param name="dbe">The engine. Written through a transaction of this thread's own.</param>
    /// <param name="offeredName">The name as typed; normalised here.</param>
    /// <param name="password">The plaintext, which nothing retains, logs or echoes.</param>
    /// <param name="account">The new account's entity, or <c>default</c> on any refusal.</param>
    /// <param name="outfit">The outfit chosen, one-based into <see cref="TatooineData.PlayerOutfits"/>; 0 for none, which leaves it to
    /// <see cref="TatooineData.OutfitFor(EntityId)"/> when the character is created.</param>
    /// <returns>What happened.</returns>
    public static SignupOutcome TrySignup(DatabaseEngine dbe, string offeredName, string password, out EntityId account, int outfit = 0)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        account = default;

        if (!AccountName.TryNormalize(offeredName, out var name))
        {
            return SignupOutcome.BadName;
        }

        if (!PasswordHash.IsAcceptable(password))
        {
            return SignupOutcome.BadPassword;
        }

        if (outfit != 0 && !TatooineData.IsOutfit(outfit))
        {
            return SignupOutcome.BadOutfit;
        }

        var identity = new Account
        {
            Name = (String64)name,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            LastSeenUtcTicks = 0,
            Outfit = (byte)outfit,
        };

        var secret = default(AccountSecret);

        // The digest and salt are written straight into the component's own storage, so the derived key never lands in a managed array somebody could forget
        // to clear. PasswordHash.Create zeroes the UTF-8 bytes of the plaintext itself.
        PasswordHash.Create(password, secret.DigestSpan(), secret.SaltSpan(), out var iterations);
        secret.Iterations = iterations;

        try
        {
            using var tx = dbe.CreateQuickTransaction();
            account = tx.Spawn<AccountArch>(AccountArch.Identity.Set(identity), AccountArch.Secret.Set(secret));
            tx.Commit();
            return SignupOutcome.Created;
        }
        catch (UniqueConstraintViolationException)
        {
            // THE refusal. Two simultaneous signups for one name both reach here; exactly one of them got Created, which is the property a pre-check cannot
            // give. The caller turns this into "that name is taken" and says nothing about indexes.
            account = default;
            return SignupOutcome.NameTaken;
        }
    }

    /// <summary>
    /// Verifies a name and password, and stamps <see cref="Account.LastSeenUtcTicks"/> on success.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="offeredName">The name as typed; normalised here.</param>
    /// <param name="password">The plaintext offered.</param>
    /// <param name="account">The account's entity on success, <c>default</c> otherwise.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    /// <b>The KDF runs even when the name is unknown</b> — see <see cref="SpendVerificationTime"/>. A login that returns in
    /// 20 µs for an absent name and 400 µs for a present one is a name oracle, and enumerating names is the first step of
    /// every credential-stuffing run.
    /// </remarks>
    public static LoginOutcome TryLogin(DatabaseEngine dbe, string offeredName, string password, out EntityId account)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        account = default;

        if (!AccountName.TryNormalize(offeredName, out var name))
        {
            return LoginOutcome.BadName;
        }

        if (!TryFind(dbe, name, out account, out _))
        {
            SpendVerificationTime(password);
            return LoginOutcome.NoSuchAccount;
        }

        if (!TryReadSecret(dbe, account, out var secret))
        {
            // An Account with no AccountSecret is a half-written account and cannot be logged into. It should not exist — the spawn writes both in one
            // transaction — so this is reported as an absent account rather than a wrong password: there is nothing to be wrong against.
            account = default;
            SpendVerificationTime(password);
            return LoginOutcome.NoSuchAccount;
        }

        if (!PasswordHash.Verify(password, secret.DigestSpan(), secret.SaltSpan(), secret.Iterations))
        {
            account = default;
            return LoginOutcome.WrongPassword;
        }

        Stamp(dbe, account);
        return LoginOutcome.Ok;
    }

    /// <summary>
    /// Resolves an account by its normalised name through the unique index.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="normalizedName">A name already through <see cref="AccountName.TryNormalize"/>.</param>
    /// <param name="account">The entity found.</param>
    /// <param name="stored">A copy of its <see cref="Account"/> component.</param>
    /// <returns><see langword="true"/> when the name resolves.</returns>
    /// <remarks>
    /// <para>
    /// <b>A one-key range over the index, not a scan.</b> <c>EnumerateIndex</c> with <c>min == max</c> is a single B+Tree
    /// descent; the component is copied out because the enumerator's reference is only valid until the next
    /// <c>MoveNext</c> and the caller holds it across the KDF.
    /// </para>
    /// <para>
    /// <b>The <see cref="IndexRef"/> is resolved per call rather than cached.</b> It captures a layout version and goes stale
    /// on schema evolution, and a login is already paying for a KDF — the resolution is beside it. Caching it would buy
    /// nothing measurable and would need an invalidation nobody would remember to write.
    /// </para>
    /// </remarks>
    public static bool TryFind(DatabaseEngine dbe, string normalizedName, out EntityId account, out Account stored)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        account = default;
        stored = default;

        var key = (String64)normalizedName;
        var indexRef = dbe.GetIndexRef<Account, String64>(a => a.Name);

        using var tx = dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<Account, String64>(indexRef, key, key);
        if (!found.MoveNext())
        {
            return false;
        }

        account = EntityId.FromRawValue((ulong)found.CurrentEntityPK);
        stored = found.CurrentComponent;
        return true;
    }

    /// <summary>Reads one account's credential component.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="account">The account, already resolved by <see cref="TryFind"/>.</param>
    /// <param name="secret">The credential on success.</param>
    /// <returns><see langword="true"/> when the account exists and carries a credential.</returns>
    /// <remarks>
    /// A second read rather than one that returns both, because <see cref="TryFind"/> goes through the name index and its enumerator yields one component type.
    /// It is the cheap half of a login: an <c>EntityMap</c> hit against a KDF.
    /// </remarks>
    private static bool TryReadSecret(DatabaseEngine dbe, EntityId account, out AccountSecret secret)
    {
        using var tx = dbe.CreateQuickTransaction();
        using var accounts = tx.For<AccountArch>();
        if (!accounts.TryOpen(account, out var entity))
        {
            secret = default;
            return false;
        }

        secret = entity.Read(AccountArch.Secret);
        return true;
    }

    /// <summary>Writes the last-seen stamp. Its own transaction, so a failed stamp cannot fail a correct login.</summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="account">The account that just logged in.</param>
    private static void Stamp(DatabaseEngine dbe, EntityId account)
    {
        using var tx = dbe.CreateQuickTransaction();
        using var accounts = tx.For<AccountArch>();
        if (!accounts.TryOpenMut(account, out var entity))
        {
            return;
        }

        // Read-modify-write of the whole component. The name is rewritten with the same value, which the commit path's unchanged-field guard recognises (#665,
        // comparing all 64 bytes), so the name's B+Tree is not touched by a login. AccountSecret is not opened at all.
        var updated = entity.Read(AccountArch.Identity);
        updated.LastSeenUtcTicks = DateTime.UtcNow.Ticks;
        entity.Set(AccountArch.Identity, updated);
        tx.Commit();
    }

    /// <summary>
    /// Burns the same KDF time a real verification would, for a name that does not exist.
    /// </summary>
    /// <param name="password">The offered plaintext, so the cost tracks the real input rather than a constant.</param>
    /// <remarks>
    /// A zero salt and <see cref="PasswordHash.DefaultIterations"/>: the result is discarded, only the time is wanted. It is
    /// not a perfect equaliser — an account hashed at an older, lower work factor still verifies faster — and that is
    /// recorded rather than hidden, because the leak it closes is the one that matters: "does this name exist at all".
    /// </remarks>
    private static void SpendVerificationTime(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > PasswordHash.MaximumPasswordLength)
        {
            return;
        }

        Span<byte> salt = stackalloc byte[PasswordHash.SaltBytes];
        Span<byte> sink = stackalloc byte[PasswordHash.HashBytes];
        PasswordHash.Verify(password, sink, salt, PasswordHash.DefaultIterations);
    }
}
